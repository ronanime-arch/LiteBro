import CoreImage
import ImageIO
import ReplayKit
import UIKit

/// The screen of the phone for the computer: iOS hands the frames of the screen recording here
/// (Control Center → Screen Recording → LiteBro), they go out as JPEG pictures at
/// http://<phone>:8081/<secret>/ while the secret that LiteBro gave after Face ID holds.
final class SampleHandler: RPBroadcastSampleHandler {
    private let server = MiniHTTP()
    private let frames = FrameStore()
    private let context = CIContext(options: [.useSoftwareRenderer: false])
    private var last = Date.distantPast

    override func broadcastStarted(withSetupInfo setupInfo: [String: NSObject]?) {
        ScreenShare.setState("starting")
        guard let token = ScreenShare.token else {
            ScreenShare.setState("no-token")
            return stop(T("Сначала включите показ экрана в LiteBro: меню «…» → «Доступ с ПК».", "Turn on screen sharing in LiteBro first: “…” menu → “Access from PC”."))
        }
        let store = frames
        do {
            try server.start(port: ScreenShare.port) { ScreenHandler(token: token, frames: store) }
            ScreenShare.setState("on")
        } catch {
            ScreenShare.setState("port: \(error)")
            stop(T("Не удалось открыть порт", "Could not open port") + " \(ScreenShare.port)")
        }
    }

    override func broadcastFinished() {
        ScreenShare.setState("off")
        frames.close()
        server.stop()
    }

    private func stop(_ why: String) {
        finishBroadcastWithError(NSError(domain: "LiteBro", code: 1, userInfo: [NSLocalizedDescriptionKey: why]))
    }

    override func processSampleBuffer(_ sampleBuffer: CMSampleBuffer, with type: RPSampleBufferType) {
        guard type == .video else { return }
        // About 8 pictures a second, while somebody watches: the extension has little memory and CPU
        let now = Date()
        guard frames.watched, now.timeIntervalSince(last) >= 0.12 else { return }
        if let until = ScreenShare.until, until < now { return stop(T("Время показа экрана вышло.", "Screen sharing time is over.")) }
        last = now
        guard let pixels = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        var image = CIImage(cvPixelBuffer: pixels)
        // The phone turned: the frame says how
        if let o = CMGetAttachment(sampleBuffer, key: RPVideoSampleOrientationKey as CFString, attachmentModeOut: nil) as? NSNumber,
           let orientation = CGImagePropertyOrientation(rawValue: o.uint32Value) {
            image = image.oriented(orientation)
            frames.orientation = orientation
        }
        let longest = max(image.extent.width, image.extent.height)
        if longest > 1280 { image = image.transformed(by: CGAffineTransform(scaleX: 1280 / longest, y: 1280 / longest)) }
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let jpeg = context.jpegRepresentation(of: image, colorSpace: space,
                                                    options: [CIImageRepresentationOption(rawValue: kCGImageDestinationLossyCompressionQuality as String): 0.55])
        else { return }
        frames.put(jpeg)
    }
}

/// The last picture; viewers wait for the next one
final class FrameStore {
    private let lock = NSCondition()
    private var frame: Data?
    private var number = 0
    private var viewers = 0
    private var closed = false
    /// How the last picture was turned from the upright screen: taps go back the other way
    var orientation = CGImagePropertyOrientation.up

    var watched: Bool { lock.lock(); defer { lock.unlock() }; return viewers > 0 }

    func put(_ d: Data) {
        lock.lock()
        frame = d
        number += 1
        lock.broadcast()
        lock.unlock()
    }

    func close() {
        lock.lock()
        closed = true
        lock.broadcast()
        lock.unlock()
    }

    func join() { lock.lock(); viewers += 1; lock.unlock() }
    func leave() { lock.lock(); viewers -= 1; lock.unlock() }

    /// The picture after the one numbered `after`; nil once closed or after 10 s without one
    func next(after: Int) -> (Data, Int)? {
        lock.lock()
        defer { lock.unlock() }
        let deadline = Date().addingTimeInterval(10)
        while number <= after && !closed {
            if !lock.wait(until: deadline) { return nil }
        }
        guard !closed, let f = frame else { return nil }
        return (f, number)
    }
}

/// The page with the picture and the stream of pictures (multipart/x-mixed-replace, as webcams send)
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

    private func stream(_ channel: HTTPConnection) {
        let boundary = "litebroframe"
        channel.sendHead(200, headers: [("Content-Type", "multipart/x-mixed-replace; boundary=\(boundary)"),
                                        ("Cache-Control", "no-store"), ("Connection", "close")])
        let frames = self.frames
        DispatchQueue.global(qos: .userInitiated).async {
            frames.join()
            defer { frames.leave() }
            var seen = 0
            while channel.isActive, let got = frames.next(after: seen) {
                let jpeg = got.0
                seen = got.1
                var part = Data("--\(boundary)\r\nContent-Type: image/jpeg\r\nContent-Length: \(jpeg.count)\r\n\r\n".utf8)
                part.append(jpeg)
                part.append(Data("\r\n".utf8))
                channel.sendAndWait(part)
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
