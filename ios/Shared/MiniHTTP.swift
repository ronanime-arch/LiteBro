import Foundation
import Network

/// A request as it came: method, path with query, headers by lower-case name
struct HTTPRequest {
    let method: String
    let uri: String
    let headers: [String: String]
    let keepAlive: Bool

    func header(_ name: String) -> String? { headers[name.lowercased()] }
}

/// What a server does with each request on one connection: the head, then the body in pieces, then
/// the end. The next request of the connection is read only after `HTTPConnection.finish`.
protocol HTTPHandler: AnyObject {
    func onHead(_ r: HTTPRequest, _ c: HTTPConnection)
    func onBody(_ d: Data, _ c: HTTPConnection)
    func onEnd(_ r: HTTPRequest, _ c: HTTPConnection)
}

/// A small HTTP/1.1 server on Network.framework for the home network: the phone's files (the app)
/// and its screen (the broadcast extension). Not SwiftNIO: its server pipeline trapped in a dynamic
/// cast on the iOS 16 runtime at every accepted connection (user bug 2026-11-26), first in
/// configureHTTPServerPipeline, then in addHandler itself.
final class MiniHTTP {
    enum Failure: Error, CustomStringConvertible {
        case portInUse, timeout, failed(String)
        var description: String {
            switch self {
            case .portInUse: return "port in use"
            case .timeout: return "no answer from the network"
            case .failed(let s): return s
            }
        }
    }

    private var listener: NWListener?
    private let queue = DispatchQueue(label: "litebro.http")
    private var connections: [ObjectIdentifier: HTTPConnection] = [:]

    deinit { stop() }

    /// Listens on every address of the phone; returns the port, or throws (taken, no network)
    @discardableResult
    func start(port: Int, handler: @escaping () -> HTTPHandler) throws -> Int {
        stop()
        guard let p = NWEndpoint.Port(rawValue: UInt16(port)) else { throw Failure.failed("bad port") }
        let params = NWParameters.tcp
        (params.defaultProtocolStack.transportProtocol as? NWProtocolTCP.Options)?.noDelay = true
        // For the home network only: a mobile operator's address (IPv6 can be reached from the internet) gets no port
        params.prohibitedInterfaceTypes = [.cellular]
        let l: NWListener
        do { l = try NWListener(using: params, on: p) } catch { throw Self.map(error) }

        let ready = DispatchSemaphore(value: 0)
        var outcome: Error?? = nil // nil: still waiting; .some(nil): ready
        l.stateUpdateHandler = { state in
            switch state {
            case .ready:
                if outcome == nil { outcome = .some(nil); ready.signal() }
            case .failed(let e), .waiting(let e):
                if outcome == nil { outcome = .some(Self.map(e)); ready.signal() }
            default: break
            }
        }
        l.newConnectionHandler = { [weak self] nw in
            guard let self else { return nw.cancel() }
            let c = HTTPConnection(nw, handler: handler(), queue: self.queue)
            let id = ObjectIdentifier(c)
            self.connections[id] = c
            c.onClose = { [weak self] in self?.connections[id] = nil }
            c.start()
        }
        l.start(queue: queue)
        if ready.wait(timeout: .now() + 5) == .timedOut {
            l.cancel()
            throw Failure.timeout
        }
        if case .some(.some(let e)) = queue.sync(execute: { outcome }) {
            l.cancel()
            throw e
        }
        listener = l
        return Int(l.port?.rawValue ?? UInt16(port))
    }

    func stop() {
        listener?.cancel()
        listener = nil
        let open = queue.sync { () -> [HTTPConnection] in
            let all = Array(connections.values)
            connections = [:]
            return all
        }
        open.forEach { $0.close() }
    }

    private static func map(_ e: Error) -> Error {
        if let nw = e as? NWError, case .posix(let code) = nw {
            return code == .EADDRINUSE ? Failure.portInUse : Failure.failed("\(nw)")
        }
        return Failure.failed("\(e)")
    }

    /// The phone's address in the Wi-Fi network (en0), else in any other network
    static func address() -> String? {
        var found: [String: String] = [:]
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0, let first = list else { return nil }
        defer { freeifaddrs(list) }
        var p: UnsafeMutablePointer<ifaddrs>? = first
        while let a = p {
            defer { p = a.pointee.ifa_next }
            guard let sa = a.pointee.ifa_addr, sa.pointee.sa_family == UInt8(AF_INET) else { continue }
            let name = String(cString: a.pointee.ifa_name)
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            if getnameinfo(sa, socklen_t(sa.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 {
                let ip = String(cString: host)
                if !ip.hasPrefix("127.") && !ip.hasPrefix("169.254.") { found[name] = ip }
            }
        }
        return found["en0"] ?? found["bridge100"] ?? found.sorted { $0.key < $1.key }.first?.value
    }

    /// A random secret for the address: what stands for a password on the home network
    static func token() -> String {
        var b = [UInt8](repeating: 0, count: 16)
        _ = SecRandomCopyBytes(kSecRandomDefault, b.count, &b)
        return Data(b).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}

/// One TCP connection: reads requests one at a time (Content-Length or chunked body), writes answers
final class HTTPConnection {
    private let nw: NWConnection
    private let handler: HTTPHandler
    private let queue: DispatchQueue
    var onClose: (() -> Void)?

    private var buffer = Data()
    // chunked: bytes left of the chunk; nil = a size line next; -1 = trailers until an empty line
    private enum Body { case empty, length(Int64), chunked(Int64?) }
    private var state: Body?          // nil while reading a head
    private var request: HTTPRequest?
    private var busy = false          // a request was read and its answer is not finished yet
    private var reading = false
    private(set) var isActive = true

    private static let maxHead = 64 * 1024

    init(_ nw: NWConnection, handler: HTTPHandler, queue: DispatchQueue) {
        self.nw = nw
        self.handler = handler
        self.queue = queue
    }

    func start() {
        nw.stateUpdateHandler = { [weak self] s in
            switch s {
            case .failed, .cancelled: self?.closed()
            default: break
            }
        }
        nw.start(queue: queue)
        receive()
    }

    private func receive() {
        guard isActive, !reading else { return }
        reading = true
        nw.receive(minimumIncompleteLength: 1, maximumLength: 256 * 1024) { [weak self] data, _, done, error in
            guard let self else { return }
            self.reading = false
            if let data, !data.isEmpty { self.buffer.append(data) }
            self.parse()
            if error != nil || (done && !self.busy) { return self.close() }
            if !done && !self.busy { self.receive() }
        }
    }

    /// Takes what the buffer holds: a head, pieces of the body, the end of the request
    private func parse() {
        while isActive && !busy {
            if state == nil {
                // Some clients put an empty line after a body
                while buffer.starts(with: Data("\r\n".utf8)) { buffer.removeFirst(2) }
                guard let end = buffer.range(of: Data("\r\n\r\n".utf8)) else {
                    if buffer.count > Self.maxHead { busy = true; respond(431, keepAlive: false) }
                    return
                }
                let text = String(decoding: buffer[buffer.startIndex..<end.lowerBound], as: UTF8.self)
                buffer.removeSubrange(buffer.startIndex..<end.upperBound)
                guard let r = Self.head(text) else { busy = true; return respond(400, keepAlive: false) }
                request = r
                if r.header("transfer-encoding")?.lowercased().contains("chunked") == true {
                    state = .chunked(nil)
                } else if let n = r.header("content-length").flatMap({ Int64($0.trimmingCharacters(in: .whitespaces)) }), n > 0 {
                    state = .length(n)
                } else {
                    state = .empty
                }
                if r.header("expect")?.lowercased() == "100-continue" { send(Data("HTTP/1.1 100 Continue\r\n\r\n".utf8)) }
                handler.onHead(r, self)
            }
            guard let r = request, let s = state else { return }
            switch s {
            case .empty:
                finishRequest(r)
            case .length(let left):
                if buffer.isEmpty { return }
                let n = Int(min(Int64(buffer.count), left))
                let piece = buffer.prefix(n)
                buffer.removeFirst(n)
                handler.onBody(Data(piece), self)
                if left - Int64(n) == 0 { finishRequest(r) } else { state = .length(left - Int64(n)); return }
            case .chunked(nil):
                guard let line = buffer.range(of: Data("\r\n".utf8)) else { return }
                let text = String(decoding: buffer[buffer.startIndex..<line.lowerBound], as: UTF8.self)
                buffer.removeSubrange(buffer.startIndex..<line.upperBound)
                guard let size = Int64(text.split(separator: ";").first.map(String.init)?.trimmingCharacters(in: .whitespaces) ?? "", radix: 16) else {
                    busy = true
                    return respond(400, keepAlive: false)
                }
                state = .chunked(size == 0 ? -1 : size)
            case .chunked(.some(let left)) where left < 0:
                // Trailer lines up to the empty one (a simple client sends none)
                guard let tail = buffer.range(of: Data("\r\n".utf8)) else { return }
                let empty = tail.lowerBound == buffer.startIndex
                buffer.removeSubrange(buffer.startIndex..<tail.upperBound)
                if empty { finishRequest(r) }
            case .chunked(.some(let left)):
                if left == 0 {
                    guard buffer.count >= 2 else { return }
                    buffer.removeFirst(2) // CRLF after the chunk
                    state = .chunked(nil)
                    continue
                }
                if buffer.isEmpty { return }
                let n = Int(min(Int64(buffer.count), left))
                let piece = buffer.prefix(n)
                buffer.removeFirst(n)
                handler.onBody(Data(piece), self)
                state = .chunked(left - Int64(n))
            }
        }
    }

    private func finishRequest(_ r: HTTPRequest) {
        state = nil
        request = nil
        busy = true
        handler.onEnd(r, self)
    }

    private static func head(_ text: String) -> HTTPRequest? {
        let lines = text.components(separatedBy: "\r\n")
        let first = lines[0].split(separator: " ", omittingEmptySubsequences: true)
        guard first.count == 3, first[2].hasPrefix("HTTP/1.") else { return nil }
        var headers: [String: String] = [:]
        for l in lines.dropFirst() {
            guard let colon = l.firstIndex(of: ":") else { continue }
            let name = l[..<colon].trimmingCharacters(in: .whitespaces).lowercased()
            let value = l[l.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            headers[name] = headers[name].map { $0 + ", " + value } ?? value
        }
        let connection = headers["connection"]?.lowercased() ?? ""
        let keepAlive = first[2] == "HTTP/1.1" ? !connection.contains("close") : connection.contains("keep-alive")
        return HTTPRequest(method: String(first[0]).uppercased(), uri: String(first[1]), headers: headers, keepAlive: keepAlive)
    }

    // MARK: Answers (any thread)

    /// Bytes as they are; `done` runs once the network took them (for pieces of a big answer)
    func send(_ d: Data, done: (() -> Void)? = nil) {
        guard isActive else { done?(); return }
        nw.send(content: d, completion: .contentProcessed { _ in done?() })
    }

    /// Bytes, and waits until the network took them: for loops off the connection's queue
    @discardableResult
    func sendAndWait(_ d: Data) -> Bool {
        let sent = DispatchSemaphore(value: 0)
        send(d) { sent.signal() }
        sent.wait()
        return isActive
    }

    /// The status line and headers of an answer whose body follows by `send`
    func sendHead(_ status: Int, headers: [(String, String)]) {
        var s = "HTTP/1.1 \(status) \(Self.reason(status))\r\n"
        for (n, v) in headers { s += "\(n): \(v)\r\n" }
        s += "\r\n"
        send(Data(s.utf8))
    }

    /// A whole answer, then the next request (or the end of the connection)
    func respond(_ status: Int, headers: [(String, String)] = [], body: Data? = nil, keepAlive: Bool = true) {
        var h = headers.filter { $0.0.lowercased() != "content-length" }
        h.append(("Content-Length", String(body?.count ?? 0)))
        if !keepAlive { h.append(("Connection", "close")) }
        var s = "HTTP/1.1 \(status) \(Self.reason(status))\r\n"
        for (n, v) in h { s += "\(n): \(v)\r\n" }
        s += "\r\n"
        var all = Data(s.utf8)
        if let body { all.append(body) }
        send(all)
        finish(keepAlive: keepAlive)
    }

    /// The answer is complete: read the next request, or close
    func finish(keepAlive: Bool) {
        queue.async { [self] in
            guard isActive else { return }
            if !keepAlive {
                nw.send(content: nil, contentContext: .finalMessage, isComplete: true, completion: .contentProcessed { [weak self] _ in self?.close() })
                return
            }
            busy = false
            parse()
            if !busy { receive() }
        }
    }

    func close() {
        queue.async { [self] in
            guard isActive else { return }
            nw.cancel()
            closed()
        }
    }

    private func closed() {
        guard isActive else { return }
        isActive = false
        onClose?()
        onClose = nil
    }

    private static func reason(_ s: Int) -> String {
        switch s {
        case 100: return "Continue"
        case 200: return "OK"
        case 201: return "Created"
        case 204: return "No Content"
        case 206: return "Partial Content"
        case 207: return "Multi-Status"
        case 301: return "Moved Permanently"
        case 400: return "Bad Request"
        case 403: return "Forbidden"
        case 404: return "Not Found"
        case 405: return "Method Not Allowed"
        case 409: return "Conflict"
        case 412: return "Precondition Failed"
        case 416: return "Range Not Satisfiable"
        case 431: return "Request Header Fields Too Large"
        default: return "Status"
        }
    }
}
