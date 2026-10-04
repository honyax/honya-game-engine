// ============================================================
//  2D 剛体物理(reference/Day44a〜47 の Physics/ を横から見た 2D に移したもの)
//  Day 44a〜47 の計画書の実験台が共有する。window.Phys2D にまとめて置く。
//
//  - 形: 円(球として慣性を持つ)/ 箱 / カプセル(軸は物体座標の y)/ 平面
//  - 判定は「A を B から引き離す向き」の法線と、最大4点のマニフォールドを返す
//  - 世界の1ステップは reference と同じ5段(積分 → 判定 → 速度 → 位置 → 溜め場)
//  - 2D なので回転は z 軸まわりの角度1つ。箱どうしの分離軸は 4 本(3D は 15 本)
//  - world.solver = 'day47' にすると Day 47 の8段(摩擦・蓄積インパルス・温存・眠り)で解く。
//    既定は 'day44' で、Day 44a〜46b の実験台はこれまでどおりの5段で動く。
//    2D なので接線は1本(3D は2本で、摩擦円で切る)
// ============================================================
(() => {
'use strict';

const MAX_POINTS = 4;

// ---------------- 形 ----------------
const Shape = {
  circle: (r) => ({ kind: 'circle', r }),
  box: (hx, hy) => ({ kind: 'box', hx, hy }),
  capsule: (r, hh) => ({ kind: 'capsule', r, hh }),
  plane: () => ({ kind: 'plane' }),
};

// z 軸まわりの慣性。3D の reference と同じ式の、横から見た軸の値
function inertia(shape, m) {
  switch (shape.kind) {
    case 'circle': return 0.4 * m * shape.r * shape.r;                       // 球の 2/5 m r²
    case 'box': return (m / 3) * (shape.hx * shape.hx + shape.hy * shape.hy); // 1/12 m (w² + h²)
    case 'capsule': {
      // 円柱 + 半球2つを平行軸の定理で足す(Day 45a 要点5)
      const r = shape.r, L = 2 * shape.hh;
      const vc = Math.PI * r * r * L, vs = (4 / 3) * Math.PI * r * r * r;
      const mc = m * vc / (vc + vs), ms = m * vs / (vc + vs);
      return mc * (L * L / 12 + r * r / 4) + ms * (0.4 * r * r + L * L / 4 + 0.375 * L * r);
    }
    default: return 0;
  }
}

class Body {
  constructor(mass, shape) {
    this.shape = shape;
    const I = mass > 0 ? inertia(shape, mass) : 0;
    this.invMass = mass > 0 ? 1 / mass : 0;
    this.invI = I > 0 ? 1 / I : 0;
    this.x = 0; this.y = 0; this.angle = 0;
    this.vx = 0; this.vy = 0; this.w = 0;
    this.px = 0; this.py = 0; this.pa = 0;
    this.fx = 0; this.fy = 0; this.torque = 0;
    this.restitution = 0.25;
    this.linearDamping = 0;
    this.angularDamping = 0;
    this.normal = [0, 1];   // 平面の法線
    // Day 47: 摩擦係数と眠り(world.solver = 'day47' のときだけ使う)
    this.friction = 0.5;
    this.sleeping = false;
    this.sleepTimer = 0;
    this.allowSleep = true;
  }
  // 眠っている間は「一時的に無限に重い」= 静的な体と同じに見せる(RigidBody.SolverInverseMass)
  get solverInvMass() { return this.sleeping ? 0 : this.invMass; }
  get solverInvI() { return this.sleeping ? 0 : this.invI; }
  wake() { this.sleeping = false; this.sleepTimer = 0; }
  sleep() { this.sleeping = true; this.vx = 0; this.vy = 0; this.w = 0; }
  static plane(px, py, nx, ny) {
    const b = new Body(0, Shape.plane());
    b.x = px; b.y = py; b.normal = [nx, ny];
    return b;
  }
  get isStatic() { return this.invMass <= 0; }
  get mass() { return this.invMass > 0 ? 1 / this.invMass : Infinity; }
  get kineticEnergy() {
    if (this.isStatic) return 0;
    const I = this.invI > 0 ? 1 / this.invI : 0;
    return 0.5 * this.mass * (this.vx * this.vx + this.vy * this.vy) + 0.5 * I * this.w * this.w;
  }
  axis(i) { const c = Math.cos(this.angle), s = Math.sin(this.angle); return i === 0 ? [c, s] : [-s, c]; }
  extent(i) { return i === 0 ? this.shape.hx : this.shape.hy; }
  toWorld(lx, ly) { const c = Math.cos(this.angle), s = Math.sin(this.angle); return [this.x + c * lx - s * ly, this.y + s * lx + c * ly]; }
  toLocal(wx, wy) { const c = Math.cos(this.angle), s = Math.sin(this.angle), dx = wx - this.x, dy = wy - this.y; return [c * dx + s * dy, -s * dx + c * dy]; }
  corners() { const { hx, hy } = this.shape; return [[-hx, -hy], [hx, -hy], [hx, hy], [-hx, hy]].map(([x, y]) => this.toWorld(x, y)); }
  segment() { const a = this.axis(1), h = this.shape.hh; return [[this.x - a[0] * h, this.y - a[1] * h], [this.x + a[0] * h, this.y + a[1] * h]]; }
  velocityAt(px, py) { const rx = px - this.x, ry = py - this.y; return [this.vx - this.w * ry, this.vy + this.w * rx]; }
  applyImpulseAtPoint(jx, jy, px, py) {
    if (this.sleeping) return;
    const rx = px - this.x, ry = py - this.y;
    this.vx += jx * this.invMass; this.vy += jy * this.invMass;
    this.w += this.invI * (rx * jy - ry * jx);
  }
  integrateVelocity(dt, gx, gy) {
    if (this.isStatic || this.sleeping) return;
    this.vx += (this.fx * this.invMass + gx) * dt;
    this.vy += (this.fy * this.invMass + gy) * dt;
    this.w += this.torque * this.invI * dt;
    if (this.linearDamping > 0) { const k = Math.pow(1 - this.linearDamping, dt); this.vx *= k; this.vy *= k; }
    if (this.angularDamping > 0) this.w *= Math.pow(1 - this.angularDamping, dt);
  }
  integratePosition(dt) {
    this.px = this.x; this.py = this.y; this.pa = this.angle;
    if (this.isStatic || this.sleeping) return;
    this.x += this.vx * dt; this.y += this.vy * dt; this.angle += this.w * dt;
  }
}

// ---------------- マニフォールド ----------------
function manifold(nx, ny, source) { return { hit: false, normal: [nx, ny], points: [], source }; }
// 4点まで。あふれたら最も浅いものと入れ替える(ContactManifold.Add)
function addPoint(m, x, y, depth) {
  m.hit = true;
  if (m.points.length < MAX_POINTS) { m.points.push({ x, y, depth }); return; }
  let s = 0;
  for (let i = 1; i < m.points.length; i++) if (m.points[i].depth < m.points[s].depth) s = i;
  if (depth > m.points[s].depth) m.points[s] = { x, y, depth };
}
// 深い順に limit 個だけ残す(ContactManifold.Reduce)
function reduce(m, limit) {
  if (m.points.length <= limit) return;
  m.points.sort((a, b) => b.depth - a.depth);
  m.points.length = limit;
}
function maxDepth(m) { return m.points.reduce((d, p) => Math.max(d, p.depth), 0); }
function flip(m) { m.normal = [-m.normal[0], -m.normal[1]]; return m; }
const pairKey = (a, b) => (a < b ? `${a},${b}` : `${b},${a}`);
// 箱と箱 / 箱と平面(3D なら面と面で4点になる組)
const depthOf = (b) => (b.shape.kind === 'box' ? b.shape.hx : 0);
const isFace = (a, b) => {
  const ka = a.shape.kind, kb = b.shape.kind;
  return (ka === 'box' && (kb === 'box' || kb === 'plane')) || (kb === 'box' && ka === 'plane');
};

// ---------------- 幾何の道具 ----------------
const dot = (a, b) => a[0] * b[0] + a[1] * b[1];
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
function closestOnSegment(p, a, b) {
  const d = [b[0] - a[0], b[1] - a[1]], len2 = dot(d, d);
  const t = len2 > 1e-12 ? clamp(((p[0] - a[0]) * d[0] + (p[1] - a[1]) * d[1]) / len2, 0, 1) : 0;
  return [a[0] + d[0] * t, a[1] + d[1] * t];
}
// 線分どうしの最近接点(Ericson 5.1.9)。recompute=false で「t を切り詰めたら s を求め直す」を省く
function segmentClosest(p0, p1, q0, q1, recompute = true) {
  const d1 = [p1[0] - p0[0], p1[1] - p0[1]], d2 = [q1[0] - q0[0], q1[1] - q0[1]], r = [p0[0] - q0[0], p0[1] - q0[1]];
  const a = dot(d1, d1), e = dot(d2, d2), f = dot(d2, r);
  let s, t;
  if (a <= 1e-12 && e <= 1e-12) { s = 0; t = 0; }
  else if (a <= 1e-12) { s = 0; t = clamp(f / e, 0, 1); }
  else {
    const c = dot(d1, r);
    if (e <= 1e-12) { t = 0; s = clamp(-c / a, 0, 1); }
    else {
      const b = dot(d1, d2), den = a * e - b * b;
      s = den > 1e-12 ? clamp((b * f - c * e) / den, 0, 1) : 0;
      t = (b * s + f) / e;
      if (t < 0) { t = 0; if (recompute) s = clamp(-c / a, 0, 1); }
      else if (t > 1) { t = 1; if (recompute) s = clamp((b - c) / a, 0, 1); }
    }
  }
  return { s, t, p: [p0[0] + d1[0] * s, p0[1] + d1[1] * s], q: [q0[0] + d2[0] * t, q0[1] + d2[1] * t] };
}
// 線分と箱の最近接点。距離²は t について凸なので三分探索で詰める(Day 45a 要点3)
function segmentBoxClosest(box, a, b, iterations = 32) {
  const la = box.toLocal(a[0], a[1]), lb = box.toLocal(b[0], b[1]);
  const { hx, hy } = box.shape;
  const at = (t) => [la[0] + (lb[0] - la[0]) * t, la[1] + (lb[1] - la[1]) * t];
  const d2 = (t) => { const p = at(t), q = [clamp(p[0], -hx, hx), clamp(p[1], -hy, hy)]; return (p[0] - q[0]) ** 2 + (p[1] - q[1]) ** 2; };
  let lo = 0, hi = 1;
  for (let i = 0; i < iterations; i++) {
    const third = (hi - lo) / 3, m1 = lo + third, m2 = hi - third;
    if (d2(m1) < d2(m2)) hi = m2; else lo = m1;
  }
  const t = (lo + hi) / 2, p = at(t), q = [clamp(p[0], -hx, hx), clamp(p[1], -hy, hy)];
  return { t, seg: box.toWorld(p[0], p[1]), box: box.toWorld(q[0], q[1]), dist: Math.sqrt(d2(t)) };
}

// ---------------- 判定(法線は A を B から引き離す向き) ----------------
function circleCircle(ca, ra, cb, rb) {
  const dx = ca[0] - cb[0], dy = ca[1] - cb[1], rs = ra + rb, sq = dx * dx + dy * dy;
  if (sq >= rs * rs) return manifold(0, 1, 'sphere');
  const d = Math.sqrt(sq), n = d > 1e-6 ? [dx / d, dy / d] : [0, 1];
  const m = manifold(n[0], n[1], 'sphere'), depth = rs - d, k = ra - depth * 0.5;
  addPoint(m, ca[0] - n[0] * k, ca[1] - n[1] * k, depth);
  return m;
}
function circlePlane(c, r, plane) {
  const n = plane.normal, dist = (c[0] - plane.x) * n[0] + (c[1] - plane.y) * n[1];
  const m = manifold(n[0], n[1], 'faceB');
  if (dist < r) addPoint(m, c[0] - n[0] * dist, c[1] - n[1] * dist, r - dist);
  return m;
}
function boxPlane(box, plane) {
  const n = plane.normal, m = manifold(n[0], n[1], 'faceB');
  for (const c of box.corners()) {
    const d = (c[0] - plane.x) * n[0] + (c[1] - plane.y) * n[1];
    // 頂点と、その真上の面上の点の中点に置く
    if (d < 0) addPoint(m, c[0] - n[0] * d * 0.5, c[1] - n[1] * d * 0.5, -d);
  }
  return m;
}
function capsulePlane(cap, plane) {
  const m = manifold(plane.normal[0], plane.normal[1], 'faceB');
  for (const e of cap.segment()) {
    const p = circlePlane(e, cap.shape.r, plane);
    if (p.hit) addPoint(m, p.points[0].x, p.points[0].y, p.points[0].depth);
  }
  return m;
}
// 円(中心 c, 半径 r)と箱。箱の物体座標で切り詰めるだけ。中に入ったら余裕のいちばん少ない面へ
function circleBox(c, r, box) {
  const l = box.toLocal(c[0], c[1]), { hx, hy } = box.shape;
  const q = [clamp(l[0], -hx, hx), clamp(l[1], -hy, hy)];
  const dx = l[0] - q[0], dy = l[1] - q[1], sq = dx * dx + dy * dy;
  let nl, depth, pl;
  if (sq > 1e-12) {
    if (sq >= r * r) return manifold(0, 1, 'sphere');
    const d = Math.sqrt(sq); nl = [dx / d, dy / d]; depth = r - d; pl = q;
  } else {
    const gx = hx - Math.abs(l[0]), gy = hy - Math.abs(l[1]);
    if (gx < gy) { nl = [Math.sign(l[0]) || 1, 0]; depth = r + gx; pl = [nl[0] * hx, l[1]]; }
    else { nl = [0, Math.sign(l[1]) || 1]; depth = r + gy; pl = [l[0], nl[1] * hy]; }
  }
  const c0 = Math.cos(box.angle), s0 = Math.sin(box.angle);
  const n = [c0 * nl[0] - s0 * nl[1], s0 * nl[0] + c0 * nl[1]];
  const surf = box.toWorld(pl[0], pl[1]);
  const m = manifold(n[0], n[1], 'sphere');
  // 重なりの真ん中 = 箱の表面から法線の逆へ深さの半分
  addPoint(m, surf[0] - n[0] * depth * 0.5, surf[1] - n[1] * depth * 0.5, depth);
  return m;
}
// 箱と箱: 分離軸 4 本 + 面のクリップ(Sat.BoxBox を 2D に)
function sat(a, b, detail) {
  const toCenter = [b.x - a.x, b.y - a.y];
  const pr = (box, n) => Math.abs(box.shape.hx * dot(box.axis(0), n)) + Math.abs(box.shape.hy * dot(box.axis(1), n));
  let best = Infinity, bestIndex = -1, bestAxis = null;
  const axes = [a.axis(0), a.axis(1), b.axis(0), b.axis(1)];
  const tried = [];
  for (let i = 0; i < 4; i++) {
    const n = axes[i];
    const overlap = pr(a, n) + pr(b, n) - Math.abs(dot(toCenter, n));
    tried.push({ axis: n, overlap, owner: i < 2 ? 'A' : 'B' });
    if (overlap <= 0) { if (detail) detail.axes = tried; return manifold(0, 1, 'none'); }
    if (overlap < best) { best = overlap; bestIndex = i; bestAxis = n; }
  }
  let normal = bestAxis.slice();
  if (dot(normal, toCenter) > 0) normal = [-normal[0], -normal[1]];
  const m = bestIndex < 2
    ? faceContact(a, b, normal, [-normal[0], -normal[1]], 'faceA', detail)
    : faceContact(b, a, normal, normal, 'faceB', detail);
  if (detail) { detail.axes = tried; detail.best = bestIndex; }
  return m;
}
function mostAligned(box, dir) {
  const d0 = dot(box.axis(0), dir), d1 = dot(box.axis(1), dir);
  const i = Math.abs(d1) > Math.abs(d0) ? 1 : 0;
  return { i, sign: dot(box.axis(i), dir) >= 0 ? 1 : -1 };
}
function clipSide(poly, n, offset) {
  const out = [];
  for (let i = 0; i < poly.length; i++) {
    const c = poly[i], nx = poly[(i + 1) % poly.length];
    const dc = dot(n, c) - offset, dn = dot(n, nx) - offset;
    if (dc <= 0) out.push(c);
    if (dc * dn < 0 && poly.length > 1) { const t = dc / (dc - dn); out.push([c[0] + (nx[0] - c[0]) * t, c[1] + (nx[1] - c[1]) * t]); }
  }
  return out;
}
function faceContact(ref, inc, normal, refOutward, source, detail) {
  const r = mostAligned(ref, refOutward);
  const refN = ref.axis(r.i).map((v) => v * r.sign);
  const refCenter = [ref.x + refN[0] * ref.extent(r.i), ref.y + refN[1] * ref.extent(r.i)];
  const g = mostAligned(inc, refN);
  const incN = inc.axis(g.i).map((v) => v * -g.sign);
  const incCenter = [inc.x + incN[0] * inc.extent(g.i), inc.y + incN[1] * inc.extent(g.i)];
  const u = 1 - g.i, eu = inc.axis(u).map((v) => v * inc.extent(u));
  let poly = [[incCenter[0] - eu[0], incCenter[1] - eu[1]], [incCenter[0] + eu[0], incCenter[1] + eu[1]]];
  const incident = poly.map((p) => p.slice());
  // 基準面の側面2枚で切る(Sutherland-Hodgman。2D では線分を2回切るだけ)
  const side = 1 - r.i, sa = ref.axis(side), ext = ref.extent(side), center = dot([ref.x, ref.y], sa);
  poly = clipSegment(poly, sa, center + ext);
  if (poly.length) poly = clipSegment(poly, sa.map((v) => -v), -(center - ext));
  const m = manifold(normal[0], normal[1], source);
  const off = dot(refCenter, refN);
  for (const p of poly) {
    const sep = dot(p, refN) - off;
    if (sep > 0) continue;
    addPoint(m, p[0] - refN[0] * sep * 0.5, p[1] - refN[1] * sep * 0.5, -sep);
  }
  if (!m.points.length) {
    let deepest = null, dsep = Infinity;
    for (const c of inc.corners()) { const s = dot(c, refN) - off; if (s < dsep) { dsep = s; deepest = c; } }
    addPoint(m, deepest[0] - refN[0] * dsep * 0.5, deepest[1] - refN[1] * dsep * 0.5, Math.max(-dsep, 0));
  }
  if (detail) Object.assign(detail, { refN, refCenter, refAxis: r.i, refBox: ref, incident, clipped: poly });
  return m;
}
// 線分(2点)を半平面 n·p <= offset で切る
function clipSegment(seg, n, offset) {
  const [a, b] = seg, da = dot(n, a) - offset, db = dot(n, b) - offset;
  if (da <= 0 && db <= 0) return [a, b];
  if (da > 0 && db > 0) return [];
  const t = da / (da - db), p = [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t];
  return da > 0 ? [p, b] : [a, p];
}
// カプセルと箱: 最近接点で1点、端の球が実際に当たっていればそれも足す(Day 45a 要点4)
function capsuleBox(cap, box) {
  const [e0, e1] = cap.segment(), r = cap.shape.r;
  const c = segmentBoxClosest(box, e0, e1);
  const base = circleBox(c.seg, r, box);
  if (!base.hit) return base;
  const m = manifold(base.normal[0], base.normal[1], 'capsule');
  const p0 = base.points[0];
  addPoint(m, p0.x, p0.y, p0.depth);
  // 基準面 = 接触点 + 法線 × 深さ/2(相手の表面)
  const surf = [p0.x + m.normal[0] * p0.depth * 0.5, p0.y + m.normal[1] * p0.depth * 0.5];
  for (const e of [e0, e1]) {
    if (!circleBox(e, r, box).hit) continue;
    const depth = r - dot([e[0] - surf[0], e[1] - surf[1]], m.normal);
    if (depth <= 0) continue;
    const px = e[0] - m.normal[0] * (r - depth * 0.5), py = e[1] - m.normal[1] * (r - depth * 0.5);
    if (m.points.some((p) => Math.hypot(p.x - px, p.y - py) < 0.05)) continue;
    addPoint(m, px, py, depth);
  }
  return m;
}
function capsuleCircle(cap, c, rc) {
  const [e0, e1] = cap.segment();
  return circleCircle(closestOnSegment(c, e0, e1), cap.shape.r, c, rc);
}
function capsuleCapsule(a, b) {
  const [p0, p1] = a.segment(), [q0, q1] = b.segment();
  const c = segmentClosest(p0, p1, q0, q1);
  return circleCircle(c.p, a.shape.r, c.q, b.shape.r);
}

// 形の組み合わせで振り分ける窓口(Collision3D.Collide)
function collide(a, b) {
  const ka = a.shape.kind, kb = b.shape.kind;
  if (ka === 'plane' && kb === 'plane') return manifold(0, 1, 'none');
  if (ka === 'plane') return flip(collide(b, a));
  const center = (x) => [x.x, x.y];
  if (kb === 'plane') {
    if (ka === 'circle') return circlePlane(center(a), a.shape.r, b);
    if (ka === 'box') return boxPlane(a, b);
    return capsulePlane(a, b);
  }
  if (ka === 'circle' && kb === 'circle') return circleCircle(center(a), a.shape.r, center(b), b.shape.r);
  if (ka === 'circle' && kb === 'box') return circleBox(center(a), a.shape.r, b);
  if (ka === 'box' && kb === 'circle') return flip(circleBox(center(b), b.shape.r, a));
  if (ka === 'box' && kb === 'box') return sat(a, b);
  if (ka === 'capsule' && kb === 'box') return capsuleBox(a, b);
  if (ka === 'box' && kb === 'capsule') return flip(capsuleBox(b, a));
  if (ka === 'capsule' && kb === 'circle') return capsuleCircle(a, center(b), b.shape.r);
  if (ka === 'circle' && kb === 'capsule') return flip(capsuleCircle(b, center(a), a.shape.r));
  return capsuleCapsule(a, b);
}

// ---------------- 世界 ----------------
class World {
  constructor() {
    this.gx = 0; this.gy = -9.81;
    this.velocityIterations = 8;
    this.maxContactsPerPair = MAX_POINTS;
    this.solveTogether = true;
    this.positionCorrection = true;
    this.positionIterations = 4;
    this.correctionRate = 0.6;
    this.slop = 0.005;
    this.restitutionThreshold = 1.0;
    this.bodies = [];
    this.contacts = [];
    this.manifolds = [];
    this.pairTests = 0;
    this.maxPenetration = 0;
    // ---- Day 47(solver = 'day47' のときだけ効く)----
    this.solver = 'day44';
    this.accumulateImpulses = true;
    this.warmStarting = true;
    this.frictionEnabled = true;
    this.frictionOverride = -1;
    this.sleepEnabled = true;
    this.sleepLinearThreshold = 0.05;
    this.sleepAngularThreshold = 0.10;
    this.sleepTime = 0.5;
    // reference には無い切り替え。false で Day 43〜46 の順番(解く前の速度で位置を進める)に戻す
    this.integrateAfterSolve = true;
    // 箱の面の接触を奥行きぶん2倍の点数で解く(3D の4点と数字を揃えるため)
    this.depthPoints = true;
    // reference には無い切り替え。false で島を組まずに1体ずつ眠らせる(要点5の失敗を見るため)
    this.islandSleep = true;
    this.cache = new Map();       // ContactCache: 組 → 持ち越した点
    this.warmStartedPoints = 0;
    this.newContactPoints = 0;
    this.islandCount = 0;
  }
  // 素朴なクランプに温存を載せると発散するので、蓄積を切ったら温存も止める(WarmStartActive)
  get warmStartActive() { return this.warmStarting && this.accumulateImpulses; }
  get sleepingCount() { return this.bodies.reduce((n, b) => n + (b.sleeping ? 1 : 0), 0); }
  add(body) { body.px = body.x; body.py = body.y; body.pa = body.angle; this.bodies.push(body); return this.bodies.length - 1; }
  clear() { this.bodies = []; this.contacts = []; this.manifolds = []; this.cache.clear(); this.islandCount = 0; }
  step(dt) {
    if (dt <= 0) return;
    if (this.solver === 'day47') { this.step47(dt); return; }
    for (const b of this.bodies) b.integrateVelocity(dt, this.gx, this.gy);
    for (const b of this.bodies) b.integratePosition(dt);
    this.generate();
    for (let it = 0; it < this.velocityIterations; it++) {
      for (const [start, count] of this.manifolds) this.solveManifold(start, count);
    }
    if (this.positionCorrection) this.correctPositions();
    for (const b of this.bodies) { b.fx = 0; b.fy = 0; b.torque = 0; }
  }
  generate() {
    this.contacts = []; this.manifolds = []; this.pairTests = 0; this.maxPenetration = 0;
    const B = this.bodies;
    for (let i = 0; i < B.length; i++) {
      for (let j = i + 1; j < B.length; j++) {
        const a = B[i], b = B[j];
        if (a.isStatic && b.isStatic) continue;
        this.pairTests++;
        const m = collide(a, b);
        if (!m.hit) continue;
        reduce(m, this.maxContactsPerPair);
        this.maxPenetration = Math.max(this.maxPenetration, maxDepth(m));
        this.manifolds.push([this.contacts.length, m.points.length]);
        for (const p of m.points) this.addContact(i, j, m.normal, p, a, b);
      }
    }
  }
  addContact(ia, ib, n, p, a, b) {
    const va = a.velocityAt(p.x, p.y), vb = b.velocityAt(p.x, p.y);
    const approach = (va[0] - vb[0]) * n[0] + (va[1] - vb[1]) * n[1];
    const e = Math.min(a.restitution, b.restitution);
    const bounce = approach < -this.restitutionThreshold ? -e * approach : 0;
    // 接触点を両方の体に貼り付けた印として物体座標で覚える(Day 44a 要点4)
    this.contacts.push({ a: ia, b: ib, n, x: p.x, y: p.y, la: a.toLocal(p.x, p.y), lb: b.toLocal(p.x, p.y), sep: -p.depth, bounce });
  }
  normalImpulse(c) {
    const a = this.bodies[c.a], b = this.bodies[c.b];
    const va = a.velocityAt(c.x, c.y), vb = b.velocityAt(c.x, c.y);
    const vn = (va[0] - vb[0]) * c.n[0] + (va[1] - vb[1]) * c.n[1];
    const delta = c.bounce - vn;
    if (delta <= 0) return 0;
    let den = a.invMass + b.invMass;
    for (const body of [a, b]) {
      const rx = c.x - body.x, ry = c.y - body.y, cr = (rx * c.n[1] - ry * c.n[0]) * body.invI;
      den += -cr * ry * c.n[0] + cr * rx * c.n[1];
    }
    return den > 0 ? delta / den : 0;
  }
  applyNormal(c, mag) {
    if (mag <= 0) return;
    this.bodies[c.a].applyImpulseAtPoint(c.n[0] * mag, c.n[1] * mag, c.x, c.y);
    this.bodies[c.b].applyImpulseAtPoint(-c.n[0] * mag, -c.n[1] * mag, c.x, c.y);
  }
  solveManifold(start, count) {
    if (!this.solveTogether) {
      for (let k = 0; k < count; k++) { const c = this.contacts[start + k]; this.applyNormal(c, this.normalImpulse(c)); }
      return;
    }
    // 同じ状態から全点ぶんを決めてから、点の数で割ってまとめて掛ける(Day 44a 要点5)
    const mags = [];
    for (let k = 0; k < count; k++) mags.push(this.normalImpulse(this.contacts[start + k]));
    for (let k = 0; k < count; k++) this.applyNormal(this.contacts[start + k], mags[k] / count);
  }
  correctPositions() {
    for (let it = 0; it < this.positionIterations; it++) {
      for (const c of this.contacts) {
        const a = this.bodies[c.a], b = this.bodies[c.b];
        const ims = a.invMass + b.invMass;
        if (ims <= 0) continue;
        const wa = a.toWorld(c.la[0], c.la[1]), wb = b.toWorld(c.lb[0], c.lb[1]);
        const sep = c.sep + (wa[0] - wb[0]) * c.n[0] + (wa[1] - wb[1]) * c.n[1];
        const overlap = Math.max(-sep - this.slop, 0);
        if (overlap <= 0) continue;
        const k = overlap * this.correctionRate / ims;
        a.x += c.n[0] * k * a.invMass; a.y += c.n[1] * k * a.invMass;
        b.x -= c.n[0] * k * b.invMass; b.y -= c.n[1] * k * b.invMass;
      }
    }
  }
  // ======================================================================
  //  Day 47: Sequential Impulses(PhysicsWorld.Step の8段)
  //  速度 → 判定 → 下ごしらえ → 速度の解決(摩擦 → 法線)→ 位置 → 補正 → 持ち越し → 眠り
  // ======================================================================
  step47(dt) {
    for (const b of this.bodies) b.integrateVelocity(dt, this.gx, this.gy);
    // Day 43〜46 の順番: 解く前の速度で位置を進めてしまう(要点4)
    if (!this.integrateAfterSolve) for (const b of this.bodies) b.integratePosition(dt);
    this.generate47();
    this.prepareContacts();
    for (let it = 0; it < this.velocityIterations; it++) {
      for (const [start, count] of this.manifolds) {
        if (this.frictionEnabled) this.solveFriction(start, count);
        this.solveManifold47(start, count);
      }
    }
    // 解いたあとの速度で位置を進める。速度が 0 に解けた物は 1mm も動かない
    if (this.integrateAfterSolve) for (const b of this.bodies) b.integratePosition(dt);
    if (this.positionCorrection) this.correctPositions47();
    this.storeImpulses();
    this.updateSleep(dt);
    for (const b of this.bodies) { b.fx = 0; b.fy = 0; b.torque = 0; }
  }
  generate47() {
    this.contacts = []; this.manifolds = []; this.pairTests = 0; this.maxPenetration = 0;
    this.stamp = (this.stamp || 0) + 1; this.warmStartedPoints = 0; this.newContactPoints = 0;
    const B = this.bodies;
    for (let i = 0; i < B.length; i++) {
      for (let j = i + 1; j < B.length; j++) {
        const a = B[i], b = B[j];
        if (a.isStatic && b.isStatic) continue;
        this.pairTests++;
        const m = collide(a, b);
        if (!m.hit) continue;
        reduce(m, this.maxContactsPerPair);
        this.maxPenetration = Math.max(this.maxPenetration, maxDepth(m));
        // 箱の面で支える接触は、3D では奥行きの手前と奥に2点ずつ並ぶ(4点)。
        // 横から見ると重なって見えるだけなので、同じ点を2回並べて reference と同じ点数にする
        const twice = this.depthPoints && isFace(a, b) ? 2 : 1;
        this.manifolds.push([this.contacts.length, m.points.length * twice]);
        for (const p of m.points) for (let k = 0; k < twice; k++) this.addContact47(i, j, m.normal, p, a, b);
      }
    }
  }
  addContact47(ia, ib, n, p, a, b) {
    const va = a.velocityAt(p.x, p.y), vb = b.velocityAt(p.x, p.y);
    const approach = (va[0] - vb[0]) * n[0] + (va[1] - vb[1]) * n[1];
    const e = Math.min(a.restitution, b.restitution);
    const bounce = approach < -this.restitutionThreshold ? -e * approach : 0;
    // 片方が 0(氷)なら全体も 0、ざらざら同士は両方より大きくならない(相乗平均)
    const friction = this.frictionOverride >= 0 ? this.frictionOverride : Math.sqrt(a.friction * b.friction);
    const c = {
      a: ia, b: ib, n, t: [n[1], -n[0]], x: p.x, y: p.y, la: a.toLocal(p.x, p.y), lb: b.toLocal(p.x, p.y),
      sep: -p.depth, bounce, friction,
      normalMass: 0, tangentMass: 0, normalImpulse: 0, tangentImpulse: 0, warm: false,
    };
    if (this.warmStartActive) {
      // ContactCache.TryMatch: 物体座標で 2cm 以内のいちばん近い点を引く
      const entry = this.cache.get(pairKey(ia, ib));
      let best = -1, bestD = 0.02 * 0.02;
      if (entry) {
        entry.points.forEach((q, k) => {
          const d = Math.max((q.la[0] - c.la[0]) ** 2 + (q.la[1] - c.la[1]) ** 2, (q.lb[0] - c.lb[0]) ** 2 + (q.lb[1] - c.lb[1]) ** 2);
          if (d < bestD) { bestD = d; best = k; }
        });
      }
      if (best < 0) this.newContactPoints++;
      else {
        c.normalImpulse = entry.points[best].normalImpulse; c.tangentImpulse = entry.points[best].tangentImpulse;
        c.warm = true; this.warmStartedPoints++;
      }
    }
    this.contacts.push(c);
  }
  effectiveMass(a, b, rA, rB, d) {
    const ca = rA[0] * d[1] - rA[1] * d[0], cb = rB[0] * d[1] - rB[1] * d[0];
    let den = a.solverInvMass + b.solverInvMass + a.solverInvI * ca * ca + b.solverInvI * cb * cb;
    if (this.depthPoints) den += a.solverInvI * depthOf(a) ** 2 + b.solverInvI * depthOf(b) ** 2;
    return den > 0 ? 1 / den : 0;
  }
  // 下ごしらえ: 実効質量を反復の外で1回だけ出し、前のステップの答えを掛けてから反復に入る
  prepareContacts() {
    for (const c of this.contacts) {
      const a = this.bodies[c.a], b = this.bodies[c.b];
      const rA = [c.x - a.x, c.y - a.y], rB = [c.x - b.x, c.y - b.y];
      c.normalMass = this.effectiveMass(a, b, rA, rB, c.n);
      c.tangentMass = this.effectiveMass(a, b, rA, rB, c.t);
      if (!c.warm) continue;
      if (!this.frictionEnabled) c.tangentImpulse = 0;
      const jx = c.n[0] * c.normalImpulse + c.t[0] * c.tangentImpulse, jy = c.n[1] * c.normalImpulse + c.t[1] * c.tangentImpulse;
      a.applyImpulseAtPoint(jx, jy, c.x, c.y);
      b.applyImpulseAtPoint(-jx, -jy, c.x, c.y);
    }
  }
  relVel(c) {
    const va = this.bodies[c.a].velocityAt(c.x, c.y), vb = this.bodies[c.b].velocityAt(c.x, c.y);
    return [va[0] - vb[0], va[1] - vb[1]];
  }
  solveManifold47(start, count) {
    if (!this.solveTogether) { for (let k = 0; k < count; k++) this.solveNormal(this.contacts[start + k], 1); return; }
    const v = [];
    for (let k = 0; k < count; k++) v.push(dot(this.relVel(this.contacts[start + k]), this.contacts[start + k].n));
    for (let k = 0; k < count; k++) this.solveNormal(this.contacts[start + k], count, v[k]);
  }
  solveNormal(c, share, vn = NaN) {
    if (Number.isNaN(vn)) vn = dot(this.relVel(c), c.n);
    const lambda = (c.bounce - vn) * c.normalMass / share;
    let applied;
    if (this.accumulateImpulses) {
      // 合計を切る → 途中の周では負の補正も出せる(要点1)
      const previous = c.normalImpulse;
      c.normalImpulse = Math.max(previous + lambda, 0);
      applied = c.normalImpulse - previous;
    } else {
      // 今掛けるぶんを切る → 掛けすぎを戻せない
      applied = Math.max(lambda, 0);
      c.normalImpulse += applied;
    }
    if (applied === 0) return;
    this.bodies[c.a].applyImpulseAtPoint(c.n[0] * applied, c.n[1] * applied, c.x, c.y);
    this.bodies[c.b].applyImpulseAtPoint(-c.n[0] * applied, -c.n[1] * applied, c.x, c.y);
  }
  // 摩擦は法線より先に解く。上限 μλn の λn はその時点で溜まっている値
  solveFriction(start, count) {
    for (let k = 0; k < count; k++) {
      const c = this.contacts[start + k];
      if (c.friction <= 0) continue;
      const lambda = -dot(this.relVel(c), c.t) * c.tangentMass / count;
      const old = c.tangentImpulse, limit = c.friction * c.normalImpulse;
      let sum = old + lambda;
      // 2D の接線は1本なので「円で切る」は ±limit で切るのと同じになる
      if (sum * sum > limit * limit) sum = sum * sum > 1e-12 ? Math.sign(sum) * limit : 0;
      c.tangentImpulse = sum;
      const d = sum - old;
      this.bodies[c.a].applyImpulseAtPoint(c.t[0] * d, c.t[1] * d, c.x, c.y);
      this.bodies[c.b].applyImpulseAtPoint(-c.t[0] * d, -c.t[1] * d, c.x, c.y);
    }
  }
  correctPositions47() {
    for (let it = 0; it < this.positionIterations; it++) {
      for (const c of this.contacts) {
        const a = this.bodies[c.a], b = this.bodies[c.b];
        const ima = a.solverInvMass, imb = b.solverInvMass, ims = ima + imb;
        if (ims <= 0) continue;
        const wa = a.toWorld(c.la[0], c.la[1]), wb = b.toWorld(c.lb[0], c.lb[1]);
        const sep = c.sep + (wa[0] - wb[0]) * c.n[0] + (wa[1] - wb[1]) * c.n[1];
        const overlap = Math.max(-sep - this.slop, 0);
        if (overlap <= 0) continue;
        const k = overlap * this.correctionRate / ims;
        a.x += c.n[0] * k * ima; a.y += c.n[1] * k * ima;
        b.x -= c.n[0] * k * imb; b.y -= c.n[1] * k * imb;
      }
    }
  }
  storeImpulses() {
    if (!this.warmStartActive) { this.cache.clear(); return; }
    for (const [start, count] of this.manifolds) {
      const first = this.contacts[start], points = [];
      for (let k = 0; k < count; k++) {
        const c = this.contacts[start + k];
        points.push({ la: c.la, lb: c.lb, normalImpulse: c.normalImpulse, tangentImpulse: c.tangentImpulse });
      }
      this.cache.set(pairKey(first.a, first.b), { stamp: this.stamp, points });
    }
    // このステップで触れなかった組は捨てる(ContactCache.Prune)
    for (const [k, v] of this.cache) if (v.stamp !== this.stamp) this.cache.delete(k);
  }
  // 触れ合っているものはまとめて眠り、まとめて起きる(要点5)
  updateSleep(dt) {
    const B = this.bodies;
    if (!this.sleepEnabled) { for (const b of B) if (b.sleeping) b.wake(); this.islandCount = 0; return; }
    const parent = B.map((_, i) => i);
    const find = (i) => { while (parent[i] !== i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; };
    for (const [start] of this.islandSleep ? this.manifolds : []) {
      const c = this.contacts[start];
      // 静的な体は繋がない。床で全部が1つの島になると誰も眠れない
      if (B[c.a].isStatic || B[c.b].isStatic) continue;
      const ra = find(c.a), rb = find(c.b);
      if (ra !== rb) parent[ra] = rb;
    }
    const timers = new Array(B.length).fill(Infinity);
    let islands = 0;
    B.forEach((b, i) => {
      if (b.isStatic) return;
      if (!b.sleeping) {
        const slow = b.vx * b.vx + b.vy * b.vy < this.sleepLinearThreshold ** 2 && b.w * b.w < this.sleepAngularThreshold ** 2;
        b.sleepTimer = slow ? b.sleepTimer + dt : 0;
      }
      const timer = b.allowSleep ? b.sleepTimer : 0, root = find(i);
      if (timers[root] === Infinity) islands++;
      // 島の中でいちばん眠りが浅い時計に合わせる
      timers[root] = Math.min(timers[root], timer);
    });
    this.islandCount = islands;
    B.forEach((b, i) => {
      if (b.isStatic) return;
      const should = timers[find(i)] >= this.sleepTime;
      if (should && !b.sleeping) b.sleep();
      else if (!should && b.sleeping) b.wake();
    });
  }
  // 「今この形を置いたら何に当たるか」(PhysicsWorld.QueryCapsule)。法線はカプセルを押し出す向き
  queryCapsule(cap) {
    const out = [];
    for (const b of this.bodies) {
      const m = collide(cap, b);
      if (m.hit) out.push(m);
    }
    return out;
  }
}

// ---------------- 描画(実験台の共通部品) ----------------
// view = { X, Y, s }(世界座標 → 画面)。alpha は前ステップとの補間係数
function drawBody(ctx, b, view, color, alpha = 1) {
  const { X, Y, s } = view;
  const x = b.px + (b.x - b.px) * alpha, y = b.py + (b.y - b.py) * alpha, ang = b.pa + (b.angle - b.pa) * alpha;
  const c = Math.cos(ang), sn = Math.sin(ang);
  const tw = (lx, ly) => [X(x + c * lx - sn * ly), Y(y + sn * lx + c * ly)];
  ctx.fillStyle = color; ctx.strokeStyle = 'rgba(0,0,0,0.4)'; ctx.lineWidth = 1;
  const k = b.shape.kind;
  if (k === 'box') {
    const { hx, hy } = b.shape;
    ctx.beginPath();
    [[-hx, -hy], [hx, -hy], [hx, hy], [-hx, hy]].forEach(([lx, ly], i) => { const [px, py] = tw(lx, ly); if (i === 0) ctx.moveTo(px, py); else ctx.lineTo(px, py); });
    ctx.closePath(); ctx.fill(); ctx.stroke();
    // 向きの目印(上の面に線)
    const [ax, ay] = tw(-hx * 0.6, hy * 0.7), [bx, by] = tw(hx * 0.6, hy * 0.7);
    ctx.strokeStyle = 'rgba(255,255,255,0.7)'; ctx.lineWidth = 2; ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(bx, by); ctx.stroke();
  } else if (k === 'circle') {
    const [cx, cy] = tw(0, 0);
    ctx.beginPath(); ctx.arc(cx, cy, b.shape.r * s, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
    const [ex, ey] = tw(b.shape.r * 0.7, 0);
    ctx.strokeStyle = 'rgba(255,255,255,0.8)'; ctx.lineWidth = 2; ctx.beginPath(); ctx.moveTo(cx, cy); ctx.lineTo(ex, ey); ctx.stroke();
  } else if (k === 'capsule') {
    const { r, hh } = b.shape;
    const [ax, ay] = tw(0, -hh), [bx, by] = tw(0, hh);
    ctx.lineCap = 'round'; ctx.lineWidth = 2 * r * s;
    ctx.strokeStyle = 'rgba(0,0,0,0.4)'; ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(bx, by); ctx.stroke();
    ctx.lineWidth = 2 * r * s - 2; ctx.strokeStyle = color; ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(bx, by); ctx.stroke();
    ctx.lineCap = 'butt'; ctx.lineWidth = 1.5; ctx.strokeStyle = 'rgba(255,255,255,0.7)';
    ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(bx, by); ctx.stroke();
  }
}
// 接触点(光る玉)と法線
function drawContacts(ctx, world, view, color) {
  const { X, Y } = view;
  for (const c of world.contacts) {
    ctx.fillStyle = color; ctx.beginPath(); ctx.arc(X(c.x), Y(c.y), 4, 0, Math.PI * 2); ctx.fill();
    ctx.strokeStyle = color; ctx.lineWidth = 1.5; ctx.beginPath(); ctx.moveTo(X(c.x), Y(c.y)); ctx.lineTo(X(c.x + c.n[0] * 0.25), Y(c.y + c.n[1] * 0.25)); ctx.stroke();
  }
}
// 平面(床・壁)を描く。見える範囲だけ
function drawPlanes(ctx, world, view, ink, w, h) {
  const { X, Y } = view;
  ctx.strokeStyle = ink; ctx.lineWidth = 2;
  for (const b of world.bodies) {
    if (b.shape.kind !== 'plane') continue;
    const n = b.normal, t = [-n[1], n[0]];
    ctx.beginPath(); ctx.moveTo(X(b.x - t[0] * 100), Y(b.y - t[1] * 100)); ctx.lineTo(X(b.x + t[0] * 100), Y(b.y + t[1] * 100)); ctx.stroke();
  }
}

window.Phys2D = {
  drawBody, drawContacts, drawPlanes,
  Shape, Body, World, collide, sat, circleBox, segmentClosest, segmentBoxClosest, closestOnSegment,
  maxDepth, inertia, MAX_POINTS,
};
})();
