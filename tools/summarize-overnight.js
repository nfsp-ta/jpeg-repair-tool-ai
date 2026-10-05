#!/usr/bin/env node
'use strict';
// Summarise testdata/overnight/results*.txt: per size kind, rank runs by visually close blocks (then true state kept).
//   node tools/summarize-overnight.js results.txt            -> markdown
//   node tools/summarize-overnight.js results.txt --best-env -> "label|ENV=val ..." for the best non-default config (by mean rank over sizes)
const fs = require('fs');
const file = process.argv[2];
const rows = fs.readFileSync(file, 'utf8').split('\n').filter(Boolean).map(l => {
  const [label, kind, rest] = l.split(' | ');
  const num = re => { const m = re.exec(rest || ''); return m ? +m[1] : NaN; };
  return { label, kind, n: num(/n=\s*(\d+)/), exact: num(/identical=\s*(\d+)/), kept: num(/true state kept to (\d+)%/), close: num(/visually close blocks=(\d+)%/),
    ident: num(/\(identical (\d+)%/), match: /truth matched=(\d+)\/(\d+)/.exec(rest || '') };
}).filter(r => !isNaN(r.close));
const labels = [...new Set(rows.filter(r => !r.label.startsWith('REAL')).map(r => r.label))];
if (process.argv.includes('--best-env')) {
  const score = {};
  for (const kind of ['thumb', 'preview', 'orig']) {
    const rs = rows.filter(r => r.kind === kind && !r.label.startsWith('REAL')).sort((a, b) => b.close - a.close);
    rs.forEach((r, i) => { score[r.label] = (score[r.label] || 0) + i; });
  }
  const best = labels.filter(l => l !== 'default').sort((a, b) => (score[a] ?? 1e9) - (score[b] ?? 1e9))[0];
  if (best) console.log(best + '|' + best.replace(/\//g, ' '));
  process.exit(0);
}
const out = ['# Overnight sweep summary', '', `Generated ${new Date().toISOString()}. Sorted by visually close blocks (%); "kept" = true state kept; "exact" = byte-identical files.`, ''];
for (const group of ['', 'REAL ']) {
  for (const kind of ['thumb', 'preview', 'orig']) {
    const rs = rows.filter(r => r.kind === kind && (group ? r.label.startsWith('REAL') : !r.label.startsWith('REAL'))).sort((a, b) => b.close - a.close || b.kept - a.kept);
    if (!rs.length) continue;
    out.push(`## ${group ? 'Real gallery files' : 'Synthetic triplets'}: ${kind}`, '', '| setting | n | close % | identical blocks % | kept % | exact files | truth matched |', '|---|---|---|---|---|---|---|');
    for (const r of rs) out.push(`| ${r.label} | ${r.n} | ${r.close} | ${r.ident} | ${r.kept} | ${r.exact} | ${r.match ? r.match[1] + '/' + r.match[2] : ''} |`);
    out.push('');
  }
}
console.log(out.join('\n'));
