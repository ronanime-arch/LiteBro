import AVKit
import SwiftUI

struct DownloadsView: View {
    @EnvironmentObject var downloads: DownloadManager
    @Environment(\.dismiss) private var dismiss
    @State private var playing: Playing?

    var body: some View {
        NavigationStack {
            List {
                if downloads.items.isEmpty {
                    Text(L("Здесь будут скачанные видео и файлы. Они же — в приложении «Файлы»: На iPhone › LiteBro › Downloads."))
                        .foregroundStyle(.secondary)
                }
                ForEach(downloads.items) { item in
                    DownloadRow(item: item)
                        .contentShape(Rectangle())
                        .onTapGesture { if item.state == .done, item.isPlayable { playing = item.file.map(Playing.init) } }
                        .contextMenu { menu(item) }
                        .swipeActions {
                            Button(role: .destructive) { downloads.remove(item.id) } label: {
                                Label(item.state == .done ? L("Удалить") : L("Отменить"), systemImage: "trash")
                            }
                        }
                }
            }
            .navigationTitle(L("Загрузки"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button(L("Готово")) { dismiss() } } }
            .fullScreenCover(item: $playing) { p in PlayerView(url: p.url) }
        }
    }

    @ViewBuilder private func menu(_ item: DownloadItem) -> some View {
        if item.state == .done, let f = item.file {
            if item.isPlayable {
                Button { playing = Playing(url: f) } label: { Label(L("Смотреть"), systemImage: "play") }
            }
            ShareLink(item: f) { Label(L("Поделиться"), systemImage: "square.and.arrow.up") }
            if item.isVideo && f.pathExtension != "movpkg" {
                Button { downloads.saveToPhotos(item) } label: { Label(L("Сохранить в Фото"), systemImage: "photo") }
            }
        }
        if let s = item.source {
            Button { UIPasteboard.general.string = s.absoluteString } label: { Label(L("Копировать ссылку"), systemImage: "link") }
        }
        Button(role: .destructive) { downloads.remove(item.id) } label: {
            Label(item.state == .done ? L("Удалить") : L("Отменить"), systemImage: "trash")
        }
    }
}

/// A file for the player sheet
struct Playing: Identifiable {
    let url: URL
    var id: String { url.absoluteString }
}

private struct DownloadRow: View {
    let item: DownloadItem

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: item.isVideo ? "film" : item.isAudio ? "music.note" : "doc")
                .foregroundStyle(.secondary).frame(width: 26)
            VStack(alignment: .leading, spacing: 4) {
                Text(verbatim: item.name).lineLimit(2)
                switch item.state {
                case .running:
                    if item.progress >= 0 {
                        ProgressView(value: item.progress)
                    } else {
                        ProgressView().progressViewStyle(.linear)
                    }
                case .exporting:
                    Text(L("Собираю mp4…")).font(.caption).foregroundStyle(.secondary)
                case .done:
                    if let f = item.file, let size = try? f.resourceValues(forKeys: [.fileSizeKey]).fileSize {
                        Text(ByteCountFormatter.string(fromByteCount: Int64(size), countStyle: .file))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                case .failed(let why):
                    Text(why).font(.caption).foregroundStyle(.red).lineLimit(3)
                }
            }
        }
    }
}

struct PlayerView: View {
    let url: URL
    @Environment(\.dismiss) private var dismiss
    @State private var player: AVPlayer?

    var body: some View {
        ZStack(alignment: .topLeading) {
            Color.black.ignoresSafeArea()
            if let player { VideoPlayer(player: player).ignoresSafeArea() }
            Button { dismiss() } label: {
                Image(systemName: "xmark.circle.fill").font(.title).symbolRenderingMode(.hierarchical).foregroundStyle(.white)
            }
            .padding()
            .accessibilityLabel(L("Закрыть"))
        }
        .onAppear { player = AVPlayer(url: url); player?.play() }
        .onDisappear { player?.pause() }
    }
}
