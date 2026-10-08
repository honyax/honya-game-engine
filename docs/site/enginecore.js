// ============================================================
//  Phase 4(エンジンコア)のクラスを JS に移したもの。window.Core にまとめて置く
//  - Day 19: GameLoop.cs(固定タイムステップ+補間)
//  - Day 20: InputMap.cs・InputSnapshot.cs・InputSystem.cs・InputRecorder.cs
//  - Day 21: Handle.cs・ResourcePool.cs・ResourceManager.cs(テクスチャは名前だけの値。復号とアップロードの時間はページ側で決める)
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

// FNV-1a(64 ビット)。float のビット表現を順に混ぜる(Day 19 の Checksum、Day 20 の PlayerChecksum)
const F32 = new Float32Array(1), U32 = new Uint32Array(F32.buffer);
const MASK = (1n << 64n) - 1n, PRIME = 1099511628211n;
function fnv1aFloats(values) {
  let h = 14695981039346656037n;
  for (const v of values) { F32[0] = v; const u = U32[0]; for (let b = 0; b < 4; b++) { h ^= BigInt((u >>> (b * 8)) & 255); h = (h * PRIME) & MASK; } }
  return h;
}
const hex16 = (h) => h.toString(16).toUpperCase().padStart(16, '0');

const Core = { GameLoop, GameAction, actionNames, InputMap, InputSnapshot, InputSystem, InputRecorder, Handle, ResourcePool, ResourceManager, fnv1aFloats, hex16 };
if (typeof window !== 'undefined') window.Core = Core;
if (typeof module !== 'undefined') module.exports = Core;
})();
