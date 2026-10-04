namespace Jpegfix;

sealed class InsNode
{
    public int Bad;            // index in the unstuffed *bad* stream before which a 0x0D is inserted
    public InsNode? Prev;
}

/// <summary>One beam state. Arrays are never mutated after a state is created (children copy on write).</summary>
sealed class State
{
    public int N, BitPos, K, RLast;
    public double Rank, Cost;
    public int PY, PCb, PCr;
    public InsNode? Ins;
    public byte[] McuPix = null!, EdgeBottom = null!, EdgeRight = null!;
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

    readonly int[] coef = new int[64];
    readonly byte[] sz = new byte[64];
    readonly double[] tmp = new double[64];
    readonly double[] T0 = new double[8], T1 = new double[8], L0 = new double[8], L1 = new double[8];
    double over;
    static readonly double[] M = BuildM();

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

    void Idct()
    {
        for (int v = 0; v < 8; v++) for (int x = 0; x < 8; x++)
        {
            double s = 0; for (int u = 0; u < 8; u++) s += coef[v * 8 + u] * M[x * 8 + u]; tmp[v * 8 + x] = s;
        }
        double ov = 0;
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
        {
            double s = 128; for (int v = 0; v < 8; v++) s += M[y * 8 + v] * tmp[v * 8 + x];
            if (s < -20) ov += -20 - s; else if (s > 275) ov += s - 275;
            R.Pix[y * 8 + x] = (byte)(s < 0 ? 0 : s > 255 ? 255 : (int)(s + 0.5));
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
    public bool EvalBlock(State S, byte[] w, int s0, int limitRel)
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
        coef[0] = dc;
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
            coef[JpegParser.ZZ[k]] = dq; sz[k] = (byte)s; k++;
        }
        if (bp > limitRel || bp > Tunables.Win * 8) return Fail(bp);

        // ---- cost: how well does this block continue its neighbours?
        int mcu = S.N / 6, mx = mcu % J.Mx, my = mcu / J.Mx;
        double sum = 0, cost; int cnt = 0;
        if (bi < 4)
        {
            Idct();
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
            cost = cnt != 0 ? sum / cnt / 8 : 0;
        }
        if (RefY != null)
        {
            if (bi < 4) { int bx = mx * 2 + (bi & 1), by = my * 2 + (bi >> 1); cost += Tunables.RefW * Math.Min(60, Math.Abs(dc / 8.0 + 128 - RefY[by * J.Mx * 2 + bx])); }
            else cost += Tunables.RefW * Math.Min(60, Math.Abs(dc / 8.0 + 128 - (bi == 4 ? RefCb! : RefCr!)[my * J.Mx + mx]));
        }
        R.Cost = cost > Tunables.Cap ? Tunables.Cap : cost;
        R.End = bp; R.Pred = pred; R.Dc = dc; R.DcBits = dcBits;
        R.Mdl = Mdl != null ? Mdl.Bits(ci, sz) - (bp - s0 - dcBits) : 0;
        return R.Ok = true;
    }

    /// <summary>Score of the block last evaluated: cost plus weighted statistics term.</summary>
    public double BScore() => R.Cost + (Mdl != null ? Tunables.WM * R.Mdl : 0);

    /// <summary>Child state after accepting the block last evaluated (result in R) with the given new insertions.</summary>
    public State MakeChild(State S, int w0, int[] newIns)
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
            var mp = new byte[256]; if (bi != 0) Array.Copy(S.McuPix, mp, 256); Array.Copy(R.Pix, 0, mp, bi * 64, 64); C.McuPix = mp;
        }
        else if (bi == 4) { C.PCb = R.Pred; C.CurCb = R.Dc; }
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
            var ab = (int[])S.AboveCb.Clone(); ab[mx] = S.CurCb; C.AboveCb = ab; C.LeftCb = S.CurCb;
            var ar = (int[])S.AboveCr.Clone(); ar[mx] = R.Dc; C.AboveCr = ar; C.LeftCr = R.Dc;
        }
        return C;
    }

    public State InitialState() => new()
    {
        McuPix = new byte[256], EdgeBottom = new byte[J.Mx * 32], EdgeRight = new byte[32],
        AboveCb = new int[J.Mx], AboveCr = new int[J.Mx], RLast = -1,
    };
}
