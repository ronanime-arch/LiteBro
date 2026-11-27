import AVFoundation
import SwiftUI

@main
struct LiteBroApp: App {
    @StateObject private var browser = Browser()
    @StateObject private var tiles = TileStore.shared
    @StateObject private var downloads = DownloadManager.shared
    @AppStorage(Prefs.themeKey) private var theme = "dark"

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
                        askComputer(url)
                    } else {
                        browser.newTab(url)
                    }
                }
        }
    }

    /// A computer for the terminal from a litebro://ssh link: asked first, with the address and the key as they
    /// are. Such a link sets the key LiteBro will trust for that computer, and any app can send one.
    private func askComputer(_ url: URL) {
        guard let l = SSHStore.link(url) else { return }
        var text = "\(l.user)@\(l.host):\(l.port)"
        if let fp = l.fingerprint { text += "\n" + fp }
        if let old = SSHStore.shared.known(l)?.fingerprint, let fp = l.fingerprint, old != fp {
            text += "\n\n" + L("Запомненный ключ этого компьютера будет заменён. Соглашайтесь, только если сами сканируете код с его экрана.")
        }
        let a = UIAlertController(title: L("Добавить компьютер в терминал?"), message: text, preferredStyle: .alert)
        a.addAction(UIAlertAction(title: L("Отмена"), style: .cancel))
        a.addAction(UIAlertAction(title: L("Добавить"), style: .default) { _ in
            if let h = SSHStore.shared.add(from: url) { browser.openTerminal(at: h.id) }
        })
        var top = UIApplication.shared.connectedScenes.compactMap { ($0 as? UIWindowScene)?.keyWindow }.first?.rootViewController
        while let next = top?.presentedViewController { top = next }
        top?.present(a, animated: true)
    }
}
