import UIKit
import WebKit

/// Test commands from the computer through the files address, `POST /<secret>/_remote/<name>` with JSON in and out:
/// `state` (the current tab), `open` {url, newTab?}, `js` {code} (the body of an async function in the page's
/// world, its `return` comes back), `click` {x, y} (a UIKit control at 0…1 of the window). So the computer
/// tests sites on the phone without taps. Also `probe` {url, parts?, features?} (a test tab, see `Probe`),
/// `features` (WebKit's switches by name) and `closeOthers` (only the tab on the screen stays). Only with «Управление касаниями» on (checked by `FileShare`).
enum Remote {
    static func run(_ name: String, _ m: [String: Any], done: @escaping (Int, String) -> Void) {
        DispatchQueue.main.async {
            switch name {
            case "state":
                done(200, json(state()))
            case "open":
                guard let s = m["url"] as? String, let u = URL(string: s), u.scheme != nil, let b = Browser.live
                else { return done(400, json(["error": "url"])) }
                if let tab = b.current, m["newTab"] as? Bool != true { tab.load(u) } else { _ = b.newTab(u) }
                done(200, json(state()))
            case "js":
                guard let code = m["code"] as? String else { return done(400, json(["error": "code"])) }
                guard let w = Browser.live?.current?.webView else { return done(409, json(["error": "no page"])) }
                // Answered once: by the page, or after the timeout when its promise never settles
                var answered = false
                let answer = { (status: Int, body: String) in
                    if answered { return }
                    answered = true
                    done(status, body)
                }
                let seconds = (m["timeout"] as? Double) ?? 20
                DispatchQueue.main.asyncAfter(deadline: .now() + seconds) { answer(504, json(["error": "timeout"])) }
                w.callAsyncJavaScript(code, arguments: [:], in: nil, in: .page) { r in
                    switch r {
                    case .success(let v): answer(200, json(["result": plain(v)]))
                    case .failure(let e): answer(200, json(["error": String(describing: e)]))
                    }
                }
            case "probe":
                // A test tab: WebKit's default web view plus the named parts of LiteBro's (see `Probe`)
                guard let s = m["url"] as? String, let u = URL(string: s), u.scheme != nil, let b = Browser.live
                else { return done(400, json(["error": "url"])) }
                let parts = Set(m["parts"] as? [String] ?? [])
                let tab = b.probeTab(u, Probe.webView(b, parts: parts, features: m["features"] as? [String: Bool] ?? [:]))
                done(200, json(["tabs": b.tabs.count, "parts": parts.sorted(), "url": tab.url?.absoluteString ?? ""]))
            case "features":
                done(200, json(Probe.features(in: Browser.live?.current?.webView?.configuration.preferences ?? WKPreferences())))
            case "closeOthers":
                guard let b = Browser.live else { return done(409, json(["error": "no browser"])) }
                b.closeOthers()
                done(200, json(state()))
            case "click":
                guard let x = m["x"] as? Double, let y = m["y"] as? Double else { return done(400, json(["error": "x, y"])) }
                done(200, Touch.click(x: min(max(x, 0), 1), y: min(max(y, 0), 1)))
            default:
                done(404, json(["error": "unknown command"]))
            }
        }
    }

    /// What the current tab shows
    private static func state() -> [String: Any] {
        guard let b = Browser.live else { return ["error": "no browser"] }
        let t = b.current
        return ["url": t?.url?.absoluteString ?? "", "title": t?.title ?? "", "loading": t?.isLoading ?? false,
                "tabs": b.tabs.count, "page": t?.webView != nil]
    }

    /// A value of the page as JSON can carry it (dates, nodes and the like become text)
    private static func plain(_ v: Any) -> Any {
        JSONSerialization.isValidJSONObject(["v": v]) ? v : String(describing: v)
    }

    private static func json(_ o: Any) -> String {
        guard JSONSerialization.isValidJSONObject(o),
              let d = try? JSONSerialization.data(withJSONObject: o, options: [.fragmentsAllowed]) else { return "{}" }
        return String(decoding: d, as: UTF8.self)
    }

    /// Whether TrollStore, and not the system Magnifier, opens this `apple-magnifier://` command: both may own
    /// the scheme. LaunchServices' list of the apps for a URL, private API; unknown means no.
    static func trollStoreTakes(_ url: URL) -> Bool {
        guard let cls = NSClassFromString("LSApplicationWorkspace") as? NSObject.Type,
              let ws = cls.value(forKey: "defaultWorkspace") as? NSObject else { return false }
        let sel = NSSelectorFromString("applicationsAvailableForOpeningURL:")
        guard ws.responds(to: sel),
              let apps = ws.perform(sel, with: url)?.takeUnretainedValue() as? [NSObject] else { return false }
        let ids = apps.compactMap { $0.value(forKey: "bundleIdentifier") as? String }
        // The first is the one iOS opens
        return ids.first?.lowercased().contains("trollstore") ?? false
    }
}
