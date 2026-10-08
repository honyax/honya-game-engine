// ============================================================
//  .NET の値を JS で同じに出すための部品。window.DotNet にまとめて置く
//  - DotNetRandom: System.Random(種あり)と同じ並びを出す。Next(n) は Math.trunc(nextDouble() * n) で同じになる
//  - fmtFloat: C# の float.ToString()(最短の桁数、ちょうど真ん中は偶数の側へ、E 表記の閾値も同じ)
//  計画書の実験台(Day 19〜 の乱数、Day 25〜30 の衝突判定 collision2d.js)が共有する
// ============================================================
(() => {
'use strict';
const f = Math.fround;

// ---------------- .NET の System.Random(種あり。Net5CompatSeedImpl と同じ並び) ----------------
class DotNetRandom {
  constructor(seed) {
    const MBIG = 2147483647, MSEED = 161803398;
    const a = new Array(56).fill(0);
    let mj = MSEED - (seed === -2147483648 ? MBIG : Math.abs(seed)), mk = 1;
    a[55] = mj;
    for (let i = 1; i < 55; i++) {
      const ii = (21 * i) % 55;
      a[ii] = mk; mk = mj - mk; if (mk < 0) mk += MBIG; mj = a[ii];
    }
    for (let k = 1; k < 5; k++) for (let i = 1; i < 56; i++) { a[i] -= a[1 + (i + 30) % 55]; if (a[i] < 0) a[i] += MBIG; }
    this.a = a; this.inext = 0; this.inextp = 21; this.MBIG = MBIG;
  }
  nextDouble() {
    let i = this.inext + 1; if (i >= 56) i = 1;
    let p = this.inextp + 1; if (p >= 56) p = 1;
    let r = this.a[i] - this.a[p];
    if (r === this.MBIG) r--;
    if (r < 0) r += this.MBIG;
    this.a[i] = r; this.inext = i; this.inextp = p;
    return r * (1.0 / this.MBIG);
  }
}

// ---------------- C# の float.ToString() ----------------
function fmtFloat(x) {
  if (Number.isNaN(x)) return 'NaN';
  if (Object.is(x, -0)) return '-0';
  if (x === 0) return '0';
  let s = '';
  for (let p = 1; p <= 9; p++) { s = roundEven(x, p); if (f(Number(s)) === x) break; }
  const n = Number(s), e = Math.floor(Math.log10(Math.abs(n)));
  if (e < -5 || e >= 15) {
    const m = String(Number((n / Math.pow(10, e)).toPrecision(9)));
    return `${m}E${e < 0 ? '-' : '+'}${String(Math.abs(e)).padStart(2, '0')}`;
  }
  return String(n);
}
// toPrecision はちょうど真ん中を大きい側へ丸めるが、.NET は偶数の側へ丸める(472.703125 → 472.70312)
function roundEven(x, p) {
  const s = x.toPrecision(p);
  const m = Math.abs(x).toExponential(60).match(/^(\d)\.(\d+)e([+-]\d+)$/);
  const digits = m[1] + m[2], rest = digits.slice(p);
  if (!/^50*$/.test(rest) || Number(digits[p - 1]) % 2 !== 0) return s;
  // 真ん中で、切り捨てた側の末尾が偶数 → 切り捨てる
  const e = Number(m[3]), mant = digits.slice(0, p);
  return String((x < 0 ? -1 : 1) * Number(`${mant[0]}.${mant.slice(1)}e${e}`));
}

const DotNet = { DotNetRandom, fmtFloat };
if (typeof window !== 'undefined') window.DotNet = DotNet;
if (typeof module !== 'undefined') module.exports = DotNet;
})();
