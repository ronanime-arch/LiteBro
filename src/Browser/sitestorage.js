// Runs in a site's page for the storage page (https://start.litebro/storage) through Runtime.evaluate:
// reads or changes localStorage, sessionStorage and IndexedDB of the page's origin. The browser puts the
// operation in place of the marker; the result comes back as a JSON string.
(async op => {
  const MaxRows = 300, MaxValue = 100000;
  // What JSON cannot hold is shown as a note and is not editable
  const show = v => {
    let odd = false;
    const text = JSON.stringify(v, function (k, x) {
      // A date has become its text by now: saved back it would stay text
      if (this[k] instanceof Date) { odd = true; return x; }
      if (typeof x === "bigint") { odd = true; return x.toString() + "n"; }
      if (typeof Blob !== "undefined" && x instanceof Blob) { odd = true; return "[Blob " + x.size + " байт]"; }
      if (x instanceof ArrayBuffer) { odd = true; return "[ArrayBuffer " + x.byteLength + " байт]"; }
      if (ArrayBuffer.isView(x)) { odd = true; return "[" + x.constructor.name + " " + x.byteLength + " байт]"; }
      if (x instanceof Map) { odd = true; return Object.fromEntries(x); }
      if (x instanceof Set) { odd = true; return [...x]; }
      if (x === undefined) { odd = true; return null; }
      return x;
    });
    return { text: text === undefined ? "undefined" : text, odd };
  };
  const done = r => new Promise((ok, bad) => { r.onsuccess = () => ok(r.result); r.onerror = () => bad(r.error); });
  const open = name => new Promise((ok, bad) => {
    const r = indexedDB.open(name);
    r.onsuccess = () => ok(r.result);
    r.onerror = () => bad(r.error);
    r.onblocked = () => bad(new Error("база занята другой вкладкой"));
    // A name that is not there any more: nothing is created
    r.onupgradeneeded = () => { r.transaction.abort(); };
  });
  const area = op.area === "session" ? sessionStorage : localStorage;
  const pairs = s => { const list = []; for (let i = 0; i < s.length; i++) { const k = s.key(i); list.push([k, s.getItem(k)]); } return list; };
  const store = async mode => {
    const db = await open(op.db);
    try { return [db, db.transaction(op.store, mode).objectStore(op.store)]; }
    catch (e) { db.close(); throw e; }
  };
  const finish = (db, store) => new Promise((ok, bad) => {
    store.transaction.oncomplete = () => { db.close(); ok(); };
    store.transaction.onerror = store.transaction.onabort = () => { db.close(); bad(store.transaction.error || new Error("отменено")); };
  });
  switch (op.op) {
    case "load": {
      const idb = [];
      const names = indexedDB.databases ? await indexedDB.databases() : [];
      for (const d of names) {
        let db;
        try { db = await open(d.name); }
        catch (e) { idb.push({ name: d.name, version: d.version, error: String(e && e.message || e), stores: [] }); continue; }
        const stores = [];
        for (const name of db.objectStoreNames) {
          const s = db.transaction(name, "readonly").objectStore(name);
          const count = await done(s.count());
          const rows = [];
          await new Promise((ok, bad) => {
            const r = s.openCursor();
            r.onerror = () => bad(r.error);
            r.onsuccess = () => {
              const c = r.result;
              if (!c || rows.length >= MaxRows) return ok();
              const key = show(c.key), value = show(c.value);
              const big = value.text.length > MaxValue;
              rows.push({ key: key.text, value: big ? value.text.slice(0, MaxValue) : value.text, fixed: key.odd || value.odd || big });
              c.continue();
            };
          });
          stores.push({ name, keyPath: s.keyPath, autoIncrement: s.autoIncrement, count, rows });
        }
        db.close();
        idb.push({ name: d.name, version: db.version, stores });
      }
      return JSON.stringify({ origin: location.origin, url: location.href, local: pairs(localStorage), session: pairs(sessionStorage), idb });
    }
    case "set":
      if (op.oldKey != null && op.oldKey !== op.key) area.removeItem(op.oldKey);
      area.setItem(op.key, op.value);
      return "{}";
    case "remove":
      area.removeItem(op.key);
      return "{}";
    case "clear":
      area.clear();
      return "{}";
    case "idbPut": {
      const [db, s] = await store("readwrite");
      const ended = finish(db, s);
      const value = JSON.parse(op.value);
      if (s.keyPath === null) {
        const key = op.key === "" && s.autoIncrement ? undefined : JSON.parse(op.key);
        if (op.oldKey != null && op.oldKey !== op.key) s.delete(JSON.parse(op.oldKey));
        s.put(value, key);
      } else {
        if (op.oldKey != null) s.delete(JSON.parse(op.oldKey));
        s.put(value);
      }
      await ended;
      return "{}";
    }
    case "idbDelete": {
      const [db, s] = await store("readwrite");
      const ended = finish(db, s);
      s.delete(JSON.parse(op.key));
      await ended;
      return "{}";
    }
    case "idbClear": {
      const [db, s] = await store("readwrite");
      const ended = finish(db, s);
      s.clear();
      await ended;
      return "{}";
    }
    case "idbCreate": {
      // A new object store is made in a version change: the base's version goes up by one
      const known = indexedDB.databases ? (await indexedDB.databases()).find(d => d.name === op.db) : null;
      await new Promise((ok, bad) => {
        const r = known ? indexedDB.open(op.db, known.version + 1) : indexedDB.open(op.db);
        r.onupgradeneeded = () => {
          const db = r.result;
          if (db.objectStoreNames.contains(op.store)) { r.transaction.abort(); return; }
          db.createObjectStore(op.store, { keyPath: op.keyPath === "" ? null : op.keyPath, autoIncrement: !!op.autoIncrement });
        };
        r.onsuccess = () => { r.result.close(); ok(); };
        r.onerror = () => bad(r.error && r.error.name === "AbortError" ? new Error("такое хранилище уже есть") : r.error);
        // The site's page keeps the base open and does not let it go: the change waits for it
        r.onblocked = () => bad(new Error("база открыта страницей сайта: хранилище появится, когда сайт её отпустит (обновите страницу сайта)"));
      });
      return "{}";
    }
    case "clearAll": {
      localStorage.clear();
      sessionStorage.clear();
      const names = indexedDB.databases ? await indexedDB.databases() : [];
      for (const d of names) {
        let db;
        try { db = await open(d.name); } catch (e) { continue; }
        const list = [...db.objectStoreNames];
        if (!list.length) { db.close(); continue; }
        const t = db.transaction(list, "readwrite");
        for (const name of list) t.objectStore(name).clear();
        await new Promise((ok, bad) => {
          t.oncomplete = () => { db.close(); ok(); };
          t.onerror = t.onabort = () => { db.close(); bad(t.error || new Error("отменено")); };
        });
      }
      return "{}";
    }
  }
  throw new Error("неизвестная операция");
})(__OP__)
