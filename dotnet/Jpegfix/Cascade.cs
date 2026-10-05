namespace Jpegfix;

/// <summary>
/// The default pipeline for real damaged files. The sizes of one picture are repaired smallest first; each repaired size is the reference
/// for the next larger one, both inside the beam search and for the row-shift / DC post-processing. For the largest size the reference
/// combines the next smaller repaired size with the smallest one (which catches damage in the nearer one). Because the search is chaotic
/// (one lost lineage can flip a file from nearly perfect to garbage), each size is repaired with several variants (different reference
/// weight and reference source) and the result that agrees best with the smallest repaired size is kept (a quality measure that needs
/// no original). Inputs are never modified: every result goes to a new file in the output directory. Safe to run unattended for a long
/// time: per-file errors are logged and skipped, finished outputs are kept and skipped on a re-run (resume), results are written atomically.
/// </summary>
public static class Cascade
{
    static readonly string[] Kinds = { "thumb", "preview", "orig" };
    static readonly object Lock = new();

    public sealed class Options
    {
        public string OutDir = "", ModelsDir = "";
        public double MaxSeconds = 1800;      // per variant; 0 = unlimited
        public int Beam = 8;
        public int Variants = 2;              // repair variants per size (the best by agreement with the smallest size is kept)
        public bool Resume = true;
    }

    static void Log(string msg) { lock (Lock) Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {msg}"); }

    static Model? LoadModel(Options o, string kind)
    {
        var p = Path.Combine(o.ModelsDir, kind + ".json");
        if (o.ModelsDir != "" && File.Exists(p)) return Model.Load(p);
        Log($"warning: no statistics model for '{kind}' ({p}); repairs will be much weaker. Build models with tools/make-models.sh");
        return null;
    }

    /// <summary>How well a repaired image agrees with the smallest repaired size (mean block-mean error, luma + half the chroma; lower is better), or the seam cost if there is no usable smallest size.</summary>
    static double Score(byte[] repaired, byte[]? anchor, JpegInfo j)
    {
        try
        {
            if (anchor != null)
            {
                var (l, c, _) = Repairer.ReferenceError(Repairer.BuildReference(anchor, j), repaired);
                return l + 0.5 * c;
            }
            return Quality.Measure(Planes.Render(repaired)).seam;
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException) { return double.PositiveInfinity; }
    }

    /// <summary>The variants to try for one size: (name, reference, reference weight).</summary>
    static List<(string Name, float[]? Ref, double RefW)> VariantsFor(byte[]? prevOut, byte[]? anchorOut, JpegInfo j, Options o, string id)
    {
        var list = new List<(string, float[]?, double)>();
        if (prevOut == null) { list.Add(("no-reference", null, 0)); return list; }
        float[]? rN = null, rC = null;
        try { rN = Repairer.BuildReference(prevOut, j); } catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException) { }
        if (anchorOut != null && !ReferenceEquals(anchorOut, prevOut) && Environment.GetEnvironmentVariable("REF_COMBINE") != "0")
        {
            try { rC = Repairer.BuildCombinedReference(prevOut, anchorOut, j, out double fb); Log($"{id}: combined reference: {100 * fb:F0}% of blocks taken from the smallest size"); }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException) { }
        }
        var first = rC ?? rN;
        if (first == null) { list.Add(("no-reference", null, 0)); return list; }
        string fn = rC != null ? "combined" : "nearest";
        list.Add(($"{fn} w2", first, 2));
        if (rC != null && rN != null) list.Add(("nearest w2", rN, 2));
        list.Add(($"{fn} w1", first, 1));
        list.Add(($"{fn} w4", first, 4));
        if (rC != null && rN != null) list.Add(("nearest w4", rN, 4));
        return list.Take(Math.Max(1, o.Variants)).ToList();
    }

    /// <summary>Repair one picture: inputs are paths in any order; they are processed by pixel area, smallest first.</summary>
    static void RepairSet(string id, List<(string Kind, string Path, string OutPath)> jobs, Options o, Dictionary<string, Model?> models, string report)
    {
        var items = new List<(string Path, string Kind, JpegInfo J, byte[] Bytes, string OutPath)>();
        foreach (var (kind, path, outP) in jobs)
        {
            try
            {
                var bytes = File.ReadAllBytes(path); var j = JpegParser.Parse(bytes);
                items.Add((path, kind, j, bytes, outP));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or IndexOutOfRangeException)
            {
                Log($"{id}: skipping {path}: {ex.Message}");
                Append(report, $"{id}\t{System.IO.Path.GetFileName(path)}\tskipped\t{ex.Message}");
            }
        }
        items = items.OrderBy(i => (long)i.J.Width * i.J.Height).ToList();
        byte[]? prevOut = null, anchorOut = null;      // the next smaller repaired size, and the smallest repaired size
        foreach (var it in items)
        {
            string outPath = it.OutPath, name = System.IO.Path.GetFileName(outPath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outPath))!);
            if (System.IO.Path.GetFullPath(outPath) == System.IO.Path.GetFullPath(it.Path)) { Log($"{id}: refusing to overwrite the input {it.Path}"); continue; }
            if (o.Resume && File.Exists(outPath)) { Log($"{id}: {name} already done, using it as the reference"); prevOut = File.ReadAllBytes(outPath); anchorOut ??= prevOut; continue; }
            try
            {
                models.TryGetValue(it.Kind, out var model);
                var variants = VariantsFor(prevOut, anchorOut, it.J, o, id);
                Log($"{id}: repairing {it.Path} ({it.J.Width}x{it.J.Height}, {it.J.Blocks} blocks), {variants.Count} variant(s)");
                byte[]? best = null; double bestScore = double.PositiveInfinity; string bestName = "", bestStatus = ""; int bestInserted = 0, bestStuck = -1; double totalSec = 0;
                var scores = new List<string>();
                foreach (var (vname, rf, rw) in variants)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var res = Repairer.Repair(it.Bytes, model, rf, o.Beam, 0, quiet: true, maxSeconds: o.MaxSeconds, refWeight: rw);
                    var repaired = Repairer.BuildOutput(it.Bytes, res);
                    if (RowFix.Enabled) repaired = RowFix.Apply(repaired, rf);
                    if (Environment.GetEnvironmentVariable("CASCADE_KEEP") is string keepDir) { Directory.CreateDirectory(keepDir); File.WriteAllBytes(System.IO.Path.Combine(keepDir, $"{it.Kind}-{id}.{vname.Replace(' ', '_')}.jpg"), repaired); }      // analysis only
                    string status = res.TimedOut ? "timeout" : res.StuckAt >= 0 ? "stuck" : res.Verified ? "ok" : "inconsistent";
                    double sc = prevOut == null ? 0 : Score(repaired, anchorOut, it.J);
                    totalSec += sw.Elapsed.TotalSeconds; scores.Add($"{vname}={sc:F2}({status})");
                    Log($"{id}: {name} variant '{vname}': {status}, {res.List.Count} bytes inserted, deviation from the smallest size {sc:F2}, {sw.Elapsed.TotalSeconds:F0}s");
                    if (best == null || sc < bestScore) { best = repaired; bestScore = sc; bestName = vname; bestStatus = status; bestInserted = res.List.Count; bestStuck = res.StuckAt; }
                }
                var tmp = outPath + ".part"; File.WriteAllBytes(tmp, best!); File.Move(tmp, outPath, true);
                Log($"{id}: {name} done: kept variant '{bestName}' ({bestStatus})");
                Append(report, $"{id}\t{name}\t{bestStatus}\tvariant={bestName}\tstuckAt={bestStuck}\tinserted={bestInserted}\tblocks={it.J.Blocks}\tseconds={totalSec:F0}\tscores: {string.Join(" ", scores)}");
                prevOut = best; anchorOut ??= best;
            }
            catch (Exception ex)
            {
                Log($"{id}: {name} failed: {ex.GetType().Name}: {ex.Message}");
                Append(report, $"{id}\t{name}\terror\t{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    static string KindOf(string path)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(path); int dash = stem.IndexOf('-');
        return dash > 0 && Kinds.Contains(stem[..dash]) ? stem[..dash] : stem;
    }

    /// <summary>cascade-gallery ROOT --out OUT: the forum gallery layout (see Gallery.cs). Each picture's files are repaired as one cascade; results go to OUT mirroring the layout, named like the input plus .jpg.</summary>
    public static int RunGallery(string root, Options o, int threads, int limit)
    {
        Directory.CreateDirectory(o.OutDir);
        var skipped = new List<string>(); var groups = Gallery.Scan(root, skipped);
        if (limit > 0 && groups.Count > limit) groups = Enumerable.Range(0, limit).Select(i => groups[(int)((long)i * groups.Count / limit)]).ToList();     // evenly spread sample
        Log($"{groups.Count} pictures from {root}, {threads} in parallel, {o.Variants} variant(s) per size, {o.MaxSeconds}s per variant, output {o.OutDir}");
        var models = LoadModels(o); var report = System.IO.Path.Combine(o.OutDir, "report.tsv"); string full = System.IO.Path.GetFullPath(root).TrimEnd('/');
        Parallel.ForEach(groups, new ParallelOptions { MaxDegreeOfParallelism = threads }, g =>
        {
            var jobs = g.Members.Select(m => (m.Kind, m.Path, System.IO.Path.Combine(o.OutDir, System.IO.Path.GetRelativePath(full, m.Path) + ".jpg"))).ToList();
            RepairSet(System.IO.Path.GetRelativePath(full, g.Dir) + "/" + g.Key, jobs, o, models, report);
        });
        Log("done");
        return 0;
    }

    static void Append(string report, string line) { lock (Lock) File.AppendAllText(report, line + "\n"); }

    static Dictionary<string, Model?> LoadModels(Options o) => Kinds.ToDictionary(k => k, k => LoadModel(o, k));

    /// <summary>cascade --out DIR [--models DIR] [--max-seconds S] [--variants N] [--beam N] [--id NAME] file1.jpg file2.jpg ...: the files are sizes of ONE picture.</summary>
    public static int RunFiles(Options o, string id, List<string> files)
    {
        Directory.CreateDirectory(o.OutDir);
        RepairSet(id, files.Select(f => (KindOf(f), f, System.IO.Path.Combine(o.OutDir, System.IO.Path.GetFileName(f)))).ToList(), o, LoadModels(o), System.IO.Path.Combine(o.OutDir, "report.tsv"));
        return 0;
    }

    /// <summary>cascade-dir IN_DIR --out DIR ...: files named thumb-ID.jpg, preview-ID.jpg, orig-ID.jpg are grouped by ID; pictures run in parallel.</summary>
    public static int RunDir(string inDir, Options o, int threads)
    {
        Directory.CreateDirectory(o.OutDir);
        var groups = Directory.GetFiles(inDir, "*.jpg").Select(f => (f, stem: System.IO.Path.GetFileNameWithoutExtension(f)))
            .Where(x => x.stem.IndexOf('-') > 0 && Kinds.Contains(x.stem[..x.stem.IndexOf('-')]))
            .GroupBy(x => x.stem[(x.stem.IndexOf('-') + 1)..]).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        Log($"{groups.Count} pictures in {inDir}, {threads} in parallel, {o.Variants} variant(s) per size, {o.MaxSeconds}s per variant, output {o.OutDir}");
        var models = LoadModels(o); var report = System.IO.Path.Combine(o.OutDir, "report.tsv");
        Parallel.ForEach(groups, new ParallelOptions { MaxDegreeOfParallelism = threads }, g => RepairSet(g.Key, g.Select(x => (KindOf(x.f), x.f, System.IO.Path.Combine(o.OutDir, System.IO.Path.GetFileName(x.f)))).ToList(), o, models, report));
        Log("done");
        return 0;
    }

    public static Options Parse(string[] a, Func<string, string?> opt)
    {
        var o = new Options { OutDir = opt("out") ?? "", ModelsDir = opt("models") ?? "models" };
        if (double.TryParse(opt("max-seconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms)) o.MaxSeconds = ms;
        if (int.TryParse(opt("beam"), out var b) && b > 0) o.Beam = b;
        if (int.TryParse(opt("variants"), out var v) && v > 0) o.Variants = v;
        if (a.Contains("--no-resume")) o.Resume = false;
        return o;
    }
}
