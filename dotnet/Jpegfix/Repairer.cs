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
    public int LostAt = -1;      // first block after which no beam state matched the true decoder state (needs truth keys); -1 = never lost / unknown
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

    Repairer(BlockDecoder d, byte[] badStream) { dec = d; J = d.J; bad = badStream; d.Filler = FillWindow; }

    void FillWindow(State S, byte[] outW)
    {
        int w0 = S.BitPos >> 3, n = Win + 8, b0 = w0 - S.K;
        int lo = Math.Max(0, -b0), hi = Math.Min(n, bad.Length - b0);          // window positions that map into the damaged stream
        Array.Clear(outW, 0, n);
        if (hi > lo) Array.Copy(bad, b0 + lo, outW, lo, hi - lo);
        int t = S.RLast - w0; if (t >= 0 && t < n) outW[t] = 0x0D;           // the byte inserted last
    }

    // Having just evaluated block S.N (result in dec.R), greedily decode the next L blocks (no further insertions)
    // and return the summed score. An undecodable block costs FailPen instead of killing the candidate.
    double LookFrom(State C, int L, double[]? costs = null, double[]? mdls = null, int[]? okCount = null) { long t0 = Prof.Start(); var r = LookFromCore(C, L, costs, mdls, okCount); Prof.Stop(3, t0); return r; }

    double LookFromCore(State C, int L, double[]? costs, double[]? mdls, int[]? okCount)
    {
        double sum = 0; var child = C; int ok = 0; bool failed = false;
        for (int j = 0; j < L && child.N < J.Blocks; j++)
        {
            if (Prof.On) Prof.Seen((child.N, child.BitPos - 8 * child.K, (child.BitPos >> 3) == child.RLast ? (child.BitPos & 7) + 1 : 0, child.PY, child.PCb, child.PCr));
            int lim = 8 * (bad.Length + child.K) - 8 * (child.BitPos >> 3);
            if (!dec.EvalBlock(child, null, child.BitPos & 7, lim, true)) { sum += Tunables.FailPen * (L - j); failed = true; break; }
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

    // selection score across parents
    static double Sel(State S) => Tunables.LookW == 1 ? S.Rank : S.Cost + Tunables.LookW * (S.Rank - S.Cost);

    static List<State> Prune(List<State> arr, int W) { long t0 = Prof.Start(); var r = PruneCore(arr, W); Prof.Stop(4, t0); return r; }

    static List<State> PruneCore(List<State> arr, int W)
    {
        IEnumerable<State> cand = arr.OrderBy(s => s.Rank);            // stable, like JS Array.sort
        if (Tunables.PerParent < arr.Count)
        {
            var perParent = new Dictionary<int, int>();
            cand = cand.Where(s => { perParent.TryGetValue(s.Origin, out int c); perParent[s.Origin] = c + 1; return c < Tunables.PerParent; }).ToList();
        }
        var sorted = (Tunables.LookW == 1 ? cand : cand.OrderBy(Sel)).ToList();
        var seen = new HashSet<(int, int, int, int, int)>(); var outList = new List<State>();
        foreach (var S in sorted)
        {
            if (!seen.Add(Key(S))) continue;
            if (outList.Count >= W && (Tunables.BeamDelta <= 0 || outList.Count >= Tunables.BeamMax || Sel(S) - Sel(outList[0]) > Tunables.BeamDelta)) break;
            outList.Add(S);
        }
        return outList;
    }

    public static RepairResult Repair(byte[] buf, Model? model, float[]? refData, int beamW, int maxBlocks, bool quiet = false, double maxSeconds = 0, List<(int, int, int, int, int)>? truthKeys = null)
    {
        var J = JpegParser.Parse(buf);
        var bad = JpegParser.Unstuff(buf, J.ScanStart, out int rawEnd);
        var rp = new Repairer(new BlockDecoder(J, model, refData), bad);
        var res = rp.Run(rawEnd, beamW <= 0 ? 8 : beamW, maxBlocks, quiet, maxSeconds, truthKeys);
        if (Tunables.Refine && maxBlocks <= 0) { var sw = System.Diagnostics.Stopwatch.StartNew(); var r2 = Refine(res, model, refData); r2.Seconds = res.Seconds + sw.Elapsed.TotalSeconds; r2.LostAt = res.LostAt; r2.TimedOut = res.TimedOut; res = r2; }
        return res;
    }

    /// <summary>Decode state S under every insertion hypothesis (none, one byte at each offset, optionally two) and report each viable child via consider(S, windowStartByte, newInsertions). Block results are in dec.R during the callback.</summary>
    void Enumerate(State S, bool allowPairs, byte[] baseW, byte[] win2, Action<State, int, int[]> consider) { long t0 = Prof.Start(); EnumerateCore(S, allowPairs, baseW, win2, consider); Prof.Stop(5, t0); }

    void EnumerateCore(State S, bool allowPairs, byte[] baseW, byte[] win2, Action<State, int, int[]> consider)
    {
        var R = dec.R;
        int w0 = S.BitPos >> 3, s0 = S.BitPos & 7;
        FillWindow(S, baseW);
        int limit0 = 8 * (bad.Length + S.K) - 8 * w0;
        double best = double.PositiveInfinity; int extent;
        if (dec.EvalBlock(S, baseW, s0, limit0, true)) { best = R.Cost; extent = R.End; consider(S, w0, Array.Empty<int>()); }
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

    RepairResult Run(int rawEnd, int beamW, int maxBlocks, bool quiet, double maxSeconds, List<(int, int, int, int, int)>? truthKeys)
    {
        int total = Math.Min(J.Blocks, maxBlocks > 0 ? maxBlocks : J.Blocks);
        var R = dec.R;
        var baseW = new byte[Win + 8]; var win2 = new byte[Win + 8];
        var beam = new List<State> { dec.InitialState() };
        int stuckAt = -1; bool timedOut = false; int lostAt = -1; bool lostDebug = Environment.GetEnvironmentVariable("LOST_DEBUG") != null;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int n = 0; n < total; n++)
        {
            var next = new List<State>(); double cutoff = double.PositiveInfinity;
            int bw = n < J.Mx * 6 * Tunables.FirstRows ? beamW * Tunables.FirstMul : beamW;
            var originOf = new Dictionary<State, int>(ReferenceEqualityComparer.Instance);
            for (int bi2 = 0; bi2 < beam.Count; bi2++) originOf[beam[bi2]] = bi2;
            var dbgAll = lostDebug && lostAt < 0 ? new List<(State C, int Ins)>() : null;
            void Consider(State S, int w0, int[] newIns)
            {
                long tc = Prof.Start();
                var C = dec.MakeChild(S, w0, newIns);
                C.Rank = C.Cost + (Tunables.Look > 0 ? LookFrom(C, Tunables.Look) : 0);
                C.Origin = originOf.TryGetValue(S, out int oi) ? oi : 0;
                dbgAll?.Add((C, newIns.Length));
                if (Sel(C) >= cutoff) return;
                next.Add(C);
                if (next.Count >= (Tunables.BeamDelta > 0 ? Tunables.BeamMax * 2 : bw * 4)) { next = Prune(next, bw); cutoff = Sel(next[^1]); }
                Prof.Stop(6, tc);
            }

            for (int rank = 0; rank < beam.Count; rank++)
                Enumerate(beam[rank], rank < Tunables.PairRank, baseW, win2, Consider);

            next = Prune(next, bw);
            if (next.Count == 0) { stuckAt = n; break; }
            beam = next;
            if (truthKeys != null && lostAt < 0 && n < truthKeys.Count && !beam.Any(s => Key(s) == truthKeys[n]))
            {
                lostAt = n + 1;
                if (dbgAll != null)
                {
                    var sorted = dbgAll.OrderBy(x => x.C.Rank).ToList(); int tr = sorted.FindIndex(x => Key(x.C) == truthKeys[n]);
                    var bst = sorted[0].C;
                    string extra = tr < 0 ? "" : $" Ktrue={sorted[tr].C.K} Kbest={bst.K} gapIns={Tunables.InsPen * (sorted[tr].C.K - bst.K):F0} gapEvidence={sorted[tr].C.Rank - bst.Rank - Tunables.InsPen * (sorted[tr].C.K - bst.K):F0} (accumulated {sorted[tr].C.Cost - bst.Cost - Tunables.InsPen * (sorted[tr].C.K - bst.K):F0}, lookahead {(sorted[tr].C.Rank - sorted[tr].C.Cost) - (bst.Rank - bst.Cost):F0})";
                    Console.Error.WriteLine($"LOST n={n} bi={n % 6} mcu={n / 6} cands={sorted.Count} trueRank={(tr < 0 ? "absent" : (tr + 1).ToString())}" + (tr >= 0 ? $" gap={sorted[tr].C.Rank - bst.Rank:F1}" : "") + extra);
                }
            }
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
        Prof.Report(sw.Elapsed.TotalSeconds);
        return new RepairResult { J = J, Bad = bad, RawEnd = rawEnd, List = list, Verified = verified, StuckAt = stuckAt, TimedOut = timedOut, LostAt = lostAt, Seconds = sw.Elapsed.TotalSeconds, Total = total };
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
