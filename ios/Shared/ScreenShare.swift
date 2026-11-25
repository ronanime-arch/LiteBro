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

    static func revoke() {
        d?.removeObject(forKey: "screenToken")
        d?.removeObject(forKey: "screenUntil")
    }
}
