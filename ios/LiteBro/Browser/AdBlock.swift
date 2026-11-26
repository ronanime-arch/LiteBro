import CryptoKit
import Foundation
import WebKit

/// Ads and telemetry, blocked by WebKit content rules: the engine itself filters, so it costs
/// almost no memory or CPU (the same idea as uBlock Origin Lite on Windows, without the download).
enum AdBlock {
    /// Ad networks
    static let ads = [
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google.com",
        "googletagservices.com", "adnxs.com", "adsrvr.org", "advertising.com", "amazon-adsystem.com",
        "criteo.com", "criteo.net", "taboola.com", "outbrain.com", "pubmatic.com", "rubiconproject.com",
        "openx.net", "casalemedia.com", "smartadserver.com", "moatads.com", "adform.net", "yieldmo.com",
        "sharethrough.com", "33across.com", "media.net", "bidswitch.net", "contextweb.com", "teads.tv",
        "spotxchange.com", "adcolony.com", "applovin.com", "unityads.unity3d.com", "popads.net",
        "propellerads.com", "popcash.net", "exoclick.com", "juicyads.com", "trafficjunky.net",
        "adsterra.com", "mgid.com", "revcontent.com", "zedo.com", "yieldlove.com", "doubleverify.com",
        "adsafeprotected.com", "an.yandex.ru", "yandexadexchange.net", "adfox.ru", "adriver.ru",
        "ad.mail.ru", "betweendigital.com", "buzzoola.com", "otm-r.com", "adhigh.net",
    ]

    /// Analytics, session recording, pixels and error telemetry
    static let telemetry = [
        "google-analytics.com", "googletagmanager.com", "analytics.google.com", "app-measurement.com",
        "firebaselogging-pa.googleapis.com", "mc.yandex.ru", "mc.yandex.com", "mc.webvisor.org",
        "top-fwz1.mail.ru", "counter.yadro.ru", "hotjar.com", "hotjar.io", "mouseflow.com",
        "fullstory.com", "clarity.ms", "api.segment.io", "cdn.segment.com", "mixpanel.com",
        "api.amplitude.com", "cdn.amplitude.com", "heapanalytics.com", "quantserve.com",
        "scorecardresearch.com", "chartbeat.com", "chartbeat.net", "nr-data.net", "ingest.sentry.io",
        "sessions.bugsnag.com", "notify.bugsnag.com", "analytics.tiktok.com", "px.ads.linkedin.com",
        "snap.licdn.com", "analytics.twitter.com", "static.ads-twitter.com", "bat.bing.com",
        "imrworldwide.com", "omtrdc.net", "demdex.net", "everesttech.net", "krxd.net", "bluekai.com",
        "tns-counter.ru", "statcounter.com",
    ]

    /// Ads and statistics that live on the site's own domain
    static let paths = [
        "facebook\\.com/tr[/?]", "connect\\.facebook\\.net/[^/]+/fbevents\\.js",
        "youtube\\.com/api/stats/ads", "youtube\\.com/pagead/", "youtube\\.com/ptracking",
        "youtube\\.com/get_midroll_", "youtube\\.com/api/stats/atr",
    ]

    static let hide = [
        ".adsbygoogle", "ins.adsbygoogle", "[id^=\"google_ads_iframe\"]", "[id^=\"div-gpt-ad\"]",
        "[data-ad-slot]", "amp-ad", "amp-embed[type=\"taboola\"]", "#taboola-below-article-thumbnails",
        ".OUTBRAIN", "[id^=\"yandex_rtb\"]", "[id^=\"adfox_\"]",
    ]

    static let hideYouTube = [
        "ytd-ad-slot-renderer", "ad-slot-renderer", "ytm-promoted-sparkles-web-renderer",
        "ytd-promoted-sparkles-web-renderer", "#masthead-ad", "ytd-banner-promo-renderer",
        "ytm-companion-ad-renderer", ".ytp-ad-overlay-container", "ytd-in-feed-ad-layout-renderer",
        "ytm-promoted-video-renderer", "ytd-player-legacy-desktop-watch-ads-renderer",
    ]

    /// The built-in rules (a JSON array without brackets): they work at once, the full lists come later
    static func builtinRules() -> String {
        var r: [[String: Any]] = []
        func block(_ f: String) { r.append(["trigger": ["url-filter": f], "action": ["type": "block"]]) }
        for d in ads + telemetry {
            block("^[a-z]+://([^/:]*\\.)?" + NSRegularExpression.escapedPattern(for: d) + "[:/]")
        }
        for p in paths { block("^[a-z]+://([^/:]*\\.)?" + p) }
        r.append(["trigger": ["url-filter": ".*"],
                  "action": ["type": "css-display-none", "selector": hide.joined(separator: ", ")]])
        r.append(["trigger": ["url-filter": ".*", "if-domain": ["*youtube.com"]],
                  "action": ["type": "css-display-none", "selector": hideYouTube.joined(separator: ", ")]])
        return FilterLists.json(r)
    }

    /// "Do not block on this site": the rules of a list stop for pages of these sites
    static func siteExceptions(_ sites: [String]) -> String {
        guard !sites.isEmpty else { return "" }
        return FilterLists.json([["trigger": ["url-filter": ".*", "if-domain": sites.map { "*" + $0 }],
                                  "action": ["type": "ignore-previous-rules"]]])
    }

    /// All ad lists: the built-in one and the parts of the full lists. An exception works only inside
    /// its own list, so every list gets the exceptions at its end.
    static func compile(_ done: @escaping ([WKContentRuleList]) -> Void) {
        let sites = siteExceptions(Prefs.adBlockOff)
        DispatchQueue.global(qos: .utility).async {
            var lists: [(id: String, json: () -> String)] = []
            let builtin = builtinRules()
            lists.append((RuleStore.id("ads-builtin", builtin + sites), { "[" + join(builtin, sites) + "]" }))
            if let full = FilterLists.built() {
                let stamp = FilterLists.updated.map { String(Int($0.timeIntervalSince1970)) } ?? "0"
                for (i, part) in full.parts.enumerated() {
                    lists.append((RuleStore.id("ads-\(i)", stamp + "\(full.count)-\(FilterLists.converter)" + sites),
                                  { "[" + join(part, full.exceptions, sites) + "]" }))
                }
            }
            RuleStore.load(lists, prefix: "ads-") { done($0) }
        }
    }

    private static func join(_ parts: String...) -> String { parts.filter { !$0.isEmpty }.joined(separator: ",") }

    /// YouTube serves its video ads from its own servers: the player is fast-forwarded through them
    /// and the skip button pressed. Once a second and only while the page is visible.
    static func youTubeScript(except: [String]) -> String {
        let off = String(decoding: try! JSONSerialization.data(withJSONObject: except), as: UTF8.self)
        return #"""
        (function () {
          var h = location.hostname, off = \#(off);
          if (!/(^|\.)youtube(-nocookie)?\.com$/.test(h)) return;
          if (off.some(function (d) { return h === d || h.endsWith('.' + d); })) return;
          setInterval(function () {
            if (document.hidden) return;
            var p = document.querySelector('.ad-showing');
            if (p) {
              var v = p.querySelector('video');
              if (v && isFinite(v.duration) && v.duration > 0) { v.muted = true; v.currentTime = v.duration; }
            }
            var b = document.querySelector('.ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button, .ytm-skip-ad-button button');
            if (b) b.click();
          }, 1000);
        })();
        """#
    }

    /// The site of a page as the exceptions keep it: the host without "www." and "m."
    static func site(of url: URL?) -> String? {
        guard let h = url?.host?.lowercased(), ["http", "https"].contains(url?.scheme?.lowercased() ?? "") else { return nil }
        for p in ["www.", "m."] where h.hasPrefix(p) { return String(h.dropFirst(p.count)) }
        return h
    }

    static func isOff(for url: URL?) -> Bool {
        guard let s = site(of: url) else { return false }
        let h = url?.host?.lowercased() ?? s
        return Prefs.adBlockOff.contains { h == $0 || h.hasSuffix("." + $0) }
    }
}

/// Compiled lists are kept by WebKit between launches: a list is compiled only when its id is new
enum RuleStore {
    static func id(_ name: String, _ content: String) -> String {
        let d = SHA256.hash(data: Data(content.utf8))
        return name + "-" + d.prefix(8).map { String(format: "%02x", $0) }.joined()
    }

    /// Looks the lists up or compiles them, in order, then forgets old lists with the same prefix
    static func load(_ lists: [(id: String, json: () -> String)], prefix: String,
                     done: @escaping ([WKContentRuleList]) -> Void) {
        DispatchQueue.main.async {
            guard let store = WKContentRuleListStore.default() else { return done([]) }
            var out: [WKContentRuleList] = []
            func next(_ i: Int) {
                guard i < lists.count else {
                    let keep = Set(lists.map(\.id))
                    store.getAvailableContentRuleListIdentifiers { ids in
                        for id in ids ?? [] where id.hasPrefix(prefix) && !keep.contains(id) {
                            store.removeContentRuleList(forIdentifier: id) { _ in }
                        }
                    }
                    return done(out)
                }
                let item = lists[i]
                store.lookUpContentRuleList(forIdentifier: item.id) { found, _ in
                    if let found {
                        out.append(found)
                        return next(i + 1)
                    }
                    // Big lists take seconds: the text is made off the main thread
                    DispatchQueue.global(qos: .utility).async {
                        let json = item.json()
                        DispatchQueue.main.async {
                            store.compileContentRuleList(forIdentifier: item.id, encodedContentRuleList: json) { list, error in
                                if let error { NSLog("LiteBro: rules \(item.id): \(error)") }
                                if let list { out.append(list) }
                                next(i + 1)
                            }
                        }
                    }
                }
            }
            next(0)
        }
    }
}
