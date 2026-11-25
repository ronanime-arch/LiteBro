import Foundation

/// "Translate page" through Google Translate's own page proxy (translate.goog): the same translator
/// Chrome uses, without Chrome. Pages behind a login or on the local network are not reachable for it.
enum Translate {
    static var language: String {
        let code = Locale.preferredLanguages.first.map { String($0.prefix(2)) } ?? "en"
        return code
    }

    static func url(for u: URL) -> URL? {
        guard u.scheme == "https" || u.scheme == "http", let host = u.host?.lowercased(),
              !host.hasSuffix(".translate.goog"), !LocalNet.isLocal(host: host) else { return nil }
        // a-b.example.com → a--b-example-com.translate.goog
        let proxied = host.replacingOccurrences(of: "-", with: "--").replacingOccurrences(of: ".", with: "-")
        var c = URLComponents()
        c.scheme = "https"
        c.host = proxied + ".translate.goog"
        c.percentEncodedPath = u.path.isEmpty ? "/" : URLComponents(url: u, resolvingAgainstBaseURL: false)?.percentEncodedPath ?? "/"
        var items = URLComponents(url: u, resolvingAgainstBaseURL: false)?.queryItems ?? []
        items += [URLQueryItem(name: "_x_tr_sl", value: "auto"), URLQueryItem(name: "_x_tr_tl", value: language),
                  URLQueryItem(name: "_x_tr_hl", value: language)]
        c.queryItems = items
        return c.url
    }

    /// The page in Chrome for iOS, when it is installed (it translates there too)
    static func chrome(_ u: URL) -> URL? {
        guard var c = URLComponents(url: u, resolvingAgainstBaseURL: false) else { return nil }
        switch c.scheme {
        case "https": c.scheme = "googlechromes"
        case "http": c.scheme = "googlechrome"
        default: return nil
        }
        return c.url
    }
}
