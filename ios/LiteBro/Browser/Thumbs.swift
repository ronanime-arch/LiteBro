import CryptoKit
import SwiftUI
import WebKit

/// Small pictures of saved pages for the folders of tiles: taken when a page is saved and each time a tab
/// with a saved page is left (another tab, the app in the background), so a folder shows the page as it was left.
final class Thumbs: ObservableObject {
    static let shared = Thumbs()

    /// Bumped on every new picture, so the folders redraw
    @Published private(set) var version = 0
    private var cache: [String: UIImage] = [:]

    private let dir: URL = {
        let d = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("thumbs", isDirectory: true)
        try? FileManager.default.createDirectory(at: d, withIntermediateDirectories: true)
        return d
    }()

    private func file(_ url: String) -> URL {
        let h = SHA256.hash(data: Data(url.utf8)).prefix(16).map { String(format: "%02x", $0) }.joined()
        return dir.appendingPathComponent(h + ".jpg")
    }

    func image(_ url: String) -> UIImage? {
        if let i = cache[url] { return i }
        guard let i = UIImage(contentsOfFile: file(url).path) else { return nil }
        cache[url] = i
        return i
    }

    /// The visible part of the page, 360 points wide
    func capture(_ w: WKWebView?, as url: String) {
        guard let w, w.url != nil, w.bounds.width > 0 else { return }
        let c = WKSnapshotConfiguration()
        c.snapshotWidth = 360
        w.takeSnapshot(with: c) { [weak self] img, _ in
            guard let self, let img, let data = img.jpegData(compressionQuality: 0.6) else { return }
            try? data.write(to: self.file(url), options: .atomic)
            self.cache[url] = img
            self.version += 1
        }
    }

    func forget(_ url: String) {
        cache[url] = nil
        try? FileManager.default.removeItem(at: file(url))
    }
}
