import CoreImage
import ImageIO
import NIOCore
import NIOHTTP1
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
        guard let token = ScreenShare.token else {
            return stop(T("Сначала включите показ экрана в LiteBro: меню «…» → «Доступ с ПК».", "Turn on screen sharing in LiteBro first: “…” menu → “Access from PC”."))
        }
        let store = frames
        do {
            try server.start(port: ScreenShare.port) { ScreenHandler(token: token, frames: store) }
        } catch {
            stop(T("Не удалось открыть порт", "Could not open port") + " \(ScreenShare.port)")
        }
    }

    override func broadcastFinished() {
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
private final class ScreenHandler: ChannelInboundHandler {
    typealias InboundIn = HTTPServerRequestPart
    typealias OutboundOut = HTTPServerResponsePart

    private let token: String
    private let frames: FrameStore

    init(token: String, frames: FrameStore) {
        self.token = token
        self.frames = frames
    }

    func channelRead(context: ChannelHandlerContext, data: NIOAny) {
        guard case .head(let h) = unwrapInboundIn(data) else { return }
        let path = String(h.uri.split(separator: "?").first ?? "")
        let channel = context.channel
        if path == "/\(token)/" || path == "/\(token)" {
            let page = """
            <!doctype html><html><head><meta charset=utf-8><title>\(T("Экран телефона", "Phone screen"))</title>
            <style>html,body{margin:0;height:100%;background:#111}img{display:block;margin:auto;max-width:100%;max-height:100vh;object-fit:contain}</style>
            </head><body><img src="stream" alt=""></body></html>
            """
            channel.respond(.ok, headers: [("Content-Type", "text/html; charset=utf-8"), ("Cache-Control", "no-store")], body: Data(page.utf8))
        } else if path == "/\(token)/stream" {
            stream(channel)
        } else {
            channel.respond(.notFound)
        }
    }

    private func stream(_ channel: Channel) {
        let boundary = "litebroframe"
        var headers = HTTPHeaders([("Content-Type", "multipart/x-mixed-replace; boundary=\(boundary)"),
                                   ("Cache-Control", "no-store"), ("Connection", "close")])
        headers.remove(name: "Content-Length")
        channel.writeAndFlush(HTTPServerResponsePart.head(HTTPResponseHead(version: .http1_1, status: .ok, headers: headers)), promise: nil)
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
                let sent = DispatchSemaphore(value: 0)
                channel.writeAndFlush(HTTPServerResponsePart.body(.byteBuffer(ByteBuffer(bytes: part)))).whenComplete { _ in sent.signal() }
                sent.wait()
            }
            channel.close(promise: nil)
        }
    }
}

/// The extension has no strings file of its own: Russian for a Russian-speaking phone, else English
private func T(_ ru: String, _ en: String) -> String {
    let lang = Locale.preferredLanguages.first?.prefix(2) ?? "en"
    return ["ru", "uk", "be", "kk"].contains(String(lang)) ? ru : en
}
