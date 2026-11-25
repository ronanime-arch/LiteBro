import Foundation
import WebKit

/// A line of the page console: what the page logged, its errors and requests, and what was typed
struct ConsoleLine: Identifiable {
    enum Kind: String { case log, info, warn, error, debug, net, netError, input, result }
    let id = UUID()
    let kind: Kind
    let text: String
    let time = Date()

    var isError: Bool { kind == .error || kind == .netError }
}

/// The page console: a script in the page world wraps console, errors, fetch and XHR and sends lines
/// in batches; typed code runs in the page world too, as in the console of a desktop browser.
enum PageConsole {
    static let handler = "litebroConsole"
    static let maxLines = 1000

    /// Turns any value into readable text (objects as JSON, errors with the stack, elements as tags)
    static let format = """
    const __fmt = (v, deep) => {
      try {
        if (typeof v === 'string') return deep ? JSON.stringify(v) : v;
        if (v === undefined) return 'undefined';
        if (typeof v === 'function') return 'ƒ ' + (v.name || '') + '()';
        if (typeof v === 'symbol' || typeof v === 'bigint') return String(v);
        if (v instanceof Error) return (v.stack && v.stack.indexOf(v.message) >= 0) ? v.stack : (v.name + ': ' + v.message + (v.stack ? '\\n' + v.stack : ''));
        if (typeof Element !== 'undefined' && v instanceof Element)
          return '<' + v.tagName.toLowerCase() + (v.id ? '#' + v.id : '') +
            (typeof v.className === 'string' && v.className ? '.' + v.className.trim().split(/\\s+/).join('.') : '') + '>';
        if (v && typeof v === 'object') {
          const seen = new WeakSet();
          const s = JSON.stringify(v, (k, x) => {
            if (typeof x === 'function') return 'ƒ ' + (x.name || '') + '()';
            if (typeof x === 'bigint') return String(x);
            if (x && typeof x === 'object') { if (seen.has(x)) return '[Circular]'; seen.add(x); }
            return x;
          }, 2);
          const name = v.constructor && v.constructor.name;
          const t = (name && name !== 'Object' && name !== 'Array' ? name + ' ' : '') + (s === undefined ? String(v) : s);
          return t.length > 4000 ? t.slice(0, 4000) + ' …' : t;
        }
        return String(v);
      } catch (e) { try { return String(v); } catch (_) { return '?'; } }
    };
    """

    /// Installed at document start in the main frame (or right away when the console is opened)
    static let source = """
    (() => {
      if (window.__litebroConsole) return;
      const post = window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.\(handler);
      if (!post) return;
      \(format)
      let queue = [], timer = 0, sent = 0;
      const flush = () => {
        timer = 0;
        if (!queue.length) return;
        const lines = queue; queue = [];
        try { post.postMessage({ lines }); } catch (_) {}
      };
      const add = (k, t) => {
        if (sent >= 3000) return;
        if (++sent === 3000) { k = 'warn'; t = 'LiteBro: too many lines, the rest are dropped'; }
        queue.push({ k, t: String(t).slice(0, 20000) });
        if (!timer) timer = setTimeout(flush, 250);
      };
      window.__litebroConsole = { add };
      const c = window.console;
      ['log', 'info', 'warn', 'error', 'debug'].forEach(k => {
        const orig = c[k];
        c[k] = function (...a) {
          add(k, a.map(x => __fmt(x)).join(' '));
          return orig && orig.apply(this, a);
        };
      });
      const assert = c.assert;
      c.assert = function (ok, ...a) {
        if (!ok) add('error', 'Assertion failed: ' + a.map(x => __fmt(x)).join(' '));
        return assert && assert.apply(this, [ok, ...a]);
      };
      const table = c.table;
      c.table = function (d, ...a) { add('log', __fmt(d)); return table && table.apply(this, [d, ...a]); };
      window.addEventListener('error', e => {
        if (e.error) add('error', __fmt(e.error));
        else if (e.message) add('error', e.message + (e.filename ? ' (' + e.filename + ':' + e.lineno + ')' : ''));
        else if (e.target && e.target !== window) add('netError', 'Failed: ' + (e.target.src || e.target.href || e.target.tagName));
      }, true);
      window.addEventListener('unhandledrejection', e => add('error', 'Unhandled rejection: ' + __fmt(e.reason)));
      const abs = u => { try { return new URL(u, location.href).href; } catch (_) { return String(u); } };
      const f = window.fetch;
      if (f) window.fetch = function (input, init) {
        const t0 = performance.now();
        const url = abs(input && input.url ? input.url : input);
        const m = ((init && init.method) || (input && input.method) || 'GET').toUpperCase();
        return f.apply(this, arguments).then(r => {
          add(r.ok ? 'net' : 'netError', m + ' ' + r.status + ' ' + url + ' · ' + Math.round(performance.now() - t0) + ' ms');
          return r;
        }, e => { add('netError', m + ' ✕ ' + url + ' · ' + __fmt(e)); throw e; });
      };
      const X = window.XMLHttpRequest && XMLHttpRequest.prototype;
      if (X) {
        const open = X.open, send = X.send;
        X.open = function (m, u) { this.__lb = { m: String(m || 'GET').toUpperCase(), u: abs(u) }; return open.apply(this, arguments); };
        X.send = function () {
          const i = this.__lb, t0 = performance.now();
          if (i) this.addEventListener('loadend', () => {
            const ok = this.status >= 200 && this.status < 400;
            add(ok ? 'net' : 'netError', i.m + ' ' + (this.status || '✕') + ' ' + i.u + ' · ' + Math.round(performance.now() - t0) + ' ms');
          });
          return send.apply(this, arguments);
        };
      }
    })();
    """

    /// Runs typed code in the page: an expression first (awaited, so promises show their value),
    /// statements if it is not one. The page's CSP does not apply to code from the app.
    static func run(_ code: String, in w: WKWebView, done: @escaping (_ text: String, _ failed: Bool) -> Void) {
        let body = format + "\nconst __v = await (\n" + code + "\n);\nreturn __fmt(__v);"
        w.callAsyncJavaScript(body, arguments: [:], in: nil, in: .page) { r in
            switch r {
            case .success(let v):
                done(v as? String ?? "undefined", false)
            case .failure(let e):
                let message = Self.message(e)
                guard message.contains("SyntaxError") else { return done(message, true) }
                // Statements: the value of the last one, when WebKit can pass it over
                w.evaluateJavaScript(code, in: nil, in: .page) { r2 in
                    switch r2 {
                    case .success(let v): done(Self.describe(v), false)
                    case .failure(let e2):
                        let m2 = Self.message(e2)
                        done(m2.contains("unsupported type") ? "undefined" : m2, !m2.contains("unsupported type"))
                    }
                }
            }
        }
    }

    private static func message(_ e: Error) -> String {
        let n = e as NSError
        if let m = n.userInfo["WKJavaScriptExceptionMessage"] as? String { return m }
        return n.localizedDescription
    }

    private static func describe(_ v: Any) -> String {
        if let s = v as? String { return s }
        if JSONSerialization.isValidJSONObject(v),
           let d = try? JSONSerialization.data(withJSONObject: v, options: [.prettyPrinted, .sortedKeys]) {
            return String(decoding: d, as: UTF8.self)
        }
        return "\(v)"
    }
}
