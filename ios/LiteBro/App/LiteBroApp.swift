import AVFoundation
import SwiftUI

@main
struct LiteBroApp: App {
    @StateObject private var browser = Browser()
    @StateObject private var tiles = TileStore.shared
    @StateObject private var downloads = DownloadManager.shared
    @AppStorage(Prefs.themeKey) private var theme = "dark"
    /// A litebro://ssh link that waits for the person's answer
    @State private var computer: URL?

    init() {
        // Before anything else: the launch log, crash traces and the safe mode
        Debug.begin()
        Debug.step("app init")
        // Video keeps playing with the screen locked, like in Safari
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .moviePlayback)
    }

    var body: some Scene {
        WindowGroup {
            BrowserView()
                .onAppear { Debug.step("first screen"); Debug.launched() }
                .environmentObject(browser)
                .environmentObject(tiles)
                .environmentObject(downloads)
                .preferredColorScheme(theme == "dark" ? .dark : theme == "light" ? .light : nil)
                // A link from another app: a new tab, as on Windows; the QR code of the terminal: that computer
                .onOpenURL { url in
                    if url.scheme?.lowercased() == "litebro" {
                        // Asked first: such a link sets the key LiteBro will trust for that computer,
                        // and any app can send one
                        if SSHStore.link(url) != nil { computer = url }
                    } else {
                        browser.newTab(url)
                    }
                }
                .alert(L("Добавить компьютер в терминал?"),
                       isPresented: Binding(get: { computer != nil }, set: { if !$0 { computer = nil } }),
                       presenting: computer) { url in
                    Button(L("Отмена"), role: .cancel) {}
                    Button(L("Добавить")) {
                        if let h = SSHStore.shared.add(from: url) { browser.openTerminal(at: h.id) }
                    }
                } message: { url in
                    Text(verbatim: Self.computerText(url))
                }
        }
    }

    /// The address, the user and the key of a litebro://ssh link, each on a line of its own
    private static func computerText(_ url: URL) -> String {
        guard let l = SSHStore.link(url) else { return "" }
        var text = "\(l.host):\(l.port)\n" + L("Пользователь") + ": \(l.user)"
        if let fp = l.fingerprint { text += "\n" + fp }
        if let old = SSHStore.shared.known(l)?.fingerprint, let fp = l.fingerprint, old != fp {
            text += "\n\n" + L("Запомненный ключ этого компьютера будет заменён. Соглашайтесь, только если сами сканируете код с его экрана.")
        }
        return text
    }
}
