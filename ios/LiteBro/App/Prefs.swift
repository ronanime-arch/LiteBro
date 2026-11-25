import Foundation

/// Looks up the English text for a Russian phrase (en.lproj/Localizable.strings), like L.T on Windows.
func L(_ s: String) -> String { NSLocalizedString(s, comment: "") }

enum Prefs {
    static let themeKey = "theme"            // dark (default, as on Windows), light, auto
    static let localOnlyKey = "localOnly"
    static let allowHostsKey = "allowHosts"
    static let unloadKey = "unloadAfter"     // minutes, 0 = never
    static let searchKey = "search"          // google, duckduckgo
    static let adBlockKey = "adBlock"        // on by default: built in, costs almost nothing
    static let adBlockOffKey = "adBlockOff"  // sites without blocking, one per line
    static let protectKey = "protect"        // malicious and phishing sites, on by default
    static let consoleKey = "console"        // page console from the start of every page, off by default

    private static var d: UserDefaults { .standard }

    static var localOnly: Bool { d.bool(forKey: localOnlyKey) }
    static var unloadAfter: Int { d.object(forKey: unloadKey) as? Int ?? 5 }
    static var adBlock: Bool { d.object(forKey: adBlockKey) as? Bool ?? true }
    static var protect: Bool { d.object(forKey: protectKey) as? Bool ?? true }
    static var console: Bool { d.bool(forKey: consoleKey) }

    /// Sites where LiteBro never offers to save a password
    static var vaultNever: [String] {
        get { d.stringArray(forKey: "vaultNever") ?? [] }
        set { d.set(newValue, forKey: "vaultNever") }
    }

    static var adBlockOff: [String] {
        get { (d.string(forKey: adBlockOffKey) ?? "").split(separator: "\n").map(String.init) }
        set { d.set(newValue.joined(separator: "\n"), forKey: adBlockOffKey) }
    }

    /// Allowed sites in the localhost-only mode: "x.com" or "*.x.com" (the site and its subdomains)
    static var allowHosts: [String] {
        (d.string(forKey: allowHostsKey) ?? "")
            .split(whereSeparator: { " ,;\n".contains($0) })
            .map { $0.lowercased() }
            .filter { !$0.isEmpty }
    }

    static func searchURL(_ q: String) -> URL? {
        var c = URLComponents(string: d.string(forKey: searchKey) == "duckduckgo"
                              ? "https://duckduckgo.com/" : "https://www.google.com/search")!
        c.queryItems = [URLQueryItem(name: "q", value: q)]
        return c.url
    }

    /// What the address bar typed means: an address (local ones over http) or a search
    static func url(from input: String) -> URL? {
        let s = input.trimmingCharacters(in: .whitespacesAndNewlines)
        if s.isEmpty { return nil }
        if s.range(of: "^[a-zA-Z][a-zA-Z0-9+.-]*:", options: .regularExpression) != nil,
           s.contains("://") || s.hasPrefix("about:") || s.hasPrefix("data:"),
           let u = URL(string: s) {
            return u
        }
        if !s.contains(" ") {
            let host = String(s.split(whereSeparator: { ":/?#".contains($0) }).first ?? "").lowercased()
            let local = LocalNet.isLocal(host: host)
            if local || host.contains("."), let u = URL(string: (local ? "http://" : "https://") + s) {
                return u
            }
        }
        return searchURL(s)
    }
}
