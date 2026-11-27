import UIKit
import WebKit

/// A tab: its own WKWebView, or none while unloaded (then only the address and the history are kept).
final class BrowserTab: NSObject, ObservableObject, Identifiable {
    let id = UUID()
    weak var browser: Browser?

    @Published var url: URL?
    @Published var title = ""
    @Published var progress = 0.0
    @Published var isLoading = false
    @Published var canGoBack = false
    @Published var canGoForward = false
    @Published var showsHome: Bool
    @Published var media: [MediaItem] = []
    /// A page the localhost-only mode did not let through
    @Published var blocked: URL?
    @Published var failure: String?
    /// The failure is the page's process that iOS or WebKit ended, not the network
    @Published var processEnd: ProcessEnd?
    enum ProcessEnd { case memory, crash }
    /// A page on the lists of malicious or phishing sites, not opened
    @Published var danger: URL?
    /// What the bar above the toolbar offers: logins, saving a password, a card
    @Published var offer: Offer?
    /// The page console (PageConsole), cleared with each new page
    @Published var console: [ConsoleLine] = []

    enum Offer {
        case login(host: String, frame: WKFrameInfo)
        case save(host: String, user: String, pass: String)
        case card(frame: WKFrameInfo)
    }
    @Published private(set) var webView: WKWebView?
    /// The name typed on the first page of a two-step sign-in, for saving the password of the next one
    var signingIn: (host: String, user: String, at: Date)?

    var lastShown = Date()
    /// The tab it was opened from: new tabs line up after it
    var openedFrom: UUID?
    /// The tab whose page opened this one (a link in a new tab, a pop-up): Back from its first page returns there.
    /// Not `openedFrom`: "open all" of a folder also lines its tabs up one after another.
    var opener: UUID?
    private var history: Any?          // WKWebView.interactionState of an unloaded tab
    private var processEnded: Date?    // when the page's process last ended
    /// The tiles are shown as the place before the first page (Back led there), not over a page
    @Published private(set) var homeIsStart = false
    /// A test tab from the computer (`Probe`): its web view is not swapped for a usual one behind the tester's back
    var keepsWebView = false
    private var watches: [NSKeyValueObservation] = []

    init(url: URL?, browser: Browser) {
        self.url = url
        self.browser = browser
        showsHome = url == nil
    }

    var displayTitle: String {
        if showsHome && webView?.url == nil { return L("Новая вкладка") }
        return title.isEmpty ? (url?.host ?? url?.absoluteString ?? L("Новая вкладка")) : title
    }

    var isLoaded: Bool { webView != nil }

    // MARK: Loading and unloading

    /// The web view of the tab, made (and the page restored) if the tab was unloaded
    @discardableResult
    func ensureWebView(load: Bool = true, adopting given: WKWebView? = nil) -> WKWebView {
        if let w = webView { return w }
        guard let browser else { fatalError("tab without browser") }
        Debug.step("web view for \(url?.host ?? "home")")
        let w = given ?? browser.makeWebView()
        w.navigationDelegate = self
        w.uiDelegate = self
        watches = [
            w.observe(\.title) { [weak self] w, _ in self?.title = w.title ?? "" },
            w.observe(\.url) { [weak self] w, _ in if let u = w.url { self?.url = u } },
            w.observe(\.estimatedProgress) { [weak self] w, _ in self?.progress = w.estimatedProgress },
            w.observe(\.isLoading) { [weak self] w, _ in self?.isLoading = w.isLoading },
            w.observe(\.canGoBack) { [weak self] w, _ in self?.canGoBack = w.canGoBack },
            w.observe(\.canGoForward) { [weak self] w, _ in self?.canGoForward = w.canGoForward },
        ]
        webView = w
        if let h = history {
            w.interactionState = h
            history = nil
        } else if load, given == nil, let u = url {
            w.load(URLRequest(url: u))
        }
        return w
    }

    func load(_ u: URL) {
        showsHome = false
        blocked = nil
        failure = nil
        danger = nil
        if Prefs.localOnly && !LocalNet.permits(u) {
            url = u
            blocked = u
            return
        }
        ensureWebView(load: false).load(URLRequest(url: u))
    }

    /// Frees the memory of a tab in the background; the page loads again when the tab is shown
    func unload() {
        guard let w = webView else { return }
        history = w.interactionState
        w.stopLoading()
        watches = []
        w.navigationDelegate = nil
        w.uiDelegate = nil
        w.removeFromSuperview()
        webView = nil
        isLoading = false
        progress = 0
    }

    func reload() {
        if let u = blocked ?? (failure != nil ? url : nil) {
            load(u)
        } else {
            webView?.reload()
        }
    }

    func goBack() {
        if danger != nil {
            danger = nil
            if webView?.url == nil { showsHome = true }
        } else if failure != nil || blocked != nil, let w = webView, let under = w.url {
            // A page that did not open left the one before it in place (a failed load is not in the
            // history): Back takes the notice away and shows that page, it does not go one more back
            failure = nil
            blocked = nil
            processEnd = nil
            url = under
        } else if showsHome, webView?.url != nil, !homeIsStart {
            // The tiles were opened over a page by their button: Back closes them
            showsHome = false
        } else if showsHome {
            // The tiles as the start of the history (or a new tab): nothing before them
        } else if let w = webView, w.canGoBack {
            blocked = nil
            failure = nil
            w.goBack()
        } else if !showsHome, let from = opener, let b = browser, b.tabs.contains(where: { $0.id == from }) {
            // A page a site opened in a new tab (target=_blank, a pop-up) has nowhere back but the tab it
            // came from: this one closes and that one is shown, as in Safari
            openedFrom = from
            b.close(self)
        } else {
            // Back from the first page: the tiles, as the place before it. Forward returns to the page.
            homeIsStart = true
            showsHome = true
        }
    }

    func goForward() {
        if showsHome {
            if homeIsStart, webView?.url != nil { showsHome = false }
            return
        }
        blocked = nil
        failure = nil
        webView?.goForward()
    }

    /// The tiles by their button, over the page: Back returns to it
    func showHome() {
        if showsHome { return }
        homeIsStart = false
        showsHome = true
    }

    var canGoBackNow: Bool { showsHome ? (webView?.url != nil && !homeIsStart) : true }
    var canGoForwardNow: Bool { showsHome ? (homeIsStart && webView?.url != nil) : canGoForward }

    // MARK: Media

    /// A stream the page loaded, or news about it (its duration, that it plays)
    func addMedia(_ u: URL, hls: Bool, title t: String?, duration: Double? = nil, playing: Bool = false,
                  mse: Bool = false) {
        if mse {
            // A MediaSource player: the stream with the same length is what plays
            guard let d = duration, d > 0 else { return }
            if let i = media.firstIndex(where: { abs(($0.duration ?? -100) - d) < 2 }) { markPlaying(i) }
            mseDuration = d
            return
        }
        let item = MediaItem(url: u, kind: MediaItem.kind(of: u, hls: hls),
                             title: (t?.isEmpty == false ? t! : title), page: webView?.url ?? url)
        if let i = media.firstIndex(where: { $0.key == item.key }) {
            if let d = duration, d > 0 { media[i].duration = d }
            if playing { markPlaying(i) }
            return
        }
        var new = item
        if let d = duration, d > 0 { new.duration = d }
        if masters.contains(where: { $0.value.contains(u) }) { new.variantOf = masters.first { $0.value.contains(u) }?.key }
        media.append(new)
        if playing { markPlaying(media.count - 1) }
        probe(new)
    }

    private func markPlaying(_ i: Int) {
        for j in media.indices { media[j].playing = j == i }
    }

    /// Variants of each master playlist on the page
    private var masters: [URL: Set<URL>] = [:]
    private var mseDuration: Double?

    private func update(_ id: UUID, _ change: (inout MediaItem) -> Void) {
        guard let i = media.firstIndex(where: { $0.id == id }) else { return }
        change(&media[i])
        if let d = mseDuration, let md = media[i].duration, abs(md - d) < 2 { markPlaying(i) }
    }

    /// Duration and quality from the playlist, size of a file: they tell the film from the ad
    private func probe(_ m: MediaItem) {
        let store = webView?.configuration.websiteDataStore.httpCookieStore
        let page = webView?.url
        let fetch: (URL, String) async -> (Data?, HTTPURLResponse?) = { u, method in
            var req = URLRequest(url: u, timeoutInterval: 15)
            req.httpMethod = method
            if let page { req.setValue(page.absoluteString, forHTTPHeaderField: "Referer") }
            if let store {
                let all: [HTTPCookie] = await withCheckedContinuation { c in
                    DispatchQueue.main.async { store.getAllCookies { c.resume(returning: $0) } }
                }
                let host = u.host?.lowercased() ?? ""
                let mine = all.filter { let d = $0.domain.lowercased().trimmingCharacters(in: CharacterSet(charactersIn: "."))
                                        return host == d || host.hasSuffix("." + d) }
                HTTPCookie.requestHeaderFields(with: mine).forEach { req.setValue($0.value, forHTTPHeaderField: $0.key) }
            }
            do {
                let (d, r) = try await URLSession.shared.data(for: req)
                return (d, r as? HTTPURLResponse)
            } catch {
                return (nil, nil)
            }
        }
        Task { [weak self] in
            if m.kind == .file {
                let (_, r) = await fetch(m.url, "HEAD")
                guard let len = r?.expectedContentLength, len > 0 else { return }
                await MainActor.run { self?.update(m.id) { $0.bytes = len } }
                return
            }
            let (data, _) = await fetch(m.url, "GET")
            guard let data, let text = String(data: data, encoding: .utf8) else { return }
            if text.contains("#EXT-X-STREAM-INF") {
                let list = HLS.variants(text, base: m.url)
                var sub: (Double?, Bool) = (nil, false)
                if let first = list.first?.url {
                    let (d2, _) = await fetch(first, "GET")
                    if let d2, let t2 = String(data: d2, encoding: .utf8) { sub = HLS.length(t2) }
                }
                let length = sub
                let variants = Set(list.map(\.url))
                let best = list.compactMap(\.height).max()
                await MainActor.run {
                    guard let self else { return }
                    self.masters[m.url] = variants
                    for j in self.media.indices where variants.contains(self.media[j].url) {
                        self.media[j].variantOf = m.url
                    }
                    self.update(m.id) {
                        if let b = best { $0.quality = L("до") + " \(b)p" }
                        if let d = length.0 { $0.duration = d }
                        $0.live = length.1
                    }
                }
            } else {
                let (d, live) = HLS.length(text)
                await MainActor.run { self?.update(m.id) { if let d { $0.duration = d }; $0.live = live } }
            }
        }
    }

    /// Looks for video again (lazy players add it late)
    func log(_ lines: [ConsoleLine]) {
        guard !lines.isEmpty else { return }
        console.append(contentsOf: lines)
        if console.count > PageConsole.maxLines { console.removeFirst(console.count - PageConsole.maxLines) }
    }

    /// Starts the console on the open page when it was not there from the start
    func startConsole() {
        webView?.evaluateJavaScript(PageConsole.source, in: nil, in: .page) { _ in }
    }

    func rescan() {
        webView?.evaluateJavaScript("window.__litebroMedia && window.__litebroMedia.scan()", completionHandler: nil)
    }
}

extension BrowserTab: WKNavigationDelegate {
    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        guard let u = action.request.url else { return decisionHandler(.allow) }
        if action.shouldPerformDownload { return decisionHandler(.download) }
        let scheme = u.scheme?.lowercased() ?? ""
        if !["http", "https", "about", "data", "blob", "file"].contains(scheme) {
            // tel:, mailto:, App Store and other apps: only after the person agrees. Without the sandbox
            // (TrollStore) a page could otherwise start any app's command by itself; TrollStore's own
            // "install this" (apple-magnifier://) and LiteBro's litebro://ssh never come from a page.
            decisionHandler(.cancel)
            if ["litebro", "apple-magnifier"].contains(scheme) { return }
            // The site is the frame the link is in, not the one it opens in (an ad frame may aim at the whole
            // page). `sourceFrame` is missing for a load the app itself began, though declared as always there.
            let source = action.value(forKey: "sourceFrame") as? WKFrameInfo
            askToLeave(u, from: source?.securityOrigin.host ?? action.targetFrame?.securityOrigin.host ?? "")
            return
        }
        if Prefs.localOnly && !LocalNet.permits(u) {
            if action.targetFrame?.isMainFrame ?? true { blocked = u }
            return decisionHandler(.cancel)
        }
        if Prefs.protect, let h = u.host?.lowercased(), browser?.trusted.contains(h) != true,
           FilterLists.isMalicious(h) {
            if action.targetFrame?.isMainFrame ?? true { danger = u }
            return decisionHandler(.cancel)
        }
        // A link to a site whose app is on the phone (PayPal, a bank) opens in that app, where the person is
        // signed in, as in Safari. iOS says yes only for a universal link of an installed app; otherwise the
        // page loads here as usual.
        if Prefs.openApps, scheme == "https", action.targetFrame?.isMainFrame ?? true, wantsApp(action, in: webView) {
            UIApplication.shared.open(u, options: [.universalLinksOnly: true]) { opened in
                if opened { Debug.write("link opened in its app: \(u.host ?? "")") }
                decisionHandler(opened ? .cancel : .allow)
            }
            return
        }
        // A new search from the Google page goes again with the picked country (back and forward stay as they are)
        if action.targetFrame?.isMainFrame == true, action.navigationType != .backForward,
           action.request.httpMethod?.uppercased() ?? "GET" == "GET", let fixed = SearchCountry.fix(u) {
            decisionHandler(.cancel)
            DispatchQueue.main.async { webView.load(URLRequest(url: fixed)) }
            return
        }
        decisionHandler(.allow)
    }

    /// Only a tap on a link to another site, or the first page of a window a tap opened (PayPal's checkout):
    /// never a typed address, a reload, back and forward, or a page the site itself goes to by a script
    private func wantsApp(_ action: WKNavigationAction, in webView: WKWebView) -> Bool {
        guard let to = action.request.url?.host?.lowercased() else { return false }
        switch action.navigationType {
        case .linkActivated:
            let sel = NSSelectorFromString("_isUserInitiated")
            if action.responds(to: sel), action.value(forKey: "_isUserInitiated") as? Bool == false { return false }
            guard let from = webView.url?.host?.lowercased() else { return true }
            return !(from == to || from.hasSuffix("." + to) || to.hasSuffix("." + from))
        case .other:
            return opener != nil && webView.backForwardList.currentItem == nil
        default:
            return false
        }
    }

    func webView(_ webView: WKWebView, decidePolicyFor response: WKNavigationResponse,
                 decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void) {
        let mime = response.response.mimeType?.lowercased() ?? ""
        if let u = response.response.url, mime.hasPrefix("video/") || mime.hasPrefix("audio/")
            || mime.contains("mpegurl") {
            addMedia(u, hls: mime.contains("mpegurl"), title: nil)
        }
        if !response.canShowMIMEType { return decisionHandler(.download) }
        decisionHandler(.allow)
    }

    func webView(_ webView: WKWebView, navigationAction: WKNavigationAction, didBecome download: WKDownload) {
        DownloadManager.shared.adopt(download)
    }

    func webView(_ webView: WKWebView, navigationResponse: WKNavigationResponse, didBecome download: WKDownload) {
        DownloadManager.shared.adopt(download)
    }

    func webView(_ webView: WKWebView, didStartProvisionalNavigation navigation: WKNavigation!) {
        blocked = nil
        failure = nil
        processEnd = nil
        danger = nil
        showsHome = false
    }

    func webView(_ webView: WKWebView, didCommit navigation: WKNavigation!) {
        media.removeAll()
        masters.removeAll()
        // "Save the password?" stays: a sign-in sends it and goes to the next page at once
        if case .save = offer {} else { offer = nil }
        mseDuration = nil
        console.removeAll()
        browser?.saveSession()
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        failed(error)
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        failed(error)
    }

    private func failed(_ error: Error) {
        let e = error as NSError
        // Cancelled, or turned into a download / handed to the player
        if e.domain == NSURLErrorDomain && e.code == NSURLErrorCancelled { return }
        if e.domain == "WebKitErrorDomain" && (e.code == 102 || e.code == 204) { return }
        if blocked != nil || danger != nil { return }
        if let failed = e.userInfo[NSURLErrorFailingURLErrorKey] as? URL { url = failed }
        failure = e.localizedDescription
    }

    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        pageProcessEnded(webView, reason: nil)
    }

    /// WebKit's private form of the call above, with the reason (`_WKProcessTerminationReason`: 0 the memory
    /// limit, 1 the CPU limit, 2 asked by the app, 3 a crash). WebKit calls this one instead when it is there.
    @objc(_webView:webContentProcessDidTerminateWithReason:)
    func webContentProcessDidTerminate(_ webView: WKWebView, reason: Int) {
        pageProcessEnded(webView, reason: reason)
    }

    private func pageProcessEnded(_ webView: WKWebView, reason: Int?) {
        let names = ["memory limit", "CPU limit", "asked by the app", "crash"]
        let why = reason.map { names.indices.contains($0) ? names[$0] : "reason \($0)" } ?? "reason unknown"
        Debug.write("page process ended (\(why)): \(webView.url?.host ?? "?"), \(Debug.memoryMB()) MB")
        // A hidden tab stays unloaded until it is shown: loading it again now would take the memory back
        guard browser?.current?.id == id || keepsWebView else { return unload() }
        // A page that ends its process again after the reload: the second time in a minute it stays down with
        // a message. Not counted while the app is left for another one: iOS ends pages then too.
        let now = Date()
        let active = UIApplication.shared.applicationState == .active
        if active, let last = processEnded, now.timeIntervalSince(last) < 60 {
            processEnded = nil
            // Only the memory limit is the memory; without the reason (an older WebKit) it is the likely one
            if reason == nil || reason == 0 {
                processEnd = .memory
                failure = L("Закройте другие вкладки и обновите страницу.")
            } else {
                processEnd = .crash
                failure = L("Страница закрывается снова после перезагрузки. Попробуйте ещё раз позже.")
            }
            return
        }
        processEnded = active ? now : nil
        webView.reload()
    }
}

extension BrowserTab: WKUIDelegate {
    /// window.open and target=_blank: a new tab right after this one.
    /// Without a tap WebKit does not even ask (javaScriptCanOpenWindowsAutomatically is off).
    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                 for action: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        browser?.openPopup(configuration: configuration, from: self)
    }

    func webViewDidClose(_ webView: WKWebView) {
        browser?.close(self)
    }

    func webView(_ webView: WKWebView, runJavaScriptAlertPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping () -> Void) {
        let a = UIAlertController(title: frame.request.url?.host, message: message, preferredStyle: .alert)
        a.addAction(UIAlertAction(title: "OK", style: .default) { _ in completionHandler() })
        present(a) { completionHandler() }
    }

    func webView(_ webView: WKWebView, runJavaScriptConfirmPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping (Bool) -> Void) {
        let a = UIAlertController(title: frame.request.url?.host, message: message, preferredStyle: .alert)
        a.addAction(UIAlertAction(title: L("Отмена"), style: .cancel) { _ in completionHandler(false) })
        a.addAction(UIAlertAction(title: "OK", style: .default) { _ in completionHandler(true) })
        present(a) { completionHandler(false) }
    }

    func webView(_ webView: WKWebView, runJavaScriptTextInputPanelWithPrompt prompt: String, defaultText: String?,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping (String?) -> Void) {
        let a = UIAlertController(title: frame.request.url?.host, message: prompt, preferredStyle: .alert)
        a.addTextField { $0.text = defaultText }
        a.addAction(UIAlertAction(title: L("Отмена"), style: .cancel) { _ in completionHandler(nil) })
        a.addAction(UIAlertAction(title: "OK", style: .default) { _ in completionHandler(a.textFields?.first?.text) })
        present(a) { completionHandler(nil) }
    }

    /// One question about another app at a time, in the whole browser: a page that asks in a loop does not
    /// pile them up. The question itself is watched, not a flag: one closed without a button (with the sheet
    /// under it) must not leave the links dead.
    private static weak var question: UIAlertController?

    /// Another app for this address? Asked every time, with the site that asks and the address as it is.
    /// Only the tab on the screen asks; while a question is up the next ones are dropped.
    fileprivate func askToLeave(_ u: URL, from host: String) {
        guard Self.question?.presentingViewController == nil, browser?.current?.id == id else { return }
        let text = u.absoluteString
        let site = host.isEmpty ? (url?.host ?? "") : host
        let a = UIAlertController(title: site.isEmpty ? L("Открыть в другом приложении?") : site + ": " + L("Открыть в другом приложении?"),
                                  message: text.count > 200 ? String(text.prefix(200)) + "…" : text, preferredStyle: .alert)
        a.addAction(UIAlertAction(title: L("Отмена"), style: .cancel))
        a.addAction(UIAlertAction(title: L("Открыть"), style: .default) { _ in UIApplication.shared.open(u) })
        Self.question = a
        present(a) {}
    }

    /// Shows a question over everything; `nobody` runs when there is nothing to show it on
    private func present(_ a: UIAlertController, nobody: () -> Void) {
        let root = UIApplication.shared.connectedScenes.compactMap { ($0 as? UIWindowScene)?.keyWindow }.first?.rootViewController
        var top = root
        while let next = top?.presentedViewController { top = next }
        guard let top else { return nobody() }
        top.present(a, animated: true)
    }
}

/// Just enough of the HLS playlist format to tell masters, variants and the length
enum HLS {
    struct Variant { let url: URL; let height: Int? }

    static func variants(_ text: String, base: URL) -> [Variant] {
        var out: [Variant] = []
        var height: Int?
        var expect = false
        for raw in text.split(whereSeparator: \.isNewline) {
            let l = raw.trimmingCharacters(in: .whitespaces)
            if l.hasPrefix("#EXT-X-STREAM-INF") {
                expect = true
                height = nil
                if let r = l.range(of: #"RESOLUTION=\d+x(\d+)"#, options: .regularExpression) {
                    height = Int(l[r].split(separator: "x").last ?? "")
                }
            } else if expect, !l.isEmpty, !l.hasPrefix("#") {
                if let u = URL(string: l, relativeTo: base)?.absoluteURL { out.append(Variant(url: u, height: height)) }
                expect = false
            }
        }
        // The best first
        return out.sorted { ($0.height ?? 0) > ($1.height ?? 0) }
    }

    /// Sum of the segments; a playlist without an end is a live stream
    static func length(_ text: String) -> (Double?, Bool) {
        var total = 0.0
        for raw in text.split(whereSeparator: \.isNewline) where raw.hasPrefix("#EXTINF:") {
            let v = raw.dropFirst(8).split(separator: ",").first.map(String.init) ?? ""
            total += Double(v.trimmingCharacters(in: .whitespaces)) ?? 0
        }
        return (total > 0 ? total : nil, !text.contains("#EXT-X-ENDLIST"))
    }
}
