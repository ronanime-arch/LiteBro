import UIKit
import WebKit

/// Test web views for the computer (`Remote`, command `probe`): a tab whose web view is WebKit's own default
/// plus only the named parts of LiteBro's setup, and WebKit's feature switches by name. So what makes a page
/// misbehave in LiteBro and not in Safari is found on the phone itself, part by part, without a build for each guess.
enum Probe {
    /// The parts of `Browser.makeWebView` that can be added one by one
    static let parts = ["controller", "transparent", "agent", "media", "fraud", "gestures"]

    static func webView(_ browser: Browser, parts: Set<String>, features: [String: Bool]) -> WKWebView {
        let c = WKWebViewConfiguration()
        if parts.contains("controller") { c.userContentController = browser.content }
        if parts.contains("media") {
            c.allowsInlineMediaPlayback = true
            c.mediaTypesRequiringUserActionForPlayback = .audio
            c.allowsPictureInPictureMediaPlayback = true
            c.defaultWebpagePreferences.preferredContentMode = .mobile
        }
        if parts.contains("agent") {
            let v = ProcessInfo.processInfo.operatingSystemVersion
            c.applicationNameForUserAgent = "Version/\(v.majorVersion).\(v.minorVersion) Mobile/15E148 Safari/604.1"
        }
        if parts.contains("fraud") { c.preferences.isFraudulentWebsiteWarningEnabled = Prefs.protect }
        for (key, on) in features { set(key, on, in: c.preferences) }
        let w = WKWebView(frame: .zero, configuration: c)
        if parts.contains("gestures") {
            w.allowsBackForwardNavigationGestures = true
            w.isFindInteractionEnabled = true
        }
        if parts.contains("transparent") {
            w.isOpaque = false
            w.backgroundColor = .systemBackground
        }
        return w
    }

    // MARK: WebKit's feature switches (private API of WKPreferences, as Safari's Develop menu uses)

    private static let lists = ["_experimentalFeatures", "_internalDebugFeatures", "_features"]

    /// Every switch WebKit names, with its state in `p`
    static func features(in p: WKPreferences = WKPreferences()) -> [String: Bool] {
        var out: [String: Bool] = [:]
        for f in all() {
            guard let key = f.value(forKey: "key") as? String else { continue }
            out[key] = isOn(f, in: p)
        }
        return out
    }

    private static func all() -> [NSObject] {
        var out: [NSObject] = []
        let cls: AnyClass = WKPreferences.self
        for name in lists {
            let sel = NSSelectorFromString(name)
            guard let m = class_getClassMethod(cls, sel) else { continue }
            typealias Fn = @convention(c) (AnyClass, Selector) -> NSArray?
            let list = unsafeBitCast(method_getImplementation(m), to: Fn.self)(cls, sel)
            out += (list as? [NSObject]) ?? []
        }
        return out
    }

    private static let getters = ["_isEnabledForFeature:", "_isEnabledForExperimentalFeature:", "_isEnabledForInternalDebugFeature:"]
    private static let setters = ["_setEnabled:forFeature:", "_setEnabled:forExperimentalFeature:", "_setEnabled:forInternalDebugFeature:"]

    private static func isOn(_ f: NSObject, in p: WKPreferences) -> Bool {
        for name in getters {
            let sel = NSSelectorFromString(name)
            guard p.responds(to: sel), let m = class_getInstanceMethod(WKPreferences.self, sel) else { continue }
            typealias Fn = @convention(c) (AnyObject, Selector, AnyObject) -> Bool
            return unsafeBitCast(method_getImplementation(m), to: Fn.self)(p, sel, f)
        }
        return false
    }

    private static func set(_ key: String, _ on: Bool, in p: WKPreferences) {
        guard let f = all().first(where: { $0.value(forKey: "key") as? String == key }) else { return }
        for name in setters {
            let sel = NSSelectorFromString(name)
            guard p.responds(to: sel), let m = class_getInstanceMethod(WKPreferences.self, sel) else { continue }
            typealias Fn = @convention(c) (AnyObject, Selector, Bool, AnyObject) -> Void
            unsafeBitCast(method_getImplementation(m), to: Fn.self)(p, sel, on, f)
        }
    }
}

/// Flash through Ruffle on a site: on iOS 16 its WebGL drawing shows an empty stage (checked on 16.2 with
/// ruffle.rs/demo, 2026-11-27), its canvas drawing works. The site's own choice of a renderer stays.
enum RuffleFix {
    static var needed: Bool { ProcessInfo.processInfo.operatingSystemVersion.majorVersion < 17 }

    static let source = #"""
(function () {
  if (window.__litebroRuffle) return;
  window.__litebroRuffle = 1;
  var cfg = { preferredRenderer: 'canvas' };
  var rp = window.RufflePlayer || {};
  try {
    Object.defineProperty(rp, 'config', {
      configurable: true, enumerable: true,
      get: function () { return cfg; },
      set: function (v) {
        if (v && typeof v === 'object') {
          if (!('preferredRenderer' in v)) { try { v.preferredRenderer = 'canvas'; } catch (e) {} }
          cfg = v;
        }
      }
    });
  } catch (e) { rp.config = cfg; }
  window.RufflePlayer = rp;
})();
"""#
}
