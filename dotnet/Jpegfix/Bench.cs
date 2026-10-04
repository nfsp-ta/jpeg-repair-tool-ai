namespace Jpegfix;

/// <summary>
/// Benchmark: corrupt each clean JPEG in a directory (delete all 0x0D), repair it, and compare with the original.
/// The statistics model is trained leave-one-out on the other files (per size kind, i.e. file-name prefix before '-').
/// </summary>
static class Bench
{
    sealed class Row
    {
        public string Name = "", Kind = "", Outcome = "";
        public int Blocks, Reached, Truth, Found, Matched, MatchedOf;
        public double Seconds;
        public bool Identical;
    }

    public static int Run(string dir, int beam, double maxSeconds, string scope, string? kindFilter, int limit, int threads)
    {
        var files = Directory.GetFiles(dir, "*.jpg").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var items = new List<(string Name, string Kind, byte[] Good, JpegInfo J)>();
        foreach (var f in files)
        {
            var name = Path.GetFileName(f); var kind = name.Split('-')[0];
            if (kindFilter != null && kind != kindFilter) continue;
            var good = File.ReadAllBytes(f);
            try { items.Add((name, kind, good, JpegParser.Parse(good))); }
            catch (InvalidDataException ex) { Console.Error.WriteLine($"skip {name}: {ex.Message}"); }
        }
        if (limit > 0 && items.Count > limit)       // spread the sample evenly rather than taking the first N
            items = Enumerable.Range(0, limit).Select(i => items[(int)((long)i * items.Count / limit)]).ToList();
        Console.Error.WriteLine($"{items.Count} files, beam {beam}, model scope '{scope}', {maxSeconds}s limit, {threads} threads");

        // per-file statistics, plus per-group totals for leave-one-out
        var own = new Model[items.Count]; var groupTotal = new Dictionary<string, Model>();
        if (scope != "none")
            for (int i = 0; i < items.Count; i++)
            {
                own[i] = new Model(); Repairer.Train(items[i].Good, own[i]);
                var g = scope == "all" ? "" : items[i].Kind;
                if (!groupTotal.TryGetValue(g, out var t)) groupTotal[g] = t = new Model();
                t.Add(own[i]);
            }

        var rows = new Row[items.Count];
        Parallel.For(0, items.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
        {
            var (name, kind, good, J) = items[i];
            Model? model = null;
            if (scope != "none")
            {
                var gt = groupTotal[scope == "all" ? "" : kind];
                model = new Model(); model.Add(gt); model.Add(own[i], -1);
                if (model.Tot[0].Sum() + model.Tot[1].Sum() > 0) model.Refresh(); else model = null;
            }
            var bad = good.Where(b => b != 0x0D).ToArray();
            var truth = Repairer.TruthList(good);
            var row = new Row { Name = name, Kind = kind, Blocks = J.Blocks, Truth = truth.Count };
            try
            {
                var res = Repairer.Repair(bad, model, null, beam, 0, quiet: true, maxSeconds: maxSeconds);
                row.Seconds = res.Seconds; row.Found = res.List.Count;
                row.Reached = res.StuckAt >= 0 ? res.StuckAt : res.Total;
                var have = new HashSet<int>(res.List);
                int last = res.List.Count > 0 ? res.List[^1] : 0;
                var tt = truth.Where(x => x <= last).ToList();
                row.Matched = tt.Count(have.Contains); row.MatchedOf = tt.Count;
                row.Identical = Repairer.BuildOutput(bad, res).AsSpan().SequenceEqual(good);
                row.Outcome = row.Identical ? "IDENTICAL" : res.TimedOut ? "timeout" : res.StuckAt >= 0 ? "stuck" : res.Verified ? "consistent-wrong" : "inconsistent";
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException) { row.Outcome = "error: " + ex.Message; }
            rows[i] = row;
        });

        Console.WriteLine($"{"file",-18} {"blocks",6} {"reached",8} {"truth",5} {"found",5} {"match",9} {"sec",6}  outcome");
        foreach (var r in rows)
            Console.WriteLine($"{r.Name,-18} {r.Blocks,6} {100.0 * r.Reached / Math.Max(1, r.Blocks),7:F0}% {r.Truth,5} {r.Found,5} {r.Matched + "/" + r.MatchedOf,9} {r.Seconds,6:F1}  {r.Outcome}");
        Console.WriteLine();
        foreach (var g in rows.GroupBy(r => r.Kind))
        {
            var l = g.ToList();
            Console.WriteLine($"{g.Key,-8} n={l.Count,3}  identical={l.Count(r => r.Identical),3}  " +
                $"mean blocks reached={100.0 * l.Average(r => (double)r.Reached / Math.Max(1, r.Blocks)):F0}%  " +
                $"truth matched={l.Sum(r => r.Matched)}/{l.Sum(r => r.MatchedOf)}  time={l.Sum(r => r.Seconds):F0}s");
        }
        return 0;
    }
}
