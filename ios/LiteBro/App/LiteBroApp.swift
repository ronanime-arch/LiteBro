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
                        // Asked first: such a link sets the key LiteBro will trust for that computer,
                        // and any app can send one
                        askComputer(url)
                    } else {
                        browser.newTab(url)
                    }
                }
        }
    }

    /// The question on the screen; a link that comes meanwhile is dropped, so the answer is about what is shown
    private static weak var question: UIAlertController?

    private func askComputer(_ url: URL, tries: Int = 20) {
        guard SSHStore.link(url) != nil, Self.question?.presentingViewController == nil else { return }
        var top = UIApplication.shared.connectedScenes.compactMap { ($0 as? UIWindowScene)?.keyWindow }.first?.rootViewController
        while let next = top?.presentedViewController, !next.isBeingDismissed { top = next }
        // Opened by the link itself: the window is not there yet
        guard let top, top.viewIfLoaded?.window != nil else {
            if tries > 0 { DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { askComputer(url, tries: tries - 1) } }
            return
        }
        let a = UIAlertController(title: L("Добавить компьютер в терминал?"), message: Self.computerText(url), preferredStyle: .alert)
        a.addAction(UIAlertAction(title: L("Отмена"), style: .cancel))
        a.addAction(UIAlertAction(title: L("Добавить"), style: .default) { _ in
            if let h = SSHStore.shared.add(from: url) { browser.openTerminal(at: h.id) }
        })
        Self.question = a
        top.present(a, animated: true)
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
