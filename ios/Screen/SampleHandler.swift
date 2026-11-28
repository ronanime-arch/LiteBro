import CoreImage
import ImageIO
import notify
import ReplayKit
import UIKit

/// The screen of the phone for the computer: iOS hands the frames of the screen recording here
/// (Control Center → Screen Recording → LiteBro), they go out as JPEG pictures at
/// http://<phone>:8081/<secret>/ while the secret that LiteBro gave after Face ID holds.
final class SampleHandler: RPBroadcastSampleHandler {
    private let server = MiniHTTP()
    private let frames = FrameStore()
    private var last = Date.distantPast
    /// Checks every second that LiteBro still allows the showing: «Запретить показ» or the end of its time stop it
    /// even while the screen does not change (no frames come then, so the frame handler cannot notice)
    private var watchdog: DispatchSourceTimer?
    /// «Запретить показ» from the app, heard at once (`ScreenShare.revoke`)
    private var revokeWatch: Int32 = NOTIFY_TOKEN_INVALID
    private var stopped = false

    override func broadcastStarted(withSetupInfo setupInfo: [String: NSObject]?) {
        ScreenShare.setState("starting")
        guard let token = ScreenShare.token else {
            ScreenShare.setState("no-token")
            return stop(T("Сначала включите показ экрана в LiteBro: меню «…» → «Доступ с ПК».", "Turn on screen sharing in LiteBro first: “…” menu → “Access from PC”."), off: false)
        }
        let store = frames
        do {
            try server.start(port: ScreenShare.port) { ScreenHandler(token: token, frames: store) }
            ScreenShare.setState("on")
            let timer = DispatchSource.makeTimerSource(queue: .global(qos: .utility))
            timer.schedule(deadline: .now() + 1, repeating: 1)
            timer.setEventHandler { [weak self] in
                if ScreenShare.token != token {
                    self?.stop(T("Показ экрана запрещён в LiteBro.", "Screen sharing was turned off in LiteBro."))
                } else if let until = ScreenShare.until, until < Date() {
                    self?.stop(T("Время показа экрана вышло.", "Screen sharing time is over."))
                }
            }
            timer.resume()
            watchdog = timer
            notify_register_dispatch(ScreenShare.revoked, &revokeWatch, DispatchQueue.global(qos: .utility)) { [weak self] _ in
                self?.stop(T("Показ экрана запрещён в LiteBro.", "Screen sharing was turned off in LiteBro."))
            }
        } catch {
            ScreenShare.setState("port: \(error)")
            stop(T("Не удалось открыть порт", "Could not open port") + " \(ScreenShare.port)", off: false)
        }
    }

    override func broadcastFinished() {
        shut()
        ScreenShare.setState("off")
    }

    /// Nothing goes out any more: the address stops answering before iOS ends the extension
    private func shut() {
        watchdog?.cancel()
        watchdog = nil
        if revokeWatch != NOTIFY_TOKEN_INVALID { notify_cancel(revokeWatch); revokeWatch = NOTIFY_TOKEN_INVALID }
        frames.close()
        server.stop()
    }

    /// `off`: the showing was on and ends here (a start that failed has left its own reason for the app)
    private func stop(_ why: String, off: Bool = true) {
        DispatchQueue.main.async { [self] in
            if stopped { return }
            stopped = true
            shut()
            if off { ScreenShare.setState("off") }
            finishBroadcastWithError(NSError(domain: "LiteBro", code: 1, userInfo: [NSLocalizedDescriptionKey: why]))
        }
    }

    override func processSampleBuffer(_ sampleBuffer: CMSampleBuffer, with type: RPSampleBufferType) {
        guard type == .video, let pixels = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        // The phone turned: the frame says how
        var orientation = CGImagePropertyOrientation.up
        if let o = CMGetAttachment(sampleBuffer, key: RPVideoSampleOrientationKey as CFString, attachmentModeOut: nil) as? NSNumber,
           let known = CGImagePropertyOrientation(rawValue: o.uint32Value) {
            orientation = known
        }
        // iOS hands a frame only when the screen changes, so the newest one is what the screen shows until the
        // next. It is always kept (one frame, not encoded): /frame and a new viewer get the screen as it is now,
        // not the picture left from the last viewer, and the last frame of a burst is not lost.
        frames.keep(pixels, orientation)
        // About 8 pictures a second, while somebody watches the stream: the extension has little memory and CPU
        let now = Date()
        guard frames.watched, now.timeIntervalSince(last) >= 0.12 else { return }
        if let until = ScreenShare.until, until < now { return stop(T("Время показа экрана вышло.", "Screen sharing time is over.")) }
        last = now
        frames.encode(wait: false)
    }
}

/// The screen for the viewers: the newest frame as iOS gave it, and the last picture made of it
final class FrameStore {
    private let lock = NSCondition()
    private var frame: Data?
    private var number = 0
    private var viewers = 0
    private var closed = false
    /// The newest frame of the screen until its picture is made
    private var kept: CVPixelBuffer?
    private var keptOrientation = CGImagePropertyOrientation.up
    /// When the screen last changed
    private var changed = Date()
    /// One picture is made at a time
    private let encoding = NSLock()
    private let context = CIContext(options: [.useSoftwareRenderer: false])
    /// How the last picture was turned from the upright screen: taps go back the other way
    var orientation = CGImagePropertyOrientation.up

    var watched: Bool { lock.lock(); defer { lock.unlock() }; return viewers > 0 }
    var isClosed: Bool { lock.lock(); defer { lock.unlock() }; return closed }

    func keep(_ pixels: CVPixelBuffer, _ o: CGImagePropertyOrientation) {
        lock.lock()
        kept = pixels
        keptOrientation = o
        changed = Date()
        lock.unlock()
    }

    /// Makes the picture of the newest frame unless it has one. Any thread; with `wait` off it gives way
    /// to a picture being made (the frame stays kept for the next call).
    func encode(wait: Bool = true) {
        if wait { encoding.lock() } else if !encoding.try() { return }
        defer { encoding.unlock() }
        lock.lock()
        let pixels = kept, o = keptOrientation
        kept = nil
        lock.unlock()
        guard let pixels else { return }
        // In a pool of its own: Core Image leaves autoreleased objects, and the stream's loop is one block
        // that never returns to let its thread's pool go (the extension has about 50 MB)
        let made = autoreleasepool { () -> Data? in
            var image = CIImage(cvPixelBuffer: pixels).oriented(o)
            let longest = max(image.extent.width, image.extent.height)
            if longest > 1280 { image = image.transformed(by: CGAffineTransform(scaleX: 1280 / longest, y: 1280 / longest)) }
            guard let space = CGColorSpace(name: CGColorSpace.sRGB) else { return nil }
            return context.jpegRepresentation(of: image, colorSpace: space,
                                              options: [CIImageRepresentationOption(rawValue: kCGImageDestinationLossyCompressionQuality as String): 0.55])
        }
        guard let jpeg = made else { return }
        lock.lock()
        frame = jpeg
        number += 1
        orientation = o
        lock.broadcast()
        lock.unlock()
    }

    func close() {
        lock.lock()
        closed = true
        kept = nil
        lock.broadcast()
        lock.unlock()
    }

    func join() { lock.lock(); viewers += 1; lock.unlock() }
    func leave() { lock.lock(); viewers -= 1; lock.unlock() }

    /// The screen as it is now: its picture, the picture's number and for how long the screen has been still;
    /// nil when iOS has given no frame yet
    func current() -> (picture: Data, number: Int, still: TimeInterval)? {
        encode()
        lock.lock()
        defer { lock.unlock() }
        guard !closed, let f = frame else { return nil }
        return (f, number, Date().timeIntervalSince(changed))
    }

    /// The picture after the one numbered `after`, waited for up to `wait` s; nil when none came (or closed)
    func next(after: Int, wait: TimeInterval) -> (Data, Int)? {
        lock.lock()
        defer { lock.unlock() }
        let deadline = Date().addingTimeInterval(wait)
        while number <= after && !closed {
            if !lock.wait(until: deadline) { break }
        }
        guard !closed, number > after, let f = frame else { return nil }
        return (f, number)
    }
}

/// The page with the picture, one picture (/frame) and the stream of pictures (multipart/x-mixed-replace,
/// as webcams send)
private final class ScreenHandler: HTTPHandler {
    private let token: String
    private let frames: FrameStore

    init(token: String, frames: FrameStore) {
        self.token = token
        self.frames = frames
    }

    private var body = Data()

    func onHead(_ r: HTTPRequest, _ c: HTTPConnection) { body = Data() }

    func onBody(_ d: Data, _ c: HTTPConnection) {
        if body.count < 4096 { body.append(d) }
    }

    func onEnd(_ r: HTTPRequest, _ c: HTTPConnection) { handle(r, channel: c) }

    private func handle(_ h: HTTPRequest, channel: HTTPConnection) {
        let path = String(h.uri.split(separator: "?").first ?? "")
        if path == "/\(token)/" || path == "/\(token)" {
            channel.respond(200, headers: [("Content-Type", "text/html; charset=utf-8"), ("Cache-Control", "no-store")],
                            body: Data(Self.page(control: ScreenShare.control && Touch.available).utf8))
        } else if path == "/\(token)/frame" {
            frame(h, channel)
        } else if path == "/\(token)/stream" {
            stream(channel)
        } else if path == "/\(token)/touch", h.method == "POST" {
            // Only while the app allows it: the same secret, Face ID for control on top
            guard ScreenShare.token == token, ScreenShare.control,
                  let m = try? JSONSerialization.jsonObject(with: body) as? [String: Any] else { return channel.respond(403) }
            if let b = m["button"] as? String {
                Touch.button(b == "side" ? 0x30 : 0x40)
            } else if let p = (m["t"] as? String).flatMap(Touch.Phase.init(rawValue:)),
                      let x = m["x"] as? Double, let y = m["y"] as? Double {
                let upright = Self.upright(x, y, frames.orientation)
                Touch.finger(p, x: min(max(upright.0, 0), 1), y: min(max(upright.1, 0), 1), Touch.Options(m))
            }
            channel.respond(204)
        } else {
            channel.respond(404)
        }
    }

    /// A point of the picture as shown back to the screen held upright
    private static func upright(_ u: Double, _ v: Double, _ o: CGImagePropertyOrientation) -> (Double, Double) {
        switch o {
        case .right, .rightMirrored: return (v, 1 - u)
        case .left, .leftMirrored: return (1 - v, u)
        case .down, .downMirrored: return (1 - u, 1 - v)
        default: return (u, v)
        }
    }

    private static func page(control: Bool) -> String {
        let script = !control ? "" : """
        <div id=bar><button data-b=home>\(T("Домой", "Home"))</button><button data-b=side>\(T("Боковая кнопка", "Side button"))</button></div>
        <script>
        const img = document.querySelector("img");
        let down = false, last = 0;
        const send = (t, e) => {
          const r = img.getBoundingClientRect();
          fetch("touch", { method: "POST", body: JSON.stringify({ t, x: (e.clientX - r.left) / r.width, y: (e.clientY - r.top) / r.height }) });
        };
        img.addEventListener("pointerdown", e => { e.preventDefault(); img.setPointerCapture(e.pointerId); down = true; send("down", e); });
        img.addEventListener("pointermove", e => { if (!down) return; const n = performance.now(); if (n - last < 30) return; last = n; send("move", e); });
        img.addEventListener("pointerup", e => { if (!down) return; down = false; send("up", e); });
        img.addEventListener("dragstart", e => e.preventDefault());
        for (const b of document.querySelectorAll("[data-b]")) b.onclick = () => fetch("touch", { method: "POST", body: JSON.stringify({ button: b.dataset.b }) });
        </script>
        """
        return """
        <!doctype html><html><head><meta charset=utf-8><title>\(T("Экран телефона", "Phone screen"))</title>
        <style>html,body{margin:0;height:100%;background:#111;color:#eee;font:14px system-ui}img{display:block;margin:auto;max-width:100%;max-height:calc(100vh - 44px);object-fit:contain;touch-action:none;user-select:none}
        #bar{display:flex;gap:8px;justify-content:center;padding:6px}#bar button{font:inherit;padding:6px 14px;border-radius:8px;border:1px solid #555;background:#222;color:#eee;cursor:pointer}</style>
        </head><body><img src="stream" alt="">\(script)</body></html>
        """
    }

    /// One picture: the screen as it is now. X-Frame is its number (it grows with every change of the screen),
    /// X-Frame-Age for how many milliseconds the screen has been still. With ?after=N nothing is sent (204)
    /// unless there is a picture newer than N: a cheap way to wait for the screen to change.
    private func frame(_ h: HTTPRequest, _ channel: HTTPConnection) {
        let after = URLComponents(string: h.uri)?.queryItems?.first { $0.name == "after" }?.value.flatMap { Int($0) }
        let frames = self.frames
        DispatchQueue.global(qos: .userInitiated).async {
            guard let now = frames.current() else { return channel.respond(503) }
            let state = [("X-Frame", String(now.number)), ("X-Frame-Age", String(Int(now.still * 1000))), ("Cache-Control", "no-store")]
            if let after, now.number <= after { return channel.respond(204, headers: state) }
            channel.respond(200, headers: state + [("Content-Type", "image/jpeg")], body: now.picture)
        }
    }

    private func stream(_ channel: HTTPConnection) {
        let boundary = "litebroframe"
        channel.sendHead(200, headers: [("Content-Type", "multipart/x-mixed-replace; boundary=\(boundary)"),
                                        ("Cache-Control", "no-store"), ("Connection", "close")])
        let frames = self.frames
        DispatchQueue.global(qos: .userInitiated).async {
            frames.join()
            defer { frames.leave() }
            func send(_ jpeg: Data) {
                var part = Data("--\(boundary)\r\nContent-Type: image/jpeg\r\nContent-Length: \(jpeg.count)\r\n\r\n".utf8)
                part.append(jpeg)
                part.append(Data("\r\n".utf8))
                channel.sendAndWait(part)
            }
            // The screen as it is now first
            frames.encode()
            var seen = 0
            var sent = Date()
            while channel.isActive, !frames.isClosed {
                // Each turn in its own pool: this block runs for as long as the viewer watches
                autoreleasepool {
                    if let got = frames.next(after: seen, wait: 0.5) {
                        seen = got.1
                        send(got.0)
                        sent = Date()
                    } else {
                        // Half a second without a picture: the last frame of a burst may still wait for its own
                        frames.encode()
                        // A still screen: the same picture again now and then, so that a viewer who left is
                        // noticed (a send to a closed connection fails) and the stream does not look dead
                        if Date().timeIntervalSince(sent) > 5, let now = frames.current(), now.number <= seen {
                            send(now.picture)
                            sent = Date()
                        }
                    }
                }
            }
            channel.close()
        }
    }
}

/// The extension has no strings file of its own: Russian for a Russian-speaking phone, else English
private func T(_ ru: String, _ en: String) -> String {
    let lang = Locale.preferredLanguages.first?.prefix(2) ?? "en"
    return ["ru", "uk", "be", "kk"].contains(String(lang)) ? ru : en
}
