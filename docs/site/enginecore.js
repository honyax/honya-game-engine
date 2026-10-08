// ============================================================
//  Phase 4(エンジンコア)のクラスを JS に移したもの。window.Core にまとめて置く
//  - Day 19: GameLoop.cs(固定タイムステップ+補間)
//  - Day 20: InputMap.cs・InputSnapshot.cs・InputSystem.cs・InputRecorder.cs
//  - Day 21: Handle.cs・ResourcePool.cs・ResourceManager.cs(テクスチャは名前だけの値。復号とアップロードの時間はページ側で決める)
//  - Day 22: Transform.cs・Component.cs・GameObject.cs・Scene.cs(Transform は 2D に絞り、回転は Z の角度で持つ)
//  - Day 23: Entity.cs・ComponentStore.cs・World.cs・EcsComponents.cs・EcsSystems.cs(ストアはフィールドごとの Float32Array)
//  Day 19〜24 の計画書の実験台が共有する。dotnet.js を先に読んでおく
//
//  - C# の float の計算は1回ごとに Math.fround で丸める(GameLoop は double なのでそのまま)
//  - キーは Silk.NET の Key の名前の文字列で表す('Left'、'X' など)
// ============================================================
(() => {
'use strict';
const f = Math.fround;

// ---------------- 固定タイムステップ(Day 19 の GameLoop.cs) ----------------
class GameLoop {
  constructor(fixedDeltaTime = 1.0 / 60.0) {
    this.accumulator = 0; this.fixedDeltaTime = fixedDeltaTime; this.maxStepsPerFrame = 8; this.dropExcess = true;
    this.alpha = 0; this.stepsLastFrame = 0; this.totalSteps = 0; this.simulationTime = 0; this.droppedSeconds = 0;
  }
  get lag() { return this.accumulator; }
  advance(frameSeconds, fixedUpdate) {
    if (Number.isNaN(frameSeconds) || frameSeconds < 0) frameSeconds = 0;
    this.accumulator += frameSeconds;
    this.stepsLastFrame = 0;
    while (this.accumulator >= this.fixedDeltaTime && this.stepsLastFrame < this.maxStepsPerFrame) {
      fixedUpdate(f(this.fixedDeltaTime));
      this.accumulator -= this.fixedDeltaTime;
      this.simulationTime += this.fixedDeltaTime;
      this.totalSteps++;
      this.stepsLastFrame++;
    }
    if (this.accumulator >= this.fixedDeltaTime && this.dropExcess) {
      // 1ステップ未満の端数だけ残して捨てる
      const keep = this.accumulator % this.fixedDeltaTime;
      this.droppedSeconds += this.accumulator - keep;
      this.accumulator = keep;
    }
    this.alpha = Math.min(Math.max(this.accumulator / this.fixedDeltaTime, 0), 1);
  }
  reset() { this.accumulator = 0; this.alpha = 0; this.stepsLastFrame = 0; this.droppedSeconds = 0; }
}

// ---------------- 入力(Day 20 の InputMap.cs・InputSnapshot.cs・InputSystem.cs・InputRecorder.cs) ----------------
// GameAction は [Flags] の uint。JS ではビット演算の結果を >>> 0 で符号なしに戻す
const GameAction = { None: 0, MoveLeft: 1 << 0, MoveRight: 1 << 1, MoveUp: 1 << 2, MoveDown: 1 << 3, Dash: 1 << 4 };
const actionNames = (a) => Object.entries(GameAction).filter(([k, v]) => v && (a & v)).map(([k]) => k).join('|') || 'None';

class InputMap {
  constructor() { this.bindings = []; }
  bind(key, action) { this.bindings.push([key, action]); }
  // 線形探索。同じキーに複数のアクションが付いていれば全部 OR する
  resolve(key) { let r = 0; for (const [k, a] of this.bindings) if (k === key) r |= a; return r >>> 0; }
  static createDefault() {
    const m = new InputMap();
    m.bind('Left', GameAction.MoveLeft); m.bind('Right', GameAction.MoveRight);
    m.bind('Up', GameAction.MoveUp); m.bind('Down', GameAction.MoveDown);
    m.bind('X', GameAction.Dash);
    return m;
  }
}

class InputSnapshot {
  constructor(held = 0, pressed = 0, released = 0, mousePosition = [0, 0], mouseDelta = [0, 0], scroll = 0) {
    this.held = held >>> 0; this.pressed = pressed >>> 0; this.released = released >>> 0;
    this.mousePosition = mousePosition; this.mouseDelta = mouseDelta; this.scroll = f(scroll);
  }
  isHeld(a) { return (this.held & a) !== 0; }
  wasPressed(a) { return (this.pressed & a) !== 0; }
  wasReleased(a) { return (this.released & a) !== 0; }
  // -1〜1。斜めは正規化。長さ0の Normalize は NaN になるので無入力は Zero を返す
  get moveAxis() {
    const x = f((this.isHeld(GameAction.MoveRight) ? 1 : 0) - (this.isHeld(GameAction.MoveLeft) ? 1 : 0));
    const y = f((this.isHeld(GameAction.MoveDown) ? 1 : 0) - (this.isHeld(GameAction.MoveUp) ? 1 : 0));
    if (x === 0 && y === 0) return [0, 0];
    const len = f(Math.sqrt(f(f(x * x) + f(y * y))));
    return [f(x / len), f(y / len)];
  }
  static get empty() { return new InputSnapshot(); }
}

class InputSystem {
  constructor(map) {
    this.map = map; this.heldNow = 0; this.pendingPressed = 0; this.pendingReleased = 0;
    this.mousePosition = [0, 0]; this.mouseDeltaAccumulator = [0, 0]; this.lastMousePosition = [0, 0]; this.scrollAccumulator = 0; this.hasMousePosition = false;
    this.current = InputSnapshot.empty;
    // このページだけの切り替え(reference の書き方を外すと何が起きるかを見る)
    this.options = { filterRepeat: true, heldIncludesPressed: true, consumePressed: true };
  }
  // ステップの境界で、溜めたイベントを1枚の値に畳む
  beginStep() {
    const o = this.options;
    this.current = new InputSnapshot(o.heldIncludesPressed ? this.heldNow | this.pendingPressed : this.heldNow, this.pendingPressed, this.pendingReleased,
      [...this.mousePosition], [...this.mouseDeltaAccumulator], this.scrollAccumulator);
    if (o.consumePressed) this.pendingPressed = 0;
    this.pendingReleased = 0;
    this.mouseDeltaAccumulator = [0, 0]; this.scrollAccumulator = 0;
    return this.current;
  }
  setCurrent(s) { this.current = s; }
  clear() {
    this.heldNow = 0; this.pendingPressed = 0; this.pendingReleased = 0;
    this.mouseDeltaAccumulator = [0, 0]; this.scrollAccumulator = 0; this.current = InputSnapshot.empty;
  }
  keyDown(key) {
    const action = this.map.resolve(key);
    if (action === 0) return;
    // オートリピートを弾く: すでに押されているぶんは除いてから立てる
    const newlyPressed = this.options.filterRepeat ? (action & ~this.heldNow) >>> 0 : action;
    this.pendingPressed = (this.pendingPressed | newlyPressed) >>> 0;
    this.heldNow = (this.heldNow | action) >>> 0;
  }
  keyUp(key) {
    const action = this.map.resolve(key);
    if (action === 0) return;
    this.pendingReleased = (this.pendingReleased | (action & this.heldNow)) >>> 0;
    this.heldNow = (this.heldNow & ~action) >>> 0;
  }
  mouseMove(p) {
    p = [f(p[0]), f(p[1])];
    if (this.hasMousePosition) {
      this.mouseDeltaAccumulator = [f(this.mouseDeltaAccumulator[0] + f(p[0] - this.lastMousePosition[0])), f(this.mouseDeltaAccumulator[1] + f(p[1] - this.lastMousePosition[1]))];
    } else this.hasMousePosition = true;
    this.lastMousePosition = p; this.mousePosition = p;
  }
  scroll(y) { this.scrollAccumulator = f(this.scrollAccumulator + f(y)); }
}

class InputRecorder {
  constructor() { this.frames = []; this.playHead = 0; this.mode = 'Off'; this.fixedDeltaTime = 0; }
  get count() { return this.frames.length; }
  startRecording(fixedDeltaTime) { this.frames = []; this.playHead = 0; this.fixedDeltaTime = fixedDeltaTime; this.mode = 'Recording'; }
  stopRecording() { this.mode = 'Off'; }
  startReplaying() { if (this.frames.length === 0) return false; this.playHead = 0; this.mode = 'Replaying'; return true; }
  stop() { this.mode = 'Off'; this.playHead = 0; }
  record(s) { if (this.mode === 'Recording') this.frames.push(s); }
  tryReplay() {
    if (this.mode !== 'Replaying' || this.playHead >= this.frames.length) return null;
    return this.frames[this.playHead++];
  }
}

// ---------------- リソース管理(Day 21 の Handle.cs・ResourcePool.cs・ResourceManager.cs) ----------------
// ハンドルは 32 ビットの整数: 上位 8 ビットが世代、下位 24 ビットが添字。世代 0 は「無効」
const INDEX_BITS = 24, INDEX_MASK = (1 << INDEX_BITS) - 1, MAX_GENERATION = (1 << (32 - INDEX_BITS)) - 1;
const Handle = {
  make: (index, generation) => (((generation << INDEX_BITS) | (index & INDEX_MASK)) >>> 0),
  index: (h) => h & INDEX_MASK,
  generation: (h) => h >>> INDEX_BITS,
  isValid: (h) => (h >>> INDEX_BITS) !== 0,
  none: 0,
  toString: (h) => ((h >>> INDEX_BITS) !== 0 ? `#${h & INDEX_MASK}.g${h >>> INDEX_BITS}` : '#none'),
};

// 配列 + 空きリスト + 世代 + 参照カウント。中身の種類は知らない
class ResourcePool {
  constructor() { this.slots = []; this.freeHead = -1; this.aliveCount = 0; }
  get slotCount() { return this.slots.length; }
  add(value) {
    let index;
    if (this.freeHead >= 0) { index = this.freeHead; this.freeHead = this.slots[index].nextFree; }
    else { index = this.slots.length; this.slots.push({ value: null, generation: 0, refCount: 0, nextFree: -1 }); }
    const s = this.slots[index];
    // 世代は解放時に進める。初回だけ 0(無効)を避けて 1 から
    if (s.generation === 0) s.generation = 1;
    s.value = value; s.refCount = 1; s.nextFree = -1;
    this.aliveCount++;
    return Handle.make(index, s.generation);
  }
  indexOf(h) {
    const i = Handle.index(h), s = this.slots[i];
    return Handle.isValid(h) && s && s.generation === Handle.generation(h) && s.value !== null ? i : -1;
  }
  tryGet(h) { const i = this.indexOf(h); return i < 0 ? null : this.slots[i].value; }
  isAlive(h) { return this.indexOf(h) >= 0; }
  refCountOf(h) { const i = this.indexOf(h); return i < 0 ? 0 : this.slots[i].refCount; }
  retain(h) { const i = this.indexOf(h); if (i < 0) return false; this.slots[i].refCount++; return true; }
  // 参照カウントが 0 になったときだけスロットを空け、世代を進める。戻り値は外したもの(まだ残っていれば undefined)
  release(h) {
    const i = this.indexOf(h); if (i < 0) return undefined;
    const s = this.slots[i];
    if (--s.refCount > 0) return undefined;
    const removed = s.value;
    s.value = null;
    s.generation = s.generation + 1 > MAX_GENERATION ? 1 : s.generation + 1;   // この1行で古いハンドルが全部死ぬ
    s.nextFree = this.freeHead; this.freeHead = i;
    this.aliveCount--;
    return removed;
  }
  replace(h, value) { const i = this.indexOf(h); if (i < 0) return undefined; const prev = this.slots[i].value; this.slots[i].value = value; return prev; }
}

// 窓口。パス → ハンドル、重複排除、非同期ロード(復号はワーカー、アップロードは Update で絞る)。
// テクスチャは { name, mip, placeholder? } のただの値。復号の完了は呼び出し側が decoded() で知らせる
class ResourceManager {
  constructor() {
    this.textures = new ResourcePool();
    this.byKey = new Map(); this.keyOf = new Map();
    this.queue = []; this.pending = 0; this.cacheHits = 0;
    this.maxUploadsPerFrame = 1;
    this.placeholder = { name: 'placeholder', placeholder: true };
    this.onDecodeRequested = () => {};   // (handle, path, mip) を受け取って、裏で「復号」を始める
    this.onUploaded = () => {};
  }
  static key(path, mip) { return mip ? path : path + '|nomip'; }   // 読み込み設定もキーに含める
  get textureCount() { return this.textures.aliveCount; }
  tryReuse(key) {
    const h = this.byKey.get(key);
    if (h !== undefined && this.textures.isAlive(h)) { this.textures.retain(h); this.cacheHits++; return h; }
    return null;
  }
  register(key, h) { this.byKey.set(key, h); this.keyOf.set(h, key); }
  loadTexture(path, mip = true) {
    const key = ResourceManager.key(path, mip), hit = this.tryReuse(key);
    if (hit !== null) return hit;
    const h = this.textures.add({ name: path, mip });
    this.register(key, h);
    return h;
  }
  // 要求した時点で辞書に入れる(読み込み中に同じパスが来ても2重に読まない)。仮の絵を指すハンドルをすぐ返す
  loadTextureAsync(path, mip = true) {
    const key = ResourceManager.key(path, mip), hit = this.tryReuse(key);
    if (hit !== null) return hit;
    const h = this.textures.add(this.placeholder);
    this.register(key, h);
    this.pending++;
    this.onDecodeRequested(h, path, mip);
    return h;
  }
  decoded(h, path, mip) { this.queue.push({ h, path, mip }); }
  // 描画スレッドで毎フレーム。アップロードは maxUploadsPerFrame 枚まで。返り値はこのフレームで上げた枚数
  update() {
    let uploaded = 0;
    for (; uploaded < this.maxUploadsPerFrame; uploaded++) {
      const job = this.queue.shift();
      if (!job) break;
      this.pending--;
      // 読んでいる間に解放されていたら捨てる(世代が違うので isAlive が false)
      if (!this.textures.isAlive(job.h)) { uploaded--; continue; }
      this.textures.replace(job.h, { name: job.path, mip: job.mip });
      this.onUploaded(job.h);
    }
    return uploaded;
  }
  getTexture(h) { return this.textures.tryGet(h) ?? this.placeholder; }
  isReady(h) { const t = this.textures.tryGet(h); return !!t && !t.placeholder; }
  retain(h) { return this.textures.retain(h); }
  release(h) {
    const removed = this.textures.release(h);
    if (removed === undefined) return false;
    const key = this.keyOf.get(h);
    if (key !== undefined) { this.keyOf.delete(h); this.byKey.delete(key); }
    return true;
  }
  refCountOf(h) { return this.textures.refCountOf(h); }
}

// ---------------- GameObject + Component(Day 22 の Transform.cs・Component.cs・GameObject.cs・Scene.cs) ----------------
// Transform は 2D に絞る: 位置 (x, y)、Z 回転(角度で持つ。reference はクォータニオン)、スケール。
// 行列は行ベクトル規約(v' = v * M)の 2D アフィン [a, b, c, d, tx, ty]。ワールド = ローカル * 親のワールド
const affine = (sx, sy, rot, tx, ty) => { const c = Math.cos(rot), s = Math.sin(rot); return [sx * c, sx * s, -sy * s, sy * c, tx, ty]; };
const mul = (m, p) => [m[0] * p[0] + m[1] * p[2], m[0] * p[1] + m[1] * p[3], m[2] * p[0] + m[3] * p[2], m[2] * p[1] + m[3] * p[3], m[4] * p[0] + m[5] * p[2] + p[4], m[4] * p[1] + m[5] * p[3] + p[5]];
const Stats = { recomputed: 0, marked: 0, earlyReturn: true };   // ワールド行列を計算し直した回数と、MarkDirty が歩いた数(このページで数える)
class Transform {
  constructor(gameObject) {
    this.gameObject = gameObject; this.children = []; this.parent = null;
    this.pos = [0, 0]; this.rot = 0; this.scale = [1, 1];
    this.prevPos = [0, 0]; this.prevRot = 0;
    this.world = [1, 0, 0, 1, 0, 0]; this.dirty = true;
  }
  get localPosition() { return this.pos; }
  set localPosition(p) { this.pos = [p[0], p[1]]; this.markDirty(); }
  get localRotationZ() { return this.rot; }
  setLocalRotationZ(r) { this.rot = r; this.markDirty(); }
  set localScale(s) { this.scale = [s[0], s[1]]; this.markDirty(); }
  // 遅延評価: 読まれたときに初めて計算する
  get localToWorld() {
    if (this.dirty) {
      const local = affine(this.scale[0], this.scale[1], this.rot, this.pos[0], this.pos[1]);
      this.world = this.parent ? mul(local, this.parent.localToWorld) : local;
      this.dirty = false; Stats.recomputed++;
    }
    return this.world;
  }
  get worldPosition() { const m = this.localToWorld; return [m[4], m[5]]; }
  setParent(parent) {
    if (parent === this.parent) return;
    for (let a = parent; a; a = a.parent) if (a === this) throw new Error(`${this.gameObject.name} を自分の子孫の下に置こうとしています`);
    if (this.parent) this.parent.children.splice(this.parent.children.indexOf(this), 1);
    this.parent = parent;
    if (parent) parent.children.push(this);
    this.markDirty();
  }
  snapshot() { this.prevPos = [...this.pos]; this.prevRot = this.rot; }
  interpolatedLocalToWorld(alpha) {
    const p = [this.prevPos[0] + (this.pos[0] - this.prevPos[0]) * alpha, this.prevPos[1] + (this.pos[1] - this.prevPos[1]) * alpha];
    const local = affine(this.scale[0], this.scale[1], this.prevRot + (this.rot - this.prevRot) * alpha, p[0], p[1]);
    return this.parent ? mul(local, this.parent.interpolatedLocalToWorld(alpha)) : local;
  }
  interpolatedWorldRotationZ(alpha) {
    const a = this.prevRot + (this.rot - this.prevRot) * alpha;
    return this.parent ? a + this.parent.interpolatedWorldRotationZ(alpha) : a;
  }
  // すでに印が付いていたら子をたどらない
  markDirty() {
    if (this.dirty && Stats.earlyReturn) return;
    this.dirty = true; Stats.marked++;
    for (const c of this.children) c.markDirty();
  }
}

class Component {
  constructor() { this.gameObject = null; this._enabled = true; }
  get transform() { return this.gameObject.transform; }
  get enabled() { return this._enabled; }
  set enabled(v) {
    if (this._enabled === v) return;
    this._enabled = v;
    if (this.gameObject.activeInHierarchy) { if (v) this.onEnable(); else this.onDisable(); }
  }
  awake() {} start() {} onEnable() {} onDisable() {} fixedUpdate(dt) {} onDestroy() {}
}

class GameObject {
  constructor(scene, name) { this.scene = scene; this.name = name; this.transform = new Transform(this); this.components = []; this._activeSelf = true; this.isDestroyed = false; }
  get activeSelf() { return this._activeSelf; }
  get activeInHierarchy() {
    if (!this._activeSelf) return false;
    for (let t = this.transform.parent; t; t = t.parent) if (!t.gameObject._activeSelf) return false;
    return true;
  }
  // Awake は AddComponent の中で走る(init で値を入れるのはそのあと)
  addComponent(Type, init) {
    const c = new Type(); c.gameObject = this;
    this.components.push(c);
    this.scene.registerComponent(c);
    if (init) Object.assign(c, init);
    return c;
  }
  getComponent(Type) { for (const c of this.components) if (c instanceof Type) return c; return null; }
  setActive(active) {
    if (this._activeSelf === active) return;
    const was = this.activeInHierarchy; this._activeSelf = active; const now = this.activeInHierarchy;
    if (was !== now) this.notifyActiveChanged(now);
  }
  notifyActiveChanged(active) {
    for (const c of this.components) { if (!c.enabled) continue; if (active) c.onEnable(); else c.onDisable(); }
    for (const ch of this.transform.children) if (ch.gameObject._activeSelf) ch.gameObject.notifyActiveChanged(active);
  }
}

class Scene {
  constructor() { this.gameObjects = []; this.pendingStart = []; this.pendingDestroy = []; this.componentCount = 0; this.input = null; this.bounds = [960, 640]; }
  createGameObject(name, parent = null) { const g = new GameObject(this, name); g.transform.setParent(parent); this.gameObjects.push(g); return g; }
  // その場では消さず、予約するだけ
  destroy(g) {
    if (g.isDestroyed) return;
    g.isDestroyed = true; this.pendingDestroy.push(g);
    for (const ch of g.transform.children) this.destroy(ch.gameObject);
  }
  registerComponent(c) {
    this.componentCount++;
    c.awake();
    if (c.enabled && c.gameObject.activeInHierarchy) c.onEnable();
    this.pendingStart.push(c);
  }
  // 4 段階: Transform の控え → 溜まった Start → 更新 → 予約された破棄
  fixedUpdate(dt) {
    for (const g of this.gameObjects) g.transform.snapshot();
    const n = this.pendingStart.length;
    for (let i = 0; i < n; i++) { const c = this.pendingStart[i]; if (!c.gameObject.isDestroyed) c.start(); }
    this.pendingStart.splice(0, n);
    const count = this.gameObjects.length;
    for (let i = 0; i < count; i++) {
      const g = this.gameObjects[i];
      if (g.isDestroyed || !g.activeInHierarchy) continue;
      for (const c of g.components) if (c.enabled) c.fixedUpdate(dt);
    }
    this.flushDestroy();
  }
  flushDestroy() {
    if (this.pendingDestroy.length === 0) return;
    for (const g of this.pendingDestroy) {
      for (const c of g.components) { if (c.enabled && g.activeSelf) c.onDisable(); c.onDestroy(); }
      this.componentCount -= g.components.length;
      g.transform.setParent(null);
    }
    this.gameObjects = this.gameObjects.filter((g) => !g.isDestroyed);   // まとめて1回で消す(RemoveAll)
    this.pendingStart = this.pendingStart.filter((c) => !c.gameObject.isDestroyed);
    this.pendingDestroy = [];
  }
  clear() {
    for (const g of this.gameObjects) { g.isDestroyed = true; for (const c of g.components) c.onDestroy(); }
    this.gameObjects = []; this.pendingStart = []; this.pendingDestroy = []; this.componentCount = 0;
  }
}

// ---------------- ECS(Day 23 の Entity.cs・ComponentStore.cs・World.cs・EcsComponents.cs・EcsSystems.cs) ----------------
// エンティティは Handle と同じ 32 ビット(世代 8 + 添字 24)。
// ComponentStore は sparse set: 実体(フィールドごとの Float32Array)と denseToEntity は隙間なく詰め、entityToDense で引く
class ComponentStore {
  constructor(name, fields) {
    this.name = name; this.fields = fields; this.count = 0;
    this.cap = 64; this.cols = {}; for (const k of fields) this.cols[k] = new Float32Array(this.cap);
    this.denseToEntity = new Int32Array(this.cap); this.entityToDense = new Int32Array(0);
  }
  ensureEntity(e) {
    if (e < this.entityToDense.length) return;
    let size = Math.max(64, this.entityToDense.length); while (size <= e) size *= 2;
    const n = new Int32Array(size).fill(-1); n.set(this.entityToDense); this.entityToDense = n;
  }
  add(e, value) {
    this.ensureEntity(e);
    let d = this.entityToDense[e];
    if (d < 0) {
      if (this.count === this.cap) {
        this.cap *= 2;
        for (const k of this.fields) { const a = new Float32Array(this.cap); a.set(this.cols[k]); this.cols[k] = a; }
        const de = new Int32Array(this.cap); de.set(this.denseToEntity); this.denseToEntity = de;
      }
      d = this.count++; this.denseToEntity[d] = e; this.entityToDense[e] = d;
    }
    for (const k of this.fields) this.cols[k][d] = value[k] ?? 0;
  }
  has(e) { return e < this.entityToDense.length && this.entityToDense[e] >= 0; }
  denseIndexOf(e) { return e < this.entityToDense.length ? this.entityToDense[e] : -1; }
  get(e) { const d = this.entityToDense[e], o = {}; for (const k of this.fields) o[k] = this.cols[k][d]; return o; }
  set(e, k, v) { this.cols[k][this.entityToDense[e]] = v; }
  // 末尾と入れ替えて縮める(O(1)。並び順は変わる)
  remove(e) {
    if (!this.has(e)) return false;
    const d = this.entityToDense[e], last = this.count - 1;
    if (d !== last) {
      for (const k of this.fields) this.cols[k][d] = this.cols[k][last];
      const moved = this.denseToEntity[last]; this.denseToEntity[d] = moved; this.entityToDense[moved] = d;
    }
    this.entityToDense[e] = -1; this.count--;
    return true;
  }
  clear() { this.entityToDense.fill(-1); this.count = 0; }
}

class World {
  constructor() { this.versions = []; this.free = []; this.nextIndex = 0; this.stores = new Map(); this.aliveCount = 0; }
  createEntity() {
    const index = this.free.length ? this.free.pop() : this.nextIndex++;
    if (!this.versions[index]) this.versions[index] = 1;
    this.aliveCount++;
    return Handle.make(index, this.versions[index]);
  }
  isAlive(e) { const i = Handle.index(e); return Handle.isValid(e) && i < this.nextIndex && this.versions[i] === Handle.generation(e); }
  destroyEntity(e) {
    if (!this.isAlive(e)) return false;
    const i = Handle.index(e);
    for (const s of this.stores.values()) s.remove(i);   // 全ストアが同じ入れ替えをする
    this.versions[i] = this.versions[i] + 1 > MAX_GENERATION ? 1 : this.versions[i] + 1;
    this.free.push(i); this.aliveCount--;
    return true;
  }
  // 種類ごとのストア。schema は { 名前: [フィールド...] }
  store(name) {
    let s = this.stores.get(name);
    if (!s) { s = new ComponentStore(name, World.schema[name]); this.stores.set(name, s); }
    return s;
  }
  add(e, name, value = {}) { if (!this.isAlive(e)) throw new Error(`${Handle.toString(e)} はもう生きていません`); this.store(name).add(Handle.index(e), value); }
  has(e, name) { return this.isAlive(e) && this.store(name).has(Handle.index(e)); }
  get(e, name) { return this.store(name).get(Handle.index(e)); }
  describeStores() { return [...this.stores.values()].map((s) => `${s.name}:${s.count}`).join(' '); }
}
// EcsComponents.cs: データだけの構造体(Transform2D 12 / Previous2D 12 / Velocity2D 16 / Sprite2D 28 バイト)
World.schema = {
  Transform2D: ['px', 'py', 'rot'], Previous2D: ['px', 'py', 'rot'],
  Velocity2D: ['vx', 'vy', 'spin', 'half'], Sprite2D: ['kind', 'size', 'r', 'g', 'b', 'a', 'layer'],
};

// EcsSystems.cs: 状態を持たない手続き。aligned なら添字をそのまま使い、そうでなければ番号で引く(結合)
const EcsSystems = {
  areAligned(a, b) {
    if (a.count !== b.count) return false;
    for (let i = 0; i < a.count; i++) if (a.denseToEntity[i] !== b.denseToEntity[i]) return false;
    return true;
  },
  snapshot(world, aligned) {
    const t = world.store('Transform2D'), p = world.store('Previous2D'), tc = t.cols, pc = p.cols;
    for (let i = 0; i < t.count; i++) {
      const d = aligned ? i : p.denseIndexOf(t.denseToEntity[i]);
      if (d < 0) continue;
      pc.px[d] = tc.px[i]; pc.py[d] = tc.py[i]; pc.rot[d] = tc.rot[i];
    }
  },
  move(world, dt, bounds, aligned) {
    const t = world.store('Transform2D'), v = world.store('Velocity2D'), tc = t.cols, vc = v.cols, f = Math.fround;
    dt = f(dt);
    for (let i = 0; i < t.count; i++) {
      const d = aligned ? i : v.denseIndexOf(t.denseToEntity[i]);
      if (d < 0) continue;
      let x = f(tc.px[i] + f(vc.vx[d] * dt)), y = f(tc.py[i] + f(vc.vy[d] * dt));
      const h = vc.half[d];
      if (x < h) { x = h; vc.vx[d] = -vc.vx[d]; } else if (x > f(bounds[0] - h)) { x = f(bounds[0] - h); vc.vx[d] = -vc.vx[d]; }
      if (y < h) { y = h; vc.vy[d] = -vc.vy[d]; } else if (y > f(bounds[1] - h)) { y = f(bounds[1] - h); vc.vy[d] = -vc.vy[d]; }
      tc.px[i] = x; tc.py[i] = y; tc.rot[i] = f(tc.rot[i] + f(vc.spin[d] * dt));
    }
  },
};

// FNV-1a(64 ビット)。float のビット表現を順に混ぜる(Day 19 の Checksum、Day 20 の PlayerChecksum)
const F32 = new Float32Array(1), U32 = new Uint32Array(F32.buffer);
const MASK = (1n << 64n) - 1n, PRIME = 1099511628211n;
function fnv1aFloats(values) {
  let h = 14695981039346656037n;
  for (const v of values) { F32[0] = v; const u = U32[0]; for (let b = 0; b < 4; b++) { h ^= BigInt((u >>> (b * 8)) & 255); h = (h * PRIME) & MASK; } }
  return h;
}
const hex16 = (h) => h.toString(16).toUpperCase().padStart(16, '0');

const Core = {
  GameLoop, GameAction, actionNames, InputMap, InputSnapshot, InputSystem, InputRecorder, Handle, ResourcePool, ResourceManager,
  Transform, TransformStats: Stats, Component, GameObject, Scene, ComponentStore, World, EcsSystems, fnv1aFloats, hex16,
};
if (typeof window !== 'undefined') window.Core = Core;
if (typeof module !== 'undefined') module.exports = Core;
})();
