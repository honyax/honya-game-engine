// ============================================================
//  2D の衝突判定(reference/Day25〜 の Physics/Shapes2D.cs・Collision2D.cs、Day 26 の SpatialGrid.cs と、
//  Program.cs の衝突デモ(Body・InitializeBodies・UpdateBodies・Test)を JS に移したもの)
//  Day 25〜27 の計画書の実験台が共有する。window.Col2D にまとめて置く。
//
//  - C# は float で計算しているので、1回の演算ごとに Math.fround で丸めて同じ値を出す
//    (同じ種から作った体を何百ステップ回しても、接触の数が C# と一致する)
//  - 乱数は .NET の System.Random(種あり)と同じ並びを出す
//  - Contact2D の Normal は「a から b へ向かう向き」。a を -Normal へ、b を +Normal へ動かすと離れる
// ============================================================
(() => {
'use strict';
const f = Math.fround;
const PI = f(Math.PI), TAU = f(2 * Math.PI);
const cos = (x) => f(Math.cos(x)), sin = (x) => f(Math.sin(x));
const v = (x, y) => [f(x), f(y)];
const dot = (a, b) => f(f(a[0] * b[0]) + f(a[1] * b[1]));
const copySign = (m, s) => (s < 0 || Object.is(s, -0) ? -Math.abs(m) : Math.abs(m));

// ---------------- 形(Shapes2D.cs) ----------------
const aabb = (min, max) => ({ kind: 'aabb', min, max });
const fromCenter = (c, h) => aabb(v(c[0] - h[0], c[1] - h[1]), v(c[0] + h[0], c[1] + h[1]));
const center = (b) => v(f(b.min[0] + b.max[0]) * 0.5, f(b.min[1] + b.max[1]) * 0.5);
const halfSize = (b) => v(f(b.max[0] - b.min[0]) * 0.5, f(b.max[1] - b.min[1]) * 0.5);
const circle = (c, r) => ({ kind: 'circle', center: c, radius: f(r) });
// 三角関数はコンストラクタで1回だけ(C# と同じ)
function obb(c, h, rot) {
  rot = f(rot);
  const cs = cos(rot), sn = sin(rot);
  return { kind: 'obb', center: c, half: h, rotation: rot, axisX: [cs, sn], axisY: [f(-sn), cs] };
}
function obbBounds(b) {
  const ac = Math.abs(b.axisX[0]), as = Math.abs(b.axisX[1]);
  return fromCenter(b.center, v(f(ac * b.half[0]) + f(as * b.half[1]), f(as * b.half[0]) + f(ac * b.half[1])));
}
function toLocal(b, w) {
  const d = v(w[0] - b.center[0], w[1] - b.center[1]);
  return [dot(d, b.axisX), dot(d, b.axisY)];
}
function corners(b) {
  const x = v(b.axisX[0] * b.half[0], b.axisX[1] * b.half[0]), y = v(b.axisY[0] * b.half[1], b.axisY[1] * b.half[1]);
  const c = b.center;
  return [v(f(c[0] - x[0]) - y[0], f(c[1] - x[1]) - y[1]), v(f(c[0] + x[0]) - y[0], f(c[1] + x[1]) - y[1]), v(f(c[0] + x[0]) + y[0], f(c[1] + x[1]) + y[1]), v(f(c[0] - x[0]) + y[0], f(c[1] - x[1]) + y[1])];
}

// ---------------- 判定(Collision2D.cs) ----------------
const NONE = { hit: false, normal: [0, 0], depth: 0 };
const touching = (n, d) => ({ hit: true, normal: n, depth: f(d) });

function overlapAabb(a, b) { return a.min[0] <= b.max[0] && a.max[0] >= b.min[0] && a.min[1] <= b.max[1] && a.max[1] >= b.min[1]; }
function overlapCircle(a, b) {
  const r = f(a.radius + b.radius), d = v(b.center[0] - a.center[0], b.center[1] - a.center[1]);
  return dot(d, d) <= f(r * r);
}
function testAabb(a, b) {
  const ca = center(a), cb = center(b), ha = halfSize(a), hb = halfSize(b);
  const d = v(cb[0] - ca[0], cb[1] - ca[1]);
  const o = v(f(ha[0] + hb[0]) - Math.abs(d[0]), f(ha[1] + hb[1]) - Math.abs(d[1]));
  if (o[0] <= 0 || o[1] <= 0) return NONE;
  if (o[0] < o[1]) return touching([copySign(1, d[0]), 0], o[0]);
  return touching([0, copySign(1, d[1])], o[1]);
}
function testCircle(a, b) {
  const d = v(b.center[0] - a.center[0], b.center[1] - a.center[1]);
  const r = f(a.radius + b.radius), d2 = dot(d, d);
  if (d2 > f(r * r)) return NONE;
  const dist = f(Math.sqrt(d2));
  if (dist < f(1e-6)) return touching([1, 0], r);
  return touching([f(d[0] / dist), f(d[1] / dist)], f(r - dist));
}
const closestPoint = (box, p) => [Math.min(Math.max(p[0], box.min[0]), box.max[0]), Math.min(Math.max(p[1], box.min[1]), box.max[1])];
function testCircleAabb(c, box) {
  const cl = closestPoint(box, c.center);
  const d = v(c.center[0] - cl[0], c.center[1] - cl[1]), d2 = dot(d, d);
  if (d2 > f(c.radius * c.radius)) return NONE;
  if (d2 < f(1e-12)) {
    const bc = center(box), bh = halfSize(box);
    const t = v(c.center[0] - bc[0], c.center[1] - bc[1]);
    const o = v(f(bh[0] + c.radius) - Math.abs(t[0]), f(bh[1] + c.radius) - Math.abs(t[1]));
    return o[0] < o[1] ? touching([-copySign(1, t[0]), 0], o[0]) : touching([0, -copySign(1, t[1])], o[1]);
  }
  const dist = f(Math.sqrt(d2));
  return touching([f(-d[0] / dist), f(-d[1] / dist)], f(c.radius - dist));
}
function testCircleObb(c, box) {
  const lc = toLocal(box, c.center);
  const local = testCircleAabb(circle(lc, c.radius), aabb(v(-box.half[0], -box.half[1]), box.half));
  if (!local.hit) return NONE;
  const n = local.normal;
  return touching(v(f(box.axisX[0] * n[0]) + f(box.axisY[0] * n[1]), f(box.axisX[1] * n[0]) + f(box.axisY[1] * n[1])), local.depth);
}
const projectedRadius = (b, axis) => f(f(Math.abs(dot(axis, b.axisX)) * b.half[0]) + f(Math.abs(dot(axis, b.axisY)) * b.half[1]));
// 1本の軸の結果を全部返す(実験台で軸ごとの投影を描くため)。判定そのものは testObb と同じ順・同じ式
function satAxes(a, b) {
  const d = v(b.center[0] - a.center[0], b.center[1] - a.center[1]);
  return [['A の X 軸', a.axisX], ['A の Y 軸', a.axisY], ['B の X 軸', b.axisX], ['B の Y 軸', b.axisY]].map(([name, axis]) => {
    const ra = projectedRadius(a, axis), rb = projectedRadius(b, axis), sep = dot(d, axis);
    return { name, axis, ra, rb, sep, overlap: f(f(ra + rb) - Math.abs(sep)) };
  });
}
function testObb(a, b) {
  let bestN = [1, 0], bestD = 3.4028234663852886e38;
  for (const ax of satAxes(a, b)) {
    if (ax.overlap <= 0) return NONE;   // 分離できた。その時点で打ち切り
    if (ax.overlap < bestD) { bestD = ax.overlap; bestN = ax.sep < 0 ? [f(-ax.axis[0]), f(-ax.axis[1])] : ax.axis; }
  }
  return touching(bestN, bestD);
}
// C# のオーバーロード Collision2D.Test(a, b) をまとめたもの(引数の型で振り分ける)
function test(a, b) {
  const k = a.kind + '-' + b.kind;
  switch (k) {
    case 'circle-circle': return testCircle(a, b);
    case 'aabb-aabb': return testAabb(a, b);
    case 'circle-aabb': return testCircleAabb(a, b);
    case 'circle-obb': return testCircleObb(a, b);
    case 'obb-obb': return testObb(a, b);
    default: throw new Error('reference に無い組: ' + k);
  }
}
const flip = (c) => (c.hit ? touching([f(-c.normal[0]), f(-c.normal[1])], c.depth) : NONE);

// ---------------- .NET の System.Random(種あり。Net5CompatSeedImpl と同じ並び) ----------------
class DotNetRandom {
  constructor(seed) {
    const MBIG = 2147483647, MSEED = 161803398;
    const a = new Array(56).fill(0);
    let mj = MSEED - (seed === -2147483648 ? MBIG : Math.abs(seed)), mk = 1;
    a[55] = mj;
    for (let i = 1; i < 55; i++) {
      const ii = (21 * i) % 55;
      a[ii] = mk; mk = mj - mk; if (mk < 0) mk += MBIG; mj = a[ii];
    }
    for (let k = 1; k < 5; k++) for (let i = 1; i < 56; i++) { a[i] -= a[1 + (i + 30) % 55]; if (a[i] < 0) a[i] += MBIG; }
    this.a = a; this.inext = 0; this.inextp = 21; this.MBIG = MBIG;
  }
  nextDouble() {
    let i = this.inext + 1; if (i >= 56) i = 1;
    let p = this.inextp + 1; if (p >= 56) p = 1;
    let r = this.a[i] - this.a[p];
    if (r === this.MBIG) r--;
    if (r < 0) r += this.MBIG;
    this.a[i] = r; this.inext = i; this.inextp = p;
    return r * (1.0 / this.MBIG);
  }
}

// ---------------- 均一グリッド(Day 26 の Physics/SpatialGrid.cs) ----------------
class SpatialGrid {
  constructor() {
    this.origin = [0, 0]; this.cellSize = f(32); this.inv = f(1 / 32); this.columns = 1; this.rows = 1;
    this.cellStart = new Int32Array(0); this.cursor = new Int32Array(0); this.entries = new Int32Array(0);
    this.mark = new Int32Array(0); this.stamp = 0; this.pairsA = new Int32Array(0); this.pairsB = new Int32Array(0); this.pairCount = 0;
    this.entryCount = 0; this.occupiedCells = 0; this.maxPerCell = 0; this.coLocatedPairs = 0;
  }
  get cellCount() { return this.columns * this.rows; }
  configure(origin, size, cellSize) {
    this.origin = origin;
    this.cellSize = Math.max(f(cellSize), 1);
    this.inv = f(1 / this.cellSize);
    this.columns = Math.min(Math.max(Math.ceil(f(size[0] * this.inv)), 1), 2048);
    this.rows = Math.min(Math.max(Math.ceil(f(size[1] * this.inv)), 1), 2048);
  }
  // 平均の直径(外接 AABB の幅と高さの平均)。合計は double で取る(C# と同じ)
  static suggestCellSize(bounds, n = bounds.length) {
    if (n === 0) return f(32);
    let total = 0;
    for (let i = 0; i < n; i++) { const b = bounds[i]; total += f(f(b.max[0] - b.min[0]) + f(b.max[1] - b.min[1])) * 0.5; }
    return Math.max(f(total / n), 1);
  }
  // 体の AABB が触れるマスの範囲。世界の外は端のマスへ丸める
  cellRange(b) {
    const inv = this.inv, o = this.origin;
    const clampI = (v, hi) => Math.min(Math.max(Math.trunc(v), 0), hi);
    return [clampI(f(f(b.min[0] - o[0]) * inv), this.columns - 1), clampI(f(f(b.min[1] - o[1]) * inv), this.rows - 1),
      clampI(f(f(b.max[0] - o[0]) * inv), this.columns - 1), clampI(f(f(b.max[1] - o[1]) * inv), this.rows - 1)];
  }
  // 数える → 接頭辞和 → 詰める
  build(bounds, n = bounds.length) {
    const cells = this.cellCount;
    if (this.cellStart.length < cells + 1) { this.cellStart = new Int32Array(cells + 1); this.cursor = new Int32Array(cells + 1); } else this.cellStart.fill(0, 0, cells + 1);
    const cs = this.cellStart, cols = this.columns;
    for (let i = 0; i < n; i++) {
      const [x0, y0, x1, y1] = this.cellRange(bounds[i]);
      for (let cy = y0; cy <= y1; cy++) { const rb = cy * cols; for (let cx = x0; cx <= x1; cx++) cs[rb + cx + 1]++; }
    }
    for (let c = 1; c <= cells; c++) cs[c] += cs[c - 1];
    this.entryCount = cs[cells];
    if (this.entries.length < this.entryCount) this.entries = new Int32Array(Math.max(this.entryCount * 2, 64));
    this.cursor.set(cs.subarray(0, cells));
    const cur = this.cursor, ent = this.entries;
    for (let i = 0; i < n; i++) {
      const [x0, y0, x1, y1] = this.cellRange(bounds[i]);
      for (let cy = y0; cy <= y1; cy++) { const rb = cy * cols; for (let cx = x0; cx <= x1; cx++) ent[cur[rb + cx]++] = i; }
    }
    let occ = 0, max = 0;
    for (let c = 0; c < cells; c++) { const k = cs[c + 1] - cs[c]; if (k > 0) { occ++; if (k > max) max = k; } }
    this.occupiedCells = occ; this.maxPerCell = max;
  }
  // 候補の組を集める。j > i と印(通し番号)で重複を消し、最後に AABB で足切り
  collectPairs(bounds, n = bounds.length) {
    this.pairCount = 0; this.coLocatedPairs = 0;
    if (this.mark.length < n) this.mark = new Int32Array(Math.max(n * 2, 64));
    if (this.pairsA.length < 64) { this.pairsA = new Int32Array(1024); this.pairsB = new Int32Array(1024); }
    if (this.stamp > 2147483647 - n - 1) { this.mark.fill(0); this.stamp = 0; }
    const cs = this.cellStart, ent = this.entries, mark = this.mark, cols = this.columns;
    for (let i = 0; i < n; i++) {
      const stamp = ++this.stamp, box = bounds[i];
      const [x0, y0, x1, y1] = this.cellRange(box);
      for (let cy = y0; cy <= y1; cy++) {
        const rb = cy * cols;
        for (let cx = x0; cx <= x1; cx++) {
          const cell = rb + cx, end = cs[cell + 1];
          for (let e = cs[cell]; e < end; e++) {
            const j = ent[e];
            if (j <= i) continue;
            if (mark[j] === stamp) continue;
            mark[j] = stamp;
            this.coLocatedPairs++;
            if (!overlapAabb(box, bounds[j])) continue;
            if (this.pairCount === this.pairsA.length) {
              const a = new Int32Array(this.pairsA.length * 2), b = new Int32Array(this.pairsB.length * 2);
              a.set(this.pairsA); b.set(this.pairsB); this.pairsA = a; this.pairsB = b;
            }
            this.pairsA[this.pairCount] = i; this.pairsB[this.pairCount] = j; this.pairCount++;
          }
        }
      }
    }
    return this.pairCount;
  }
  cellContents(column, row) { const c = row * this.columns + column; return this.cellStart[c + 1] - this.cellStart[c]; }
}

// ---------------- 衝突デモ(Program.cs) ----------------
const CIRCLE = 0, BOX = 1, ROTATED = 2;
const pickShape = (mix, i) => (mix === 1 ? CIRCLE : mix === 2 ? BOX : mix === 3 ? ROTATED : i % 3);
// sizeScale は Day 26 から(2,000 体を超えたら面積の合計が変わらないよう、一辺を √(2000/n) で縮める)
const sizeScaleFor = (count, fixedSize) => (fixedSize || count <= 2000 ? f(1) : f(Math.sqrt(f(2000 / count))));
function initializeBodies(count, mix, width, height, sizeScale = f(1)) {
  const r = new DotNetRandom(20260825), out = [];
  const rnd = () => f(r.nextDouble());
  width = f(width); height = f(height);
  for (let i = 0; i < count; i++) {
    const size = f(f(9 + f(rnd() * 12)) * sizeScale), speed = f(40 + f(rnd() * 90)), dir = f(rnd() * TAU);
    const px = f(size + f(rnd() * f(width - f(size * 2)))), py = f(size + f(rnd() * f(height - f(size * 2))));
    const vel = [f(cos(dir) * speed), f(sin(dir) * speed)];
    const hy = f(size * f(f(0.6) + f(rnd() * f(0.6))));
    const rot = f(rnd() * TAU), spin = f(f(rnd() - 0.5) * f(1.6));
    out.push({ pos: [px, py], vel, half: [size, hy], rot, spin, shape: pickShape(mix, i), contacts: 0 });
  }
  return out;
}
const toCircle = (b) => circle(b.pos, b.half[0]);
const toAabb = (b) => fromCenter(b.pos, b.half);
const toObb = (b) => obb(b.pos, b.half, b.shape === ROTATED ? b.rot : 0);
function boundsExtent(b) { return b.shape === CIRCLE ? [b.half[0], b.half[0]] : b.shape === BOX ? b.half : halfSize(obbBounds(toObb(b))); }
// 形の組み合わせ表(3種類で6通り)
function testBodies(a, b) {
  const s = a.shape * 3 + b.shape;
  switch (s) {
    case CIRCLE * 3 + CIRCLE: return testCircle(toCircle(a), toCircle(b));
    case BOX * 3 + BOX: return testAabb(toAabb(a), toAabb(b));
    case CIRCLE * 3 + BOX: return testCircleAabb(toCircle(a), toAabb(b));
    case BOX * 3 + CIRCLE: return flip(testCircleAabb(toCircle(b), toAabb(a)));
    case CIRCLE * 3 + ROTATED: return testCircleObb(toCircle(a), toObb(b));
    case ROTATED * 3 + CIRCLE: return flip(testCircleObb(toCircle(b), toObb(a)));
    default: return testObb(toObb(a), toObb(b));
  }
}
// 1ステップ: 動かす → 組を絞る → 判定して半分ずつ押し戻す。{ pairs, contacts, broadMs } を返す。
// grid を渡すと Day 26 の均一グリッド(cellSize が 0 なら平均の直径)、渡さなければ Day 25 の総当たり。
// onBounce を渡すと、壁で跳ねた体をその場で渡す(Day 27 の bounced → PlayBounce。動かすループの中で呼ぶ順も同じ)
function updateBodies(bodies, count, dt, bounds, resolve, grid = null, cellSize = 0, onBounce = null) {
  dt = f(dt);
  for (let i = 0; i < count; i++) {
    const b = bodies[i];
    b.contacts = 0;
    b.pos = [f(b.pos[0] + f(b.vel[0] * dt)), f(b.pos[1] + f(b.vel[1] * dt))];
    b.rot = f(b.rot + f(b.spin * dt));
    const e = boundsExtent(b);
    let bounced = false;
    if (b.pos[0] < e[0]) { b.pos[0] = e[0]; b.vel[0] = Math.abs(b.vel[0]); bounced = true; } else if (b.pos[0] > f(bounds[0] - e[0])) { b.pos[0] = f(bounds[0] - e[0]); b.vel[0] = -Math.abs(b.vel[0]); bounced = true; }
    if (b.pos[1] < e[1]) { b.pos[1] = e[1]; b.vel[1] = Math.abs(b.vel[1]); bounced = true; } else if (b.pos[1] > f(bounds[1] - e[1])) { b.pos[1] = f(bounds[1] - e[1]); b.vel[1] = -Math.abs(b.vel[1]); bounced = true; }
    if (bounced && onBounce) onBounce(b, i);
  }
  // 1組ぶんの判定と押し戻し(Day 26 の Resolve。総当たりとグリッドで同じ関数を通る)
  const resolvePair = (i, j) => {
    const c = testBodies(bodies[i], bodies[j]);
    if (!c.hit) return false;
    bodies[i].contacts++; bodies[j].contacts++;
    if (resolve) {
      const h = f(c.depth * 0.5), push = [f(c.normal[0] * h), f(c.normal[1] * h)];
      const a = bodies[i], b = bodies[j];
      a.pos = [f(a.pos[0] - push[0]), f(a.pos[1] - push[1])];
      b.pos = [f(b.pos[0] + push[0]), f(b.pos[1] + push[1])];
    }
    return true;
  };
  let pairs = 0, contacts = 0, broadMs = 0;
  if (grid) {
    const t0 = typeof performance !== 'undefined' ? performance.now() : 0;
    const boxes = new Array(count);
    for (let i = 0; i < count; i++) boxes[i] = fromCenter(bodies[i].pos, boundsExtent(bodies[i]));
    grid.configure([0, 0], bounds, cellSize > 0 ? cellSize : SpatialGrid.suggestCellSize(boxes));
    grid.build(boxes);
    pairs = grid.collectPairs(boxes);
    if (typeof performance !== 'undefined') broadMs = performance.now() - t0;
    for (let p = 0; p < grid.pairCount; p++) if (resolvePair(grid.pairsA[p], grid.pairsB[p])) contacts++;
  } else {
    for (let i = 0; i < count; i++) for (let j = i + 1; j < count; j++) { pairs++; if (resolvePair(i, j)) contacts++; }
  }
  return { pairs, contacts, broadMs };
}

// ---------------- 書式(C# の float.ToString と Vector2.ToString) ----------------
// 最短で float に戻る桁数。指数が -5 未満か 15 以上なら E 表記(4.371139E-08)
function fmtFloat(x) {
  if (Number.isNaN(x)) return 'NaN';
  if (Object.is(x, -0)) return '-0';
  if (x === 0) return '0';
  let s = '';
  for (let p = 1; p <= 9; p++) { s = roundEven(x, p); if (f(Number(s)) === x) break; }
  const n = Number(s), e = Math.floor(Math.log10(Math.abs(n)));
  if (e < -5 || e >= 15) {
    const m = String(Number((n / Math.pow(10, e)).toPrecision(9)));
    return `${m}E${e < 0 ? '-' : '+'}${String(Math.abs(e)).padStart(2, '0')}`;
  }
  return String(n);
}
// toPrecision はちょうど真ん中を大きい側へ丸めるが、.NET は偶数の側へ丸める(472.703125 → 472.70312)
function roundEven(x, p) {
  const s = x.toPrecision(p);
  const m = Math.abs(x).toExponential(60).match(/^(\d)\.(\d+)e([+-]\d+)$/);
  const digits = m[1] + m[2], rest = digits.slice(p);
  if (!/^50*$/.test(rest) || Number(digits[p - 1]) % 2 !== 0) return s;
  // 真ん中で、切り捨てた側の末尾が偶数 → 切り捨てる
  const e = Number(m[3]), mant = digits.slice(0, p);
  return String((x < 0 ? -1 : 1) * Number(`${mant[0]}.${mant.slice(1)}e${e}`));
}
const fmtVec = (a) => `<${fmtFloat(a[0])}, ${fmtFloat(a[1])}>`;

// ---------------- F9 の自己チェック(Program.RunCollisionCheck) ----------------
function selfCheck() {
  const lines = [];
  let fails = 0;
  const check = (name, ok, detail = '') => { lines.push(`  [${ok ? 'OK' : 'NG'}] ${name}${detail ? '  ' + detail : ''}`); if (!ok) fails++; };
  const near = (a, b) => Math.abs(a - b) < 0.001;
  const c1 = circle(v(0, 0), 10), c2 = circle(v(15, 0), 10);
  const hit = testCircle(c1, c2);
  check('円同士: 当たる', hit.hit);
  check('円同士: 深さ 5', near(hit.depth, 5), fmtFloat(hit.depth));
  check('円同士: 法線は +X', near(hit.normal[0], 1) && near(hit.normal[1], 0), fmtVec(hit.normal));
  check('円同士: 離れていれば当たらない', !testCircle(c1, circle(v(21, 0), 10)).hit);
  check('円同士: ちょうど接するのは当たり扱い', testCircle(c1, circle(v(20, 0), 10)).hit);
  const same = testCircle(c1, circle(v(0, 0), 10));
  check('円同士: 中心が重なっても NaN にならない', same.hit && !Number.isNaN(same.normal[0]) && !Number.isNaN(same.depth), `${fmtVec(same.normal)} ${fmtFloat(same.depth)}`);
  const b1 = fromCenter(v(0, 0), v(10, 10)), b2 = fromCenter(v(16, 4), v(10, 10));
  const boxHit = testAabb(b1, b2);
  check('AABB: 浅い軸で押す(X)', near(boxHit.depth, 4) && near(boxHit.normal[0], 1), `深さ ${fmtFloat(boxHit.depth)} 法線 ${fmtVec(boxHit.normal)}`);
  check('AABB: 角がかすっていなければ当たらない', !testAabb(b1, fromCenter(v(21, 21), v(10, 10))).hit);
  const mixed = testCircleAabb(circle(v(14, 0), 6), b1);
  check('円とAABB: 辺に当たる', mixed.hit && near(mixed.depth, 2), `深さ ${fmtFloat(mixed.depth)}`);
  check('円とAABB: 法線は円から箱へ(-X)', near(mixed.normal[0], -1), fmtVec(mixed.normal));
  check('円とAABB: 届かなければ当たらない', !testCircleAabb(circle(v(24, 0), 6), b1).hit);
  const inside = testCircleAabb(circle(v(2, 0), 3), b1);
  check('円とAABB: 円が中にあっても向きが決まる', inside.hit && !Number.isNaN(inside.normal[0]) && Math.abs(inside.normal[0]) > 0.5, `${fmtVec(inside.normal)} 深さ ${fmtFloat(inside.depth)}`);
  const square = obb(v(0, 0), v(10, 10), 0), tilted = obb(v(23, 0), v(10, 10), f(PI / 4));
  const sat = testObb(square, tilted);
  check('OBB: 45度の角が刺さっているのを検出', sat.hit, `深さ ${sat.depth.toFixed(3)}`);
  check('OBB: 法線は +X 寄り', sat.normal[0] > 0.9, fmtVec(sat.normal));
  check('OBB: 離れていれば当たらない', !testObb(square, obb(v(25, 0), v(10, 10), f(PI / 4))).hit);
  const via = testObb(obb(v(0, 0), v(10, 10), 0), obb(v(16, 4), v(10, 10), 0));
  check('OBB(回転0) と AABB の答えが一致', near(via.depth, boxHit.depth) && near(via.normal[0], boxHit.normal[0]), `SAT 深さ ${fmtFloat(via.depth)} 法線 ${fmtVec(via.normal)}`);
  const co = testCircleObb(circle(v(0, 12), 3), obb(v(0, 0), v(10, 4), f(PI / 2)));
  check('円とOBB: 回転を考慮している', co.hit && near(co.depth, 1), `深さ ${fmtFloat(co.depth)}`);
  check('円とOBB: 法線は -Y', near(co.normal[1], -1), fmtVec(co.normal));
  const mv = circle(v(0, 0), 10), fx = circle(v(12, 0), 10), push = testCircle(mv, fx), h = f(push.depth * 0.5);
  const aa = circle(v(mv.center[0] - f(push.normal[0] * h), mv.center[1] - f(push.normal[1] * h)), 10);
  const bb = circle(v(fx.center[0] + f(push.normal[0] * h), fx.center[1] + f(push.normal[1] * h)), 10);
  check('押し戻すと重なりが消える', !testCircle(aa, bb).hit || testCircle(aa, bb).depth < 0.001);
  lines.push(fails === 0 ? '  すべて合格' : `  ${fails} 件 不合格`);
  return { lines, fails, count: lines.length - 1 };
}

const Col2D = {
  f, PI, TAU, dot, aabb, fromCenter, center, halfSize, circle, obb, obbBounds, toLocal, corners,
  NONE, overlapAabb, overlapCircle, testAabb, testCircle, closestPoint, testCircleAabb, testCircleObb, projectedRadius, satAxes, testObb, test, flip,
  SpatialGrid, sizeScaleFor, DotNetRandom, CIRCLE, BOX, ROTATED, initializeBodies, toCircle, toAabb, toObb, boundsExtent, testBodies, updateBodies,
  fmtFloat, fmtVec, selfCheck,
};
if (typeof window !== 'undefined') window.Col2D = Col2D;
if (typeof module !== 'undefined') module.exports = Col2D;
})();
