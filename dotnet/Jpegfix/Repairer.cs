namespace Jpegfix;

sealed class RepairResult
{
    public JpegInfo J = null!;
    public byte[] Bad = null!;
    public int RawEnd;
    public List<int> List = new();
    public bool Verified;
    public int StuckAt = -1;
    public bool TimedOut;
    public double Seconds;
    public int Total;
}

/// <summary>Beam search over blocks, hypothesising a deleted 0x0D at every byte offset a block spans.</summary>
sealed partial class Repairer
{
    const int Win = Tunables.Win;
    readonly BlockDecoder dec;
    readonly JpegInfo J;
    readonly byte[] bad;
    readonly byte[] chainWin = new byte[Win + 8];

    Repairer(BlockDecoder d, byte[] badStream) { dec = d; J = d.J; bad = badStream; }

    void FillWindow(State S, byte[] outW)
    {
        int w0 = S.BitPos >> 3;
        for (int t = 0; t < Win + 8; t++)
        {
            int i = w0 + t;
            if (i == S.RLast) outW[t] = 0x0D;
            else { int b = i - S.K; outW[t] = b >= 0 && b < bad.Length ? bad[b] : (byte)0; }
        }
    }

    // Having just evaluated block S.N (result in dec.R), greedily decode the next L blocks (no further insertions)
    // and return the summed score. An undecodable block costs FailPen instead of killing the candidate.
    double LookFrom(State C, int L, double[]? costs = null, double[]? mdls = null, int[]? okCount = null)
    {
        double sum = 0; var child = C; int ok = 0; bool failed = false;
        for (int j = 0; j < L && child.N < J.Blocks; j++)
        {
            FillWindow(child, chainWin);
            int lim = 8 * (bad.Length + child.K) - 8 * (child.BitPos >> 3);
            if (!dec.EvalBlock(child, chainWin, child.BitPos & 7, lim)) { sum += Tunables.FailPen * (L - j); failed = true; break; }
            sum += dec.BScore();
            if (costs != null) { costs[j] = dec.R.Cost; mdls![j] = dec.R.Mdl; }
            ok++;
            child = dec.MakeChild(child, child.BitPos >> 3, Array.Empty<int>());
        }
        if (okCount != null) { okCount[0] = ok; okCount[1] = failed ? 1 : 0; }
        return sum;
    }

    // decoder-equivalent state: same future decoding
    static (int, int, int, int, int) Key(State S) =>
        (S.BitPos - 8 * S.K, (S.BitPos >> 3) == S.RLast ? (S.BitPos & 7) + 1 : 0, S.PY, S.PCb, S.PCr);

    static List<State> Prune(List<State> arr, int W)
    {
        var sorted = arr.OrderBy(s => s.Rank).ToList();      // stable, like JS Array.sort
        var seen = new HashSet<(int, int, int, int, int)>(); var outList = new List<State>();
        foreach (var S in sorted)
        {
            if (!seen.Add(Key(S))) continue;
            outList.Add(S);
            if (outList.Count >= W) break;
        }
        return outList;
    }

    public static RepairResult Repair(byte[] buf, Model? model, float[]? refData, int beamW, int maxBlocks, bool quiet = false, double maxSeconds = 0)
    {
        var J = JpegParser.Parse(buf);
        var bad = JpegParser.Unstuff(buf, J.ScanStart, out int rawEnd);
        var rp = new Repairer(new BlockDecoder(J, model, refData), bad);
        return rp.Run(rawEnd, beamW <= 0 ? 8 : beamW, maxBlocks, quiet, maxSeconds);
    }

    /// <summary>Decode state S under every insertion hypothesis (none, one byte at each offset, optionally two) and report each viable child via consider(S, windowStartByte, newInsertions). Block results are in dec.R during the callback.</summary>
    void Enumerate(State S, bool allowPairs, byte[] baseW, byte[] win2, Action<State, int, int[]> consider)
    {
        var R = dec.R;
        int w0 = S.BitPos >> 3, s0 = S.BitPos & 7;
        for (int t = 0; t < Win + 8; t++)
        {
            int i = w0 + t;
            if (i == S.RLast) baseW[t] = 0x0D;
            else { int b = i - S.K; baseW[t] = b >= 0 && b < bad.Length ? bad[b] : (byte)0; }
        }
        int limit0 = 8 * (bad.Length + S.K) - 8 * w0;
        double best = double.PositiveInfinity; int extent;
        if (dec.EvalBlock(S, baseW, s0, limit0)) { best = R.Cost; extent = R.End; consider(S, w0, Array.Empty<int>()); }
        else extent = R.Fail;
        int dmin = Math.Max(s0 > 0 ? 1 : 0, S.RLast == w0 ? 1 : 0);
        int dmax = Math.Min((extent + 7) / 8 + 1, Win - 16);
        for (int d = dmin; d <= dmax; d++)
        {
            Array.Copy(baseW, 0, win2, 0, d); win2[d] = 0x0D; Array.Copy(baseW, d, win2, d + 1, Win + 7 - d);
            if (!dec.EvalBlock(S, win2, s0, limit0 + 8)) continue;
            if (R.End <= 8 * d) continue;                       // insertion not reached: same as no insertion
            if (R.Cost + Tunables.InsPen < best) best = R.Cost + Tunables.InsPen;
            consider(S, w0, new[] { w0 + d });
        }
        // two missing bytes inside one block (only when nothing else looks good)
        if (allowPairs && best > Tunables.PairTrigger)
        {
            int hi = Math.Min(dmax + 6, dmin + Tunables.PairSpan);
            for (int d1 = dmin; d1 <= hi; d1++) for (int d2 = d1 + 1; d2 <= hi + 1; d2++)
            {
                Array.Copy(baseW, 0, win2, 0, d1); win2[d1] = 0x0D;
                Array.Copy(baseW, d1, win2, d1 + 1, d2 - 1 - d1); win2[d2] = 0x0D;
                Array.Copy(baseW, d2 - 1, win2, d2 + 1, Win + 7 - d2);
                if (!dec.EvalBlock(S, win2, s0, limit0 + 16)) continue;
                if (R.End <= 8 * d2) continue;
                consider(S, w0, new[] { w0 + d1, w0 + d2 });
            }
        }
    }

    RepairResult Run(int rawEnd, int beamW, int maxBlocks, bool quiet, double maxSeconds)
    {
        int total = Math.Min(J.Blocks, maxBlocks > 0 ? maxBlocks : J.Blocks);
        var R = dec.R;
        var baseW = new byte[Win + 8]; var win2 = new byte[Win + 8];
        var beam = new List<State> { dec.InitialState() };
        int stuckAt = -1; bool timedOut = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int n = 0; n < total; n++)
        {
            var next = new List<State>(); double cutoff = double.PositiveInfinity;
            void Consider(State S, int w0, int[] newIns)
            {
                var C = dec.MakeChild(S, w0, newIns);
                C.Rank = C.Cost + (Tunables.Look > 0 ? LookFrom(C, Tunables.Look) : 0);
                if (C.Rank >= cutoff) return;
                next.Add(C);
                if (next.Count >= beamW * 4) { next = Prune(next, beamW); cutoff = next[^1].Rank; }
            }

            for (int rank = 0; rank < beam.Count; rank++)
                Enumerate(beam[rank], rank < Tunables.PairRank, baseW, win2, Consider);

            next = Prune(next, beamW);
            if (next.Count == 0) { stuckAt = n; break; }
            beam = next;
            if (maxSeconds > 0 && sw.Elapsed.TotalSeconds > maxSeconds) { stuckAt = n + 1; timedOut = true; break; }
            if (!quiet && (n % 1500 == 0 || n == total - 1))
                Console.Error.WriteLine($"block {n + 1}/{total}  best cost {beam[0].Cost:F0}  insertions {beam[0].K}  ({sw.Elapsed.TotalSeconds:F1}s)");
        }

        // pick the winner: must end exactly at the end of the data
        State? winner = null;
        foreach (var S in beam)
        {
            int left = 8 * (bad.Length + S.K) - S.BitPos;       // unread bits (0..7 = padding)
            if (left >= 0 && left < 8 && (winner == null || S.Cost < winner.Cost)) winner = S;
        }
        bool verified = winner != null && stuckAt < 0 && total == J.Blocks;
        winner ??= beam[0];
        var list = new List<int>(); for (var nd = winner.Ins; nd != null; nd = nd.Prev) list.Add(nd.Bad); list.Reverse();
        return new RepairResult { J = J, Bad = bad, RawEnd = rawEnd, List = list, Verified = verified, StuckAt = stuckAt, TimedOut = timedOut, Seconds = sw.Elapsed.TotalSeconds, Total = total };
    }

    /// <summary>Train the statistics model on a clean JPEG by decoding it sequentially.</summary>
    public static void Train(byte[] buf, Model model)
    {
        var J = JpegParser.Parse(buf);
        var bad = JpegParser.Unstuff(buf, J.ScanStart, out _);
        var dec = new BlockDecoder(J, null, null);
        var S = dec.InitialState(); var w = new byte[Win + 8];
        var rp = new Repairer(dec, bad);
        for (int n = 0; n < J.Blocks; n++)
        {
            rp.FillWindow(S, w);
            int lim = 8 * bad.Length - 8 * (S.BitPos >> 3);
            if (!dec.EvalBlock(S, w, S.BitPos & 7, lim)) throw new InvalidDataException($"clean file does not decode at block {n}");
            model.Train(n % 6 < 4 ? 0 : 1, dec.Sizes);
            S = dec.MakeChild(S, S.BitPos >> 3, Array.Empty<int>());
        }
    }

    public static byte[] BuildOutput(byte[] buf, RepairResult res)
    {
        var list = res.List; var bad = res.Bad;
        var u = new byte[bad.Length + list.Count]; int o = 0, li = 0;
        for (int i = 0; i <= bad.Length; i++)
        {
            while (li < list.Count && list[li] == i) { u[o++] = 0x0D; li++; }
            if (i < bad.Length) u[o++] = bad[i];
        }
        var scan = JpegParser.Restuff(u);
        var tail = buf.AsSpan(res.RawEnd);
        var outBuf = new byte[res.J.ScanStart + scan.Length + tail.Length];
        Array.Copy(buf, outBuf, res.J.ScanStart);
        scan.CopyTo(outBuf, res.J.ScanStart);
        tail.CopyTo(outBuf.AsSpan(res.J.ScanStart + scan.Length));
        return outBuf;
    }

    /// <summary>Positions (in the unstuffed stream of the damaged file) where the clean file had 0x0D.</summary>
    public static List<int> TruthList(byte[] good)
    {
        var J = JpegParser.Parse(good); var data = JpegParser.Unstuff(good, J.ScanStart, out _);
        var t = new List<int>(); int c = 0;
        for (int i = 0; i < data.Length; i++) if (data[i] == 0x0D) { t.Add(i - c); c++; }
        return t;
    }
}
