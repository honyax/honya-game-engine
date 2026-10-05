// ============================================================
//  ソフトウェア描画の実験台が共有する小さな部品(カメラ・投影・陰影)。window.SoftView にまとめて置く
//  Day 63a・63b の実験台が使う。OrbitCameraController.EyePosition と同じカメラで、視野角は 60 度
// ============================================================
(() => {
'use strict';
const sub3 = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const cross3 = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const norm3 = (a) => { const l = Math.hypot(a[0], a[1], a[2]) || 1; return [a[0] / l, a[1] / l, a[2] / l]; };

// 原点(または target)を見るカメラ。project(p) は [画面の x, 画面の y, 奥行き]。zoom を上げると画角が狭くなる
function makeView(w, h, yaw, pitch, dist, target = [0, 0, 0], zoom = 1) {
  const cp = Math.cos(pitch);
  const eye = [target[0] + dist * cp * Math.sin(yaw), target[1] + dist * Math.sin(pitch), target[2] + dist * cp * Math.cos(yaw)];
  const f = norm3(sub3(target, eye));
  const r = norm3(cross3(f, [0, 1, 0]));
  const u = cross3(r, f);
  const focal = zoom * (h / 2) / Math.tan(Math.PI / 6);
  const project = (p) => {
    const d = sub3(p, eye);
    const z = d[0] * f[0] + d[1] * f[1] + d[2] * f[2];
    const x = d[0] * r[0] + d[1] * r[1] + d[2] * r[2];
    const y = d[0] * u[0] + d[1] * u[1] + d[2] * u[2];
    return [w / 2 + (x / z) * focal, h / 2 - (y / z) * focal, z];
  };
  return { eye, project, focal };
}

const LIGHT = norm3([0.35, 0.8, 0.5]);
// 法線 n の面の明るさ。base は 0〜1 の色、lo + hi * (n・光) 倍する。shadeRGB は 0〜255 の数、shade は CSS の色文字列
function shadeRGB(n, base, lo = 0.4, hi = 0.8) {
  const d = Math.max(0, n[0] * LIGHT[0] + n[1] * LIGHT[1] + n[2] * LIGHT[2]);
  const k = lo + hi * d;
  return base.map((v) => Math.round(Math.min(1, v * k) * 255));
}
const shade = (n, base, lo, hi) => `rgb(${shadeRGB(n, base, lo, hi).join(',')})`;

window.SoftView = { sub3, cross3, norm3, makeView, LIGHT, shade, shadeRGB };
})();
