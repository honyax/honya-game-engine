// ============================================================
//  被写界深度とモーションブラー(Day 55a・55b の実験台が使う)
//  reference/Day55b の BlurField / DepthOfField / MotionBlur と、
//  blur-field・blur-tiles・blur-max・dof-prepare・dof-gather・dof-combine・motion-blur の各 .frag の移植。
//  画像は W x H の Float32Array(RGB)。行 0 が gl_FragCoord.y = 0 の行(GL と同じ向き)
//  使う側: const { lensScale, buildField, renderDof, renderMotionBlur, openFraction } = CameraBlur;
// ============================================================
(() => {
'use strict';
const TILE = 16, MAX_RADIUS = 16, SENSOR = 0.024, SOFT_DEPTH = 0.1;

// 半精度(地図・升目・中間の絵は RG16F / RGBA16F)
function half(x) {
  if (x === 0 || !Number.isFinite(x)) return x;
  const a = Math.abs(x), step = a < 6.103515625e-5 ? 5.960464477539063e-8 : Math.pow(2, Math.floor(Math.log2(a)) - 10);
  return Math.sign(x) * Math.round(a / step) * step;
}

// DepthOfField のレンズの式
const focalLength = (fov) => SENSOR * 0.5 / Math.tan(fov * 0.5);
const aperture = (fov, f) => focalLength(fov) / f;
const lensScale = (fov, f, h) => aperture(fov, f) * h / (4 * Math.tan(fov * 0.5));
const coc = (z, s, l) => l * (1 / s - 1 / z);
function thinLensCoc(z, s, fov, f, h) {
  const fl = focalLength(fov), A = aperture(fov, f);
  return 0.5 * (A * fl * (z - s) / (z * (s - fl))) / SENSOR * h;
}
// MotionBlur.OpenFraction
const openFraction = (mode, angle, seconds, frameSeconds) => (mode === 'angle' ? angle / 360 : seconds / Math.max(frameSeconds, 1e-4));

// BlurField.Build: 地図(奥行きと錯乱円)→ 升目の横 → 縦 → 近所
// viewDepth: 奥行き [m] の配列、velocity: UV の配列(x, y の組)か null
function buildField(W, H, viewDepth, velocity, lens, focus) {
  const field = new Float32Array(W * H * 2);
  for (let i = 0; i < W * H; i++) {
    const z = viewDepth[i];
    field[i * 2] = half(z); field[i * 2 + 1] = half(Math.min(Math.max(coc(z, focus, lens), -MAX_RADIUS), MAX_RADIUS));
  }
  const tx = Math.ceil(W / TILE), ty = Math.ceil(H / TILE);
  // blur-tiles.frag: 横 16 画素の最大(いちばん速い速度・いちばん手前の錯乱円・いちばん大きい |錯乱円|)
  const strips = new Float32Array(tx * H * 4);
  for (let y = 0; y < H; y++) for (let t = 0; t < tx; t++) {
    let fx = 0, fy = 0, fs = 0, nearest = 0, largest = 0;
    for (let x = 0; x < TILE; x++) {
      const px = Math.min(t * TILE + x, W - 1), i = y * W + px, c = field[i * 2 + 1];
      nearest = Math.min(nearest, c); largest = Math.max(largest, Math.abs(c));
      if (velocity) { const vx = half(velocity[i * 2]) * W, vy = half(velocity[i * 2 + 1]) * H, s = vx * vx + vy * vy; if (s > fs) { fs = s; fx = vx; fy = vy; } }
    }
    strips.set([fx, fy, nearest, largest].map(half), (y * tx + t) * 4);
  }
  // blur-max.frag: 窓の中の最大
  const maxPass = (src, sw, sh, dw, dh, stride, offset, count) => {
    const out = new Float32Array(dw * dh * 4);
    for (let y = 0; y < dh; y++) for (let x = 0; x < dw; x++) {
      const r = [0, 0, 0, 0]; let fs = 0;
      for (let b = 0; b < count[1]; b++) for (let a = 0; a < count[0]; a++) {
        const sx = Math.min(Math.max(x * stride[0] + offset[0] + a, 0), sw - 1), sy = Math.min(Math.max(y * stride[1] + offset[1] + b, 0), sh - 1), k = (sy * sw + sx) * 4;
        const s = src[k] * src[k] + src[k + 1] * src[k + 1];
        if (s > fs) { fs = s; r[0] = src[k]; r[1] = src[k + 1]; }
        r[2] = Math.min(r[2], src[k + 2]); r[3] = Math.max(r[3], src[k + 3]);
      }
      out.set(r, (y * dw + x) * 4);
    }
    return out;
  };
  const tiles = maxPass(strips, tx, H, tx, ty, [1, TILE], [0, 0], [1, TILE]);
  const neighbors = maxPass(tiles, tx, ty, tx, ty, [1, 1], [-1, -1], [3, 3]);
  return { W, H, field, tx, ty, tiles, neighbors };
}

// GL の texture()(双線形、端は伸ばす)。src は c 成分の配列
function sample(src, w, h, c, u, v, out) {
  const px = u * w - 0.5, py = v * h - 0.5, x0 = Math.floor(px), y0 = Math.floor(py), fx = px - x0, fy = py - y0;
  const at = (x, y, k) => src[(Math.min(Math.max(y, 0), h - 1) * w + Math.min(Math.max(x, 0), w - 1)) * c + k];
  for (let k = 0; k < c; k++) out[k] = (at(x0, y0, k) * (1 - fx) + at(x0 + 1, y0, k) * fx) * (1 - fy) + (at(x0, y0 + 1, k) * (1 - fx) + at(x0 + 1, y0 + 1, k) * fx) * fy;
  return out;
}

// DepthOfField.Render: 下ごしらえ(半分)→ 集める(半分、2層)→ 重ねる(原寸)
function renderDof(scene, bf, opts = {}) {
  const { W, H, field, tx, neighbors } = bf, scatter = opts.scatter !== false, debug = opts.debug;
  const hw = Math.max(1, (W + 1) >> 1), hh = Math.max(1, (H + 1) >> 1);
  // dof-prepare.frag: 2x2 の平均の色と、いちばん手前の錯乱円
  const prep = new Float32Array(hw * hh * 4);
  for (let y = 0; y < hh; y++) for (let x = 0; x < hw; x++) {
    let r = 0, g = 0, b = 0, c = 1e9;
    for (let j = 0; j < 2; j++) for (let i = 0; i < 2; i++) {
      const px = Math.min(x * 2 + i, W - 1), py = Math.min(y * 2 + j, H - 1), k = py * W + px;
      r += scene[k * 3]; g += scene[k * 3 + 1]; b += scene[k * 3 + 2]; c = Math.min(c, field[k * 2 + 1]);
    }
    prep.set([r * 0.25, g * 0.25, b * 0.25, c].map(half), (y * hw + x) * 4);
  }
  // dof-gather.frag: Vogel の円盤に 48 点
  const N = 48, GOLDEN = 2.39996323, cover = (radius, off) => Math.min(Math.max(radius - off + 0.5, 0), 1);
  const smooth = (a, b, x) => { const t = Math.min(Math.max((x - a) / (b - a), 0), 1); return t * t * (3 - 2 * t); };
  const farL = new Float32Array(hw * hh * 4), nearL = new Float32Array(hw * hh * 4), s = [0, 0, 0, 0];
  for (let y = 0; y < hh; y++) for (let x = 0; x < hw; x++) {
    const k = (y * hw + x) * 4, ca = prep[k + 3];
    const tile = neighbors[(Math.min(Math.floor(y * 2 / TILE), bf.ty - 1) * tx + Math.min(Math.floor(x * 2 / TILE), tx - 1)) * 4 + 2];
    // opts.noTiles: 近所の升目を見ずに自分の錯乱円だけで探す(実験台で升目の効き目を見るための切り替え。reference には無い)
    let radius = scatter && !opts.noTiles ? Math.max(Math.abs(ca), -tile) : Math.abs(ca);
    radius = Math.min(radius, MAX_RADIUS);
    if (radius < 0.5) { if (!scatter) farL.set([prep[k], prep[k + 1], prep[k + 2], 1], k); continue; }
    const far = [0, 0, 0, 0], near = [0, 0, 0, 0], u0 = (x + 0.5) / hw, v0 = (y + 0.5) / hh;
    for (let i = 0; i < N; i++) {
      const r = Math.sqrt((i + 0.5) / N) * radius, a = i * GOLDEN;
      sample(prep, hw, hh, 4, u0 + Math.cos(a) * r / W, v0 + Math.sin(a) * r / H, s);
      if (!scatter) { far[0] += s[0]; far[1] += s[1]; far[2] += s[2]; far[3] += 1; continue; }
      const fw = cover(Math.max(Math.min(ca, s[3]), 0), r);
      far[0] += s[0] * fw; far[1] += s[1] * fw; far[2] += s[2] * fw; far[3] += fw;
      const nr = -s[3], spread = radius / Math.max(nr, 1), nw = cover(nr, r) * smooth(1, 2, nr) * spread * spread;
      near[0] += s[0] * nw; near[1] += s[1] * nw; near[2] += s[2] * nw; near[3] += nw;
    }
    if (!scatter) { farL.set([far[0] / far[3], far[1] / far[3], far[2] / far[3], 1].map(half), k); continue; }
    if (ca >= 0.5 && far[3] > 0.0001) farL.set([far[0] / far[3], far[1] / far[3], far[2] / far[3], 1].map(half), k);
    const cov = Math.min(Math.max(near[3] / N, 0), 1), nc = near[3] > 0.0001 ? [near[0] / near[3], near[1] / near[3], near[2] / near[3]] : [0, 0, 0];
    nearL.set([nc[0] * cov, nc[1] * cov, nc[2] * cov, cov].map(half), k);
  }
  // dof-combine.frag: 原寸で重ねる(半分の絵はテントで読む)
  const out = new Float32Array(W * H * 3), t = [0, 0, 0, 0], acc = [0, 0, 0, 0];
  const tent = (src, u, v) => {
    const dx = 0.5 / hw, dy = 0.5 / hh; acc.fill(0);
    for (const [ox, oy] of [[-dx, -dy], [dx, -dy], [-dx, dy], [dx, dy]]) { sample(src, hw, hh, 4, u + ox, v + oy, t); for (let k = 0; k < 4; k++) acc[k] += t[k] * 0.25; }
    return acc.slice();
  };
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
    const i = y * W + x, sharp = [scene[i * 3], scene[i * 3 + 1], scene[i * 3 + 2]], c = field[i * 2 + 1];
    if (debug) {
      const base = (0.2126 * sharp[0] + 0.7152 * sharp[1] + 0.0722 * sharp[2]) * 0.3;
      let col = [base, base, base];
      if (Math.abs(c) >= 1) { const tt = Math.min(Math.abs(c) / MAX_RADIUS, 1), tint = c < 0 ? [0.15, 0.45, 1.6] : [1.6, 0.6, 0.15], m = 0.25 + 0.75 * tt; col = col.map((v, k) => v + (tint[k] - v) * m); }
      out.set(col, i * 3); continue;
    }
    const u = (x + 0.5) / W, v = (y + 0.5) / H, fl = tent(farL, u, v);
    const far = fl[3] > 0.001 ? [fl[0] / fl[3], fl[1] / fl[3], fl[2] / fl[3]] : sharp;
    const amt = smooth(1, 2, scatter ? c : Math.abs(c));
    let col = sharp.map((sv, k) => sv + (far[k] - sv) * amt);
    const nl = tent(nearL, u, v);
    col = col.map((cv, k) => cv * (1 - nl[3]) + nl[k]);
    out.set(col, i * 3);
  }
  return out;
}

// MotionBlur.Render(motion-blur.frag)。velocity は UV の配列
function renderMotionBlur(scene, velocity, bf, opts) {
  const { W, H, field, tx, neighbors } = bf, open = opts.open, useTiles = opts.useTiles !== false;
  const halfExt = (vx, vy) => { let ex = vx * open * 0.5, ey = vy * open * 0.5; const s = Math.hypot(ex, ey); if (s > MAX_RADIUS) { ex *= MAX_RADIUS / s; ey *= MAX_RADIUS / s; } return [ex, ey]; };
  const inFront = (a, b) => Math.min(Math.max(1 - (a - b) / SOFT_DEPTH, 0), 1);
  const cone = (g, e) => Math.min(Math.max(1 - g / e, 0), 1);
  const smooth = (a, b, x) => { const t = Math.min(Math.max((x - a) / (b - a), 0), 1); return t * t * (3 - 2 * t); };
  const cyl = (g, e) => 1 - smooth(0.95 * e, 1.05 * e, g);
  const noise = (x, y) => { const f = (v) => v - Math.floor(v); return f(52.9829189 * f(x * 0.06711056 + y * 0.00583715)); };
  const vel = (i) => [half(velocity[i * 2]) * W, half(velocity[i * 2 + 1]) * H];
  const out = new Float32Array(W * H * 3), N = 15;
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
    const i = y * W + x, color = [scene[i * 3], scene[i * 3 + 1], scene[i * 3 + 2]];
    const own = halfExt(...vel(i));
    const t0 = neighbors[(Math.floor(y / TILE) * tx + Math.floor(x / TILE)) * 4];
    const dom = useTiles ? halfExt(t0, neighbors[(Math.floor(y / TILE) * tx + Math.floor(x / TILE)) * 4 + 1]) : own;
    if (opts.debug) { out.set([color[0] * 0.25 + Math.abs(dom[0]) / MAX_RADIUS * 2, color[1] * 0.25 + Math.abs(dom[1]) / MAX_RADIUS * 2, color[2] * 0.25], i * 3); continue; }
    if (Math.hypot(dom[0], dom[1]) < 0.5) { out.set(color, i * 3); continue; }
    const dX = field[i * 2], eX = Math.max(Math.hypot(own[0], own[1]), 0.5);
    let w = 1 / eX; const sum = color.map((c) => c * w);
    const jit = opts.jitter === false ? 0 : noise(x + 0.5, y + 0.5) - 0.5;
    for (let k = 0; k < N; k++) {
      if (k === (N >> 1)) continue;
      const t = -1 + 2 * ((k + jit + 1) / (N + 1));
      const ox = Math.min(Math.max(Math.floor(x + 0.5 + dom[0] * t), 0), W - 1), oy = Math.min(Math.max(Math.floor(y + 0.5 + dom[1] * t), 0), H - 1), j = oy * W + ox;
      const gap = Math.hypot(ox - x, oy - y), dY = field[j * 2], eY = Math.max(Math.hypot(...halfExt(...vel(j))), 0.5);
      const amt = inFront(dY, dX) * cone(gap, eY) + inFront(dX, dY) * cone(gap, eX) + cyl(gap, eY) * cyl(gap, eX) * 2;
      w += amt; for (let c = 0; c < 3; c++) sum[c] += scene[j * 3 + c] * amt;
    }
    out.set(sum.map((s) => s / w), i * 3);
  }
  return out;
}

window.CameraBlur = { TILE, MAX_RADIUS, half, focalLength, aperture, lensScale, coc, thinLensCoc, openFraction, buildField, renderDof, renderMotionBlur };
})();
