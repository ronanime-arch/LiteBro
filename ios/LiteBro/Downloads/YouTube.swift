import Foundation

/// Links to a YouTube video through YouTube's own iPhone client (the way yt-dlp does it):
/// the player answer of that client has an HLS manifest and plain mp4 links without signature ciphers.
/// YouTube changes this from time to time; then the client constants below need updating.
enum YouTube {
    private static let clientVersion = "20.10.4"
    private static let userAgent = "com.google.ios.youtube/\(clientVersion) (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)"

    enum Failure: LocalizedError {
        case notPlayable(String)
        case nothing
        var errorDescription: String? {
            switch self {
            case .notPlayable(let why): return L("YouTube не отдал видео") + (why.isEmpty ? "" : ": " + why)
            case .nothing: return L("YouTube не отдал ссылок: возможно, он поменял защиту")
            }
        }
    }

    /// The video id of a YouTube page, or nil
    static func videoID(_ url: URL?) -> String? {
        guard let url, let host = url.host?.lowercased() else { return nil }
        let path = url.path
        if host == "youtu.be" { return clean(String(path.dropFirst())) }
        guard host == "youtube.com" || host.hasSuffix(".youtube.com") || host.hasSuffix("youtube-nocookie.com") else { return nil }
        if path == "/watch" {
            return clean(URLComponents(url: url, resolvingAgainstBaseURL: false)?
                .queryItems?.first(where: { $0.name == "v" })?.value)
        }
        for prefix in ["/shorts/", "/embed/", "/live/", "/v/"] where path.hasPrefix(prefix) {
            return clean(String(path.dropFirst(prefix.count).split(separator: "/").first ?? ""))
        }
        return nil
    }

    private static func clean(_ s: String?) -> String? {
        guard let s, s.range(of: "^[A-Za-z0-9_-]{11}$", options: .regularExpression) != nil else { return nil }
        return s
    }

    static func streams(_ id: String, page: URL?) async throws -> [MediaItem] {
        var req = URLRequest(url: URL(string: "https://www.youtube.com/youtubei/v1/player?prettyPrint=false")!)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        req.setValue("5", forHTTPHeaderField: "X-YouTube-Client-Name")
        req.setValue(clientVersion, forHTTPHeaderField: "X-YouTube-Client-Version")
        let body: [String: Any] = [
            "context": ["client": [
                "clientName": "IOS", "clientVersion": clientVersion,
                "deviceMake": "Apple", "deviceModel": "iPhone16,2",
                "osName": "iPhone", "osVersion": "18.3.2.22D82",
                "hl": Locale.current.language.languageCode?.identifier ?? "en",
            ]],
            "videoId": id, "contentCheckOk": true, "racyCheckOk": true,
        ]
        req.httpBody = try JSONSerialization.data(withJSONObject: body)
        let (data, _) = try await URLSession.shared.data(for: req)
        guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw Failure.nothing }

        let status = json["playabilityStatus"] as? [String: Any]
        if let s = status?["status"] as? String, s != "OK" {
            throw Failure.notPlayable(status?["reason"] as? String ?? s)
        }
        let title = (json["videoDetails"] as? [String: Any])?["title"] as? String ?? id
        let streaming = json["streamingData"] as? [String: Any] ?? [:]
        let headers = ["User-Agent": userAgent]
        var out: [MediaItem] = []

        if let s = streaming["hlsManifestUrl"] as? String, let u = URL(string: s) {
            out.append(MediaItem(url: u, kind: .hls, title: title, page: page,
                                 note: L("Лучшее качество, видео со звуком (HLS)"), headers: headers))
        }
        // Progressive formats: video with sound in one file (usually only 360p)
        for f in streaming["formats"] as? [[String: Any]] ?? [] {
            guard let s = f["url"] as? String, let u = URL(string: s) else { continue }
            let q = f["qualityLabel"] as? String ?? ""
            out.append(MediaItem(url: u, kind: .file, title: title, page: page,
                                 note: "mp4 " + q + " · " + L("видео со звуком"), headers: headers, ext: "mp4"))
        }
        // Sound only, for music
        let audio = (streaming["adaptiveFormats"] as? [[String: Any]] ?? [])
            .filter { ($0["mimeType"] as? String ?? "").hasPrefix("audio/mp4") && $0["url"] != nil }
            .max { ($0["bitrate"] as? Int ?? 0) < ($1["bitrate"] as? Int ?? 0) }
        if let a = audio, let s = a["url"] as? String, let u = URL(string: s) {
            out.append(MediaItem(url: u, kind: .file, title: title, page: page,
                                 note: "m4a · " + L("только звук"), headers: headers, ext: "m4a"))
        }
        if out.isEmpty { throw Failure.nothing }
        return out
    }
}
