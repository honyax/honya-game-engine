// ============================================================
//  球面調和(SH)の実験台が共有する部品。window.SH にまとめて置く
//  reference の Render/SphericalHarmonics.cs(9係数の基底・立体角・キューブの向き・射影・余弦の畳み込み・評価)を JS に移したもの。
//  式・順番・定数は C# と同じ(C# は float、こちらは double なので、値は 4 桁まで一致する)。
//  実験台のために、次数を上げた版(l = 3, 4)も足してある。reference は 2 次(9 個)まで。
// ============================================================
(() => {
'use strict';

// --- 基底(reference と同じ 9 個)。n = [x, y, z] は単位ベクトル
const B0 = 0.282095, B1 = 0.488603, B2 = 1.092548, B2Z = 0.315392, B2S = 0.546274;
function basis9(n) {
  const [x, y, z] = n;
  return [B0, B1 * y, B1 * z, B1 * x, B2 * x * y, B2 * y * z, B2Z * (3 * z * z - 1), B2 * x * z, B2S * (x * x - y * y)];
}
// --- 次数を上げた版(実数の球面調和、l = 0〜4。25 個)。並びは l ごとに m = −l〜l。9 個の版と l = 0〜2 の値は同じ並びではない(下の mapTo9 で対応を取る)
// Condon-Shortley の符号を付けない、Sloan の資料と同じ流儀(l = 0〜2 は basis9 と一致する)
function basisN(n, maxL) {
  const [x, y, z] = n;
  const out = [];
  // 連関ルジャンドル。極の軸は z(reference の basis9 と同じ: l = 1 は y, z, x の順、l = 2 は xy, yz, 3z²−1, xz, x²−y² の順)
  const ct = z, st = Math.sqrt(Math.max(0, 1 - z * z));
  const phi = Math.atan2(y, x); // x–y 面の角度
  const P = (l, m) => {
    // P_l^m(ct)、m >= 0。再帰
    let pmm = 1;
    if (m > 0) { const somx2 = st; let fact = 1; for (let i = 1; i <= m; i++) { pmm *= -fact * somx2; fact += 2; } }
    if (l === m) return pmm;
    let pmmp1 = ct * (2 * m + 1) * pmm;
    if (l === m + 1) return pmmp1;
    let pll = 0;
    for (let ll = m + 2; ll <= l; ll++) { pll = (ct * (2 * ll - 1) * pmmp1 - (ll + m - 1) * pmm) / (ll - m); pmm = pmmp1; pmmp1 = pll; }
    return pll;
  };
  const fact = (k) => { let r = 1; for (let i = 2; i <= k; i++) r *= i; return r; };
  const K = (l, m) => Math.sqrt((2 * l + 1) / (4 * Math.PI) * fact(l - m) / fact(l + m));
  for (let l = 0; l <= maxL; l++) {
    for (let m = -l; m <= l; m++) {
      const am = Math.abs(m);
      const base = Math.SQRT2 * K(l, am) * P(l, am) * (am % 2 ? -1 : 1); // Condon-Shortley を打ち消す
      out.push(m === 0 ? K(l, 0) * P(l, 0) : (m > 0 ? base * Math.cos(am * phi) : base * Math.sin(am * phi)));
    }
  }
  return out;
}
// 余弦のこぶと畳み込んだあとに残る割合 Â_l / π(l = 0: 1、l = 1: 2/3、l = 2: 1/4、奇数の l >= 3: 0、偶数の l >= 4: 負の小さな値)
function bandFactorOfL(l) {
  if (l === 0) return 1;
  if (l === 1) return 2 / 3;
  if (l % 2) return 0;
  const fact = (k) => { let r = 1; for (let i = 2; i <= k; i++) r *= i; return r; };
  const c = fact(l) / (2 ** l * fact(l / 2) ** 2);
  return 2 * Math.PI * (((l / 2 - 1) % 2) ? -1 : 1) / ((l + 2) * (l - 1)) * c / Math.PI;
}
// reference の BandFactor(係数の番号 → 掛ける割合)。9 個の版
const bandFactor9 = (i) => (i === 0 ? 1 : i < 4 ? 2 / 3 : 0.25);

// --- キューブマップ。OpenGL の仕様の表(Table 8.19)。reference と同じ
function cubeTexelDirection(face, s, t) {
  const u = 2 * s - 1, v = 2 * t - 1;
  let d;
  switch (face) {
    case 0: d = [1, -v, -u]; break;
    case 1: d = [-1, -v, u]; break;
    case 2: d = [u, 1, v]; break;
    case 3: d = [u, -1, -v]; break;
    case 4: d = [u, -v, 1]; break;
    default: d = [-u, -v, -1];
  }
  const l = Math.hypot(d[0], d[1], d[2]);
  return [d[0] / l, d[1] / l, d[2] / l];
}
// テクセルの立体角(厳密な式。4 隅で足し引き)
const areaElement = (x, y) => Math.atan2(x * y, Math.sqrt(x * x + y * y + 1));
function texelSolidAngle(x, y, size) {
  const inv = 1 / size;
  const x0 = 2 * x * inv - 1, y0 = 2 * y * inv - 1, x1 = 2 * (x + 1) * inv - 1, y1 = 2 * (y + 1) * inv - 1;
  return areaElement(x0, y0) - areaElement(x0, y1) - areaElement(x1, y0) + areaElement(x1, y1);
}
// よく使われる近似(テクセルの中心の値 x 面積)。厳密な式との比較に使う
function texelSolidAngleApprox(x, y, size) {
  const u = 2 * (x + 0.5) / size - 1, v = 2 * (y + 0.5) / size - 1;
  return 4 / (size * size * Math.pow(1 + u * u + v * v, 1.5));
}

// --- 射影・畳み込み・評価
// radiance(direction) → 値(スカラー)。size x size x 6 面で足す。weighting: 'exact' | 'approx' | 'equal'
function projectFunction(radiance, size, maxL = 2, weighting = 'exact') {
  const count = (maxL + 1) * (maxL + 1);
  const c = new Array(count).fill(0);
  const equal = 4 * Math.PI / (6 * size * size);
  for (let face = 0; face < 6; face++) for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const d = cubeTexelDirection(face, (x + 0.5) / size, (y + 0.5) / size);
    const w = weighting === 'equal' ? equal : weighting === 'approx' ? texelSolidAngleApprox(x, y, size) : texelSolidAngle(x, y, size);
    const v = radiance(d);
    if (v === 0) continue;
    const b = maxL === 2 ? basis9(d) : basisN(d, maxL);
    for (let i = 0; i < count; i++) c[i] += v * b[i] * w;
  }
  return c;
}
// 放射輝度の係数 → 放射照度 / π の係数(次数ごとに掛け算)
function toIrradiance(c, maxL = 2) {
  const out = c.slice();
  let i = 0;
  for (let l = 0; l <= maxL; l++) for (let m = -l; m <= l; m++, i++) out[i] *= bandFactorOfL(l);
  return out;
}
// l = cutoffL までだけ使って評価する(それより上の次数は 0 とみなす)
function evaluate(c, n, maxL = 2, cutoffL = maxL) {
  const b = maxL === 2 ? basis9(n) : basisN(n, maxL);
  let s = 0, i = 0;
  for (let l = 0; l <= maxL; l++) for (let m = -l; m <= l; m++, i++) if (l <= cutoffL) s += c[i] * b[i];
  return s;
}
// 本当の放射照度 / π(n を軸にした半球で、radiance に余弦を掛けて、キューブの刻みで積分する。刻みが細かいほど正確)
function trueIrradiance(radiance, n, size) {
  let s = 0;
  for (let face = 0; face < 6; face++) for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const d = cubeTexelDirection(face, (x + 0.5) / size, (y + 0.5) / size);
    const cos = d[0] * n[0] + d[1] * n[1] + d[2] * n[2];
    if (cos <= 0) continue;
    const v = radiance(d);
    if (v) s += v * cos * texelSolidAngle(x, y, size);
  }
  return s / Math.PI;
}

const api = { basis9, basisN, bandFactorOfL, bandFactor9, cubeTexelDirection, texelSolidAngle, texelSolidAngleApprox, projectFunction, toIrradiance, evaluate, trueIrradiance };
if (typeof window !== 'undefined') window.SH = api;
if (typeof module !== 'undefined') module.exports = api; // Node での検算用
})();
