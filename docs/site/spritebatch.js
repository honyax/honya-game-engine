// ============================================================
//  スプライトバッチ(Day 17・18 の実験台が使う)。window.SpriteBatchSim にまとめて置く
//  reference/Day17〜18 の SpriteBatch.cs(Begin / Draw / Flush / End、Day 18 のソートキーと切れ目ごとの描画)と、
//  Program.cs のスプライト(InitializeSprites・UpdateSprites)を JS に移したもの。GPU は使わず、
//  「どのドローコールに何枚入ったか」を記録して、その記録どおりに Canvas 2D で描く
//
//  - スプライトの絵は assets/textures の sprite-*.png をそのまま data URI で埋め込んである(ready で読み終わる)
//  - 乱数は dotnet.js の DotNetRandom(.NET の new Random(20260816) と同じ並び)。先に読んでおく
//  - 色の掛け算は Canvas 2D でできないので、色を段階に丸めて染めた絵を作り置きする(色は見た目が近いだけ)
//  使う側: const SB = SpriteBatchSim; SB.ready.then((tex) => { ... });
// ============================================================
(() => {
'use strict';
// assets/textures の PNG そのもの(circle・ring は Day 17、star・diamond は Day 18 で足したもの)
const IMAGES = {
  'sprite-circle': 'data:image/png;base64,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',
  'sprite-ring': 'data:image/png;base64,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',
  'sprite-star': 'data:image/png;base64,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',
  'sprite-diamond': 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAq0lEQVR42t2XIRIDIRAEeds9FgQGh8Vi0VgkDoUgqWzlBy2YRLTuruIOdp333t3E/XrA8+VKwBNCeH0gEUgeYzRIBJKnlAwSgeQ5Z4NEIHkpxSARSF5rNUgEkrfWDBKB5L13g0Qg+RjDIBFIPuc0SASSr7UMEoHke2+DRCD5OccgEVIB149A4iOU+A0lLiKJq1jiMZJ4jiUGEomRTGIolRjLJRYTidXsP7bjN9lOutwqPkSyAAAAAElFTkSuQmCC',
};

// ---------------- テクスチャ ----------------
// { name, handle, w, h, canvas, data(RGBA、行 0 が画像の一番上) }。handle は GL の名前の代わり(ソートキーに使う)
let nextHandle = 2;
function textureFromCanvas(name, canvas) {
  const ctx = canvas.getContext('2d');
  return { name, handle: nextHandle++, w: canvas.width, h: canvas.height, canvas, data: ctx.getImageData(0, 0, canvas.width, canvas.height).data };
}
const ready = Promise.all(Object.entries(IMAGES).map(([name, uri]) => new Promise((res) => {
  const img = new Image();
  img.onload = () => { const c = document.createElement('canvas'); c.width = img.width; c.height = img.height; c.getContext('2d').drawImage(img, 0, 0); res([name, textureFromCanvas(name, c)]); };
  img.src = uri;
}))).then((list) => Object.fromEntries(list));

// テクスチャ全体を指す領域(Day 18 の AtlasRegion。Day 17 は常にこれ)
const whole = (texture) => ({ texture, uvMin: [0, 0], uvMax: [1, 1] });

// ---------------- スプライト(Program.InitializeSprites / UpdateSprites) ----------------
// Day 18 は Kind(何番目の絵か)と Layer を足している。Layer の NextDouble が増えるぶん、並びは Day 17 とずれる
function makeSprites(n, w, h, day18) {
  const r = new DotNet.DotNetRandom(20260816), out = [];
  for (let i = 0; i < n; i++) {
    const speed = 60 + r.nextDouble() * 120, dir = r.nextDouble() * Math.PI * 2;
    const s = { x: r.nextDouble() * w, y: r.nextDouble() * h, vx: Math.cos(dir) * speed, vy: Math.sin(dir) * speed };
    s.size = 14 + r.nextDouble() * 24; s.rotation = r.nextDouble() * Math.PI * 2; s.spin = (r.nextDouble() - 0.5) * 3;
    s.color = [0.45 + r.nextDouble() * 0.55, 0.45 + r.nextDouble() * 0.55, 0.45 + r.nextDouble() * 0.55, 0.85];
    if (day18) { s.kind = i % 4; s.layer = r.nextDouble(); }
    out.push(s);
  }
  return out;
}
function updateSprites(sprites, count, dt, w, h) {
  for (let i = 0; i < count; i++) {
    const s = sprites[i], half = s.size * 0.5;
    s.x += s.vx * dt; s.y += s.vy * dt; s.rotation += s.spin * dt;
    if (s.x < half) { s.x = half; s.vx = -s.vx; } else if (s.x > w - half) { s.x = w - half; s.vx = -s.vx; }
    if (s.y < half) { s.y = half; s.vy = -s.vy; } else if (s.y > h - half) { s.y = h - half; s.vy = -s.vy; }
  }
}

// ---------------- SpriteBatch ----------------
// day18 が false なら Day 17 の作り(テクスチャが変わるか満杯で Flush = 転送 + ドローコール 1 回)。
// true なら Day 18 の作り(満杯か Immediate でテクスチャが変わったら FlushAll。並べ替え → 転送 1 回 → 切れ目ごとにドローコール)
// 結果は calls(ドローコールごとの { texture, quads })、drawCalls、uploads(BufferSubData の回数)、uploadedBytes に残る
class Batch {
  constructor({ capacity = 4000, day18 = false, vertexBytes = 32 } = {}) {
    Object.assign(this, { capacity, day18, vertexBytes, batching: true, orphaning: false });
    this.pending = []; this.began = false;
  }
  begin(sortMode = 'Texture') {
    if (this.began) throw new Error('Begin が二重に呼ばれています。End を先に呼んでください');
    this.began = true; this.sortMode = sortMode;
    this.drawCalls = 0; this.spriteCount = 0; this.uploads = 0; this.uploadedBytes = 0; this.calls = [];
    this.current = null; this.pending = [];
  }
  // region: { texture, uvMin, uvMax }。回転した4隅を CPU で計算して頂点に焼き込む
  draw(region, cx, cy, sw, sh, rotation, color, layer = 0) {
    if (!this.began) throw new Error('Begin を先に呼んでください');
    const t = region.texture;
    if (!this.day18) {
      if (this.current !== t || this.pending.length >= this.capacity) { this.flush(); this.current = t; }
    } else {
      if (this.pending.length >= this.capacity) this.flushAll();
      if (this.sortMode === 'Immediate' && this.current && this.current !== t) this.flushAll();
      this.current = t;
    }
    const c = Math.cos(rotation), s = Math.sin(rotation);
    const rx = c * sw * 0.5, ry = s * sw * 0.5, dx = -s * sh * 0.5, dy = c * sh * 0.5;
    const q = {
      texture: t, uvMin: region.uvMin, uvMax: region.uvMax, color, order: this.pending.length,
      tl: [cx - rx - dx, cy - ry - dy], tr: [cx + rx - dx, cy + ry - dy], br: [cx + rx + dx, cy + ry + dy], bl: [cx - rx + dx, cy - ry + dy],
    };
    if (this.day18) q.key = this.sortKey(t, layer);
    this.pending.push(q);
    this.spriteCount++;
    if (!this.batching) { if (this.day18) this.flushAll(); else this.flush(); }
  }
  // Day 18 の MakeSortKey: 上位 32 ビットにレイヤー(0〜65535 に量子化)、下位にテクスチャのハンドル
  sortKey(texture, layer) {
    if (this.sortMode !== 'BackToFront') return texture.handle;
    const qz = Math.floor(Math.min(1, Math.max(0, layer)) * 65535);
    return qz * 4294967296 + texture.handle;
  }
  upload(n) { this.uploads++; this.uploadedBytes += n * 4 * this.vertexBytes; }
  // Day 17: 溜まったぶんを送って、1 回描く
  flush() {
    if (!this.pending.length || !this.current) return;
    this.upload(this.pending.length);
    this.calls.push({ texture: this.current, quads: this.pending, transfer: true });
    this.drawCalls++;
    this.pending = [];
  }
  // Day 18: 並べ替えて 1 回で送り、テクスチャの切れ目ごとにオフセットをずらして描く
  // (JS の sort は安定なので、同じキーは積んだ順のまま。C# の Array.Sort は不安定)
  flushAll() {
    if (!this.pending.length) return;
    let list = this.pending;
    if (this.sortMode !== 'Immediate') list = list.slice().sort((a, b) => a.key - b.key);
    this.upload(list.length);
    let start = 0;
    for (let i = 1; i <= list.length; i++) {
      if (i < list.length && list[i].texture === list[start].texture) continue;
      this.calls.push({ texture: list[start].texture, quads: list.slice(start, i), transfer: start === 0, offset: start });
      this.drawCalls++;
      start = i;
    }
    this.pending = []; this.current = null;
  }
  end() {
    if (!this.began) throw new Error('Begin が呼ばれていません');
    if (this.day18) this.flushAll(); else this.flush();
    this.began = false;
  }
}

// ---------------- 描く(記録したドローコールを Canvas 2D で) ----------------
// 色ごとに染めた絵を作り置きする。key = 領域 + 丸めた色 + ブレンド
const tinted = new Map();
const STEPS = 8;
function tint(region, color, opaque) {
  const t = region.texture, q = (v) => Math.round(v * STEPS) / STEPS;
  const sx = Math.round(region.uvMin[0] * t.w), sy = Math.round((1 - region.uvMax[1]) * t.h);
  const sw = Math.max(1, Math.round((region.uvMax[0] - region.uvMin[0]) * t.w)), sh = Math.max(1, Math.round((region.uvMax[1] - region.uvMin[1]) * t.h));
  const r = q(color[0]), g = q(color[1]), b = q(color[2]);
  const key = `${t.handle}:${sx},${sy},${sw},${sh}:${r},${g},${b}:${opaque ? 1 : 0}`;
  let c = tinted.get(key);
  if (c) return c;
  c = document.createElement('canvas'); c.width = sw; c.height = sh;
  const ctx = c.getContext('2d'), img = ctx.createImageData(sw, sh);
  for (let y = 0; y < sh; y++) for (let x = 0; x < sw; x++) {
    const i = ((sy + y) * t.w + sx + x) * 4, o = (y * sw + x) * 4, a = t.data[i + 3];
    // ブレンドを切ると α は効かない。透明なところも元の灰色(166)のまま塗られて、四角い板になる
    const base = opaque && a === 0 ? [166, 166, 166] : [t.data[i], t.data[i + 1], t.data[i + 2]];
    img.data[o] = base[0] * r; img.data[o + 1] = base[1] * g; img.data[o + 2] = base[2] * b; img.data[o + 3] = opaque ? 255 : a;
  }
  ctx.putImageData(img, 0, 0);
  tinted.set(key, c);
  return c;
}
// blend: 'alpha'(SrcAlpha, OneMinusSrcAlpha)/ 'add'(SrcAlpha, One)/ 'none'(ブレンド無効)。scale は描き先の倍率
function render(ctx, calls, { blend = 'alpha', scale = 1 } = {}) {
  ctx.save();
  ctx.globalCompositeOperation = blend === 'add' ? 'lighter' : 'source-over';
  for (const call of calls) for (const q of call.quads) {
    const src = tint(q, q.color, blend === 'none');
    // 単位正方形を tl・tr・bl へ写す変換で、回転した四角形に貼る(左上に UV の (uMin, vMax) が来る)
    ctx.setTransform(scale * (q.tr[0] - q.tl[0]), scale * (q.tr[1] - q.tl[1]), scale * (q.bl[0] - q.tl[0]), scale * (q.bl[1] - q.tl[1]), scale * q.tl[0], scale * q.tl[1]);
    ctx.globalAlpha = blend === 'none' ? 1 : q.color[3];
    ctx.drawImage(src, 0, 0, 1, 1);
  }
  ctx.restore();
}

window.SpriteBatchSim = { ready, textureFromCanvas, whole, makeSprites, updateSprites, Batch, render };
})();
