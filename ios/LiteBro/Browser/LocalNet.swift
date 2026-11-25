import Foundation
import WebKit

/// The localhost-only mode: this device, private networks and the allowed sites.
/// WebKit content rules block every other request (websockets, workers and iframes too).
enum LocalNet {
    static func isLocal(host raw: String) -> Bool {
        let h = raw.lowercased().trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
        if h == "localhost" || h.hasSuffix(".localhost") || h.hasSuffix(".local") || h == "::1" { return true }
        let parts = h.split(separator: ".", omittingEmptySubsequences: false)
        if parts.count == 4 {
            let p = parts.compactMap { Int($0) }
            if p.count == 4 {
                switch (p[0], p[1]) {
                case (127, _), (10, _), (192, 168), (169, 254), (172, 16...31): return true
                default: return false
                }
            }
        }
        if h.contains(":") {
            return h.hasPrefix("fe80:") || h.hasPrefix("fc") || h.hasPrefix("fd")
        }
        return false
    }

    static func isAllowed(_ host: String) -> Bool {
        let h = host.lowercased()
        return Prefs.allowHosts.contains { rule in
            let base = rule.hasPrefix("*.") ? String(rule.dropFirst(2)) : rule
            return h == base || (rule.hasPrefix("*.") && h.hasSuffix("." + base))
        }
    }

    /// May a page go to this address in the localhost-only mode
    static func permits(_ url: URL) -> Bool {
        switch url.scheme?.lowercased() {
        case "about", "data", "blob", "file": return true
        case "http", "https", "ws", "wss":
            guard let h = url.host, url.user == nil else { return false }
            return isLocal(host: h) || isAllowed(h)
        default: return false
        }
    }

    /// Content rules: block everything, then let local addresses through.
    /// WebKit filters have no `|` and no `{n}`, so one rule per pattern.
    static func rules() -> String {
        var r: [[String: Any]] = [["trigger": ["url-filter": ".*"], "action": ["type": "block"]]]
        func pass(_ f: String) { r.append(["trigger": ["url-filter": f], "action": ["type": "ignore-previous-rules"]]) }
        ["^about:", "^data:", "^blob:", "^file:"].forEach(pass)
        var hosts = ["localhost", "[^/:@]+\\.localhost", "[^/:@]+\\.local",
                     "127\\.[0-9.]+", "10\\.[0-9.]+", "192\\.168\\.[0-9.]+", "169\\.254\\.[0-9.]+",
                     "172\\.1[6-9]\\.[0-9.]+", "172\\.2[0-9]\\.[0-9.]+", "172\\.3[01]\\.[0-9.]+",
                     "\\[::1\\]", "\\[f[cd][0-9a-f:]*\\]", "\\[fe80:[0-9a-f:%a-z]*\\]"]
        for rule in Prefs.allowHosts {
            let sub = rule.hasPrefix("*.")
            let base = NSRegularExpression.escapedPattern(for: sub ? String(rule.dropFirst(2)) : rule)
            hosts.append(base)
            if sub { hosts.append("[^/:@]+\\." + base) }
        }
        for h in hosts { pass("^[a-z]+://" + h + "[:/]") }
        // An address with a user name could pretend to be local: "http://localhost:x@site.com/"
        r.append(["trigger": ["url-filter": "^[a-z]+://[^/]*@"], "action": ["type": "block"]])
        let data = try! JSONSerialization.data(withJSONObject: r)
        return String(decoding: data, as: UTF8.self)
    }

    static func compile(_ done: @escaping (WKContentRuleList?) -> Void) {
        let json = rules()
        RuleStore.load([(RuleStore.id("localOnly", json), { json })], prefix: "localOnly") { done($0.first) }
    }
}
