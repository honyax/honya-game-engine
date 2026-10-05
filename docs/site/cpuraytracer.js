// ============================================================
//  CPU レイトレーサ(Day 59・60a・60b の実験台が使う)
//  reference/Day60b の CpuRayTracer の移植。Optics・Sphere・Plane・Material・SceneLibrary・Camera・
//  WhittedTracer・PathTracer・Rng(PCG32)・Sampler・Aabb・Bvh・ProgressiveRenderer の式。
//  計算は double(C# は float)。乱数は 64 ビットの演算を 32 ビット2つで組み、C# と同じ数列を出す
//  使う側: const C = window.CpuRayTracer;
// ============================================================
(() => {
'use strict';

// ---- ベクトル(配列 [x, y, z])----
const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const mul = (a, s) => [a[0] * s, a[1] * s, a[2] * s];
const mulv = (a, b) => [a[0] * b[0], a[1] * b[1], a[2] * b[2]];
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const len = (a) => Math.sqrt(dot(a, a));
const norm = (a) => mul(a, 1 / len(a));
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const clamp = (v, lo, hi) => Math.min(Math.max(v, lo), hi);
const ZERO = [0, 0, 0];
const isZero = (a) => a[0] === 0 && a[1] === 0 && a[2] === 0;

// ---- 光学(Optics.cs)----
const reflect = (d, n) => sub(d, mul(n, 2 * dot(d, n)));
function refract(d, n, eta) {
  const cosI = -dot(d, n), sin2T = eta * eta * (1 - cosI * cosI);
  if (sin2T > 1) return null;
  const cosT = Math.sqrt(1 - sin2T);
  return add(mul(d, eta), mul(n, eta * cosI - cosT));
}
function fresnel(cosI, n1, n2) {
  if (n1 === n2) return 0;
  cosI = clamp(cosI, 0, 1);
  const sinT = n1 / n2 * Math.sqrt(1 - cosI * cosI);
  if (sinT >= 1) return 1;
  const cosT = Math.sqrt(1 - sinT * sinT);
  const rs = (n1 * cosI - n2 * cosT) / (n1 * cosI + n2 * cosT), rp = (n2 * cosI - n1 * cosT) / (n2 * cosI + n1 * cosT);
  return 0.5 * (rs * rs + rp * rp);
}
const r0 = (n1, n2) => { const r = (n1 - n2) / (n1 + n2); return r * r; };
function schlick(cosI, n1, n2) {
  const f0 = r0(n1, n2);
  let c = clamp(cosI, 0, 1);
  if (n1 > n2) { const s2 = (n1 / n2) ** 2 * (1 - c * c); if (s2 >= 1) return 1; c = Math.sqrt(1 - s2); }
  return f0 + (1 - f0) * (1 - c) ** 5;
}
// RTIOW の書き方(内側からでも入射角の cos)。Day 59 の自己チェック7の比べる相手
const schlickNaive = (cosI, n1, n2) => { const f0 = r0(n1, n2); return f0 + (1 - f0) * (1 - cosI) ** 5; };
const schlickConductor = (cosI, f0) => { const m5 = (1 - clamp(cosI, 0, 1)) ** 5; return f0.map((f) => f + (1 - f) * m5); };

// ---- 乱数(Rng.cs の PCG32)。64 ビットの値は [hi, lo](どちらも 32 ビットの符号なし)----
const TWO32 = 4294967296;
function mul64(ah, al, bh, bl) {
  // 16 ビットずつに割って、下の 64 ビットだけを組み立てる
  const a0 = al & 0xffff, a1 = al >>> 16, a2 = ah & 0xffff, a3 = ah >>> 16;
  const b0 = bl & 0xffff, b1 = bl >>> 16, b2 = bh & 0xffff, b3 = bh >>> 16;
  let c = a0 * b0;
  const r0_ = c % 65536; c = Math.floor(c / 65536);
  c += a0 * b1 + a1 * b0;
  const r1 = c % 65536; c = Math.floor(c / 65536);
  c += a0 * b2 + a1 * b1 + a2 * b0;
  const r2 = c % 65536; c = Math.floor(c / 65536);
  c += a0 * b3 + a1 * b2 + a2 * b1 + a3 * b0;
  const r3 = c % 65536;
  return [((r3 << 16) | r2) >>> 0, ((r1 << 16) | r0_) >>> 0];
}
function add64(ah, al, bh, bl) {
  const lo = al + bl, carry = lo >= TWO32 ? 1 : 0;
  return [(ah + bh + carry) >>> 0, lo >>> 0];
}
function shr64(h, l, n) {
  if (n === 0) return [h, l];
  if (n >= 32) return [0, h >>> (n - 32)];
  return [h >>> n, ((l >>> n) | (h << (32 - n))) >>> 0];
}
const MUL = [0x5851f42d, 0x4c957f2d], INC = [0x14057b7e, 0xf767814f], GOLDEN = [0x9e3779b9, 0x7f4a7c15];
const SM1 = [0xbf58476d, 0x1ce4e5b9], SM2 = [0x94d049bb, 0x133111eb];
function splitMix64(h, l) {
  [h, l] = add64(h, l, GOLDEN[0], GOLDEN[1]);
  let s = shr64(h, l, 30); [h, l] = mul64((h ^ s[0]) >>> 0, (l ^ s[1]) >>> 0, SM1[0], SM1[1]);
  s = shr64(h, l, 27); [h, l] = mul64((h ^ s[0]) >>> 0, (l ^ s[1]) >>> 0, SM2[0], SM2[1]);
  s = shr64(h, l, 31);
  return [(h ^ s[0]) >>> 0, (l ^ s[1]) >>> 0];
}
class Rng {
  constructor(h, l) { this.h = h; this.l = l; }
  // Rng.Create。(画素, サンプル番号) から作り直す
  static create(x, y, sample, decorrelate = true) {
    let [h, l] = mul64(0, sample >>> 0, GOLDEN[0], GOLDEN[1]);
    if (decorrelate) { const m = splitMix64(y >>> 0, x >>> 0); h = (h ^ m[0]) >>> 0; l = (l ^ m[1]) >>> 0; }
    const s = splitMix64(h, l);
    return new Rng(s[0], s[1]);
  }
  nextUInt() {
    const oh = this.h, ol = this.l;
    [this.h, this.l] = add64(...mul64(oh, ol, MUL[0], MUL[1]), INC[0], INC[1]);
    const s = shr64(oh, ol, 18), x = shr64((s[0] ^ oh) >>> 0, (s[1] ^ ol) >>> 0, 27)[1];
    const rot = oh >>> 27;
    return ((x >>> rot) | (x << ((32 - rot) & 31))) >>> 0;
  }
  nextFloat() { return (this.nextUInt() >>> 8) * (1 / 16777216); }
  nextInt(count) { return Math.floor(this.nextUInt() * count / TWO32); }
}

// ---- サンプリング(Sampler.cs)----
function basis(n) {
  const sign = n[2] < 0 || Object.is(n[2], -0) ? -1 : 1, a = -1 / (sign + n[2]), b = n[0] * n[1] * a;
  return [[1 + sign * n[0] * n[0] * a, sign * b, -sign * n[0]], [b, sign + n[1] * n[1] * a, -n[1]]];
}
function uniformHemisphere(n, rng) {
  const cosT = rng.nextFloat(), sinT = Math.sqrt(Math.max(0, 1 - cosT * cosT)), phi = 2 * Math.PI * rng.nextFloat();
  const [t, b] = basis(n);
  return norm(add(add(mul(t, sinT * Math.cos(phi)), mul(b, sinT * Math.sin(phi))), mul(n, cosT)));
}
function cosineHemisphere(n, rng) {
  const u1 = rng.nextFloat(), u2 = rng.nextFloat(), r = Math.sqrt(u1), phi = 2 * Math.PI * u2;
  const x = r * Math.cos(phi), y = r * Math.sin(phi), z = Math.sqrt(Math.max(0, 1 - u1));
  const [t, b] = basis(n);
  return norm(add(add(mul(t, x), mul(b, y)), mul(n, z)));
}
function uniformCone(axis, cosMax, rng) {
  const cosT = 1 - rng.nextFloat() * (1 - cosMax), sinT = Math.sqrt(Math.max(0, 1 - cosT * cosT)), phi = 2 * Math.PI * rng.nextFloat();
  const [t, b] = basis(axis);
  return norm(add(add(mul(t, sinT * Math.cos(phi)), mul(b, sinT * Math.sin(phi))), mul(axis, cosT)));
}
const uniformHemispherePdf = 1 / (2 * Math.PI);
const cosineHemispherePdf = (c) => Math.max(c, 0) / Math.PI;
const uniformConePdf = (cosMax) => 1 / (2 * Math.PI * (1 - cosMax));

// ---- 材質(Material.cs)----
const material = (kind, o) => {
  const m = { kind, albedo: ZERO, alt: ZERO, checker: 0, specular: 0, shininess: 64, ior: 1, emission: ZERO, ...o };
  m.emissive = !isZero(m.emission);
  m.specularKind = kind === 'metal' || kind === 'glass';
  return m;
};
const diffuse = (albedo, specular = 0, shininess = 64) => material('diffuse', { albedo, specular, shininess });
const checker = (albedo, alt, size) => material('diffuse', { albedo, alt, checker: size });
const metal = (f0, shininess = 4000) => material('metal', { albedo: f0, shininess });
const glass = (ior, shininess = 4000) => material('glass', { ior, shininess });
const light = (radiance) => material('diffuse', { emission: radiance });
const albedoAt = (m, p) => (m.checker > 0 ? (((Math.floor(p[0] / m.checker) + Math.floor(p[2] / m.checker)) & 1) === 0 ? m.albedo : m.alt) : m.albedo);

// ---- 形(Sphere.cs・Plane.cs)----
function sphere(name, c, r, m) {
  const s = {
    name, m, c, r,
    hit(o, d, tMin, tMax) {
      const oc = sub(o, c), b = dot(oc, d), cc = dot(oc, oc) - r * r, disc = b * b - cc;
      if (disc < 0) return -1;
      const root = Math.sqrt(disc);
      let t = -b - root;
      if (t <= tMin) t = -b + root;
      return t > tMin && t < tMax ? t : -1;
    },
    normal: (p) => mul(sub(p, c), 1 / r),
    bounds: () => ({ min: sub(c, [r, r, r]), max: add(c, [r, r, r]) }),
    // TrySampleDirection。球が空を覆っている円錐の中に一様に引く
    sampleDirection(from, rng) {
      const toC = sub(c, from), dist = len(toC);
      if (dist <= r * 1.0001) return null;
      const sin2 = r * r / (dist * dist), cosMax = Math.sqrt(Math.max(0, 1 - sin2));
      const dir = uniformCone(mul(toC, 1 / dist), cosMax, rng), pdf = uniformConePdf(cosMax);
      const t = s.hit(from, dir, 0, Infinity);
      return t < 0 ? null : { dir, distance: t, pdf };
    },
  };
  return s;
}
const plane = (name, n, dist, m) => ({
  name, m,
  hit(o, d, tMin, tMax) {
    const den = dot(n, d);
    if (Math.abs(den) < 1e-8) return -1;
    const t = (dist - dot(n, o)) / den;
    return t > tMin && t < tMax ? t : -1;
  },
  normal: () => n,
  bounds: () => null,
  sampleDirection: () => null,
});

// ---- .NET の Random(seed)(場面5の玉の配置に使う。lights.js と同じ移植)----
function dotNetRandom(seed) {
  const MBIG = 2147483647, a = new Array(56).fill(0);
  let mj = 161803398 - Math.abs(seed), mk = 1;
  a[55] = mj;
  for (let i = 1; i < 55; i++) { const ii = (21 * i) % 55; a[ii] = mk; mk = mj - mk; if (mk < 0) mk += MBIG; mj = a[ii]; }
  for (let k = 1; k < 5; k++) for (let i = 1; i < 56; i++) { a[i] -= a[1 + ((i + 30) % 55)]; if (a[i] < 0) a[i] += MBIG; }
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

// ---- 場面(SceneLibrary.cs)----
const orbit = (target, yaw, pitch, distance, fov) => ({ target, yaw, pitch, distance, fov });
const g = (v) => [v, v, v];
const SCENES = {
  whitted: () => ({
    name: 'Whitted 1980', view: orbit([-0.3, 0.8, 0.9], 0.35, 0.22, 7.0, 40), ambient: [0.06, 0.07, 0.09], horizon: [0.80, 0.86, 0.95], zenith: [0.18, 0.36, 0.78],
    shapes: [
      plane('床(市松)', [0, 1, 0], 0, checker([0.75, 0.08, 0.05], [0.80, 0.70, 0.08], 1)),
      sphere('ガラス玉', [0.3, 1, 0], 1, glass(1.5)),
      sphere('鏡の玉', [-1.6, 0.8, 2.4], 0.8, metal(g(0.9))),
      sphere('青い玉', [2, 0.5, 2.2], 0.5, diffuse([0.10, 0.25, 0.75], 0.3)),
    ],
    lights: [{ name: '光1', p: [-4, 6, -3], c: [1, 1, 1], i: 180 }, { name: '光2', p: [4, 4, -2], c: [1, 0.95, 0.85], i: 50 }],
  }),
  materialRow: () => ({
    name: '屈折率と金属', view: orbit([0, 0.4, 0.7], 0, 0.30, 5.6, 45), ambient: [0.06, 0.06, 0.07], horizon: [0.85, 0.87, 0.90], zenith: [0.30, 0.42, 0.70],
    shapes: [
      plane('床(市松)', [0, 1, 0], 0, checker(g(0.7), g(0.08), 0.5)),
      ...[1.00, 1.10, 1.33, 1.50, 2.42].map((n, i) => sphere(`ガラス玉(屈折率 ${n.toFixed(2)})`, [2.2 - 1.1 * i, 0.45, 0], 0.45, glass(n))),
      sphere('白い玉(つや消し)', [2.2, 0.45, 1.4], 0.45, diffuse(g(0.75))),
      sphere('赤い玉(つや)', [1.1, 0.45, 1.4], 0.45, diffuse([0.70, 0.08, 0.06], 0.5, 200)),
      sphere('金の玉', [0, 0.45, 1.4], 0.45, metal([1, 0.71, 0.29])),
      sphere('銅の玉', [-1.1, 0.45, 1.4], 0.45, metal([0.95, 0.64, 0.54])),
      sphere('アルミの玉', [-2.2, 0.45, 1.4], 0.45, metal([0.91, 0.92, 0.92])),
    ],
    lights: [{ name: '光1', p: [-3, 5, -4], c: [1, 1, 1], i: 150 }, { name: '光2', p: [4, 3, -1], c: [1, 0.9, 0.8], i: 40 }],
  }),
  mirror: () => {
    const mirrorM = metal([0.86, 0.92, 0.88]);
    return {
      name: '合わせ鏡', view: orbit([0.4, 0.7, 0.6], -0.9, 0.12, 2.2, 60), ambient: [0.05, 0.05, 0.06], horizon: [0.80, 0.85, 0.92], zenith: [0.25, 0.40, 0.75],
      shapes: [
        plane('床(市松)', [0, 1, 0], 0, checker(g(0.72), g(0.06), 0.5)),
        plane('天井', [0, 1, 0], 2.4, diffuse([0.60, 0.58, 0.55])),
        plane('鏡(x=-1.6)', [1, 0, 0], -1.6, mirrorM),
        plane('鏡(x=+1.6)', [-1, 0, 0], -1.6, mirrorM),
        sphere('赤い玉', [0, 0.5, 0.6], 0.5, diffuse([0.75, 0.10, 0.08], 0.3)),
        sphere('ガラス玉', [0.7, 0.35, -0.3], 0.35, glass(1.5)),
        sphere('金の玉', [-0.7, 0.3, 1.6], 0.3, metal([1, 0.71, 0.29])),
      ],
      lights: [{ name: '光1', p: [0, 1.9, 0.4], c: [1, 1, 1], i: 15 }],
    };
  },
  cornell: () => {
    const white = diffuse(g(0.73)), red = diffuse([0.63, 0.065, 0.05]), green = diffuse([0.14, 0.45, 0.091]);
    return {
      name: 'コーネルボックス', view: orbit([0, 1.2, 0.35], 0, 0.05, 3.6, 45), ambient: g(0.05), horizon: ZERO, zenith: ZERO,
      shapes: [
        plane('床', [0, 1, 0], 0, white), plane('天井', [0, 1, 0], 2.6, white),
        plane('左の壁(赤)', [1, 0, 0], 1.3, red), plane('右の壁(緑)', [1, 0, 0], -1.3, green),
        plane('奥の壁', [0, 0, 1], 2.6, white), plane('手前の壁', [0, 0, 1], -4.2, white),
        sphere('光る球', [0, 2.2, 0.3], 0.35, light([22, 21, 19])),
        sphere('鏡の玉', [-0.62, 0.45, 0.5], 0.45, metal(g(0.92))),
        sphere('白い玉', [0.70, 0.5, 0.95], 0.5, diffuse(g(0.73))),
      ],
      lights: [],
    };
  },
  sphereField: () => {
    const shapes = [plane('床(市松)', [0, 1, 0], 0, checker(g(0.62), g(0.28), 1))];
    const anchors = [[[0, 0.62, 0], 0.62], [[-2.3, 0.62, 0.4], 0.62], [[2.3, 0.62, -0.4], 0.62]];
    const rnd = dotNetRandom(60);
    for (let a = -11; a < 11; a++) {
      for (let b = -11; b < 11; b++) {
        const c = [a * 0.55 + 0.34 * rnd(), 0.11, b * 0.55 + 0.34 * rnd()];
        if (anchors.some(([ac, ar]) => len(sub(c, ac)) < ar + 0.35)) continue;
        const roll = rnd();
        const m = roll < 0.70 ? diffuse([rnd() * rnd(), rnd() * rnd(), rnd() * rnd()])
          : roll < 0.90 ? metal([0.5 + 0.5 * rnd(), 0.5 + 0.5 * rnd(), 0.5 + 0.5 * rnd()]) : glass(1.5);
        shapes.push(sphere(`小さな玉(${a},${b})`, c, 0.11, m));
      }
    }
    shapes.push(sphere('ガラスの大玉', anchors[0][0], 0.62, glass(1.5)), sphere('赤い大玉', anchors[1][0], 0.62, diffuse([0.62, 0.14, 0.10])), sphere('金の大玉', anchors[2][0], 0.62, metal([1, 0.71, 0.29])));
    return {
      name: '球がたくさん', view: orbit([0, 0.55, 0], 0.25, 0.17, 9.0, 32), ambient: [0.10, 0.11, 0.13], horizon: [0.85, 0.90, 1.00], zenith: [0.32, 0.52, 0.95],
      shapes, lights: [{ name: '光', p: [-6, 9, -7], c: [1, 0.97, 0.92], i: 420 }],
    };
  },
  caustic: () => ({
    name: 'ガラスの集光', view: orbit([0, 0.45, 0.55], 0.10, 0.34, 5.0, 40), ambient: g(0.03), horizon: [0.05, 0.06, 0.09], zenith: [0.02, 0.03, 0.07],
    shapes: [
      plane('床', [0, 1, 0], 0, diffuse(g(0.70))),
      sphere('ガラス玉', [0, 1, 0], 0.55, glass(1.5)),
      sphere('白い玉', [1.7, 0.5, 0.3], 0.5, diffuse(g(0.72))),
      sphere('光る球', [0, 4.2, -1.2], 0.5, light([45, 44, 40])),
    ],
    lights: [],
  }),
};
// Day ごとの場面の並び(キーの 1〜6)
const LIBRARY = {
  59: ['whitted', 'materialRow', 'mirror'],
  '60a': ['whitted', 'materialRow', 'mirror', 'cornell', 'caustic'],
  '60b': ['whitted', 'materialRow', 'mirror', 'cornell', 'sphereField', 'caustic'],
};
const sky = (s, d) => { const u = Math.sqrt(clamp(d[1], 0, 1)); return s.horizon.map((h, k) => h + (s.zenith[k] - h) * u); };

// ---- AABB とスラブ法(Aabb.cs)----
const surfaceArea = (b) => { if (b.min[0] > b.max[0]) return 0; const d = sub(b.max, b.min); return 2 * (d[0] * d[1] + d[1] * d[2] + d[2] * d[0]); };
const emptyBox = () => ({ min: [Infinity, Infinity, Infinity], max: [-Infinity, -Infinity, -Infinity] });
const union = (a, b) => ({ min: a.min.map((v, k) => Math.min(v, b.min[k])), max: a.max.map((v, k) => Math.max(v, b.max[k])) });
const unionPoint = (a, p) => ({ min: a.min.map((v, k) => Math.min(v, p[k])), max: a.max.map((v, k) => Math.max(v, p[k])) });
const centroid = (b) => mul(add(b.min, b.max), 0.5);
const longestAxis = (b) => { const d = sub(b.max, b.min); return d[0] >= d[1] && d[0] >= d[2] ? 0 : d[1] >= d[2] ? 1 : 2; };
// minMax = true は、比較ではなく Math.min / Math.max で書いた版(NaN を拾ってしまう。Day 60b の自己チェック17の比べる相手)
function slabHit(b, o, inv, tMin, tMax, minMax = false) {
  let enter = tMin, exit = tMax;
  for (let k = 0; k < 3; k++) {
    let t0 = (b.min[k] - o[k]) * inv[k], t1 = (b.max[k] - o[k]) * inv[k];
    if (minMax) { const lo = Math.min(t0, t1), hi = Math.max(t0, t1); enter = Math.max(enter, lo); exit = Math.min(exit, hi); continue; }
    if (t0 > t1) [t0, t1] = [t1, t0];
    if (t0 > enter) enter = t0;
    if (t1 < exit) exit = t1;
  }
  return enter <= exit;
}

// ---- BVH(Bvh.cs)----
const MAX_SHAPES_PER_LEAF = 4, TRAVERSAL_COST = 0.125;
function buildBvh(shapes, mode) {
  const ordered = shapes.slice(), bounds = shapes.map((s) => s.bounds()), cents = bounds.map(centroid);
  const nodes = [];
  let leafCount = 0, maxDepth = 0;
  // 並べ替えは安定にしてある(.NET の Array.Sort は安定でないが、場面5は重心の値が軸ごとにほぼ重ならないので同じ木になる)
  function sortRange(first, count, axis) {
    const idx = []; for (let i = 0; i < count; i++) idx.push(first + i);
    idx.sort((p, q) => cents[p][axis] - cents[q][axis]);
    const s = idx.map((i) => ordered[i]), b = idx.map((i) => bounds[i]), c = idx.map((i) => cents[i]);
    for (let i = 0; i < count; i++) { ordered[first + i] = s[i]; bounds[first + i] = b[i]; cents[first + i] = c[i]; }
  }
  function medianSplit(first, count) {
    let cb = emptyBox();
    for (let i = first; i < first + count; i++) cb = unionPoint(cb, cents[i]);
    const axis = longestAxis(cb);
    if (cb.max[axis] - cb.min[axis] <= 0) return { split: -1, axis };
    sortRange(first, count, axis);
    return { split: first + Math.floor(count / 2), axis };
  }
  function sahSplit(first, count, parent) {
    const parentArea = surfaceArea(parent), rightAreas = new Array(count).fill(0);
    let bestCost = count, bestAxis = -1, bestSplit = -1;
    for (let a = 0; a < 3; a++) {
      sortRange(first, count, a);
      let right = emptyBox();
      for (let i = count - 1; i > 0; i--) { right = union(right, bounds[first + i]); rightAreas[i] = surfaceArea(right); }
      let left = emptyBox();
      for (let i = 1; i < count; i++) {
        left = union(left, bounds[first + i - 1]);
        const cost = TRAVERSAL_COST + (surfaceArea(left) * i + rightAreas[i] * (count - i)) / parentArea;
        if (cost < bestCost) { bestCost = cost; bestAxis = a; bestSplit = first + i; }
      }
    }
    if (bestAxis < 0) return { split: -1, axis: 0 };
    if (bestAxis !== 2) sortRange(first, count, bestAxis);
    return { split: bestSplit, axis: bestAxis };
  }
  function buildRange(first, count, depth) {
    maxDepth = Math.max(maxDepth, depth);
    const self = nodes.length;
    let box = emptyBox();
    for (let i = first; i < first + count; i++) box = union(box, bounds[i]);
    nodes.push({ box, index: 0, count: 0, axis: 0, depth });
    let r = { split: -1, axis: 0 };
    if (count > MAX_SHAPES_PER_LEAF) r = mode === 'sah' ? sahSplit(first, count, box) : medianSplit(first, count);
    if (r.split < 0) { nodes[self].index = first; nodes[self].count = count; leafCount++; return self; }
    nodes[self].axis = r.axis;
    buildRange(first, r.split - first, depth + 1);
    nodes[self].index = buildRange(r.split, first + count - r.split, depth + 1);
    return self;
  }
  if (shapes.length) buildRange(0, shapes.length, 1);
  return { nodes, shapes: ordered, nodeCount: nodes.length, leafCount, maxDepth };
}
// 近い子から辿る(Bvh.Intersect)。visit(i) で辿った節点を受け取れる(実験台の絵のため)
function bvhIntersect(bvh, o, d, cnt, best, visit) {
  if (!bvh.nodes.length) return best;
  const inv = [1 / d[0], 1 / d[1], 1 / d[2]], neg = inv.map((v) => v < 0);
  const stack = [];
  let cur = 0;
  for (;;) {
    cnt.boxTests++;
    const nd = bvh.nodes[cur];
    if (slabHit(nd.box, o, inv, 0, best.t)) {
      if (visit) visit(cur);
      if (nd.count > 0) {
        cnt.shapeTests += nd.count;
        for (let i = nd.index; i < nd.index + nd.count; i++) { const t = bvh.shapes[i].hit(o, d, 0, best.t); if (t >= 0) best = { t, shape: bvh.shapes[i] }; }
      } else {
        let left = cur + 1, right = nd.index;
        if (neg[nd.axis]) [left, right] = [right, left];
        stack.push(right); cur = left; continue;
      }
    }
    if (!stack.length) break;
    cur = stack.pop();
  }
  return best;
}
function bvhOccluder(bvh, o, d, maxD, cnt) {
  if (!bvh.nodes.length) return null;
  const inv = [1 / d[0], 1 / d[1], 1 / d[2]], stack = [];
  let cur = 0;
  for (;;) {
    cnt.boxTests++;
    const nd = bvh.nodes[cur];
    if (slabHit(nd.box, o, inv, 0, maxD)) {
      if (nd.count > 0) {
        cnt.shapeTests += nd.count;
        for (let i = nd.index; i < nd.index + nd.count; i++) if (bvh.shapes[i].hit(o, d, 0, maxD) >= 0) return bvh.shapes[i];
      } else { stack.push(nd.index); cur++; continue; }
    }
    if (!stack.length) return null;
    cur = stack.pop();
  }
}
// Scene.Prepare。mode は 'brute' / 'median' / 'sah'
function prepare(scene, mode = 'brute') {
  scene.mode = mode;
  scene.areaLights = scene.shapes.filter((s) => s.m.emissive);
  if (mode === 'brute') { scene.unbounded = scene.shapes.slice(); scene.bvh = null; return scene; }
  scene.unbounded = scene.shapes.filter((s) => !s.bounds());
  scene.bvh = buildBvh(scene.shapes.filter((s) => s.bounds()), mode);
  return scene;
}
function intersect(scene, o, d, cnt) {
  let best = { t: Infinity, shape: null };
  cnt.shapeTests += scene.unbounded.length;
  for (const s of scene.unbounded) { const t = s.hit(o, d, 0, best.t); if (t >= 0) best = { t, shape: s }; }
  if (scene.bvh) best = bvhIntersect(scene.bvh, o, d, cnt, best);
  return best.shape ? best : null;
}
function findOccluder(scene, o, d, maxD, cnt) {
  cnt.shapeTests += scene.unbounded.length;
  for (const s of scene.unbounded) if (s.hit(o, d, 0, maxD) >= 0) return s;
  return scene.bvh ? bvhOccluder(scene.bvh, o, d, maxD, cnt) : null;
}

// ---- カメラ(Camera.cs)----
function camera(view, w, h) {
  const pos = add(view.target, mul([Math.sin(view.yaw) * Math.cos(view.pitch), Math.sin(view.pitch), -Math.cos(view.yaw) * Math.cos(view.pitch)], view.distance));
  const f = norm(sub(view.target, pos)), r = norm(cross(f, [0, 1, 0])), u = cross(r, f);
  const hh = Math.tan(view.fov * Math.PI / 360), hw = hh * w / h;
  return { pos, w, h, ray(px, py) { const nx = px / w * 2 - 1, ny = 1 - py / h * 2; return norm(add(f, add(mul(r, nx * hw), mul(u, ny * hh)))); } };
}

// ---- 追い方(Tracer.cs・WhittedTracer.cs・PathTracer.cs)----
// settings: { algorithm: 'path' | 'whitted', maxDepth, shadows, fresnel: 'exact' | 'schlick' | 'off', nee, rr, decorrelate, view }
const SURFACE_OFFSET = 1e-3, ROULETTE_START = 3, MIN_SURVIVAL = 0.05;
const counters = () => ({ camera: 0, shadow: 0, reflection: 0, refraction: 0, scatter: 0, boxTests: 0, shapeTests: 0 });
const total = (c) => c.camera + c.shadow + c.reflection + c.refraction + c.scatter;
const f3 = (v) => v.map((x) => x.toFixed(3)).join(', ');
const f2 = (v) => v.map((x) => x.toFixed(2)).join(', ');
function tracer(scene, st) {
  if (!scene.unbounded) prepare(scene, 'brute');
  const off = (p, n) => add(p, mul(n, SURFACE_OFFSET));
  const dielectric = (c, n1, n2) => (st.fresnel === 'schlick' ? schlick(c, n1, n2) : st.fresnel === 'off' ? r0(n1, n2) : fresnel(c, n1, n2));
  const metalF = (c, f0) => (st.fresnel === 'off' ? f0 : schlickConductor(c, f0));
  const blinn = (n, hv, sh) => (sh + 8) / (8 * Math.PI) * Math.max(dot(n, hv), 0) ** sh;
  const log = (L, depth, s) => { if (L) L.push('   '.repeat(Math.min(depth, 24)) + s); };
  // TryHit。法線を光線の側へ裏返す
  function tryHit(o, d, cnt) {
    const h = intersect(scene, o, d, cnt);
    if (!h) return null;
    const p = add(o, mul(d, h.t));
    let n = h.shape.normal(p);
    const front = dot(d, n) < 0;
    if (!front) n = mul(n, -1);
    return { p, n, v: mul(d, -1), front, t: h.t, m: h.shape.m, shape: h.shape };
  }

  // ---- Whitted(光線の木)----
  function reach(hit, lt, depth, cnt, L) {
    const offv = sub(lt.p, hit.p), dist = len(offv), toL = mul(offv, 1 / dist), cos = dot(hit.n, toL);
    if (cos <= 0) { log(L, depth + 1, `影: ${lt.name} は面の裏側。光線は出さない`); return null; }
    if (st.shadows !== false) {
      cnt.shadow++;
      const occ = findOccluder(scene, off(hit.p, hit.n), toL, dist, cnt);
      if (occ) { log(L, depth + 1, `影 → ${lt.name}: ${occ.name} に遮られた`); return null; }
      log(L, depth + 1, `影 → ${lt.name}: 届いた`);
    }
    return { toL, e: mul(lt.c, lt.i / (dist * dist) * cos) };
  }
  function highlights(hit, depth, cnt, L) {
    let sum = ZERO;
    for (const lt of scene.lights) {
      const r = reach(hit, lt, depth, cnt, L);
      if (!r) continue;
      const hv = norm(add(hit.v, r.toL)), cosH = dot(hit.v, hv);
      const f = hit.m.kind === 'metal' ? metalF(cosH, hit.m.albedo) : g(dielectric(cosH, 1, hit.m.ior));
      sum = add(sum, mulv(f, mul(r.e, blinn(hit.n, hv, hit.m.shininess))));
    }
    return sum;
  }
  function whitted(o, d, depth, cnt, L, label) {
    const hit = tryHit(o, d, cnt);
    if (!hit) { log(L, depth, `${label} → 空`); return sky(scene, d); }
    log(L, depth, `${label} → ${hit.shape.name}  ${hit.t.toFixed(3)}m 先${hit.front ? '' : '(内側から)'}`);
    const m = hit.m, p = hit.p, n = hit.n;
    if (m.kind === 'diffuse') {
      const a = albedoAt(m, p);
      let c = add(m.emission, mulv(a, scene.ambient));
      for (const lt of scene.lights) {
        const r = reach(hit, lt, depth, cnt, L);
        if (!r) continue;
        const hv = norm(add(hit.v, r.toL)), spec = m.specular * blinn(n, hv, m.shininess);
        c = add(c, mulv(add(mul(a, 1 / Math.PI), g(spec)), r.e));
      }
      return c;
    }
    if (m.kind === 'metal') {
      const c = highlights(hit, depth, cnt, L);
      if (depth >= st.maxDepth) { log(L, depth + 1, '反射: 深さの上限なので追わない(黒)'); return c; }
      const f = metalF(dot(n, hit.v), m.albedo);
      cnt.reflection++;
      log(L, depth + 1, `反射の割合 (${f2(f)})`);
      return add(c, mulv(f, whitted(off(p, n), norm(reflect(d, n)), depth + 1, cnt, L, '反射')));
    }
    const n1 = hit.front ? 1 : m.ior, n2 = hit.front ? m.ior : 1, cosI = dot(n, hit.v);
    const refr = refract(d, n, n1 / n2), F = refr ? dielectric(cosI, n1, n2) : 1;
    const ang = Math.acos(clamp(cosI, 0, 1)) * 180 / Math.PI;
    log(L, depth + 1, refr
      ? `屈折率 ${n1.toFixed(2)} → ${n2.toFixed(2)}  入射角 ${ang.toFixed(1)}度  反射 ${F.toFixed(3)} / 透過 ${(1 - F).toFixed(3)}`
      : `屈折率 ${n1.toFixed(2)} → ${n2.toFixed(2)}  入射角 ${ang.toFixed(1)}度  全反射(透過の角度が無い)`);
    let c = hit.front ? highlights(hit, depth, cnt, L) : ZERO;
    if (depth >= st.maxDepth) { log(L, depth + 1, '反射・屈折: 深さの上限なので追わない(黒)'); return c; }
    if (F > 0) { cnt.reflection++; c = add(c, mul(whitted(off(p, n), norm(reflect(d, n)), depth + 1, cnt, L, refr ? '反射' : '全反射'), F)); }
    if (refr && F < 1) { cnt.refraction++; c = add(c, mul(whitted(off(p, mul(n, -1)), norm(refr), depth + 1, cnt, L, '屈折'), 1 - F)); }
    return c;
  }

  // ---- パストレーシング(道を1本歩く)----
  function directLight(hit, a, depth, rng, cnt, L) {
    const pointCount = scene.lights.length, totalLights = pointCount + scene.areaLights.length;
    if (!totalLights) return ZERO;
    const brdf = mul(a, 1 / Math.PI), pick = rng.nextInt(totalLights);
    if (pick < pointCount) {
      const lt = scene.lights[pick], offv = sub(lt.p, hit.p), dist = len(offv), toL = mul(offv, 1 / dist), cos = dot(hit.n, toL);
      if (cos <= 0) { log(L, depth + 1, `NEE → ${lt.name}: 面の裏側。光線は出さない`); return ZERO; }
      cnt.shadow++;
      const occ = findOccluder(scene, off(hit.p, hit.n), toL, dist, cnt);
      if (occ) { log(L, depth + 1, `NEE → ${lt.name}: ${occ.name} に遮られた`); return ZERO; }
      const contrib = mul(mulv(brdf, mul(lt.c, lt.i / (dist * dist) * cos)), totalLights);
      log(L, depth + 1, `NEE → ${lt.name}: 届いた  寄与 (${f3(contrib)})`);
      return contrib;
    }
    const ls = scene.areaLights[pick - pointCount], smp = ls.sampleDirection(hit.p, rng);
    if (!smp || smp.pdf <= 0) return ZERO;
    const cosS = dot(hit.n, smp.dir);
    if (cosS <= 0) { log(L, depth + 1, `NEE → ${ls.name}: 面の裏側`); return ZERO; }
    cnt.shadow++;
    const blocker = findOccluder(scene, off(hit.p, hit.n), smp.dir, Math.max(smp.distance - 2 * SURFACE_OFFSET, 0), cnt);
    if (blocker) { log(L, depth + 1, `NEE → ${ls.name}: ${blocker.name} に遮られた`); return ZERO; }
    const contrib = mul(mulv(brdf, ls.m.emission), cosS / smp.pdf * totalLights);
    log(L, depth + 1, `NEE → ${ls.name}: 届いた  pdf ${smp.pdf.toFixed(3)}  寄与 (${f3(contrib)})`);
    return contrib;
  }
  function path(o, d, rng, cnt, L) {
    let radiance = ZERO, throughput = [1, 1, 1], specular = true, label = 'カメラ';
    for (let depth = 0; ; depth++) {
      const hit = tryHit(o, d, cnt);
      if (!hit) { const s = sky(scene, d); radiance = add(radiance, mulv(throughput, s)); log(L, depth, `${label} → 空 (${f2(s)})`); break; }
      const m = hit.m;
      log(L, depth, `${label} → ${hit.shape.name}  ${hit.t.toFixed(3)}m 先${hit.front ? '' : '(内側から)'}`);
      if (m.emissive) {
        const counted = st.nee && !specular;
        if (!counted) radiance = add(radiance, mulv(throughput, m.emission));
        log(L, depth + 1, counted ? '発光: NEE で数え済みなので足さない(2重に数えないため)' : `発光 (${m.emission.map((x) => x.toFixed(1)).join(', ')}) を足す`);
        break;
      }
      if (m.kind === 'diffuse') {
        const a = albedoAt(m, hit.p);
        if (st.nee) radiance = add(radiance, mulv(throughput, directLight(hit, a, depth, rng, cnt, L)));
        if (depth >= st.maxDepth) { log(L, depth + 1, '散乱: 深さの上限なので追わない(この先の光は届かない)'); break; }
        const sc = cosineHemisphere(hit.n, rng);
        throughput = mulv(throughput, a); specular = false; label = '散乱'; cnt.scatter++;
        o = off(hit.p, hit.n); d = norm(sc);
        log(L, depth + 1, `散乱(拡散) 重み (${f2(a)}) → 累積 (${f3(throughput)})`);
      } else {
        if (depth >= st.maxDepth) { log(L, depth + 1, '反射・屈折: 深さの上限なので追わない'); break; }
        const cosI = dot(hit.n, hit.v);
        if (m.kind === 'metal') {
          const f = metalF(cosI, m.albedo);
          throughput = mulv(throughput, f); label = '反射'; cnt.reflection++;
          log(L, depth + 1, `反射(金属) 重み (${f2(f)}) → 累積 (${f3(throughput)})`);
          o = off(hit.p, hit.n); d = norm(reflect(d, hit.n));
        } else {
          const n1 = hit.front ? 1 : m.ior, n2 = hit.front ? m.ior : 1;
          const refr = refract(d, hit.n, n1 / n2), F = refr ? dielectric(cosI, n1, n2) : 1;
          if (rng.nextFloat() < F) {
            label = refr ? '反射' : '全反射'; cnt.reflection++;
            log(L, depth + 1, `屈折率 ${n1.toFixed(2)} → ${n2.toFixed(2)}  反射の確率 ${F.toFixed(3)} → ${label}を選んだ(重みは 1 のまま)`);
            o = off(hit.p, hit.n); d = norm(reflect(d, hit.n));
          } else {
            label = '屈折'; cnt.refraction++;
            log(L, depth + 1, `屈折率 ${n1.toFixed(2)} → ${n2.toFixed(2)}  反射の確率 ${F.toFixed(3)} → 屈折を選んだ(重みは 1 のまま)`);
            o = off(hit.p, mul(hit.n, -1)); d = norm(refr);
          }
        }
        specular = true;
      }
      if (st.rr && depth >= ROULETTE_START) {
        const surv = clamp(Math.max(...throughput), MIN_SURVIVAL, 1);
        if (rng.nextFloat() >= surv) { log(L, depth + 1, `ロシアンルーレット: 続ける確率 ${surv.toFixed(3)} → 打ち切り`); break; }
        throughput = mul(throughput, 1 / surv);
        log(L, depth + 1, `ロシアンルーレット: 続ける確率 ${surv.toFixed(3)} → 続ける(重みを ${(1 / surv).toFixed(2)} 倍)`);
      }
    }
    return radiance;
  }

  const trace = (o, d, rng, cnt, L) => (st.algorithm === 'whitted' ? whitted(o, d, 0, cnt, L, 'カメラ') : path(o, d, rng, cnt, L));
  // Tracer.Sample。表示の切り替え
  function sample(cam, px, py, rng, cnt) {
    const d = cam.ray(px, py), raysBefore = total(cnt), testsBefore = cnt.boxTests + cnt.shapeTests;
    cnt.camera++;
    if (st.view === 'normal') { const hit = tryHit(cam.pos, d, cnt); return hit ? hit.n.map((v) => v * 0.5 + 0.5) : ZERO; }
    const c = trace(cam.pos, d, rng, cnt, null);
    if (st.view === 'rays') return heat(total(cnt) - raysBefore, 6);
    if (st.view === 'cost') return heat(cnt.boxTests + cnt.shapeTests - testsBefore, 10);
    return c;
  }
  // Tracer.TracePixel。画素の真ん中を1本だけ追い、木(道)を行で返す
  function tracePixel(cam, x, y, sampleIndex = 0) {
    const L = [], cnt = counters();
    cnt.camera++;
    const rng = Rng.create(x, y, sampleIndex, st.decorrelate !== false);
    const color = trace(cam.pos, cam.ray(x + 0.5, y + 0.5), rng, cnt, L);
    return { lines: L, color, counters: cnt };
  }
  return { trace, whitted, sample, tracePixel, scene, settings: st };
}

// HeatColor(数を 2倍ごとに1段。maxPower 段で白)
function heat(n, maxPower = 6) {
  const stops = [[0, 0, 0.3], [0, 0.3, 1], [0, 0.9, 0.9], [0.2, 0.9, 0.1], [1, 0.9, 0], [1, 0.2, 0], [1, 1, 1]];
  const scale = (stops.length - 1) / maxPower;
  const level = clamp(Math.log2(Math.max(n, 1)) * scale, 0, stops.length - 1), i = Math.min(Math.floor(level), stops.length - 2), f = level - i;
  return stops[i].map((a, k) => a + (stops[i + 1][k] - a) * f);
}
const radicalInverse = (index, radix) => { let r = 0, f = 1 / radix; while (index > 0) { r += (index % radix) * f; index = Math.floor(index / radix); f /= radix; } return r; };
const sampleOffset = (s) => (s === 0 ? [0.5, 0.5] : [radicalInverse(s, 2), radicalInverse(s, 3)]);
const linearToSrgb = (v) => (!(v > 0) ? 0 : v >= 1 ? 1 : v <= 0.0031308 ? v * 12.92 : 1.055 * v ** (1 / 2.4) - 0.055);

const api = {
  add, sub, mul, mulv, dot, len, norm, cross, clamp,
  reflect, refract, fresnel, schlick, schlickNaive, schlickConductor, r0,
  Rng, basis, uniformHemisphere, cosineHemisphere, uniformCone, uniformHemispherePdf, cosineHemispherePdf, uniformConePdf,
  diffuse, checker, metal, glass, light, albedoAt, sphere, plane, dotNetRandom,
  SCENES, LIBRARY, scene: (key) => SCENES[key](), library: (day) => LIBRARY[day].map((k) => SCENES[k]()), sky,
  surfaceArea, slabHit, buildBvh, bvhIntersect, prepare, intersect, findOccluder,
  camera, tracer, counters, total, heat, radicalInverse, sampleOffset, linearToSrgb, SURFACE_OFFSET,
};
if (typeof module !== 'undefined') module.exports = api; else window.CpuRayTracer = api;
})();
