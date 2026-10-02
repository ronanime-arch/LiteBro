// The mini-games of the page about a site that does not answer: a runner, a snake and a flying packet.
// Nothing runs until a game is played. Records and the game picked go to the browser (games.txt), which
// hands them back in window.__litebroGames.
(() => {
  "use strict";
  const W = 640, H = 200, GROUND = 172;
  const saved = window.__litebroGames || { pick: "runner", best: {} };
  const post = m => { try { window.chrome.webview.postMessage(m); } catch { } };

  const box = document.getElementById("games");
  const pick = document.createElement("select");
  pick.id = "gamePick";
  pick.setAttribute("aria-label", "Мини-игра");
  for (const [value, name] of [["runner", "Раннер-штекер"], ["snake", "Змейка"], ["flappy", "Летающий пакет"]])
    pick.append(new Option(name, value));
  const canvas = document.createElement("canvas");
  canvas.tabIndex = 0;
  const hint = document.createElement("p");
  hint.className = "muted";
  box.append(canvas, hint);
  document.body.append(pick);
  const ctx = canvas.getContext("2d");
  const style = getComputedStyle(document.body);
  const ACCENT = "#4d6bfe", HOT = "#e5484d", LED = "#37c46a";

  let game, state = "ready", score = 0, last = 0, frame = 0;
  const best = () => saved.best[pick.value] || 0;
  const rand = (a, b) => a + Math.random() * (b - a);

  function rounded(x, y, w, h, r) {
    ctx.beginPath();
    ctx.roundRect(x, y, w, h, r);
  }

  // ---- Runner: a plug runs along its cable and jumps over sockets, ducks under sparks
  const runner = {
    hint: "Пробел, ↑ или клик — прыжок, ↓ — пригнуться",
    keys: ["Space", "ArrowUp", "KeyW", "ArrowDown", "KeyS"],
    reset() {
      this.y = 0; this.vy = 0; this.duck = false; this.speed = .32; this.dist = 0; this.next = 500; this.things = [];
    },
    key(k, down) {
      if (k === "Space" || k === "ArrowUp" || k === "KeyW") { if (down) this.jump(); return true; }
      if (k === "ArrowDown" || k === "KeyS") {
        this.duck = down;
        if (down && this.y > 0) this.vy = Math.min(this.vy, -.6); // down fast
        return true;
      }
      return false;
    },
    tap() { this.jump(); },
    jump() { if (this.y === 0 && !this.duck) this.vy = .9; },
    height() { return this.duck && this.y === 0 ? 18 : 34; },
    step(dt) {
      this.speed = Math.min(.75, .32 + this.dist / 40000);
      const move = this.speed * dt;
      this.dist += move;
      score = Math.floor(this.dist / 40);
      if (this.y > 0 || this.vy > 0) {
        this.vy -= .0045 * dt;
        this.y = Math.max(0, this.y + this.vy * dt);
        if (this.y === 0) this.vy = 0;
      }
      for (const t of this.things) t.x -= move;
      this.things = this.things.filter(t => t.x + t.w > -10);
      if ((this.next -= move) <= 0) {
        // A spark only once the game is under way, at the height a ducking plug passes under
        if (this.dist > 1500 && Math.random() < .3) this.things.push({ x: W + 10, w: 26, h: 14, y: 24, spark: true });
        else {
          const double = this.dist > 800 && Math.random() < .3;
          this.things.push({ x: W + 10, w: double ? 52 : 26, h: rand(26, 40), y: 0, double });
        }
        this.next = rand(260, 560) + this.speed * 300;
      }
      const px = 60, pw = 30, ph = this.height();
      for (const t of this.things) {
        const over = px + 4 < t.x + t.w && px + pw - 4 > t.x;
        const up = this.y + 3 < t.y + t.h && this.y + ph - 3 > t.y;
        if (over && up) return over_();
      }
    },
    draw(c) {
      // The ground and the plug's cable trailing back along it
      ctx.strokeStyle = c.dim;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(0, GROUND + .5);
      ctx.lineTo(W, GROUND + .5);
      ctx.stroke();
      ctx.fillStyle = c.dim;
      const shift = (this.dist || 0) % 40;
      for (let x = -shift; x < W; x += 40) ctx.fillRect(x + 10, GROUND + 8, 6, 1.5);
      for (const t of this.things) {
        if (t.spark) {
          ctx.strokeStyle = HOT;
          ctx.lineWidth = 3;
          ctx.beginPath();
          const top = GROUND - t.y - t.h;
          ctx.moveTo(t.x, top + t.h / 2);
          ctx.lineTo(t.x + 8, top);
          ctx.lineTo(t.x + 13, top + t.h);
          ctx.lineTo(t.x + 19, top + 2);
          ctx.lineTo(t.x + t.w, top + t.h / 2);
          ctx.stroke();
          continue;
        }
        // A socket, single or double
        ctx.fillStyle = c.fg;
        rounded(t.x, GROUND - t.h, t.w, t.h, 5);
        ctx.fill();
        ctx.fillStyle = c.bg;
        for (let i = 0; i < (t.double ? 2 : 1); i++) {
          const cx = t.x + 13 + i * 26, cy = GROUND - t.h + 13;
          ctx.beginPath();
          ctx.arc(cx - 4, cy, 2.2, 0, 7);
          ctx.arc(cx + 4, cy, 2.2, 0, 7);
          ctx.fill();
        }
      }
      const h = this.height(), x = 60, top = GROUND - (this.y || 0) - h;
      ctx.strokeStyle = c.fg;
      ctx.lineWidth = 3;
      ctx.beginPath();
      ctx.moveTo(x, top + h - 8);
      ctx.quadraticCurveTo(x - 18, GROUND - 2, x - 40, GROUND - 2);
      ctx.lineTo(-10, GROUND - 2);
      ctx.stroke();
      ctx.fillStyle = ACCENT;
      rounded(x, top, 30, h, 6);
      ctx.fill();
      // Prongs in front, an eye that blinks while running
      ctx.fillStyle = c.fg;
      ctx.fillRect(x + 30, top + h * .28, 9, 3);
      ctx.fillRect(x + 30, top + h * .62, 9, 3);
      ctx.fillStyle = "#fff";
      ctx.fillRect(x + 19, top + 6, 4, state === "over" ? 1.5 : 4);
    },
  };

  // ---- Snake: it grows on the packets it picks up
  const COLS = 32, ROWS = 10, CELL = 20;
  const DIRS = { ArrowUp: [0, -1], KeyW: [0, -1], ArrowDown: [0, 1], KeyS: [0, 1], ArrowLeft: [-1, 0], KeyA: [-1, 0], ArrowRight: [1, 0], KeyD: [1, 0] };
  const snake = {
    hint: "Стрелки, WASD или клик — повернуть",
    keys: Object.keys(DIRS),
    reset() {
      this.body = [{ x: 8, y: 5 }, { x: 7, y: 5 }, { x: 6, y: 5 }];
      this.dir = { x: 1, y: 0 }; this.queue = []; this.acc = 0; this.tick = 150;
      this.place();
    },
    place() {
      do this.food = { x: Math.floor(rand(0, COLS)), y: Math.floor(rand(0, ROWS)) };
      while (this.body.some(p => p.x === this.food.x && p.y === this.food.y));
    },
    turn(dx, dy) {
      const lastDir = this.queue.length ? this.queue[this.queue.length - 1] : this.dir;
      if ((dx !== -lastDir.x || dy !== -lastDir.y) && (dx !== lastDir.x || dy !== lastDir.y) && this.queue.length < 3)
        this.queue.push({ x: dx, y: dy });
    },
    key(k, down) {
      const d = DIRS[k];
      if (!d) return false;
      if (down) this.turn(d[0], d[1]);
      return true;
    },
    tap(x, y) {
      // Towards the tap, across the way it goes
      const head = this.body[0], dx = x - (head.x + .5) * CELL, dy = y - (head.y + .5) * CELL;
      if (this.dir.x !== 0) this.turn(0, Math.sign(dy) || 1);
      else this.turn(Math.sign(dx) || 1, 0);
    },
    step(dt) {
      this.acc += dt;
      while (this.acc >= this.tick && state === "play") {
        this.acc -= this.tick;
        if (this.queue.length) this.dir = this.queue.shift();
        const head = this.body[0], next = { x: head.x + this.dir.x, y: head.y + this.dir.y };
        const eats = next.x === this.food.x && next.y === this.food.y;
        const body = eats ? this.body : this.body.slice(0, -1);
        if (next.x < 0 || next.y < 0 || next.x >= COLS || next.y >= ROWS || body.some(p => p.x === next.x && p.y === next.y))
          return over_();
        this.body.unshift(next);
        if (eats) {
          score++;
          this.tick = Math.max(70, this.tick - 4);
          this.place();
        } else this.body.pop();
      }
    },
    draw(c) {
      ctx.fillStyle = c.faint;
      for (let x = 0; x < COLS; x++) for (let y = 0; y < ROWS; y++) ctx.fillRect(x * CELL + 9, y * CELL + 9, 2, 2);
      const f = this.food;
      ctx.fillStyle = HOT;
      rounded(f.x * CELL + 4, f.y * CELL + 5, 12, 10, 2);
      ctx.fill();
      ctx.strokeStyle = c.bg;
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.moveTo(f.x * CELL + 5, f.y * CELL + 6);
      ctx.lineTo(f.x * CELL + 10, f.y * CELL + 10);
      ctx.lineTo(f.x * CELL + 15, f.y * CELL + 6);
      ctx.stroke();
      this.body.forEach((p, i) => {
        ctx.fillStyle = i === 0 ? c.fg : ACCENT;
        rounded(p.x * CELL + 1, p.y * CELL + 1, CELL - 2, CELL - 2, i === 0 ? 6 : 4);
        ctx.fill();
      });
    },
  };

  // ---- A data packet flaps its way between server racks
  const flappy = {
    hint: "Пробел, ↑ или клик — взмах",
    keys: ["Space", "ArrowUp", "KeyW"],
    reset() { this.y = 90; this.vy = 0; this.racks = []; this.next = 120; this.speed = .16; },
    key(k, down) {
      if (k !== "Space" && k !== "ArrowUp" && k !== "KeyW") return false;
      if (down) this.flap();
      return true;
    },
    tap() { this.flap(); },
    flap() { this.vy = -.42; },
    step(dt) {
      this.speed = Math.min(.26, .16 + score * .004);
      this.vy = Math.min(.6, this.vy + .0016 * dt);
      this.y += this.vy * dt;
      const move = this.speed * dt;
      for (const r of this.racks) {
        r.x -= move;
        if (!r.passed && r.x + 38 < 120) { r.passed = true; score++; }
      }
      this.racks = this.racks.filter(r => r.x > -50);
      if ((this.next -= move) <= 0) {
        const gap = 74;
        this.racks.push({ x: W + 10, top: rand(18, H - 18 - gap), gap });
        this.next = 230;
      }
      if (this.y < 0 || this.y + 16 > H) {
        this.y = Math.max(0, Math.min(this.y, H - 16)); // seen whole where it fell
        return over_();
      }
      for (const r of this.racks)
        if (120 + 22 > r.x + 2 && 120 < r.x + 36 && (this.y + 2 < r.top || this.y + 14 > r.top + r.gap)) return over_();
    },
    draw(c) {
      for (const r of this.racks) {
        for (const [y, h] of [[0, r.top], [r.top + r.gap, H - r.top - r.gap]]) {
          ctx.fillStyle = c.fg;
          ctx.fillRect(r.x, y, 38, h);
          // Units of the rack, each with its light
          ctx.fillStyle = c.bg;
          for (let u = y + 6; u < y + h - 8; u += 12) ctx.fillRect(r.x + 4, u, 30, 7);
          ctx.fillStyle = LED;
          for (let u = y + 6; u < y + h - 8; u += 12) ctx.fillRect(r.x + 28, u + 2, 3, 3);
        }
      }
      const x = 120, y = this.y;
      ctx.fillStyle = ACCENT;
      rounded(x, y, 22, 16, 3);
      ctx.fill();
      ctx.strokeStyle = "#fff";
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.moveTo(x + 2, y + 3);
      ctx.lineTo(x + 11, y + 10);
      ctx.lineTo(x + 20, y + 3);
      ctx.stroke();
    },
  };

  const games = { runner, snake, flappy };

  function colors() {
    return { fg: style.color, bg: style.backgroundColor, dim: "rgba(127,127,127,.55)", faint: "rgba(127,127,127,.25)" };
  }

  function draw() {
    const c = colors();
    ctx.clearRect(0, 0, W, H);
    game.draw(c);
    ctx.fillStyle = c.fg;
    ctx.font = "600 14px Consolas, monospace";
    ctx.textAlign = "right";
    ctx.textBaseline = "top";
    const pad = n => String(n).padStart(5, "0");
    ctx.globalAlpha = .6;
    ctx.fillText("Рекорд " + pad(best()), W - 92, 10);
    ctx.globalAlpha = 1;
    ctx.fillText(pad(score), W - 12, 10);
    if (state === "play") return;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.font = "600 17px 'Segoe UI', sans-serif";
    ctx.fillText(state === "over" ? "Игра окончена" : pick.options[pick.selectedIndex].text, W / 2, 70);
    ctx.font = "14px 'Segoe UI', sans-serif";
    ctx.globalAlpha = .75;
    ctx.fillText(state === "over" ? "Пробел или клик — ещё раз" : "Пробел или клик — играть", W / 2, 96);
    ctx.globalAlpha = 1;
  }

  function loop(t) {
    frame = 0;
    if (state !== "play") return;
    const dt = Math.min(40, t - (last || t));
    last = t;
    game.step(dt);
    draw();
    if (state === "play") frame = requestAnimationFrame(loop);
  }

  function start() {
    game.reset();
    score = 0;
    last = 0;
    state = "play";
    if (!frame) frame = requestAnimationFrame(loop);
  }

  function over_() {
    state = "over";
    if (score > best()) {
      saved.best[pick.value] = score;
      post({ type: "game", game: pick.value, best: score });
    }
    draw();
  }

  function choose(name) {
    pick.value = games[name] ? name : "runner";
    game = games[pick.value];
    game.reset();
    score = 0;
    state = "ready";
    hint.textContent = game.hint;
    draw();
  }

  // Keys for the game only while nothing else on the page (the link, the list) has them
  addEventListener("keydown", e => {
    if ((e.target !== document.body && e.target !== canvas) || e.ctrlKey || e.altKey || e.metaKey) return;
    const k = e.code;
    if (state !== "play") {
      if (k !== "Space" && k !== "Enter" && !game.keys.includes(k)) return;
      e.preventDefault();
      if (e.repeat) return;
      start();
      if (k !== "Enter" && k !== "Space") game.key(k, true);
      return;
    }
    if (game.key(k, true)) e.preventDefault();
  });
  addEventListener("keyup", e => { if (state === "play") game.key(e.code, false); });
  canvas.addEventListener("pointerdown", e => {
    e.preventDefault();
    canvas.focus();
    if (state !== "play") return start();
    const r = canvas.getBoundingClientRect();
    game.tap((e.clientX - r.left) * W / r.width, (e.clientY - r.top) * H / r.height);
  });
  pick.addEventListener("change", () => {
    choose(pick.value);
    post({ type: "game", pick: pick.value });
    pick.blur();
  });

  // Sharp on any screen: the drawing is in a 640×200 space scaled to the canvas
  function fit() {
    const dpr = devicePixelRatio || 1, w = canvas.clientWidth || W;
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(w * H / W * dpr);
    ctx.setTransform(canvas.width / W, 0, 0, canvas.height / H, 0, 0);
    draw();
  }
  addEventListener("resize", fit);
  matchMedia("(prefers-color-scheme: dark)").addEventListener("change", draw);
  choose(saved.pick);
  fit();
})();
