// ============================================================
//  roadmap.html の全体図を、本文の表から組み立てる
//  原本は表だけ。「状態」列に「済」と書けば、全体図と進捗バーが追従する。
//  Day 列に <a href="plans/DayXX.html"> があれば、全体図のチップもそこへリンクする。
// ============================================================
(() => {
'use strict';
const root = document.getElementById('overview');
if (!root) return;

const esc = (s) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

// Phase の見出し(h3)ごとに、その下の最初の表を読む
const phases = [];
for (const h3 of document.querySelectorAll('main h3')) {
  const m = h3.textContent.trim().match(/^(Phase \d+|教養編|VRM編)[:：]?\s*(.*)$/);
  if (!m) continue;
  let table = null;
  for (let el = h3.nextElementSibling; el && !/^H[23]$/.test(el.tagName); el = el.nextElementSibling) {
    table = el.tagName === 'TABLE' ? el : el.querySelector('table');
    if (table) break;
  }
  if (!table) continue;
  const heads = [...table.querySelectorAll('thead th')].map((th) => th.textContent.trim());
  const iDay = heads.indexOf('Day'), iState = heads.indexOf('状態');
  if (iDay < 0 || iState < 0) continue;

  const parts = m[2].replace(/\(Day [^)]*\)/, '').split(' — ');
  const phase = { label: m[1], name: parts[0].trim() || (parts[1] || '').trim(), anchor: h3.id, days: [] };
  for (const tr of table.querySelectorAll('tbody tr')) {
    const td = tr.children;
    const link = td[iDay].querySelector('a');
    const done = td[iState].textContent.trim() === '済';
    // 表示だけ整える(セルの文字は「済」のまま)
    if (done && !td[iState].querySelector('.pill')) td[iState].innerHTML = '<span class="pill good">済</span>';
    phase.days.push({
      id: td[iDay].textContent.trim(),
      href: link ? link.getAttribute('href') : null,
      done,
      text: (td[2] ? td[2].textContent : '').trim(),
    });
  }
  phases.push(phase);
}

const total = phases.reduce((n, p) => n + p.days.length, 0);
const done = phases.reduce((n, p) => n + p.days.filter((d) => d.done).length, 0);

const rows = phases.map((p) => {
  const chips = p.days.map((d) => {
    const cls = 'chip' + (d.done ? ' done' : '') + (d.href ? ' has-doc' : '');
    const tip = esc(`Day ${d.id}: ${d.text}`);
    return `<a class="${cls}" href="${esc(d.href || '#' + p.anchor)}" title="${tip}">${esc(d.id)}</a>`;
  }).join('');
  const n = p.days.filter((d) => d.done).length;
  return `<div class="phase-row"><a class="phase-name" href="#${p.anchor}"><span class="pl">${esc(p.label)}</span>`
    + `<span class="pn">${esc(p.name)}</span></a><div class="chips">${chips}</div><span class="pc">${n}/${p.days.length}</span></div>`;
}).join('');

root.innerHTML = `
  <div class="progress-head">
    <p class="big"><span class="num">${done}</span><span class="of">/ ${total} Day 写経済み</span></p>
    <div class="legend-row">
      <span class="lg"><i class="chip done"></i>写経済み</span>
      <span class="lg"><i class="chip"></i>未着手</span>
      <span class="lg"><i class="chip has-doc"></i>HTML の計画書あり(クリックで開く)</span>
    </div>
  </div>
  <div class="bar" role="img" aria-label="${done} / ${total}"><span style="width:${(done / Math.max(total, 1) * 100).toFixed(1)}%"></span></div>
  <div class="phases">${rows}</div>`;
})();
