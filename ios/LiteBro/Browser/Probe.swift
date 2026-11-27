import UIKit
import WebKit

/// Test web views for the computer (`Remote`, command `probe`): a tab whose web view is WebKit's own default
/// plus only the named parts of LiteBro's setup, and WebKit's feature switches by name. So what makes a page
/// misbehave in LiteBro and not in Safari is found on the phone itself, part by part, without a build for each guess.
enum Probe {
    /// The parts of `Browser.makeWebView` that can be added one by one
    static let parts = ["controller", "transparent", "agent", "media", "fraud", "gestures"]

    /// The web view and the feature switches that were really set
    static func webView(_ browser: Browser, parts: Set<String>, features: [String: Bool]) -> (WKWebView, [String]) {
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
        let applied = features.filter { set($0.key, $0.value, in: c.preferences) }.keys.sorted()
        let w = WKWebView(frame: .zero, configuration: c)
        if parts.contains("gestures") {
            w.allowsBackForwardNavigationGestures = true
            w.isFindInteractionEnabled = true
        }
        if parts.contains("transparent") {
            w.isOpaque = false
            w.backgroundColor = .systemBackground
        }
        return (w, applied)
    }

    // MARK: WebKit's feature switches (private API of WKPreferences, as Safari's Develop menu uses)

    /// WebKit keeps its switches in lists by kind, each kind with its own reader and writer: a switch goes
    /// only to those of the list it came from (another kind's object there is read as something it is not)
    private struct Kind {
        let list: String, getter: String, setter: String
    }

    private static let kinds = [
        Kind(list: "_features", getter: "_isEnabledForFeature:", setter: "_setEnabled:forFeature:"),
        Kind(list: "_experimentalFeatures", getter: "_isEnabledForExperimentalFeature:", setter: "_setEnabled:forExperimentalFeature:"),
        Kind(list: "_internalDebugFeatures", getter: "_isEnabledForInternalDebugFeature:", setter: "_setEnabled:forInternalDebugFeature:"),
    ]

    private static func all() -> [(feature: NSObject, key: String, kind: Kind)] {
        var out: [(NSObject, String, Kind)] = []
        var seen = Set<String>()
        let cls: AnyClass = WKPreferences.self
        for kind in kinds {
            let sel = NSSelectorFromString(kind.list)
            guard let m = class_getClassMethod(cls, sel),
                  class_getInstanceMethod(cls, NSSelectorFromString(kind.getter)) != nil,
                  class_getInstanceMethod(cls, NSSelectorFromString(kind.setter)) != nil else { continue }
            typealias Fn = @convention(c) (AnyClass, Selector) -> NSArray?
            let list = unsafeBitCast(method_getImplementation(m), to: Fn.self)(cls, sel)
            for f in (list as? [NSObject]) ?? [] {
                guard let key = f.value(forKey: "key") as? String, seen.insert(key).inserted else { continue }
                out.append((f, key, kind))
            }
        }
        return out
    }

    /// Every switch WebKit names, with its state in `p`
    static func features(in p: WKPreferences = WKPreferences()) -> [String: Bool] {
        var out: [String: Bool] = [:]
        for f in all() {
            let sel = NSSelectorFromString(f.kind.getter)
            guard let m = class_getInstanceMethod(WKPreferences.self, sel) else { continue }
            typealias Fn = @convention(c) (AnyObject, Selector, AnyObject) -> Bool
            out[f.key] = unsafeBitCast(method_getImplementation(m), to: Fn.self)(p, sel, f.feature)
        }
        return out
    }

    /// False when WebKit has no switch of this name
    @discardableResult
    static func set(_ key: String, _ on: Bool, in p: WKPreferences) -> Bool {
        guard let f = all().first(where: { $0.key == key }) else { return false }
        let sel = NSSelectorFromString(f.kind.setter)
        guard let m = class_getInstanceMethod(WKPreferences.self, sel) else { return false }
        typealias Fn = @convention(c) (AnyObject, Selector, Bool, AnyObject) -> Void
        unsafeBitCast(method_getImplementation(m), to: Fn.self)(p, sel, on, f.feature)
        return true
    }
}

/// Flash through Ruffle on a site: on iOS 16 its WebGL drawing shows an empty stage (checked on 16.2 with
/// ruffle.rs/demo, 2026-11-27), its canvas drawing works. The site's own choice of a renderer stays.
/// Nothing is put on the page until the page itself makes `RufflePlayer`: a site that asks whether Ruffle
/// is there still gets "no", and whatever object it or ruffle.js puts there gets the default in its config.
enum RuffleFix {
    static var needed: Bool { ProcessInfo.processInfo.operatingSystemVersion.majorVersion < 17 }

    static let source = #"""
(function () {
  var current;
  function withDefault(c) {
    if (c && typeof c === 'object' && !('preferredRenderer' in c)) { try { c.preferredRenderer = 'canvas'; } catch (e) {} }
    return c;
  }
  function guard(o) {
    if (!o || typeof o !== 'object') return o;
    try {
      var cfg = withDefault(o.config && typeof o.config === 'object' ? o.config : {});
      Object.defineProperty(o, 'config', {
        configurable: true, enumerable: true,
        get: function () { return cfg; },
        set: function (v) { if (v && typeof v === 'object') cfg = withDefault(v); }
      });
    } catch (e) {}
    return o;
  }
  try {
    Object.defineProperty(window, 'RufflePlayer', {
      configurable: true, enumerable: false,
      get: function () { return current; },
      set: function (v) { current = guard(v); }
    });
  } catch (e) {}
})();
"""#
}
