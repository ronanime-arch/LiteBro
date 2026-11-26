import SwiftUI

extension Color {
    init(hex: String) {
        var v: UInt64 = 0
        Scanner(string: hex.trimmingCharacters(in: CharacterSet(charactersIn: "#"))).scanHexInt64(&v)
        self.init(red: Double((v >> 16) & 0xff) / 255, green: Double((v >> 8) & 0xff) / 255, blue: Double(v & 0xff) / 255)
    }
}

/// The start page: project tiles and the localhost-only switch
struct HomeView: View {
    @EnvironmentObject var browser: Browser
    @EnvironmentObject var tiles: TileStore
    @ObservedObject var tab: BrowserTab
    @Binding var sheet: Sheet?
    @State private var editing: Tile?
    @State private var folder: Tile?

    private let columns = [GridItem(.adaptive(minimum: 96, maximum: 140), spacing: 16)]

    var body: some View {
        ScrollView {
            VStack(spacing: 22) {
                Text(verbatim: "LiteBro").font(.largeTitle.bold()).padding(.top, 28)
                SafeModeNotice(sheet: $sheet)
                LazyVGrid(columns: columns, spacing: 18) {
                    ForEach(tiles.tiles) { t in
                        TileView(tile: t)
                            // A tile with links is a folder: its pages with their pictures
                            .onTapGesture { if t.links.isEmpty { open(t.url, in: tab) } else { folder = t } }
                            .contextMenu { menu(t) }
                    }
                    Button { editing = Tile(name: "", url: "http://localhost:3000") } label: {
                        VStack(spacing: 6) {
                            RoundedRectangle(cornerRadius: 18).strokeBorder(style: StrokeStyle(lineWidth: 1.5, dash: [5]))
                                .foregroundStyle(.secondary)
                                .frame(width: 72, height: 72)
                                .overlay(Image(systemName: "plus").font(.title2).foregroundStyle(.secondary))
                            Text(L("Добавить")).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .buttonStyle(.plain)
                }
                .padding(.horizontal)

                if tiles.tiles.isEmpty {
                    Text(L("Плитка — это адрес проекта и его ссылки, например http://localhost:3000. Долгое нажатие на плитку — изменить, «Открыть всё»."))
                        .font(.footnote).foregroundStyle(.secondary).multilineTextAlignment(.center).padding(.horizontal, 30)
                }

                HStack(spacing: 12) {
                    Button { browser.openTerminal() } label: {
                        Label(L("Терминал ПК"), systemImage: "terminal")
                            .padding(.horizontal, 16).padding(.vertical, 10)
                            .background(Capsule().fill(Color(hex: "#6a43ed").opacity(0.25)))
                    }
                    Button { sheet = .phone } label: {
                        Label(L("Доступ с ПК"), systemImage: "laptopcomputer.and.iphone")
                            .padding(.horizontal, 16).padding(.vertical, 10)
                            .background(Capsule().fill(Color(.secondarySystemBackground)))
                    }
                }
                .buttonStyle(.plain)

                Button { browser.setLocalOnly(!browser.localOnly) } label: {
                    Label(browser.localOnly ? L("Только localhost: включено") : L("Только localhost: выключено"),
                          systemImage: browser.localOnly ? "lock.shield.fill" : "lock.shield")
                        .padding(.horizontal, 16).padding(.vertical, 10)
                        .background(Capsule().fill(browser.localOnly ? Color.green.opacity(0.25) : Color(.secondarySystemBackground)))
                }
                .buttonStyle(.plain)
                .padding(.bottom, 30)
            }
            .frame(maxWidth: .infinity)
        }
        .background(Color(.systemBackground))
        .sheet(item: $editing) { t in TileEditor(tile: t, isNew: !tiles.tiles.contains { $0.id == t.id }) }
        .sheet(item: $folder) { t in
            FolderView(tileID: t.id) { s in open(s, in: tab) } openAll: { openAll($0) }
        }
    }

    @ViewBuilder private func menu(_ t: Tile) -> some View {
        Button { open(t.url, in: tab) } label: { Label(L("Открыть"), systemImage: "arrow.up.right.square") }
        Button { if let u = Prefs.url(from: t.url) { browser.newTab(u, after: tab) } } label: {
            Label(L("Открыть в новой вкладке"), systemImage: "plus.square.on.square")
        }
        if !t.links.isEmpty {
            Section {
                ForEach(t.links) { l in
                    Button(l.name.isEmpty ? l.url : l.name) { open(l.url, in: tab) }
                }
                Button { openAll(t) } label: { Label(L("Открыть всё"), systemImage: "square.stack.3d.up") }
            }
        }
        Section {
            Button { editing = t } label: { Label(L("Изменить"), systemImage: "pencil") }
            Button { tiles.shift(t, by: -1) } label: { Label(L("Левее"), systemImage: "arrow.left") }
            Button { tiles.shift(t, by: 1) } label: { Label(L("Правее"), systemImage: "arrow.right") }
            Button(role: .destructive) { tiles.remove(t) } label: { Label(L("Удалить"), systemImage: "trash") }
        }
    }

    private func open(_ s: String, in tab: BrowserTab) {
        if let u = Prefs.url(from: s) { tab.load(u) }
    }

    /// The project in this tab, its links in background tabs (as on Windows)
    private func openAll(_ t: Tile) {
        var from = tab
        open(t.url, in: tab)
        for l in t.links {
            if let u = Prefs.url(from: l.url) { from = browser.newTab(u, after: from, background: true) }
        }
    }
}

struct TileView: View {
    let tile: Tile

    var body: some View {
        VStack(spacing: 6) {
            RoundedRectangle(cornerRadius: 18)
                .fill(Color(hex: Tile.palette[tile.color % Tile.palette.count]).gradient)
                .frame(width: 72, height: 72)
                .overlay(Text(verbatim: tile.letters).font(.title2.bold()).foregroundStyle(.white))
                .overlay(alignment: .topTrailing) {
                    if !tile.links.isEmpty {
                        Text(verbatim: "\(tile.links.count + 1)")
                            .font(.system(size: 11, weight: .bold)).foregroundStyle(.white)
                            .frame(minWidth: 18, minHeight: 18)
                            .background(Circle().fill(.black.opacity(0.45)))
                            .offset(x: 5, y: -5)
                    }
                }
            Text(verbatim: tile.name.isEmpty ? tile.url : tile.name)
                .font(.caption).lineLimit(1)
        }
        .contentShape(Rectangle())
    }
}

struct TileEditor: View {
    @EnvironmentObject var tiles: TileStore
    @Environment(\.dismiss) private var dismiss
    @State var tile: Tile
    let isNew: Bool

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField(L("Название"), text: $tile.name)
                    TextField(L("Адрес"), text: $tile.url)
                        .keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                }
                Section(L("Цвет")) {
                    HStack {
                        ForEach(Tile.palette.indices, id: \.self) { i in
                            Circle().fill(Color(hex: Tile.palette[i])).frame(width: 28, height: 28)
                                .overlay(Circle().stroke(.primary, lineWidth: tile.color == i ? 2 : 0))
                                .onTapGesture { tile.color = i }
                        }
                    }
                }
                Section {
                    ForEach($tile.links) { $l in
                        VStack(alignment: .leading) {
                            TextField(L("Название"), text: $l.name)
                            TextField(L("Адрес"), text: $l.url)
                                .keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                                .font(.footnote)
                        }
                    }
                    .onDelete { tile.links.remove(atOffsets: $0) }
                    if tile.links.count < Tile.maxLinks {
                        Button { tile.links.append(TileLink(name: "", url: "")) } label: {
                            Label(L("Добавить ссылку"), systemImage: "plus")
                        }
                    }
                } header: {
                    Text(L("Ссылки проекта"))
                } footer: {
                    Text(L("Например, документация бэкенда. «Открыть всё» открывает их в фоновых вкладках."))
                }
            }
            .navigationTitle(isNew ? L("Новая плитка") : L("Плитка"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button(L("Отмена")) { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button(L("Сохранить")) {
                        tile.links.removeAll { $0.url.trimmingCharacters(in: .whitespaces).isEmpty }
                        if tile.name.isEmpty { tile.name = URL(string: tile.url)?.host ?? tile.url }
                        tiles.upsert(tile)
                        dismiss()
                    }
                    .disabled(tile.url.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
        }
    }
}

/// The pages of a tile, each with its picture as it was when the tab was left
struct FolderView: View {
    @EnvironmentObject var tiles: TileStore
    @ObservedObject private var thumbs = Thumbs.shared
    @Environment(\.dismiss) private var dismiss
    let tileID: UUID
    let open: (String) -> Void
    let openAll: (Tile) -> Void

    private let columns = [GridItem(.adaptive(minimum: 150), spacing: 14)]

    var body: some View {
        NavigationStack {
            Group {
                if let t = tiles.tiles.first(where: { $0.id == tileID }) {
                    ScrollView {
                        LazyVGrid(columns: columns, spacing: 16) {
                            card(t.name.isEmpty ? t.url : t.name, t.url, color: t.color)
                            ForEach(t.links) { l in
                                card(l.name.isEmpty ? (URL(string: l.url)?.host ?? l.url) : l.name, l.url, color: t.color)
                                    .contextMenu {
                                        Button(role: .destructive) { tiles.toggle(l.url, name: l.name, in: t.id) } label: {
                                            Label(L("Убрать из папки"), systemImage: "trash")
                                        }
                                    }
                            }
                        }
                        .padding()
                    }
                    .navigationTitle(t.name)
                    .toolbar {
                        ToolbarItem(placement: .cancellationAction) { Button(L("Готово")) { dismiss() } }
                        ToolbarItem(placement: .primaryAction) {
                            Button { dismiss(); openAll(t) } label: { Label(L("Открыть всё"), systemImage: "square.stack.3d.up") }
                        }
                    }
                }
            }
            .navigationBarTitleDisplayMode(.inline)
        }
    }

    private func card(_ name: String, _ url: String, color: Int) -> some View {
        Button { dismiss(); open(url) } label: {
            VStack(alignment: .leading, spacing: 6) {
                ZStack {
                    RoundedRectangle(cornerRadius: 12).fill(Color(hex: Tile.palette[color % Tile.palette.count]).opacity(0.25))
                    if let img = thumbs.image(url) {
                        Image(uiImage: img).resizable().scaledToFill()
                            .frame(minWidth: 0, maxWidth: .infinity, minHeight: 0, maxHeight: .infinity, alignment: .top)
                    } else {
                        Image(systemName: "globe").font(.largeTitle).foregroundStyle(.secondary)
                    }
                }
                .frame(height: 190)
                .frame(maxWidth: .infinity)
                .clipShape(RoundedRectangle(cornerRadius: 12))
                .overlay(RoundedRectangle(cornerRadius: 12).stroke(Color(.separator), lineWidth: 0.5))
                .id(thumbs.version)
                Text(verbatim: name).font(.footnote.weight(.medium)).lineLimit(1)
                Text(verbatim: URL(string: url)?.host ?? url).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
            }
        }
        .buttonStyle(.plain)
    }
}
