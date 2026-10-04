// ============================================================
//  実験台の共通部品。window.Lab にまとめて置く
//  使う側: const { $, tok, fit, seg, stat, animate } = Lab;
// ============================================================
(() => {
'use strict';
const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
const $ = (id) => document.getElementById(id);
const tok = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
const fmt = (v, d = 2) => (Number.isFinite(v) ? v.toFixed(d) : '∞');

function fit(canvas) {
  const dpr = window.devicePixelRatio || 1;
  const w = canvas.clientWidth, h = canvas.clientHeight;
  const W = Math.max(1, Math.round(w * dpr)), H = Math.max(1, Math.round(h * dpr));
  if (canvas.width !== W || canvas.height !== H) { canvas.width = W; canvas.height = H; }
  const ctx = canvas.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);
  return { ctx, w, h };
}

// セグメントボタン。選ばれた値を返すコールバックを呼ぶ
function seg(id, onChange) {
  const root = $(id);
  root.addEventListener('click', (ev) => {
    const btn = ev.target.closest('button');
    if (!btn) return;
    setSeg(id, btn.dataset.v);
    onChange(btn.dataset.v);
  });
}
function setSeg(id, v) {
  for (const b of $(id).querySelectorAll('button')) b.setAttribute('aria-pressed', String(b.dataset.v === String(v)));
}
function segValue(id) {
  const b = $(id).querySelector('button[aria-pressed="true"]');
  return b ? b.dataset.v : null;
}
function stat(k, v, extra = '') { return `<div class="stat"><div class="k">${k}</div><div class="v">${v}${extra}</div></div>`; }

function arrow(ctx, x0, y0, x1, y1, color, width = 2) {
  const dx = x1 - x0, dy = y1 - y0;
  const len = Math.hypot(dx, dy);
  if (len < 0.5) return;
  const ux = dx / len, uy = dy / len;
  const head = Math.min(10, len * 0.5);
  ctx.strokeStyle = color; ctx.fillStyle = color; ctx.lineWidth = width;
  ctx.beginPath(); ctx.moveTo(x0, y0); ctx.lineTo(x1 - ux * head * 0.8, y1 - uy * head * 0.8); ctx.stroke();
  ctx.beginPath(); ctx.moveTo(x1, y1);
  ctx.lineTo(x1 - ux * head - uy * head * 0.5, y1 - uy * head + ux * head * 0.5);
  ctx.lineTo(x1 - ux * head + uy * head * 0.5, y1 - uy * head - ux * head * 0.5);
  ctx.closePath(); ctx.fill();
}

// 球を1つ描く。線は向きの目印(回っていれば分かる)
function drawBall(ctx, sx, sy, sr, angle, color) {
  ctx.fillStyle = color;
  ctx.beginPath(); ctx.arc(sx, sy, sr, 0, Math.PI * 2); ctx.fill();
  ctx.strokeStyle = 'rgba(0,0,0,0.35)'; ctx.lineWidth = 1; ctx.stroke();
  ctx.strokeStyle = 'rgba(255,255,255,0.85)'; ctx.lineWidth = Math.max(1.5, sr * 0.12);
  ctx.beginPath();
  ctx.moveTo(sx - Math.cos(angle) * sr * 0.75, sy + Math.sin(angle) * sr * 0.75);
  ctx.lineTo(sx + Math.cos(angle) * sr * 0.75, sy - Math.sin(angle) * sr * 0.75);
  ctx.stroke();
  ctx.fillStyle = 'rgba(255,255,255,0.95)';
  ctx.beginPath(); ctx.arc(sx + Math.cos(angle) * sr * 0.55, sy - Math.sin(angle) * sr * 0.55, Math.max(2, sr * 0.13), 0, Math.PI * 2); ctx.fill();
}
const rgb = (r, g, b) => `rgb(${Math.round(r * 255)},${Math.round(g * 255)},${Math.round(b * 255)})`;

// 画面内にあるものだけ毎フレーム回す
const animators = [];
const visible = new WeakMap();
const io = 'IntersectionObserver' in window
  ? new IntersectionObserver((entries) => { for (const e of entries) visible.set(e.target, e.isIntersecting); }, { rootMargin: '100px' })
  : null;
function animate(el, fn) { animators.push({ el, fn }); if (io) io.observe(el); else visible.set(el, true); }
let last = performance.now();
function frame(now) {
  const dt = Math.min((now - last) / 1000, 0.1);
  last = now;
  for (const a of animators) if (visible.get(a.el) !== false) a.fn(dt);
  requestAnimationFrame(frame);
}

window.Lab = { reduceMotion, $, tok, fmt, fit, seg, setSeg, segValue, stat, arrow, drawBall, rgb, animate };
requestAnimationFrame(frame);
})();
