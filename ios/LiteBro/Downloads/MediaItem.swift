import Foundation

/// A video or audio stream found on a page (or offered by the YouTube extractor)
struct MediaItem: Identifiable, Hashable {
    enum Kind: Hashable { case hls, file }

    let id = UUID()
    let url: URL
    let kind: Kind
    var title: String
    var page: URL?
    /// Quality and the like, shown under the name
    var note: String = ""
    /// Headers the server wants (YouTube: its own client's User-Agent)
    var headers: [String: String] = [:]
    /// File extension when the address has none (YouTube: /videoplayback)
    var ext: String? = nil

    /// Seconds, from the player or the playlist
    var duration: Double?
    var bytes: Int64?
    var playing = false
    var live = false
    /// "up to 1080p" of an HLS master playlist
    var quality: String?
    /// A variant of a master playlist on the same page: hidden, the master gives the best one
    var variantOf: URL?

    /// Files differ only by their query (byte ranges, tokens): one per path
    var key: String { kind == .file ? (url.host ?? "") + url.path : url.absoluteString }

    /// Short ones are most likely ads
    var isShort: Bool {
        if live || playing { return false }
        if let d = duration, d > 0 { return d < 45 }
        if let b = bytes { return b < 1_500_000 }
        return false
    }

    /// Longer and larger first: the film before the ad
    var weight: Double { (duration ?? 0) * 1_000_000 + Double(bytes ?? 0) / 1000 }

    var label: String {
        let name = url.lastPathComponent.isEmpty ? (url.host ?? url.absoluteString) : url.lastPathComponent
        return kind == .hls ? "HLS · " + name : name
    }

    var details: String {
        var parts = [url.host ?? ""]
        if live { parts.append(L("прямой эфир")) }
        else if let d = duration, d > 0 { parts.append(Self.time(d)) }
        if let q = quality { parts.append(q) }
        if let b = bytes { parts.append(ByteCountFormatter.string(fromByteCount: b, countStyle: .file)) }
        return parts.filter { !$0.isEmpty }.joined(separator: " · ")
    }

    static func time(_ s: Double) -> String {
        let t = Int(s.rounded())
        return t >= 3600 ? String(format: "%d:%02d:%02d", t / 3600, t / 60 % 60, t % 60)
                         : String(format: "%d:%02d", t / 60, t % 60)
    }

    /// A file name without characters iOS file names cannot have
    func fileName(ext: String) -> String {
        var base = title.isEmpty ? url.deletingPathExtension().lastPathComponent : title
        base = base.components(separatedBy: CharacterSet(charactersIn: "/\\:?%*|\"<>\n\r\t")).joined(separator: " ")
            .trimmingCharacters(in: .whitespaces)
        if base.isEmpty { base = "video" }
        if base.count > 80 { base = String(base.prefix(80)) }
        return base + "." + ext
    }

    static func kind(of url: URL, hls flag: Bool = false) -> Kind {
        flag || url.pathExtension.lowercased() == "m3u8" ? .hls : .file
    }

    static func == (a: MediaItem, b: MediaItem) -> Bool { a.id == b.id }
    func hash(into h: inout Hasher) { h.combine(id) }
}
