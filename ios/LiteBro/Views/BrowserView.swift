import SwiftUI
import WebKit

enum Sheet: String, Identifiable {
    case tabs, media, downloads, settings, newTile
    var id: String { rawValue }
}

struct BrowserView: View {
    @EnvironmentObject var browser: Browser

    var body: some View {
        if let tab = browser.current {
            TabScreen(tab: tab).id(tab.id)
        }
    }
}

/// The current tab: address bar, page or tiles, toolbar
struct TabScreen: View {
    @EnvironmentObject var browser: Browser
    @EnvironmentObject var tiles: TileStore
    @EnvironmentObject var downloads: DownloadManager
    @ObservedObject var tab: BrowserTab
    @State private var sheet: Sheet?

    var body: some View {
        VStack(spacing: 0) {
            AddressBar(tab: tab)
            ZStack {
                if tab.showsHome || (tab.webView == nil && tab.blocked == nil) {
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
                        Notice(icon: "wifi.exclamationmark", title: L("Сайт не отвечает"),
                               detail: (tab.url?.absoluteString ?? "") + "\n" + f, action: L("Попробовать ещё раз")) {
                            tab.reload()
                        }
                    }
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            Toolbar(tab: tab, sheet: $sheet)
        }
        .sheet(item: $sheet) { s in
            switch s {
            case .tabs: TabsView { sheet = nil }
            case .media: MediaSheet(tab: tab)
            case .downloads: DownloadsView()
            case .settings: SettingsView()
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

/// The page that stands in for the engine's error pages
struct Notice: View {
    let icon: String
    let title: String
    let detail: String
    let action: String
    let run: () -> Void

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: icon).font(.system(size: 44)).foregroundStyle(.secondary)
            Text(title).font(.title3.bold()).multilineTextAlignment(.center)
            Text(detail).font(.footnote).foregroundStyle(.secondary).multilineTextAlignment(.center).lineLimit(6)
            Button(action, action: run).buttonStyle(.borderedProminent)
        }
        .padding(28)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(.systemBackground))
    }
}

struct AddressBar: View {
    @EnvironmentObject var browser: Browser
    @ObservedObject var tab: BrowserTab
    @State private var text = ""
    @FocusState private var editing: Bool

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                if browser.localOnly {
                    Image(systemName: "lock.shield.fill").foregroundStyle(.green)
                        .accessibilityLabel(L("Только localhost"))
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
            Menu {
                Button { browser.newTab() } label: { Label(L("Новая вкладка"), systemImage: "plus.square") }
                Button { sheet = .downloads } label: { Label(L("Загрузки"), systemImage: "tray.and.arrow.down") }
                if !tab.showsHome, let u = tab.url {
                    Button { sheet = .newTile } label: {
                        Label(L("Сохранить плиткой"), systemImage: tiles.contains(u) ? "star.fill" : "star")
                    }
                    ShareLink(item: u) { Label(L("Поделиться"), systemImage: "square.and.arrow.up") }
                    Button { UIPasteboard.general.string = u.absoluteString } label: {
                        Label(L("Копировать ссылку"), systemImage: "link")
                    }
                }
                if browser.adBlock, AdBlock.site(of: tab.url) != nil, !tab.showsHome {
                    Toggle(isOn: Binding(get: { AdBlock.isOff(for: tab.url) }, set: { _ in browser.toggleAdBlock(for: tab) })) {
                        Label(L("Не блокировать рекламу на этом сайте"), systemImage: "shield.slash")
                    }
                }
                Toggle(isOn: Binding(get: { browser.localOnly }, set: { browser.setLocalOnly($0) })) {
                    Label(L("Только localhost"), systemImage: "lock.shield")
                }
                Button { sheet = .settings } label: { Label(L("Настройки"), systemImage: "gearshape") }
            } label: {
                Image(systemName: "ellipsis.circle")
                    .overlay(alignment: .topTrailing) { badge(downloads.items.filter { $0.state == .running || $0.state == .exporting }.count) }
                    .frame(maxWidth: .infinity, minHeight: 36)
            }
            .accessibilityLabel(L("Меню"))
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
