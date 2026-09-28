// Put into every page while «только localhost» or the network journal is on, and run again on open pages when
// the rules change. The browser puts the rules in place of the config marker: {names, allow, block, trace}.
(() => {
  const cfg = __CONFIG__;
  // The browser's own pages talk to it directly
  if (location.hostname === "start.litebro") return;
  if (window.__litebroNet) { window.__litebroNet(cfg); return; }
  if (!cfg.block && !cfg.trace) return;
  const hook = window.chrome && window.chrome.webview;
  let names = [], allow = [], block = false, trace = false;
  const sockets = new Set();

  const esc = s => s.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*/g, ".*").replace(/\?/g, ".");
  const rx = p => new RegExp("^" + (p.startsWith("*.") ? "(.*\\.)?" + esc(p.slice(2)) : esc(p)) + "$", "i");

  // This machine and the local network, as the browser counts them (NetGuard.IsLocal)
  function local(h) {
    h = h.replace(/^\[|\]$/g, "").toLowerCase();
    if (h === "localhost" || h.endsWith(".localhost") || h === "::1" || h === "0.0.0.0" || h === "::") return true;
    const m = h.match(/^(\d+)\.(\d+)\.\d+\.\d+$/);
    if (m) {
      const a = +m[1], b = +m[2];
      return a === 127 || a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168) || (a === 169 && b === 254);
    }
    if (/^f[cd]|^fe[89ab]/.test(h) && h.includes(":")) return true;
    return names.some(r => r.test(h));
  }
  const outside = u => /^(https?|wss?):$/.test(u.protocol) && !local(u.hostname);
  const refused = u => block && outside(u) && !allow.some(r => r.test(u.hostname));

  function url(x) {
    try { return new URL(x instanceof Request ? x.url : String(x), location.href); } catch (e) { return null; }
  }

  // Tells the browser who asked for an outside address: the journal puts the stack on its entry
  function note(event, kind, method, u) {
    if (!hook || (event !== "ws-blocked" && !trace)) return;
    try {
      hook.postMessage("litebro-net:" + JSON.stringify({
        event, kind, method, url: u.href, stack: String(new Error().stack || "").slice(0, 4000),
      }));
    } catch (e) {}
  }

  function set(c) {
    names = c.names.map(rx);
    allow = c.allow.map(rx);
    block = c.block;
    trace = c.trace;
    // Sockets the new rules forbid are closed now, not when the page is reloaded
    for (const s of sockets) if (refused(s.__litebroUrl)) try { s.close(4000, "LiteBro: только localhost"); } catch (e) {}
  }
  Object.defineProperty(window, "__litebroNet", { value: set });
  set(cfg);

  const F = window.fetch;
  if (F) window.fetch = function (input, init) {
    const u = url(input);
    if (u && outside(u)) note("stack", "fetch", ((init && init.method) || (input instanceof Request ? input.method : "GET")).toUpperCase(), u);
    return F.apply(this, arguments);
  };

  const X = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function (method, address) {
    const u = url(address);
    if (u && outside(u)) note("stack", "XHR", String(method).toUpperCase(), u);
    return X.apply(this, arguments);
  };

  const B = navigator.sendBeacon;
  if (B) navigator.sendBeacon = function (address) {
    const u = url(address);
    if (u && outside(u)) note("stack", "ping/beacon", "POST", u);
    return B.apply(navigator, arguments);
  };

  const E = window.EventSource;
  if (E) {
    const ES = function (address, options) {
      const u = url(address);
      if (u && outside(u)) note("stack", "EventSource", "GET", u);
      return new E(address, options);
    };
    ES.prototype = E.prototype;
    for (const k of ["CONNECTING", "OPEN", "CLOSED"]) ES[k] = E[k];
    window.EventSource = ES;
  }

  // Web sockets never reach the browser's request filter: refused here, and journaled from here
  const W = window.WebSocket;
  if (W) {
    const WS = function (address, protocols) {
      const u = url(address);
      if (u && refused(u)) {
        note("ws-blocked", "WebSocket", "GET", u);
        throw new DOMException("Заблокировано режимом «только localhost»: " + u.href, "SecurityError");
      }
      if (u && outside(u)) note("ws", "WebSocket", "GET", u);
      const s = protocols === undefined ? new W(address) : new W(address, protocols);
      if (u) {
        s.__litebroUrl = u;
        sockets.add(s);
        s.addEventListener("close", () => sockets.delete(s));
      }
      return s;
    };
    WS.prototype = W.prototype;
    for (const k of ["CONNECTING", "OPEN", "CLOSING", "CLOSED"]) WS[k] = W[k];
    window.WebSocket = WS;
  }
})();
