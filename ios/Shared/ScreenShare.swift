import Foundation

/// What the app and the screen extension share: the secret of the screen address and until when it holds.
/// The app writes it after Face ID; the extension shows the screen only while it holds.
enum ScreenShare {
    static let group = "group.io.github.ronanime-arch.LiteBro"
    static let port = 8081
    private static var d: UserDefaults? { UserDefaults(suiteName: group) }

    static var token: String? {
        guard let t = d?.string(forKey: "screenToken"), let until = d?.object(forKey: "screenUntil") as? Date, until > Date() else { return nil }
        return t
    }

    static var until: Date? { d?.object(forKey: "screenUntil") as? Date }

    static func allow(minutes: Int) -> String {
        let t = MiniHTTP.token()
        d?.set(t, forKey: "screenToken")
        d?.set(Date().addingTimeInterval(TimeInterval(max(minutes, 1) * 60)), forKey: "screenUntil")
        return t
    }

    /// Taps from the computer too (Face ID in the app); goes with the secret
    static var control: Bool {
        get { token != nil && d?.bool(forKey: "screenControl") == true }
        set { d?.set(newValue, forKey: "screenControl") }
    }

    /// What the extension is doing, for the app: "on" while the screen goes out, else why it stopped
    static var state: (text: String, at: Date)? {
        guard let t = d?.string(forKey: "screenState"), let at = d?.object(forKey: "screenStateAt") as? Date else { return nil }
        return (t, at)
    }

    static func setState(_ text: String) {
        d?.set(text, forKey: "screenState")
        d?.set(Date(), forKey: "screenStateAt")
    }

    /// Does the app see the extension's settings at all (the App Group works with this signature)
    static var shared: Bool { d != nil && FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: group) != nil }

    static func revoke() {
        d?.removeObject(forKey: "screenControl")
        d?.removeObject(forKey: "screenToken")
        d?.removeObject(forKey: "screenUntil")
    }
}
