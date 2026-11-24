import SwiftUI

/// What can be downloaded from the page of the tab
struct MediaSheet: View {
    @EnvironmentObject var downloads: DownloadManager
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var tab: BrowserTab
    @State private var youtube: [MediaItem] = []
    @State private var youtubeError: String?
    @State private var asking = false
    @State private var started: Set<URL> = []

    private var videoID: String? { YouTube.videoID(tab.url) }

    var body: some View {
        NavigationStack {
            List {
                if let id = videoID {
                    Section {
                        if asking {
                            HStack { ProgressView(); Text(L("Спрашиваю YouTube…")).foregroundStyle(.secondary) }
                        } else if let e = youtubeError {
                            Text(e).foregroundStyle(.secondary)
                            Button(L("Попробовать ещё раз")) { ask(id) }
                        }
                        ForEach(youtube) { row($0) }
                    } header: {
                        Text(verbatim: "YouTube")
                    }
                    .task(id: id) { ask(id) }
                }
                Section {
                    if tab.media.isEmpty {
                        Text(L("Видео на странице не найдено. Запустите его в плеере и откройте это окно снова."))
                            .foregroundStyle(.secondary)
                    }
                    ForEach(Array(tab.media.reversed())) { row($0) }
                } header: {
                    Text(L("На странице"))
                } footer: {
                    Text(L("Находятся файлы (mp4, webm, mp3…) и потоки HLS (m3u8): HLS сохраняется в mp4 в лучшем качестве. Видео с защитой DRM (Netflix, Apple TV и т. п.) скачать нельзя."))
                }
            }
            .navigationTitle(L("Скачать видео"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .navigationBarLeading) {
                    Button { tab.rescan() } label: { Image(systemName: "arrow.clockwise") }
                        .accessibilityLabel(L("Искать снова"))
                }
                ToolbarItem(placement: .confirmationAction) { Button(L("Готово")) { dismiss() } }
            }
        }
        .presentationDetents([.medium, .large])
    }

    private func row(_ m: MediaItem) -> some View {
        HStack {
            Image(systemName: m.kind == .hls ? "dot.radiowaves.left.and.right" : "film")
                .foregroundStyle(.secondary).frame(width: 26)
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: m.note.isEmpty ? m.label : m.note).lineLimit(2)
                Text(verbatim: m.url.host ?? "").font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if started.contains(m.url) {
                Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
            } else {
                Button {
                    guard let store = tab.webView?.configuration.websiteDataStore.httpCookieStore else { return }
                    downloads.start(m, cookies: store)
                    started.insert(m.url)
                } label: {
                    Image(systemName: "arrow.down.circle.fill").font(.title2)
                }
                .buttonStyle(.borderless)
                .accessibilityLabel(L("Скачать"))
            }
        }
        .contextMenu {
            Button { UIPasteboard.general.string = m.url.absoluteString } label: { Label(L("Копировать ссылку"), systemImage: "link") }
        }
    }

    private func ask(_ id: String) {
        asking = true
        youtubeError = nil
        Task {
            do {
                let found = try await YouTube.streams(id, page: tab.url)
                await MainActor.run { youtube = found; asking = false }
            } catch {
                await MainActor.run { youtubeError = error.localizedDescription; asking = false }
            }
        }
    }
}
