// ============================================================
//  Phase 1(ソフトウェアラスタライザ)のクラスを JS に移したもの。window.Raster にまとめて置く
//  Day 3〜10 の計画書の実験台が共有する。Day ごとに reference の形のまま足していく
//  - Day 3: Framebuffer.cs(0xAARRGGBB の int 配列、Bresenham の DrawLine)と
//           Rasterizer.cs の FillTriangle(エッジ関数、巻き方向の正規化、top-left rule、加算合成)
//  - Day 4: Vertex.cs と Framebuffer.Rgb(float)、バリセントリック補間する FillTriangle(PixelShader を受け取る)
//  - Day 5: Mat4.cs(行ベクトル規約)と、float の頂点を画素の中心で判定する FillTriangle(バイアスは最小の正の数)
//  - Day 6: Mat4.LookAt と Perspective、Camera.cs、Rasterizer.TryProjectToScreen
//  - Day 7: DepthBuffer.cs と、深度テストつきの FillTriangle(色を計算する前に判定する)
//  - Day 8: Texture.cs(ニアレスト、バイリニア、リピート)と、透視補正つきの FillTriangle(属性は配列で持つ)
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

// ---------------- Day 5: Mat4(行ベクトル規約)と、float の頂点を画素の中心で判定する Rasterizer ----------------
// m[r * 4 + c] が M(r+1)(c+1)。点は横に寝た行ベクトルで v' = v * M。平行移動は 4 行目(M41〜M43)
const Mat4 = {
  identity: () => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
  translation: (x, y, z = 0) => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1],
  scale: (x, y = x, z = x) => [x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, 1],
  rotationX(t) { const c = Math.cos(t), s = Math.sin(t); return [1, 0, 0, 0, 0, c, s, 0, 0, -s, c, 0, 0, 0, 0, 1]; },
  rotationY(t) { const c = Math.cos(t), s = Math.sin(t); return [c, 0, -s, 0, 0, 1, 0, 0, s, 0, c, 0, 0, 0, 0, 1]; },
  rotationZ(t) { const c = Math.cos(t), s = Math.sin(t); return [c, s, 0, 0, -s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]; },
  // a * b(左に書いた変換から順に適用)
  mul(a, b) { const o = new Array(16); for (let r = 0; r < 4; r++) for (let c = 0; c < 4; c++) o[r * 4 + c] = a[r * 4] * b[c] + a[r * 4 + 1] * b[4 + c] + a[r * 4 + 2] * b[8 + c] + a[r * 4 + 3] * b[12 + c]; return o; },
  // 同次座標のまま v * M
  transform(v, m) { const o = [0, 0, 0, 0]; for (let c = 0; c < 4; c++) o[c] = v[0] * m[c] + v[1] * m[4 + c] + v[2] * m[8 + c] + v[3] * m[12 + c]; return o; },
  transformPoint: (v, m) => Mat4.transform([v[0], v[1], v[2], 1], m).slice(0, 3),       // W = 1: 平行移動を受ける
  transformDirection: (v, m) => Mat4.transform([v[0], v[1], v[2], 0], m).slice(0, 3),   // W = 0: 受けない
  transposed(m) { const o = []; for (let r = 0; r < 4; r++) for (let c = 0; c < 4; c++) o[r * 4 + c] = m[c * 4 + r]; return o; },
};
// 頂点は { pos: [x, y](float), color: [r, g, b] }
const vertex5 = (pos, color) => ({ pos, color });
class Rasterizer5 {
  constructor(target) { this.target = target; this.stats = { tested: 0, filled: 0, triangles: 0 }; }
  // float.Epsilon の代わりに、double で表せる最小の正の数を引く(ちょうど 0 だった値だけが負になる)
  static get topLeftBias() { return Number.MIN_VALUE; }
  fillTriangle(v0, v1, v2, shader) {
    this.stats.triangles++;
    const E = (a, b, px, py) => (b[0] - a[0]) * (py - a[1]) - (b[1] - a[1]) * (px - a[0]);
    const TL = (a, b) => (a[1] === b[1] && b[0] > a[0]) || b[1] < a[1];
    let area = E(v0.pos, v1.pos, v2.pos[0], v2.pos[1]);
    if (area === 0) return;
    if (area < 0) { [v1, v2] = [v2, v1]; area = -area; }
    const t = this.target, p0 = v0.pos, p1 = v1.pos, p2 = v2.pos;
    const minX = Math.max(Math.floor(Math.min(p0[0], p1[0], p2[0])), 0), maxX = Math.min(Math.ceil(Math.max(p0[0], p1[0], p2[0])), t.width - 1);
    const minY = Math.max(Math.floor(Math.min(p0[1], p1[1], p2[1])), 0), maxY = Math.min(Math.ceil(Math.max(p0[1], p1[1], p2[1])), t.height - 1);
    const B = Rasterizer5.topLeftBias, b0 = TL(p1, p2) ? 0 : -B, b1 = TL(p2, p0) ? 0 : -B, b2 = TL(p0, p1) ? 0 : -B;
    const inv = 1 / area, px = t.pixels, w = t.width, c0 = v0.color, c1 = v1.color, c2 = v2.color;
    for (let y = minY; y <= maxY; y++) {
      const py = y + 0.5;   // 画素の中心
      for (let x = minX; x <= maxX; x++) {
        this.stats.tested++;
        const qx = x + 0.5;
        const w0 = E(p1, p2, qx, py), w1 = E(p2, p0, qx, py), w2 = E(p0, p1, qx, py);
        if (w0 + b0 < 0 || w1 + b1 < 0 || w2 + b2 < 0) continue;
        const l0 = w0 * inv, l1 = w1 * inv, l2 = w2 * inv;
        const a = [c0[0] * l0 + c1[0] * l1 + c2[0] * l2, c0[1] * l0 + c1[1] * l1 + c2[1] * l2, c0[2] * l0 + c1[2] * l1 + c2[2] * l2];
        px[y * w + x] = shader ? shader(a) : rgbF(a[0], a[1], a[2]);
        this.stats.filled++;
        if (this.onFill) this.onFill(x, y);
      }
    }
  }
  drawTriangleWireframe(a, b, c, color) {
    const t = this.target, r = (v) => [Math.round(v[0]), Math.round(v[1])];
    const [A, Bp, C] = [r(a), r(b), r(c)];
    t.drawLine(A[0], A[1], Bp[0], Bp[1], color); t.drawLine(Bp[0], Bp[1], C[0], C[1], color); t.drawLine(C[0], C[1], A[0], A[1], color);
  }
}

// ---------------- Day 6: LookAt・Perspective・Camera と、透視除算 + ビューポート変換 ----------------
const sub3 = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const dot3 = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const cross3 = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const norm3 = (a) => { const l = Math.hypot(a[0], a[1], a[2]) || 1; return [a[0] / l, a[1] / l, a[2] / l]; };
// カメラの姿勢の逆変換。回転は転置、平行移動は各軸との内積の符号を反転
Mat4.lookAt = (eye, target, up) => {
  const z = norm3(sub3(eye, target)), x = norm3(cross3(up, z)), y = cross3(z, x);
  return [x[0], y[0], z[0], 0, x[1], y[1], z[1], 0, x[2], y[2], z[2], 0, -dot3(x, eye), -dot3(y, eye), -dot3(z, eye), 1];
};
// M34 = -1 で Z を W へコピーし、M44 = 0。深度は near で 0、far で 1(m44one はこのページだけ: M44 = 1 にして W を 1 に固定する)
Mat4.perspective = (fovY, aspect, near, far, m44one = false) => {
  const ys = 1 / Math.tan(fovY * 0.5), xs = ys / aspect;
  return [xs, 0, 0, 0, 0, ys, 0, 0, 0, 0, far / (near - far), m44one ? 0 : -1, 0, 0, (near * far) / (near - far), m44one ? 1 : 0];
};
// Camera.cs(位置・注視点・視野角から View と Projection を作る)
const camera = (o = {}) => ({ position: [0, 0, 5], target: [0, 0, 0], up: [0, 1, 0], fov: Math.PI / 3, aspect: 4 / 3, near: 0.1, far: 100, ...o });
const viewProjection = (c, m44one) => Mat4.mul(Mat4.lookAt(c.position, c.target, c.up), Mat4.perspective(c.fov, c.aspect, c.near, c.far, m44one));
// Rasterizer.TryProjectToScreen: クリップ座標 → W で割って NDC → ピクセル。W が正でなければ null(Day 10 までは三角形ごと捨てる)
function projectToScreen(p, mvp, width, height) {
  const c = Mat4.transform([p[0], p[1], p[2], 1], mvp);
  if (c[3] <= 1e-5) return null;
  const iw = 1 / c[3];
  return [(c[0] * iw * 0.5 + 0.5) * width, (0.5 - c[1] * iw * 0.5) * height, c[2] * iw];
}

// ---------------- Day 7: DepthBuffer と、深度テストつきの Rasterizer ----------------
// 頂点の pos は [画面 x, 画面 y, NDC の z](z は 0 = near、1 = far。画面上で線形なので単純補間でよい)
class DepthBuffer {
  constructor(w, h) { this.width = w; this.height = h; this.depth = new Float32Array(w * h); this.clear(); }
  clear() { this.depth.fill(1); }   // 1.0 = 遠クリップ面。何も描かれていない
}
class Rasterizer7 {
  constructor(target) { this.target = target; this.depthBuffer = new DepthBuffer(target.width, target.height); this.depthTest = true; this.laterWins = false; this.stats = { tested: 0, filled: 0, triangles: 0, rejected: 0 }; }
  // TryProjectToScreen を 3 頂点に通し、1 つでも W が正でなければ捨てる
  drawTriangle(v0, v1, v2, mvp, shader) {
    const t = this.target, s = [v0, v1, v2].map((v) => projectToScreen(v.pos, mvp, t.width, t.height));
    if (s.some((p) => !p)) return;
    this.fillTriangle(vertex5(s[0], v0.color), vertex5(s[1], v1.color), vertex5(s[2], v2.color), shader);
  }
  fillTriangle(v0, v1, v2, shader) {
    this.stats.triangles++;
    const E = (a, b, px, py) => (b[0] - a[0]) * (py - a[1]) - (b[1] - a[1]) * (px - a[0]);
    const TL = (a, b) => (a[1] === b[1] && b[0] > a[0]) || b[1] < a[1];
    let area = E(v0.pos, v1.pos, v2.pos[0], v2.pos[1]);
    if (area === 0) return;
    if (area < 0) { [v1, v2] = [v2, v1]; area = -area; }
    const t = this.target, p0 = v0.pos, p1 = v1.pos, p2 = v2.pos;
    const minX = Math.max(Math.floor(Math.min(p0[0], p1[0], p2[0])), 0), maxX = Math.min(Math.ceil(Math.max(p0[0], p1[0], p2[0])), t.width - 1);
    const minY = Math.max(Math.floor(Math.min(p0[1], p1[1], p2[1])), 0), maxY = Math.min(Math.ceil(Math.max(p0[1], p1[1], p2[1])), t.height - 1);
    const B = Number.MIN_VALUE, b0 = TL(p1, p2) ? 0 : -B, b1 = TL(p2, p0) ? 0 : -B, b2 = TL(p0, p1) ? 0 : -B;
    const inv = 1 / area, px = t.pixels, w = t.width, depth = this.depthBuffer.depth, c0 = v0.color, c1 = v1.color, c2 = v2.color;
    for (let y = minY; y <= maxY; y++) {
      const py = y + 0.5;
      for (let x = minX; x <= maxX; x++) {
        this.stats.tested++;
        const qx = x + 0.5, w0 = E(p1, p2, qx, py), w1 = E(p2, p0, qx, py), w2 = E(p0, p1, qx, py);
        if (w0 + b0 < 0 || w1 + b1 < 0 || w2 + b2 < 0) continue;
        const l0 = w0 * inv, l1 = w1 * inv, l2 = w2 * inv, i = y * w + x;
        // 深度テストは色を計算する前に。等しければ先に描いたほうを残す(laterWins はこのページだけ: > で捨てる = 後勝ち)
        // 深度だけは reference と同じく float で計算する(同一平面の 2 枚が丸めでばらつく = Zファイティングが出る)
        const F = Math.fround, z = F(F(F(F(p0[2]) * F(l0)) + F(F(p1[2]) * F(l1))) + F(F(p2[2]) * F(l2)));
        if (this.depthTest) {
          if (this.laterWins ? z > depth[i] : z >= depth[i]) { this.stats.rejected++; continue; }
          depth[i] = z;
        }
        const a = [c0[0] * l0 + c1[0] * l1 + c2[0] * l2, c0[1] * l0 + c1[1] * l1 + c2[1] * l2, c0[2] * l0 + c1[2] * l1 + c2[2] * l2];
        px[i] = shader ? shader(a) : rgbF(a[0], a[1], a[2]);
        this.stats.filled++;
      }
    }
  }
  drawTriangleWireframe(a, b, c, color) { Rasterizer5.prototype.drawTriangleWireframe.call(this, a, b, c, color); }
}

// ---------------- Day 8: Texture と、透視補正つきで属性を補間する Rasterizer ----------------
const unpack3 = (c) => [((c >>> 16) & 255) / 255, ((c >>> 8) & 255) / 255, (c & 255) / 255];
const wrapIndex = (v, n) => ((v % n) + n) % n;   // 負の UV でも配列の外に出ない
class Texture {
  constructor(width, height) { this.width = width; this.height = height; this.texels = new Uint32Array(width * height); this.filter = 'bilinear'; }
  sample(u, v) { return this.filter === 'nearest' ? this.sampleNearest(u, v) : this.sampleBilinear(u, v); }
  sampleNearest(u, v) { const x = wrapIndex(Math.floor(u * this.width), this.width), y = wrapIndex(Math.floor(v * this.height), this.height); return unpack3(this.texels[y * this.width + x]); }
  // テクセルの色は中心にあるので -0.5 してから、周囲 4 つを混ぜる
  sampleBilinear(u, v) {
    const W = this.width, H = this.height, x = u * W - 0.5, y = v * H - 0.5, x0 = Math.floor(x), y0 = Math.floor(y), fx = x - x0, fy = y - y0;
    const X0 = wrapIndex(x0, W), Y0 = wrapIndex(y0, H), X1 = wrapIndex(x0 + 1, W), Y1 = wrapIndex(y0 + 1, H);
    const c00 = unpack3(this.texels[Y0 * W + X0]), c10 = unpack3(this.texels[Y0 * W + X1]), c01 = unpack3(this.texels[Y1 * W + X0]), c11 = unpack3(this.texels[Y1 * W + X1]);
    return [0, 1, 2].map((k) => { const top = c00[k] + (c10[k] - c00[k]) * fx, bot = c01[k] + (c11[k] - c01[k]) * fx; return top + (bot - top) * fy; });
  }
  // Texture.CreateTestPattern: 市松 + 中央のオレンジの円 + 縁の水色
  static createTestPattern(size, cells) {
    const t = new Texture(size, size), cell = Math.max(Math.trunc(size / cells), 1), c = size / 2, r = size * 0.34;
    for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
      const dark = (Math.trunc(x / cell) + Math.trunc(y / cell)) % 2 === 0;
      let col = dark ? [0.16, 0.20, 0.32] : [0.88, 0.86, 0.78];
      if (Math.hypot(x + 0.5 - c, y + 0.5 - c) < r) col = dark ? [0.90, 0.45, 0.15] : [0.95, 0.70, 0.30];
      if (x === 0 || y === 0 || x === size - 1 || y === size - 1) col = [0.10, 0.85, 0.75];
      t.texels[y * size + x] = rgbF(...col);
    }
    return t;
  }
}
// 頂点は { pos: [x, y, z](モデル座標), vary: [...](色や UV など。属性はいくつでもよい) }
// 透視補正: 属性/W と 1/W は画面上で線形なので、その 2 つを補間してから割り戻す
class Rasterizer8 {
  constructor(target) { this.target = target; this.depthBuffer = new DepthBuffer(target.width, target.height); this.depthTest = true; this.perspectiveCorrect = true; this.stats = { tested: 0, filled: 0, triangles: 0, rejected: 0 }; }
  drawTriangle(v0, v1, v2, mvp, shader) {
    const t = this.target, s = [v0, v1, v2].map((v) => this.project(v.pos, mvp));
    if (s.some((p) => !p)) return false;
    this.fillTriangle({ pos: s[0].screen, invW: s[0].invW, vary: v0.vary }, { pos: s[1].screen, invW: s[1].invW, vary: v1.vary }, { pos: s[2].screen, invW: s[2].invW, vary: v2.vary }, shader);
    return true;
  }
  project(p, mvp) {
    const t = this.target, c = Mat4.transform([p[0], p[1], p[2], 1], mvp);
    if (c[3] <= 1e-5) return null;
    const iw = 1 / c[3];
    return { screen: [(c[0] * iw * 0.5 + 0.5) * t.width, (0.5 - c[1] * iw * 0.5) * t.height, c[2] * iw], invW: iw, clip: c };
  }
  fillTriangle(v0, v1, v2, shader) {
    this.stats.triangles++;
    const E = (a, b, px, py) => (b[0] - a[0]) * (py - a[1]) - (b[1] - a[1]) * (px - a[0]);
    const TL = (a, b) => (a[1] === b[1] && b[0] > a[0]) || b[1] < a[1];
    let area = E(v0.pos, v1.pos, v2.pos[0], v2.pos[1]);
    if (area === 0) return;
    if (area < 0) { [v1, v2] = [v2, v1]; area = -area; }
    const t = this.target, p0 = v0.pos, p1 = v1.pos, p2 = v2.pos;
    const minX = Math.max(Math.floor(Math.min(p0[0], p1[0], p2[0])), 0), maxX = Math.min(Math.ceil(Math.max(p0[0], p1[0], p2[0])), t.width - 1);
    const minY = Math.max(Math.floor(Math.min(p0[1], p1[1], p2[1])), 0), maxY = Math.min(Math.ceil(Math.max(p0[1], p1[1], p2[1])), t.height - 1);
    const B = Number.MIN_VALUE, b0 = TL(p1, p2) ? 0 : -B, b1 = TL(p2, p0) ? 0 : -B, b2 = TL(p0, p1) ? 0 : -B;
    // 三角形ごとに 1 回: 属性を W で割っておく(補正を切ると W = 1 として単純補間になる)
    const pc = this.perspectiveCorrect, iw0 = pc ? v0.invW : 1, iw1 = pc ? v1.invW : 1, iw2 = pc ? v2.invW : 1;
    const n = v0.vary.length, a0 = v0.vary.map((x) => x * iw0), a1 = v1.vary.map((x) => x * iw1), a2 = v2.vary.map((x) => x * iw2);
    const inv = 1 / area, px = t.pixels, w = t.width, depth = this.depthBuffer.depth, F = Math.fround;
    const vary = new Array(n);
    for (let y = minY; y <= maxY; y++) {
      const py = y + 0.5;
      for (let x = minX; x <= maxX; x++) {
        this.stats.tested++;
        const qx = x + 0.5, w0 = E(p1, p2, qx, py), w1 = E(p2, p0, qx, py), w2 = E(p0, p1, qx, py);
        if (w0 + b0 < 0 || w1 + b1 < 0 || w2 + b2 < 0) continue;
        const l0 = w0 * inv, l1 = w1 * inv, l2 = w2 * inv, i = y * w + x;
        const z = F(F(F(F(p0[2]) * F(l0)) + F(F(p1[2]) * F(l1))) + F(F(p2[2]) * F(l2)));
        if (this.depthTest) { if (z >= depth[i]) { this.stats.rejected++; continue; } depth[i] = z; }
        // ピクセルごと: 補間して、補間した 1/W で割り戻す
        const W1 = 1 / (iw0 * l0 + iw1 * l1 + iw2 * l2);
        for (let k = 0; k < n; k++) vary[k] = (a0[k] * l0 + a1[k] * l1 + a2[k] * l2) * W1;
        px[i] = shader(vary, x, y);
        this.stats.filled++;
      }
    }
  }
  drawTriangleWireframe(a, b, c, color) { Rasterizer5.prototype.drawTriangleWireframe.call(this, a, b, c, color); }
}

// GameWindow の HueColor(Day 2〜)
function hueColor(h01) {
  const h = (h01 - Math.floor(h01)) * 6, s = Math.trunc(h), f = h - s, up = Math.trunc(f * 255), down = Math.trunc((1 - f) * 255);
  return [rgb(255, up, 0), rgb(down, 255, 0), rgb(0, 255, up), rgb(0, down, 255), rgb(up, 0, 255), rgb(255, 0, down)][Math.min(5, s)];
}

window.Raster = { rgb, Framebuffer, edgeFunction, isTopLeft, Rasterizer3, hueColor, rgbF, vertex, vertexFromPacked, Rasterizer4, Mat4, vertex5, Rasterizer5, sub3, dot3, cross3, norm3, camera, viewProjection, projectToScreen, DepthBuffer, Rasterizer7, unpack3, Texture, Rasterizer8 };
})();
