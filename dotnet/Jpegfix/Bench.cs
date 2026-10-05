namespace Jpegfix;

/// <summary>
/// Benchmark: corrupt each clean JPEG in a directory (delete all 0x0D), repair it, and compare with the original.
/// The statistics model is trained leave-one-out (by image id) on the loaded files; --kind only selects which files are evaluated.
/// </summary>
static class Bench
{
    sealed class Row
    {
        public string Name = "", Kind = "", Outcome = "";
        public int Blocks, Reached, Truth, Found, Matched, MatchedOf;
        public double Prefix, GoodBlocks, CloseBlocks, Survive;      // fraction of blocks identical to the original: leading run / overall
        public double Seconds;
        public bool Identical;
    }

    // Oracle reference from the clean sibling size. REF_OFF=1 keeps the sibling-only file selection but disables the reference (for A/B runs on identical files).
    static float[]? MakeRef(List<Item> all, int i, string? refFrom) =>
        refFrom == null || Environment.GetEnvironmentVariable("REF_OFF") != null ? null : Repairer.BuildReference(all[Corpus.Sibling(all, i, refFrom)].Good, all[i].J);

    /// <summary>Scorer diagnostic over a directory: how often does the true hypothesis beat all wrong ones, per parameter set?</summary>
    public static int RunDiag(string dir, string scope, string? kindFilter, int limit, int threads, string? refFrom = null)
    {
        var all = Corpus.Load(dir);
        var ev = Corpus.Pick(all, kindFilter, limit, refFrom);
        var loo = new LooModels(all.Select(i => (i.Good, i.Kind, i.Id)).ToList(), scope);
        Console.Error.WriteLine($"{ev.Count} evaluated files of {all.Count} loaded (model scope '{scope}')");
        var total = new Repairer.DiagStats(); var lk = new object();
        Parallel.For(0, ev.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, k =>
        {
            int i = ev[k];
            var s = Repairer.Diagnose(all[i].Good, loo.For(i), MakeRef(all, i, refFrom));
            lock (lk) total.Add(s);
        });
        string[] cn = { "needs-insertion luma", "needs-insertion chroma", "no-insertion luma", "no-insertion chroma" };
        Console.WriteLine($"{total.Files} files; blocks followed: " + string.Join(", ", Enumerable.Range(0, 4).Select(c => cn[c] + "=" + total.Total[c])) +
            "; true path became unreachable: " + string.Join("/", total.Unreachable) + "; true block violating hard rules: " + total.TruthInvalid);
        int bf = 0;        // failure-penalty index used by the tables below (0 = the search's FAILPEN)
        double Pct(int c, int w, int l, int p) => total.Total[c] == 0 ? 0 : 100.0 * total.Wins[c, w, l, p, bf] / total.Total[c];
        double PctAll(int cs, int w, int l, int p) { long n = 0, k = 0; for (int c = 0; c < 4; c++) if (c / 2 == cs) { n += total.Total[c]; k += total.Wins[c, w, l, p, bf]; } return n == 0 ? 0 : 100.0 * k / n; }
        int bw = Array.IndexOf(Repairer.GridWm, Tunables.WM), bl = Array.IndexOf(Repairer.GridLook, Tunables.Look), bp = Array.IndexOf(Repairer.GridPen, Tunables.InsPen);
        Console.WriteLine("\ntrue hypothesis wins (%), per block class, at the search defaults (W_M=" + Tunables.WM + " LOOK=" + Tunables.Look + " INS_PEN=" + Tunables.InsPen + "):");
        if (bw >= 0 && bl >= 0 && bp >= 0) for (int c = 0; c < 4; c++) Console.WriteLine($"  {cn[c],-24} {Pct(c, bw, bl, bp),5:F1}");
        double Err(int w, int l, int p) { long e = 0; for (int c = 0; c < 4; c++) e += total.Total[c] - total.Wins[c, w, l, p, bf]; return (double)e / Math.Max(1, total.Files); }
        Console.WriteLine("\nINS_PEN sweep (W_M=" + Repairer.GridWm[bw] + ", LOOK=" + Repairer.GridLook[bl] + "): % of blocks where the truth wins; ins = blocks needing an insertion, none = blocks without; err = lost blocks per file");
        Console.WriteLine("  pen    ins   none    err");
        for (int p = 0; p < Repairer.GridPen.Length; p++) Console.WriteLine($"  {Repairer.GridPen[p],3}  {PctAll(0, bw, bl, p),5:F1}  {PctAll(1, bw, bl, p),5:F1}  {Err(bw, bl, p),5:F1}");
        Console.WriteLine("\nW_M x LOOK (INS_PEN=" + Repairer.GridPen[bp] + "): ins% / none% / err per file");
        Console.Write("  W_M  "); foreach (var l in Repairer.GridLook) Console.Write($"  LOOK={l,-13}"); Console.WriteLine();
        for (int w = 0; w < Repairer.GridWm.Length; w++)
        {
            Console.Write($"  {Repairer.GridWm[w],-4} ");
            for (int l = 0; l < Repairer.GridLook.Length; l++) Console.Write($"  {PctAll(0, w, l, bp),3:F0}/{PctAll(1, w, l, bp),-3:F0}/{Err(w, l, bp),-5:F1}  ");
            Console.WriteLine();
        }
        Console.WriteLine("\nlookahead failure penalty (W_M=" + Repairer.GridWm[bw] + ", INS_PEN=" + Repairer.GridPen[bp] + "; positive = per remaining lookahead block, negative = constant, 0 = none): LOOK 2 / 4 / 6 / 8 as ins% none% err");
        for (int f = 0; f < Repairer.GridFail.Length; f++)
        {
            bf = f; Console.Write($"  fail {Repairer.GridFail[f],4}: ");
            foreach (int lk2 in new[] { 1, 2, 3, 4 }) Console.Write($"  {PctAll(0, bw, lk2, bp),3:F0}/{PctAll(1, bw, lk2, bp),-3:F0}/{Err(bw, lk2, bp),-5:F1}");
            Console.WriteLine();
        }
        bf = 0;
        Console.WriteLine("\nbest (W_M, LOOK, INS_PEN) by errors per file:");
        var best = (from w in Enumerable.Range(0, Repairer.GridWm.Length) from l in Enumerable.Range(0, Repairer.GridLook.Length) from p in Enumerable.Range(0, Repairer.GridPen.Length) orderby Err(w, l, p) select (w, l, p)).Take(5);
        foreach (var (w, l, p) in best) Console.WriteLine($"  W_M={Repairer.GridWm[w]} LOOK={Repairer.GridLook[l]} INS_PEN={Repairer.GridPen[p]}: err/file {Err(w, l, p):F1}, ins {PctAll(0, w, l, p):F1}%, none {PctAll(1, w, l, p):F1}%");
        return 0;
    }

    public static int Run(string dir, int beam, double maxSeconds, string scope, string? kindFilter, int limit, int threads, string? refFrom = null)
    {
        var all = Corpus.Load(dir);
        var ev = Corpus.Pick(all, kindFilter, limit, refFrom);
        Console.Error.WriteLine($"{ev.Count} evaluated files of {all.Count} loaded, beam {beam}, model scope '{scope}', {maxSeconds}s limit, {threads} threads");
        var loo = new LooModels(all.Select(i => (i.Good, i.Kind, i.Id)).ToList(), scope);

        var rows = new Row[ev.Count];
        Parallel.For(0, ev.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, k =>
        {
            int i = ev[k];
            var (name, kind, _, good, J) = all[i];
            var model = loo.For(i);
            var bad = good.Where(b => b != 0x0D).ToArray();
            var truth = Repairer.TruthList(good);
            var row = new Row { Name = name, Kind = kind, Blocks = J.Blocks, Truth = truth.Count };
            try
            {
                float[]? rf = MakeRef(all, i, refFrom);
                var res = Repairer.Repair(bad, model, rf, beam, 0, quiet: true, maxSeconds: maxSeconds, truthKeys: Repairer.TruthKeys(good));
                row.Survive = res.LostAt >= 0 ? (double)res.LostAt / J.Blocks : (res.StuckAt >= 0 ? (double)res.StuckAt / J.Blocks : 1.0);
                row.Seconds = res.Seconds; row.Found = res.List.Count;
                row.Reached = res.StuckAt >= 0 ? res.StuckAt : res.Total;
                var have = new HashSet<int>(res.List);
                int last = res.List.Count > 0 ? res.List[^1] : 0;
                var tt = truth.Where(x => x <= last).ToList();
                row.Matched = tt.Count(have.Contains); row.MatchedOf = tt.Count;
                var outBytes = Repairer.BuildOutput(bad, res);
                row.Identical = outBytes.AsSpan().SequenceEqual(good);
                Repairer.CompareBlocks(outBytes, good, out int pre, out int eq, out int tot, out int cl);
                row.Prefix = (double)pre / tot; row.GoodBlocks = (double)eq / tot; row.CloseBlocks = (double)cl / tot;
                row.Outcome = row.Identical ? "IDENTICAL" : res.TimedOut ? "timeout" : res.StuckAt >= 0 ? "stuck" : res.Verified ? "consistent-wrong" : "inconsistent";
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException) { row.Outcome = "error: " + ex.Message; }
            rows[k] = row;
        });

        Console.WriteLine($"{"file",-18} {"blocks",6} {"reached",8} {"truth",5} {"found",5} {"match",9} {"sec",6}  outcome");
        foreach (var r in rows)
            Console.WriteLine($"{r.Name,-18} {r.Blocks,6} {100.0 * r.Reached / Math.Max(1, r.Blocks),7:F0}% {r.Truth,5} {r.Found,5} {r.Matched + "/" + r.MatchedOf,9} {r.Seconds,6:F1}  {r.Outcome}");
        Console.WriteLine();
        foreach (var g in rows.GroupBy(r => r.Kind))
        {
            var l = g.ToList();
            Console.WriteLine($"{g.Key,-8} n={l.Count,3}  identical={l.Count(r => r.Identical),3}  " +
                $"mean blocks reached={100.0 * l.Average(r => (double)r.Reached / Math.Max(1, r.Blocks)):F0}%  true state kept to {100.0 * l.Average(r => r.Survive):F0}%  visually close blocks={100.0 * l.Average(r => r.CloseBlocks):F0}% (identical {100.0 * l.Average(r => r.GoodBlocks):F0}%, leading run {100.0 * l.Average(r => r.Prefix):F0}%)  " +
                $"truth matched={l.Sum(r => r.Matched)}/{l.Sum(r => r.MatchedOf)}  time={l.Sum(r => r.Seconds):F0}s");
        }
        return 0;
    }
}
