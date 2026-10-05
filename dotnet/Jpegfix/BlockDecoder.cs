using System.Runtime.Intrinsics;

namespace Jpegfix;

sealed class InsNode
{
    public int Bad;            // index in the unstuffed *bad* stream before which a 0x0D is inserted
    public InsNode? Prev;
}

/// <summary>One beam state. Arrays are never mutated after a state is created (children copy on write).</summary>
sealed class State
{
    public int N, BitPos, K, RLast, Origin;      // Origin: index of the parent in the previous beam
    public double Rank, Cost;
    public int PY, PCb, PCr;
    public InsNode? Ins;
    public byte[] McuPix = null!, EdgeBottom = null!, EdgeRight = null!;      // McuPix: 4 luma blocks, then Cb, then Cr (384 bytes)
    public byte[] CebCb = null!, CebCr = null!, CerCb = null!, CerCr = null!;   // chroma edges: bottom two rows per MCU column (2*Mx*8), right two columns of the previous MCU (16)
    public int[] AboveCb = null!, AboveCr = null!;
    public int LeftCb, LeftCr, CurCb;
    public State Clone() => (State)MemberwiseClone();
}

/// <summary>Result of the last <see cref="BlockDecoder.EvalBlock"/> call (reused to avoid allocation).</summary>
sealed class BlockResult
{
    public bool Ok;
    public int End, Fail, Pred, Dc, DcBits;
    public double Cost, Mdl;
    public byte[] Pix = new byte[64];
}

/// <summary>Bit-level block decoder plus the scoring that decides how plausible a decoded block is.</summary>
sealed class BlockDecoder
{
    public readonly JpegInfo J;
    public readonly Model? Mdl;
    public readonly BlockResult R = new();
    public readonly float[]? RefY, RefCb, RefCr;
    public double RefWeight = Tunables.RefW;          // weight of the sibling-reference term (per run, so variants can differ)

    readonly int[] coef = new int[64];
    int rowMask;                                   // bit v set when coefficient row v has a non-zero value (the inverse DCT skips the zero rows)
    readonly int[] nzCols = new int[8], nzRows = new int[8];
    readonly byte[] sz = new byte[64];
    readonly double[] tmp = new double[64];
    readonly double[] T0 = new double[8], T1 = new double[8], L0 = new double[8], L1 = new double[8];
    double over;
    static readonly double[] M = BuildM();
    static readonly double[] Mt = BuildMt();          // transposed: Mt[u * 8 + x] = M[x * 8 + u], contiguous in x for the vector loads
    readonly double[] px = new double[64];

    static double[] BuildMt() { var t = new double[64]; for (int x = 0; x < 8; x++) for (int u = 0; u < 8; u++) t[u * 8 + x] = M[x * 8 + u]; return t; }

    static double[] BuildM()
    {
        var m = new double[64];
        for (int x = 0; x < 8; x++) for (int u = 0; u < 8; u++)
            m[x * 8 + u] = (u == 0 ? Math.Sqrt(0.5) : 1) / 2 * Math.Cos((2 * x + 1) * u * Math.PI / 16);
        return m;
    }

    public BlockDecoder(JpegInfo j, Model? model, float[]? refData)
    {
        J = j; Mdl = model;
        if (refData != null)
        {
            int ny = J.Mx * J.My * 4, nc = J.Mx * J.My;
            RefY = refData[..ny]; RefCb = refData[ny..(ny + nc)]; RefCr = refData[(ny + nc)..(ny + 2 * nc)];
        }
    }

    public byte[] Sizes => sz;

    void Idct() { long t0 = Prof.Start(); IdctCore(); Prof.Stop(1, t0); }

    void IdctCore()
    {
        // Same arithmetic in the same order as the dense scalar version (multiply then add per term, zero terms skipped, no fused multiply-add),
        // so the results are bit-identical; the vector path computes 4 output columns at once.
        int nr = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (int v = 0; v < 8; v++)
            {
                if ((rowMask >> v & 1) == 0) continue;
                nzRows[nr++] = v; int b = v * 8, nc = 0;
                for (int u = 0; u < 8; u++) if (coef[b + u] != 0) nzCols[nc++] = u;
                for (int h = 0; h < 8; h += 4)
                {
                    var s = Vector256<double>.Zero;
                    for (int j = 0; j < nc; j++) { int u = nzCols[j]; s = s + Vector256.Create((double)coef[b + u]) * Vector256.LoadUnsafe(ref Mt[u * 8 + h]); }
                    s.StoreUnsafe(ref tmp[b + h]);
                }
            }
            for (int y = 0; y < 8; y++) for (int h = 0; h < 8; h += 4)
            {
                var s = Vector256.Create(128.0);
                for (int j = 0; j < nr; j++) { int v = nzRows[j]; s = s + Vector256.Create(Mt[v * 8 + y]) * Vector256.LoadUnsafe(ref tmp[v * 8 + h]); }
                s.StoreUnsafe(ref px[y * 8 + h]);
            }
        }
        else
        {
            for (int v = 0; v < 8; v++)
            {
                if ((rowMask >> v & 1) == 0) continue;
                nzRows[nr++] = v; int b = v * 8, nc = 0;
                for (int u = 0; u < 8; u++) if (coef[b + u] != 0) nzCols[nc++] = u;
                for (int x = 0; x < 8; x++) { double s = 0; for (int j = 0; j < nc; j++) { int u = nzCols[j]; s += coef[b + u] * M[x * 8 + u]; } tmp[b + x] = s; }
            }
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            {
                double s = 128; for (int j = 0; j < nr; j++) { int v = nzRows[j]; s += M[y * 8 + v] * tmp[v * 8 + x]; }
                px[y * 8 + x] = s;
            }
        }
        double ov = 0;
        for (int i = 0; i < 64; i++)
        {
            double s = px[i];
            if (s < -20) ov += -20 - s; else if (s > 275) ov += s - 275;
            R.Pix[i] = (byte)(s < 0 ? 0 : s > 255 ? 255 : (int)(s + 0.5));
        }
        over = ov / 64;
    }

    static int P16(byte[] w, int bp)
    {
        int i = bp >> 3;
        return (((w[i] << 16) | (w[i + 1] << 8) | w[i + 2]) >> (8 - (bp & 7))) & 0xFFFF;
    }

    bool Fail(int bp) { R.Fail = bp; return R.Ok = false; }

    /// <summary>Decode block S.N from window w starting at bit s0. Fills R. limitRel = bits available in the window.</summary>
    /// <param name="pure">The window is exactly the damaged stream at the state's position with no hypothetical insertion in it, so the decode (bits to pixels) depends only on the decoder-equivalent state and can be reused.</param>
    public bool EvalBlock(State S, byte[]? w, int s0, int limitRel, bool pure = false)
    {
        long t0 = Prof.Start();
        bool ok = (pure ? DecodeCached(S, w, s0, limitRel) : DecodeCore(S, w!, s0, limitRel)) && Score(S, s0);
        Prof.Stop(0, t0); return ok;
    }

    // ---- decode stage: bits -> coefficients -> pixels (+ statistics term). Pure function of the bits and the DC predictor.
    int dPred, dDc, dDcBits, dBp; double dMdl;

    sealed class DecEntry { public bool Fail; public int Consumed, Pred, Dc, DcBits; public double Mdl, Over; public readonly byte[] Pix = new byte[64]; }
    readonly Dictionary<(int, int, int), DecEntry>[] cache = { new(), new(), new(), new() };
    readonly int[] cacheN = { -1, -1, -1, -1 };
    public static long CacheHits, CacheMisses;
    public Action<State, byte[]>? Filler;          // builds the standard window for a state; used lazily on a cache miss when the caller passes no window
    readonly byte[] pureWin = new byte[Tunables.Win + 8];

    bool DecodeCached(State S, byte[]? w, int s0, int limitRel)
    {
        int ci = S.N % 6 < 4 ? 0 : S.N % 6 - 3, pred0 = ci == 0 ? S.PY : ci == 1 ? S.PCb : S.PCr;
        var key = (S.BitPos - 8 * S.K, (S.BitPos >> 3) == S.RLast ? (S.BitPos & 7) + 1 : 0, pred0);
        int slot = S.N & 3; if (cacheN[slot] != S.N) { cache[slot].Clear(); cacheN[slot] = S.N; }
        if (cache[slot].TryGetValue(key, out var e))
        {
            if (Prof.On) CacheHits++;
            if (e.Fail) { R.Fail = s0 + e.Consumed; R.Ok = false; return false; }
            Array.Copy(e.Pix, R.Pix, 64); over = e.Over; dPred = e.Pred; dDc = e.Dc; dDcBits = e.DcBits; dBp = s0 + e.Consumed; dMdl = e.Mdl; return true;
        }
        if (Prof.On) CacheMisses++;
        if (w == null) { Filler!(S, pureWin); w = pureWin; }
        bool ok = DecodeCore(S, w, s0, limitRel);
        var ne = new DecEntry();
        if (!ok) { ne.Fail = true; ne.Consumed = R.Fail - s0; }
        else { ne.Consumed = dBp - s0; ne.Pred = dPred; ne.Dc = dDc; ne.DcBits = dDcBits; ne.Mdl = dMdl; ne.Over = over; Array.Copy(R.Pix, ne.Pix, 64); }
        cache[slot][key] = ne; return ok;
    }

    bool DecodeCore(State S, byte[] w, int s0, int limitRel)
    {
        int bi = S.N % 6, ci = bi < 4 ? 0 : bi - 3;
        var dcLut = J.DcLut[ci]; var acLut = J.AcLut[ci]; var q = J.Qz[ci];
        int pred = ci == 0 ? S.PY : ci == 1 ? S.PCb : S.PCr;
        int bp = s0;
        Array.Clear(coef); Array.Clear(sz);
        int e = dcLut[P16(w, bp)], len = e >> 8;
        if (len == 0) return Fail(bp);
        bp += len;
        int s = e & 255;
        if (s > 11) return Fail(bp);
        if (s != 0) { int v = P16(w, bp) >> (16 - s); bp += s; if (v < (1 << (s - 1))) v -= (1 << s) - 1; pred += v; }
        int dcBits = bp - s0;
        int dc = pred * q[0];
        if (dc > 1100 || dc < -1100) return Fail(bp);
        coef[0] = dc; rowMask = 1;
        int k = 1;
        while (k < 64)
        {
            e = acLut[P16(w, bp)]; len = e >> 8;
            if (len == 0) return Fail(bp);
            bp += len;
            int rs = e & 255, r = rs >> 4; s = rs & 15;
            if (s == 0)
            {
                if (r == 15) { k += 16; if (k >= 64) return Fail(bp); continue; }
                if (r == 0) break;
                return Fail(bp);
            }
            k += r;
            if (k > 63 || s > 10) return Fail(bp);
            int v = P16(w, bp) >> (16 - s); bp += s;
            if (v < (1 << (s - 1))) v -= (1 << s) - 1;
            int dq = v * q[k];
            if (dq > 1500 || dq < -1500) return Fail(bp);
            coef[JpegParser.ZZ[k]] = dq; rowMask |= 1 << (JpegParser.ZZ[k] >> 3); sz[k] = (byte)s; k++;
        }
        if (bp > limitRel || bp > Tunables.Win * 8) return Fail(bp);
        Idct();                                   // pixels are needed by the cost, and for the edges kept in the state
        dPred = pred; dDc = dc; dDcBits = dcBits; dBp = bp;
        dMdl = Mdl != null ? Mdl.Bits(ci, sz) - (bp - s0 - dcBits) : 0;
        return true;
    }

    // ---- scoring stage: how well does the decoded block continue its neighbours? (depends on the state, so never cached)
    bool Score(State S, int s0)
    {
        int bi = S.N % 6, ci = bi < 4 ? 0 : bi - 3;
        int pred = dPred, dc = dDc, dcBits = dDcBits, bp = dBp;
        int mcu = S.N / 6, mx = mcu % J.Mx, my = mcu / J.Mx;
        double sum = 0, cost; int cnt = 0;
        if (bi < 4)
        {
            var pix = R.Pix; var mp = S.McuPix; var eb = S.EdgeBottom; var er = S.EdgeRight; int W16 = J.Mx * 16;
            bool hasT = false, hasL = false;
            if (bi == 0)
            {
                if (my > 0) { hasT = true; for (int c = 0; c < 8; c++) { T0[c] = eb[mx * 16 + c]; T1[c] = eb[W16 + mx * 16 + c]; } }
                if (mx > 0) { hasL = true; for (int r = 0; r < 8; r++) { L0[r] = er[r]; L1[r] = er[16 + r]; } }
            }
            else if (bi == 1)
            {
                if (my > 0) { hasT = true; for (int c = 0; c < 8; c++) { T0[c] = eb[mx * 16 + 8 + c]; T1[c] = eb[W16 + mx * 16 + 8 + c]; } }
                hasL = true; for (int r = 0; r < 8; r++) { L0[r] = mp[r * 8 + 7]; L1[r] = mp[r * 8 + 6]; }
            }
            else if (bi == 2)
            {
                hasT = true; for (int c = 0; c < 8; c++) { T0[c] = mp[56 + c]; T1[c] = mp[48 + c]; }
                if (mx > 0) { hasL = true; for (int r = 0; r < 8; r++) { L0[r] = er[8 + r]; L1[r] = er[16 + 8 + r]; } }
            }
            else
            {
                hasT = true; for (int c = 0; c < 8; c++) { T0[c] = mp[64 + 56 + c]; T1[c] = mp[64 + 48 + c]; }
                hasL = true; for (int r = 0; r < 8; r++) { L0[r] = mp[128 + r * 8 + 7]; L1[r] = mp[128 + r * 8 + 6]; }
            }
            if (Tunables.PlainMetric)
            {
                if (hasT) { for (int c = 0; c < 8; c++) sum += Math.Abs(pix[c] - T0[c]); cnt += 8; }
                if (hasL) { for (int r = 0; r < 8; r++) sum += Math.Abs(pix[r * 8] - L0[r]); cnt += 8; }
            }
            else
            {
                if (hasT) { for (int c = 0; c < 8; c++) sum += Math.Abs((pix[c] - T0[c]) - ((pix[8 + c] - pix[c]) + (T0[c] - T1[c])) / 2); cnt += 8; }
                if (hasL) { for (int r = 0; r < 8; r++) sum += Math.Abs((pix[r * 8] - L0[r]) - ((pix[r * 8 + 1] - pix[r * 8]) + (L0[r] - L1[r])) / 2); cnt += 8; }
            }
            cost = (cnt != 0 ? sum / cnt : 0) + over;
        }
        else
        {
            int left = bi == 4 ? S.LeftCb : S.LeftCr; var above = bi == 4 ? S.AboveCb : S.AboveCr;
            if (mx > 0) { sum += Math.Abs(dc - left); cnt++; }
            if (my > 0) { sum += Math.Abs(dc - above[mx]); cnt++; }
            double dcCost = cnt != 0 ? sum / cnt / 8 : 0;
            if (Tunables.Chroma == "dc") cost = dcCost;
            else
            {
                var pix = R.Pix; var ceb = bi == 4 ? S.CebCb : S.CebCr; var cer = bi == 4 ? S.CerCb : S.CerCr; int CW = J.Mx * 8;
                double ssum = 0; int scnt = 0;
                if (my > 0) { for (int c = 0; c < 8; c++) { double t0 = ceb[mx * 8 + c], t1 = ceb[CW + mx * 8 + c]; ssum += Math.Abs((pix[c] - t0) - ((pix[8 + c] - pix[c]) + (t0 - t1)) / 2); } scnt += 8; }
                if (mx > 0) { for (int r = 0; r < 8; r++) { double l0 = cer[r], l1 = cer[8 + r]; ssum += Math.Abs((pix[r * 8] - l0) - ((pix[r * 8 + 1] - pix[r * 8]) + (l0 - l1)) / 2); } scnt += 8; }
                cost = (scnt != 0 ? ssum / scnt : 0) + over;
                if (Tunables.Chroma == "both") cost += dcCost;
            }
        }
        if (RefY != null)
        {
            float rv = bi < 4 ? RefY[(my * 2 + (bi >> 1)) * J.Mx * 2 + mx * 2 + (bi & 1)] : (bi == 4 ? RefCb! : RefCr!)[my * J.Mx + mx];
            if (!float.IsNaN(rv)) cost += RefWeight * Math.Min(60, Math.Abs(dc / 8.0 + 128 - rv));      // NaN = the sibling is unknown here
        }
        R.Cost = cost > Tunables.Cap ? Tunables.Cap : cost;
        R.End = bp; R.Pred = pred; R.Dc = dc; R.DcBits = dcBits;
        R.Mdl = dMdl;
        return R.Ok = true;
    }

    /// <summary>Score of the block last evaluated: cost plus weighted statistics term.</summary>
    public double BScore() => R.Cost + (Mdl != null ? Tunables.WM * R.Mdl : 0);

    /// <summary>Child state after accepting the block last evaluated (result in R) with the given new insertions.</summary>
    public State MakeChild(State S, int w0, int[] newIns) { long t0 = Prof.Start(); var c = MakeChildCore(S, w0, newIns); Prof.Stop(2, t0); return c; }

    State MakeChildCore(State S, int w0, int[] newIns)
    {
        int bi = S.N % 6, mcu = S.N / 6, mx = mcu % J.Mx;
        var C = S.Clone();
        C.N = S.N + 1; C.BitPos = w0 * 8 + R.End;
        C.Cost = S.Cost + BScore() + Tunables.InsPen * newIns.Length;
        if (newIns.Length > 0)
        {
            C.K = S.K + newIns.Length; C.RLast = newIns[^1];
            var node = S.Ins;
            for (int i = 0; i < newIns.Length; i++) node = new InsNode { Bad = newIns[i] - S.K - i, Prev = node };
            C.Ins = node;
        }
        if (bi < 4)
        {
            C.PY = R.Pred;
            var mp = new byte[384]; if (bi != 0) Array.Copy(S.McuPix, mp, 256); Array.Copy(R.Pix, 0, mp, bi * 64, 64); C.McuPix = mp;
        }
        else if (bi == 4)
        {
            C.PCb = R.Pred; C.CurCb = R.Dc;
            var mp = (byte[])S.McuPix.Clone(); Array.Copy(R.Pix, 0, mp, 256, 64); C.McuPix = mp;
        }
        else
        {
            C.PCr = R.Pred;
            var mp = S.McuPix;
            var eb = (byte[])S.EdgeBottom.Clone(); var er = new byte[32]; int W16 = J.Mx * 16;
            for (int c = 0; c < 8; c++)
            {
                eb[mx * 16 + c] = mp[128 + 56 + c]; eb[mx * 16 + 8 + c] = mp[192 + 56 + c];
                eb[W16 + mx * 16 + c] = mp[128 + 48 + c]; eb[W16 + mx * 16 + 8 + c] = mp[192 + 48 + c];
            }
            for (int r = 0; r < 8; r++)
            {
                er[r] = mp[64 + r * 8 + 7]; er[8 + r] = mp[192 + r * 8 + 7];
                er[16 + r] = mp[64 + r * 8 + 6]; er[16 + 8 + r] = mp[192 + r * 8 + 6];
            }
            C.EdgeBottom = eb; C.EdgeRight = er;
            int CW = J.Mx * 8;
            var cebCb = (byte[])S.CebCb.Clone(); var cebCr = (byte[])S.CebCr.Clone(); var cerCb = new byte[16]; var cerCr = new byte[16];
            for (int c = 0; c < 8; c++)
            {
                cebCb[mx * 8 + c] = mp[256 + 56 + c]; cebCb[CW + mx * 8 + c] = mp[256 + 48 + c];
                cebCr[mx * 8 + c] = R.Pix[56 + c]; cebCr[CW + mx * 8 + c] = R.Pix[48 + c];
            }
            for (int r = 0; r < 8; r++)
            {
                cerCb[r] = mp[256 + r * 8 + 7]; cerCb[8 + r] = mp[256 + r * 8 + 6];
                cerCr[r] = R.Pix[r * 8 + 7]; cerCr[8 + r] = R.Pix[r * 8 + 6];
            }
            C.CebCb = cebCb; C.CebCr = cebCr; C.CerCb = cerCb; C.CerCr = cerCr;
            var ab = (int[])S.AboveCb.Clone(); ab[mx] = S.CurCb; C.AboveCb = ab; C.LeftCb = S.CurCb;
            var ar = (int[])S.AboveCr.Clone(); ar[mx] = R.Dc; C.AboveCr = ar; C.LeftCr = R.Dc;
        }
        return C;
    }

    public State InitialState() => new()
    {
        McuPix = new byte[384], EdgeBottom = new byte[J.Mx * 32], EdgeRight = new byte[32],
        CebCb = new byte[2 * J.Mx * 8], CebCr = new byte[2 * J.Mx * 8], CerCb = new byte[16], CerCr = new byte[16],
        AboveCb = new int[J.Mx], AboveCr = new int[J.Mx], RLast = -1,
    };
}
