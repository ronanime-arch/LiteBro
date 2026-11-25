/// The page script that finds video and audio for the downloader; runs in every frame at document start.
/// Only what really loaded is reported (an ad blocked by the rules is not), with the duration and
/// whether it plays, so the downloader can put the video being watched first.
enum MediaSniffer {
    static let source = #"""
(function () {
  if (window.__litebroMedia) return;
  var seen = {};
  var media = /\.(m3u8|mp4|m4v|mov|webm|mkv|mp3|m4a|aac|ogg|opus|flac|wav)(\?|#|$)/i;
  var hls = /\.m3u8(\?|#|$)|[?&]format=m3u8|\/manifest\/hls/i;

  function send(u, how, extra) {
    try { u = new URL(u, location.href).href; } catch (e) { return; }
    if (!/^https?:/i.test(u)) return;
    var m = { url: u, how: how, hls: hls.test(u), title: (top === self ? document.title : '') };
    for (var k in extra || {}) m[k] = extra[k];
    // The same address again only when it says something new (duration, playing)
    var key = u + '|' + (m.duration || '') + '|' + (m.playing ? 1 : 0);
    if (seen[key]) return;
    seen[key] = 1;
    try { window.webkit.messageHandlers.litebroMedia.postMessage(m); } catch (e) {}
  }
  function check(u, how) {
    if (typeof u !== 'string') { try { u = u && (u.url || String(u)); } catch (e) { return; } }
    if (u && (media.test(u) || hls.test(u))) send(u, how);
  }
  // <video>/<audio> that loaded; blob: is MediaSource (its segments come through fetch/XHR)
  function element(v, playing) {
    if (!v || !v.tagName || v.readyState < 1) return;
    var s = v.currentSrc || v.src;
    var extra = { duration: isFinite(v.duration) ? v.duration : 0, playing: !!playing };
    if (s && !/^blob:/i.test(s)) send(s, v.tagName.toLowerCase(), extra);
    else if (playing) send(location.href, 'mse', { mse: true, playing: true, duration: extra.duration });
  }
  function scan() {
    var list = document.querySelectorAll('video, audio');
    for (var i = 0; i < list.length; i++) element(list[i], !list[i].paused);
    try { performance.getEntriesByType('resource').forEach(function (e) { check(e.name, 'net'); }); } catch (e) {}
  }
  document.addEventListener('loadedmetadata', function (e) { element(e.target, false); }, true);
  document.addEventListener('playing', function (e) { element(e.target, true); }, true);
  try {
    new PerformanceObserver(function (l) { l.getEntries().forEach(function (e) { check(e.name, 'net'); }); })
      .observe({ type: 'resource', buffered: true });
  } catch (e) {}
  var f = window.fetch;
  if (f) window.fetch = function () {
    var p = f.apply(this, arguments);
    p.then(function (r) { if (r && r.ok) check(r.url, 'fetch'); }, function () {});
    return p;
  };
  var open = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function (m, u) {
    var x = this;
    try {
      x.addEventListener('load', function () {
        if (x.status >= 200 && x.status < 300) check(x.responseURL || String(u), 'xhr');
      });
    } catch (e) {}
    return open.apply(this, arguments);
  };
  window.__litebroMedia = { scan: scan };
  document.addEventListener('DOMContentLoaded', scan);
})();
"""#
}
