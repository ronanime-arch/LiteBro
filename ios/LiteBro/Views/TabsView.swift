import SwiftUI

struct TabsView: View {
    @EnvironmentObject var browser: Browser
    let done: () -> Void

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
            }
        }
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
