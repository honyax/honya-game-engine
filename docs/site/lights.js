// ============================================================
//  点光源と、裏通りを漂う光の群れ(Day 52b・53 の実験台が使う)
//  reference/Day52b の Render/PointLight.cs と Demo/LightSwarm.cs の移植。
//  乱数は .NET の new Random(seed)(種を渡したときの Knuth の引き算法)をそのまま移したので、
//  光の位置・色は reference と同じ並びになる
//  使う側: const { attenuation, LightSwarm } = Lights;
// ============================================================
(() => {
'use strict';

// .NET の Random(seed)。NextSingle は Sample() を float に丸めたもの
function dotNetRandom(seed) {
  const MBIG = 2147483647, a = new Array(56).fill(0);
  let mj = 161803398 - Math.abs(seed), mk = 1;
  a[55] = mj;
  for (let i = 1; i < 55; i++) {
    const ii = (21 * i) % 55;
    a[ii] = mk; mk = mj - mk; if (mk < 0) mk += MBIG; mj = a[ii];
  }
  for (let k = 1; k < 5; k++) {
    for (let i = 1; i < 56; i++) { a[i] -= a[1 + ((i + 30) % 55)]; if (a[i] < 0) a[i] += MBIG; }
  }
  let inext = 0, inextp = 21;
  return () => {
    if (++inext >= 56) inext = 1;
    if (++inextp >= 56) inextp = 1;
    let r = a[inext] - a[inextp];
    if (r === MBIG) r--;
    if (r < 0) r += MBIG;
    a[inext] = r;
    return Math.fround(r * (1 / MBIG));
  };
}

// PointLight.Attenuation。窓 = saturate(1 - (d/r)^4)^2、減衰 = 窓 / (d^2 + 1)
function attenuation(d, r) {
  if (r <= 0 || d >= r) return 0;
  const q = d / r, q4 = q * q * q * q, w = Math.min(Math.max(1 - q4, 0), 1);
  return w * w / (d * d + 1);
}
const MAX_FORWARD = 64;

// LightSwarm。位置は時刻の関数で、数を変えると半径・高さ・強さを決め直す
const BOUNDS_MIN = [-3.6, 0.15, -12.5], BOUNDS_MAX = [3.0, 2.6, 7.0];
const COUNT_STEPS = [0, 16, 64, 256, 1024], BASE_RADIUS = 4, BASE_COUNT = 16;
const PALETTE = [[1, 0.55, 0.2], [1, 0.32, 0.12], [1, 0.78, 0.45], [0.3, 0.62, 1], [0.45, 1, 0.55], [0.95, 0.35, 0.85]];
const radiusFor = (n) => (n <= 0 ? BASE_RADIUS : BASE_RADIUS * Math.sqrt(BASE_COUNT / n));
const heightRange = (r) => [BOUNDS_MIN[1], Math.min(Math.max(0.2 + 0.55 * r, 0.45), BOUNDS_MAX[1])];
class LightSwarm {
  constructor(capacity = 1024, seed = 20520101) {
    const rnd = dotNetRandom(seed), T = Math.PI * 2;
    this.seeds = [];
    for (let i = 0; i < capacity; i++) {
      // 揺れ幅ぶん内側に中心を置く(C# と同じ順に乱数を引く)
      const amp = [0.3 + rnd() * 0.9, 0.3 + rnd() * 1.2];
      const lo = [BOUNDS_MIN[0] + amp[0], BOUNDS_MIN[2] + amp[1]], hi = [BOUNDS_MAX[0] - amp[0], BOUNDS_MAX[2] - amp[1]];
      const c = [lo[0] + rnd() * (hi[0] - lo[0]), lo[1] + rnd() * (hi[1] - lo[1])];
      const sp = [0.15 + rnd() * 0.35, 0.3 + rnd() * 0.4, 0.15 + rnd() * 0.35];
      const ph = [rnd() * T, rnd() * T, rnd() * T];
      const k = 0.8 + rnd() * 0.4, p = PALETTE[i % PALETTE.length];
      this.seeds.push({ c, amp, sp, ph, h: rnd(), col: [p[0] * k, p[1] * k, p[2] * k] });
    }
    this.count = 0; this.time = 0; this.intensity = 8; this.lights = [];
    this.setCount(0);
  }
  get radius() { return radiusFor(this.count); }
  setCount(n) { this.count = Math.min(Math.max(n, 0), this.seeds.length); this.evaluate(); }
  update(dt) { this.time += dt; this.evaluate(); }
  setTime(t) { this.time = t; this.evaluate(); }
  evaluate() {
    const r = this.radius, s = this.intensity * (r / BASE_RADIUS), [lo, hi] = heightRange(r), t = this.time;
    const out = [];
    for (let i = 0; i < this.count; i++) {
      const q = this.seeds[i];
      const x = q.c[0] + Math.sin(t * q.sp[0] + q.ph[0]) * q.amp[0];
      const z = q.c[1] + Math.cos(t * q.sp[2] + q.ph[2]) * q.amp[1];
      const bob = Math.min(Math.max(q.h + 0.15 * Math.sin(t * q.sp[1] + q.ph[1]), 0), 1);
      out.push({ pos: [x, lo + (hi - lo) * bob, z], radius: r, color: [q.col[0] * s, q.col[1] * s, q.col[2] * s] });
    }
    this.lights = out;
  }
}

window.Lights = { dotNetRandom, attenuation, MAX_FORWARD, LightSwarm, BOUNDS_MIN, BOUNDS_MAX, COUNT_STEPS, radiusFor, heightRange };
})();
