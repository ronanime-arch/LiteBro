import Foundation
import WebKit

/// Full filter lists (EasyList, RU AdList, EasyPrivacy) turned into WebKit content rules on the phone,
/// and lists of malicious and phishing sites. Downloaded once, refreshed every 4 days, kept in
/// Application Support/filters. Until they are there, the small built-in list of `AdBlock` works.
enum FilterLists {
    struct Source { let name: String; let urls: [String] }

    static let ads = [
        Source(name: "easylist", urls: ["https://easylist.to/easylist/easylist.txt",
                                        "https://raw.githubusercontent.com/easylist/easylist/gh-pages/easylist.txt"]),
        Source(name: "ruadlist", urls: ["https://easylist-downloads.adblockplus.org/advblock.txt"]),
        Source(name: "easyprivacy", urls: ["https://easylist.to/easylist/easyprivacy.txt",
                                           "https://raw.githubusercontent.com/easylist/easylist/gh-pages/easyprivacy.txt"]),
    ]
    static let malware = [
        Source(name: "badware", urls: ["https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/badware.txt"]),
        Source(name: "urlhaus", urls: ["https://urlhaus.abuse.ch/downloads/hostfile/"]),
        Source(name: "phishing", urls: ["https://phishing.army/download/phishing_army_blocklist.txt"]),
    ]

    /// WebKit takes up to 50 000 rules in a list on older iOS: the rules go out in parts
    static let partSize = 45_000
    static let maxAge: TimeInterval = 4 * 24 * 3600
    /// Raised when the converter changes: rules built by an older one are made again from the lists
    static let converter = 2

    static let folder: URL = {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("filters", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()

    private static func file(_ name: String) -> URL { folder.appendingPathComponent(name) }

    // MARK: Download

    /// Downloads what is missing or old; `changed` is true when something new came
    static func refresh(_ sources: [Source], force: Bool = false) async -> Bool {
        var changed = false
        for s in sources {
            let dest = file(s.name + ".txt")
            let age = (try? dest.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate)
                .map { Date().timeIntervalSince($0) } ?? .infinity
            if !force && age < maxAge { continue }
            for u in s.urls {
                guard let url = URL(string: u),
                      let got = try? await URLSession.shared.data(from: url),
                      (got.1 as? HTTPURLResponse)?.statusCode == 200, got.0.count > 1000 else { continue }
                try? got.0.write(to: dest, options: .atomic)
                changed = true
                break
            }
        }
        return changed
    }

    static var updated: Date? {
        try? file("easylist.txt").resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate
    }

    // MARK: Ads → rules

    /// The rule parts (JSON arrays without the brackets) and the exceptions every part gets at its end
    struct Built { var parts: [String]; var exceptions: String; var count: Int }

    static func built() -> Built? {
        guard let meta = try? Data(contentsOf: file("ads-meta.json")),
              let m = try? JSONSerialization.jsonObject(with: meta) as? [String: Any],
              let n = m["parts"] as? Int, let count = m["count"] as? Int,
              m["converter"] as? Int == converter else { return nil }
        var parts: [String] = []
        for i in 0..<n {
            guard let s = try? String(contentsOf: file("ads-\(i).json")) else { return nil }
            parts.append(s)
        }
        let exc = (try? String(contentsOf: file("ads-exceptions.json"))) ?? ""
        return Built(parts: parts, exceptions: exc, count: count)
    }

    /// Converts the downloaded lists; returns false when there is nothing to convert
    @discardableResult
    static func build() -> Bool {
        var lines: [Substring] = []
        var texts: [String] = []
        for s in ads {
            if let t = try? String(contentsOf: file(s.name + ".txt")) { texts.append(t) }
        }
        if texts.isEmpty { return false }
        for t in texts { lines.append(contentsOf: t.split(whereSeparator: \.isNewline)) }
        let c = Converter()
        for l in lines { c.add(l) }
        c.finish()
        let rules = c.blocks + c.cosmetic
        var parts: [String] = []
        var i = 0
        while i < rules.count {
            let part = Array(rules[i..<min(i + partSize, rules.count)])
            parts.append(json(part))
            i += partSize
        }
        for (n, p) in parts.enumerated() { try? p.write(to: file("ads-\(n).json"), atomically: true, encoding: .utf8) }
        try? json(c.exceptions).write(to: file("ads-exceptions.json"), atomically: true, encoding: .utf8)
        let meta: [String: Any] = ["parts": parts.count, "count": rules.count + c.exceptions.count, "converter": converter]
        try? JSONSerialization.data(withJSONObject: meta).write(to: file("ads-meta.json"))
        return true
    }

    /// A JSON array of rules without its brackets, so parts and exceptions join as text
    static func json(_ rules: [[String: Any]]) -> String {
        guard !rules.isEmpty, let d = try? JSONSerialization.data(withJSONObject: rules) else { return "" }
        let s = String(decoding: d, as: UTF8.self)
        return String(s.dropFirst().dropLast())
    }

    // MARK: Malicious sites

    private static var badHosts: Set<String>?
    private static let lock = NSLock()

    static func loadMalware() {
        var set = Set<String>()
        for s in malware {
            guard let t = try? String(contentsOf: file(s.name + ".txt")) else { continue }
            for raw in t.split(whereSeparator: \.isNewline) {
                var l = raw.trimmingCharacters(in: .whitespaces)
                if l.isEmpty || l.hasPrefix("#") || l.hasPrefix("!") { continue }
                if l.hasPrefix("||") {
                    // uBlock badware: ||host^ or ||host^$doc,…
                    l.removeFirst(2)
                    guard let end = l.firstIndex(of: "^") else { continue }
                    let rest = l[l.index(after: end)...]
                    guard rest.isEmpty || rest.hasPrefix("$") else { continue }
                    l = String(l[..<end])
                } else if let sp = l.firstIndex(where: { $0 == " " || $0 == "\t" }) {
                    // hosts file: 127.0.0.1 host
                    l = l[sp...].trimmingCharacters(in: .whitespaces)
                }
                let h = l.lowercased()
                if h.contains("."), h.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "." || $0 == "-") }) {
                    set.insert(h)
                }
            }
        }
        lock.lock(); badHosts = set; lock.unlock()
    }

    /// Is the host (or a domain above it) on a list of malicious or phishing sites
    static func isMalicious(_ host: String?) -> Bool {
        guard var h = host?.lowercased() else { return false }
        lock.lock(); let set = badHosts; lock.unlock()
        guard let set, !set.isEmpty else { return false }
        while true {
            if set.contains(h) { return true }
            guard let dot = h.firstIndex(of: ".") else { return false }
            h = String(h[h.index(after: dot)...])
            if !h.contains(".") { return false }
        }
    }
}

/// Adblock Plus syntax → WebKit content rules. Only what WebKit can do exactly: address rules with
/// types, third-party and domains; `@@` exceptions; element hiding with plain CSS selectors and its
/// exceptions (`$generichide`, `#@#`): without them generic hiding broke chatgpt.com, claude.ai,
/// Google sign-in and the Cloudflare check, which the lists exempt exactly this way.
/// Anything else (scriptlets, :has-text, redirects, regular expressions) is skipped.
private final class Converter {
    var blocks: [[String: Any]] = []
    var cosmetic: [[String: Any]] = []
    var exceptions: [[String: Any]] = []
    private var generic: [String] = []
    /// Sites where generic hiding is off (`@@||site^$generichide`)
    private var noGeneric = Set<String>()
    /// A selector with the sites where it must not hide (`site#@#selector`)
    private var unhide: [String: [String]] = [:]
    private var specific: [String: [String]] = [:]

    private static let types: [String: String] = [
        "script": "script", "image": "image", "stylesheet": "style-sheet", "font": "font", "media": "media",
        "xmlhttprequest": "raw", "websocket": "raw", "other": "raw", "ping": "raw", "popup": "popup",
        "subdocument": "document",
    ]
    private static let selectorChars = Set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 #.-_[]=\"'^$*~>+,|")
    private static let domainChars = Set("abcdefghijklmnopqrstuvwxyz0123456789.-")

    func add(_ raw: Substring) {
        let l = raw.trimmingCharacters(in: .whitespaces)
        guard let first = l.first, first != "!", first != "[" else { return }
        for bad in ["#?#", "#$#", "#%#", "$$"] where l.contains(bad) { return }
        if let r = l.range(of: "#@#") {
            let s = String(l[r.upperBound...])
            let ds = l[..<r.lowerBound].split(separator: ",").map(String.init)
                .filter { d in !d.hasSuffix(".") && d.allSatisfy { Self.domainChars.contains($0) } }
            if !s.isEmpty && !ds.isEmpty { unhide[s, default: []].append(contentsOf: ds) }
            return
        }
        if let r = l.range(of: "##") {
            hide(domains: String(l[..<r.lowerBound]), selector: String(l[r.upperBound...]))
            return
        }
        network(l)
    }

    private func hide(domains: String, selector s: String) {
        guard !s.isEmpty, !s.hasPrefix("+js"), !s.hasPrefix("^"), !s.contains(":"),
              s.allSatisfy({ Self.selectorChars.contains($0) }) else { return }
        if domains.isEmpty { generic.append(s); return }
        let ds = domains.split(separator: ",").map(String.init)
        if ds.contains(where: { $0.hasPrefix("~") }) { return }
        let ok = ds.filter { d in !d.hasSuffix(".") && d.allSatisfy { Self.domainChars.contains($0) } }.sorted()
        if ok.isEmpty { return }
        specific[ok.joined(separator: ","), default: []].append(s)
    }

    /// Generic hiding in groups of 500, except on the exempt sites; a selector with its own
    /// exceptions goes alone. Exceptions come anywhere in the lists, so this waits for the last line.
    private func finishGeneric() {
        let off = noGeneric.sorted().map { "*" + $0 }
        func rule(_ selectors: [String], unless: [String]) {
            var t: [String: Any] = ["url-filter": ".*"]
            if !unless.isEmpty { t["unless-domain"] = unless }
            cosmetic.append(["trigger": t, "action": ["type": "css-display-none", "selector": selectors.joined(separator: ", ")]])
        }
        var group: [String] = []
        var seen = Set<String>()
        for s in generic where seen.insert(s).inserted {
            if let ds = unhide[s] {
                rule([s], unless: Array(Set(off + ds.map { "*" + $0 })).sorted())
                continue
            }
            group.append(s)
            if group.count == 500 { rule(group, unless: off); group.removeAll() }
        }
        if !group.isEmpty { rule(group, unless: off) }
        generic.removeAll()
    }

    /// After the last line: the cosmetic rules collected by domain
    func finish() {
        finishGeneric()
        for (ds, ss) in specific {
            cosmetic.append(["trigger": ["url-filter": ".*", "if-domain": ds.split(separator: ",").map { "*" + $0 }],
                             "action": ["type": "css-display-none", "selector": ss.joined(separator: ", ")]])
        }
    }

    private func network(_ line: String) {
        var l = line
        let exception = l.hasPrefix("@@")
        if exception { l.removeFirst(2) }
        if l.count > 2 && l.hasPrefix("/") && l.hasSuffix("/") { return }   // regular expression
        var options = ""
        if let d = l.lastIndex(of: "$") {
            options = String(l[l.index(after: d)...])
            l = String(l[..<d])
        }
        var trigger: [String: Any] = [:]
        var resource: [String] = []
        for o in options.split(separator: ",").map(String.init) {
            let name = o.split(separator: "=", maxSplits: 1).first.map(String.init) ?? o
            switch name {
            case "document" where exception:
                // The whole site without blocking
                if l.hasPrefix("||") {
                    var host = String(l.dropFirst(2))
                    if host.hasSuffix("^") { host.removeLast() }
                    if !host.isEmpty, host.allSatisfy({ Self.domainChars.contains($0) }) {
                        exceptions.append(["trigger": ["url-filter": ".*", "if-domain": ["*" + host]],
                                           "action": ["type": "ignore-previous-rules"]])
                    }
                }
                return
            case "generichide", "ghide", "elemhide", "ehide":
                // The site without generic hiding (its own rules stay)
                if exception, l.hasPrefix("||") {
                    var host = String(l.dropFirst(2))
                    if host.hasSuffix("^") { host.removeLast() }
                    if !host.isEmpty, host.allSatisfy({ Self.domainChars.contains($0) }) { noGeneric.insert(host) }
                }
                return
            case "third-party", "3p": trigger["load-type"] = ["third-party"]
            case "~third-party", "1p", "first-party": trigger["load-type"] = ["first-party"]
            case "match-case": trigger["url-filter-is-case-sensitive"] = true
            case "domain":
                let ds = o.dropFirst(7).split(separator: "|").map(String.init)
                let pos = ds.filter { !$0.hasPrefix("~") }
                let neg = ds.filter { $0.hasPrefix("~") }.map { String($0.dropFirst()) }
                if !pos.isEmpty && !neg.isEmpty { return }
                let list = pos.isEmpty ? neg : pos
                if list.isEmpty || !list.allSatisfy({ d in d.allSatisfy { Self.domainChars.contains($0) } }) { return }
                trigger[pos.isEmpty ? "unless-domain" : "if-domain"] = list.map { "*" + $0 }
            default:
                guard let t = Self.types[o] else { return }   // an option WebKit cannot do
                if !resource.contains(t) { resource.append(t) }
            }
        }
        // Frames of the site itself stay: "document" would also stop the page
        if resource.contains("document") && (trigger["load-type"] as? [String]) != ["third-party"] { return }
        if !resource.isEmpty { trigger["resource-type"] = resource }
        guard let filter = Self.regex(l) else { return }
        trigger["url-filter"] = filter
        if exception {
            exceptions.append(["trigger": trigger, "action": ["type": "ignore-previous-rules"]])
        } else {
            blocks.append(["trigger": trigger, "action": ["type": "block"]])
        }
    }

    /// An address pattern as a WebKit regular expression (no `|`, `{}`, `\d`; `^` and `$` only at the ends)
    static func regex(_ pattern: String) -> String? {
        var p = Substring(pattern)
        var prefix = ""
        if p.hasPrefix("||") { prefix = "^[a-z]+://([^/:]*\\.)?"; p = p.dropFirst(2) }
        else if p.hasPrefix("|") { prefix = "^"; p = p.dropFirst() }
        var end = ""
        if p.hasSuffix("|") { end = "$"; p = p.dropLast() }
        if p.isEmpty { return nil }
        var out = ""
        for ch in p {
            guard let a = ch.asciiValue, a > 32, a < 127 else { return nil }
            switch ch {
            case "{", "}", "(", ")", "[", "]", "\\", "|", "$": return nil
            case "*": out += ".*"
            case "^": out += "[^a-zA-Z0-9_.%-]"
            case ".", "+", "?": out += "\\" + String(ch)
            default: out.append(ch)
            }
        }
        while out.hasPrefix(".*") { out.removeFirst(2) }
        while out.hasSuffix(".*") { out.removeLast(2) }
        if out.isEmpty || out.hasPrefix("+") || out.hasPrefix("?") { return nil }
        // Too short without an anchor would block half the internet
        if prefix.isEmpty && out.replacingOccurrences(of: ".*", with: "").count < 4 { return nil }
        return prefix + out + end
    }
}
