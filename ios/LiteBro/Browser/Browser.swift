import UIKit
import WebKit

/// All tabs and what they share: one content controller (the media script, the localhost-only rules)
/// and the default website data store (cookies of logins).
final class Browser: NSObject, ObservableObject {
    @Published private(set) var tabs: [BrowserTab] = []
    @Published var currentID: UUID?
    /// The localhost-only mode, mirrored for the views
    @Published private(set) var localOnly = Prefs.localOnly
    @Published private(set) var adBlock = Prefs.adBlock

    private let content = WKUserContentController()
    private var timer: Timer?

    var current: BrowserTab? { tabs.first { $0.id == currentID } ?? tabs.first }

    override init() {
        super.init()
        content.add(WeakHandler(self), name: "litebroMedia")
        restoreSession()
        // The first page waits for the rules, so nothing leaks out in the localhost-only mode
        applyRules { [weak self] in
            if let self, let tab = self.current { self.show(tab) }
        }

        timer = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in self?.unloadIdle() }
        let nc = NotificationCenter.default
        nc.addObserver(forName: UIApplication.didReceiveMemoryWarningNotification, object: nil, queue: .main) { [weak self] _ in
            self?.unloadIdle(all: true)
        }
        nc.addObserver(forName: UIApplication.willResignActiveNotification, object: nil, queue: .main) { [weak self] _ in
            self?.saveSession()
        }
    }

    // MARK: Web views

    func makeWebView(configuration given: WKWebViewConfiguration? = nil) -> WKWebView {
        let c = given ?? {
            let c = WKWebViewConfiguration()
            c.userContentController = content
            c.allowsInlineMediaPlayback = true
            c.mediaTypesRequiringUserActionForPlayback = .audio
            c.allowsPictureInPictureMediaPlayback = true
            c.defaultWebpagePreferences.preferredContentMode = .mobile
            // No address checks sent out (Safe Browsing), as SmartScreen is off on Windows
            c.preferences.isFraudulentWebsiteWarningEnabled = false
            return c
        }()
        let w = WKWebView(frame: .zero, configuration: c)
        w.allowsBackForwardNavigationGestures = true
        w.isOpaque = false
        w.backgroundColor = .systemBackground
        if #available(iOS 16.4, *) { w.isInspectable = true }   // Safari › Develop on a Mac
        return w
    }

    // MARK: Tabs

    func show(_ tab: BrowserTab) {
        current?.lastShown = Date()
        currentID = tab.id
        tab.lastShown = Date()
        if !tab.showsHome || tab.url != nil, tab.blocked == nil { tab.ensureWebView() }
    }

    @discardableResult
    func newTab(_ url: URL? = nil, after from: BrowserTab? = nil, background: Bool = false) -> BrowserTab {
        let tab = BrowserTab(url: nil, browser: self)
        insert(tab, after: from)
        if let url { tab.load(url) }
        if !background { show(tab) }
        saveSession()
        return tab
    }

    /// A new tab goes right after the tab it came from, the next ones from it after those (as on Windows)
    private func insert(_ tab: BrowserTab, after from: BrowserTab?) {
        if let from, let i = tabs.firstIndex(where: { $0.id == from.id }) {
            var j = i + 1
            while j < tabs.count, tabs[j].openedFrom == from.id { j += 1 }
            tab.openedFrom = from.id
            tabs.insert(tab, at: j)
        } else {
            tabs.append(tab)
        }
    }

    func openPopup(configuration: WKWebViewConfiguration, from: BrowserTab) -> WKWebView {
        let tab = BrowserTab(url: nil, browser: self)
        tab.showsHome = false
        insert(tab, after: from)
        let w = tab.ensureWebView(load: false, adopting: makeWebView(configuration: configuration))
        show(tab)
        return w
    }

    func close(_ tab: BrowserTab) {
        tab.unload()
        tabs.removeAll { $0.id == tab.id }
        if tabs.isEmpty {
            // The last tab is replaced with the home page, as on Windows
            newTab()
        } else if currentID == tab.id {
            show(tabs.first(where: { $0.id == tab.openedFrom }) ?? tabs[tabs.count - 1])
        }
        saveSession()
    }

    func move(from source: IndexSet, to destination: Int) {
        tabs.move(fromOffsets: source, toOffset: destination)
        saveSession()
    }

    /// Background tabs past `unloadAfter` minutes give their memory back; not those playing sound
    private func unloadIdle(all: Bool = false) {
        let minutes = Prefs.unloadAfter
        if minutes == 0 && !all { return }
        let limit = Date().addingTimeInterval(-Double(minutes) * 60)
        for tab in tabs where tab.id != current?.id && tab.isLoaded && (all || tab.lastShown < limit) {
            tab.webView?.requestMediaPlaybackState { state in
                if state != .playing { tab.unload() }
            }
        }
    }

    // MARK: Localhost-only and the blocker

    func setLocalOnly(_ on: Bool) {
        UserDefaults.standard.set(on, forKey: Prefs.localOnlyKey)
        localOnly = on
        applyRules { [weak self] in
            // Pages that are already open lose their outside connections on reload
            if on { self?.tabs.filter(\.isLoaded).forEach { $0.reload() } }
        }
    }

    func setAdBlock(_ on: Bool) {
        UserDefaults.standard.set(on, forKey: Prefs.adBlockKey)
        adBlock = on
        applyRules { [weak self] in self?.current?.reload() }
    }

    /// "Do not block on this site", then the page loads again
    func toggleAdBlock(for tab: BrowserTab) {
        guard let site = AdBlock.site(of: tab.url) else { return }
        var off = Prefs.adBlockOff
        if AdBlock.isOff(for: tab.url) {
            let h = tab.url?.host?.lowercased() ?? site
            off.removeAll { h == $0 || h.hasSuffix("." + $0) }
        } else {
            off.append(site)
        }
        Prefs.adBlockOff = off
        objectWillChange.send()
        applyRules { tab.reload() }
    }

    /// The allowed sites changed
    func rulesChanged() { applyRules {} }

    /// Rules and page scripts for the current settings; open pages get them with the next load
    private func applyRules(_ done: @escaping () -> Void) {
        content.removeAllUserScripts()
        content.addUserScript(WKUserScript(source: MediaSniffer.source, injectionTime: .atDocumentStart,
                                           forMainFrameOnly: false))
        if Prefs.adBlock {
            content.addUserScript(WKUserScript(source: AdBlock.youTubeScript(except: Prefs.adBlockOff),
                                               injectionTime: .atDocumentEnd, forMainFrameOnly: false))
        }
        // Each list is compiled apart: "ignore-previous-rules" of one does not open the other
        let group = DispatchGroup()
        var lists: [WKContentRuleList] = []
        if Prefs.localOnly {
            group.enter()
            LocalNet.compile { l in if let l { lists.append(l) }; group.leave() }
        }
        if Prefs.adBlock {
            group.enter()
            AdBlock.compile { l in if let l { lists.append(l) }; group.leave() }
        }
        group.notify(queue: .main) { [weak self] in
            guard let self else { return }
            self.content.removeAllContentRuleLists()
            lists.forEach { self.content.add($0) }
            done()
        }
    }

    // MARK: Session

    func saveSession() {
        let list = tabs.map { $0.url?.absoluteString ?? "" }
        UserDefaults.standard.set(list, forKey: "tabs")
        UserDefaults.standard.set(tabs.firstIndex { $0.id == currentID } ?? 0, forKey: "currentTab")
    }

    private func restoreSession() {
        let list = UserDefaults.standard.stringArray(forKey: "tabs") ?? []
        tabs = list.map { BrowserTab(url: URL(string: $0), browser: self) }
        if tabs.isEmpty { tabs = [BrowserTab(url: nil, browser: self)] }
        let i = UserDefaults.standard.integer(forKey: "currentTab")
        currentID = tabs[min(max(i, 0), tabs.count - 1)].id
    }

    // MARK: Data

    func clearSiteData(_ done: @escaping () -> Void) {
        WKWebsiteDataStore.default().removeData(ofTypes: WKWebsiteDataStore.allWebsiteDataTypes(),
                                                modifiedSince: .distantPast, completionHandler: done)
        HTTPCookieStorage.shared.removeCookies(since: .distantPast)
    }
}

extension Browser: WKScriptMessageHandler {
    func userContentController(_ c: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let w = message.webView, let tab = tabs.first(where: { $0.webView === w }),
              let body = message.body as? [String: Any], let s = body["url"] as? String, let u = URL(string: s) else { return }
        tab.addMedia(u, hls: body["hls"] as? Bool ?? false, title: body["title"] as? String)
    }
}

/// WKUserContentController keeps its handlers strongly
private final class WeakHandler: NSObject, WKScriptMessageHandler {
    weak var target: WKScriptMessageHandler?
    init(_ t: WKScriptMessageHandler) { target = t }
    func userContentController(_ c: WKUserContentController, didReceive m: WKScriptMessage) {
        target?.userContentController(c, didReceive: m)
    }
}
