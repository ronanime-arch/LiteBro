import Foundation
import NIOCore
import NIOHTTP1
import NIOPosix

/// A small HTTP server on SwiftNIO for the home network: the phone's files (the app) and its screen
/// (the broadcast extension). Each connection gets its own handler from the factory.
final class MiniHTTP {
    private let group = MultiThreadedEventLoopGroup(numberOfThreads: 1)
    private var channel: Channel?

    deinit { stop(); try? group.syncShutdownGracefully() }

    /// Listens on every address of the phone; returns the port, or throws (taken, no network)
    @discardableResult
    func start(port: Int, handler: @escaping @Sendable () -> ChannelHandler) throws -> Int {
        let bootstrap = ServerBootstrap(group: group)
            .serverChannelOption(ChannelOptions.backlog, value: 64)
            .serverChannelOption(ChannelOptions.socket(SocketOptionLevel(SOL_SOCKET), SO_REUSEADDR), value: 1)
            .childChannelOption(ChannelOptions.socket(SocketOptionLevel(IPPROTO_TCP), TCP_NODELAY), value: 1)
            .childChannelInitializer { ch in
                ch.eventLoop.makeCompletedFuture {
                    // One by one, as configureHTTPServerPipeline would: its array cast to an existential
                    // with Sendable aborts on the iOS 16 Swift runtime at every accepted connection
                    let ops = ch.pipeline.syncOperations
                    try ops.addHandler(HTTPResponseEncoder())
                    try ops.addHandler(ByteToMessageHandler(HTTPRequestDecoder(leftOverBytesStrategy: .dropBytes)))
                    try ops.addHandler(HTTPServerPipelineHandler())
                    try ops.addHandler(HTTPServerProtocolErrorHandler())
                    try ops.addHandler(handler())
                }
            }
        let ch = try bootstrap.bind(host: "0.0.0.0", port: port).wait()
        channel = ch
        return ch.localAddress?.port ?? port
    }

    func stop() {
        try? channel?.close().wait()
        channel = nil
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

extension Channel {
    /// Writes a whole response
    func respond(_ status: HTTPResponseStatus, headers: [(String, String)] = [], body: Data? = nil, keepAlive: Bool = true) {
        var h = HTTPHeaders(headers)
        h.replaceOrAdd(name: "Content-Length", value: String(body?.count ?? 0))
        if !keepAlive { h.replaceOrAdd(name: "Connection", value: "close") }
        let head = HTTPResponseHead(version: .http1_1, status: status, headers: h)
        write(HTTPServerResponsePart.head(head), promise: nil)
        if let body, !body.isEmpty {
            write(HTTPServerResponsePart.body(.byteBuffer(ByteBuffer(bytes: body))), promise: nil)
        }
        let done = writeAndFlush(HTTPServerResponsePart.end(nil))
        if !keepAlive { done.whenComplete { [weak self] _ in self?.close(promise: nil) } }
    }
}
