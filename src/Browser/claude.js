// What Claude reads and points at in a site's page (Mcp.cs, BrowserForm.Claude.cs): run by Runtime.evaluate with
// the operation in place of the placeholder at the end, gives back a JSON string. The numbered elements live on the window until the next listing.
(function (op) {
  const refs = window.__litebroRefs || [];
  const clean = s => (s || "").replace(/\s+/g, " ").trim();
  const visible = e => {
    const r = e.getBoundingClientRect();
    if (r.width < 1 || r.height < 1) return false;
    const st = getComputedStyle(e);
    return st.visibility !== "hidden" && st.display !== "none" && st.opacity !== "0";
  };
  const named = e => {
    let s = e.getAttribute("aria-label") || "";
    const by = e.getAttribute("aria-labelledby");
    if (!s && by) s = by.split(/\s+/).map(id => { const l = document.getElementById(id); return l ? l.innerText : ""; }).join(" ");
    if (!s && e.labels && e.labels.length) s = e.labels[0].innerText;
    if (!s && e.tagName === "INPUT" && /^(submit|button|reset)$/.test(e.type)) s = e.value;
    if (!s && !/^(INPUT|TEXTAREA|SELECT)$/.test(e.tagName)) s = e.innerText || "";
    if (!s) { const img = e.querySelector && e.querySelector("img[alt]"); if (img) s = img.alt; }
    if (!s) s = e.getAttribute("placeholder") || e.getAttribute("title") || e.getAttribute("alt") || e.getAttribute("name") || "";
    s = clean(s);
    return s.length > 100 ? s.slice(0, 100) + "…" : s;
  };
  const kind = e => {
    const role = e.getAttribute("role");
    if (role) return role;
    const t = e.tagName.toLowerCase();
    if (t === "a") return "link";
    if (t === "input") return (e.type || "text") === "text" ? "textbox" : e.type;
    if (t === "textarea") return "textbox";
    if (e.isContentEditable) return "editable";
    return t;
  };
  const byRef = n => {
    const e = refs[n - 1];
    if (!e || !e.isConnected) throw new Error("No element " + n + ": the page changed. Read the elements again.");
    return e;
  };
  // Open shadow roots too
  const all = (root, out) => {
    for (const e of root.querySelectorAll("*")) {
      out.push(e);
      if (e.shadowRoot) all(e.shadowRoot, out);
    }
    return out;
  };
  const SELECT = "a[href],button,input:not([type=hidden]),select,textarea,summary,[contenteditable=''],[contenteditable=true],[onclick]," +
    "[role=button],[role=link],[role=checkbox],[role=radio],[role=tab],[role=menuitem],[role=option],[role=switch],[role=combobox]," +
    "[role=textbox],[role=searchbox],[role=slider],[role=treeitem],[tabindex]:not([tabindex='-1'])";

  if (op.op === "size")
    return JSON.stringify({ w: innerWidth, h: innerHeight, y: Math.round(scrollY), height: document.documentElement.scrollHeight });

  if (op.op === "text") {
    const text = (document.body ? document.body.innerText : "").replace(/\n{3,}/g, "\n\n").trim();
    const from = Math.max(0, op.offset || 0);
    return JSON.stringify({ title: document.title, url: location.href, total: text.length, text: text.slice(from, from + (op.max || 20000)) });
  }

  if (op.op === "elements") {
    const list = all(document, []).filter(e => e.matches(SELECT) && visible(e));
    const inView = e => { const r = e.getBoundingClientRect(); return r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth; };
    // What is on screen first, then the rest of the page, each in page order
    const shown = list.filter(inView), rest = list.filter(e => !inView(e));
    const picked = shown.concat(rest).slice(0, op.max || 300);
    const lines = picked.map((e, i) => {
      let line = "[" + (i + 1) + "] " + kind(e) + ' "' + named(e) + '"';
      if (/^(INPUT|TEXTAREA|SELECT)$/.test(e.tagName)) {
        if (e.type === "checkbox" || e.type === "radio") line += e.checked ? " checked" : " unchecked";
        else if (e.type === "password") line += e.value ? " value=(hidden)" : "";
        else if (e.tagName === "SELECT") line += ' value="' + clean(e.selectedOptions[0] ? e.selectedOptions[0].text : "") + '"';
        else if (e.value) line += ' value="' + clean(e.value).slice(0, 80) + '"';
      }
      if (e.disabled) line += " disabled";
      if (e.tagName === "A") line += " -> " + e.href.slice(0, 120);
      if (!inView(e)) line += " (off screen)";
      return line;
    });
    Object.defineProperty(window, "__litebroRefs", { value: picked, configurable: true, enumerable: false, writable: true });
    return JSON.stringify({ title: document.title, url: location.href, total: list.length, lines });
  }

  if (op.op === "point") {
    const e = byRef(op.ref);
    e.scrollIntoView({ block: "center", inline: "center", behavior: "instant" });
    const r = e.getBoundingClientRect();
    const x = r.left + Math.min(r.width / 2, 20 + r.width / 4), y = r.top + r.height / 2;
    const top = document.elementFromPoint(x, y);
    const covered = top && top !== e && !e.contains(top) && !(top.shadowRoot || top.contains(e)) ? top.tagName.toLowerCase() : "";
    return JSON.stringify({ x, y, covered });
  }

  if (op.op === "focus") {
    const e = byRef(op.ref);
    e.scrollIntoView({ block: "center", behavior: "instant" });
    e.focus();
    if (op.clear) {
      if (typeof e.select === "function") e.select();
      else if (e.isContentEditable) {
        const range = document.createRange();
        range.selectNodeContents(e);
        const sel = getSelection();
        sel.removeAllRanges();
        sel.addRange(range);
      }
    }
    return JSON.stringify({ focused: document.activeElement === e || e.contains(document.activeElement) });
  }
  throw new Error("Unknown operation");
})(__OP__)
