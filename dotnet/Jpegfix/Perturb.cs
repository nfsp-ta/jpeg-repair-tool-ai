namespace Jpegfix;

/// <summary>
/// Stage-2 oracle experiment. Start from the TRUE insertion list of a clean file, apply one edit (move an insertion by d bytes,
/// drop it, or add a spurious one) and ask whether a window score over the following blocks still prefers the truth.
/// The window score is the sum of the search's own block scores (cost + W_M x statistics term). A window of 2 blocks is what
/// stage 1 sees (LOOK=2); a window of a whole MCU row or more also contains the row below, i.e. the context stage 2 would add.
/// </summary>
static class Perturb
{
    static readonly string[] Types = { "move 1", "move 3", "move 10", "move 40", "drop", "add 3", "add 10", "add 40" };
    static readonly string[] WinNames = { "2 blocks", "12 blocks", "1 MCU row", "2 MCU rows" };

    sealed class Stats
    {
        public long[] Total = new long[Types.Length], Invalid = new long[Types.Length];
        public long[,] WinPen = new long[Types.Length, 4], WinNoPen = new long[Types.Length, 4], VTotal = new long[Types.Length, 4], VPen = new long[Types.Length, 4], VNoPen = new long[Types.Length, 4];
        public int Files;
        public void Add(Stats o)
        {
            Files += o.Files;
            for (int t = 0; t < Types.Length; t++)
            {
                Total[t] += o.Total[t]; Invalid[t] += o.Invalid[t];
                for (int w = 0; w < 4; w++) { WinPen[t, w] += o.WinPen[t, w]; WinNoPen[t, w] += o.WinNoPen[t, w]; VTotal[t, w] += o.VTotal[t, w]; VPen[t, w] += o.VPen[t, w]; VNoPen[t, w] += o.VNoPen[t, w]; }
            }
        }
    }

    /// <summary>The repaired stream for an insertion list (sorted bad-stream indices; a 0x0D goes before bad[index]).</summary>
    internal static byte[] Build(byte[] bad, List<int> ins)
    {
        var rep = new byte[bad.Length + ins.Count + Tunables.Win + 16];
        int o = 0, k = 0;
        for (int i = 0; i <= bad.Length; i++)
        {
            while (k < ins.Count && ins[k] == i) { rep[o++] = 0x0D; k++; }
            if (i < bad.Length) rep[o++] = bad[i];
        }
        Array.Resize(ref rep, o + Tunables.Win + 16);
        return rep;
    }

    /// <summary>Per-block scores of the stream; stops at <paramref name="stopAt"/> blocks, at the end, or at the first block that does not decode (failedAt).</summary>
    static double[] Decode(BlockDecoder dec, byte[] rep, int dataLen, int stopAt, double[]? same, out int failedAt, out int firstDiff, int extra)
    {
        int N = dec.J.Blocks;
        var sc = new double[N]; var w = new byte[Tunables.Win + 8];
        var S = dec.InitialState();
        failedAt = -1; firstDiff = -1;
        for (int n = 0; n < Math.Min(N, stopAt); n++)
        {
            int w0 = S.BitPos >> 3;
            Array.Copy(rep, w0, w, 0, Math.Min(w.Length, rep.Length - w0));
            if (!dec.EvalBlock(S, w, S.BitPos & 7, 8 * dataLen - 8 * w0)) { failedAt = n; return sc; }
            sc[n] = dec.BScore();
            if (same != null && firstDiff < 0 && sc[n] != same[n]) { firstDiff = n; stopAt = Math.Min(stopAt, n + extra + 1); }
            S = dec.MakeChild(S, w0, Array.Empty<int>());
        }
        return sc;
    }

    static Stats One(byte[] good, Model? model)
    {
        var st = new Stats { Files = 1 };
        var J = JpegParser.Parse(good);
        var goodU = JpegParser.Unstuff(good, J.ScanStart, out _);
        var bad = goodU.Where(b => b != 0x0D).ToArray();
        var truth = new List<int>();
        for (int i = 0, z = 0; i < goodU.Length; i++) { if (goodU[i] == 0x0D) { truth.Add(i - z); z++; } }
        if (truth.Count == 0) return st;
        var dec = new BlockDecoder(J, model, null);
        int row = J.Mx * 6, N = J.Blocks;
        int[] W = { 2, 12, row, 2 * row };
        var tRep = Build(bad, truth);
        var tSc = Decode(dec, tRep, goodU.Length, N, null, out int tFail, out _, 0);
        if (tFail >= 0) return st;
        var tCum = new double[N + 1]; for (int n = 0; n < N; n++) tCum[n + 1] = tCum[n] + tSc[n];

        var rng = new Random(truth.Count * 7919 + N);
        int picks = Math.Min(6, truth.Count);
        for (int pk = 0; pk < picks; pk++)
        {
            int ti = (int)((long)(2 * pk + 1) * truth.Count / (2 * picks));
            for (int t = 0; t < Types.Length; t++)
            {
                var list = new List<int>(truth);
                int sign = rng.Next(2) == 0 ? -1 : 1;
                int d = t switch { 0 => 1, 1 => 3, 2 => 10, 3 => 40, 5 => 3, 6 => 10, 7 => 40, _ => 0 } * sign;
                int p = truth[ti];
                if (t <= 3) { int np = p + d; if (np < 0 || np > bad.Length) { np = p - d; if (np < 0 || np > bad.Length) continue; } list[ti] = np; }
                else if (t == 4) list.RemoveAt(ti);
                else { int np = p + d; if (np < 0 || np > bad.Length) { np = p - d; if (np < 0 || np > bad.Length) continue; } list.Add(np); }
                list.Sort();
                var rep = Build(bad, list);
                var pSc = Decode(dec, rep, bad.Length + list.Count, N, tSc, out int pFail, out int d0, 2 * row + 2);
                if (d0 < 0 && pFail < 0) continue;                      // edit changed nothing observable
                if (d0 < 0) d0 = pFail;                                  // failed before any score differed
                st.Total[t]++;
                int penDiff = list.Count - truth.Count;               // extra insertions the perturbed list pays for
                bool invAny = false;
                for (int wi = 0; wi < 4; wi++)
                {
                    int e = Math.Min(N - 1, d0 + W[wi]);
                    bool inv = pFail >= 0 && pFail <= e;
                    if (wi == 2) invAny = inv;
                    double tS = tCum[e + 1] - tCum[d0], pS = 0;
                    if (!inv) { for (int n = d0; n <= e; n++) pS += pSc[n]; }
                    if (!inv) { st.VTotal[t, wi]++; if (tS < pS) st.VNoPen[t, wi]++; if (tS < pS + Tunables.InsPen * penDiff) st.VPen[t, wi]++; }
                    if (inv || tS < pS) st.WinNoPen[t, wi]++;
                    if (inv || tS < pS + Tunables.InsPen * penDiff) st.WinPen[t, wi]++;
                }
                if (invAny) st.Invalid[t]++;
            }
        }
        return st;
    }

    public static int Run(string dir, string scope, string? kindFilter, int limit, int threads)
    {
        var all = Corpus.Load(dir);
        var ev = Corpus.Pick(all, kindFilter, limit);
        var loo = new LooModels(all.Select(i => (i.Good, i.Kind, i.Id)).ToList(), scope);
        Console.Error.WriteLine($"{ev.Count} evaluated files of {all.Count} loaded");
        var total = new Stats(); var lk = new object();
        Parallel.For(0, ev.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, k =>
        {
            var s = One(all[ev[k]].Good, loo.For(ev[k]));
            lock (lk) total.Add(s);
        });
        Console.WriteLine($"{total.Files} files. % of edits where the TRUE list scores better than the edited list, by window after the first affected block.");
        Console.WriteLine("Cells: with INS_PEN charged / without. 'invalid' = edited decode hits a hard-rule failure within the 1-row window.");
        Console.WriteLine($"  {"edit",-9} {"n",5} " + string.Join(" ", WinNames.Select(x => $"{x,11}")) + "   invalid");
        for (int t = 0; t < Types.Length; t++)
        {
            long n = Math.Max(1, total.Total[t]);
            Console.WriteLine($"  {Types[t],-9} {total.Total[t],5} " + string.Join(" ", Enumerable.Range(0, 4).Select(w => $"{100.0 * total.WinPen[t, w] / n,4:F0}/{100.0 * total.WinNoPen[t, w] / n,-4:F0}   ")) + $"  {100.0 * total.Invalid[t] / n,4:F0}%");
        }
        Console.WriteLine("\nSame, but only edits whose decode stays valid through the window (the cases a hard rule does not catch); n per window in brackets for the 1-row window:");
        Console.WriteLine($"  {"edit",-9} {"n",5} " + string.Join(" ", WinNames.Select(x => $"{x,11}")));
        for (int t = 0; t < Types.Length; t++)
            Console.WriteLine($"  {Types[t],-9} {total.VTotal[t, 2],5} " + string.Join(" ", Enumerable.Range(0, 4).Select(w => $"{100.0 * total.VPen[t, w] / Math.Max(1, total.VTotal[t, w]),4:F0}/{100.0 * total.VNoPen[t, w] / Math.Max(1, total.VTotal[t, w]),-4:F0}   ")));
        return 0;
    }
}
