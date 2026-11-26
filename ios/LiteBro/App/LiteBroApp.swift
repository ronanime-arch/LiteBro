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
                        if let h = SSHStore.shared.add(from: url) { browser.openTerminal(at: h.id) }
                    } else {
                        browser.newTab(url)
                    }
                }
        }
    }
}
