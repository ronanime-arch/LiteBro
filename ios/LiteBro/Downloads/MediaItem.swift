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

    var label: String {
        let name = url.lastPathComponent.isEmpty ? (url.host ?? url.absoluteString) : url.lastPathComponent
        return kind == .hls ? "HLS · " + name : name
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
}
