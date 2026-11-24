import AVFoundation
import UIKit
import WebKit

struct DownloadItem: Identifiable {
    enum State: Equatable { case running, exporting, done, failed(String) }

    let id = UUID()
    var name: String
    var source: URL?
    var state: State = .running
    /// 0…1, or -1 while the size is unknown
    var progress: Double = 0
    var file: URL?
    var date = Date()

    var isVideo: Bool { ["mp4", "m4v", "mov", "movpkg", "webm", "mkv"].contains(ext) }
    var isAudio: Bool { ["m4a", "mp3", "aac", "wav", "ogg", "opus", "flac"].contains(ext) }
    var isPlayable: Bool { isVideo || isAudio }
    private var ext: String { (file?.pathExtension ?? (name as NSString).pathExtension).lowercased() }
}

/// Downloads into Documents/Downloads (seen in the Files app as "On My iPhone › LiteBro"):
/// plain files through URLSession with the page's cookies, HLS through AVAssetDownloadURLSession
/// (segments, AES-128 keys, separate audio), then a passthrough export to mp4.
final class DownloadManager: NSObject, ObservableObject {
    static let shared = DownloadManager()

    @Published private(set) var items: [DownloadItem] = []
    let folder: URL

    private var owner: [ObjectIdentifier: UUID] = [:]      // task or WKDownload → item
    private var cancels: [UUID: () -> Void] = [:]
    private var packages: [UUID: URL] = [:]                 // finished HLS .movpkg
    private var watches: [UUID: NSKeyValueObservation] = [:]

    private lazy var files = URLSession(configuration: .default, delegate: self, delegateQueue: .main)
    private lazy var streams = AVAssetDownloadURLSession(
        configuration: .background(withIdentifier: "io.github.ronanime-arch.LiteBro.hls"),
        assetDownloadDelegate: self, delegateQueue: .main)

    override init() {
        let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        folder = docs.appendingPathComponent("Downloads", isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        super.init()
        loadFinished()
        // HLS tasks left from the last run have nobody to finish them
        streams.getAllTasks { tasks in tasks.forEach { $0.cancel() } }
    }

    private func loadFinished() {
        let keys: [URLResourceKey] = [.creationDateKey]
        let found = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: keys,
                                                                   options: [.skipsHiddenFiles])) ?? []
        items = found.map { url in
            var item = DownloadItem(name: url.lastPathComponent, source: nil, state: .done, progress: 1, file: url)
            item.date = (try? url.resourceValues(forKeys: Set(keys)).creationDate) ?? Date.distantPast
            return item
        }.sorted { $0.date > $1.date }
    }

    // MARK: Start

    func start(_ m: MediaItem, cookies store: WKHTTPCookieStore) {
        store.getAllCookies { cookies in
            DispatchQueue.main.async {
                m.kind == .hls ? self.startStream(m, cookies) : self.startFile(m, cookies)
            }
        }
    }

    private func startFile(_ m: MediaItem, _ cookies: [HTTPCookie]) {
        cookies.forEach { HTTPCookieStorage.shared.setCookie($0) }
        var req = URLRequest(url: m.url)
        if let p = m.page { req.setValue(p.absoluteString, forHTTPHeaderField: "Referer") }
        m.headers.forEach { req.setValue($0.value, forHTTPHeaderField: $0.key) }
        let ext = m.ext ?? (m.url.pathExtension.isEmpty ? "mp4" : m.url.pathExtension.lowercased())
        let task = files.downloadTask(with: req)
        track(DownloadItem(name: m.fileName(ext: ext), source: m.url), task) { task.cancel() }
        task.resume()
    }

    private func startStream(_ m: MediaItem, _ cookies: [HTTPCookie]) {
        var headers = m.headers
        if let p = m.page { headers["Referer"] = p.absoluteString }
        var options: [String: Any] = [AVURLAssetHTTPCookiesKey: cookies]
        if !headers.isEmpty { options["AVURLAssetHTTPHeaderFieldsKey"] = headers }
        let asset = AVURLAsset(url: m.url, options: options)
        let item = DownloadItem(name: m.fileName(ext: "mp4"), source: m.url)
        // A bitrate higher than any variant: AVFoundation then takes the best one
        guard let task = streams.makeAssetDownloadTask(
            asset: asset, assetTitle: m.title.isEmpty ? "video" : m.title, assetArtworkData: nil,
            options: [AVAssetDownloadTaskMinimumRequiredMediaBitrateKey: 1_000_000_000]) else {
            var failed = item
            failed.state = .failed(L("Не удалось начать загрузку потока"))
            items.insert(failed, at: 0)
            return
        }
        track(item, task) { task.cancel() }
        task.resume()
    }

    /// A file the page itself sent for download (a link with `download`, a zip and so on)
    func adopt(_ d: WKDownload) { d.delegate = self }

    private func track(_ item: DownloadItem, _ key: AnyObject, cancel: @escaping () -> Void) {
        items.insert(item, at: 0)
        owner[ObjectIdentifier(key)] = item.id
        cancels[item.id] = cancel
    }

    // MARK: Changes

    private func update(_ id: UUID, _ change: (inout DownloadItem) -> Void) {
        guard let i = items.firstIndex(where: { $0.id == id }) else { return }
        change(&items[i])
    }

    private func finish(_ id: UUID, file: URL) {
        cancels[id] = nil
        watches[id] = nil
        update(id) { $0.state = .done; $0.progress = 1; $0.file = file; $0.name = file.lastPathComponent }
    }

    private func fail(_ id: UUID, _ error: Error?) {
        cancels[id] = nil
        watches[id] = nil
        if let e = error as NSError?, e.domain == NSURLErrorDomain, e.code == NSURLErrorCancelled {
            items.removeAll { $0.id == id }
            return
        }
        update(id) { $0.state = .failed(error?.localizedDescription ?? L("Ошибка загрузки")) }
    }

    func remove(_ id: UUID) {
        cancels.removeValue(forKey: id)?()
        watches[id] = nil
        if let pkg = packages.removeValue(forKey: id) { try? FileManager.default.removeItem(at: pkg) }
        if let file = items.first(where: { $0.id == id })?.file { try? FileManager.default.removeItem(at: file) }
        items.removeAll { $0.id == id }
    }

    func saveToPhotos(_ item: DownloadItem) {
        guard let f = item.file, UIVideoAtPathIsCompatibleWithSavedPhotosAlbum(f.path) else { return }
        UISaveVideoAtPathToSavedPhotosAlbum(f.path, nil, nil, nil)
    }

    private func place(_ name: String) -> URL {
        let base = (name as NSString).deletingPathExtension
        let ext = (name as NSString).pathExtension
        var url = folder.appendingPathComponent(name)
        var n = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = folder.appendingPathComponent("\(base) (\(n))" + (ext.isEmpty ? "" : "." + ext))
            n += 1
        }
        return url
    }

    // MARK: HLS → mp4

    private func export(_ id: UUID, package: URL) {
        update(id) { $0.state = .exporting; $0.progress = -1 }
        let name = items.first(where: { $0.id == id })?.name ?? "video.mp4"
        exportAs([(.mp4, "mp4"), (.mov, "mov")], id: id, asset: AVURLAsset(url: package), package: package,
                 base: (name as NSString).deletingPathExtension)
    }

    private func exportAs(_ types: [(AVFileType, String)], id: UUID, asset: AVURLAsset, package: URL, base: String) {
        guard let first = types.first else {
            // No container took the streams as they are: keep the package, the player in LiteBro plays it
            let dest = place(base + ".movpkg")
            if (try? FileManager.default.moveItem(at: package, to: dest)) != nil {
                packages[id] = nil
                finish(id, file: dest)
            } else {
                finish(id, file: package)
            }
            return
        }
        let (type, ext) = first
        guard let session = AVAssetExportSession(asset: asset, presetName: AVAssetExportPresetPassthrough) else {
            exportAs(Array(types.dropFirst()), id: id, asset: asset, package: package, base: base)
            return
        }
        let dest = place(base + "." + ext)
        session.outputURL = dest
        session.outputFileType = type
        session.shouldOptimizeForNetworkUse = true
        session.exportAsynchronously {
            DispatchQueue.main.async {
                if session.status == .completed {
                    try? FileManager.default.removeItem(at: package)
                    self.packages[id] = nil
                    self.finish(id, file: dest)
                } else {
                    NSLog("LiteBro: export \(ext): \(String(describing: session.error))")
                    try? FileManager.default.removeItem(at: dest)
                    self.exportAs(Array(types.dropFirst()), id: id, asset: asset, package: package, base: base)
                }
            }
        }
    }
}

extension DownloadManager: URLSessionDownloadDelegate, AVAssetDownloadDelegate {
    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        guard let id = owner[ObjectIdentifier(downloadTask)] else { return }
        update(id) { $0.progress = totalBytesExpectedToWrite > 0 ? Double(totalBytesWritten) / Double(totalBytesExpectedToWrite) : -1 }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        guard let id = owner[ObjectIdentifier(downloadTask)] else { return }
        if let http = downloadTask.response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            update(id) { $0.state = .failed(L("Сервер ответил") + " \(http.statusCode)") }
            owner[ObjectIdentifier(downloadTask)] = nil
            cancels[id] = nil
            return
        }
        // The temporary file is gone after this call: move it now
        let dest = place(items.first(where: { $0.id == id })?.name ?? location.lastPathComponent)
        do {
            try FileManager.default.moveItem(at: location, to: dest)
            finish(id, file: dest)
        } catch {
            fail(id, error)
        }
    }

    func urlSession(_ session: URLSession, assetDownloadTask: AVAssetDownloadTask, didLoad timeRange: CMTimeRange,
                    totalTimeRangesLoaded loadedTimeRanges: [NSValue], timeRangeExpectedToLoad: CMTimeRange) {
        guard let id = owner[ObjectIdentifier(assetDownloadTask)] else { return }
        let loaded = loadedTimeRanges.reduce(0.0) { $0 + $1.timeRangeValue.duration.seconds }
        let total = timeRangeExpectedToLoad.duration.seconds
        update(id) { $0.progress = total > 0 ? min(loaded / total, 1) : -1 }
    }

    func urlSession(_ session: URLSession, assetDownloadTask: AVAssetDownloadTask, didFinishDownloadingTo location: URL) {
        guard let id = owner[ObjectIdentifier(assetDownloadTask)] else { return }
        packages[id] = location
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let id = owner.removeValue(forKey: ObjectIdentifier(task)) else { return }
        if let error {
            if let pkg = packages.removeValue(forKey: id) { try? FileManager.default.removeItem(at: pkg) }
            fail(id, error)
        } else if task is AVAssetDownloadTask {
            cancels[id] = nil
            if let pkg = packages[id] { export(id, package: pkg) } else { fail(id, nil) }
        }
    }
}

extension DownloadManager: WKDownloadDelegate {
    func download(_ download: WKDownload, decideDestinationUsing response: URLResponse, suggestedFilename: String,
                  completionHandler: @escaping (URL?) -> Void) {
        let dest = place(suggestedFilename.isEmpty ? "download" : suggestedFilename)
        let item = DownloadItem(name: dest.lastPathComponent, source: response.url)
        track(item, download) { download.cancel { _ in } }
        let id = item.id
        watches[id] = download.progress.observe(\.fractionCompleted) { [weak self] p, _ in
            let f = p.fractionCompleted
            DispatchQueue.main.async { self?.update(id) { $0.progress = f } }
        }
        update(id) { $0.file = dest }
        completionHandler(dest)
    }

    func downloadDidFinish(_ download: WKDownload) {
        guard let id = owner.removeValue(forKey: ObjectIdentifier(download)),
              let file = items.first(where: { $0.id == id })?.file else { return }
        finish(id, file: file)
    }

    func download(_ download: WKDownload, didFailWithError error: Error, resumeData: Data?) {
        guard let id = owner.removeValue(forKey: ObjectIdentifier(download)) else { return }
        fail(id, error)
    }
}
