// ============================================================
//  メッシュレットの実験台が共有する部品。window.Meshlet にまとめて置く
//  Day 64a・64b の実験台が使う(Day 65 以降も同じトーラスノットと切り方を使う)。
//  reference の Geometry/TorusKnot.cs・Geometry/MeshletBuilder.cs・Scene/SceneData.cs・Scene/Camera.cs と、
//  shaders/meshlet.task の判定を JS に移したもの(GPU は使わない)。
//  式・順番・上限の既定値(頂点 64・三角形 124)は C# と同じ。計算は double で、位置と重心だけ float に丸めて持つ。
//  切り方の結果(個数・番号・球・円錐)と、体の並べ方・カメラの6面・カリングの数は C# の検証用プロジェクトと一致を確かめてある。
// ============================================================
(() => {
'use strict';
const f32 = Math.fround;
const TUBULAR = 512, RADIAL = 32;
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const len = (a) => Math.hypot(a[0], a[1], a[2]);
const norm = (a) => { const l = len(a); return [a[0] / l, a[1] / l, a[2] / l]; };

// ------------------------------------------------------------
//  トーラスノット(TorusKnot.Create)。頂点 16,384 個・三角形 32,768 枚。三角形は帯ごと(線に沿った j 番目と j+1 番目の円の間を1周)に並ぶ
//  メッシュレットの「育てる(丸く)」は、距離の同点の決まり方が float の丸めで変わるので、C# と同じ順に float で計算する(f32 で丸める)
// ------------------------------------------------------------
const vsub = (a, b) => [f32(a[0] - b[0]), f32(a[1] - b[1]), f32(a[2] - b[2])];
const vadd = (a, b) => [f32(a[0] + b[0]), f32(a[1] + b[1]), f32(a[2] + b[2])];
const vscale = (s, a) => [f32(s * a[0]), f32(s * a[1]), f32(s * a[2])];
const vcross = (a, b) => [f32(f32(a[1] * b[2]) - f32(a[2] * b[1])), f32(f32(a[2] * b[0]) - f32(a[0] * b[2])), f32(f32(a[0] * b[1]) - f32(a[1] * b[0]))];
const vnorm = (a) => { const l = f32(Math.sqrt(f32(f32(f32(a[0] * a[0]) + f32(a[1] * a[1])) + f32(a[2] * a[2])))); return [f32(a[0] / l), f32(a[1] / l), f32(a[2] / l)]; };
const fcos = (x) => f32(Math.cos(x)), fsin = (x) => f32(Math.sin(x));
const fdot = (a, b) => f32(f32(f32(a[0] * b[0]) + f32(a[1] * b[1])) + f32(a[2] * b[2]));
const fl = (a) => f32(Math.sqrt(fdot(a, a)));
const F_PI = f32(Math.PI);
// C# の MathF.Cos / MathF.Sin(float)は、正しく丸めた値(JS の Math.cos を float に丸めたもの)と 1 ULP 違う答えを返すことがある。
// トーラスノットの 16,384 頂点を C# と全ビット突き合わせて見つかった7か所(キーは「j + a か b(p1 か p2) + 呼び出し」)だけ、C# の値に合わせる
const TRIG_FIX = { '33bcu': 1, '95bsu': -1, '277bcq': -1, '354bsu': 1, '406asq': 1, '419bcu': -1, '484bsu': -1 };
const ulp = (x, d) => { if (!d) return x; const b = new DataView(new ArrayBuffer(4)); b.setFloat32(0, x); b.setInt32(0, b.getInt32(0) + (x < 0 ? -d : d)); return b.getFloat32(0); };
const tc = (x, key) => ulp(fcos(x), TRIG_FIX[key]), ts = (x, key) => ulp(fsin(x), TRIG_FIX[key]);
function pointOnCurve(u, p, q, radius, tag) {
  const qu = f32(f32(q / p) * u);
  const r = f32(f32(radius * f32(2 + tc(qu, tag + 'cq'))) * 0.5);
  return [f32(r * tc(u, tag + 'cu')), f32(r * ts(u, tag + 'su')), f32(f32(radius * ts(qu, tag + 'sq')) * 0.5)];
}
function createTorusKnot(p = 2, q = 3, radius = 1.0, tube = 0.28) {
  radius = f32(radius); tube = f32(tube);
  const pos = new Float32Array(TUBULAR * RADIAL * 3), nor = new Float32Array(TUBULAR * RADIAL * 3);
  for (let j = 0; j < TUBULAR; j++) {
    const u = f32(f32(f32(f32(j / TUBULAR) * p) * F_PI) * 2);
    const p1 = pointOnCurve(u, p, q, radius, j + 'a'), p2 = pointOnCurve(f32(u + f32(0.01)), p, q, radius, j + 'b');
    const tangent = vsub(p2, p1);
    let normal = vadd(p2, p1);
    const binormal = vnorm(vcross(tangent, normal));
    normal = vnorm(vcross(binormal, tangent));
    for (let i = 0; i < RADIAL; i++) {
      const v = f32(f32(f32(i / RADIAL) * F_PI) * 2);
      const cx = f32(-tube * fcos(v)), cy = f32(tube * fsin(v));
      const P = vadd(vadd(p1, vscale(cx, normal)), vscale(cy, binormal));
      const k = (j * RADIAL + i) * 3;
      pos.set(P, k); nor.set(vnorm(vsub(P, p1)), k);
    }
  }
  const idx = new Uint32Array(TUBULAR * RADIAL * 6);
  let k = 0;
  for (let j = 0; j < TUBULAR; j++) {
    const jn = (j + 1) % TUBULAR;
    for (let i = 0; i < RADIAL; i++) {
      const inx = (i + 1) % RADIAL;
      const a = j * RADIAL + i, b = jn * RADIAL + i, c = jn * RADIAL + inx, d = j * RADIAL + inx;
      idx[k++] = a; idx[k++] = b; idx[k++] = d;
      idx[k++] = b; idx[k++] = c; idx[k++] = d;
    }
  }
  return { pos, nor, idx, vertexCount: TUBULAR * RADIAL, triangleCount: idx.length / 3 };
}

// ------------------------------------------------------------
//  メッシュレットに切る(MeshletBuilder)
// ------------------------------------------------------------
// MeshletWriter。作りかけのメッシュレットを1つ持ち、閉じるたびに大きな配列へ書き出す。閉じるときに球と円錐も作る(Day 64b)
class MeshletWriter {
  constructor(mesh, maxV, maxT) {
    this.mesh = mesh; this.maxV = maxV; this.maxT = maxT;
    this.local = new Int32Array(mesh.vertexCount).fill(-1);
    this.verts = []; this.tris = [];
    this.meshlets = []; this.allVerts = []; this.allTris = []; this.triMeshlet = []; this.ordered = [];
  }
  newVertexCount(t) {
    let n = 0;
    for (let c = 0; c < 3; c++) if (this.local[this.mesh.idx[t * 3 + c]] < 0) n++;
    return n;
  }
  canAdd(t) { return this.tris.length < this.maxT && this.verts.length + this.newVertexCount(t) <= this.maxV; }
  add(t) {
    let packed = 0;
    for (let c = 0; c < 3; c++) {
      const v = this.mesh.idx[t * 3 + c];
      if (this.local[v] < 0) { this.local[v] = this.verts.length; this.verts.push(v); }
      packed = (packed | (this.local[v] << (c * 8))) >>> 0; // i0 | i1 << 8 | i2 << 16
      this.ordered.push(v);
    }
    this.tris.push(packed);
  }
  P(v) { const p = this.mesh.pos; return [p[v * 3], p[v * 3 + 1], p[v * 3 + 2]]; }
  // 境界の球。頂点を囲む箱の中心を中心にして、いちばん遠い頂点までを半径にする
  sphere() {
    const mn = [Infinity, Infinity, Infinity], mx = [-Infinity, -Infinity, -Infinity];
    for (const v of this.verts) { const p = this.P(v); for (let a = 0; a < 3; a++) { mn[a] = Math.min(mn[a], p[a]); mx[a] = Math.max(mx[a], p[a]); } }
    const c = [f32(f32(mn[0] + mx[0]) * 0.5), f32(f32(mn[1] + mx[1]) * 0.5), f32(f32(mn[2] + mx[2]) * 0.5)];
    let r = 0;
    for (const v of this.verts) { const d = vsub(c, this.P(v)); r = Math.max(r, f32(Math.sqrt(f32(f32(f32(d[0] * d[0]) + f32(d[1] * d[1])) + f32(d[2] * d[2]))))); }
    return [c[0], c[1], c[2], r];
  }
  // 法線の円錐。軸 = 面の法線の平均の向き、w = sin α(α が 90 度以上で使えないときは 1)
  cone() {
    const normals = [];
    let sum = [0, 0, 0];
    for (const packed of this.tris) {
      const p0 = this.P(this.verts[packed & 255]), p1 = this.P(this.verts[(packed >> 8) & 255]), p2 = this.P(this.verts[(packed >> 16) & 255]);
      const n = vnorm(vcross(vsub(p1, p0), vsub(p2, p0)));
      normals.push(n); sum = vadd(sum, n);
    }
    if (fl(sum) < f32(1e-3)) return [0, 0, 1, 1];
    const axis = vnorm(sum);
    let minCos = 1;
    for (const n of normals) minCos = Math.min(minCos, fdot(n, axis));
    if (minCos <= 0) return [axis[0], axis[1], axis[2], 1];
    return [axis[0], axis[1], axis[2], f32(Math.sqrt(f32(1 - f32(minCos * minCos))))];
  }
  flush() {
    if (this.tris.length === 0) return;
    const index = this.meshlets.length;
    this.meshlets.push({
      vertexOffset: this.allVerts.length, triangleOffset: this.allTris.length, vertexCount: this.verts.length, triangleCount: this.tris.length,
      sphere: this.sphere(), cone: this.cone(),
    });
    for (const v of this.verts) this.allVerts.push(v);
    for (const t of this.tris) { this.allTris.push(t); this.triMeshlet.push(index); }
    for (const v of this.verts) this.local[v] = -1;
    this.verts = []; this.tris = [];
  }
}

// 並び順のまま切る
function buildScan(mesh, w) {
  for (let t = 0; t < mesh.triangleCount; t++) {
    if (!w.canAdd(t)) w.flush();
    w.add(t);
  }
  w.flush();
}

// 育てる。種の三角形から、新しい頂点がいちばん増えない隣を足していく。
// 同点のとき: grow は候補に入った順、round は(いま入っている三角形の重心の平均に)中心が近い順
function buildGrow(mesh, w, round) {
  const tc = mesh.triangleCount, indices = mesh.idx;
  const first = new Int32Array(mesh.vertexCount + 1);
  for (let i = 0; i < indices.length; i++) first[indices[i] + 1]++;
  for (let v = 0; v < mesh.vertexCount; v++) first[v + 1] += first[v];
  const cursor = first.slice();
  const trisOfVertex = new Int32Array(indices.length);
  for (let i = 0; i < indices.length; i++) trisOfVertex[cursor[indices[i]]++] = (i / 3) | 0;

  // 三角形の重心(float)
  const cen = new Float32Array(tc * 3);
  for (let t = 0; t < tc; t++) {
    for (let a = 0; a < 3; a++) cen[t * 3 + a] = f32(f32(f32(mesh.pos[indices[t * 3] * 3 + a] + mesh.pos[indices[t * 3 + 1] * 3 + a]) + mesh.pos[indices[t * 3 + 2] * 3 + a]) / 3);
  }
  let sx = 0, sy = 0, sz = 0, taken = 0;

  const used = new Uint8Array(tc), queued = new Uint8Array(tc);
  let candidates = [];
  let seed = 0;
  const take = (t) => {
    used[t] = 1;
    w.add(t);
    sx = f32(sx + cen[t * 3]); sy = f32(sy + cen[t * 3 + 1]); sz = f32(sz + cen[t * 3 + 2]); taken++;
    for (let c = 0; c < 3; c++) {
      const v = indices[t * 3 + c];
      for (let k = first[v]; k < first[v + 1]; k++) {
        const n = trisOfVertex[k];
        if (!used[n] && !queued[n]) { queued[n] = 1; candidates.push(n); }
      }
    }
  };
  for (;;) {
    while (seed < tc && used[seed]) seed++;
    if (seed === tc) break;
    take(seed);
    for (;;) {
      let best = -1, bestNew = 0x7fffffff, bestDist = 3.4028234663852886e38, write = 0;
      const cx = f32(sx / taken), cy = f32(sy / taken), cz = f32(sz / taken);
      for (let read = 0; read < candidates.length; read++) {
        const t = candidates[read];
        if (used[t]) continue;
        candidates[write++] = t;
        if (!round && best >= 0 && bestNew === 0) continue;
        const added = w.newVertexCount(t);
        if (added > bestNew || !w.canAdd(t)) continue;
        let d = 0;
        if (round) {
          const dx = f32(cen[t * 3] - cx), dy = f32(cen[t * 3 + 1] - cy), dz = f32(cen[t * 3 + 2] - cz);
          d = f32(f32(f32(dx * dx) + f32(dy * dy)) + f32(dz * dz));
        }
        if (added < bestNew || d < bestDist) { best = t; bestNew = added; bestDist = d; }
      }
      candidates.length = write;
      if (best < 0) break;
      take(best);
    }
    w.flush();
    sx = sy = sz = 0; taken = 0;
    for (const t of candidates) queued[t] = 0;
    candidates = [];
  }
}

// MeshletBuilder.Build。strategy は 'round'(丸く育てる)| 'grow'(育てる)| 'scan'(並び順のまま)
function build(mesh, strategy, maxV = 64, maxT = 124) {
  const w = new MeshletWriter(mesh, maxV, maxT);
  if (strategy === 'scan') buildScan(mesh, w); else buildGrow(mesh, w, strategy === 'round');
  const n = w.meshlets.length;
  const usable = w.meshlets.filter((m) => m.cone[3] < 1);
  const usableTris = usable.reduce((s, m) => s + m.triangleCount, 0);
  return {
    strategy, maxV, maxT, meshlets: w.meshlets,
    vertexIndices: Uint32Array.from(w.allVerts), triangles: Uint32Array.from(w.allTris),
    triMeshlet: Uint32Array.from(w.triMeshlet), ordered: Uint32Array.from(w.ordered),
    count: n, trisPerMeshlet: w.allTris.length / n, vertsPerMeshlet: w.allVerts.length / n,
    duplication: w.allVerts.length / mesh.vertexCount,
    hitV: w.meshlets.filter((m) => m.vertexCount === maxV).length,
    hitT: w.meshlets.filter((m) => m.triangleCount === maxT).length,
    // Day 64b: 背面の判定に使えるメッシュレットの割合と、その半角の平均(度。三角形の数で重みを付ける)
    coneUsable: usable.length / n,
    coneAvgDeg: usableTris === 0 ? 0 : usable.reduce((s, m) => s + Math.asin(m.cone[3]) * 180 / Math.PI * m.triangleCount, 0) / usableTris,
    // 見る向きが一様にばらつくとしたときに、裏向きと言える向きの割合の平均 = (1 - sin α) / 2(使えないものは 0)
    expectedBackface: w.meshlets.reduce((s, m) => s + (m.cone[3] < 1 ? (1 - m.cone[3]) / 2 : 0), 0) / n,
  };
}

// 1つのメッシュレットが、管の周り(32 升)と長さ方向(512 升)に何升ぶん広がっているか(三角形の数で重みを付けた平均)。
// 頂点番号 v は「線に沿った j = v / 32 番目の円の、周りの i = v % 32 番目」。円の上の範囲(最大の隙間の補集合)を数える
function spread(set) {
  const range = (a, n) => { a = [...a].sort((x, y) => x - y); if (a.length === 1) return 1; let g = 0; for (let k = 0; k < a.length; k++) g = Math.max(g, (k + 1 < a.length ? a[k + 1] : a[0] + n) - a[k]); return n - g + 1; };
  let around = 0, along = 0, tri = 0;
  for (const m of set.meshlets) {
    const I = new Set(), J = new Set();
    for (let k = 0; k < m.vertexCount; k++) { const v = set.vertexIndices[m.vertexOffset + k]; I.add(v % RADIAL); J.add((v / RADIAL) | 0); }
    around += range(I, RADIAL) * m.triangleCount; along += range(J, TUBULAR) * m.triangleCount; tri += m.triangleCount;
  }
  return { around: around / tri, along: along / tri };
}
// 円錐の半角の分布(10 度刻みで 9 本。使えないもの = 半角 90 度以上は別に数える)
function coneHistogram(set) {
  const bins = new Array(9).fill(0); let unusable = 0;
  for (const m of set.meshlets) { if (m.cone[3] >= 1) { unusable++; continue; } bins[Math.min(8, Math.floor(Math.asin(m.cone[3]) * 180 / Math.PI / 10))]++; }
  return { bins, unusable };
}

// 検算用: C# の検証用プロジェクトと同じ手順の FNV-1a(64 ビット)
function hash(set) {
  const M = (1n << 64n) - 1n;
  let h = 1469598103934665603n;
  const mix = (x) => { h = ((h ^ BigInt(x)) * 1099511628211n) & M; };
  for (const m of set.meshlets) { mix(m.vertexCount); mix(m.triangleCount); }
  for (const v of set.vertexIndices) mix(v);
  for (const t of set.triangles) mix(t);
  return h.toString(16);
}

// ------------------------------------------------------------
//  場面(SceneData)。体の並べ方と視点。System.Numerics と同じ「行ベクトル x 行列」で、行列は行が先の16個
// ------------------------------------------------------------
// .NET の Random(seed)(Net5CompatSeedImpl)。SceneData は Random(64) で体の向きを決める
class NetRandom {
  constructor(seed) {
    const MBIG = 2147483647, MSEED = 161803398;
    this.s = new Int32Array(56);
    let mj = MSEED - Math.abs(seed);
    this.s[55] = mj;
    let mk = 1, ii = 0;
    for (let i = 1; i < 55; i++) {
      ii += 21; if (ii >= 55) ii -= 55;
      this.s[ii] = mk;
      mk = mj - mk; if (mk < 0) mk += MBIG;
      mj = this.s[ii];
    }
    for (let k = 1; k < 5; k++) {
      for (let i = 1; i < 56; i++) {
        let n = i + 30; if (n >= 55) n -= 55;
        this.s[i] -= this.s[1 + n]; if (this.s[i] < 0) this.s[i] += MBIG;
      }
    }
    this.inext = 0; this.inextp = 21;
  }
  sample() {
    let a = this.inext + 1; if (a >= 56) a = 1;
    let b = this.inextp + 1; if (b >= 56) b = 1;
    let r = this.s[a] - this.s[b];
    if (r === 2147483647) r--;
    if (r < 0) r += 2147483647;
    this.s[a] = r; this.inext = a; this.inextp = b;
    return r * (1.0 / 2147483647);
  }
  nextSingle() { return f32(this.sample()); }
}
// float で計算する(System.Numerics は float)。cos / sin は float に丸めた値、掛け算は左から足す(足し方を変えても C# と全ビット一致する)
const matMul = (A, B) => {
  const R = new Array(16);
  for (let i = 0; i < 4; i++) for (let j = 0; j < 4; j++) {
    const p = (k) => f32(A[i * 4 + k] * B[k * 4 + j]);
    R[i * 4 + j] = f32(f32(f32(p(0) + p(1)) + p(2)) + p(3));
  }
  return R;
};
const fcosR = (x) => f32(Math.cos(x)), fsinR = (x) => f32(Math.sin(x));
const rotX = (r) => { const c = fcosR(r), s = fsinR(r); return [1, 0, 0, 0, 0, c, s, 0, 0, -s, c, 0, 0, 0, 0, 1]; };
const rotY = (r) => { const c = fcosR(r), s = fsinR(r); return [c, 0, -s, 0, 0, 1, 0, 0, s, 0, c, 0, 0, 0, 0, 1]; };
const trans = (x, y, z) => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1];
// 点を行列で運ぶ(Vector3.Transform)/ 向きを運ぶ(Vector3.TransformNormal。平行移動を含めない)
const xformPoint = (m, p) => [p[0] * m[0] + p[1] * m[4] + p[2] * m[8] + m[12], p[0] * m[1] + p[1] * m[5] + p[2] * m[9] + m[13], p[0] * m[2] + p[1] * m[6] + p[2] * m[10] + m[14]];
const xformDir = (m, p) => [p[0] * m[0] + p[1] * m[4] + p[2] * m[8], p[0] * m[1] + p[1] * m[5] + p[2] * m[9], p[0] * m[2] + p[1] * m[6] + p[2] * m[10]];

const PRESETS = [1, 16, 64, 256];
const SPACING = 4.0;
// SceneData.CreateInstances。毎回同じ並びになるよう乱数の種を固定する
function sceneInstances(count) {
  const side = Math.round(Math.sqrt(count));
  const rnd = new NetRandom(64);
  const out = [];
  for (let z = 0; z < side; z++) {
    for (let x = 0; x < side; x++) {
      const yaw = f32(f32(rnd.nextSingle() * F_PI) * 2);
      const tilt = f32(f32(rnd.nextSingle() - 0.5) * f32(0.8));
      const px = f32(f32(x - f32((side - 1) * 0.5)) * SPACING), pz = f32(f32(z - f32((side - 1) * 0.5)) * SPACING);
      out.push(matMul(matMul(rotX(tilt), rotY(yaw)), trans(px, 0, pz)));
    }
  }
  return out;
}
// SceneData.DefaultView。1体のときは寄り、それ以外は格子の中に立って斜めに見渡す
function defaultView(count) {
  return count === 1
    ? { target: [0, 0.9, 0], yaw: 0.6, pitch: 0.35, dist: 6.0, fov: 50 }
    : { target: [0, 0, 0], yaw: 0.6, pitch: 0.30, dist: 14.0, fov: 50 };
}

// ------------------------------------------------------------
//  カメラ(Camera)。OrbitView の目の位置、ビュー・投影行列、視錐台の6面
// ------------------------------------------------------------
const NEAR = 0.1, FAR = 200.0;
// lensShift = [x, y](NDC。画面の端が ±1)は、見る位置は変えずに絵だけをずらす(project にだけ効く。視錐台の6面は変わらない)
function orbit(view, w, h, lensShift = [0, 0]) {
  // OrbitView.Position(float)
  const cp = fcosR(view.pitch), sp = fsinR(view.pitch), sy = fsinR(view.yaw), cy = fcosR(view.yaw);
  const d = f32(view.dist);
  const pos = [f32(view.target[0] + f32(d * f32(sy * cp))), f32(view.target[1] + f32(d * sp)), f32(view.target[2] + f32(d * f32(-cy * cp)))];
  // Matrix4x4.CreateLookAt(position, target, UnitY)
  const z = vnorm(vsub(pos, view.target)), x = vnorm(vcross([0, 1, 0], z)), y = vcross(z, x);
  const dt = (a, b) => f32(f32(f32(a[0] * b[0]) + f32(a[1] * b[1])) + f32(a[2] * b[2]));
  const V = [x[0], y[0], z[0], 0, x[1], y[1], z[1], 0, x[2], y[2], z[2], 0, -dt(x, pos), -dt(y, pos), -dt(z, pos), 1];
  // Matrix4x4.CreatePerspectiveFieldOfView(右手系・深度 0〜1)。y を裏返す(Vulkan)
  const fov = f32(f32(f32(view.fov) * F_PI) / 180);
  const ys = f32(1 / f32(Math.tan(f32(fov * 0.5)))), xs = f32(ys / f32(w / h));
  const P = [xs, 0, 0, 0, 0, -ys, 0, 0, 0, 0, f32(FAR / f32(NEAR - FAR)), -1, 0, 0, f32(f32(NEAR * FAR) / f32(NEAR - FAR)), 0];
  const VP = matMul(V, P);
  // 視錐台の6面(Gribb と Hartmann)。(法線 xyz, 距離 w)で、dot(xyz, p) + w >= 0 が内側。法線は長さ 1
  const col = (c) => [VP[c], VP[4 + c], VP[8 + c], VP[12 + c]];
  const c0 = col(0), c1 = col(1), c2 = col(2), c3 = col(3);
  const pl = (a, b, s) => a.map((v, i) => f32(v + s * b[i]));
  const planes = [pl(c3, c0, 1), pl(c3, c0, -1), pl(c3, c1, 1), pl(c3, c1, -1), c2.slice(), pl(c3, c2, -1)]
    .map((p) => { const l = f32(Math.sqrt(f32(f32(f32(p[0] * p[0]) + f32(p[1] * p[1])) + f32(p[2] * p[2])))); return p.map((v) => f32(v / l)); });
  // 世界の点 → [画面の x, 画面の y, 奥行き(目の座標の -z), NDC の深度(手前 0 〜 奥 1)]。y は下向き(Vulkan)
  const project = (p) => {
    const cx = p[0] * VP[0] + p[1] * VP[4] + p[2] * VP[8] + VP[12];
    const cy = p[0] * VP[1] + p[1] * VP[5] + p[2] * VP[9] + VP[13];
    const cw = p[0] * VP[3] + p[1] * VP[7] + p[2] * VP[11] + VP[15];
    const cz = p[0] * VP[2] + p[1] * VP[6] + p[2] * VP[10] + VP[14];
    return [(cx / cw * 0.5 + 0.5 + lensShift[0] * 0.5) * w, (cy / cw * 0.5 + 0.5 + lensShift[1] * 0.5) * h, cw, cz / cw];
  };
  return { pos, VP, planes, project, w, h };
}

// ------------------------------------------------------------
//  カリングの判定(meshlet.task と同じ式)
// ------------------------------------------------------------
// 球が6枚の面のどれか1枚の「完全に外側」にあれば外。外した面の番号(なければ -1)を返す
function outsideFrustum(planes, c, r) {
  for (let i = 0; i < planes.length; i++) if (planes[i][0] * c[0] + planes[i][1] * c[1] + planes[i][2] * c[2] + planes[i][3] < -r) return i;
  return -1;
}
// メッシュレットの三角形が全部裏を向いているか。dot(軸, v) >= sin α |v| + r (1 + sin α)
function facingAway(c, r, axis, sinA, eye) {
  const v = sub(c, eye);
  return dot(axis, v) >= sinA * len(v) + r * (1 + sinA);
}
const CULL_FRUSTUM = 1, CULL_BACKFACE = 2;
// 1つのメッシュレットを、体の行列 model で運んで判定する。'frustum' | 'backface' | 'visible'
function classify(m, model, cullCam, flags) {
  const c = xformPoint(model, m.sphere), r = m.sphere[3];
  if ((flags & CULL_FRUSTUM) && outsideFrustum(cullCam.planes, c, r) >= 0) return 'frustum';
  if ((flags & CULL_BACKFACE) && facingAway(c, r, xformDir(model, m.cone), m.cone[3], cullCam.pos)) return 'backface';
  return 'visible';
}
// 体の数ぶんまとめて数える。タスクシェーダが数える3つ(画面の外 + 裏向き + 描く)と、描かれる三角形の数
function cullStats(set, instances, cullCam, flags, perInstance = false) {
  const s = { tested: 0, frustum: 0, backface: 0, visible: 0, triangles: 0, per: perInstance ? [] : null };
  for (const model of instances) {
    let vis = 0;
    for (const m of set.meshlets) {
      const k = classify(m, model, cullCam, flags);
      s.tested++; s[k]++;
      if (k === 'visible') { vis++; s.triangles += m.triangleCount; }
    }
    if (perInstance) s.per.push(vis);
  }
  return s;
}

// ------------------------------------------------------------
//  体ごとのカリング(Day 65a。MeshData.ComputeBoundingSphere と、cull.comp / Camera.IsOutside の判定)
// ------------------------------------------------------------
// 形全体の球(頂点を囲む箱の中心から、いちばん遠い頂点まで)。float で計算する
function boundingSphere(mesh) {
  const mn = [Infinity, Infinity, Infinity], mx = [-Infinity, -Infinity, -Infinity];
  for (let i = 0; i < mesh.vertexCount; i++) for (let a = 0; a < 3; a++) { const v = mesh.pos[i * 3 + a]; mn[a] = Math.min(mn[a], v); mx[a] = Math.max(mx[a], v); }
  const c = [0, 1, 2].map((a) => f32(f32(mn[a] + mx[a]) * 0.5));
  let r = 0;
  for (let i = 0; i < mesh.vertexCount; i++) { const d = vsub(c, [mesh.pos[i * 3], mesh.pos[i * 3 + 1], mesh.pos[i * 3 + 2]]); r = Math.max(r, fl(d)); }
  return [c[0], c[1], c[2], r];
}
// 視錐台の外ではない体の番号(番号順)。sphere は形全体の球。radiusScale は球の半径の倍率(reference は 1。このページだけで変える)
function selectBodies(instances, cam, sphere, radiusScale = 1) {
  const ids = [];
  instances.forEach((m, i) => { if (outsideFrustum(cam.planes, xformPoint(m, sphere), sphere[3] * radiusScale) < 0) ids.push(i); });
  return ids;
}

// ------------------------------------------------------------
//  描く順と画素シェーダの回数。深度テストに通った画素の数を、体を order の順に描いて数える
//  (早期の深度テストで画素シェーダの前に捨てられるものは数えない。GPU の 2x2 の組での動きは再現しないので、GPU の値とは 0.03% ほどずれる)
//  opt.map = true で、画素ごとに「深度テストに通った回数」の画像(Uint8Array)も返す
// ------------------------------------------------------------
function countFragments(mesh, set, instances, order, cam, w, h, opt = {}) {
  const zbuf = new Float32Array(w * h).fill(Infinity);
  const map = opt.map ? new Uint8Array(w * h) : null;
  const ord = set.ordered, tc = mesh.triangleCount, eye = cam.pos, vc = mesh.vertexCount;
  const P = new Float32Array(vc * 3), W3 = new Float32Array(vc * 3);
  let frags = 0, tris = 0;
  for (const bi of order) {
    const model = instances[bi];
    for (let i = 0; i < vc; i++) {
      const q = xformPoint(model, [mesh.pos[i * 3], mesh.pos[i * 3 + 1], mesh.pos[i * 3 + 2]]);
      W3[i * 3] = q[0]; W3[i * 3 + 1] = q[1]; W3[i * 3 + 2] = q[2];
      const sc = cam.project(q); P[i * 3] = sc[0]; P[i * 3 + 1] = sc[1]; P[i * 3 + 2] = sc[2];
    }
    for (let k = 0; k < tc; k++) {
      const a = ord[k * 3], b = ord[k * 3 + 1], c = ord[k * 3 + 2];
      const ax = W3[a * 3], ay = W3[a * 3 + 1], az = W3[a * 3 + 2];
      const ux = W3[b * 3] - ax, uy = W3[b * 3 + 1] - ay, uz = W3[b * 3 + 2] - az, vx = W3[c * 3] - ax, vy = W3[c * 3 + 1] - ay, vz = W3[c * 3 + 2] - az;
      const nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
      if (nx * (eye[0] - ax) + ny * (eye[1] - ay) + nz * (eye[2] - az) <= 0) continue;
      const Ax = P[a * 3], Ay = P[a * 3 + 1], Az = P[a * 3 + 2], Bx = P[b * 3], By = P[b * 3 + 1], Bz = P[b * 3 + 2], Cx = P[c * 3], Cy = P[c * 3 + 1], Cz = P[c * 3 + 2];
      if (Az <= 0 || Bz <= 0 || Cz <= 0) continue;
      const x0 = Math.max(0, Math.floor(Math.min(Ax, Bx, Cx))), x1 = Math.min(w - 1, Math.ceil(Math.max(Ax, Bx, Cx)));
      const y0 = Math.max(0, Math.floor(Math.min(Ay, By, Cy))), y1 = Math.min(h - 1, Math.ceil(Math.max(Ay, By, Cy)));
      const den = (By - Cy) * (Ax - Cx) + (Cx - Bx) * (Ay - Cy);
      if (Math.abs(den) < 1e-9) continue;
      tris++;
      const ia = 1 / Az, ib = 1 / Bz, ic = 1 / Cz;
      for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
        const sx = x + 0.5, sy = y + 0.5;
        const l0 = ((By - Cy) * (sx - Cx) + (Cx - Bx) * (sy - Cy)) / den, l1 = ((Cy - Ay) * (sx - Cx) + (Ax - Cx) * (sy - Cy)) / den, l2 = 1 - l0 - l1;
        if (l0 < -1e-4 || l1 < -1e-4 || l2 < -1e-4) continue;
        const z = 1 / (l0 * ia + l1 * ib + l2 * ic), o = y * w + x;
        if (z < zbuf[o]) { zbuf[o] = z; frags++; if (map && map[o] < 255) map[o]++; }
      }
    }
  }
  return { frags, tris, map };
}
// 体の番号の並べ方。'index' = 番号順(CPU で選んだとき)、'near' = 手前から、'far' = 奥から。
// 'warps' = GPU の atomicAdd の席の取り方のまね: 32 体ずつの組の中は番号順で、組どうしの順番が入れ替わる(perm は組の順番)
function orderBodies(kind, ids, instances, cam, perm) {
  const dist = (i) => Math.hypot(instances[i][12] - cam.pos[0], instances[i][14] - cam.pos[2]);
  if (kind === 'near') return ids.slice().sort((a, b) => dist(a) - dist(b));
  if (kind === 'far') return ids.slice().sort((a, b) => dist(b) - dist(a));
  if (kind === 'warps') return perm.flatMap((wp) => ids.filter((i) => ((i / 32) | 0) === wp));
  return ids.slice();
}

// ------------------------------------------------------------
//  深度と Hi-Z(Day 65b。depth_reduce.comp と DepthPyramid の段の大きさ)
// ------------------------------------------------------------
// 選ばれた体を描いたときの深度(手前 0、奥 1。何も描かれない画素は 1)。Vulkan の深度バッファと同じ向き
// mask(体の番号, メッシュレットの番号)が false のメッシュレットは描かない(Day 65c の早いパス)。item = true で、画素ごとの「体 * メッシュレット数 + メッシュレット」の番号の画像も返す
function renderDepth(mesh, set, instances, ids, cam, w, h, mask, item) {
  const depth = new Float32Array(w * h).fill(1);
  const owner = item ? new Int32Array(w * h).fill(-1) : null;
  const ord = set.ordered, tc = mesh.triangleCount, eye = cam.pos, vc = mesh.vertexCount;
  const P = new Float32Array(vc * 4), W3 = new Float32Array(vc * 3);
  for (const bi of ids) {
    const model = instances[bi];
    for (let i = 0; i < vc; i++) {
      const q = xformPoint(model, [mesh.pos[i * 3], mesh.pos[i * 3 + 1], mesh.pos[i * 3 + 2]]);
      W3[i * 3] = q[0]; W3[i * 3 + 1] = q[1]; W3[i * 3 + 2] = q[2];
      const sc = cam.project(q); P[i * 4] = sc[0]; P[i * 4 + 1] = sc[1]; P[i * 4 + 2] = sc[2]; P[i * 4 + 3] = sc[3];
    }
    for (let k = 0; k < tc; k++) {
      if (mask && !mask(bi, set.triMeshlet[k])) continue;
      const a = ord[k * 3], b = ord[k * 3 + 1], c = ord[k * 3 + 2];
      const ax = W3[a * 3], ay = W3[a * 3 + 1], az = W3[a * 3 + 2];
      const ux = W3[b * 3] - ax, uy = W3[b * 3 + 1] - ay, uz = W3[b * 3 + 2] - az, vx = W3[c * 3] - ax, vy = W3[c * 3 + 1] - ay, vz = W3[c * 3 + 2] - az;
      if ((uy * vz - uz * vy) * (eye[0] - ax) + (uz * vx - ux * vz) * (eye[1] - ay) + (ux * vy - uy * vx) * (eye[2] - az) <= 0) continue;
      const Ax = P[a * 4], Ay = P[a * 4 + 1], Bx = P[b * 4], By = P[b * 4 + 1], Cx = P[c * 4], Cy = P[c * 4 + 1];
      if (P[a * 4 + 2] <= 0 || P[b * 4 + 2] <= 0 || P[c * 4 + 2] <= 0) continue;
      const Az = P[a * 4 + 3], Bz = P[b * 4 + 3], Cz = P[c * 4 + 3];
      const x0 = Math.max(0, Math.floor(Math.min(Ax, Bx, Cx))), x1 = Math.min(w - 1, Math.ceil(Math.max(Ax, Bx, Cx)));
      const y0 = Math.max(0, Math.floor(Math.min(Ay, By, Cy))), y1 = Math.min(h - 1, Math.ceil(Math.max(Ay, By, Cy)));
      const den = (By - Cy) * (Ax - Cx) + (Cx - Bx) * (Ay - Cy);
      if (Math.abs(den) < 1e-9) continue;
      for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
        const sx = x + 0.5, sy = y + 0.5;
        const l0 = ((By - Cy) * (sx - Cx) + (Cx - Bx) * (sy - Cy)) / den, l1 = ((Cy - Ay) * (sx - Cx) + (Ax - Cx) * (sy - Cy)) / den, l2 = 1 - l0 - l1;
        if (l0 < -1e-4 || l1 < -1e-4 || l2 < -1e-4) continue;
        const z = l0 * Az + l1 * Bz + l2 * Cz, o = y * w + x;
        if (z < depth[o]) { depth[o] = z; if (owner) owner[o] = bi * set.count + set.triMeshlet[k]; }
      }
    }
  }
  return item ? { depth, owner } : depth;
}
// Hi-Z の段の大きさ。段 0 = 深度の半分(960 x 540 → 480 x 270)、段 L = max(1, 段 0 の大きさ >> L)(切り捨て)。段の数 = floor(log2(max(幅, 高さ))) + 1
function pyramidSizes(depthW, depthH) {
  const w0 = depthW >> 1, h0 = depthH >> 1;
  const levels = Math.floor(Math.log2(Math.max(w0, h0))) + 1;
  return Array.from({ length: levels }, (_, L) => [Math.max(1, w0 >> L), Math.max(1, h0 >> L)]);
}
// depth_reduce.comp と同じ畳み方。mode は 'max'(いちばん奥。reference)| 'mean' | 'min'(課題1。このページだけ)
// 下の段が奇数で、自分が端の出力なら 3 列(3 行)読む。読む位置は端の画素へ丸める
function buildPyramid(depth, depthW, depthH, mode = 'max') {
  const sizes = pyramidSizes(depthW, depthH), levels = [];
  let src = depth, sw = depthW, sh = depthH;
  for (const [ow, oh] of sizes) {
    const out = new Float32Array(ow * oh);
    for (let y = 0; y < oh; y++) for (let x = 0; x < ow; x++) {
      const cxn = (x === ow - 1 && (sw & 1)) ? 3 : 2, cyn = (y === oh - 1 && (sh & 1)) ? 3 : 2;
      let m = mode === 'min' ? Infinity : 0, sum = 0, n = 0;
      for (let j = 0; j < cyn; j++) for (let i = 0; i < cxn; i++) {
        const v = src[Math.min(y * 2 + j, sh - 1) * sw + Math.min(x * 2 + i, sw - 1)];
        sum += v; n++; m = mode === 'min' ? Math.min(m, v) : Math.max(m, v);
      }
      out[y * ow + x] = mode === 'mean' ? sum / n : m;
    }
    levels.push({ w: ow, h: oh, data: out });
    src = out; sw = ow; sh = oh;
  }
  return levels;
}
// common.glsl の occluded(center, radius)。球を囲む立方体の8つの角を画面に投影して箱にし、箱に合う段の Hi-Z のいちばん奥と、角のいちばん手前を比べる。
// eps は比べるときの「甘さ」(reference は 0。このページの検算でだけ変える)。info を渡すと、箱・段・読んだ画素を書き込む
function occluded(center, radius, cam, pyr, w, h, eps = 0, info = null) {
  const lo = [1e30, 1e30], hi = [-1e30, -1e30];
  let nearest = 1;
  const VP = cam.VP;
  for (let i = 0; i < 8; i++) {
    const px = center[0] + radius * ((i & 1) ? 1 : -1), py = center[1] + radius * ((i & 2) ? 1 : -1), pz = center[2] + radius * ((i & 4) ? 1 : -1);
    const cx = px * VP[0] + py * VP[4] + pz * VP[8] + VP[12], cy = px * VP[1] + py * VP[5] + pz * VP[9] + VP[13];
    const cz = px * VP[2] + py * VP[6] + pz * VP[10] + VP[14], cw = px * VP[3] + py * VP[7] + pz * VP[11] + VP[15];
    if (cw <= 0 || cz < 0) return false; // 角が目の後ろ(か手前の切り口より手前)にかかる。判定しない
    const nx = cx / cw, ny = cy / cw, nz = cz / cw;
    lo[0] = Math.min(lo[0], nx); lo[1] = Math.min(lo[1], ny); hi[0] = Math.max(hi[0], nx); hi[1] = Math.max(hi[1], ny);
    nearest = Math.min(nearest, nz);
  }
  const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
  const pmin = [clamp((lo[0] * 0.5 + 0.5) * w, 0, w - 1), clamp((lo[1] * 0.5 + 0.5) * h, 0, h - 1)];
  const pmax = [clamp((hi[0] * 0.5 + 0.5) * w, 0, w - 1), clamp((hi[1] * 0.5 + 0.5) * h, 0, h - 1)];
  const extent = Math.max(pmax[0] - pmin[0], pmax[1] - pmin[1]);
  const level = clamp(Math.ceil(Math.log2(Math.max(extent, 1))) - 1, 0, pyr.length - 1);
  const texel = 2 ** (level + 1), lv = pyr[level];
  const t0 = [Math.min(Math.floor(pmin[0] / texel), lv.w - 1), Math.min(Math.floor(pmin[1] / texel), lv.h - 1)];
  const t1 = [Math.min(Math.floor(pmax[0] / texel), lv.w - 1), Math.min(Math.floor(pmax[1] / texel), lv.h - 1)];
  let farthest = 0;
  for (let y = t0[1]; y <= t1[1]; y++) for (let x = t0[0]; x <= t1[0]; x++) farthest = Math.max(farthest, lv.data[y * lv.w + x]);
  if (info) Object.assign(info, { pmin, pmax, extent, level, texel, t0, t1, nearest, farthest });
  return nearest > farthest - eps;
}

// ------------------------------------------------------------
//  2パスのオクルージョンカリング(Day 65c)。止まった場面で、見えた印を落ち着かせながらフレームを重ねる。
//  印は reference と同じ 2 本(体ごと・メッシュレットごと)。早いパスは「印が立っていて、画面の外でも裏向きでもない」ものを描き、
//  その深度から Hi-Z を作り、遅いパスが全部を Hi-Z と比べ直して印を書き直す。タスクの道(cullFlags = 視錐台 + 背面 + 見える体を全部送る)
// ------------------------------------------------------------
function createTwoPass(mesh, set, instances, sphere) {
  return { mesh, set, instances, sphere, instMark: new Uint8Array(instances.length), mlMark: new Uint8Array(instances.length * set.count), frames: 0 };
}
// 印を捨てる(起動直後は全部 0)。視点が飛んだ直後を作るときは、捨てずに視点だけ変える
function resetMarks(st) { st.instMark.fill(0); st.mlMark.fill(0); st.frames = 0; }
// 1フレーム。cam は描く目 = カリングの目。戻り値は早いパス・遅いパスの数と、描かれた三角形の数
function twoPassFrame(st, cam, w, h, opt = {}) {
  const { mesh, set, instances, sphere } = st, n = set.count, eps = opt.eps || 0;
  const bodyOutside = instances.map((m) => outsideFrustum(cam.planes, xformPoint(m, sphere), sphere[3]) >= 0);
  // メッシュレットの視錐台 + 背面のカリングは、体の行列で運んで判定する。結果は 'frustum' | 'backface' | 'ok'
  const culledKind = (bi, mi) => { const k = classify(set.meshlets[mi], instances[bi], cam, 3); return k === 'visible' ? 'ok' : k; };
  // ---- 早いパス ----
  const earlyBodies = [];
  instances.forEach((_, bi) => { if (st.instMark[bi] && !bodyOutside[bi]) earlyBodies.push(bi); });
  const earlyOk = new Map(); // 体 → 描いたメッシュレットの Uint8Array
  let earlyMeshlets = 0, earlyTris = 0;
  for (const bi of earlyBodies) {
    const ok = new Uint8Array(n);
    for (let mi = 0; mi < n; mi++) if (st.mlMark[bi * n + mi] && culledKind(bi, mi) === 'ok') { ok[mi] = 1; earlyMeshlets++; earlyTris += set.meshlets[mi].triangleCount; }
    earlyOk.set(bi, ok);
  }
  const depth = renderDepth(mesh, set, instances, earlyBodies, cam, w, h, (bi, mi) => earlyOk.get(bi)[mi] === 1);
  const pyr = buildPyramid(depth, w, h, 'max');
  // ---- 遅いパス ----
  const was = Uint8Array.from(st.instMark);
  let frustumInst = 0, occludedInst = 0, lateBodies = 0;
  const tally = { tested: 0, frustum: 0, backface: 0, occluded: 0, visible: 0 };
  let lateMeshlets = 0, lateTris = 0, visibleTris = 0;
  const visibleMask = new Uint8Array(instances.length * n), occludedMask = new Uint8Array(instances.length * n), addedMask = new Uint8Array(instances.length * n);
  instances.forEach((m, bi) => {
    let visible = false;
    if (bodyOutside[bi]) frustumInst++;
    else if (occluded(xformPoint(m, sphere), sphere[3], cam, pyr, w, h, eps)) occludedInst++;
    else visible = true;
    st.instMark[bi] = visible ? 1 : 0;
    if (!visible) return;
    lateBodies++;
    for (let mi = 0; mi < n; mi++) {
      const ml = set.meshlets[mi];
      tally.tested++;
      const kind = culledKind(bi, mi);
      let vis = false;
      if (kind === 'frustum') tally.frustum++;
      else if (kind === 'backface') tally.backface++;
      else if (occluded(xformPoint(instances[bi], ml.sphere), ml.sphere[3], cam, pyr, w, h, eps)) { tally.occluded++; occludedMask[bi * n + mi] = 1; }
      else { vis = true; tally.visible++; }
      const drewEarly = was[bi] && st.mlMark[bi * n + mi];
      if (vis && !drewEarly) { lateMeshlets++; lateTris += ml.triangleCount; addedMask[bi * n + mi] = 1; }
      if (vis) { visibleTris += ml.triangleCount; visibleMask[bi * n + mi] = 1; }
      st.mlMark[bi * n + mi] = vis ? 1 : 0;
    }
  });
  st.frames++;
  return { earlyBodies: earlyBodies.length, lateBodies, frustumInst, occludedInst, earlyMeshlets, earlyTris, lateMeshlets, lateTris, visibleTris, tally, depth, pyr, visibleMask, occludedMask, addedMask };
}

// 値が 1.0(受け持つ範囲のどこかに「何も無い」画素がある)の画素の割合
const fractionOfOnes = (level) => { let n = 0; for (const v of level.data) if (v >= 1) n++; return n / level.data.length; };

// ------------------------------------------------------------
//  ソフトウェア描画。三角形ごとに色を決めて、z バッファ付きで塗る(表側の三角形だけ)
//  opt.color(k, meshletIndex, 面の法線) → [r, g, b](0〜255)。null を返すとその三角形を描かない
//  opt.model: 体の行列(省略すると単位行列)
// ------------------------------------------------------------
function render(mesh, set, cam, w, h, opt) {
  const model = opt.model || [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
  const vc = mesh.vertexCount;
  const W3 = new Float32Array(vc * 3), P = new Float32Array(vc * 3);
  for (let i = 0; i < vc; i++) {
    const q = xformPoint(model, [mesh.pos[i * 3], mesh.pos[i * 3 + 1], mesh.pos[i * 3 + 2]]);
    W3[i * 3] = q[0]; W3[i * 3 + 1] = q[1]; W3[i * 3 + 2] = q[2];
    const s = cam.project(q);
    P[i * 3] = s[0]; P[i * 3 + 1] = s[1]; P[i * 3 + 2] = s[2];
  }
  const img = new ImageData(w, h);
  const px = new Uint32Array(img.data.buffer);
  px.fill(opt.background || 0xff1c1512); // #12151c(ABGR)
  const zbuf = new Float32Array(w * h).fill(Infinity);
  const eye = cam.pos, ord = set.ordered, tc = mesh.triangleCount;
  for (let k = 0; k < tc; k++) {
    const a = ord[k * 3], b = ord[k * 3 + 1], c = ord[k * 3 + 2];
    const A = [W3[a * 3], W3[a * 3 + 1], W3[a * 3 + 2]], B = [W3[b * 3], W3[b * 3 + 1], W3[b * 3 + 2]], C = [W3[c * 3], W3[c * 3 + 1], W3[c * 3 + 2]];
    const n = cross(sub(B, A), sub(C, A));
    if (n[0] * (eye[0] - A[0]) + n[1] * (eye[1] - A[1]) + n[2] * (eye[2] - A[2]) <= 0) continue;
    const rgb = opt.color(k, set.triMeshlet[k], norm(n));
    if (!rgb) continue;
    const col = (255 << 24) | (rgb[2] << 16) | (rgb[1] << 8) | rgb[0];
    const ax = P[a * 3], ay = P[a * 3 + 1], az = P[a * 3 + 2], bx = P[b * 3], by = P[b * 3 + 1], bz = P[b * 3 + 2], cx = P[c * 3], cy = P[c * 3 + 1], cz = P[c * 3 + 2];
    if (az <= 0 || bz <= 0 || cz <= 0) continue;
    const x0 = Math.max(0, Math.floor(Math.min(ax, bx, cx))), x1 = Math.min(w - 1, Math.ceil(Math.max(ax, bx, cx)));
    const y0 = Math.max(0, Math.floor(Math.min(ay, by, cy))), y1 = Math.min(h - 1, Math.ceil(Math.max(ay, by, cy)));
    const den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
    if (Math.abs(den) < 1e-9) continue;
    const ia = 1 / az, ib = 1 / bz, ic = 1 / cz;
    for (let y = y0; y <= y1; y++) {
      for (let x = x0; x <= x1; x++) {
        const sx = x + 0.5, sy = y + 0.5;
        const l0 = ((by - cy) * (sx - cx) + (cx - bx) * (sy - cy)) / den;
        const l1 = ((cy - ay) * (sx - cx) + (ax - cx) * (sy - cy)) / den;
        const l2 = 1 - l0 - l1;
        if (l0 < -1e-4 || l1 < -1e-4 || l2 < -1e-4) continue;
        const z = 1 / (l0 * ia + l1 * ib + l2 * ic);
        const o = y * w + x;
        if (z < zbuf[o]) { zbuf[o] = z; px[o] = col; }
      }
    }
  }
  return img;
}

const api = {
  TUBULAR, RADIAL, createTorusKnot, build, hash, spread, coneHistogram,
  PRESETS, sceneInstances, defaultView, orbit, matMul,  xformPoint, xformDir,
  outsideFrustum, facingAway, classify, cullStats, CULL_FRUSTUM, CULL_BACKFACE, render,
  boundingSphere, selectBodies, countFragments, orderBodies, renderDepth, pyramidSizes, buildPyramid, fractionOfOnes,
  occluded, createTwoPass, resetMarks, twoPassFrame,
};
if (typeof window !== 'undefined') window.Meshlet = api;
if (typeof module !== 'undefined') module.exports = api; // Node での検算用
})();
