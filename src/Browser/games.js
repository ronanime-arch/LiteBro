// The mini-games of the page about a site that does not answer. Nothing runs until a game is played, and the
// games without motion (minesweeper, the card games of cards.js) draw only when something changes. Records and
// the game picked go to the browser (games.txt), which hands them back in window.__litebroGames.
(() => {
  "use strict";
  const W = 640, H = 200, GROUND = 172;
  const saved = window.__litebroGames || { pick: "runner", best: {} };
  const post = m => { try { window.chrome.webview.postMessage(m); } catch { } };

  const box = document.getElementById("games");
  const pick = document.createElement("select");
  pick.id = "gamePick";
  pick.setAttribute("aria-label", "Мини-игра");
  // Score and record above the field, so that they never cover the game
  const hud = document.createElement("div");
  hud.className = "hud";
  const bestText = document.createElement("span"), scoreText = document.createElement("span");
  bestText.className = "muted";
  hud.append(bestText, scoreText);
  const canvas = document.createElement("canvas");
  canvas.tabIndex = 0;
  const hint = document.createElement("p");
  hint.className = "muted";
  box.append(hud, canvas, hint);
  document.body.append(pick);
  const ctx = canvas.getContext("2d");
  const style = getComputedStyle(document.body);
  const ACCENT = "#4d6bfe", HOT = "#e5484d", LED = "#37c46a", AMBER = "#f59f00";

  let game, state = "ready", score = 0, last = 0, frame = 0, begun = 0, FH = H;
  const best = () => saved.best[pick.value] || 0;
  const rand = (a, b) => a + Math.random() * (b - a);
  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
  const seconds = () => (performance.now() - begun) / 1000;

  function rounded(x, y, w, h, r) {
    ctx.beginPath();
    ctx.roundRect(x, y, w, h, r);
  }

  // A data packet: an envelope of the given color
  function packet(x, y, w, h, color) {
    ctx.fillStyle = color;
    rounded(x, y, w, h, 2);
    ctx.fill();
    ctx.strokeStyle = "rgba(255,255,255,.85)";
    ctx.lineWidth = 1.2;
    ctx.beginPath();
    ctx.moveTo(x + 2, y + 2);
    ctx.lineTo(x + w / 2, y + h * .62);
    ctx.lineTo(x + w - 2, y + 2);
    ctx.stroke();
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

  // ---- Pong: the player's paddle on the left, the browser's on the right
  const PADDLE = 44, BALL = 8;
  const pong = {
    hint: "↑ ↓, W S или мышь — ракетка. Отбил — очко, забил браузеру — пять",
    keys: ["ArrowUp", "KeyW", "ArrowDown", "KeyS"],
    reset() {
      this.me = this.ai = (H - PADDLE) / 2; this.up = this.down = false; this.hits = 0;
      this.serve(1);
    },
    serve(dir) {
      const a = rand(-.45, .45);
      this.x = (W - BALL) / 2; this.y = rand(40, H - 40); this.speed = .3; this.wait = 700; this.aim = 0;
      this.vx = dir * this.speed * Math.cos(a); this.vy = this.speed * Math.sin(a);
    },
    key(k, down) {
      if (k === "ArrowUp" || k === "KeyW") this.up = down;
      else if (k === "ArrowDown" || k === "KeyS") this.down = down;
      else return false;
      return true;
    },
    point(x, y) { this.me = clamp(y - PADDLE / 2, 0, H - PADDLE); },
    tap(x, y) { this.point(x, y); },
    hit(paddle, dir) {
      // Off the paddle's middle the ball leaves at a steeper angle, and every hit is a little faster
      const a = clamp((this.y + BALL / 2 - paddle - PADDLE / 2) / (PADDLE / 2), -1, 1) * .9;
      this.speed = Math.min(.8, this.speed * 1.05);
      this.vx = dir * this.speed * Math.cos(a); this.vy = this.speed * Math.sin(a);
    },
    step(dt) {
      if (this.up !== this.down) this.me = clamp(this.me + (this.down ? .45 : -.45) * dt, 0, H - PADDLE);
      // The browser goes after a ball coming its way, not quite as fast as the ball, and now and then off the mark
      const goal = this.vx > 0 ? this.y + BALL / 2 - PADDLE / 2 + this.aim : (H - PADDLE) / 2;
      const reach = (.17 + Math.min(.13, this.hits * .005)) * dt;
      this.ai = clamp(this.ai + clamp(goal - this.ai, -reach, reach), 0, H - PADDLE);
      if ((this.wait -= dt) > 0) return;
      // Small steps, so that a fast ball never jumps over a paddle
      const n = Math.ceil(this.speed * dt / 3), meets = p => this.y + BALL > p && this.y < p + PADDLE;
      for (let i = 0; i < n; i++) {
        this.x += this.vx * dt / n;
        this.y += this.vy * dt / n;
        if (this.y < 0) { this.y = 0; this.vy = Math.abs(this.vy); }
        if (this.y > H - BALL) { this.y = H - BALL; this.vy = -Math.abs(this.vy); }
        if (this.vx < 0 && this.x <= 24 && this.x + BALL >= 16 && meets(this.me)) {
          this.x = 24;
          this.hit(this.me, 1);
          this.hits++;
          score++;
          this.aim = Math.random() < .22 ? (Math.random() < .5 ? -1 : 1) * rand(30, 40) : rand(-14, 14);
        } else if (this.vx > 0 && this.x + BALL >= W - 24 && this.x <= W - 16 && meets(this.ai)) {
          this.x = W - 24 - BALL;
          this.hit(this.ai, -1);
        }
        if (this.x < -BALL) return over_();
        if (this.x > W) { score += 5; return this.serve(-1); }
      }
    },
    draw(c) {
      ctx.fillStyle = c.faint;
      for (let y = 4; y < H; y += 16) ctx.fillRect(W / 2 - 1, y, 2, 8);
      ctx.fillStyle = ACCENT;
      rounded(16, this.me, 8, PADDLE, 3);
      ctx.fill();
      ctx.fillStyle = c.fg;
      rounded(W - 24, this.ai, 8, PADDLE, 3);
      ctx.fill();
      rounded(this.x, this.y, BALL, BALL, 2);
      ctx.fill();
    },
  };

  // ---- Breakout: the ball breaks the packets at the top, the paddle keeps it in play
  const PAD_W = 84, PAD_Y = 184, BRICK_W = 40, BRICK_H = 11, BRICK_COLORS = [HOT, AMBER, LED, ACCENT];
  const breakout = {
    hint: "← →, A D или мышь — ракетка",
    keys: ["ArrowLeft", "KeyA", "ArrowRight", "KeyD"],
    reset() { this.px = (W - PAD_W) / 2; this.left = this.right = false; this.level = 0; this.build(); },
    build() {
      this.bricks = [];
      for (let r = 0; r < 4; r++) for (let i = 0; i < 14; i++) this.bricks.push({ x: 14 + i * 44, y: 14 + r * 15, r });
      this.speed = .3 + this.level * .03; this.wait = 700;
      this.stick();
    },
    stick() { this.bx = this.px + (PAD_W - BALL) / 2; this.by = PAD_Y - BALL; },
    key(k, down) {
      if (k === "ArrowLeft" || k === "KeyA") this.left = down;
      else if (k === "ArrowRight" || k === "KeyD") this.right = down;
      else return false;
      return true;
    },
    point(x) { this.px = clamp(x - PAD_W / 2, 0, W - PAD_W); },
    tap(x) { this.point(x); },
    step(dt) {
      if (this.left !== this.right) this.px = clamp(this.px + (this.right ? .55 : -.55) * dt, 0, W - PAD_W);
      if (this.wait > 0) {
        // The ball rides the paddle a moment before it goes
        this.stick();
        if ((this.wait -= dt) <= 0) {
          const a = rand(-.5, .5);
          this.vx = this.speed * Math.sin(a); this.vy = -this.speed * Math.cos(a);
        }
        return;
      }
      const n = Math.ceil(this.speed * dt / 3);
      for (let i = 0; i < n; i++) {
        const px = this.bx, py = this.by;
        this.bx += this.vx * dt / n;
        this.by += this.vy * dt / n;
        if (this.bx < 0) { this.bx = 0; this.vx = Math.abs(this.vx); }
        if (this.bx > W - BALL) { this.bx = W - BALL; this.vx = -Math.abs(this.vx); }
        if (this.by < 0) { this.by = 0; this.vy = Math.abs(this.vy); }
        if (this.vy > 0 && this.by + BALL >= PAD_Y && py + BALL <= PAD_Y + 1 && this.bx + BALL > this.px && this.bx < this.px + PAD_W) {
          const a = clamp((this.bx + BALL / 2 - this.px - PAD_W / 2) / (PAD_W / 2), -1, 1) * 1.05;
          this.by = PAD_Y - BALL;
          this.vx = this.speed * Math.sin(a); this.vy = -this.speed * Math.cos(a);
        }
        const b = this.bricks.find(b => this.bx + BALL > b.x && this.bx < b.x + BRICK_W && this.by + BALL > b.y && this.by < b.y + BRICK_H);
        if (b) {
          this.bricks.splice(this.bricks.indexOf(b), 1);
          score += 4 - b.r;
          // From above or below it bounces back up or down, from the side sideways
          if (px + BALL > b.x && px < b.x + BRICK_W) this.vy = -this.vy;
          else this.vx = -this.vx;
          this.speed = Math.min(.55, this.speed + .003);
          const k = this.speed / Math.hypot(this.vx, this.vy);
          this.vx *= k; this.vy *= k;
          if (!this.bricks.length) { this.level++; return this.build(); }
        }
        if (this.by > H) return over_();
      }
    },
    draw(c) {
      for (const b of this.bricks) packet(b.x, b.y, BRICK_W, BRICK_H, BRICK_COLORS[b.r]);
      ctx.fillStyle = c.fg;
      rounded(this.px, PAD_Y, PAD_W, 8, 4);
      ctx.fill();
      ctx.fillStyle = ACCENT;
      ctx.beginPath();
      ctx.arc(this.bx + BALL / 2, this.by + BALL / 2, BALL / 2, 0, 7);
      ctx.fill();
    },
  };

  // ---- Invaders: the cannon at the bottom shoots the rows of viruses closing in
  const FOE_W = 24, FOE_H = 16, GUN_Y = 182, GUN_W = 26, FOE_COLORS = [HOT, AMBER, LED];
  function virus(x, y, color, turn, c) {
    ctx.strokeStyle = ctx.fillStyle = color;
    ctx.lineWidth = 2;
    for (let k = 0; k < 8; k++) {
      const a = (k + turn / 2) * Math.PI / 4, cos = Math.cos(a), sin = Math.sin(a);
      ctx.beginPath();
      ctx.moveTo(x + cos * 5, y + sin * 5);
      ctx.lineTo(x + cos * 8.5, y + sin * 8.5);
      ctx.stroke();
      ctx.beginPath();
      ctx.arc(x + cos * 8.5, y + sin * 8.5, 1.5, 0, 7);
      ctx.fill();
    }
    ctx.beginPath();
    ctx.arc(x, y, 6, 0, 7);
    ctx.fill();
    ctx.fillStyle = c.bg;
    ctx.fillRect(x - 3.5, y - 2, 2.5, 2.5);
    ctx.fillRect(x + 1, y - 2, 2.5, 2.5);
  }
  const invaders = {
    hint: "← →, A D или мышь — пушка; пробел, ↑ или клик — выстрел",
    keys: ["ArrowLeft", "KeyA", "ArrowRight", "KeyD", "Space", "ArrowUp", "KeyW"],
    reset() { this.x = (W - GUN_W) / 2; this.left = this.right = this.fire = false; this.wave = 0; this.t = 0; this.build(); },
    build() {
      this.foes = [];
      for (let r = 0; r < 3; r++) for (let i = 0; i < 10; i++) this.foes.push({ x: 40 + i * 36, y: 12 + r * 22, r });
      this.dir = 1; this.shots = []; this.bombs = []; this.next = 900; this.cool = 0;
    },
    key(k, down) {
      if (k === "ArrowLeft" || k === "KeyA") this.left = down;
      else if (k === "ArrowRight" || k === "KeyD") this.right = down;
      else if (k === "Space" || k === "ArrowUp" || k === "KeyW") this.fire = down;
      else return false;
      return true;
    },
    point(x) { this.x = clamp(x - GUN_W / 2, 0, W - GUN_W); },
    tap(x) { this.point(x); this.shoot(); },
    shoot() {
      if (this.cool > 0 || this.shots.length >= 2) return;
      this.shots.push({ x: this.x + GUN_W / 2 - 1, y: GUN_Y - 14 });
      this.cool = 280;
    },
    step(dt) {
      this.t += dt;
      this.cool -= dt;
      if (this.left !== this.right) this.x = clamp(this.x + (this.right ? .32 : -.32) * dt, 0, W - GUN_W);
      if (this.fire) this.shoot();
      // The fewer viruses are left, the faster they go; at an edge they all step down
      const pace = (.025 + this.wave * .008) * (1 + (30 - this.foes.length) * .09) * dt;
      let edge = false;
      for (const f of this.foes) {
        f.x += this.dir * pace;
        if (f.x < 6 || f.x + FOE_W > W - 6) edge = true;
      }
      if (edge) {
        this.dir = -this.dir;
        for (const f of this.foes) { f.x += this.dir * pace; f.y += 10; }
      }
      for (const s of this.shots) s.y -= .5 * dt;
      for (const b of this.bombs) b.y += (.16 + this.wave * .02) * dt;
      this.shots = this.shots.filter(s => {
        if (s.y < -10) return false;
        const f = this.foes.find(f => s.x + 2 > f.x && s.x < f.x + FOE_W && s.y < f.y + FOE_H && s.y + 8 > f.y);
        if (!f) return true;
        this.foes.splice(this.foes.indexOf(f), 1);
        score += 3 - f.r;
        return false;
      });
      this.bombs = this.bombs.filter(b => b.y < H);
      if ((this.next -= dt) <= 0 && this.foes.length) {
        // The lowest virus of a column drops a bomb
        const f = this.foes[Math.floor(rand(0, this.foes.length))];
        const low = this.foes.filter(o => Math.abs(o.x - f.x) < 4).reduce((a, o) => o.y > a.y ? o : a, f);
        this.bombs.push({ x: low.x + FOE_W / 2 - 1.5, y: low.y + FOE_H });
        this.next = rand(450, 1300) / (1 + this.wave * .25);
      }
      for (const b of this.bombs)
        if (b.x + 3 > this.x && b.x < this.x + GUN_W && b.y + 8 > GUN_Y - 6 && b.y < GUN_Y + 8) return over_();
      if (this.foes.some(f => f.y + FOE_H >= GUN_Y - 8)) return over_();
      if (!this.foes.length) { this.wave++; this.build(); }
    },
    draw(c) {
      ctx.strokeStyle = c.dim;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(0, GUN_Y + 10.5);
      ctx.lineTo(W, GUN_Y + 10.5);
      ctx.stroke();
      const turn = Math.floor((this.t || 0) / 350) % 2;
      for (const f of this.foes) virus(f.x + FOE_W / 2, f.y + FOE_H / 2, FOE_COLORS[f.r], turn, c);
      ctx.fillStyle = ACCENT;
      rounded(this.x, GUN_Y - 2, GUN_W, 10, 3);
      ctx.fill();
      ctx.fillRect(this.x + GUN_W / 2 - 2, GUN_Y - 8, 4, 7);
      ctx.fillStyle = c.fg;
      for (const s of this.shots) ctx.fillRect(s.x, s.y, 2, 8);
      ctx.fillStyle = HOT;
      for (const b of this.bombs) ctx.fillRect(b.x, b.y, 3, 8);
    },
  };

  // ---- Minesweeper: open the cells without hitting a bug; a number counts the bugs around it
  const MC = 32, MR = 10, MS = 20, BUGS = 40;
  const NUM = ["", ACCENT, "#2f9e44", HOT, "#8b5cf6", "#c2410c", "#0e7490", "#868e96", "#868e96"];
  function bug(x, y, color) {
    ctx.strokeStyle = ctx.fillStyle = color;
    ctx.lineWidth = 1.3;
    ctx.beginPath();
    for (const dy of [-3, 0, 3]) {
      ctx.moveTo(x - 7, y + dy - 1);
      ctx.lineTo(x + 7, y + dy + 1);
    }
    ctx.stroke();
    ctx.beginPath();
    ctx.ellipse(x, y + 1, 4, 5, 0, 0, 7);
    ctx.fill();
    ctx.beginPath();
    ctx.arc(x, y - 5, 2.5, 0, 7);
    ctx.fill();
  }
  function flag(x, y, wrong, c) {
    ctx.strokeStyle = c.fg;
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.moveTo(x + 7.5, y + 4);
    ctx.lineTo(x + 7.5, y + 16);
    ctx.stroke();
    ctx.fillStyle = HOT;
    ctx.beginPath();
    ctx.moveTo(x + 8, y + 4);
    ctx.lineTo(x + 15, y + 7.5);
    ctx.lineTo(x + 8, y + 11);
    ctx.fill();
    if (!wrong) return;
    // A flag where there was no bug
    ctx.beginPath();
    ctx.moveTo(x + 3, y + 3);
    ctx.lineTo(x + 17, y + 17);
    ctx.moveTo(x + 17, y + 3);
    ctx.lineTo(x + 3, y + 17);
    ctx.stroke();
  }
  const mines = {
    hint: "Клик — открыть клетку, правый клик — пометить баг, N — новое поле",
    keys: ["KeyN"],
    still: true, tapStarts: true,
    reset() {
      this.cells = Array.from({ length: MC * MR }, () => ({ bug: false, open: false, flag: false, n: 0 }));
      this.laid = false; this.opened = 0; this.boom = -1; this.won = false;
    },
    key(k, down) {
      if (k !== "KeyN") return false;
      if (down) start();
      return true;
    },
    near(i, f) {
      const x = i % MC, y = (i / MC) | 0;
      for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
        const nx = x + dx, ny = y + dy;
        if ((dx || dy) && nx >= 0 && ny >= 0 && nx < MC && ny < MR) f(ny * MC + nx);
      }
    },
    lay(first) {
      // Never on the first cell opened nor around it, so that the game starts with an opening
      const safe = new Set([first]);
      this.near(first, j => safe.add(j));
      for (let left = BUGS; left;) {
        const i = Math.floor(rand(0, MC * MR));
        if (!safe.has(i) && !this.cells[i].bug) { this.cells[i].bug = true; left--; }
      }
      this.cells.forEach((cell, i) => this.near(i, j => { if (this.cells[j].bug) cell.n++; }));
      this.laid = true;
    },
    open(i) {
      for (const todo = [i]; todo.length;) {
        const j = todo.pop(), cell = this.cells[j];
        if (cell.open || cell.flag) continue;
        cell.open = true;
        if (cell.bug) { this.boom = j; continue; }
        this.opened++;
        if (!cell.n) this.near(j, k => todo.push(k));
      }
    },
    tap(x, y, button) {
      const cx = Math.floor(x / MS), cy = Math.floor(y / MS);
      if (cx < 0 || cy < 0 || cx >= MC || cy >= MR) return;
      const i = cy * MC + cx, cell = this.cells[i];
      if (button === 2) {
        if (!cell.open) cell.flag = !cell.flag;
        return draw();
      }
      if (button !== 0 || cell.flag) return;
      if (!this.laid) this.lay(i);
      if (!cell.open) this.open(i);
      else if (cell.n) {
        // An open number with as many flags around opens the rest around it
        let flags = 0;
        this.near(i, j => { if (this.cells[j].flag) flags++; });
        if (flags === cell.n) this.near(i, j => this.open(j));
      }
      score = this.opened;
      if (this.boom >= 0) return over_();
      if (this.opened === MC * MR - BUGS) {
        this.won = true;
        score += Math.max(0, 1200 - Math.round(seconds()));
        return over_();
      }
      draw();
    },
    step() { },
    draw(c) {
      ctx.font = "700 13px Consolas, monospace";
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      this.cells.forEach((cell, i) => {
        const x = (i % MC) * MS, y = ((i / MC) | 0) * MS;
        if (!cell.open && !(state === "over" && cell.bug && !cell.flag)) {
          ctx.fillStyle = c.closed;
          rounded(x + 1, y + 1, MS - 2, MS - 2, 3);
          ctx.fill();
          if (cell.flag) flag(x, y, state === "over" && !cell.bug, c);
          return;
        }
        if (i === this.boom) {
          ctx.fillStyle = HOT;
          rounded(x + 1, y + 1, MS - 2, MS - 2, 3);
          ctx.fill();
        }
        if (cell.bug) bug(x + MS / 2, y + MS / 2 + 1, i === this.boom ? "#fff" : c.fg);
        else if (cell.n) {
          ctx.fillStyle = NUM[cell.n];
          ctx.fillText(cell.n, x + MS / 2, y + MS / 2 + 1);
        }
      });
    },
  };

  const games = { runner, snake, flappy, pong, breakout, invaders, mines };
  const names = [["runner", "Раннер-штекер"], ["snake", "Змейка"], ["flappy", "Летающий пакет"], ["pong", "Понг"],
    ["breakout", "Арканоид"], ["invaders", "Космические захватчики"], ["mines", "Сапёр"]];
  // The card games of cards.js, with what they share with these
  if (window.__litebroCards) {
    const cards = window.__litebroCards({
      ctx, W, rounded, ACCENT,
      get score() { return score; },
      set score(n) { score = n; },
      draw: () => draw(), over: () => over_(), restart: () => start(), seconds,
    });
    delete window.__litebroCards;
    for (const [name, title, g] of cards) {
      games[name] = g;
      names.push([name, title]);
    }
  }
  for (const [value, name] of names) pick.append(new Option(name, value));

  function colors() {
    return {
      fg: style.color, bg: style.backgroundColor, dim: "rgba(127,127,127,.55)", faint: "rgba(127,127,127,.25)",
      closed: "rgba(127,127,127,.3)",
    };
  }

  const pad = n => String(n).padStart(5, "0");
  const show = (el, text) => { if (el.textContent !== text) el.textContent = text; };

  function draw() {
    const c = colors();
    ctx.clearRect(0, 0, W, FH);
    game.draw(c);
    // A game without motion may set a record at any move
    if (game.still && state === "play") record();
    show(bestText, "Рекорд " + pad(best()));
    show(scoreText, pad(score));
    if (state === "play") return;
    const y = FH / 2;
    ctx.fillStyle = c.bg;
    ctx.globalAlpha = .85;
    rounded(W / 2 - 180, y - 47, 360, 62, 10);
    ctx.fill();
    ctx.globalAlpha = 1;
    ctx.fillStyle = c.fg;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.font = "600 17px 'Segoe UI', sans-serif";
    ctx.fillText(state === "ready" ? pick.options[pick.selectedIndex].text : game.won ? "Победа" : "Игра окончена", W / 2, y - 30);
    ctx.font = "14px 'Segoe UI', sans-serif";
    ctx.globalAlpha = .75;
    ctx.fillText(state === "over" ? "Пробел или клик — ещё раз" : "Пробел или клик — играть", W / 2, y - 4);
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
    if (state !== "ready") game.reset();
    score = 0;
    last = 0;
    begun = performance.now();
    state = "play";
    if (game.still) draw();
    else if (!frame) frame = requestAnimationFrame(loop);
  }

  function record() {
    if (score <= best()) return;
    saved.best[pick.value] = score;
    post({ type: "game", game: pick.value, best: score });
  }

  function over_() {
    state = "over";
    record();
    draw();
  }

  function choose(name) {
    pick.value = games[name] ? name : "runner";
    game = games[pick.value];
    game.reset();
    score = 0;
    state = "ready";
    hint.textContent = game.hint;
    FH = game.h || H;
    canvas.style.aspectRatio = W + " / " + FH;
    fit();
  }

  // Keys for the game only while nothing else on the page (the link, the list) has them
  addEventListener("keydown", e => {
    if ((e.target !== document.body && e.target !== canvas) || e.ctrlKey || e.altKey || e.metaKey) return;
    const k = e.code;
    if (state !== "play") {
      if (k !== "Space" && k !== "Enter" && (game.still || !game.keys.includes(k))) return;
      e.preventDefault();
      if (e.repeat) return;
      start();
      if (k !== "Enter" && k !== "Space") game.key(k, true);
      return;
    }
    // Space never scrolls the page under a game
    if ((game.keys.includes(k) && game.key(k, true)) || k === "Space") e.preventDefault();
  });
  addEventListener("keyup", e => { if (state === "play" && game.keys.includes(e.code)) game.key(e.code, false); });
  // Keys held when the window loses focus never come up
  addEventListener("blur", () => { if (state === "play" && !game.still) for (const k of game.keys) game.key(k, false); });

  const at = e => {
    const r = canvas.getBoundingClientRect();
    return [(e.clientX - r.left) * W / r.width, (e.clientY - r.top) * FH / r.height];
  };
  canvas.addEventListener("pointerdown", e => {
    e.preventDefault();
    canvas.focus();
    if (e.button !== 0 && e.button !== 2) return;
    if (state !== "play") {
      start();
      if (game.tapStarts && e.button === 0) game.tap(...at(e), 0);
      return;
    }
    if (e.button !== 0 && !game.still) return;
    canvas.setPointerCapture(e.pointerId);
    game.tap(...at(e), e.button);
  });
  canvas.addEventListener("pointermove", e => { if (state === "play" && game.point) game.point(...at(e)); });
  canvas.addEventListener("pointerup", e => { if (state === "play" && game.release) game.release(...at(e)); });
  canvas.addEventListener("contextmenu", e => e.preventDefault());
  pick.addEventListener("change", () => {
    choose(pick.value);
    post({ type: "game", pick: pick.value });
    pick.blur();
  });

  // Sharp on any screen: the drawing is in a 640-wide space scaled to the canvas
  function fit() {
    const dpr = devicePixelRatio || 1, w = canvas.clientWidth || W;
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(w * FH / W * dpr);
    ctx.setTransform(canvas.width / W, 0, 0, canvas.height / FH, 0, 0);
    draw();
  }
  addEventListener("resize", fit);
  matchMedia("(prefers-color-scheme: dark)").addEventListener("change", draw);
  choose(saved.pick);
})();
