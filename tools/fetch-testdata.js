#!/usr/bin/env node
'use strict';
/*
 * Download clean (uncorrupted) gallery JPEGs from the Wayback Machine for benchmarking.
 *   node tools/fetch-testdata.js [outDir=testdata/gallery]
 * Files are named <kind>-<id>.jpg with kind = orig | preview | thumb.  Existing files are skipped.
 * A file is kept only if it looks like a JPEG and contains 0x0D bytes (a damaged one would have none).
 */
const fs = require('fs'), path = require('path');
const out = process.argv[2] || path.join(__dirname, '..', 'testdata', 'gallery');
fs.mkdirSync(out, { recursive: true });
const sleep = ms => new Promise(r => setTimeout(r, ms));

async function get(url, tries = 4) {
  for (let t = 0; t < tries; t++) {
    try {
      const r = await fetch(url, { signal: AbortSignal.timeout(60000) });
      if (r.ok) return Buffer.from(await r.arrayBuffer());
      if (r.status !== 429 && r.status < 500) return null;
    } catch (e) { /* retry */ }
    await sleep(2000 * (t + 1));
  }
  return null;
}

(async () => {
  const cdx = 'https://web.archive.org/cdx/search/cdx?url=nfspmotorsports.com/forum/index.php%3Faction=mgallery;sa=media;id=*' +
    '&output=txt&fl=timestamp,original,mimetype&filter=statuscode:200&filter=mimetype:image/jpeg&limit=10000';
  const list = (await get(cdx)).toString().split('\n').filter(Boolean);
  const items = new Map();                                  // one capture per (kind,id)
  for (const line of list) {
    const [ts, url] = line.split(' ');
    const m = /;id=(\d+)(?:;(thumb|preview))?$/.exec(url);
    if (!m) continue;
    const key = (m[2] || 'orig') + '-' + m[1];
    if (!items.has(key)) items.set(key, { ts, url });
  }
  console.log(`${list.length} jpeg captures, ${items.size} unique images`);
  let ok = 0, skipped = 0, bad = 0;
  for (const [key, { ts, url }] of items) {
    const f = path.join(out, key + '.jpg');
    if (fs.existsSync(f)) { skipped++; continue; }
    const b = await get(`https://web.archive.org/web/${ts}id_/${url}`);
    if (!b || b[0] !== 0xFF || b[1] !== 0xD8 || !b.includes(0x0D)) { bad++; console.log('rejected ' + key); continue; }
    fs.writeFileSync(f, b); ok++;
    if (ok % 50 === 0) console.log(`  ${ok} downloaded`);
    await sleep(300);
  }
  console.log(`done: ${ok} downloaded, ${skipped} already present, ${bad} rejected`);
})();
