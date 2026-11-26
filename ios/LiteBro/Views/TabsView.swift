import SwiftUI

struct TabsView: View {
    @EnvironmentObject var browser: Browser
    @EnvironmentObject var tiles: TileStore
    let done: () -> Void
    @State private var saved = false

    var body: some View {
        NavigationStack {
            List {
                ForEach(browser.tabs) { tab in
                    TabRow(tab: tab, current: tab.id == browser.current?.id)
                        .contentShape(Rectangle())
                        .onTapGesture { browser.show(tab); done() }
                        .swipeActions { Button(role: .destructive) { browser.close(tab) } label: { Label(L("Закрыть"), systemImage: "xmark") } }
                }
                .onMove { browser.move(from: $0, to: $1) }
            }
            .navigationTitle(L("Вкладки"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .navigationBarLeading) { EditButton() }
                ToolbarItem(placement: .navigationBarTrailing) {
                    Button { browser.newTab(); done() } label: { Image(systemName: "plus") }
                        .accessibilityLabel(L("Новая вкладка"))
                }
                ToolbarItem(placement: .bottomBar) {
                    Button { saveAll() } label: {
                        Label(saved ? L("Сохранено в папку") : L("Сохранить все в папку"),
                              systemImage: saved ? "checkmark" : "folder.badge.plus")
                    }
                    .disabled(saved || pages.isEmpty)
                }
            }
        }
    }
}

extension TabsView {
    private var pages: [BrowserTab] {
        var seen = Set<String>()
        return browser.tabs.filter { t in
            guard !t.showsHome, let u = t.url, ["http", "https", "file"].contains(u.scheme ?? "") else { return false }
            return seen.insert(u.absoluteString).inserted
        }
    }

    /// All pages as one tile «Вкладки <date>», as on Windows: the first is its address, the rest its folder
    private func saveAll() {
        let list = Array(pages.prefix(1 + Tile.maxLinks))
        guard let first = list.first, let u = first.url else { return }
        let name = L("Вкладки") + " " + Date().formatted(date: .numeric, time: .shortened)
        let links = list.dropFirst().compactMap { t in
            t.url.map { TileLink(name: t.title.isEmpty ? ($0.host ?? $0.absoluteString) : t.title, url: $0.absoluteString) }
        }
        tiles.upsert(Tile(name: name, url: u.absoluteString, links: links, color: 6))
        for t in list { if let u = t.url { Thumbs.shared.capture(t.webView, as: u.absoluteString) } }
        saved = true
    }
}

private struct TabRow: View {
    @ObservedObject var tab: BrowserTab
    let current: Bool

    var body: some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: tab.displayTitle).lineLimit(1).fontWeight(current ? .semibold : .regular)
                if let u = tab.url, !tab.showsHome {
                    Text(verbatim: u.absoluteString).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            Spacer()
            if !tab.isLoaded && tab.url != nil {
                Image(systemName: "moon.zzz").foregroundStyle(.secondary).accessibilityLabel(L("Выгружена"))
            }
            if current { Image(systemName: "checkmark").foregroundStyle(.tint) }
        }
    }
}
