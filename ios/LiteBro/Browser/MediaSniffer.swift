/// The page script that finds video and audio for the downloader; runs in every frame at document start.
enum MediaSniffer {
    static let source = #"""
// LiteBro: finds video and audio on the page for the downloader. Runs in every frame at document start.
(function () {
  if (window.__litebroMedia) return;
  var seen = {};
  var media = /\.(m3u8|mp4|m4v|mov|webm|mkv|mp3|m4a|aac|ogg|opus|flac|wav)(\?|#|$)/i;
  var hls = /\.m3u8(\?|#|$)|[?&]format=m3u8|\/manifest\/hls/i;

  function post(u, how) {
    try { u = new URL(u, location.href).href; } catch (e) { return; }
    if (!/^https?:/i.test(u) || seen[u]) return;
    seen[u] = 1;
    try {
      window.webkit.messageHandlers.litebroMedia.postMessage({
        url: u, how: how, hls: hls.test(u), title: (top === self ? document.title : '')
      });
    } catch (e) {}
  }
  function check(u, how) {
    if (typeof u !== 'string') { try { u = u && (u.url || String(u)); } catch (e) { return; } }
    if (u && (media.test(u) || hls.test(u))) post(u, how);
  }
  // <video>/<audio>: blob: is MediaSource (the segments come through fetch/XHR instead)
  function element(v) {
    if (!v || !v.tagName) return;
    [v.currentSrc, v.src].forEach(function (s) { if (s && !/^blob:/i.test(s)) post(s, v.tagName.toLowerCase()); });
    var list = v.querySelectorAll ? v.querySelectorAll('source[src]') : [];
    for (var i = 0; i < list.length; i++) check(list[i].src, 'source');
  }
  function scan() {
    var list = document.querySelectorAll('video, audio');
    for (var i = 0; i < list.length; i++) element(list[i]);
    try { performance.getEntriesByType('resource').forEach(function (e) { check(e.name, 'net'); }); } catch (e) {}
  }
  ['loadedmetadata', 'play', 'loadstart'].forEach(function (t) {
    document.addEventListener(t, function (e) { element(e.target); }, true);
  });
  try {
    new PerformanceObserver(function (l) { l.getEntries().forEach(function (e) { check(e.name, 'net'); }); })
      .observe({ type: 'resource', buffered: true });
  } catch (e) {}
  var f = window.fetch;
  if (f) window.fetch = function (input) { check(input, 'fetch'); return f.apply(this, arguments); };
  var open = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function (m, u) { check(u, 'xhr'); return open.apply(this, arguments); };
  window.__litebroMedia = { scan: scan };
  document.addEventListener('DOMContentLoaded', scan);
})();
"""#
}
