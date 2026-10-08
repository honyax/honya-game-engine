// ============================================================
//  Phase 1(ソフトウェアラスタライザ)のクラスを JS に移したもの。window.Raster にまとめて置く
//  Day 3〜10 の計画書の実験台が共有する。Day ごとに reference の形のまま足していく
//  - Day 3: Framebuffer.cs(0xAARRGGBB の int 配列、Bresenham の DrawLine)と
//           Rasterizer.cs の FillTriangle(エッジ関数、巻き方向の正規化、top-left rule、加算合成)
//  - Day 4: Vertex.cs と Framebuffer.Rgb(float)、バリセントリック補間する FillTriangle(PixelShader を受け取る)
//
//  - 画素の色は reference と同じ 0xAARRGGBB の整数で持つ。canvas に出すときだけ並べ替える(toCanvas)
//  - 実験台が数えたいもの(判定した画素・塗った画素)は stats に足していく。reference には無い
// ============================================================
(() => {
'use strict';

// ---------------- Framebuffer ----------------
const rgb = (r, g, b) => (0xFF000000 | (r << 16) | (g << 8) | b) >>> 0;
class Framebuffer {
  constructor(width, height) { this.width = width; this.height = height; this.pixels = new Uint32Array(width * height); }
  clear(color) { this.pixels.fill(color); }
  setPixel(x, y, color) { if (x < 0 || y < 0 || x >= this.width || y >= this.height) return; this.pixels[y * this.width + x] = color; }
  // Bresenham の一般形(Day 2 の DrawLineBresenham。Day 3 から DrawLine の本体)
  drawLine(x0, y0, x1, y1, color) {
    const dx = Math.abs(x1 - x0), dy = -Math.abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
    let err = dx + dy;
    for (;;) {
      this.setPixel(x0, y0, color);
      if (x0 === x1 && y0 === y1) break;
      const e2 = err * 2;
      if (e2 >= dy) { err += dy; x0 += sx; }
      if (e2 <= dx) { err += dx; y0 += sy; }
    }
  }
  // canvas に貼る(0xAARRGGBB → ImageData の R, G, B, A)
  toCanvas(canvas) {
    const ctx = canvas.getContext('2d'), img = ctx.createImageData(this.width, this.height), d = new Uint32Array(img.data.buffer), p = this.pixels;
    for (let i = 0; i < p.length; i++) { const v = p[i]; d[i] = (v & 0xFF00FF00) | ((v >>> 16) & 0xFF) | ((v & 0xFF) << 16); }
    ctx.putImageData(img, 0, 0);
  }
}

// ---------------- Day 3 の Rasterizer ----------------
// 2 次元の外積。符号で内外、大きさで面積(の 2 倍)
const edgeFunction = (ax, ay, bx, by, px, py) => (bx - ax) * (py - ay) - (by - ay) * (px - ax);
// 巻き方向を正にそろえた y 下向きの座標系で、上の辺か左の辺か
const isTopLeft = (ax, ay, bx, by) => (ay === by && bx > ax) || by < ay;
const addSaturate = (d, s) => rgb(Math.min(((d >>> 16) & 255) + ((s >>> 16) & 255), 255), Math.min(((d >>> 8) & 255) + ((s >>> 8) & 255), 255), Math.min((d & 255) + (s & 255), 255));
class Rasterizer3 {
  constructor(target) { this.target = target; this.useTopLeftRule = true; this.additiveBlend = false; this.stats = { tested: 0, filled: 0, triangles: 0 }; this.onFill = null; }
  fillTriangle(x0, y0, x1, y1, x2, y2, color) {
    this.stats.triangles++;
    const area = edgeFunction(x0, y0, x1, y1, x2, y2);
    if (area === 0) return;
    if (area < 0) [x1, y1, x2, y2] = [x2, y2, x1, y1];   // 負なら頂点を入れ替えて正にそろえる
    const t = this.target;
    const minX = Math.max(Math.min(x0, x1, x2), 0), maxX = Math.min(Math.max(x0, x1, x2), t.width - 1);
    const minY = Math.max(Math.min(y0, y1, y2), 0), maxY = Math.min(Math.max(y0, y1, y2), t.height - 1);
    const tl = this.useTopLeftRule;
    const bias0 = !tl || isTopLeft(x1, y1, x2, y2) ? 0 : -1;
    const bias1 = !tl || isTopLeft(x2, y2, x0, y0) ? 0 : -1;
    const bias2 = !tl || isTopLeft(x0, y0, x1, y1) ? 0 : -1;
    const px = t.pixels, w = t.width;
    for (let y = minY; y <= maxY; y++) {
      for (let x = minX; x <= maxX; x++) {
        this.stats.tested++;
        const w0 = edgeFunction(x1, y1, x2, y2, x, y) + bias0;
        const w1 = edgeFunction(x2, y2, x0, y0, x, y) + bias1;
        const w2 = edgeFunction(x0, y0, x1, y1, x, y) + bias2;
        if ((w0 | w1 | w2) >= 0) {
          const i = y * w + x;
          px[i] = this.additiveBlend ? addSaturate(px[i], color) : color;
          this.stats.filled++;
          if (this.onFill) this.onFill(x, y);
        }
      }
    }
  }
  drawTriangleWireframe(x0, y0, x1, y1, x2, y2, color) { const t = this.target; t.drawLine(x0, y0, x1, y1, color); t.drawLine(x1, y1, x2, y2, color); t.drawLine(x2, y2, x0, y0, color); }
}

// ---------------- Day 4: Vertex と、バリセントリック補間する Rasterizer ----------------
// Framebuffer.Rgb(float, float, float): 0〜1 にクランプして 255 倍し、四捨五入
const rgbF = (r, g, b) => rgb(Math.trunc(Math.min(1, Math.max(0, r)) * 255 + 0.5), Math.trunc(Math.min(1, Math.max(0, g)) * 255 + 0.5), Math.trunc(Math.min(1, Math.max(0, b)) * 255 + 0.5));
// Vertex: 位置は int、色(属性)は float
const vertex = (x, y, r, g, b) => ({ x, y, r, g, b });
const vertexFromPacked = (x, y, c) => vertex(x, y, ((c >>> 16) & 255) / 255, ((c >>> 8) & 255) / 255, (c & 255) / 255);
class Rasterizer4 {
  constructor(target) { this.target = target; this.stats = { tested: 0, filled: 0, triangles: 0 }; }
  // shader(a0, a1, a2) → 色。無ければ属性をそのまま色にする(Rgb(float))
  fillTriangle(v0, v1, v2, shader) {
    this.stats.triangles++;
    let area = edgeFunction(v0.x, v0.y, v1.x, v1.y, v2.x, v2.y);
    if (area === 0) return;
    if (area < 0) { [v1, v2] = [v2, v1]; area = -area; }   // 頂点ごと入れ替える(色も一緒に)
    const t = this.target;
    const minX = Math.max(Math.min(v0.x, v1.x, v2.x), 0), maxX = Math.min(Math.max(v0.x, v1.x, v2.x), t.width - 1);
    const minY = Math.max(Math.min(v0.y, v1.y, v2.y), 0), maxY = Math.min(Math.max(v0.y, v1.y, v2.y), t.height - 1);
    const bias0 = isTopLeft(v1.x, v1.y, v2.x, v2.y) ? 0 : -1, bias1 = isTopLeft(v2.x, v2.y, v0.x, v0.y) ? 0 : -1, bias2 = isTopLeft(v0.x, v0.y, v1.x, v1.y) ? 0 : -1;
    const invArea = 1 / area, px = t.pixels, w = t.width;
    for (let y = minY; y <= maxY; y++) {
      for (let x = minX; x <= maxX; x++) {
        this.stats.tested++;
        const w0 = edgeFunction(v1.x, v1.y, v2.x, v2.y, x, y), w1 = edgeFunction(v2.x, v2.y, v0.x, v0.y, x, y), w2 = edgeFunction(v0.x, v0.y, v1.x, v1.y, x, y);
        if (((w0 + bias0) | (w1 + bias1) | (w2 + bias2)) < 0) continue;   // 判定はバイアス付き
        const l0 = w0 * invArea, l1 = w1 * invArea, l2 = w2 * invArea;   // 補間は生の値
        const a0 = l0 * v0.r + l1 * v1.r + l2 * v2.r, a1 = l0 * v0.g + l1 * v1.g + l2 * v2.g, a2 = l0 * v0.b + l1 * v1.b + l2 * v2.b;
        px[y * w + x] = shader ? shader(a0, a1, a2, l0, l1, l2) : rgbF(a0, a1, a2);
        this.stats.filled++;
      }
    }
  }
  drawTriangleWireframe(x0, y0, x1, y1, x2, y2, color) { const t = this.target; t.drawLine(x0, y0, x1, y1, color); t.drawLine(x1, y1, x2, y2, color); t.drawLine(x2, y2, x0, y0, color); }
}

// GameWindow の HueColor(Day 2〜)
function hueColor(h01) {
  const h = (h01 - Math.floor(h01)) * 6, s = Math.trunc(h), f = h - s, up = Math.trunc(f * 255), down = Math.trunc((1 - f) * 255);
  return [rgb(255, up, 0), rgb(down, 255, 0), rgb(0, 255, up), rgb(0, down, 255), rgb(up, 0, 255), rgb(255, 0, down)][Math.min(5, s)];
}

window.Raster = { rgb, Framebuffer, edgeFunction, isTopLeft, Rasterizer3, hueColor, rgbF, vertex, vertexFromPacked, Rasterizer4 };
})();
