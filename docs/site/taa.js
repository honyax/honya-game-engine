// ============================================================
//  TAA の解決(Day 54a・54b の実験台が使う)
//  reference/Day54b の shaders/taa.frag と Render/TemporalAA.cs の移植。
//  54a の実験台は filter: 'bilinear'・toneWeighted: false で使う(Day 54a の taa.frag と同じ式になる)
//  履歴と今の絵は RGBA16F なので、書くときに半精度へ丸める(自己チェックの数字が最後の桁まで揃う)
//  使う側: const { halton, jitterPixels, half, TemporalResolve } = Taa;
// ============================================================
(() => {
'use strict';

// TemporalAA.RadicalInverse。整数の桁を小数点の右へ鏡に映す
function radicalInverse(index, base) {
  let result = 0, f = 1 / base, i = index;
  while (i > 0) { result += f * (i % base); i = Math.floor(i / base); f /= base; }
  return result;
}
const halton = (i) => [radicalInverse(i, 2), radicalInverse(i, 3)];
const jitterPixels = (i) => { const h = halton(i); return [h[0] - 0.5, h[1] - 0.5]; };

// 半精度(符号 1・指数 5・仮数 10)に丸める
function half(x) {
  if (x === 0 || !Number.isFinite(x)) return x === 0 ? 0 : x;
  const a = Math.abs(x);
  if (a >= 65520) return Math.sign(x) * Infinity;
  const step = a < 6.103515625e-5 ? 5.960464477539063e-8 : Math.pow(2, Math.floor(Math.log2(a)) - 10);
  return Math.sign(x) * Math.round(a / step) * step;
}

const toYCoCg = (c) => [0.25 * c[0] + 0.5 * c[1] + 0.25 * c[2], 0.5 * c[0] - 0.5 * c[2], -0.25 * c[0] + 0.5 * c[1] - 0.25 * c[2]];
const fromYCoCg = (c) => [c[0] + c[1] - c[2], c[0] + c[2], c[0] - c[1] - c[2]];
const maxComp = (c) => Math.max(c[0], c[1], c[2]);

function clipTowardCenter(color, low, high) {
  const center = [0, 1, 2].map((i) => 0.5 * (high[i] + low[i]));
  const ext = [0, 1, 2].map((i) => 0.5 * (high[i] - low[i]) + 0.000001);
  const off = [0, 1, 2].map((i) => color[i] - center[i]);
  const far = Math.max(...[0, 1, 2].map((i) => Math.abs(off[i] / ext[i])));
  return far > 1 ? [0, 1, 2].map((i) => center[i] + off[i] / far) : color;
}

// opts: weight(今の絵の割合)・rectify('none' | 'minmax' | 'variance')・gamma・includeCurrent・filter('bilinear' | 'catmull')・toneWeighted
class TemporalResolve {
  constructor(w, h) { this.w = w; this.h = h; this.history = new Float32Array(w * h * 3); this.valid = false; this.moved = new Float32Array(w * h); }
  reset() { this.valid = false; }
  // GL の texture()。端は伸ばす(CLAMP_TO_EDGE)
  texel(x, y) { x = Math.min(Math.max(x, 0), this.w - 1); y = Math.min(Math.max(y, 0), this.h - 1); const k = (y * this.w + x) * 3; return [this.history[k], this.history[k + 1], this.history[k + 2]]; }
  bilinear(u, v) {
    const px = u * this.w - 0.5, py = v * this.h - 0.5, x0 = Math.floor(px), y0 = Math.floor(py), fx = px - x0, fy = py - y0;
    const a = this.texel(x0, y0), b = this.texel(x0 + 1, y0), c = this.texel(x0, y0 + 1), d = this.texel(x0 + 1, y0 + 1);
    return [0, 1, 2].map((i) => (a[i] * (1 - fx) + b[i] * fx) * (1 - fy) + (c[i] * (1 - fx) + d[i] * fx) * fy);
  }
  // SampleCatmullRom: 双線形 9 回で 4x4
  catmull(u, v) {
    const size = [this.w, this.h], pos = [u * this.w, v * this.h];
    const center = pos.map((p) => Math.floor(p - 0.5) + 0.5), f = pos.map((p, i) => p - center[i]);
    const w0 = f.map((t) => t * (-0.5 + t * (1 - 0.5 * t))), w1 = f.map((t) => 1 + t * t * (-2.5 + 1.5 * t));
    const w2 = f.map((t) => t * (0.5 + t * (2 - 1.5 * t))), w3 = f.map((t) => t * t * (-0.5 + 0.5 * t));
    const w12 = [w1[0] + w2[0], w1[1] + w2[1]], o12 = [w2[0] / w12[0], w2[1] / w12[1]];
    const uv0 = center.map((c, i) => (c - 1) / size[i]), uv12 = center.map((c, i) => (c + o12[i]) / size[i]), uv3 = center.map((c, i) => (c + 2) / size[i]);
    const xs = [[uv0[0], w0[0]], [uv12[0], w12[0]], [uv3[0], w3[0]]], ys = [[uv0[1], w0[1]], [uv12[1], w12[1]], [uv3[1], w3[1]]];
    const r = [0, 0, 0];
    for (const [yy, wy] of ys) for (const [xx, wx] of xs) { const s = this.bilinear(xx, yy); for (let i = 0; i < 3; i++) r[i] += s[i] * wx * wy; }
    return r.map((x) => Math.max(x, 0));
  }
  // current: RGB の Float32Array。velocity(i) → [du, dv](UV、前 → 今)。depth(i) → 0〜1
  resolve(current, velocity, depth, o) {
    const W = this.w, H = this.h, out = new Float32Array(W * H * 3);
    const weigh = (c) => (o.toneWeighted ? c.map((x) => x / (1 + maxComp(c))) : c);
    const unweigh = (c) => (o.toneWeighted ? c.map((x) => x / Math.max(1 - maxComp(c), 0.001)) : c);
    const cur = (x, y) => { const k = (y * W + x) * 3; return [half(current[k]), half(current[k + 1]), half(current[k + 2])]; };
    for (let y = 0; y < H; y++) {
      for (let x = 0; x < W; x++) {
        const i = y * W + x, now = cur(x, y);
        let res = now;
        this.moved[i] = 0;
        if (this.valid) {
          const sum = [0, 0, 0], sq = [0, 0, 0], mn = [1e30, 1e30, 1e30], mx = [-1e30, -1e30, -1e30];
          let closest = i, cd = 2;
          for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
            const nx = Math.min(Math.max(x + dx, 0), W - 1), ny = Math.min(Math.max(y + dy, 0), H - 1);
            const c = toYCoCg(weigh(cur(nx, ny)));
            for (let k = 0; k < 3; k++) { sum[k] += c[k]; sq[k] += c[k] * c[k]; mn[k] = Math.min(mn[k], c[k]); mx[k] = Math.max(mx[k], c[k]); }
            const d = depth(ny * W + nx);
            if (d < cd) { cd = d; closest = ny * W + nx; }
          }
          const vel = velocity(closest).map(half);
          const hu = (x + 0.5) / W - vel[0], hv = (y + 0.5) / H - vel[1];
          if (hu >= 0 && hu <= 1 && hv >= 0 && hv <= 1) {
            const hist = o.filter === 'catmull' ? this.catmull(hu, hv) : this.bilinear(hu, hv);
            const hc = toYCoCg(weigh(hist)), cc = toYCoCg(weigh(now));
            let rect = hc;
            if (o.rectify === 'minmax') rect = hc.map((v, k) => Math.min(Math.max(v, mn[k]), mx[k]));
            else if (o.rectify === 'variance') {
              const mean = sum.map((s) => s / 9), sig = sq.map((s, k) => Math.sqrt(Math.max(s / 9 - mean[k] * mean[k], 0)));
              const g = o.gamma ?? 1;
              let low = mean.map((m, k) => Math.max(m - g * sig[k], mn[k])), high = mean.map((m, k) => Math.min(m + g * sig[k], mx[k]));
              if (o.includeCurrent !== false) { low = low.map((l, k) => Math.min(l, cc[k])); high = high.map((hh, k) => Math.max(hh, cc[k])); }
              rect = clipTowardCenter(hc, low, high);
            }
            this.moved[i] = Math.hypot(rect[0] - hc[0], rect[1] - hc[1], rect[2] - hc[2]) / Math.max(hc[0], 0.02);
            const mix = rect.map((r, k) => r + (cc[k] - r) * o.weight);
            res = unweigh(fromYCoCg(mix)).map((v) => Math.max(v, 0));
          }
        }
        for (let k = 0; k < 3; k++) out[i * 3 + k] = half(res[k]);
      }
    }
    this.history = out; this.valid = true;
    return out;
  }
}

// ------------------------------------------------------------
//  横に振るカメラの小さな場面(実験台の作り物。reference には無い)
//  奥の壁(z = 12、レンガ)・床(y = -1、市松)・手前の柱と斜めの棒(z = 3)・動く玉(z = 4)。
//  カメラは x だけ動き、向きは +z 固定。視線ごとに一番手前に当たったものの色と深さを返す
// ------------------------------------------------------------
const CAM_Y = 0.4;
function shadePan(wx, wy, sxN, syN, ball) {
  // 玉(z = 4)
  if (ball) {
    const x = ball.x + 0, z = 4, px = wx + sxN * z, py = CAM_Y + syN * z, dx = px - x, dy = py - ball.y;
    if (dx * dx + dy * dy < ball.r * ball.r) {
      const band = Math.abs(((dx * 2.2 + dy * 1.1) % 0.5 + 0.5) % 0.5 - 0.25) < 0.07;
      return { c: band ? [0.9, 0.85, 0.75] : [1.1, 0.42, 0.12], z, obj: true };
    }
  }
  // 柱と斜めの棒(z = 3)
  {
    const z = 3, px = wx + sxN * z, py = CAM_Y + syN * z;
    const pole = Math.abs(px - 0.6) < 0.09 && py < 2.4 && py > -1;
    // (−1.6, 2.0) から (2.6, 0.9) へ、太さ 0.035m の棒(1画素より細い)
    const ax = -1.6, ay = 2.0, bx = 2.6, by = 0.9, t = Math.min(Math.max(((px - ax) * (bx - ax) + (py - ay) * (by - ay)) / ((bx - ax) ** 2 + (by - ay) ** 2), 0), 1);
    const bar = Math.hypot(px - (ax + (bx - ax) * t), py - (ay + (by - ay) * t)) < 0.0175;
    if (pole || bar) return { c: bar ? [1.6, 1.5, 1.3] : [0.05, 0.05, 0.06], z, obj: false };
  }
  // 床(y = -1)
  if (syN < 0) {
    const z = (CAM_Y + 1) / -syN;
    if (z < 12) {
      const fx = wx + sxN * z, chk = (Math.floor(fx * 1.5) + Math.floor(z * 1.5)) & 1;
      return { c: chk ? [0.30, 0.28, 0.25] : [0.12, 0.11, 0.10], z, obj: false };
    }
  }
  // 奥の壁(z = 12、レンガ)
  const z = 12, px = wx + sxN * z, py = CAM_Y + syN * z;
  const row = Math.floor(py / 0.3), off = (row & 1) * 0.3, mortar = (py / 0.3 - row) < 0.12 || (((px + off) / 0.6) % 1 + 1) % 1 < 0.06;
  return { c: mortar ? [0.55, 0.52, 0.48] : [0.42, 0.16, 0.10], z, obj: false };
}
// W x H 画素を描く。cam: いまのカメラの x、prevCam: 前のフレームの x。jitter: [jx, jy] 画素。
// ball / prevBall: {x, y, r}。objectVelocity: 物の速度(2段目)を書くか
function renderPan(W, H, s) {
  const f = H * 1.1, color = new Float32Array(W * H * 3), depth = new Float32Array(W * H), vel = new Float32Array(W * H * 2), obj = new Uint8Array(W * H);
  const [jx, jy] = s.jitter || [0, 0];
  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const i = y * W + x;
      const sxN = (x + 0.5 + jx - W / 2) / f, syN = (H / 2 - (y + 0.5 + jy)) / f;
      const hit = shadePan(s.cam, CAM_Y, sxN, syN, s.ball);
      color.set(hit.c, i * 3); depth[i] = hit.z / 100; obj[i] = hit.obj ? 1 : 0;
      // 1段目: 止まっている点として、カメラの動きだけで「前 → 今」の移動を出す(ずらしは含めない)
      let dx = -(s.cam - s.prevCam) * f / hit.z;
      // 2段目: 自分で動いた物は、物の動きのぶんを足す
      if (hit.obj && s.objectVelocity && s.prevBall) dx += (s.ball.x - s.prevBall.x) * f / hit.z;
      vel[i * 2] = dx / W; vel[i * 2 + 1] = 0;
    }
  }
  return { color, depth, vel, obj };
}

window.Taa = { radicalInverse, halton, jitterPixels, half, toYCoCg, fromYCoCg, TemporalResolve, renderPan };
})();
