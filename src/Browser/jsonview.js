// Put into a page the engine shows as JSON text (an API's answer opened in a tab): the same data as a tree
// that folds, with a switch back to the text as it came. Nothing of it is sent anywhere.
(() => {
  if (window.__litebroJson || !/json/i.test(document.contentType)) return;
  const pre = document.querySelector("body > pre");
  const raw = pre ? pre.textContent : document.body ? document.body.innerText : "";
  if (!raw || raw.length > 20 * 1024 * 1024) return;
  let data;
  try { data = JSON.parse(raw); } catch { return; }
  window.__litebroJson = true;
  window.__litebroJsonText = raw; // for "Save as": the tree replaces the text

  const css = `
:root { color-scheme: light dark; --fg: #1b1d22; --muted: #6b7080; --key: #8a1bb5; --str: #1a7f37; --num: #0550ae; --lit: #b35900; --bg: #fff; --bar: #f2f3f6; --line: rgba(0,0,0,.12); }
@media (prefers-color-scheme: dark) { :root { --fg: #e9eaee; --muted: #9a9fae; --key: #d2a8ff; --str: #7ee787; --num: #79c0ff; --lit: #ffa657; --bg: #1c1d20; --bar: #121212; --line: rgba(255,255,255,.14); } }
html, body { margin: 0; background: var(--bg); color: var(--fg); }
#lb-bar { position: sticky; top: 0; display: flex; gap: 8px; align-items: center; padding: 8px 14px; background: var(--bar); border-bottom: 1px solid var(--line); font: 13px "Segoe UI", sans-serif; z-index: 1; }
#lb-bar button { font: 13px "Segoe UI", sans-serif; padding: 3px 10px; border: 1px solid var(--line); border-radius: 6px; background: transparent; color: inherit; cursor: pointer; }
#lb-bar button:hover { background: rgba(127,127,127,.15); }
#lb-bar button.on { background: rgba(127,127,127,.25); font-weight: 600; }
#lb-bar span { color: var(--muted); margin-left: auto; }
#lb-tree, #lb-raw { font: 13px/1.5 Consolas, monospace; padding: 10px 16px 40px; margin: 0; white-space: pre-wrap; word-break: break-word; }
#lb-tree ul { list-style: none; margin: 0; padding-left: 20px; border-left: 1px dotted var(--line); }
#lb-tree li { position: relative; }
.lb-t { cursor: pointer; user-select: none; color: var(--muted); display: inline-block; width: 14px; margin-left: -16px; }
.lb-k { color: var(--key); }
.lb-s { color: var(--str); }
.lb-n { color: var(--num); }
.lb-l { color: var(--lit); }
.lb-c { color: var(--muted); cursor: pointer; }
.lb-closed > ul { display: none; }
.lb-closed > .lb-c::after { content: " …"; }
a.lb-s { text-decoration: underline; }`;

  const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
  // Russian forms; in English (few and many alike) one is for 1 only
  const plural = (n, one, few, many) => n + " " + (few === many ? (n === 1 ? one : many) : n % 10 === 1 && n % 100 !== 11 ? one : n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14) ? few : many);
  const count = v => Array.isArray(v) ? plural(v.length, "элемент", "элемента", "элементов") : plural(Object.keys(v).length, "ключ", "ключа", "ключей");

  function value(v) {
    if (v === null) return el("span", "lb-l", "null");
    if (typeof v === "boolean") return el("span", "lb-l", String(v));
    if (typeof v === "number") return el("span", "lb-n", String(v));
    if (typeof v === "string") {
      const text = JSON.stringify(v);
      if (/^https?:\/\/\S+$/.test(v)) { const a = el("a", "lb-s", text); a.href = v; return a; }
      return el("span", "lb-s", text);
    }
    return null;
  }

  function node(key, v, last, depth) {
    const li = el("li");
    const toggle = el("span", "lb-t");
    li.append(toggle);
    if (key !== null) li.append(el("span", "lb-k", JSON.stringify(key)), ": ");
    const simple = value(v);
    if (simple) {
      li.append(simple);
      if (!last) li.append(",");
      return li;
    }
    const array = Array.isArray(v), entries = array ? v.map((x, i) => [null, x]) : Object.entries(v);
    li.append(array ? "[" : "{");
    if (!entries.length) {
      li.append(array ? "]" : "}");
      if (!last) li.append(",");
      return li;
    }
    toggle.textContent = "▾";
    const summary = el("span", "lb-c", " " + count(v) + " ");
    const ul = el("ul");
    // Deep or long parts start folded: a large answer opens fast
    let built = false;
    const build = () => {
      if (built) return;
      built = true;
      entries.forEach(([k, x], i) => ul.append(node(k, x, i === entries.length - 1, depth + 1)));
    };
    const fold = closed => {
      if (!closed) build();
      li.classList.toggle("lb-closed", closed);
      toggle.textContent = closed ? "▸" : "▾";
    };
    toggle.onclick = summary.onclick = () => fold(!li.classList.contains("lb-closed"));
    li.lbFold = fold;
    li.append(summary, ul, array ? "]" : "}");
    if (!last) li.append(",");
    fold(depth >= 3 || entries.length > 500);
    return li;
  }

  document.head.append(el("style", null, css));
  const bar = el("div");
  bar.id = "lb-bar";
  const treeButton = el("button", "on", "Дерево"), rawButton = el("button", null, "Текст"), prettyButton = el("button", null, "Текст с отступами");
  const openAll = el("button", null, "Развернуть всё"), closeAll = el("button", null, "Свернуть всё"), copy = el("button", null, "Копировать");
  const size = raw.length < 1024 ? raw.length + " Б" : raw.length < 1048576 ? (raw.length / 1024).toFixed(1) + " КБ" : (raw.length / 1048576).toFixed(1) + " МБ";
  bar.append(treeButton, rawButton, prettyButton, openAll, closeAll, copy, el("span", null, "JSON, " + size));
  const tree = el("div");
  tree.id = "lb-tree";
  const top = el("ul");
  top.style.borderLeft = "0";
  top.style.paddingLeft = "16px";
  top.append(node(null, data, true, 0));
  tree.append(top);
  const rawView = el("pre", null, raw);
  rawView.id = "lb-raw";
  rawView.hidden = true;
  document.body.textContent = "";
  document.body.append(bar, tree, rawView);

  const show = which => {
    tree.hidden = which !== "tree";
    rawView.hidden = which === "tree";
    if (which === "raw") rawView.textContent = raw;
    if (which === "pretty") rawView.textContent = JSON.stringify(data, null, 2);
    treeButton.classList.toggle("on", which === "tree");
    rawButton.classList.toggle("on", which === "raw");
    prettyButton.classList.toggle("on", which === "pretty");
    openAll.hidden = closeAll.hidden = which !== "tree";
  };
  treeButton.onclick = () => show("tree");
  rawButton.onclick = () => show("raw");
  prettyButton.onclick = () => show("pretty");
  // Everything opened: at most a few thousand nodes, else it would hang the tab
  openAll.onclick = () => {
    let left = 5000;
    const walk = li => {
      if (left-- <= 0 || !li.lbFold) return;
      li.lbFold(false);
      for (const child of li.querySelector(":scope > ul").children) walk(child);
    };
    walk(top.firstChild);
  };
  closeAll.onclick = () => { for (const li of tree.querySelectorAll("li")) if (li.lbFold && li !== top.firstChild) li.lbFold(true); };
  copy.onclick = async () => {
    const text = tree.hidden ? rawView.textContent : JSON.stringify(data, null, 2);
    try { await navigator.clipboard.writeText(text); copy.textContent = "Скопировано"; }
    catch { copy.textContent = "Не удалось"; }
    setTimeout(() => { copy.textContent = "Копировать"; }, 1500);
  };
})();
