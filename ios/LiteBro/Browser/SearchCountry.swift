import Foundation

/// Google search as seen from another country, as on Windows: only the free gl (country) and hl (language)
/// parameters of the search address, no proxy.
enum SearchCountry {
    struct Country: Identifiable, Hashable {
        let code: String     // two letters, upper case; gl is its lower case
        let ru: String
        let hl: String
        var id: String { code }
        var name: String { L(ru) }
    }

    static let key = "searchCountry"   // empty: search as usual

    static let all: [Country] = [
        .init(code: "RU", ru: "Россия", hl: "ru"),
        .init(code: "UA", ru: "Украина", hl: "uk"),
        .init(code: "KZ", ru: "Казахстан", hl: "ru"),
        .init(code: "BY", ru: "Беларусь", hl: "ru"),
        .init(code: "GE", ru: "Грузия", hl: "ka"),
        .init(code: "US", ru: "США", hl: "en"),
        .init(code: "GB", ru: "Великобритания", hl: "en"),
        .init(code: "CA", ru: "Канада", hl: "en"),
        .init(code: "DE", ru: "Германия", hl: "de"),
        .init(code: "FR", ru: "Франция", hl: "fr"),
        .init(code: "ES", ru: "Испания", hl: "es"),
        .init(code: "IT", ru: "Италия", hl: "it"),
        .init(code: "PL", ru: "Польша", hl: "pl"),
        .init(code: "LT", ru: "Литва", hl: "lt"),
        .init(code: "CZ", ru: "Чехия", hl: "cs"),
        .init(code: "NL", ru: "Нидерланды", hl: "nl"),
        .init(code: "SE", ru: "Швеция", hl: "sv"),
        .init(code: "TR", ru: "Турция", hl: "tr"),
        .init(code: "IL", ru: "Израиль", hl: "iw"),
        .init(code: "IN", ru: "Индия", hl: "en"),
        .init(code: "CN", ru: "Китай", hl: "zh-CN"),
        .init(code: "JP", ru: "Япония", hl: "ja"),
        .init(code: "KR", ru: "Южная Корея", hl: "ko"),
        .init(code: "BR", ru: "Бразилия", hl: "pt-BR"),
        .init(code: "MX", ru: "Мексика", hl: "es"),
        .init(code: "AU", ru: "Австралия", hl: "en"),
    ]

    static var current: Country? {
        get {
            let c = UserDefaults.standard.string(forKey: key) ?? ""
            return all.first { $0.code == c }
        }
        set { UserDefaults.standard.set(newValue?.code ?? "", forKey: key) }
    }

    /// google.com, www.google.de, google.co.uk, www.google.com.ua
    static func isGoogle(_ u: URL?) -> Bool {
        guard let u, ["http", "https"].contains(u.scheme?.lowercased() ?? ""), let h = u.host?.lowercased() else { return false }
        return h.range(of: "^(www\\.)?google\\.(com?\\.)?[a-z]{2,3}$", options: .regularExpression) != nil
    }

    /// A page of Google search results
    static func isSearch(_ u: URL?) -> Bool { isGoogle(u) && u?.path == "/search" }

    /// The address with gl and hl of the country, or without them for nil; the rest stays as it is
    static func apply(_ c: Country?, to u: URL) -> URL {
        guard var comps = URLComponents(url: u, resolvingAgainstBaseURL: false) else { return u }
        var items = (comps.queryItems ?? []).filter { $0.name != "gl" && $0.name != "hl" }
        if let c {
            items.append(URLQueryItem(name: "gl", value: c.code.lowercased()))
            items.append(URLQueryItem(name: "hl", value: c.hl))
        }
        comps.queryItems = items.isEmpty ? nil : items
        return comps.url ?? u
    }

    /// A new search on Google lacks the picked country: the address to load instead, or nil when it is right
    static func fix(_ u: URL) -> URL? {
        guard let c = current, isSearch(u) else { return nil }
        let q = URLComponents(url: u, resolvingAgainstBaseURL: false)?.queryItems ?? []
        if q.first(where: { $0.name == "gl" })?.value == c.code.lowercased(),
           q.first(where: { $0.name == "hl" })?.value == c.hl { return nil }
        return apply(c, to: u)
    }
}
