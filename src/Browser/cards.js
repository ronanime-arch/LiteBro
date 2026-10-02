// The card games of the page about a site that does not answer: Klondike, Spider of one suit and FreeCell.
// games.js calls window.__litebroCards with what the games share and adds the games it gets back to its list.
// They draw only when something changes: a move, a click, a card dragged.
window.__litebroCards = g => {
  "use strict";
  const { ctx, W, rounded, ACCENT } = g;
  const H = 400;
  const SUITS = ["♠", "♥", "♦", "♣"], RANKS = ["", "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K"];
  const isRed = c => c.s === 1 || c.s === 2;
  const top = p => p.cards[p.cards.length - 1];
  // A card goes on one a rank higher of the other color
  const follows = (c, under) => isRed(c) !== isRed(under) && c.r === under.r - 1;
  const homeTakes = (home, c) => top(home) ? top(home).s === c.s && c.r === top(home).r + 1 : c.r === 1;
  const homeCount = t => t.piles.reduce((n, p) => n + (p.kind === "home" ? p.cards.length : 0), 0);

  function deck(suits, times) {
    const d = [];
    for (let k = 0; k < times; k++) for (const s of suits) for (let r = 1; r <= 13; r++) d.push({ r, s, up: false });
    for (let i = d.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [d[i], d[j]] = [d[j], d[i]];
    }
    return d;
  }

  // The x of n columns of cards w wide, spread across the field
  const columns = (n, w, margin) => Array.from({ length: n }, (_, i) => margin + i * (W - 2 * margin - w) / (n - 1));

  function card(x, y, w, h, c) {
    rounded(x, y, w, h, 5);
    ctx.fillStyle = c.up ? "#fcfcfc" : ACCENT;
    ctx.fill();
    ctx.strokeStyle = "rgba(0,0,0,.3)";
    ctx.lineWidth = 1;
    ctx.stroke();
    if (!c.up) {
      ctx.strokeStyle = "rgba(255,255,255,.45)";
      rounded(x + 4, y + 4, w - 8, h - 8, 3);
      ctx.stroke();
      return;
    }
    // Rank and suit in the top line, the part a fanned card still shows
    const f = Math.round(w * .21);
    ctx.fillStyle = isRed(c) ? "#d8373e" : "#1d2125";
    ctx.textBaseline = "top";
    ctx.textAlign = "left";
    ctx.font = "700 " + f + "px 'Segoe UI', sans-serif";
    ctx.fillText(RANKS[c.r], x + 4, y + 3);
    ctx.textAlign = "right";
    ctx.font = f + "px 'Segoe UI Symbol', 'Segoe UI', sans-serif";
    ctx.fillText(SUITS[c.s], x + w - 4, y + 3);
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.font = Math.round(w * .46) + "px 'Segoe UI Symbol', 'Segoe UI', sans-serif";
    ctx.fillText(SUITS[c.s], x + w / 2, y + h * .6);
  }

  function slot(x, y, w, h, label, c) {
    rounded(x + .5, y + .5, w - 1, h - 1, 5);
    ctx.strokeStyle = c.dim;
    ctx.lineWidth = 1;
    ctx.stroke();
    if (!label) return;
    ctx.fillStyle = c.faint;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.font = "600 " + Math.round(w * .34) + "px 'Segoe UI', sans-serif";
    ctx.fillText(label, x + w / 2, y + h / 2);
  }

  // A table of piles: cards are dragged between them, a double click sends a card home, Z takes a move back.
  // The rules of a game say how it deals, which cards can be taken and where they can go.
  function table(rules) {
    const { cw, ch } = rules;
    return {
      still: true, h: H, hint: rules.hint, keys: ["KeyZ", "KeyN"],
      reset() {
        this.piles = rules.deal();
        this.undo = [];
        this.down = this.drag = this.last = null;
        this.won = false;
      },
      key(k, down) {
        if (k === "KeyN") { if (down) g.restart(); return true; }
        if (k === "KeyZ") { if (down) this.back(); return true; }
        return false;
      },
      save() {
        this.undo.push({ cards: JSON.stringify(this.piles.map(p => p.cards)), score: g.score });
        if (this.undo.length > 500) this.undo.shift();
      },
      back() {
        const s = this.undo.pop();
        if (!s) return;
        JSON.parse(s.cards).forEach((cards, i) => { this.piles[i].cards = cards; });
        g.score = s.score;
        g.draw();
      },
      // Where each card of a pile lies: a column fans out downwards, squeezed to stay on the field
      spots(p) {
        if (p.spread) return p.cards.map((_, i) => [p.x + Math.floor(i / p.spread) * p.step, p.y]);
        if (!p.fan) return p.cards.map(() => [p.x, p.y]);
        const n = p.cards.length, room = H - 6 - p.y - ch;
        let down = 0, up = 0, downGap = Math.round(ch * .09), upGap = Math.round(ch * .24);
        for (let i = 0; i < n - 1; i++) p.cards[i].up ? up++ : down++;
        if (down * downGap + up * upGap > room && up) upGap = Math.max(6, (room - down * downGap) / up);
        if (down * downGap + up * upGap > room && down) downGap = Math.max(2, (room - up * upGap) / down);
        const out = [];
        for (let i = 0, y = p.y; i < n; y += p.cards[i].up ? upGap : downGap, i++) out.push([p.x, y]);
        return out;
      },
      find(x, y) {
        const inside = ([px, py]) => x >= px && x < px + cw && y >= py && y < py + ch;
        for (let k = 0; k < this.piles.length; k++) {
          const p = this.piles[k], s = this.spots(p);
          for (let i = p.cards.length - 1; i >= 0; i--) if (inside(s[i])) return { k, i };
          if (inside([p.x, p.y])) return { k, i: -1 };
        }
        return null;
      },
      tap(x, y, button) {
        if (button !== 0) return;
        const hit = this.find(x, y);
        this.down = { x, y, hit, moved: false };
        this.drag = null;
        if (!hit || hit.i < 0 || !rules.pick(this, this.piles[hit.k], hit.i)) return;
        const s = this.spots(this.piles[hit.k]), [sx, sy] = s[hit.i];
        this.drag = { k: hit.k, i: hit.i, dx: x - sx, dy: y - sy, x: sx, y: sy, offsets: s.slice(hit.i).map(p => p[1] - sy) };
      },
      point(x, y) {
        const d = this.down;
        if (!d || (!d.moved && Math.hypot(x - d.x, y - d.y) < 5)) return;
        d.moved = true;
        if (!this.drag) return;
        this.drag.x = x - this.drag.dx;
        this.drag.y = y - this.drag.dy;
        g.draw();
      },
      release() {
        const d = this.down, drag = this.drag;
        this.down = this.drag = null;
        if (!d) return;
        if (d.moved) {
          if (drag) this.drop(drag);
          return g.draw();
        }
        // A click: the stock deals; a second click on the same card soon after sends it home
        const hit = d.hit, now = performance.now(), last = this.last;
        this.last = hit && { k: hit.k, i: hit.i, t: now };
        if (!hit) return;
        if (last && last.k === hit.k && last.i === hit.i && now - last.t < 450) {
          this.last = null;
          this.home(hit);
        } else if (rules.click) {
          this.save();
          if (rules.click(this, this.piles[hit.k], hit.i)) this.after();
          else this.undo.pop();
        }
        g.draw();
      },
      // The dragged cards go to the pile they cover the most of that takes them
      drop(drag) {
        const area = p => {
          const s = this.spots(p), [px, py] = p.cards.length ? s[s.length - 1] : [p.x, p.y];
          const w = Math.min(drag.x, px) + cw - Math.max(drag.x, px), h = Math.min(drag.y, py) + ch - Math.max(drag.y, py);
          return w > 0 && h > 0 ? w * h : 0;
        };
        const order = this.piles.map((p, k) => ({ k, a: k === drag.k ? 0 : area(p) })).filter(o => o.a > 0).sort((a, b) => b.a - a.a);
        for (const o of order) if (this.move(drag.k, drag.i, o.k)) return;
      },
      move(from, i, to) {
        const src = this.piles[from], dst = this.piles[to], cards = src.cards.slice(i);
        if (from === to || !rules.drop(this, cards, src, dst)) return false;
        this.save();
        src.cards.length = i;
        dst.cards.push(...cards);
        if (rules.moved) rules.moved(this, src, dst);
        this.after();
        return true;
      },
      home(hit) {
        const p = this.piles[hit.k];
        if (hit.i < 0 || hit.i !== p.cards.length - 1 || !rules.pick(this, p, hit.i)) return;
        for (const kind of rules.home || []) {
          if (kind === p.kind) continue;
          for (let k = 0; k < this.piles.length; k++) if (this.piles[k].kind === kind && this.move(hit.k, hit.i, k)) return;
        }
      },
      after() {
        if (rules.after) rules.after(this);
        if (this.won || !rules.won(this)) return;
        this.won = true;
        // A bonus for a quick game
        g.score += Math.max(0, 1200 - Math.round(g.seconds()));
        g.over();
      },
      draw(c) {
        const drag = this.drag && this.down && this.down.moved ? this.drag : null;
        this.piles.forEach((p, k) => {
          slot(p.x, p.y, cw, ch, rules.label ? rules.label(p) : "", c);
          const s = this.spots(p), end = drag && drag.k === k ? drag.i : p.cards.length;
          // A pile that does not fan out shows its top card only
          for (let i = p.fan || p.spread ? 0 : Math.max(0, end - 1); i < end; i++) card(s[i][0], s[i][1], cw, ch, p.cards[i]);
        });
        if (drag) this.piles[drag.k].cards.slice(drag.i).forEach((c2, j) => card(drag.x, drag.y + drag.offsets[j], cw, ch, c2));
      },
    };
  }

  // ---- Klondike: one card at a time from the stock, any number of times through it
  const klondike = table({
    hint: "Перетаскивайте карты мышью, двойной клик — в дом, клик по колоде — следующая карта, Z — отменить ход, N — новая раздача",
    cw: 70, ch: 96, home: ["home"],
    deal() {
      const d = deck([0, 1, 2, 3], 1), xs = columns(7, 70, 16), piles = [];
      piles.push({ kind: "stock", x: xs[0], y: 10, cards: [] }, { kind: "waste", x: xs[1], y: 10, cards: [] });
      for (let i = 3; i < 7; i++) piles.push({ kind: "home", x: xs[i], y: 10, cards: [] });
      for (let i = 0; i < 7; i++) {
        const cards = d.splice(0, i + 1);
        cards[i].up = true;
        piles.push({ kind: "tab", x: xs[i], y: 120, fan: true, cards });
      }
      piles[0].cards = d;
      return piles;
    },
    pick: (t, p, i) => p.cards[i].up && (p.kind === "tab" || i === p.cards.length - 1),
    drop(t, cards, src, dst) {
      const c = cards[0], under = top(dst);
      if (dst.kind === "home") return cards.length === 1 && homeTakes(dst, c);
      if (dst.kind === "tab") return under ? under.up && follows(c, under) : c.r === 13;
      return false;
    },
    moved(t, src, dst) {
      if (dst.kind === "home") g.score += 10;
      else if (src.kind === "waste") g.score += 5;
      else if (src.kind === "home") g.score = Math.max(0, g.score - 15);
    },
    click(t, p) {
      if (p.kind !== "stock") return false;
      const waste = t.piles[1];
      if (p.cards.length) {
        const c = p.cards.pop();
        c.up = true;
        waste.cards.push(c);
        return true;
      }
      if (!waste.cards.length) return false;
      // Through the stock once more, for a few points
      p.cards = waste.cards.reverse().map(c => ({ r: c.r, s: c.s, up: false }));
      waste.cards = [];
      g.score = Math.max(0, g.score - 20);
      return true;
    },
    after(t) {
      for (const p of t.piles) {
        if (p.kind === "tab" && p.cards.length && !top(p).up) { top(p).up = true; g.score += 5; }
      }
      // All open and nothing left to deal: the cards go home by themselves
      if (t.piles[0].cards.length || t.piles[1].cards.length || t.piles.some(p => p.cards.some(c => !c.up))) return;
      for (let moved = true; moved;) {
        moved = false;
        for (const p of t.piles) {
          const home = p.kind === "tab" && p.cards.length && t.piles.find(h => h.kind === "home" && homeTakes(h, top(p)));
          if (!home) continue;
          home.cards.push(p.cards.pop());
          g.score += 10;
          moved = true;
        }
      }
    },
    won: t => homeCount(t) === 52,
    label: p => p.kind === "home" ? "A" : p.kind === "tab" ? "K" : p.kind === "stock" ? "↻" : "",
  });

  // ---- Spider of one suit: two decks of spades, a run from king to ace leaves the table
  const spider = table({
    hint: "Перетаскивайте карты мышью, клик по колоде — раздать (не при пустом столбце), Z — отменить ход, N — новая раздача",
    cw: 56, ch: 76,
    deal() {
      const d = deck([0], 8), xs = columns(10, 56, 12), piles = [];
      piles.push({ kind: "stock", x: xs[0], y: 10, cards: [], spread: 10, step: 6 });
      piles.push({ kind: "done", x: xs[9], y: 10, cards: [], spread: 13, step: -6 });
      for (let i = 0; i < 10; i++) {
        const cards = d.splice(0, i < 4 ? 6 : 5);
        cards[cards.length - 1].up = true;
        piles.push({ kind: "tab", x: xs[i], y: 98, fan: true, cards });
      }
      piles[0].cards = d;
      return piles;
    },
    pick(t, p, i) {
      if (p.kind !== "tab" || !p.cards[i].up) return false;
      for (let j = i + 1; j < p.cards.length; j++) if (p.cards[j].r !== p.cards[j - 1].r - 1 || p.cards[j].s !== p.cards[j - 1].s) return false;
      return true;
    },
    drop(t, cards, src, dst) {
      const under = top(dst);
      return dst.kind === "tab" && (!under || (under.up && under.r === cards[0].r + 1));
    },
    click(t, p) {
      // A card to every column, but not while one is empty
      const tabs = t.piles.filter(q => q.kind === "tab");
      if (p.kind !== "stock" || !p.cards.length || tabs.some(q => !q.cards.length)) return false;
      for (const q of tabs) {
        const c = p.cards.pop();
        c.up = true;
        q.cards.push(c);
      }
      return true;
    },
    after(t) {
      const done = t.piles[1];
      for (const p of t.piles) {
        if (p.kind !== "tab") continue;
        const n = p.cards.length, run = p.cards.slice(n - 13);
        if (n >= 13 && run.every((c, j) => c.up && c.r === 13 - j && c.s === run[0].s)) {
          p.cards.length = n - 13;
          done.cards.push(...run.reverse());
          g.score += 100;
        }
        if (p.cards.length && !top(p).up) top(p).up = true;
      }
    },
    won: t => t.piles[1].cards.length === 104,
    label: p => p.kind === "done" ? "K" : "",
  });

  // ---- FreeCell: all cards open, four cells to hold one card each
  const freecell = table({
    hint: "Перетаскивайте карты мышью, двойной клик — в дом или в свободную ячейку, Z — отменить ход, N — новая раздача",
    cw: 68, ch: 92, home: ["home", "cell"],
    deal() {
      const d = deck([0, 1, 2, 3], 1), xs = columns(8, 68, 13), piles = [];
      for (let i = 0; i < 4; i++) piles.push({ kind: "cell", x: xs[i], y: 10, cards: [] });
      for (let i = 4; i < 8; i++) piles.push({ kind: "home", x: xs[i], y: 10, cards: [] });
      for (let i = 0; i < 8; i++) piles.push({ kind: "tab", x: xs[i], y: 116, fan: true, cards: [] });
      d.forEach((c, i) => { c.up = true; piles[8 + i % 8].cards.push(c); });
      return piles;
    },
    pick(t, p, i) {
      if (p.kind === "home") return false;
      for (let j = i + 1; j < p.cards.length; j++) if (!follows(p.cards[j], p.cards[j - 1])) return false;
      return true;
    },
    drop(t, cards, src, dst) {
      const c = cards[0], under = top(dst);
      if (dst.kind === "cell") return cards.length === 1 && !under;
      if (dst.kind === "home") return cards.length === 1 && homeTakes(dst, c);
      // As many cards as the free cells and empty columns let move one at a time
      const free = t.piles.filter(p => p.kind === "cell" && !p.cards.length).length;
      const empty = t.piles.filter(p => p.kind === "tab" && !p.cards.length && p !== dst).length;
      return cards.length <= (free + 1) * 2 ** empty && (!under || follows(c, under));
    },
    moved(t, src, dst) { if (dst.kind === "home") g.score += 10; },
    won: t => homeCount(t) === 52,
    label: p => p.kind === "home" ? "A" : "",
  });

  return [["klondike", "Косынка", klondike], ["spider", "Паук", spider], ["freecell", "Свободная ячейка", freecell]];
};
