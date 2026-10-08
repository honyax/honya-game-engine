// ============================================================
//  Canvas に三角形を描く、小さなソフトウェア版の GL(Day 14〜18 の実験台が使う)。window.SoftGL にまとめて置く
//  GPU のしていること(頂点シェーダ → クリップ → ビューポート → ラスタライズと補間 → フラグメントシェーダ →
//  深度テスト → 書き込み)を、何が起きているか分かる程度に JS で真似たもの。GL と画素単位で一致はしない
//
//  - ラスタライズは GL と同じ窓の座標(左下が原点、y は上向き)で行い、色と深度は canvas と同じく上の行から並べて持つ。
//    readPixel(x, y) の座標は GL と同じ(左下が原点)
//  - シェーダは JS の関数。vs(頂点) → { pos: [x, y, z, w](クリップ座標), vary: [...] }、fs(vary, grad) → [r, g, b, a](0〜1)
//    grad() は隣の画素(x+1 と y+1)での vary を返す(テクスチャのミップマップの段を決めるのに使う)
//  - 行列は System.Numerics.Matrix4x4 と同じ並び(行優先 M11, M12, …, M44 の 16 個)・同じ規約(行ベクトル v * M)
//  - テクスチャは画素の配列から作る(ファイルの画像は file:// で開いたページから読めないので、絵はページで作る)
//  使う側: const G = SoftGL;
// ============================================================
(() => {
'use strict';

// ---------------- 行列(System.Numerics.Matrix4x4 と同じ並びと規約) ----------------
// m[r * 4 + c] が M(r+1)(c+1)。メモリの並びもこの順(行優先)
const Mat = {
  identity: () => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
  // a * b(行ベクトル規約なので「a を適用してから b」)
  mul(a, b) {
    const o = new Array(16).fill(0);
    for (let r = 0; r < 4; r++) for (let c = 0; c < 4; c++) { let s = 0; for (let k = 0; k < 4; k++) s += a[r * 4 + k] * b[k * 4 + c]; o[r * 4 + c] = s; }
    return o;
  },
  rotationZ(t) { const c = Math.cos(t), s = Math.sin(t); return [c, s, 0, 0, -s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]; },
  rotationY(t) { const c = Math.cos(t), s = Math.sin(t); return [c, 0, -s, 0, 0, 1, 0, 0, s, 0, c, 0, 0, 0, 0, 1]; },
  rotationX(t) { const c = Math.cos(t), s = Math.sin(t); return [1, 0, 0, 0, 0, c, s, 0, 0, -s, c, 0, 0, 0, 0, 1]; },
  scale: (x, y, z = 1) => [x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, 1],
  translation: (x, y, z = 0) => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1],
  // Matrix4x4.CreateLookAt(右手系、カメラは -Z を見る)
  lookAt(eye, target, up) {
    const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]], dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    const norm = (a) => { const l = Math.hypot(...a); return a.map((v) => v / l); };
    const z = norm(sub(eye, target)), x = norm(cross(up, z)), y = cross(z, x);
    return [x[0], y[0], z[0], 0, x[1], y[1], z[1], 0, x[2], y[2], z[2], 0, -dot(x, eye), -dot(y, eye), -dot(z, eye), 1];
  },
  // Day 16 の Camera.CreatePerspective(OpenGL の規約。NDC の z は -1〜1)
  perspectiveGL(fovY, aspect, n, f) {
    const ys = 1 / Math.tan(fovY * 0.5), xs = ys / aspect;
    return [xs, 0, 0, 0, 0, ys, 0, 0, 0, 0, (f + n) / (n - f), -1, 0, 0, (2 * f * n) / (n - f), 0];
  },
  // Matrix4x4.CreatePerspectiveFieldOfView(DirectX の規約。NDC の z は 0〜1)
  perspectiveDX(fovY, aspect, n, f) {
    const ys = 1 / Math.tan(fovY * 0.5), xs = ys / aspect;
    return [xs, 0, 0, 0, 0, ys, 0, 0, 0, 0, f / (n - f), -1, 0, 0, (n * f) / (n - f), 0];
  },
  // Day 16 の Camera.CreateOrthographic(OpenGL の規約)
  orthographicGL(w, h, n, f) { return [2 / w, 0, 0, 0, 0, 2 / h, 0, 0, 0, 0, 2 / (n - f), 0, 0, 0, (f + n) / (n - f), 1]; },
  // 行ベクトル規約の v * M
  mulRow(v, m) { const o = [0, 0, 0, 0]; for (let c = 0; c < 4; c++) o[c] = v[0] * m[c] + v[1] * m[4 + c] + v[2] * m[8 + c] + v[3] * m[12 + c]; return o; },
  transpose(m) { const o = []; for (let r = 0; r < 4; r++) for (let c = 0; c < 4; c++) o[r * 4 + c] = m[c * 4 + r]; return o; },
  // glUniformMatrix4fv に float 16 個を渡したとき、GL(列ベクトル規約)が見る行列 G を行優先で返す。
  // GL はメモリを列優先として読むので、transpose が false なら G[r][c] = data[c * 4 + r](= 転置)
  asSeenByGL(data, transpose) { return transpose ? data.slice() : Mat.transpose(data); },
  // 列ベクトル規約の G * v(GLSL の uTransform * vec4(...))
  mulColumn(G, v) { const o = [0, 0, 0, 0]; for (let r = 0; r < 4; r++) o[r] = G[r * 4] * v[0] + G[r * 4 + 1] * v[1] + G[r * 4 + 2] * v[2] + G[r * 4 + 3] * v[3]; return o; },
};

// ---------------- 描き先(色と深度) ----------------
function createTarget(w, h) {
  return { w, h, color: new Uint8ClampedArray(w * h * 4), depth: new Float32Array(w * h), fragments: 0, triangles: 0, culled: 0 };
}
function clear(t, rgba, depth = 1) {
  const c = rgba.map((v) => Math.round(Math.min(1, Math.max(0, v)) * 255));
  for (let i = 0; i < t.w * t.h; i++) { t.color[i * 4] = c[0]; t.color[i * 4 + 1] = c[1]; t.color[i * 4 + 2] = c[2]; t.color[i * 4 + 3] = 255; }
  t.depth.fill(depth);
  t.fragments = 0; t.triangles = 0; t.culled = 0;
}
// GL の座標(左下が原点)で 1 画素を読む。glReadPixels と同じ向き
function readPixel(t, x, y) { const i = ((t.h - 1 - y) * t.w + x) * 4; return [t.color[i], t.color[i + 1], t.color[i + 2]]; }

// ---------------- クリップ(手前と奥の面だけ。横は画面の枠で切る) ----------------
function clipPolygon(poly) {
  for (const sgn of [1, -1]) {   // 1: z >= -w(手前)、-1: z <= w(奥)
    const out = [];
    for (let i = 0; i < poly.length; i++) {
      const a = poly[i], b = poly[(i + 1) % poly.length];
      const da = a.pos[3] + sgn * a.pos[2], db = b.pos[3] + sgn * b.pos[2];
      if (da >= 0) out.push(a);
      if ((da >= 0) !== (db >= 0)) {
        const s = da / (da - db);
        out.push({ pos: a.pos.map((v, k) => v + (b.pos[k] - v) * s), vary: a.vary.map((v, k) => v + (b.vary[k] - v) * s) });
      }
    }
    poly = out;
    if (poly.length < 3) return [];
  }
  return poly;
}

// クリップ座標 → 窓の座標(GL と同じく y は上向き、深度は 0〜1)
function toWindow(t, v) {
  const iw = 1 / v.pos[3];
  return { x: (v.pos[0] * iw + 1) * 0.5 * t.w, y: (v.pos[1] * iw + 1) * 0.5 * t.h, z: (v.pos[2] * iw + 1) * 0.5, iw, vary: v.vary };
}

// ---------------- 描く ----------------
// mesh: { vertices: [...], indices?: [...] }(indices が無ければ 3 個ずつ)
// state: { vs, fs, cull: 'none' | 'back' | 'front', frontFace: 'ccw' | 'cw', depthTest, depthWrite, polygonMode: 'fill' | 'line', blend }
function draw(t, mesh, state) {
  const idx = mesh.indices || mesh.vertices.map((_, i) => i);
  const done = mesh.vertices.map((v) => state.vs(v));   // 頂点シェーダは頂点ごとに 1 回
  for (let i = 0; i + 2 < idx.length; i += 3) {
    t.triangles++;
    const poly = clipPolygon([done[idx[i]], done[idx[i + 1]], done[idx[i + 2]]]);
    if (!poly.length) continue;
    const win = poly.map((v) => toWindow(t, v));
    // 向き(窓の座標で左回りが表)。カリングはクリップの後の多角形全体で決める
    let area = 0;
    for (let k = 0; k < win.length; k++) { const a = win[k], b = win[(k + 1) % win.length]; area += a.x * b.y - b.x * a.y; }
    const front = (area > 0) === ((state.frontFace || 'ccw') === 'ccw');
    if ((state.cull === 'back' && !front) || (state.cull === 'front' && front)) { t.culled++; continue; }
    if (state.polygonMode === 'line') {
      // ワイヤーは元の三角形の辺だけ(クリップでできた辺も描く)
      for (let k = 0; k < win.length; k++) line(t, win[k], win[(k + 1) % win.length], state);
    } else {
      for (let k = 1; k + 1 < win.length; k++) fillTriangle(t, win[0], win[k], win[k + 1], state);
    }
  }
}

function writeFragment(t, x, y, z, vary, grad, state) {
  if (x < 0 || y < 0 || x >= t.w || y >= t.h) return;
  const p = (t.h - 1 - y) * t.w + x;
  if (state.depthTest && !(z < t.depth[p])) return;
  const c = state.fs(vary, grad);
  if (!c) return;   // discard
  const i = p * 4;
  if (state.blend) {
    const a = c[3];
    t.color[i] = Math.round(c[0] * 255 * a + t.color[i] * (1 - a));
    t.color[i + 1] = Math.round(c[1] * 255 * a + t.color[i + 1] * (1 - a));
    t.color[i + 2] = Math.round(c[2] * 255 * a + t.color[i + 2] * (1 - a));
  } else {
    t.color[i] = Math.round(c[0] * 255); t.color[i + 1] = Math.round(c[1] * 255); t.color[i + 2] = Math.round(c[2] * 255);
  }
  if (state.depthTest && state.depthWrite !== false) t.depth[p] = z;
  t.fragments++;
}

// 透視補正つきの補間。重心座標 l0, l1, l2(窓の座標で求めたもの)から vary を出す
function interp(a, b, c, l0, l1, l2) {
  const w0 = l0 * a.iw, w1 = l1 * b.iw, w2 = l2 * c.iw, s = 1 / (w0 + w1 + w2);
  const n = a.vary.length, o = new Array(n);
  for (let k = 0; k < n; k++) o[k] = (a.vary[k] * w0 + b.vary[k] * w1 + c.vary[k] * w2) * s;
  return o;
}

function fillTriangle(t, a, b, c, state) {
  let area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
  if (area === 0) return;
  if (area < 0) { [b, c] = [c, b]; area = -area; }   // 左回りにそろえる(辺の向きで境界の持ち主を決めるため)
  // 2 枚の三角形が共有する辺の上の画素は、片方だけが描く(GPU の top-left 規則と同じ考え方。両方が描くと半透明が二重に濃くなる)
  const owns = (p, q) => { const dy = q.y - p.y, dx = q.x - p.x; return dy < 0 || (dy === 0 && dx > 0); };
  const own0 = owns(b, c), own1 = owns(c, a), own2 = owns(a, b);
  const inv = 1 / area;
  const minX = Math.max(0, Math.floor(Math.min(a.x, b.x, c.x))), maxX = Math.min(t.w - 1, Math.ceil(Math.max(a.x, b.x, c.x)));
  const minY = Math.max(0, Math.floor(Math.min(a.y, b.y, c.y))), maxY = Math.min(t.h - 1, Math.ceil(Math.max(a.y, b.y, c.y)));
  // 辺の関数(p → q の左側が正)。隣の三角形と同じ辺を同じ順で計算して、符号がきっちり逆になるようにする
  // (順が違うと丸めの違いで両方が負になり、すき間ができる)
  const raw = (p, q, px, py) => (q.x - p.x) * (py - p.y) - (px - p.x) * (q.y - p.y);
  const edge = (p, q, px, py) => (p.x < q.x || (p.x === q.x && p.y < q.y) ? raw(p, q, px, py) : -raw(q, p, px, py));
  // 重心座標は 3 つとも辺の関数から出す(引き算で出すと辺の上でちょうど 0 にならない)
  const bary = (px, py) => [edge(b, c, px, py) * inv, edge(c, a, px, py) * inv, edge(a, b, px, py) * inv];
  for (let y = minY; y <= maxY; y++) {
    for (let x = minX; x <= maxX; x++) {
      const px = x + 0.5, py = y + 0.5;   // 画素の中心で判定する
      const [l0, l1, l2] = bary(px, py);
      if (l0 < 0 || l1 < 0 || l2 < 0) continue;
      if ((l0 === 0 && !own0) || (l1 === 0 && !own1) || (l2 === 0 && !own2)) continue;
      const z = l0 * a.z + l1 * b.z + l2 * c.z;
      const vary = interp(a, b, c, l0, l1, l2);
      // 隣の画素での値(GPU は 2x2 の画素をまとめて動かし、その差からミップマップの段を決める)
      const grad = () => { const bx = bary(px + 1, py), by = bary(px, py + 1); return [interp(a, b, c, ...bx), interp(a, b, c, ...by)]; };
      writeFragment(t, x, y, z, vary, grad, state);
    }
  }
}

function line(t, a, b, state) {
  const n = Math.max(1, Math.ceil(Math.max(Math.abs(b.x - a.x), Math.abs(b.y - a.y))));
  for (let i = 0; i <= n; i++) {
    const s = i / n;
    const w0 = (1 - s) * a.iw, w1 = s * b.iw, k = 1 / (w0 + w1);
    const vary = a.vary.map((v, j) => (v * w0 + b.vary[j] * w1) * k);
    const g = [vary, vary];
    writeFragment(t, Math.floor(a.x + (b.x - a.x) * s), Math.floor(a.y + (b.y - a.y) * s), a.z + (b.z - a.z) * s - 1e-5, vary, () => g, state);
  }
}

// 描き先を canvas に貼る。縦横比を保って真ん中に置き、置いた位置を返す
const offscreen = new WeakMap();
function present(t, ctx, w, h, smooth = true) {
  let off = offscreen.get(t);
  if (!off) { off = document.createElement('canvas'); off.width = t.w; off.height = t.h; offscreen.set(t, off); }
  const oc = off.getContext('2d'), img = oc.createImageData(t.w, t.h);
  img.data.set(t.color);
  oc.putImageData(img, 0, 0);
  const s = Math.min(w / t.w, h / t.h), dw = t.w * s, dh = t.h * s, dx = (w - dw) / 2, dy = (h - dh) / 2;
  ctx.imageSmoothingEnabled = smooth;
  ctx.drawImage(off, dx, dy, dw, dh);
  return { x: dx, y: dy, s };
}

// ---------------- テクスチャ ----------------
// rgba: 幅 w・高さ h の Uint8 の並び。行 0 を UV の v = 0(一番下)として持つ(GL と同じ)
function createTexture(w, h, rgba) {
  const levels = [{ w, h, d: rgba }];
  // ミップマップ(GenerateMipmap と同じく、2x2 を平均して半分にしていく)
  while (levels[levels.length - 1].w > 1 || levels[levels.length - 1].h > 1) {
    const p = levels[levels.length - 1], nw = Math.max(1, p.w >> 1), nh = Math.max(1, p.h >> 1), d = new Uint8ClampedArray(nw * nh * 4);
    for (let y = 0; y < nh; y++) for (let x = 0; x < nw; x++) for (let k = 0; k < 4; k++) {
      let s = 0;
      for (const [dx, dy] of [[0, 0], [1, 0], [0, 1], [1, 1]]) s += p.d[((Math.min(p.h - 1, y * 2 + dy)) * p.w + Math.min(p.w - 1, x * 2 + dx)) * 4 + k];
      d[(y * nw + x) * 4 + k] = s / 4;
    }
    levels.push({ w: nw, h: nh, d });
  }
  return { w, h, levels, minFilter: 'LinearMipmapLinear', magFilter: 'Linear', wrapS: 'Repeat', wrapT: 'Repeat' };
}
// 画像ファイルと同じ「行 0 が一番上」の並びを、UV の向き(行 0 が一番下)へ上下反転する(stbi_set_flip_vertically_on_load)
function flipRows(w, h, rgba) {
  const o = new Uint8ClampedArray(rgba.length);
  for (let y = 0; y < h; y++) o.set(rgba.subarray((h - 1 - y) * w * 4, (h - y) * w * 4), y * w * 4);
  return o;
}
function wrap(i, n, mode) {
  if (mode === 'Repeat') return ((i % n) + n) % n;
  if (mode === 'MirroredRepeat') { const p = ((i % (2 * n)) + 2 * n) % (2 * n); return p < n ? p : 2 * n - 1 - p; }
  return Math.min(n - 1, Math.max(0, i));   // ClampToEdge
}
function texel(L, i, j, tex) { const k = (wrap(j, L.h, tex.wrapT) * L.w + wrap(i, L.w, tex.wrapS)) * 4; return [L.d[k] / 255, L.d[k + 1] / 255, L.d[k + 2] / 255, L.d[k + 3] / 255]; }
function sampleLevel(tex, lv, u, v, linear) {
  const L = tex.levels[lv];
  if (!linear) return texel(L, Math.floor(u * L.w), Math.floor(v * L.h), tex);
  // バイリニア(Day 8 で自作したもの)。テクセルの中心は +0.5 にある
  const x = u * L.w - 0.5, y = v * L.h - 0.5, i = Math.floor(x), j = Math.floor(y), fx = x - i, fy = y - j;
  const a = texel(L, i, j, tex), b = texel(L, i + 1, j, tex), c = texel(L, i, j + 1, tex), d = texel(L, i + 1, j + 1, tex);
  return a.map((_, k) => (a[k] * (1 - fx) + b[k] * fx) * (1 - fy) + (c[k] * (1 - fx) + d[k] * fx) * fy);
}
const lerp4 = (a, b, s) => a.map((v, k) => v + (b[k] - v) * s);
// texture(sampler, uv)。grad は [ddx の uv, ddy の uv](無ければ拡大扱い)。戻り値に使った段(lod)も付ける
function sample(tex, u, v, grad) {
  let lod = 0;
  if (grad) {
    const dux = (grad[0][0] - u) * tex.w, dvx = (grad[0][1] - v) * tex.h, duy = (grad[1][0] - u) * tex.w, dvy = (grad[1][1] - v) * tex.h;
    lod = Math.log2(Math.max(Math.hypot(dux, dvx), Math.hypot(duy, dvy)) || 1e-9);
  }
  const top = tex.levels.length - 1;
  let c;
  if (lod <= 0) c = sampleLevel(tex, 0, u, v, tex.magFilter === 'Linear');   // 拡大: MagFilter(ミップマップは使えない)
  else {
    const f = tex.minFilter, lin = f.startsWith('Linear');
    if (f === 'Nearest' || f === 'Linear') c = sampleLevel(tex, 0, u, v, lin);   // ミップマップ無しの縮小は原寸から間引く
    else if (f.endsWith('MipmapNearest')) c = sampleLevel(tex, Math.min(top, Math.round(lod)), u, v, lin);
    else { const l0 = Math.min(top, Math.floor(lod)), l1 = Math.min(top, l0 + 1); c = lerp4(sampleLevel(tex, l0, u, v, lin), sampleLevel(tex, l1, u, v, lin), lod - Math.floor(lod)); }
  }
  c.lod = lod;
  return c;
}

// assets/textures/uv-test.png(256x256)と同じ絵を作る。並びは画像ファイルと同じ「行 0 が一番上」。
// 32px ごとの罫線(30)、画像の左上(ファイルの先頭)に白い四角(1〜23px)、明暗の市松(66 / 115)、右へ赤・下へ緑が 100 ずつ増える
function uvTest() {
  const n = 256, d = new Uint8ClampedArray(n * n * 4);
  for (let y = 0; y < n; y++) for (let x = 0; x < n; x++) {
    const i = (y * n + x) * 4;
    let c;
    if (x % 32 === 0 || y % 32 === 0) c = [30, 30, 30];
    else if (x <= 23 && y <= 23) c = [255, 255, 255];
    else { const b = ((x >> 5) + (y >> 5)) % 2 ? 115 : 66; c = [b + Math.floor((x * 100) / 255), b + Math.floor((y * 100) / 255), b]; }
    d[i] = c[0]; d[i + 1] = c[1]; d[i + 2] = c[2]; d[i + 3] = 255;
  }
  return { w: n, h: n, data: d };
}

// ---------------- Day 16 のシーン(Day 16〜18 の実験台が使う) ----------------
// reference/Day16 の Primitives.cs(四角形と 24 頂点の立方体)、Program.cs の床と6個の立方体、
// OrbitCameraController.cs(球面座標)、textured.vert / textured.frag
const DemoScene = (() => {
  const v = (p, uv, c) => ({ pos: p, uv, color: c });
  const QUAD = { vertices: [v([-0.5, -0.5, 0], [0, 0], [1, 1, 1, 1]), v([0.5, -0.5, 0], [1, 0], [1, 1, 1, 1]), v([0.5, 0.5, 0], [1, 1], [1, 1, 1, 1]), v([-0.5, 0.5, 0], [0, 1], [1, 1, 1, 1])], indices: [0, 1, 2, 2, 3, 0] };
  // 6 面とも外から見て反時計回り
  const CUBE = (() => {
    const h = 0.5, vs = [], ix = [];
    const face = (bl, br, tr, tl, c) => { const b = vs.length; vs.push(v(bl, [0, 0], c), v(br, [1, 0], c), v(tr, [1, 1], c), v(tl, [0, 1], c)); ix.push(b, b + 1, b + 2, b + 2, b + 3, b); };
    face([-h, -h, h], [h, -h, h], [h, h, h], [-h, h, h], [1.00, 0.55, 0.55, 1]);     // +Z 赤
    face([h, -h, -h], [-h, -h, -h], [-h, h, -h], [h, h, -h], [0.55, 1.00, 0.65, 1]); // -Z 緑
    face([h, -h, h], [h, -h, -h], [h, h, -h], [h, h, h], [0.60, 0.70, 1.00, 1]);     // +X 青
    face([-h, -h, -h], [-h, -h, h], [-h, h, h], [-h, h, -h], [1.00, 0.95, 0.55, 1]); // -X 黄
    face([-h, h, h], [h, h, h], [h, h, -h], [-h, h, -h], [1.00, 1.00, 1.00, 1]);     // +Y 白
    face([-h, -h, -h], [h, -h, -h], [h, -h, h], [-h, -h, h], [0.70, 0.70, 0.75, 1]); // -Y 灰
    return { vertices: vs, indices: ix };
  })();
  // Program.Cubes: 位置・大きさ・自転の速さ
  const CUBES = [[[0, 0.25, 0], 1.5, 0.8], [[3, 0, 3], 1, 0], [[3, 1, 3], 1, 0.5], [[-3, 0, 3], 1, -0.4], [[3, 0, -3], 1, 0.3], [[-3, 0, -3], 1, 0]];
  const cubeMat = { tint: [1, 1, 1, 1], uv: [1, 1] }, floorMat = { tint: [0.45, 0.50, 0.60, 1], uv: [10, 10] };
  let tex = null;
  const texture = () => { if (!tex) { const p = uvTest(); tex = createTexture(p.w, p.h, flipRows(p.w, p.h, p.data)); } return tex; };
  // OrbitCameraController(Reset と同じ初期値)
  const createOrbit = () => ({ yaw: 0.6, pitch: 0.4, distance: 9, target: [0, 0, 0], fov: Math.PI / 3, near: 0.1, far: 100, position: [0, 0, 0], orthoHeight: 10 });
  function applyOrbit(o) {
    o.pitch = Math.min(1.5, Math.max(-1.5, o.pitch));   // 約 86 度で止める(真上で LookAt が軸を作れなくなる)
    o.distance = Math.min(40, Math.max(2, o.distance));
    const cp = Math.cos(o.pitch), t = o.target, d = o.distance;
    o.position = [t[0] + d * cp * Math.sin(o.yaw), t[1] + d * Math.sin(o.pitch), t[2] + d * cp * Math.cos(o.yaw)];
    o.orthoHeight = 2 * d * Math.tan(o.fov * 0.5);
  }
  // opts: { angle, orthographic, depthTest, cull, wire, filter: 'Linear' | 'Nearest', wrap: 'Repeat' | 'ClampToEdge' }
  function render(t, o, opts) {
    const tx = texture(), aspect = t.w / t.h;
    tx.minFilter = opts.filter === 'Nearest' ? 'NearestMipmapNearest' : 'LinearMipmapLinear'; tx.magFilter = opts.filter || 'Linear';
    tx.wrapS = tx.wrapT = opts.wrap || 'Repeat';
    const view = Mat.lookAt(o.position, o.target, [0, 1, 0]);
    const proj = opts.orthographic ? Mat.orthographicGL(o.orthoHeight * aspect, o.orthoHeight, o.near, o.far) : Mat.perspectiveGL(o.fov, aspect, o.near, o.far);
    // フレームごと: uViewProjection を 1 回だけ(行ベクトル規約なので View → Projection の順)
    const VP = Mat.asSeenByGL(Mat.mul(view, proj), false);
    const state = { depthTest: opts.depthTest !== false, cull: opts.cull === false ? 'none' : 'back', polygonMode: opts.wire ? 'line' : 'fill' };
    const drawObj = (mesh, mat, model) => {
      const Mm = Mat.asSeenByGL(model, false);
      // textured.vert: gl_Position = uViewProjection * uModel * vec4(aPosition, 1.0)
      draw(t, mesh, { ...state,
        vs: (vx) => ({ pos: Mat.mulColumn(VP, Mat.mulColumn(Mm, [...vx.pos, 1])), vary: [vx.uv[0] * mat.uv[0], vx.uv[1] * mat.uv[1], ...vx.color] }),
        fs: (q, grad) => { const c = sample(tx, q[0], q[1], grad()); return [c[0] * q[2] * mat.tint[0], c[1] * q[3] * mat.tint[1], c[2] * q[4] * mat.tint[2], 1]; } });
    };
    drawObj(QUAD, floorMat, Mat.mul(Mat.mul(Mat.scale(20, 20, 20), Mat.rotationX(-Math.PI / 2)), Mat.translation(0, -0.5, 0)));
    for (const [p, s, spin] of CUBES) drawObj(CUBE, cubeMat, Mat.mul(Mat.mul(Mat.scale(s, s, s), Mat.rotationY((opts.angle || 0) * spin)), Mat.translation(...p)));
  }
  return { QUAD, CUBE, CUBES, createOrbit, applyOrbit, render };
})();

window.SoftGL = { Mat, createTarget, clear, readPixel, draw, present, createTexture, flipRows, sample, uvTest, DemoScene };
})();
