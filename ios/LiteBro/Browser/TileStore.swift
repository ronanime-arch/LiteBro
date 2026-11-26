import Foundation

/// A project tile: an address and its links (as on Windows; programs cannot be started on iOS)
struct Tile: Identifiable, Codable, Equatable {
    var id = UUID()
    var name: String
    var url: String
    var links: [TileLink] = []
    var color = 0

    static let maxLinks = 20
    static let palette = ["#6a43ed", "#3a5aeb", "#0e9f6e", "#e3a008", "#e02424", "#d61f69", "#0694a2", "#6b7280"]

    var letters: String {
        let words = name.split(separator: " ")
        let s = words.count > 1 ? words.prefix(2).compactMap(\.first).map(String.init).joined()
                                : String(name.prefix(2))
        return s.uppercased()
    }
}

struct TileLink: Identifiable, Codable, Equatable {
    var id = UUID()
    var name: String
    var url: String
}

final class TileStore: ObservableObject {
    static let shared = TileStore()

    @Published var tiles: [Tile] = [] { didSet { save() } }

    private let file: URL = {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("tiles.json")
    }()

    init() {
        if let data = try? Data(contentsOf: file), let list = try? JSONDecoder().decode([Tile].self, from: data) {
            tiles = list
        }
        addStarters()
    }

    /// Claude and ChatGPT once, for new and old installs; a removed one does not come back
    private func addStarters() {
        let d = UserDefaults.standard
        guard d.integer(forKey: "starterTiles") < 1 else { return }
        d.set(1, forKey: "starterTiles")
        let starters = [
            Tile(name: "Claude", url: "https://claude.ai/new",
                 links: [TileLink(name: "Claude Code", url: "https://claude.ai/code")], color: 3),
            Tile(name: "ChatGPT", url: "https://chatgpt.com/", color: 2),
        ]
        for t in starters where !tiles.contains(where: { URL(string: $0.url)?.host == URL(string: t.url)?.host }) {
            tiles.append(t)
        }
    }

    private func save() {
        if let data = try? JSONEncoder().encode(tiles) { try? data.write(to: file, options: .atomic) }
    }

    func upsert(_ t: Tile) {
        if let i = tiles.firstIndex(where: { $0.id == t.id }) { tiles[i] = t } else { tiles.append(t) }
    }

    func remove(_ t: Tile) { tiles.removeAll { $0.id == t.id } }

    func shift(_ t: Tile, by d: Int) {
        guard let i = tiles.firstIndex(where: { $0.id == t.id }) else { return }
        let j = i + d
        guard tiles.indices.contains(j) else { return }
        tiles.swapAt(i, j)
    }

    /// Adds the page to the folder of a tile, or takes it out when it is there already
    func toggle(_ url: String, name: String, in id: UUID) {
        guard let i = tiles.firstIndex(where: { $0.id == id }) else { return }
        if tiles[i].links.contains(where: { $0.url == url }) {
            tiles[i].links.removeAll { $0.url == url }
        } else if tiles[i].url != url, tiles[i].links.count < Tile.maxLinks {
            tiles[i].links.append(TileLink(name: name, url: url))
        }
    }

    func has(_ url: String, in t: Tile) -> Bool { t.url == url || t.links.contains { $0.url == url } }

    /// Is the address already saved (the star turns gold)
    func contains(_ url: URL?) -> Bool {
        guard let s = url?.absoluteString else { return false }
        return tiles.contains { $0.url == s || $0.links.contains { $0.url == s } }
    }
}
