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
//  - Day 9: Light.cs(Shade)と Mesh.cs(立方体、球、床)、GameWindow.DrawMesh(Day 9・10 の2日で使うのでここに置く)
//  - Day 10: 背面カリングと近クリップ面のクリッピング(サザーランド・ホジマン)
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

// ---------------- Day 9: Light と Mesh ----------------
const reflect3 = (i, n) => { const d = 2 * dot3(i, n); return [i[0] - d * n[0], i[1] - d * n[1], i[2] - d * n[2]]; };
// Light.Shade: 色 = アルベド × (環境光 + 光の色 × 拡散) + 光の色 × 鏡面。parts でどの成分を足すか選べる(このページだけ)
const defaultLight = () => ({ position: [3, 4, 3], color: [1, 0.97, 0.9], ambient: [0.12, 0.13, 0.18], shininess: 32, specularStrength: 0.6 });
function shade(light, world, normal, albedo, cameraPos, parts = { ambient: true, diffuse: true, specular: true, normalize: true }) {
  const n = parts.normalize === false ? normal : norm3(normal);
  const toLight = norm3(sub3(light.position, world));
  const diffuse = Math.max(dot3(n, toLight), 0);
  let specular = 0;
  if (diffuse > 0) {   // 裏から当たっている面にはハイライトを出さない
    const toCam = norm3(sub3(cameraPos, world)), r = reflect3([-toLight[0], -toLight[1], -toLight[2]], n);
    specular = Math.pow(Math.max(dot3(r, toCam), 0), light.shininess) * light.specularStrength;
  }
  const amb = parts.ambient === false ? [0, 0, 0] : light.ambient, dif = parts.diffuse === false ? 0 : diffuse, spe = parts.specular === false ? 0 : specular;
  return [0, 1, 2].map((k) => albedo[k] * (amb[k] + light.color[k] * dif) + light.color[k] * spe);
}
// Mesh: 頂点 { pos, normal, uv } と索引。どれも外から見て反時計回り
const Mesh = {
  cube() {
    const fn = [[-1, 0, 0], [1, 0, 0], [0, -1, 0], [0, 1, 0], [0, 0, -1], [0, 0, 1]], fc = [[0, 2, 6, 4], [5, 7, 3, 1], [0, 4, 5, 1], [2, 3, 7, 6], [1, 3, 2, 0], [4, 6, 7, 5]];
    const uvs = [[0, 0], [1, 0], [1, 1], [0, 1]], vertices = [], indices = [];
    for (let f = 0; f < 6; f++) {
      for (let k = 0; k < 4; k++) { const c = fc[f][k]; vertices.push({ pos: [c & 1 ? 1 : -1, c & 2 ? 1 : -1, c & 4 ? 1 : -1], normal: fn[f], uv: uvs[k] }); }
      const b = f * 4; indices.push(b, b + 2, b + 1, b, b + 3, b + 2);
    }
    return { vertices, indices };
  },
  sphere(rings, segments) {
    const vertices = [], indices = [];
    for (let r = 0; r <= rings; r++) {
      const phi = (Math.PI * r) / rings, y = Math.cos(phi), rr = Math.sin(phi);
      for (let s = 0; s <= segments; s++) { const th = (Math.PI * 2 * s) / segments, p = [rr * Math.sin(th), y, rr * Math.cos(th)]; vertices.push({ pos: p, normal: p, uv: [s / segments, r / rings] }); }
    }
    for (let r = 0; r < rings; r++) for (let s = 0; s < segments; s++) { const c = r * (segments + 1) + s, n = c + segments + 1; indices.push(c, n, c + 1, c + 1, n, n + 1); }
    return { vertices, indices };
  },
  plane(half, uvRepeat, div) {
    const vertices = [], indices = [];
    for (let z = 0; z <= div; z++) for (let x = 0; x <= div; x++) { const fx = x / div, fz = z / div; vertices.push({ pos: [(fx * 2 - 1) * half, 0, (fz * 2 - 1) * half], normal: [0, 1, 0], uv: [fx * uvRepeat, fz * uvRepeat] }); }
    for (let z = 0; z < div; z++) for (let x = 0; x < div; x++) { const c = z * (div + 1) + x, n = c + div + 1; indices.push(c, n, c + 1, c + 1, n, n + 1); }
    return { vertices, indices };
  },
};

// GameWindow.DrawMesh(Day 9〜10): 全頂点をワールドへ変換してから、索引で三角形を組む
// 属性 vary = [r, g, b, u, v, nx, ny, nz, wx, wy, wz](色・UV・法線・ワールド座標)
// o: { mode: 'flat' | 'gouraud' | 'phong', albedo, light, cam(カメラの位置), tex, emissive, parts, wire }
function drawMesh(ras, mesh, model, vp, o) {
  const { mode, albedo, light, cam, tex = null, emissive = false, parts, wire = false } = o;
  const verts = mesh.vertices.map((v) => {
    const pos = Mat4.transformPoint(v.pos, model), n = norm3(Mat4.transformDirection(v.normal, model));
    const color = mode === 'gouraud' && !emissive ? shade(light, pos, n, albedo, cam, parts) : albedo;
    return { pos, n, uv: v.uv, color };
  });
  const albedoAt = (a) => { if (!tex) return [a[0], a[1], a[2]]; const t = tex.sample(a[3], a[4]); return [t[0] * a[0], t[1] * a[1], t[2] * a[2]]; };
  const shader = emissive ? (a) => rgbF(a[0], a[1], a[2])
    : mode === 'phong' ? (a) => { const c = shade(light, [a[8], a[9], a[10]], [a[5], a[6], a[7]], albedoAt(a), cam, parts); return rgbF(c[0], c[1], c[2]); }
    : (a) => { const c = albedoAt(a); return rgbF(c[0], c[1], c[2]); };
  const mk = (v, color) => ({ pos: v.pos, vary: [...color, ...v.uv, ...v.n, ...v.pos] });
  let n = 0;
  for (let i = 0; i + 2 < mesh.indices.length; i += 3) {
    const v0 = verts[mesh.indices[i]], v1 = verts[mesh.indices[i + 1]], v2 = verts[mesh.indices[i + 2]];
    let c0 = v0.color, c1 = v1.color, c2 = v2.color;
    if (mode === 'flat' && !emissive) {
      // 面の法線は 2 辺の外積(巻き方向で向きが決まる)、明るさは面の中心で 1 回だけ
      const fnrm = norm3(cross3(sub3(v1.pos, v0.pos), sub3(v2.pos, v0.pos)));
      const center = [0, 1, 2].map((k) => (v0.pos[k] + v1.pos[k] + v2.pos[k]) / 3);
      c0 = c1 = c2 = shade(light, center, fnrm, albedo, cam, parts);
    }
    ras.drawTriangle(mk(v0, c0), mk(v1, c1), mk(v2, c2), vp, shader);
    n++;
    if (wire) { const s = [v0, v1, v2].map((v) => projectToScreen(v.pos, vp, ras.target.width, ras.target.height)); if (s.every(Boolean)) ras.drawTriangleWireframe(s[0], s[1], s[2], rgb(255, 60, 60)); }
  }
  return n;
}

// ---------------- Day 10: 背面カリングと、近クリップ面のクリッピング ----------------
// cull: 'back' | 'front' | 'none'。clip を false にすると Day 9 までの「W が正でない頂点を含む三角形は捨てる」(このページだけ)
class Rasterizer10 extends Rasterizer8 {
  constructor(target) { super(target); this.cull = 'back'; this.clip = true; this.culled = 0; this.drawn = 0; }
  resetStatistics() { this.culled = 0; this.drawn = 0; }
  drawTriangle(v0, v1, v2, mvp, shader) {
    if (!this.clip) return super.drawTriangle(v0, v1, v2, mvp, shader);
    // クリップ座標のまま、z >= 0(近クリップ面の内側)で切る。サザーランド・ホジマン
    const src = [v0, v1, v2].map((v) => ({ c: Mat4.transform([v.pos[0], v.pos[1], v.pos[2], 1], mvp), vary: v.vary }));
    const out = [];
    for (let i = 0; i < 3; i++) {
      const a = src[i], b = src[(i + 1) % 3], da = a.c[2], db = b.c[2], ain = da >= 0, bin = db >= 0;
      if (ain) out.push(a);
      if (ain !== bin) { const t = da / (da - db); out.push({ c: a.c.map((x, k) => x + (b.c[k] - x) * t), vary: a.vary.map((x, k) => x + (b.vary[k] - x) * t) }); }
    }
    if (out.length < 3) return false;
    const T = this.target, toScreen = (q) => { if (q.c[3] <= 1e-5) return null; const iw = 1 / q.c[3]; return { pos: [(q.c[0] * iw * 0.5 + 0.5) * T.width, (0.5 - q.c[1] * iw * 0.5) * T.height, q.c[2] * iw], invW: iw, vary: q.vary }; };
    for (let i = 1; i + 1 < out.length; i++) {   // 最大 4 頂点の多角形をファンで三角形に戻す
      const s = [out[0], out[i], out[i + 1]].map(toScreen);
      if (s.every(Boolean)) this.fillTriangle(s[0], s[1], s[2], shader);
    }
    return true;
  }
  fillTriangle(v0, v1, v2, shader) {
    const E = (a, b, px, py) => (b[0] - a[0]) * (py - a[1]) - (b[1] - a[1]) * (px - a[0]);
    const area = E(v0.pos, v1.pos, v2.pos[0], v2.pos[1]);
    if (area === 0) return;
    // Day 3 で捨てていた符号。モデルが外から見て反時計回りなら、画面(y 下向き)では負が表
    const front = area < 0;
    if (this.cull !== 'none' && (this.cull === 'back' ? !front : front)) { this.culled++; return; }
    this.drawn++;
    super.fillTriangle(v0, v1, v2, shader);
  }
}

// GameWindow の HueColor(Day 2〜)
function hueColor(h01) {
  const h = (h01 - Math.floor(h01)) * 6, s = Math.trunc(h), f = h - s, up = Math.trunc(f * 255), down = Math.trunc((1 - f) * 255);
  return [rgb(255, up, 0), rgb(down, 255, 0), rgb(0, 255, up), rgb(0, down, 255), rgb(up, 0, 255), rgb(255, 0, down)][Math.min(5, s)];
}

window.Raster = { rgb, Framebuffer, edgeFunction, isTopLeft, Rasterizer3, hueColor, rgbF, vertex, vertexFromPacked, Rasterizer4, Mat4, vertex5, Rasterizer5, sub3, dot3, cross3, norm3, camera, viewProjection, projectToScreen, DepthBuffer, Rasterizer7, unpack3, Texture, Rasterizer8, reflect3, defaultLight, shade, Mesh, drawMesh, Rasterizer10 };
})();
