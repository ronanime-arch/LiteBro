import AVFoundation
import SwiftUI

@main
struct LiteBroApp: App {
    @StateObject private var browser = Browser()
    @StateObject private var tiles = TileStore()
    @StateObject private var downloads = DownloadManager.shared
    @AppStorage(Prefs.themeKey) private var theme = "dark"

    init() {
        // Video keeps playing with the screen locked, like in Safari
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .moviePlayback)
    }

    var body: some Scene {
        WindowGroup {
            BrowserView()
                .environmentObject(browser)
                .environmentObject(tiles)
                .environmentObject(downloads)
                .preferredColorScheme(theme == "dark" ? .dark : theme == "light" ? .light : nil)
        }
    }
}
