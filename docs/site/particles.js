// ============================================================
//  パーティクル(reference/Day49 の Render/ParticleEmitter.cs と ParticleTextures.cs を JS に移したもの)
//  Day 49・50 の計画書の実験台が共有する。window.Particles にまとめて置く。
//
//  - Emitter: 撒く・進める・捨てる(端数の持ち越し、swap-remove、指数減衰の抵抗)
//  - softDot / smokeFrame: テクスチャを作らずに、同じ式で1画素ぶんのアルファを出す
//  - 乱数は System.Random の代わりの再現できる乱数。並びは reference と一致しないが、分布は同じ
// ============================================================
(() => {
'use strict';
// 再現できる乱数(System.Random の代わり。並びは一致しないが、分布は同じ)
function rng(seed) {
  let s = seed >>> 0;
  return () => { s = (s + 0x6D2B79F5) >>> 0; let t = s; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}
const v3 = (x, y, z) => [x, y, z];
const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
const mul = (a, k) => [a[0] * k, a[1] * k, a[2] * k];
const norm = (a) => { const l = Math.hypot(a[0], a[1], a[2]); return l > 0 ? mul(a, 1 / l) : a; };
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const lerp = (a, b, t) => a + (b - a) * t;

// ============================================================
//  ParticleEmitter(reference/Day49 の移植)
// ============================================================
class Emitter {
  constructor(capacity, seed) {
    this.capacity = capacity; this.random = rng(seed); this.p = []; this.carry = 0; this.total = 0;
    Object.assign(this, {
      position: v3(0, 0, 0), direction: v3(0, 1, 0), shape: 'cone', coneAngle: 20, radius: 0.1, emitRate: 200, emitting: true,
      lifeMin: 0.8, lifeMax: 1.4, speedMin: 2, speedMax: 3.5, gravity: v3(0, -9.81, 0), drag: 0,
      startSize: 0.25, endSize: 0.05, sizeJitter: 0.3, startColor: [1, 0.75, 0.35, 1], endColor: [0.6, 0.15, 0.05, 0], spinRange: 2, cols: 1, rows: 1,
    });
  }
  get frameCount() { return Math.max(1, this.cols * this.rows); }
  burst(n) { for (let i = 0; i < n; i++) this.spawn(); }
  clear() { this.p.length = 0; this.carry = 0; this.total = 0; }
  update(dt) {
    if (dt <= 0) return;
    if (this.emitting && this.emitRate > 0) {
      // 端数を持ち越す(要点8)
      this.carry += this.emitRate * dt;
      const n = Math.min(Math.trunc(this.carry), this.capacity);
      this.carry -= Math.trunc(this.carry);
      for (let i = 0; i < n; i++) this.spawn();
    }
    // 抵抗は指数減衰(1 - Drag·dt だと dt が跳ねたときに逆走する)
    const damping = this.drag > 0 ? Math.exp(-this.drag * dt) : 1;
    const P = this.p;
    for (let i = 0; i < P.length;) {
      const q = P[i];
      q.age += dt;
      if (q.age >= q.life) {
        // swap-remove。添字を進めない(要点6)
        P[i] = P[P.length - 1]; P.pop(); continue;
      }
      q.vel = mul(add(q.vel, mul(this.gravity, dt)), damping);
      q.pos = add(q.pos, mul(q.vel, dt));
      q.rot += q.spin * dt;
      i++;
    }
  }
  colorAt(t) { return this.startColor.map((s, k) => lerp(s, this.endColor[k], t)); }
  sizeAt(t, seed) { return lerp(this.startSize, this.endSize, t) * (1 + (seed - 0.5) * 2 * this.sizeJitter); }
  frameAt(t) { return Math.min(Math.max(Math.trunc(t * this.frameCount), 0), this.frameCount - 1); }
  spawn() {
    if (this.p.length >= this.capacity) return;
    const R = this.random;
    let offset, dir;
    if (this.shape === 'sphere') { dir = this.randomDirection(); offset = mul(dir, this.radius * R()); }
    else if (this.shape === 'disc') {
      const a = R() * Math.PI * 2, r = this.radius * Math.sqrt(R()), flat = v3(Math.cos(a), 0, Math.sin(a));
      offset = mul(flat, r); dir = norm(add(flat, v3(0, 0.6, 0)));
    } else { offset = mul(this.randomDirection(), this.radius * R()); dir = this.randomInCone(this.direction, this.coneAngle); }
    const speed = lerp(this.speedMin, this.speedMax, R());
    this.p.push({ pos: add(this.position, offset), vel: mul(dir, speed), age: 0, life: Math.max(0.01, lerp(this.lifeMin, this.lifeMax, R())),
      rot: R() * Math.PI * 2, spin: (R() - 0.5) * 2 * this.spinRange, seed: R() });
    this.total++;
  }
  randomDirection() {
    const R = this.random, z = R() * 2 - 1, a = R() * Math.PI * 2, r = Math.sqrt(Math.max(0, 1 - z * z));
    return v3(r * Math.cos(a), r * Math.sin(a), z);
  }
  randomInCone(axis, deg) {
    const R = this.random, f = norm(axis), cosMax = Math.cos(deg * Math.PI / 180);
    const c = 1 - R() * (1 - cosMax), s = Math.sqrt(Math.max(0, 1 - c * c)), phi = R() * Math.PI * 2;
    const helper = Math.abs(f[1]) < 0.99 ? v3(0, 1, 0) : v3(1, 0, 0);
    const right = norm(cross(helper, f)), up = cross(f, right);
    return norm(add(add(mul(f, c), mul(right, s * Math.cos(phi))), mul(up, s * Math.sin(phi))));
  }
}


// ParticleTextures.CreateSoftDot。中心が濃く縁へ (1 - r)² で落ちる
function softDot(tx, ty) { const f = Math.max(0, 1 - Math.hypot(tx, ty)); return f * f; }
// ParticleTextures.CreateSmokeFlipbook の1コマ。t はコマ番号 / (コマ数 - 1)。
// fadeTo はいちばん最後のコマの濃さ(reference は 0.45。0 にすると色のカーブと二重に薄れる)
function smokeFrame(tx, ty, t, fadeTo = 0.45) {
  const radius = 0.35 + 0.65 * t, fade = 1 - (1 - fadeTo) * t, ripple = 0.08 + 0.22 * t;
  const r = Math.hypot(tx, ty), ang = Math.atan2(ty, tx);
  const wobble = Math.sin(ang * 3 + t * 2) * 0.6 + Math.sin(ang * 7 - t * 3) * 0.4;
  const f = Math.max(0, 1 - r / Math.max(0.01, radius * (1 + wobble * ripple)));
  return f * f * fade;
}
// 粒1つのテクスチャのアルファ(コマ送りなら煙、そうでなければ丸)
function texAlpha(emitter, tx, ty, frame, fadeTo) {
  if (emitter.frameCount <= 1) return softDot(tx, ty);
  return smokeFrame(tx, ty, frame / (emitter.frameCount - 1), fadeTo);
}

window.Particles = { rng, v3, add, mul, norm, cross, lerp, Emitter, softDot, smokeFrame, texAlpha };
})();
