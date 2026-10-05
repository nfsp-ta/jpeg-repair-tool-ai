#!/usr/bin/env node
'use strict';
/*
 * Download freely licensed car photos from Wikimedia Commons (public API) as extra *clean* source images for local
 * benchmarking; nothing is committed (output dir is git-ignored).
 *   node tools/fetch-commons.js [outDir=testdata/overnight/src] [count=400]
 * Files are saved as orig-c<pageid>.jpg (1280 px wide thumbnails as served by Commons; make-synthetic-triplets.sh resizes them).
 * Resumable: existing files are skipped, the search state is not needed. Polite: identifies itself, ~1 request/second, backs off on 429/5xx.
 */
const fs = require('fs'), path = require('path');
const out = process.argv[2] || path.join(__dirname, '..', 'testdata', 'overnight', 'src');
const want = +process.argv[3] || 400;
const UA = 'jpegfix-research/0.1 (ashley+claude@harmonicwebdesign.com; local JPEG-repair benchmarking)';
fs.mkdirSync(out, { recursive: true });
const sleep = ms => new Promise(r => setTimeout(r, ms));

async function get(url, binary) {
  for (let t = 0; t < 6; t++) {
    try {
      const r = await fetch(url, { headers: { 'User-Agent': UA }, signal: AbortSignal.timeout(90000) });
      if (r.ok) return binary ? Buffer.from(await r.arrayBuffer()) : await r.json();
      if (r.status !== 429 && r.status < 500) return null;
    } catch (e) { /* retry */ }
    await sleep(3000 * (t + 1) * (t + 1));
  }
  return null;
}

const terms = ['Infiniti G35', 'Infiniti G37', 'Nissan 350Z', 'Nissan 370Z', 'Nissan Skyline', 'Nissan Maxima', 'Nissan Altima', 'Toyota Supra', 'Toyota Camry',
  'Honda Civic', 'Honda Accord', 'Acura TL', 'Subaru WRX', 'Mazda MX-5', 'Mazda RX-8', 'Ford Mustang', 'Chevrolet Camaro', 'Chevrolet Corvette',
  'Dodge Charger', 'BMW M3', 'BMW 3 Series', 'Audi A4', 'Mercedes-Benz C-Class', 'Porsche 911', 'Mitsubishi Lancer', 'Lexus IS', 'car show', 'autocross',
  'drag racing', 'drifting car', 'race car track day', 'car meet parking lot', 'sports car rear', 'sedan side view', 'car engine bay', 'car wheels rims'];

(async () => {
  let have = fs.readdirSync(out).filter(f => f.endsWith('.jpg')).length, tried = new Set();
  console.log(`${have} files present, want ${want}`);
  for (const term of terms) {
    for (let offset = 0; offset < 150 && have < want; offset += 50) {
      const api = 'https://commons.wikimedia.org/w/api.php?action=query&format=json&generator=search&gsrnamespace=6&gsrlimit=50&gsroffset=' + offset +
        '&gsrsearch=' + encodeURIComponent(term + ' filetype:bitmap') + '&prop=imageinfo&iiprop=url|size|mime&iiurlwidth=1280';
      const j = await get(api); await sleep(1000);
      if (!j || !j.query) break;
      for (const p of Object.values(j.query.pages)) {
        const i = p.imageinfo && p.imageinfo[0];
        if (!i || i.mime !== 'image/jpeg' || i.width < 1400 || i.height < 700 || tried.has(p.pageid)) continue;
        tried.add(p.pageid);
        const f = path.join(out, `orig-c${p.pageid}.jpg`);
        if (fs.existsSync(f)) continue;
        const b = await get(i.thumburl, true); await sleep(1500);
        if (!b || b[0] !== 0xFF || b[1] !== 0xD8 || b.length < 30000) continue;
        fs.writeFileSync(f, b); have++;
        if (have % 25 === 0) console.log(`  ${have} / ${want}  (${term})`);
        if (have >= want) break;
      }
    }
    if (have >= want) break;
  }
  console.log(`done: ${have} files in ${out}`);
})();
