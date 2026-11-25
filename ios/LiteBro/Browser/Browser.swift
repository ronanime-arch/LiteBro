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

    /// What the filter lists are doing now, for the settings
    @Published var listsState: String?
    private var listsBusy = false

    let content = WKUserContentController()
    private var timer: Timer?

    var current: BrowserTab? { tabs.first { $0.id == currentID } ?? tabs.first }

    override init() {
        super.init()
        content.add(WeakHandler(self), name: "litebroMedia")
        content.add(WeakHandler(self), contentWorld: .defaultClient, name: "litebroVault")
        content.add(WeakHandler(self), contentWorld: .page, name: PageConsole.handler)
        restoreSession()
        // The first page waits for the rules, so nothing leaks out in the localhost-only mode
        if Prefs.protect { DispatchQueue.global(qos: .utility).async { FilterLists.loadMalware() } }
        applyRules { [weak self] in
            if let self, let tab = self.current { self.show(tab) }
            self?.refreshLists()
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
            // Safe Browsing of WebKit: hashed address prefixes are checked through Apple
            c.preferences.isFraudulentWebsiteWarningEnabled = Prefs.protect
            return c
        }()
        let w = WKWebView(frame: .zero, configuration: c)
        w.allowsBackForwardNavigationGestures = true
        w.isFindInteractionEnabled = true
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
        if on { refreshLists() }
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
        content.addUserScript(WKUserScript(source: VaultScript.source, injectionTime: .atDocumentEnd,
                                           forMainFrameOnly: false, in: .defaultClient))
        if Prefs.console {
            content.addUserScript(WKUserScript(source: PageConsole.source, injectionTime: .atDocumentStart,
                                               forMainFrameOnly: true))
        }
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
            AdBlock.compile { ls in lists.append(contentsOf: ls); group.leave() }
        }
        group.notify(queue: .main) { [weak self] in
            guard let self else { return }
            self.content.removeAllContentRuleLists()
            lists.forEach { self.content.add($0) }
            done()
        }
    }

    /// Downloads the full lists when they are missing or old, then applies them
    func refreshLists(force: Bool = false) {
        guard !listsBusy else { return }
        let ads = Prefs.adBlock, protect = Prefs.protect
        guard ads || protect else { return }
        listsBusy = true
        listsState = L("Загружаю списки…")
        Task.detached(priority: .utility) { [weak self] in
            if protect {
                _ = await FilterLists.refresh(FilterLists.malware, force: force)
                FilterLists.loadMalware()
            }
            var rebuilt = false
            if ads {
                let fresh = await FilterLists.refresh(FilterLists.ads, force: force)
                if fresh || FilterLists.built() == nil {
                    await MainActor.run { self?.listsState = L("Готовлю правила…") }
                    rebuilt = FilterLists.build()
                }
            }
            let didRebuild = rebuilt
            await MainActor.run {
                guard let self else { return }
                self.listsBusy = false
                self.listsState = nil
                if didRebuild { self.applyRules {} }
                self.objectWillChange.send()
            }
        }
    }

    func setProtect(_ on: Bool) {
        UserDefaults.standard.set(on, forKey: Prefs.protectKey)
        objectWillChange.send()
        if on { refreshLists() }
    }

    /// The page console from the start of every page, not only after it is opened
    func setConsole(_ on: Bool) {
        UserDefaults.standard.set(on, forKey: Prefs.consoleKey)
        objectWillChange.send()
        applyRules {}
    }

    /// A malicious site the user chose to open anyway, until the app quits
    var trusted: Set<String> = []

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
        if message.name == "litebroVault" { return vault(message) }
        if message.name == PageConsole.handler { return consoleLines(message) }
        guard let w = message.webView, let tab = tabs.first(where: { $0.webView === w }),
              let body = message.body as? [String: Any], let s = body["url"] as? String, let u = URL(string: s) else { return }
        tab.addMedia(u, hls: body["hls"] as? Bool ?? false, title: body["title"] as? String,
                     duration: body["duration"] as? Double, playing: body["playing"] as? Bool ?? false,
                     mse: body["mse"] as? Bool ?? false)
    }
}

extension Browser {
    /// From the password script: forms on the page and passwords sent. The site is the origin of
    /// the frame (never what the page says), and only https or local pages get anything filled.
    fileprivate func vault(_ message: WKScriptMessage) {
        guard let w = message.webView, let tab = tabs.first(where: { $0.webView === w }),
              let body = message.body as? [String: Any], let type = body["type"] as? String else { return }
        let origin = message.frameInfo.securityOrigin
        let host = origin.host.lowercased()
        guard origin.protocol == "https" || LocalNet.isLocal(host: host), !host.isEmpty else { return }
        let vault = Vault.shared
        switch type {
        case "login":
            if !vault.logins(for: host).isEmpty { tab.offer = .login(host: host, frame: message.frameInfo) }
        case "card":
            if !vault.cards.isEmpty && origin.protocol == "https" { tab.offer = .card(frame: message.frameInfo) }
        case "save":
            guard let pass = body["pass"] as? String, !pass.isEmpty else { return }
            let user = body["user"] as? String ?? ""
            if Prefs.vaultNever.contains(host) || vault.isSaved(host: host, user: user, pass: pass) { return }
            tab.offer = .save(host: host, user: user, pass: pass)
        default: break
        }
    }
}

extension Browser {
    /// Lines from the console script of the main frame
    fileprivate func consoleLines(_ message: WKScriptMessage) {
        guard message.frameInfo.isMainFrame, let w = message.webView,
              let tab = tabs.first(where: { $0.webView === w }),
              let body = message.body as? [String: Any], let lines = body["lines"] as? [[String: Any]] else { return }
        tab.log(lines.compactMap { l in
            guard let t = l["t"] as? String else { return nil }
            return ConsoleLine(kind: ConsoleLine.Kind(rawValue: l["k"] as? String ?? "") ?? .log, text: t)
        })
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
