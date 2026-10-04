#!/usr/bin/env node
'use strict';
/*
 * jpegfix.js - repair baseline JPEGs from which every 0x0D byte was deleted
 * (e.g. FTP ASCII-mode transfer).  No dependencies.
 *
 *   node jpegfix.js repair  bad.jpg fixed.jpg [--beam 8] [--truth good.jpg]
 *   node jpegfix.js corrupt good.jpg bad.jpg      (test helper: deletes all 0x0D)
 *   node jpegfix.js verify  a.jpg b.jpg           (byte-for-byte compare)
 *
 * Idea: the missing bytes are 8 whole bits missing from the Huffman bitstream.
 * We decode block by block with our own decoder and, at every block, consider
 * "no byte missing here" and "a 0x0D is missing at byte offset d" for every d in the
 * span the block occupies.  Each hypothesis is scored by how smoothly the decoded
 * block continues its already-decoded neighbours (pixel seams for luma, DC
 * continuity for chroma) and by hard JPEG validity rules.  A beam of the best
 * hypotheses is kept, so a wrong guess that looks plausible for a block or two is
 * still recoverable.  At the end the surviving path must end exactly at the end of
 * the file, which is a strong global check.
 */
const fs = require('fs');

// ------------------------------------------------------------------ tunables
const W_M = +(process.env.W_M || 0.5);            // weight of the coefficient-statistics term
const LOOK = +(process.env.LOOK || 4);             // blocks of lookahead used to rank a hypothesis
const FAILPEN = +(process.env.FAILPEN || 25);         // per un-decodable lookahead block
const WIN = 400;            // bytes of lookahead window per block decode (a block is rarely > 200 B)
const CAP = 60;             // cap on the cost of any single block
const INS_PEN = +(process.env.INS_PEN || 8);          // cost charged per inserted byte (prior: insertions are rare-ish)
const PAIR_TRIGGER = 22;    // try 2 insertions inside one block only if best single option costs more
const PAIR_RANK = 2;        // ...and only for the best N beam states
const PAIR_SPAN = 56;       // max bytes searched for double insertions

const ZZ = [0,1,8,16,9,2,3,10,17,24,32,25,18,11,4,5,12,19,26,33,40,48,41,34,27,20,13,6,7,14,21,28,
  35,42,49,56,57,50,43,36,29,22,15,23,30,37,44,51,58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63];

// ------------------------------------------------------------------ JPEG parsing
function buildLut(counts, syms) {
  const lut = new Uint16Array(65536);       // (len << 8) | symbol, 0 = invalid
  let code = 0, k = 0;
  for (let len = 1; len <= 16; len++) {
    for (let i = 0; i < counts[len - 1]; i++) {
      const sym = syms[k++];
      const lo = code << (16 - len), hi = (code + 1) << (16 - len);
      for (let j = lo; j < hi && j < 65536; j++) lut[j] = (len << 8) | sym;
      code++;
    }
    code <<= 1;
  }
  return lut;
}

function parseJpeg(buf) {
  if (buf[0] !== 0xFF || buf[1] !== 0xD8) throw new Error('not a JPEG');
  const J = { qt: {}, hf: {}, comps: [], scan: [], dri: 0 };
  let p = 2;
  for (;;) {
    if (buf[p] !== 0xFF) throw new Error('header damaged (no marker at byte ' + p + ')');
    while (buf[p] === 0xFF) p++;
    const m = buf[p++];
    if (m === 0xD9 || m === 0x00) throw new Error('unexpected marker before SOS');
    const len = (buf[p] << 8) | buf[p + 1];
    const seg = buf.subarray(p + 2, p + len);
    if (m === 0xDB) {
      let o = 0;
      while (o < seg.length) {
        const pq = seg[o] >> 4, tq = seg[o] & 15; o++;
        const t = new Int32Array(64);
        for (let i = 0; i < 64; i++) { if (pq) { t[i] = (seg[o] << 8) | seg[o + 1]; o += 2; } else t[i] = seg[o++]; }
        J.qt[tq] = t;
      }
    } else if (m === 0xC4) {
      let o = 0;
      while (o < seg.length) {
        const tc = seg[o] >> 4, th = seg[o] & 15; o++;
        const counts = seg.subarray(o, o + 16); o += 16;
        let tot = 0; for (let i = 0; i < 16; i++) tot += counts[i];
        J.hf[tc * 4 + th] = buildLut(counts, seg.subarray(o, o + tot)); o += tot;
      }
    } else if (m === 0xC0 || m === 0xC1) {
      J.precision = seg[0]; J.height = (seg[1] << 8) | seg[2]; J.width = (seg[3] << 8) | seg[4];
      for (let i = 0; i < seg[5]; i++) J.comps.push({ id: seg[6 + 3 * i], h: seg[7 + 3 * i] >> 4, v: seg[7 + 3 * i] & 15, tq: seg[8 + 3 * i] });
    } else if (m >= 0xC2 && m <= 0xCF && m !== 0xC4 && m !== 0xC8 && m !== 0xCC) {
      throw new Error('only baseline JPEG is supported');
    } else if (m === 0xDD) {
      J.dri = (seg[0] << 8) | seg[1];
    } else if (m === 0xDA) {
      for (let i = 0; i < seg[0]; i++) J.scan.push({ cs: seg[1 + 2 * i], td: seg[2 + 2 * i] >> 4, ta: seg[2 + 2 * i] & 15 });
      J.scanStart = p + len;
      break;
    }
    p += len;
  }
  if (J.dri) throw new Error('restart markers present (not supported by this prototype)');
  if (J.precision !== 8 || J.comps.length !== 3 || J.scan.length !== 3) throw new Error('only 8-bit 3-component images supported');
  const [y, cb, cr] = J.comps;
  if (!(y.h === 2 && y.v === 2 && cb.h === 1 && cb.v === 1 && cr.h === 1 && cr.v === 1))
    throw new Error('only 4:2:0 (2x2,1x1,1x1) supported in this prototype');
  J.mx = Math.ceil(J.width / 16); J.my = Math.ceil(J.height / 16);
  J.mcus = J.mx * J.my; J.blocks = J.mcus * 6;
  J.dcLut = []; J.acLut = []; J.qz = [];
  for (let i = 0; i < 3; i++) {
    const sc = J.scan[i], comp = J.comps.find(c => c.id === sc.cs);
    J.dcLut.push(J.hf[sc.td]); J.acLut.push(J.hf[4 + sc.ta]); J.qz.push(J.qt[comp.tq]);
    if (!J.dcLut[i] || !J.acLut[i] || !J.qz[i]) throw new Error('missing table');
  }
  return J;
}

// strip byte stuffing (FF 00 -> FF); returns { data, end } where end = raw index of EOI/end
function unstuff(buf, s) {
  let e = buf.length;
  if (buf[e - 2] === 0xFF && buf[e - 1] === 0xD9) e -= 2;
  const out = new Uint8Array(e - s); let o = 0;
  for (let i = s; i < e; i++) {
    const b = buf[i]; out[o++] = b;
    if (b === 0xFF) { i++; if (i < e && buf[i] !== 0x00) throw new Error('unexpected marker inside scan at ' + (i - 1)); }
  }
  return { data: out.subarray(0, o), end: e };
}
function restuff(u) {
  let n = u.length; for (let i = 0; i < u.length; i++) if (u[i] === 0xFF) n++;
  const out = Buffer.alloc(n); let o = 0;
  for (let i = 0; i < u.length; i++) { out[o++] = u[i]; if (u[i] === 0xFF) out[o++] = 0; }
  return out;
}

// ------------------------------------------------------------------ block decoding
const M = new Float64Array(64);
for (let x = 0; x < 8; x++) for (let u = 0; u < 8; u++) M[x * 8 + u] = (u === 0 ? Math.SQRT1_2 : 1) / 2 * Math.cos((2 * x + 1) * u * Math.PI / 16);
const coef = new Int32Array(64), tmp = new Float64Array(64);
let OVER = 0;
function idct(c, out) {
  for (let v = 0; v < 8; v++) for (let x = 0; x < 8; x++) {
    let s = 0; for (let u = 0; u < 8; u++) s += c[v * 8 + u] * M[x * 8 + u]; tmp[v * 8 + x] = s;
  }
  let over = 0;
  for (let y = 0; y < 8; y++) for (let x = 0; x < 8; x++) {
    let s = 128; for (let v = 0; v < 8; v++) s += M[y * 8 + v] * tmp[v * 8 + x];
    if (s < -20) over += -20 - s; else if (s > 275) over += s - 275;
    out[y * 8 + x] = s < 0 ? 0 : s > 255 ? 255 : (s + 0.5) | 0;
  }
  OVER = over / 64;
}
function p16(w, bp) { const i = bp >> 3; return (((w[i] << 16) | (w[i + 1] << 8) | w[i + 2]) >> (8 - (bp & 7))) & 0xFFFF; }

const T0 = new Float64Array(8), T1 = new Float64Array(8), L0 = new Float64Array(8), L1 = new Float64Array(8);
const REFW = +(process.env.REFW || 1);
const REF = { y: null, cb: null, cr: null };
const METRIC = process.env.METRIC || 'grad';
const SZ = new Uint8Array(64);
const R = { ok: false, end: 0, pred: 0, cost: 0, dc: 0, pix: new Uint8Array(64) };

// Decode block number S.n from window `w` starting at bit s0.  Returns true/false and fills R.
function evalBlock(J, S, w, s0, limitRel) {
  const bi = S.n % 6, ci = bi < 4 ? 0 : bi - 3;
  const dcLut = J.dcLut[ci], acLut = J.acLut[ci], q = J.qz[ci];
  let pred = ci === 0 ? S.pY : ci === 1 ? S.pCb : S.pCr;
  let bp = s0;
  coef.fill(0); SZ.fill(0);
  let e = dcLut[p16(w, bp)], len = e >> 8;
  if (!len) { R.fail = bp; return R.ok = false; }
  bp += len;
  let s = e & 255;
  if (s > 11) { R.fail = bp; return R.ok = false; }
  if (s) { let v = p16(w, bp) >> (16 - s); bp += s; if (v < (1 << (s - 1))) v -= (1 << s) - 1; pred += v; }
  const dcBits = bp - s0;
  const dc = pred * q[0];
  if (dc > 1100 || dc < -1100) { R.fail = bp; return R.ok = false; }
  coef[0] = dc;
  let k = 1;
  while (k < 64) {
    e = acLut[p16(w, bp)]; len = e >> 8;
    if (!len) { R.fail = bp; return R.ok = false; }
    bp += len;
    const rs = e & 255, r = rs >> 4; s = rs & 15;
    if (s === 0) {
      if (r === 15) { k += 16; if (k >= 64) { R.fail = bp; return R.ok = false; } continue; }
      if (r === 0) break;
      R.fail = bp; return R.ok = false;
    }
    k += r;
    if (k > 63 || s > 10) { R.fail = bp; return R.ok = false; }
    let v = p16(w, bp) >> (16 - s); bp += s;
    if (v < (1 << (s - 1))) v -= (1 << s) - 1;
    const dq = v * q[k];
    if (dq > 1500 || dq < -1500) { R.fail = bp; return R.ok = false; }
    coef[ZZ[k]] = dq; SZ[k] = s; k++;
  }
  if (bp > limitRel || bp > WIN * 8) { R.fail = bp; return R.ok = false; }

  // ---- cost: how well does this block continue its neighbours?
  const mcu = (S.n / 6) | 0, mx = mcu % J.mx, my = (mcu / J.mx) | 0;
  let sum = 0, cnt = 0, cost;
  if (bi < 4) {
    const pix = R.pix; idct(coef, pix);
    const mp = S.mcuPix, eb = S.edgeBottom, er = S.edgeRight, W16 = J.mx * 16;
    let hasT = false, hasL = false;
    if (bi === 0) {
      if (my > 0) { hasT = true; for (let c = 0; c < 8; c++) { T0[c] = eb[mx * 16 + c]; T1[c] = eb[W16 + mx * 16 + c]; } }
      if (mx > 0) { hasL = true; for (let r = 0; r < 8; r++) { L0[r] = er[r]; L1[r] = er[16 + r]; } }
    } else if (bi === 1) {
      if (my > 0) { hasT = true; for (let c = 0; c < 8; c++) { T0[c] = eb[mx * 16 + 8 + c]; T1[c] = eb[W16 + mx * 16 + 8 + c]; } }
      hasL = true; for (let r = 0; r < 8; r++) { L0[r] = mp[r * 8 + 7]; L1[r] = mp[r * 8 + 6]; }
    } else if (bi === 2) {
      hasT = true; for (let c = 0; c < 8; c++) { T0[c] = mp[56 + c]; T1[c] = mp[48 + c]; }
      if (mx > 0) { hasL = true; for (let r = 0; r < 8; r++) { L0[r] = er[8 + r]; L1[r] = er[16 + 8 + r]; } }
    } else {
      hasT = true; for (let c = 0; c < 8; c++) { T0[c] = mp[64 + 56 + c]; T1[c] = mp[64 + 48 + c]; }
      hasL = true; for (let r = 0; r < 8; r++) { L0[r] = mp[128 + r * 8 + 7]; L1[r] = mp[128 + r * 8 + 6]; }
    }
    if (METRIC === 'plain') {
      if (hasT) { for (let c = 0; c < 8; c++) sum += Math.abs(pix[c] - T0[c]); cnt += 8; }
      if (hasL) { for (let r = 0; r < 8; r++) sum += Math.abs(pix[r * 8] - L0[r]); cnt += 8; }
    } else {
      if (hasT) { for (let c = 0; c < 8; c++) sum += Math.abs((pix[c] - T0[c]) - ((pix[8 + c] - pix[c]) + (T0[c] - T1[c])) / 2); cnt += 8; }
      if (hasL) { for (let r = 0; r < 8; r++) sum += Math.abs((pix[r * 8] - L0[r]) - ((pix[r * 8 + 1] - pix[r * 8]) + (L0[r] - L1[r])) / 2); cnt += 8; }
    }
    cost = (cnt ? sum / cnt : 0) + OVER;
  } else {
    const left = bi === 4 ? S.leftCb : S.leftCr, above = bi === 4 ? S.aboveCb : S.aboveCr;
    if (mx > 0) { sum += Math.abs(dc - left); cnt++; }
    if (my > 0) { sum += Math.abs(dc - above[mx]); cnt++; }
    cost = cnt ? sum / cnt / 8 : 0;
  }
  if (REF.y) {
    if (bi < 4) { const bx = mx * 2 + (bi & 1), by = my * 2 + (bi >> 1); cost += REFW * Math.min(60, Math.abs(dc / 8 + 128 - REF.y[by * J.mx * 2 + bx])); }
    else cost += REFW * Math.min(60, Math.abs(dc / 8 + 128 - (bi === 4 ? REF.cb : REF.cr)[my * J.mx + mx]));
  }
  R.cost = cost > CAP ? CAP : cost;
  R.end = bp; R.pred = pred; R.dc = dc; R.dcBits = dcBits;
  R.mdl = MODEL.ready ? modelBitsFast(ci) - (bp - s0 - dcBits) : 0;
  return R.ok = true;
}


// ------------------------------------------------------------------ coefficient-size model
const MODEL = { cnt: [new Float64Array(64 * 5 * 11), new Float64Array(64 * 5 * 11)], tot: [new Float64Array(64 * 5), new Float64Array(64 * 5)] };
function trainBlock(ci) {
  const m = ci === 0 ? 0 : 1;
  for (let k = 1; k < 64; k++) {
    const ctx = Math.min(SZ[k - 1], 4), idx = k * 5 + ctx;
    MODEL.cnt[m][idx * 11 + SZ[k]]++; MODEL.tot[m][idx]++;
  }
}
MODEL.lg = [new Float32Array(64 * 5 * 11), new Float32Array(64 * 5 * 11)];
function refreshModel() {
  for (let m = 0; m < 2; m++) for (let idx = 0; idx < 64 * 5; idx++) for (let s = 0; s < 11; s++)
    MODEL.lg[m][idx * 11 + s] = -Math.log2((MODEL.cnt[m][idx * 11 + s] + 0.3) / (MODEL.tot[m][idx] + 0.3 * 11)) + s;
}
function modelBitsFast(ci) {
  const lg = MODEL.lg[ci === 0 ? 0 : 1]; let bits = 0;
  for (let k = 1; k < 64; k++) bits += lg[(k * 5 + (SZ[k - 1] > 4 ? 4 : SZ[k - 1])) * 11 + SZ[k]];
  return bits;
}
// bits needed to code the AC part of the last decoded block under the model (incl. raw magnitude bits)
function modelBits(ci) {
  const m = ci === 0 ? 0 : 1; let bits = 0;
  for (let k = 1; k < 64; k++) {
    const ctx = Math.min(SZ[k - 1], 4), idx = k * 5 + ctx;
    bits += -Math.log2((MODEL.cnt[m][idx * 11 + SZ[k]] + 0.3) / (MODEL.tot[m][idx] + 0.3 * 11)) + SZ[k];
  }
  return bits;
}

// ------------------------------------------------------------------ search
function makeChild(J, S, w0, newIns) {
  const bi = S.n % 6, mcu = (S.n / 6) | 0, mx = mcu % J.mx;
  const C = Object.assign({}, S);
  C.n = S.n + 1; C.bitPos = w0 * 8 + R.end;
  C.cost = S.cost + bscore() + INS_PEN * newIns.length;
  if (newIns.length) {
    C.k = S.k + newIns.length; C.rLast = newIns[newIns.length - 1];
    let node = S.ins;
    for (let i = 0; i < newIns.length; i++) node = { bad: newIns[i] - S.k - i, prev: node };
    C.ins = node;
  }
  if (bi < 4) {
    C.pY = R.pred;
    const mp = new Uint8Array(256); if (bi) mp.set(S.mcuPix); mp.set(R.pix, bi * 64); C.mcuPix = mp;
  } else if (bi === 4) {
    C.pCb = R.pred; C.curCb = R.dc;
  } else {
    C.pCr = R.pred;
    const mp = S.mcuPix;
    const eb = new Uint8Array(S.edgeBottom); const er = new Uint8Array(32), W16 = J.mx * 16;
    for (let c = 0; c < 8; c++) {
      eb[mx * 16 + c] = mp[128 + 56 + c]; eb[mx * 16 + 8 + c] = mp[192 + 56 + c];
      eb[W16 + mx * 16 + c] = mp[128 + 48 + c]; eb[W16 + mx * 16 + 8 + c] = mp[192 + 48 + c];
    }
    for (let r = 0; r < 8; r++) {
      er[r] = mp[64 + r * 8 + 7]; er[8 + r] = mp[192 + r * 8 + 7];
      er[16 + r] = mp[64 + r * 8 + 6]; er[16 + 8 + r] = mp[192 + r * 8 + 6];
    }
    C.edgeBottom = eb; C.edgeRight = er;
    const ab = new Int32Array(S.aboveCb); ab[mx] = S.curCb; C.aboveCb = ab; C.leftCb = S.curCb;
    const ar = new Int32Array(S.aboveCr); ar[mx] = R.dc; C.aboveCr = ar; C.leftCr = R.dc;
  }
  return C;
}

function bi0(S) { return (S.n % 6) < 4 ? 0 : 1; }
function fillWindow(S, bad, out) {
  const w0 = S.bitPos >> 3;
  for (let t = 0; t < WIN + 8; t++) {
    const i = w0 + t;
    if (i === S.rLast) out[t] = 0x0D;
    else { const b = i - S.k; out[t] = b < bad.length ? bad[b] : 0; }
  }
}
const chainWin = new Uint8Array(WIN + 8);
// Having just evaluated block S.n (result in R) with insertions `newIns`, greedily decode the next L blocks
// (no further insertions) and return the summed score (this block included).  A block that cannot be decoded
// costs FAILPEN instead of killing the candidate, since another byte may be missing inside the lookahead.
function bscore() { return R.cost + (MODEL.ready ? W_M * R.mdl : 0); }
function lookFrom(J, C, L, bad) {
  let sum = 0, child = C;
  for (let j = 0; j < L && child.n < J.blocks; j++) {
    fillWindow(child, bad, chainWin);
    const lim = 8 * (bad.length + child.k) - 8 * (child.bitPos >> 3);
    if (!evalBlock(J, child, chainWin, child.bitPos & 7, lim)) { sum += FAILPEN * (L - j); break; }
    sum += bscore();
    child = makeChild(J, child, child.bitPos >> 3, []);
  }
  return sum;
}

function repair(buf, opts) {
  const J = parseJpeg(buf);
  if (opts.ref) { const f = new Float32Array(opts.ref.buffer, opts.ref.byteOffset, opts.ref.length >> 2), ny = J.mx * J.my * 4, nc = J.mx * J.my; REF.y = f.subarray(0, ny); REF.cb = f.subarray(ny, ny + nc); REF.cr = f.subarray(ny + nc, ny + 2 * nc); }
  const { data: bad, end: rawEnd } = unstuff(buf, J.scanStart);
  const beamW = opts.beam || 8;
  const total = Math.min(J.blocks, opts.maxBlocks || J.blocks);
  const base = new Uint8Array(WIN + 8), win2 = new Uint8Array(WIN + 8);

  let beam = [{
    n: 0, bitPos: 0, k: 0, rLast: -1, rank: 0, pY: 0, pCb: 0, pCr: 0, cost: 0, ins: null,
    mcuPix: new Uint8Array(256), edgeBottom: new Uint8Array(J.mx * 32), edgeRight: new Uint8Array(32),
    aboveCb: new Int32Array(J.mx), aboveCr: new Int32Array(J.mx), leftCb: 0, leftCr: 0, curCb: 0,
  }];
  let stuckAt = -1;
  const t0 = Date.now();

  for (let n = 0; n < total; n++) {
    let next = [], cutoff = Infinity;
    const consider = (S, w0, newIns) => {
      const C = makeChild(J, S, w0, newIns);
      C.rank = C.cost + (LOOK ? lookFrom(J, C, LOOK, bad) : 0);
      if (opts.truthIns && opts.dbgN === n) {
        const l = []; for (let nd = C.ins; nd; nd = nd.prev) l.push(nd.bad); l.reverse();
        const ok = l.every((v, j) => v === opts.truthIns[j]);
        const tag = ok ? (l.length ? 'TRUTH' : 'noins-ok') : '';
        console.error(`  n=${n} cand ins=[${l.slice(-2)}] first=${(C.cost - S.cost).toFixed(1)} look=${(C.rank - C.cost).toFixed(1)} rank=${C.rank.toFixed(1)} ${tag}`);
      }
      if (C.rank >= cutoff) return;
      next.push(C);
      if (next.length >= beamW * 4) { next = prune(next, beamW); cutoff = next[next.length - 1].rank; }
    };

    beam.forEach((S, rank) => {
      const w0 = S.bitPos >> 3, s0 = S.bitPos & 7;
      for (let t = 0; t < WIN + 8; t++) {
        const i = w0 + t;
        if (i === S.rLast) base[t] = 0x0D;
        else { const b = i - S.k; base[t] = b < bad.length ? bad[b] : 0; }
      }
      const limit0 = 8 * (bad.length + S.k) - 8 * w0;
      let best = Infinity, extent;
      if (evalBlock(J, S, base, s0, limit0)) { best = R.cost; extent = R.end; consider(S, w0, []); }
      else extent = R.fail;
      const dmin = Math.max(s0 > 0 ? 1 : 0, S.rLast === w0 ? 1 : 0);
      const dmax = Math.min(Math.ceil(extent / 8) + 1, WIN - 16);
      if (opts.noIns) { if (opts.train && R.ok) trainBlock(bi0(S)); return; }
      for (let d = dmin; d <= dmax; d++) {
        win2.set(base.subarray(0, d), 0); win2[d] = 0x0D; win2.set(base.subarray(d, WIN + 7), d + 1);
        if (!evalBlock(J, S, win2, s0, limit0 + 8)) continue;
        if (R.end <= 8 * d) continue;                       // insertion not reached: same as no insertion
        if (R.cost + INS_PEN < best) best = R.cost + INS_PEN;
        consider(S, w0, [w0 + d]);
      }
      // two missing bytes inside one block (only when nothing else looks good)
      if (rank < PAIR_RANK && best > PAIR_TRIGGER) {
        const hi = Math.min(dmax + 6, dmin + PAIR_SPAN);
        for (let d1 = dmin; d1 <= hi; d1++) for (let d2 = d1 + 1; d2 <= hi + 1; d2++) {
          win2.set(base.subarray(0, d1), 0); win2[d1] = 0x0D;
          win2.set(base.subarray(d1, d2 - 1), d1 + 1); win2[d2] = 0x0D;
          win2.set(base.subarray(d2 - 1, WIN + 6), d2 + 1);
          if (!evalBlock(J, S, win2, s0, limit0 + 16)) continue;
          if (R.end <= 8 * d2) continue;
          consider(S, w0, [w0 + d1, w0 + d2]);
        }
      }
    });

    next = prune(next, beamW);
    if (!next.length) {
      stuckAt = n;
      if (opts.truthIns) { const S = beam[0], prog = (S.bitPos >> 3) - S.k; console.error(`stuck: block ${n} (mcu ${(n / 6) | 0}, blk ${n % 6}) bad-progress ${prog}; truth insertions nearby: ${opts.truthIns.filter(x => x > prog - 30 && x < prog + 120)}  (beam size ${beam.length}, best k=${S.k})`); }
      break;
    }
    beam = next;
    if (opts.truthIns) {
      let tr = -1;
      beam.forEach((S, i) => { if (tr >= 0) return; const l = []; for (let nd = S.ins; nd; nd = nd.prev) l.push(nd.bad); l.reverse();
        const prog = (S.bitPos >> 3) - S.k - 3; const m = opts.truthIns.filter(x => x < prog).length;
        if (l.length >= m && l.every((v, j) => v === opts.truthIns[j])) tr = i; });
      if (tr < 0 && !opts.lost) { opts.lost = true; console.error('TRUTH LOST at block ' + n + ' (mcu ' + ((n / 6) | 0) + ', blk ' + (n % 6) + '); best state k=' + beam[0].k); }
      if (opts.verbose && n < opts.verbose) console.error(`n=${n} truthRank=${tr} best.cost=${beam[0].cost.toFixed(1)} rank0=${beam[0].rank.toFixed(1)} ` + (tr >= 0 ? `truth.cost=${beam[tr].cost.toFixed(1)} rank=${beam[tr].rank.toFixed(1)}` : ''));
    }
    if (n % 1500 === 0 || n === total - 1) {
      const b = beam[0];
      console.error(`block ${n + 1}/${total}  best cost ${b.cost.toFixed(0)}  insertions ${b.k}  (${((Date.now() - t0) / 1000).toFixed(1)}s)`);
    }
  }

  // pick the winner: must end exactly at the end of the data
  let best = null;
  for (const S of beam) {
    const left = 8 * (bad.length + S.k) - S.bitPos;       // unread bits (0..7 = padding)
    S.left = left;
    if (left >= 0 && left < 8 && (!best || S.cost < best.cost)) best = S;
  }
  const verified = !!best && stuckAt < 0 && total === J.blocks;
  if (!best) best = beam[0];
  const list = []; for (let nd = best.ins; nd; nd = nd.prev) list.push(nd.bad); list.reverse();
  return { J, bad, rawEnd, list, verified, stuckAt, best, total };
}

function prune(arr, W) {
  arr.sort((a, b) => a.rank - b.rank);
  const seen = new Set(), out = [];
  for (const S of arr) {
    const inIns = (S.bitPos >> 3) === S.rLast ? (S.bitPos & 7) + 1 : 0;
    const key = (S.bitPos - 8 * S.k) + '|' + inIns + '|' + S.pY + '|' + S.pCb + '|' + S.pCr;
    if (seen.has(key)) continue; seen.add(key); out.push(S);
    if (out.length >= W) break;
  }
  return out;
}

function buildOutput(buf, res) {
  const { J, bad, rawEnd, list } = res;
  const out = new Uint8Array(bad.length + list.length); let o = 0, li = 0;
  for (let i = 0; i <= bad.length; i++) {
    while (li < list.length && list[li] === i) { out[o++] = 0x0D; li++; }
    if (i < bad.length) out[o++] = bad[i];
  }
  return Buffer.concat([buf.subarray(0, J.scanStart), restuff(out), buf.subarray(rawEnd)]);
}

function saveModel(path) { fs.writeFileSync(path, JSON.stringify({ cnt: MODEL.cnt.map(a => Array.from(a)), tot: MODEL.tot.map(a => Array.from(a)) })); }
function loadModel(path) {
  const m = JSON.parse(fs.readFileSync(path, 'utf8'));
  for (let i = 0; i < 2; i++) { MODEL.cnt[i].set(m.cnt[i]); MODEL.tot[i].set(m.tot[i]); }
  refreshModel(); MODEL.ready = true;
}
function truthList(goodBuf) {
  const J = parseJpeg(goodBuf), { data } = unstuff(goodBuf, J.scanStart);
  const t = []; let c = 0;
  for (let i = 0; i < data.length; i++) if (data[i] === 0x0D) { t.push(i - c); c++; }
  return t;
}

// ------------------------------------------------------------------ CLI
function main() {
  const a = process.argv.slice(2);
  const opt = n => { const i = a.indexOf('--' + n); return i >= 0 ? a[i + 1] : undefined; };
  if (a[0] === 'corrupt') { fs.writeFileSync(a[2], Buffer.from(fs.readFileSync(a[1]).filter(b => b !== 0x0D))); return; }
  if (a[0] === 'train') {      // train <model.json> <clean1.jpg> [clean2.jpg ...]
    for (const f of a.slice(2)) repair(fs.readFileSync(f), { beam: 1, noIns: true, train: true });
    saveModel(a[1]); console.error('model written to ' + a[1]); return;
  }
  if (a[0] === 'verify') {
    const x = fs.readFileSync(a[1]), y = fs.readFileSync(a[2]);
    console.log(x.equals(y) ? 'IDENTICAL' : 'DIFFERENT'); process.exit(x.equals(y) ? 0 : 1);
  }
  if (a[0] === 'repair') {
    const buf = fs.readFileSync(a[1]);
    if (opt('model')) loadModel(opt('model'));
    const res = repair(buf, { ref: opt('ref') ? fs.readFileSync(opt('ref')) : null, beam: +opt('beam') || 8, maxBlocks: +opt('max-blocks') || 0 });
    if (opt('truth')) {
      const t = truthList(fs.readFileSync(opt('truth')));
      const have = new Set(res.list);
      const lastBad = res.list.length ? res.list[res.list.length - 1] : 0;
      const tt = t.filter(x => x <= lastBad);
      console.error(`truth: ${t.length} insertions; found ${res.list.length}; of the truth up to the last found position, ${tt.filter(x => have.has(x)).length}/${tt.length} matched`);
    }
    if (res.stuckAt >= 0) console.error('STUCK at block ' + res.stuckAt + ' - search ran out of plausible candidates; writing partial result');
    else console.error(res.verified ? 'OK: decode ends exactly at end of file (consistent)' : 'WARNING: result is not consistent with end of file');
    fs.writeFileSync(a[2], buildOutput(buf, res));
    console.error(`inserted ${res.list.length} bytes`);
    return;
  }
  console.error('usage: jpegfix.js repair|corrupt|verify ...');
}
if (require.main === module) main();
module.exports = { repair, parseJpeg, loadModelPublic: p => loadModel(p), truth: truthList, refresh: () => { refreshModel(); MODEL.ready = true; } };