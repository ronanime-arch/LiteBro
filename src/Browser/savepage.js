// Run in a page for "Save as" with one of "txt", "csv", "json": the file's text, or null when the page has nothing
// for it (no tables). A JSON document gives its own text; an array of objects in it also makes a CSV.
(mode) => {
  const isJson = /json/i.test(document.contentType);
  const raw = window.__litebroJsonText ?? (isJson ? (document.querySelector("body > pre")?.textContent ?? document.body?.innerText ?? "") : null);
  // Excel with Russian settings splits on semicolons; = + - @ at the start would be a formula
  const cell = v => {
    let s = v == null ? "" : typeof v === "object" ? JSON.stringify(v) : String(v);
    if (/^[=+\-@\t\r]/.test(s)) s = "'" + s;
    return /[;"\r\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s;
  };
  const csv = rows => rows.map(r => r.map(cell).join(";")).join("\r\n") + "\r\n";

  if (raw != null) {
    if (mode === "txt" || mode === "json") return raw;
    let data;
    try { data = JSON.parse(raw); } catch { return null; }
    // The first array of rows: the document itself, or one of its fields ({"items": [...]})
    const list = Array.isArray(data) ? data : Object.values(data ?? {}).find(Array.isArray);
    if (!list || list.length === 0) return null;
    if (list.every(Array.isArray)) return csv(list);
    const keys = [];
    for (const item of list)
      if (item && typeof item === "object") for (const k of Object.keys(item)) if (!keys.includes(k)) keys.push(k);
    if (keys.length === 0) return csv(list.map(v => [v]));
    return csv([keys, ...list.map(item => keys.map(k => item?.[k]))]);
  }

  if (mode === "txt") return document.body?.innerText ?? "";
  const tables = [...document.querySelectorAll("table")].map(t => ({
    caption: (t.caption?.innerText ?? "").trim(),
    head: t.rows.length > 1 && [...t.rows[0].cells].every(c => c.tagName === "TH"),
    rows: [...t.rows].map(r => [...r.cells].map(c => c.innerText.trim())),
  })).filter(t => t.rows.length > 0);
  if (tables.length === 0) return null;
  if (mode === "csv")
    return tables.map(t => (t.caption ? cell(t.caption) + "\r\n" : "") + csv(t.rows)).join("\r\n");
  // JSON: a table with a header row becomes objects by column name
  const shape = t => t.head
    ? t.rows.slice(1).map(r => Object.fromEntries(t.rows[0].map((h, i) => [h || "column" + (i + 1), r[i] ?? ""])))
    : t.rows;
  const out = tables.length === 1 ? shape(tables[0]) : tables.map(t => ({ caption: t.caption, rows: shape(t) }));
  return JSON.stringify(out, null, 2);
}
