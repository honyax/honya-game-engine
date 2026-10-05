// ============================================================
//  ハードウェアレイトレーサの場面と光線の追い方(Day 62a・62b・62c の実験台が使う)
//  reference/Day62a〜62c の Scene/SceneData.cs・Scene/Camera.cs・shaders/trace.comp の移植と、
//  球を囲む箱の木(BLAS の代わり。RT コアの中の作りは公開されていないので、近い子から辿る2分木で近似する)。
//  計算は double(GPU は float)。場面の生成だけは C# と同じ float の値になるように丸める
//  使う側: const R = window.HwRayTracer;
// ============================================================
(() => {
'use strict';

const F = Math.fround;
const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const mul = (a, s) => [a[0] * s, a[1] * s, a[2] * s];
const mulv = (a, b) => [a[0] * b[0], a[1] * b[1], a[2] * b[2]];
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const norm = (a) => mul(a, 1 / Math.sqrt(dot(a, a)));
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const clamp = (v, lo, hi) => Math.min(Math.max(v, lo), hi);
const mix = (a, b, t) => a.map((v, k) => v * (1 - t) + b[k] * t);

// ---- .NET の Random(seed)(種ありは .NET Framework と同じ数列。NextDouble は丸めない)----
function netRandom(seed) {
  const MBIG = 2147483647, a = new Array(56).fill(0);
  let mj = 161803398 - Math.abs(seed), mk = 1;
  a[55] = mj;
  for (let i = 1; i < 55; i++) { const ii = (21 * i) % 55; a[ii] = mk; mk = mj - mk; if (mk < 0) mk += MBIG; mj = a[ii]; }
  for (let k = 1; k < 5; k++) for (let i = 1; i < 56; i++) { a[i] -= a[1 + ((i + 30) % 55)]; if (a[i] < 0) a[i] += MBIG; }
  let inext = 0, inextp = 21;
  const sample = () => {
    if (++inext >= 56) inext = 1;
    if (++inextp >= 56) inextp = 1;
    let r = a[inext] - a[inextp];
    if (r === MBIG) r--;
    if (r < 0) r += MBIG;
    a[inext] = r;
    return r * (1 / MBIG);
  };
  return { nextDouble: sample, next: (n) => Math.floor(sample() * n) };
}

// ---- 場面(SceneData.cs)----
// day: '62a'(x = r*20-10)/ '62b'(x = (r*2-1)*extent。5000 個は extent 26)/ '62c'(62b を材質で並べ替え。拡散が先)
const PRESETS = [8, 120, 720, 5000];
const DEFAULT_VIEW = { target: [0, 0.7, 0], yaw: 0.35, pitch: 0.18, distance: 12, fov: 45 };
const metal = (c, r, f0) => ({ c, r, albedo: f0, kind: 1 });
const diffuse = (c, r, albedo) => ({ c, r, albedo, kind: 0 });
function createScene(count, day = '62b') {
  const extent = count <= 720 ? 10 : 26;
  const s = [
    metal([-2.6, 1, 0], 1, [0.95, 0.95, 0.97].map(F)),
    diffuse([0, 1, 0], 1, [0.75, 0.25, 0.20].map(F)),
    metal([2.6, 1, 0], 1, [0.90, 0.70, 0.30].map(F)),
  ];
  const rng = netRandom(20260919);
  while (s.length < count) {
    const coord = () => (day === '62a' ? F(rng.nextDouble() * 20 - 10) : F(F(rng.nextDouble() * 2 - 1) * extent));
    const x = coord(), z = coord(), r = F(rng.nextDouble() * 0.12 + 0.12);
    const c = [x, r, z];
    let overlaps = false;
    for (let i = 0; i < 3; i++) {
      const d = sub(c, s[i].c);
      if (F(Math.sqrt(dot(d, d))) < F(F(s[i].r + r) + F(0.15))) { overlaps = true; break; }
    }
    if (overlaps) continue;
    if (rng.next(4) === 0) s.push(metal(c, r, [0.85, 0.87, 0.90].map(F)));
    else {
      const ab = [0, 1, 2].map(() => F(rng.nextDouble() * rng.nextDouble()));
      s.push(diffuse(c, r, ab.map((v) => Math.min(F(F(v * F(1.6)) + F(0.05)), F(0.9)))));
    }
  }
  // Day 62c は SBT のために拡散を先、金属を後ろに並べる(OrderBy は安定なので、同じ材質の中の順は保たれる)
  return day === '62c' ? [...s.filter((x) => x.kind === 0), ...s.filter((x) => x.kind === 1)] : s;
}

// ---- 球を囲む箱の木(BLAS の代わり)----
// 葉は箱1つ(= 球1つ)。重心の広がりがいちばん大きい軸で、真ん中で2つに割る
function buildBoxTree(spheres) {
  const nodes = [], idx = spheres.map((_, i) => i);
  function build(lo, hi) {
    const n = { min: [Infinity, Infinity, Infinity], max: [-Infinity, -Infinity, -Infinity], leaf: -1, left: -1, right: -1 };
    const cmin = [Infinity, Infinity, Infinity], cmax = [-Infinity, -Infinity, -Infinity];
    for (let k = lo; k < hi; k++) {
      const s = spheres[idx[k]];
      for (let a = 0; a < 3; a++) { n.min[a] = Math.min(n.min[a], s.c[a] - s.r); n.max[a] = Math.max(n.max[a], s.c[a] + s.r); cmin[a] = Math.min(cmin[a], s.c[a]); cmax[a] = Math.max(cmax[a], s.c[a]); }
    }
    const me = nodes.length;
    nodes.push(n);
    if (hi - lo === 1) { n.leaf = idx[lo]; return me; }
    const ext = [0, 1, 2].map((a) => cmax[a] - cmin[a]), axis = ext[0] >= ext[1] && ext[0] >= ext[2] ? 0 : ext[1] >= ext[2] ? 1 : 2;
    const part = idx.slice(lo, hi).sort((p, q) => spheres[p].c[axis] - spheres[q].c[axis]);
    for (let k = lo; k < hi; k++) idx[k] = part[k - lo];
    const mid = (lo + hi) >> 1;
    n.left = build(lo, mid); n.right = build(mid, hi);
    return me;
  }
  if (spheres.length) build(0, spheres.length);
  let depth = 0;
  (function walk(i, d) { depth = Math.max(depth, d); if (nodes[i] && nodes[i].leaf < 0) { walk(nodes[i].left, d + 1); walk(nodes[i].right, d + 1); } })(0, 1);
  return { nodes, depth };
}
// 箱と光線(スラブ法)。当たれば入る t を返す
function slab(n, o, inv, tMin, tMax) {
  let t0 = tMin, t1 = tMax;
  for (let a = 0; a < 3; a++) {
    let p = (n.min[a] - o[a]) * inv[a], q = (n.max[a] - o[a]) * inv[a];
    if (p > q) [p, q] = [q, p];
    if (p > t0) t0 = p;
    if (q < t1) t1 = q;
  }
  return t0 <= t1 ? t0 : -1;
}
// ray query の代わり。cnt.boxes は木の箱の判定(実機ではシェーダから見えない)、
// cnt.tests は葉の箱に当たってシェーダへ戻ってきた回数(= hitSphere を呼んだ回数。実機で gTests が数えるもの)
function boxTreeAccel(spheres, tree = buildBoxTree(spheres)) {
  const walk = (o, d, tMin, tMax, cnt, anyHit) => {
    if (!tree.nodes.length) return null;
    const inv = d.map((v) => 1 / v), stack = [0];
    let best = null, upper = tMax;
    while (stack.length) {
      const n = tree.nodes[stack.pop()];
      cnt.boxes = (cnt.boxes || 0) + 1;
      if (slab(n, o, inv, tMin, upper) < 0) continue;
      if (n.leaf >= 0) {
        const s = spheres[n.leaf], t = hitSphere(o, d, s, tMin, upper, cnt);
        if (t >= 0) { upper = t; best = { t, s }; if (anyHit) return best; }
        continue;
      }
      // 近い子を後に積む(先に取り出す)
      const a = tree.nodes[n.left], b = tree.nodes[n.right];
      const ta = slab(a, o, inv, tMin, upper), tb = slab(b, o, inv, tMin, upper);
      if (ta >= 0 && tb >= 0 && tb < ta) { stack.push(n.left, n.right); } else { stack.push(n.right, n.left); }
    }
    return best;
  };
  return { tree, closest: (o, d, a, b, cnt) => walk(o, d, a, b, cnt, false), any: (o, d, a, b, cnt) => walk(o, d, a, b, cnt, true) !== null };
}

// ---- カメラ(Camera.cs)----
function camera(view, w, h) {
  const pos = add(view.target, mul([Math.sin(view.yaw) * Math.cos(view.pitch), Math.sin(view.pitch), -Math.cos(view.yaw) * Math.cos(view.pitch)], view.distance));
  const f = norm(sub(view.target, pos)), r = norm(cross(f, [0, 1, 0])), u = cross(r, f);
  const hh = Math.tan(view.fov * Math.PI / 360), hw = hh * w / h;
  return {
    pos, w, h, forward: f, right: r, up: u, halfWidth: hw, halfHeight: hh,
    // 画素の中心を通る光線(trace.comp の main と同じ式)
    ray(x, y) { const nx = (x + 0.5) / w * 2 - 1, ny = 1 - (y + 0.5) / h * 2; return norm(add(f, add(mul(r, nx * hw), mul(u, ny * hh)))); },
  };
}

// ---- 光線の追い方(trace.comp)----
const MAX_BOUNCES = 3, SUN_DIR = norm([-0.45, 0.85, -0.30]), SUN_COLOR = mul([1, 0.96, 0.88], 1.9);
function hitSphere(o, d, s, tMin, tMax, cnt) {
  cnt.tests++;
  const oc = sub(o, s.c), b = dot(oc, d), c = dot(oc, oc) - s.r * s.r, disc = b * b - c;
  if (disc < 0) return -1;
  const root = Math.sqrt(disc);
  let t = -b - root;
  if (t <= tMin) t = -b + root;
  return t > tMin && t < tMax ? t : -1;
}
// 総当たり。accel を渡すと、球を探すところだけをそれに任せる(Day 62b の加速構造)
function bruteClosest(spheres, o, d, tMin, tMax, cnt) {
  let best = null, bt = tMax;
  for (const s of spheres) { const t = hitSphere(o, d, s, tMin, bt, cnt); if (t >= 0) { bt = t; best = s; } }
  return best ? { t: bt, s: best } : null;
}
function bruteAny(spheres, o, d, tMin, tMax, cnt) {
  for (const s of spheres) if (hitSphere(o, d, s, tMin, tMax, cnt) >= 0) return true;
  return false;
}
function sky(d) {
  const up = clamp(d[1], 0, 1);
  const g = mix([0.72, 0.80, 0.92], [0.24, 0.42, 0.78], Math.sqrt(up));
  return add(g, mul(SUN_COLOR, Math.max(dot(d, SUN_DIR), 0) ** 900));
}
// scale: 何回で振り切るか(Day 62a は球の数の3倍、Day 62b の細かい目盛りは 48)
function heatColor(tests, scale) {
  const x = clamp(tests / scale, 0, 1);
  const stops = [[0.03, 0.03, 0.12], [0.15, 0.25, 0.85], [0.10, 0.80, 0.85], [0.20, 0.85, 0.25], [0.95, 0.85, 0.15], [0.90, 0.15, 0.10]];
  const s = x * 5, i = Math.min(Math.floor(s), 4);
  return mix(stops[i], stops[i + 1], s - i);
}
// accel: { closest(o, d, tMin, tMax, cnt), any(o, d, tMin, tMax, cnt) }。省くと総当たり
// opts(Day 62c の実験台): maxBounces(反射の回数。reference は 3)/
//   primaryGroup(sphere) … 一次光線が球に当たったときに呼ばれる hit グループ(0 = diffuse.rchit、1 = metal.rchit、-1 = SBT の外)/
//   primaryMissBlack … 一次光線の miss で shadow.rmiss が呼ばれる(荷物 0 に色が入らず黒になる)
function tracer(spheres, accel, opts = {}) {
  const maxBounces = opts.maxBounces ?? MAX_BOUNCES;
  const closest = accel ? accel.closest : (o, d, a, b, cnt) => bruteClosest(spheres, o, d, a, b, cnt);
  const any = accel ? accel.any : (o, d, a, b, cnt) => bruteAny(spheres, o, d, a, b, cnt);
  function traceScene(o, d, tMin, tMax, cnt) {
    let hit = null;
    const h = closest(o, d, tMin, tMax, cnt);
    if (h) { const p = add(o, mul(d, h.t)); hit = { t: h.t, p, n: mul(sub(p, h.s.c), 1 / h.s.r), albedo: h.s.albedo, kind: h.s.kind, sphere: h.s }; }
    if (Math.abs(d[1]) > 1e-6) {
      const t = (0 - o[1]) / d[1];
      if (t > tMin && t < (hit ? hit.t : tMax)) {
        const p = add(o, mul(d, t)), even = (((Math.floor(p[0]) + Math.floor(p[2])) % 2) + 2) % 2 < 0.5;
        hit = { t, p, n: [0, 1, 0], albedo: even ? [0.62, 0.62, 0.62] : [0.18, 0.18, 0.18], kind: 0, sphere: null };
      }
    }
    return hit;
  }
  function shadeDiffuse(hit, cnt) {
    const ndotl = Math.max(dot(hit.n, SUN_DIR), 0);
    const lit = ndotl > 0 && !any(add(hit.p, mul(hit.n, 1e-3)), SUN_DIR, 1e-4, 1e30, cnt);
    const direct = lit ? mul(SUN_COLOR, ndotl) : [0, 0, 0];
    return mulv(hit.albedo, add(direct, mul(sky(hit.n), 0.13)));
  }
  // 1画素。mode: 0 = 陰影、1 = 法線、2 = 交差判定の回数(球の数の3倍で振り切る)、3 = 同・細かい目盛り(48 回)
  // depth は RT パイプラインで数えたときの再帰の深さ(一次 1 + 反射の回数 + 影の光線 1)
  function pixel(cam, x, y, mode) {
    const cnt = { tests: 0, rays: 0, bounces: 0, boxes: 0, depth: 1 };
    let o = cam.pos, d = cam.ray(x, y), color = [0, 0, 0], tp = [1, 1, 1];
    for (let bounce = 0; bounce <= maxBounces; bounce++) {
      cnt.rays++;
      const hit = traceScene(o, d, 1e-4, 1e30, cnt);
      if (bounce === 0 && opts.primaryMissBlack && !(hit && hit.sphere)) { color = [0, 0, 0]; break; }
      if (!hit) { color = add(color, mulv(tp, sky(d))); cnt.depth = 1 + cnt.bounces; break; }
      if (bounce === 0 && opts.primaryGroup && hit.sphere) {
        const g = opts.primaryGroup(hit.sphere);
        if (g < 0) { color = [0, 0, 0]; cnt.outOfTable = true; break; }
        hit.kind = g;
      }
      if (mode === 1) { color = hit.n.map((v) => v * 0.5 + 0.5); if (!hit.sphere) cnt.depth++; break; }
      if (hit.kind === 1 && bounce < maxBounces) {
        tp = mulv(tp, hit.albedo); o = add(hit.p, mul(hit.n, 1e-3)); d = sub(d, mul(hit.n, 2 * dot(d, hit.n))); cnt.bounces++;
        continue;
      }
      if (dot(hit.n, SUN_DIR) > 0) cnt.rays++;
      cnt.depth = 1 + cnt.bounces + 1;
      color = add(color, mulv(tp, shadeDiffuse(hit, cnt)));
      break;
    }
    if (mode >= 2) color = heatColor(cnt.tests, mode === 3 ? 48 : Math.max(spheres.length, 1) * 3);
    else if (mode === 0) color = color.map((v) => (v / (v + 1)) ** (1 / 2.2));
    return { color, ...cnt };
  }
  return { traceScene, pixel };
}

const api = { netRandom, PRESETS, DEFAULT_VIEW, createScene, buildBoxTree, boxTreeAccel, camera, tracer, hitSphere, sky, heatColor, SUN_DIR, add, sub, mul, dot, norm, cross, clamp };
if (typeof module !== 'undefined') module.exports = api; else window.HwRayTracer = api;
})();
