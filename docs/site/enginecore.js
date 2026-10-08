// ============================================================
//  Phase 4(エンジンコア)のクラスを JS に移したもの。window.Core にまとめて置く
//  - Day 19: GameLoop.cs(固定タイムステップ+補間)
//  - Day 20: InputMap.cs・InputSnapshot.cs・InputSystem.cs・InputRecorder.cs
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

// FNV-1a(64 ビット)。float のビット表現を順に混ぜる(Day 19 の Checksum、Day 20 の PlayerChecksum)
const F32 = new Float32Array(1), U32 = new Uint32Array(F32.buffer);
const MASK = (1n << 64n) - 1n, PRIME = 1099511628211n;
function fnv1aFloats(values) {
  let h = 14695981039346656037n;
  for (const v of values) { F32[0] = v; const u = U32[0]; for (let b = 0; b < 4; b++) { h ^= BigInt((u >>> (b * 8)) & 255); h = (h * PRIME) & MASK; } }
  return h;
}
const hex16 = (h) => h.toString(16).toUpperCase().padStart(16, '0');

const Core = { GameLoop, GameAction, actionNames, InputMap, InputSnapshot, InputSystem, InputRecorder, fnv1aFloats, hex16 };
if (typeof window !== 'undefined') window.Core = Core;
if (typeof module !== 'undefined') module.exports = Core;
})();
