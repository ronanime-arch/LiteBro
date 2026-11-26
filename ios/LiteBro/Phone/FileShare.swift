import Foundation
import LocalAuthentication
import UIKit
import UniformTypeIdentifiers

/// «Файлы для ПК»: the phone's files over Wi-Fi, after Face ID, for a while. WebDAV (a network folder in
/// Windows Explorer) and a page for any browser, at http://<phone>:<port>/<secret>/. With the rights of
/// TrollStore (no sandbox) the phone's whole user storage, else the Documents folder of LiteBro.
final class FileShare: ObservableObject {
    static let shared = FileShare()
    static let port = 8080
    /// Tried in turn when 8080 is taken (another LiteBro build sharing too); 8081 is the screen's
    static let ports = [8080] + Array(8082...8089)

    @Published private(set) var running = false
    @Published private(set) var url: String?
    @Published private(set) var until: Date?
    @Published var error: String?

    /// The phone's storage when the sandbox lets it be read, else the app's own Documents
    let root: URL
    let wholePhone: Bool

    private var server: MiniHTTP?
    private var timer: Timer?

    private init() {
        let mobile = URL(fileURLWithPath: "/var/mobile")
        if (try? FileManager.default.contentsOfDirectory(atPath: "/var/mobile/Media")) != nil {
            root = mobile
            wholePhone = true
        } else {
            root = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            wholePhone = false
        }
    }

    /// Face ID (or the passcode) first; a new secret every time, so an old address stops working
    func start(minutes: Int, readOnly: Bool) {
        let ctx = LAContext()
        ctx.evaluatePolicy(.deviceOwnerAuthentication, localizedReason: L("Открыть файлы телефона для компьютера")) { ok, _ in
            DispatchQueue.main.async {
                guard ok else { return }
                self.run(minutes: minutes, readOnly: readOnly)
            }
        }
    }

    private func run(minutes: Int, readOnly: Bool) {
        stop()
        guard let ip = MiniHTTP.address() else {
            error = L("Телефон не подключён к сети Wi-Fi.")
            return
        }
        let token = MiniHTTP.token()
        let s = MiniHTTP()
        let root = self.root
        do {
            var bound: Int?
            for p in Self.ports {
                do {
                    bound = try s.start(port: p) { DAVHandler(root: root, token: token, readOnly: readOnly) }
                    break
                } catch MiniHTTP.Failure.portInUse {
                    continue
                }
            }
            guard let port = bound else {
                // Every port is held, most likely by another LiteBro build (Debug) sharing its files
                error = L("Порты 8080–8089 заняты. Скорее всего, доступ с ПК уже включён в другой сборке LiteBro (например, Debug): выключите его там или закройте её.")
                return
            }
            server = s
            url = "http://\(ip):\(port)/\(token)/"
            running = true
            error = nil
            UIApplication.shared.isIdleTimerDisabled = true // a locked phone stops the server
            if minutes > 0 {
                let end = Date().addingTimeInterval(TimeInterval(minutes * 60))
                until = end
                timer = Timer.scheduledTimer(withTimeInterval: TimeInterval(minutes * 60), repeats: false) { [weak self] _ in self?.stop() }
            } else {
                until = nil
            }
        } catch {
            self.error = L("Не удалось открыть порт") + ": \(error)"
        }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        server?.stop()
        server = nil
        running = false
        url = nil
        until = nil
        UIApplication.shared.isIdleTimerDisabled = false
    }
}

/// One connection: WebDAV for Explorer, plain GET with a page for browsers. The secret is the first
/// part of every path; anything else is "not found".
private final class DAVHandler: HTTPHandler {
    private let root: URL
    private let rootPath: String
    private let token: String
    private let readOnly: Bool
    private var upload: (handle: FileHandle, temp: URL, target: URL)?
    private var body = Data()
    private static let work = DispatchQueue(label: "litebro.files", qos: .userInitiated, attributes: .concurrent)

    init(root: URL, token: String, readOnly: Bool) {
        self.root = root
        rootPath = root.resolvingSymlinksInPath().standardizedFileURL.path
        self.token = token
        self.readOnly = readOnly
    }

    // MARK: Paths

    /// The file of a request path, only inside the shared folder; nil for a wrong secret or a way out
    private func file(_ uri: String) -> (url: URL, rel: [String])? {
        let path = String(uri.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false).first ?? "")
        var parts = path.split(separator: "/").map { String($0).removingPercentEncoding ?? String($0) }
        guard parts.first == token else { return nil }
        parts.removeFirst()
        if parts.contains(where: { $0 == ".." || $0 == "." || $0.contains("\0") }) { return nil }
        let url = parts.reduce(root) { $0.appendingPathComponent($1) }
        // Through a link nothing outside the folder
        let real = url.resolvingSymlinksInPath().standardizedFileURL.path
        if FileManager.default.fileExists(atPath: url.path), !(real == rootPath || real.hasPrefix(rootPath + "/")) { return nil }
        return (url, parts)
    }

    private func href(_ rel: [String], dir: Bool) -> String {
        var allowed = CharacterSet.urlPathAllowed
        allowed.remove(charactersIn: "/?#[]@!$&'()*+,;=")
        let enc = rel.map { $0.addingPercentEncoding(withAllowedCharacters: allowed) ?? $0 }
        return "/" + ([token] + enc).joined(separator: "/") + (dir && !rel.isEmpty ? "/" : rel.isEmpty ? "/" : "")
    }

    // MARK: Request

    func onHead(_ h: HTTPRequest, _ c: HTTPConnection) {
        body = Data()
        upload = nil
        if h.method == "PUT", !readOnly, let f = file(h.uri) {
            let dir = f.url.deletingLastPathComponent()
            let temp = dir.appendingPathComponent(".litebro-upload-" + UUID().uuidString)
            if FileManager.default.createFile(atPath: temp.path, contents: nil),
               let handle = try? FileHandle(forWritingTo: temp) {
                upload = (handle, temp, f.url)
            }
        }
    }

    func onBody(_ d: Data, _ c: HTTPConnection) {
        if let u = upload {
            u.handle.write(d)
        } else if body.count < 1 << 20 {
            body.append(d)
        }
    }

    func onEnd(_ h: HTTPRequest, _ c: HTTPConnection) {
        let up = upload
        upload = nil
        Self.work.async { [self] in
            self.handle(h, upload: up, channel: c, keepAlive: h.keepAlive)
        }
    }

    private func handle(_ h: HTTPRequest, upload: (handle: FileHandle, temp: URL, target: URL)?,
                        channel: HTTPConnection, keepAlive: Bool) {
        let fm = FileManager.default
        // Taps from the computer, sent by the app itself: the broadcast extension's sandbox keeps it from the
        // HID event system, the app (no sandbox, TrollStore) reaches it. Only with «Управление касаниями» on.
        let path = String(h.uri.split(separator: "?").first ?? "")
        if path == "/\(token)/_touch" {
            guard ScreenShare.control else { return channel.respond(403, keepAlive: keepAlive) }
            if h.method == "POST", let m = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any] {
                if let b = m["button"] as? String {
                    Touch.button(b == "side" ? 0x30 : 0x40)
                } else if let p = (m["t"] as? String).flatMap(Touch.Phase.init(rawValue:)),
                          let x = m["x"] as? Double, let y = m["y"] as? Double {
                    Touch.finger(p, x: min(max(x, 0), 1), y: min(max(y, 0), 1))
                }
                return channel.respond(204, keepAlive: keepAlive)
            }
            let info = "{\"available\":\(Touch.available)}"
            return channel.respond(200, headers: [("Content-Type", "application/json")], body: Data(info.utf8), keepAlive: keepAlive)
        }
        guard let f = file(h.uri) else {
            if let upload { try? upload.handle.close(); try? fm.removeItem(at: upload.temp) }
            return channel.respond(404, keepAlive: keepAlive)
        }
        let writes = ["PUT", "DELETE", "MKCOL", "MOVE", "COPY", "PROPPATCH"]
        if readOnly && writes.contains(h.method) { return channel.respond(403, keepAlive: keepAlive) }
        var isDir: ObjCBool = false
        let exists = fm.fileExists(atPath: f.url.path, isDirectory: &isDir)

        switch h.method {
        case "OPTIONS":
            channel.respond(200, headers: [("DAV", "1, 2"), ("MS-Author-Via", "DAV"),
                                           ("Allow", "OPTIONS, GET, HEAD, PUT, DELETE, MKCOL, MOVE, COPY, PROPFIND, PROPPATCH, LOCK, UNLOCK")],
                            keepAlive: keepAlive)
        case "PROPFIND":
            guard exists else { return channel.respond(404, keepAlive: keepAlive) }
            let depth = h.header("Depth") ?? "1"
            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<D:multistatus xmlns:D=\"DAV:\">\n"
            xml += entry(f.url, rel: f.rel)
            if isDir.boolValue && depth != "0" {
                let names = (try? fm.contentsOfDirectory(atPath: f.url.path)) ?? []
                for n in names where !n.hasPrefix(".litebro-upload-") {
                    xml += entry(f.url.appendingPathComponent(n), rel: f.rel + [n])
                }
            }
            xml += "</D:multistatus>\n"
            channel.respond(207, headers: [("Content-Type", "application/xml; charset=utf-8")],
                            body: Data(xml.utf8), keepAlive: keepAlive)
        case "PROPPATCH":
            let xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<D:multistatus xmlns:D=\"DAV:\"><D:response><D:href>"
                + Self.escape(href(f.rel, dir: isDir.boolValue)) + "</D:href><D:propstat><D:prop/><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>"
            channel.respond(207, headers: [("Content-Type", "application/xml; charset=utf-8")],
                            body: Data(xml.utf8), keepAlive: keepAlive)
        case "GET", "HEAD":
            guard exists else { return channel.respond(404, keepAlive: keepAlive) }
            if isDir.boolValue {
                if !h.uri.split(separator: "?").first!.hasSuffix("/") {
                    return channel.respond(301, headers: [("Location", href(f.rel, dir: true))], keepAlive: keepAlive)
                }
                let page = Data(listing(f.url, rel: f.rel).utf8)
                channel.respond(200, headers: [("Content-Type", "text/html; charset=utf-8"), ("Cache-Control", "no-store"),
                                               ("Content-Security-Policy", "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self'; media-src 'self'; connect-src 'self'")],
                                body: h.method == "HEAD" ? nil : page, keepAlive: keepAlive)
            } else {
                send(f.url, head: h, channel: channel, keepAlive: keepAlive)
            }
        case "PUT":
            guard let upload else { return channel.respond(409, keepAlive: keepAlive) }
            try? upload.handle.close()
            do {
                let had = fm.fileExists(atPath: upload.target.path)
                if had { _ = try fm.replaceItemAt(upload.target, withItemAt: upload.temp) } else { try fm.moveItem(at: upload.temp, to: upload.target) }
                channel.respond(had ? 204 : 201, keepAlive: keepAlive)
            } catch {
                try? fm.removeItem(at: upload.temp)
                channel.respond(403, keepAlive: keepAlive)
            }
        case "DELETE":
            guard exists, !f.rel.isEmpty else { return channel.respond(exists ? 403 : 404, keepAlive: keepAlive) }
            do { try fm.removeItem(at: f.url); channel.respond(204, keepAlive: keepAlive) }
            catch { channel.respond(403, keepAlive: keepAlive) }
        case "MKCOL":
            if exists { return channel.respond(405, keepAlive: keepAlive) }
            do { try fm.createDirectory(at: f.url, withIntermediateDirectories: false); channel.respond(201, keepAlive: keepAlive) }
            catch { channel.respond(409, keepAlive: keepAlive) }
        case "MOVE", "COPY":
            guard exists, !f.rel.isEmpty, let d = h.header("Destination"),
                  let path = URLComponents(string: d)?.percentEncodedPath ?? Optional(d), let to = file(path) else {
                return channel.respond(400, keepAlive: keepAlive)
            }
            let overwrite = h.header("Overwrite")?.uppercased() != "F"
            let had = fm.fileExists(atPath: to.url.path)
            if had && !overwrite { return channel.respond(412, keepAlive: keepAlive) }
            do {
                if had { try fm.removeItem(at: to.url) }
                if h.method == "MOVE" { try fm.moveItem(at: f.url, to: to.url) } else { try fm.copyItem(at: f.url, to: to.url) }
                channel.respond(had ? 204 : 201, keepAlive: keepAlive)
            } catch {
                channel.respond(409, keepAlive: keepAlive)
            }
        case "LOCK":
            // Explorer writes only with a lock; one person, one computer: every lock is granted
            let lock = "opaquelocktoken:" + UUID().uuidString
            let xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<D:prop xmlns:D=\"DAV:\"><D:lockdiscovery><D:activelock>"
                + "<D:locktype><D:write/></D:locktype><D:lockscope><D:exclusive/></D:lockscope><D:depth>0</D:depth>"
                + "<D:timeout>Second-3600</D:timeout><D:locktoken><D:href>\(lock)</D:href></D:locktoken>"
                + "</D:activelock></D:lockdiscovery></D:prop>"
            channel.respond(200, headers: [("Content-Type", "application/xml; charset=utf-8"), ("Lock-Token", "<\(lock)>")],
                            body: Data(xml.utf8), keepAlive: keepAlive)
        case "UNLOCK":
            channel.respond(204, keepAlive: keepAlive)
        default:
            channel.respond(405, keepAlive: keepAlive)
        }
    }

    // MARK: Answers

    private static let httpDate: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "GMT")
        f.dateFormat = "EEE, dd MMM yyyy HH:mm:ss 'GMT'"
        return f
    }()

    static func escape(_ s: String) -> String {
        s.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;").replacingOccurrences(of: "\"", with: "&quot;")
    }

    private static func type(_ url: URL) -> String {
        UTType(filenameExtension: url.pathExtension)?.preferredMIMEType ?? "application/octet-stream"
    }

    private func entry(_ url: URL, rel: [String]) -> String {
        let a = (try? FileManager.default.attributesOfItem(atPath: url.path)) ?? [:]
        let dir = (a[.type] as? FileAttributeType) == .typeDirectory
        let size = (a[.size] as? NSNumber)?.int64Value ?? 0
        let mod = a[.modificationDate] as? Date ?? Date()
        let made = a[.creationDate] as? Date ?? mod
        var x = "<D:response><D:href>" + Self.escape(href(rel, dir: dir)) + "</D:href><D:propstat><D:prop>"
        x += "<D:displayname>" + Self.escape(rel.last ?? "") + "</D:displayname>"
        x += dir ? "<D:resourcetype><D:collection/></D:resourcetype>"
                 : "<D:resourcetype/><D:getcontentlength>\(size)</D:getcontentlength><D:getcontenttype>\(Self.type(url))</D:getcontenttype>"
        x += "<D:getlastmodified>" + Self.httpDate.string(from: mod) + "</D:getlastmodified>"
        x += "<D:creationdate>" + ISO8601DateFormatter().string(from: made) + "</D:creationdate>"
        x += "<D:supportedlock><D:lockentry><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockentry></D:supportedlock>"
        x += "</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>\n"
        return x
    }

    /// A file in pieces, with Range for video in the browser; the next piece waits for the network
    private func send(_ url: URL, head h: HTTPRequest, channel: HTTPConnection, keepAlive: Bool) {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return channel.respond(403, keepAlive: keepAlive) }
        defer { try? handle.close() }
        let attrs = (try? FileManager.default.attributesOfItem(atPath: url.path)) ?? [:]
        let size = (attrs[.size] as? NSNumber)?.int64Value ?? 0
        var start: Int64 = 0, end = size - 1, partial = false
        if let r = h.header("Range"), r.hasPrefix("bytes="), !r.contains(",") {
            let p = r.dropFirst(6).split(separator: "-", omittingEmptySubsequences: false).map(String.init)
            if p.count == 2 {
                if p[0].isEmpty, let n = Int64(p[1]) { start = max(0, size - n) }
                else if let a = Int64(p[0]) { start = a; if let b = Int64(p[1]) { end = min(b, size - 1) } }
                partial = true
            }
        }
        if size > 0 && (start > end || start >= size) {
            return channel.respond(416, headers: [("Content-Range", "bytes */\(size)")], keepAlive: keepAlive)
        }
        let length = size == 0 ? 0 : end - start + 1
        var headers = [("Content-Type", Self.type(url)), ("Content-Length", String(length)), ("Accept-Ranges", "bytes"),
                       ("Last-Modified", Self.httpDate.string(from: attrs[.modificationDate] as? Date ?? Date()))]
        if partial { headers.append(("Content-Range", "bytes \(start)-\(end)/\(size)")) }
        if !keepAlive { headers.append(("Connection", "close")) }
        channel.sendHead(partial ? 206 : 200, headers: headers)
        if h.method == "GET" && length > 0 {
            try? handle.seek(toOffset: UInt64(start))
            var left = length
            while left > 0 && channel.isActive {
                let piece = handle.readData(ofLength: Int(min(left, 256 * 1024)))
                if piece.isEmpty { break }
                left -= Int64(piece.count)
                channel.sendAndWait(piece)
            }
        }
        channel.finish(keepAlive: keepAlive)
    }

    /// The page of a folder: files, upload, new folder and delete (unless read-only)
    private func listing(_ dir: URL, rel: [String]) -> String {
        let fm = FileManager.default
        let names = ((try? fm.contentsOfDirectory(atPath: dir.path)) ?? []).filter { !$0.hasPrefix(".litebro-upload-") }
        var rows: [(name: String, dir: Bool, size: Int64, date: Date)] = []
        for n in names {
            let a = (try? fm.attributesOfItem(atPath: dir.appendingPathComponent(n).path)) ?? [:]
            rows.append((n, (a[.type] as? FileAttributeType) == .typeDirectory, (a[.size] as? NSNumber)?.int64Value ?? 0,
                         a[.modificationDate] as? Date ?? .distantPast))
        }
        rows.sort { $0.dir != $1.dir ? $0.dir : $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        let df = DateFormatter()
        df.dateFormat = "yyyy-MM-dd HH:mm"
        let bf = ByteCountFormatter()
        var crumbs = "<a href=\"" + href([], dir: true) + "\">" + Self.escape(L("Телефон")) + "</a>"
        for i in rel.indices {
            crumbs += " / <a href=\"" + href(Array(rel[...i]), dir: true) + "\">" + Self.escape(rel[i]) + "</a>"
        }
        var list = ""
        if !rel.isEmpty { list += "<tr><td><a href=\"../\">..</a></td><td></td><td></td><td></td></tr>" }
        for r in rows {
            let link = href(rel + [r.name], dir: r.dir)
            list += "<tr><td><a href=\"\(link)\">" + (r.dir ? "📁 " : "") + Self.escape(r.name) + "</a></td>"
            list += "<td class=n>" + (r.dir ? "" : bf.string(fromByteCount: r.size)) + "</td><td class=n>" + df.string(from: r.date) + "</td>"
            list += readOnly ? "<td></td>" : "<td><button data-del=\"\(link)\">✕</button></td>"
            list += "</tr>"
        }
        let tools = readOnly ? "" : """
        <p class=tools><label class=btn>\(Self.escape(L("Загрузить файлы")))<input type=file multiple hidden id=up></label>
        <button id=mk>\(Self.escape(L("Новая папка")))</button> <span id=st></span></p>
        """
        let script = readOnly ? "" : """
        <script>
        const st = document.getElementById("st");
        document.getElementById("up").onchange = async e => {
          for (const f of e.target.files) {
            st.textContent = f.name + "…";
            const r = await fetch(encodeURIComponent(f.name), { method: "PUT", body: f });
            if (!r.ok) { st.textContent = f.name + ": " + r.status; return; }
          }
          location.reload();
        };
        document.getElementById("mk").onclick = async () => {
          const n = prompt("\(L("Имя папки"))");
          if (n) { await fetch(encodeURIComponent(n) + "/", { method: "MKCOL" }); location.reload(); }
        };
        for (const b of document.querySelectorAll("[data-del]")) b.onclick = async () => {
          if (!confirm("\(L("Удалить"))?\\n" + decodeURIComponent(b.dataset.del))) return;
          const r = await fetch(b.dataset.del, { method: "DELETE" });
          if (r.ok) location.reload(); else st.textContent = r.status;
        };
        </script>
        """
        return """
        <!doctype html><html><head><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
        <title>\(Self.escape(rel.last ?? L("Телефон")))</title>
        <style>
        :root { color-scheme: light dark; } body { font: 14px system-ui, sans-serif; margin: 16px; }
        table { border-collapse: collapse; width: 100%; } td { padding: 5px 8px; border-bottom: 1px solid rgba(127,127,127,.25); }
        td.n { color: gray; white-space: nowrap; text-align: right; } a { text-decoration: none; }
        button, .btn { font: inherit; padding: 4px 10px; border: 1px solid rgba(127,127,127,.5); border-radius: 6px; background: none; cursor: pointer; }
        </style></head><body>
        <h3>\(crumbs)</h3>\(tools)
        <table>\(list)</table>\(script)
        </body></html>
        """
    }
}
