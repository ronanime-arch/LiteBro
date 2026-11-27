import SwiftUI
import WebKit

enum Sheet: String, Identifiable {
    case tabs, media, downloads, settings, newTile, console, phone, debug
    var id: String { rawValue }
}

struct BrowserView: View {
    @EnvironmentObject var browser: Browser

    var body: some View {
        ZStack {
            if let tab = browser.current {
                TabScreen(tab: tab).id(tab.id)
            }
        }
        .fullScreenCover(isPresented: $browser.showTerminal) { TerminalHome(path: browser.terminalPath) }
    }
}

/// The current tab: address bar, page or tiles, toolbar
struct TabScreen: View {
    @EnvironmentObject var browser: Browser
    @EnvironmentObject var tiles: TileStore
    @EnvironmentObject var downloads: DownloadManager
    @ObservedObject var tab: BrowserTab
    @State private var sheet: Sheet?
    /// While typing, the toolbar steps aside: the page and the keyboard get the room
    @State private var typing = false

    var body: some View {
        VStack(spacing: 0) {
            AddressBar(tab: tab, sheet: $sheet)
            ZStack {
                if let u = tab.danger {
                    Notice(icon: "exclamationmark.octagon.fill", title: L("Опасный сайт"),
                           detail: (u.host ?? "") + "\n" + L("Сайт есть в списках вредоносных и фишинговых сайтов: он может украсть пароли и данные карт или заразить устройство."),
                           action: L("Назад"), tint: .red) {
                        tab.goBack()
                    } extra: {
                        Button(L("Всё равно открыть")) {
                            if let h = u.host?.lowercased() { browser.trusted.insert(h) }
                            tab.load(u)
                        }
                        .font(.footnote)
                    }
                } else if tab.showsHome || (tab.webView == nil && tab.blocked == nil) {
                    HomeView(tab: tab, sheet: $sheet)
                } else if let u = tab.blocked {
                    Notice(icon: "lock.shield", title: L("Заблокировано режимом «только localhost»"),
                           detail: u.absoluteString, action: L("Выключить режим")) {
                        browser.setLocalOnly(false)
                        tab.load(u)
                    }
                } else if let w = tab.webView {
                    WebContainer(webView: w)
                    if let f = tab.failure {
                        Notice(icon: tab.outOfMemory ? "memorychip" : "wifi.exclamationmark",
                               title: tab.outOfMemory ? L("Странице не хватило памяти телефона") : L("Сайт не отвечает"),
                               detail: (tab.url?.absoluteString ?? "") + "\n" + f, action: L("Попробовать ещё раз")) {
                            tab.reload()
                        }
                    }
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            OfferBar(tab: tab)
            if !typing { Toolbar(tab: tab, sheet: $sheet) }
        }
        .onReceive(NotificationCenter.default.publisher(for: UIResponder.keyboardWillShowNotification)) { _ in typing = true }
        .onReceive(NotificationCenter.default.publisher(for: UIResponder.keyboardWillHideNotification)) { _ in typing = false }
        .sheet(item: $sheet) { s in
            switch s {
            case .tabs: TabsView { sheet = nil }
            case .media: MediaSheet(tab: tab)
            case .downloads: DownloadsView()
            case .settings: SettingsView()
            case .console: ConsoleSheet(tab: tab)
            case .phone: PhoneAccessView()
            case .debug: NavigationStack { DebugLogView() }
            case .newTile:
                TileEditor(tile: Tile(name: tab.title.isEmpty ? (tab.url?.host ?? "") : tab.title,
                                      url: tab.url?.absoluteString ?? ""), isNew: true)
            }
        }
    }
}

struct WebContainer: UIViewRepresentable {
    let webView: WKWebView

    func makeUIView(context: Context) -> UIView {
        let v = UIView()
        attach(to: v)
        return v
    }

    func updateUIView(_ v: UIView, context: Context) {
        if webView.superview !== v {
            v.subviews.forEach { $0.removeFromSuperview() }
            attach(to: v)
        }
    }

    private func attach(to v: UIView) {
        webView.removeFromSuperview()
        webView.frame = v.bounds
        webView.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        v.addSubview(webView)
    }
}

extension Notice where Extra == EmptyView {
    init(icon: String, title: String, detail: String, action: String, run: @escaping () -> Void) {
        self.init(icon: icon, title: title, detail: detail, action: action, run: run, extra: { EmptyView() })
    }
}

/// The page that stands in for the engine's error pages
struct Notice<Extra: View>: View {
    let icon: String
    let title: String
    let detail: String
    let action: String
    var tint: Color = .accentColor
    let run: () -> Void
    @ViewBuilder var extra: () -> Extra

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: icon).font(.system(size: 44)).foregroundStyle(.secondary)
            Text(title).font(.title3.bold()).multilineTextAlignment(.center)
            Text(detail).font(.footnote).foregroundStyle(.secondary).multilineTextAlignment(.center).lineLimit(6)
            Button(action, action: run).buttonStyle(.borderedProminent).tint(tint)
            extra()
        }
        .padding(28)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(.systemBackground))
    }
}

struct AddressBar: View {
    @EnvironmentObject var browser: Browser
    @EnvironmentObject var tiles: TileStore
    @ObservedObject var tab: BrowserTab
    @Binding var sheet: Sheet?
    @State private var text = ""
    @FocusState private var editing: Bool

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                if browser.localOnly {
                    Image(systemName: "lock.shield.fill").foregroundStyle(.green)
                        .accessibilityLabel(L("Только localhost"))
                }
                if !editing, !tab.showsHome, let u = tab.url, ["http", "https", "file"].contains(u.scheme ?? "") {
                    StarMenu(url: u, title: tab.title, webView: tab.webView, sheet: $sheet).equatable()
                }
                TextField(L("Адрес или поиск"), text: $text)
                    .keyboardType(.webSearch)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .submitLabel(.go)
                    .focused($editing)
                    .onSubmit {
                        if let u = Prefs.url(from: text) { tab.load(u) }
                        editing = false
                    }
                if editing && !text.isEmpty {
                    Button { text = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary) }
                } else if !tab.showsHome {
                    Button { if tab.isLoading { tab.webView?.stopLoading() } else { tab.reload() } } label: {
                        Image(systemName: tab.isLoading ? "xmark" : "arrow.clockwise")
                    }
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 9)
            .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 12))
            .padding(.horizontal, 10)
            .padding(.vertical, 6)

            ProgressView(value: tab.isLoading ? max(tab.progress, 0.05) : 0)
                .progressViewStyle(.linear)
                .opacity(tab.isLoading ? 1 : 0)
                .frame(height: 2)
        }
        .onAppear { text = shown }
        .onChange(of: tab.url) { _ in if !editing { text = shown } }
        .onChange(of: tab.showsHome) { _ in if !editing { text = shown } }
        .onChange(of: editing) { on in
            if on { text = tab.showsHome ? "" : (tab.url?.absoluteString ?? "") } else { text = shown }
        }
    }

    /// Not editing: the address as is, so the port and the path of a local project stay visible
    private var shown: String { tab.showsHome ? "" : (tab.url?.absoluteString ?? "") }
}

struct Toolbar: View {
    @EnvironmentObject var browser: Browser
    @EnvironmentObject var tiles: TileStore
    @EnvironmentObject var downloads: DownloadManager
    @ObservedObject var tab: BrowserTab
    @Binding var sheet: Sheet?

    var body: some View {
        HStack {
            button("chevron.backward", L("Назад")) { tab.goBack() }
                .disabled(tab.showsHome && tab.webView?.url == nil)
            button("chevron.forward", L("Вперёд")) { tab.goForward() }
                .disabled(!tab.canGoForward || tab.showsHome)
            button("house", L("Плитки")) { tab.showsHome = true }
            Button { tab.rescan(); sheet = .media } label: {
                Image(systemName: "arrow.down.circle")
                    .overlay(alignment: .topTrailing) { badge(tab.media.count) }
                    .frame(maxWidth: .infinity, minHeight: 36)
            }
            .accessibilityLabel(L("Скачать видео"))
            Button { sheet = .tabs } label: {
                ZStack {
                    RoundedRectangle(cornerRadius: 4).stroke(lineWidth: 1.6).frame(width: 20, height: 20)
                    Text(verbatim: "\(min(browser.tabs.count, 99))").font(.system(size: 11, weight: .bold))
                }
                .frame(maxWidth: .infinity, minHeight: 36)
            }
            .accessibilityLabel(L("Вкладки"))
            MainMenu(tab: tab, url: tab.showsHome ? nil : tab.url, summary: downloads.summary, sheet: $sheet)
                .equatable()
        }
        .font(.system(size: 19))
        .padding(.horizontal, 6)
        .padding(.top, 4)
        .background(.bar)
    }

    private func button(_ icon: String, _ name: String, run: @escaping () -> Void) -> some View {
        Button(action: run) { Image(systemName: icon).frame(maxWidth: .infinity, minHeight: 36) }
            .accessibilityLabel(name)
    }

    @ViewBuilder private func badge(_ n: Int) -> some View {
        if n > 0 {
            Text(verbatim: "\(min(n, 99))")
                .font(.system(size: 10, weight: .bold)).foregroundStyle(.white)
                .padding(.horizontal, 4).frame(minWidth: 16, minHeight: 16)
                .background(Capsule().fill(.red))
                .offset(x: 9, y: -7)
        }
    }
}

/// The star at the left of the address: a new tile or this page into the folder of a tile.
/// Equatable on the address, so the page loading does not rebuild the open menu.
struct StarMenu: View, Equatable {
    @EnvironmentObject var tiles: TileStore
    let url: URL
    let title: String
    let webView: WKWebView?
    @Binding var sheet: Sheet?

    static func == (a: StarMenu, b: StarMenu) -> Bool { a.url == b.url && a.title == b.title && a.webView === b.webView }

    var body: some View {
        let s = url.absoluteString
        let saved = tiles.contains(url)
        Menu {
            Button { keep(); sheet = .newTile } label: { Label(L("Новая плитка"), systemImage: "plus.square") }
            if !tiles.tiles.isEmpty {
                Section(L("Добавить в папку")) {
                    ForEach(tiles.tiles) { t in
                        Button {
                            if !tiles.has(s, in: t) { keep() }
                            tiles.toggle(s, name: title.isEmpty ? (url.host ?? s) : title, in: t.id)
                        } label: {
                            if tiles.has(s, in: t) {
                                Label(t.name, systemImage: "checkmark")
                            } else {
                                Text(verbatim: t.name)
                            }
                        }
                        .disabled(t.url == s)
                    }
                }
            }
        } label: {
            // Gold when the address is already a tile or in a folder, as on Windows
            Image(systemName: saved ? "star.fill" : "star")
                .foregroundStyle(saved ? Color.yellow : Color.secondary)
        }
        .accessibilityLabel(L("Сохранить плиткой"))
    }

    private func keep() { Thumbs.shared.capture(webView, as: url.absoluteString) }
}

/// The "…" menu. Equatable on what it shows, so a download or a loading page does not rebuild it while
/// it is open (it blinked).
struct MainMenu: View, Equatable {
    @EnvironmentObject var browser: Browser
    @EnvironmentObject var tiles: TileStore
    let tab: BrowserTab
    let url: URL?
    let summary: String
    @Binding var sheet: Sheet?

    static func == (a: MainMenu, b: MainMenu) -> Bool { a.tab === b.tab && a.url == b.url && a.summary == b.summary }

    var body: some View {
        Menu {
            Button { browser.newTab() } label: { Label(L("Новая вкладка"), systemImage: "plus.square") }
            Button { sheet = .downloads } label: {
                Label(L("Загрузки"), systemImage: "tray.and.arrow.down")
                if !summary.isEmpty { Text(verbatim: summary) }
            }
            Button { browser.openTerminal() } label: { Label(L("Терминал ПК"), systemImage: "terminal") }
            Button { sheet = .phone } label: { Label(L("Доступ с ПК"), systemImage: "laptopcomputer.and.iphone") }
            if let u = url {
                Button { Thumbs.shared.capture(tab.webView, as: u.absoluteString); sheet = .newTile } label: {
                    Label(L("Сохранить плиткой"), systemImage: tiles.contains(u) ? "star.fill" : "star")
                }
                ShareButton(item: u)
                Button { UIPasteboard.general.string = u.absoluteString } label: {
                    Label(L("Копировать ссылку"), systemImage: "link")
                }
                Button { tab.webView?.findInteraction?.presentFindNavigator(showingReplace: false) } label: {
                    Label(L("Найти на странице"), systemImage: "doc.text.magnifyingglass")
                }
                Button { sheet = .console } label: {
                    Label(L("Консоль страницы"), systemImage: "chevron.left.forwardslash.chevron.right")
                }
                if SearchCountry.isSearch(u) { countryMenu(u) }
                if let t = Translate.url(for: u) {
                    Button { tab.load(t) } label: { Label(L("Перевести страницу"), systemImage: "character.bubble") }
                }
                if let c = Translate.chrome(u), UIApplication.shared.canOpenURL(c) {
                    Button { UIApplication.shared.open(c) } label: { Label(L("Открыть в Chrome"), systemImage: "globe") }
                }
                if browser.adBlock, AdBlock.site(of: u) != nil {
                    Toggle(isOn: Binding(get: { AdBlock.isOff(for: u) }, set: { _ in browser.toggleAdBlock(for: tab) })) {
                        Label(L("Не блокировать рекламу на этом сайте"), systemImage: "shield.slash")
                    }
                }
            }
            Button { sheet = .settings } label: { Label(L("Настройки"), systemImage: "gearshape") }
        } label: {
            Image(systemName: "ellipsis.circle")
                .overlay(alignment: .topTrailing) { DownloadsBadge() }
                .frame(maxWidth: .infinity, minHeight: 36)
        }
        .accessibilityLabel(L("Меню"))
    }

    /// Google results as seen from another country (gl and hl), the page reloads with them
    private func countryMenu(_ u: URL) -> some View {
        let now = SearchCountry.current
        return Menu {
            Button { pick(nil, u) } label: {
                if now == nil { Label(L("Как обычно"), systemImage: "checkmark") } else { Text(L("Как обычно")) }
            }
            ForEach(SearchCountry.all) { c in
                Button { pick(c, u) } label: {
                    if now == c { Label(c.name, systemImage: "checkmark") } else { Text(verbatim: c.name) }
                }
            }
        } label: {
            Label(L("Страна поиска") + (now.map { ": " + $0.code } ?? ""), systemImage: "globe.europe.africa")
        }
    }

    private func pick(_ c: SearchCountry.Country?, _ u: URL) {
        SearchCountry.current = c
        tab.load(SearchCountry.apply(c, to: u))
    }
}

/// Downloads running or finished and not looked at: the red number on "…"
struct DownloadsBadge: View {
    @ObservedObject private var downloads = DownloadManager.shared

    var body: some View {
        let n = downloads.items.filter { $0.state == .running || $0.state == .exporting }.count + downloads.unseen
        if n > 0 {
            Text(verbatim: "\(min(n, 99))")
                .font(.system(size: 10, weight: .bold)).foregroundStyle(.white)
                .padding(.horizontal, 4).frame(minWidth: 16, minHeight: 16)
                .background(Capsule().fill(.red))
                .offset(x: 9, y: -7)
        }
    }
}
