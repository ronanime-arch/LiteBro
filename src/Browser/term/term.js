// The page side of a terminal tab: xterm.js draws, the browser runs PowerShell in a pseudo console
(() => {
  const dark = matchMedia("(prefers-color-scheme: dark)");
  const themes = {
    dark: { background: "#1e1e1e", foreground: "#e8e8e8", cursor: "#e8e8e8", selectionBackground: "#4d6bfe80" },
    light: { background: "#ffffff", foreground: "#1f1f1f", cursor: "#1f1f1f", selectionBackground: "#4d6bfe55",
      // Yellow and white of the dark palette are unreadable on white
      yellow: "#9a6700", brightYellow: "#7d5500", white: "#6e6e6e", brightWhite: "#3a3a3a" },
  };
  const term = new Terminal({
    fontFamily: "'Cascadia Mono', Consolas, monospace",
    fontSize: 14,
    cursorBlink: true,
    scrollback: 5000,
    allowProposedApi: false,
    theme: dark.matches ? themes.dark : themes.light,
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(document.getElementById("term"));
  fit.fit();
  dark.addEventListener("change", () => { term.options.theme = dark.matches ? themes.dark : themes.light; });

  const post = m => window.chrome.webview.postMessage(m);
  const note = document.getElementById("note");
  let ended = false;

  function start() {
    ended = false;
    note.hidden = true;
    post({ type: "termStart", cols: term.cols, rows: term.rows });
  }

  window.chrome.webview.addEventListener("message", e => {
    const m = e.data;
    if (!m || typeof m !== "object") return;
    if (m.type === "termOut") term.write(m.data);
    else if (m.type === "termExit") {
      ended = true;
      term.write("\r\n\x1b[90m[Процесс завершён. Enter — запустить снова]\x1b[0m\r\n");
    } else if (m.type === "termError") {
      ended = true;
      term.write("\r\n\x1b[31m" + m.text + "\x1b[0m\r\n");
    }
  });

  term.onData(d => {
    if (ended) { if (d === "\r") { term.clear(); start(); } return; }
    post({ type: "termIn", data: d });
  });
  term.onResize(s => post({ type: "termSize", cols: s.cols, rows: s.rows }));
  term.onTitleChange(t => { document.title = t || "Терминал"; });

  const copy = () => {
    const s = term.getSelection();
    if (!s) return false;
    navigator.clipboard.writeText(s).catch(() => {});
    term.clearSelection();
    return true;
  };
  const paste = () => navigator.clipboard.readText().then(t => { if (t) term.paste(t); }).catch(() => {});

  // As in Windows Terminal: Ctrl+C copies a selection (else it stops the command), Ctrl+V pastes
  term.attachCustomKeyEventHandler(e => {
    if (e.type !== "keydown" || !e.ctrlKey || e.altKey) return true;
    const k = e.key.toLowerCase();
    if (k === "c" && (e.shiftKey || term.hasSelection())) { copy(); e.preventDefault(); return false; }
    // The browser's own paste event reaches xterm
    if (k === "v") return false;
    return true;
  });
  // Right click: copy the selection, or paste
  document.addEventListener("contextmenu", e => {
    e.preventDefault();
    if (!copy()) paste();
  });

  let timer = 0;
  addEventListener("resize", () => {
    clearTimeout(timer);
    timer = setTimeout(() => fit.fit(), 60);
  });
  term.focus();
  start();
})();
