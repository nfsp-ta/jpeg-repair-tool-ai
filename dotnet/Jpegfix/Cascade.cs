namespace Jpegfix;

/// <summary>
/// The default pipeline for real damaged files. The sizes of one picture are repaired smallest first; each repaired size is the
/// reference for the next larger one, both inside the beam search and for the row-shift / DC post-processing. Inputs are never
/// modified: every result goes to a new file in the output directory. Safe to run unattended for a long time: per-file errors are
/// logged and skipped, finished outputs are kept and skipped on a re-run (resume), results are written atomically.
/// </summary>
public static class Cascade
{
    static readonly string[] Kinds = { "thumb", "preview", "orig" };
    static readonly object Lock = new();

    public sealed class Options
    {
        public string OutDir = "", ModelsDir = "";
        public double MaxSeconds = 1800;      // per file; 0 = unlimited
        public int Beam = 8;
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

    /// <summary>Repair one picture: inputs are (display name, path) in any order; they are processed by pixel area, smallest first.</summary>
    static void RepairSet(string id, List<string> inputs, Options o, Dictionary<string, Model?> models, string report)
    {
        var items = new List<(string Path, string Kind, JpegInfo J, byte[] Bytes)>();
        foreach (var path in inputs)
        {
            try
            {
                var bytes = File.ReadAllBytes(path); var j = JpegParser.Parse(bytes);
                var stem = System.IO.Path.GetFileNameWithoutExtension(path); int dash = stem.IndexOf('-');
                var kind = dash > 0 && Kinds.Contains(stem[..dash]) ? stem[..dash] : stem;
                items.Add((path, kind, j, bytes));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or IndexOutOfRangeException)
            {
                Log($"{id}: skipping {path}: {ex.Message}");
                Append(report, $"{id}\t{System.IO.Path.GetFileName(path)}\tskipped\t{ex.Message}");
            }
        }
        items = items.OrderBy(i => (long)i.J.Width * i.J.Height).ToList();
        byte[]? prevOut = null;
        foreach (var it in items)
        {
            string name = $"{it.Kind}-{id}.jpg", outPath = System.IO.Path.Combine(o.OutDir, name);
            if (System.IO.Path.GetFullPath(outPath) == System.IO.Path.GetFullPath(it.Path)) { Log($"{id}: refusing to overwrite the input {it.Path}"); continue; }
            if (o.Resume && File.Exists(outPath)) { Log($"{id}: {name} already done, using it as the reference"); prevOut = File.ReadAllBytes(outPath); continue; }
            try
            {
                float[]? rf = null;
                if (prevOut != null)
                    try { rf = Repairer.BuildReference(prevOut, it.J); }
                    catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException) { Log($"{id}: reference for {name} unusable: {ex.Message}"); }
                models.TryGetValue(it.Kind, out var model);
                var bad = it.Bytes; var sw = System.Diagnostics.Stopwatch.StartNew();
                Log($"{id}: repairing {it.Path} ({it.J.Width}x{it.J.Height}, {it.J.Blocks} blocks){(rf != null ? ", with reference" : "")}");
                var res = Repairer.Repair(bad, model, rf, o.Beam, 0, quiet: true, maxSeconds: o.MaxSeconds);
                var repaired = Repairer.BuildOutput(bad, res);
                if (RowFix.Enabled) repaired = RowFix.Apply(repaired, rf);
                var tmp = outPath + ".part"; File.WriteAllBytes(tmp, repaired); File.Move(tmp, outPath, true);
                string status = res.TimedOut ? "timeout" : res.StuckAt >= 0 ? "stuck" : res.Verified ? "ok" : "inconsistent";
                Log($"{id}: {name} {status}, {res.List.Count} bytes inserted, {sw.Elapsed.TotalSeconds:F0}s");
                Append(report, $"{id}\t{name}\t{status}\tstuckAt={res.StuckAt}\tinserted={res.List.Count}\tblocks={res.J.Blocks}\tseconds={sw.Elapsed.TotalSeconds:F0}");
                prevOut = repaired;
            }
            catch (Exception ex)
            {
                Log($"{id}: {name} failed: {ex.GetType().Name}: {ex.Message}");
                Append(report, $"{id}\t{name}\terror\t{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    static void Append(string report, string line) { lock (Lock) File.AppendAllText(report, line + "\n"); }

    static Dictionary<string, Model?> LoadModels(Options o) => Kinds.ToDictionary(k => k, k => LoadModel(o, k));

    /// <summary>cascade --out DIR [--models DIR] [--max-seconds S] [--beam N] [--id NAME] file1.jpg file2.jpg ...: the files are sizes of ONE picture.</summary>
    public static int RunFiles(Options o, string id, List<string> files)
    {
        Directory.CreateDirectory(o.OutDir);
        RepairSet(id, files, o, LoadModels(o), System.IO.Path.Combine(o.OutDir, "report.tsv"));
        return 0;
    }

    /// <summary>cascade-dir IN_DIR --out DIR ...: files named thumb-ID.jpg, preview-ID.jpg, orig-ID.jpg are grouped by ID; pictures run in parallel.</summary>
    public static int RunDir(string inDir, Options o, int threads)
    {
        Directory.CreateDirectory(o.OutDir);
        var groups = Directory.GetFiles(inDir, "*.jpg").Select(f => (f, stem: System.IO.Path.GetFileNameWithoutExtension(f)))
            .Where(x => x.stem.IndexOf('-') > 0 && Kinds.Contains(x.stem[..x.stem.IndexOf('-')]))
            .GroupBy(x => x.stem[(x.stem.IndexOf('-') + 1)..]).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        Log($"{groups.Count} pictures in {inDir}, {threads} in parallel, {o.MaxSeconds}s per file, output {o.OutDir}");
        var models = LoadModels(o); var report = System.IO.Path.Combine(o.OutDir, "report.tsv");
        Parallel.ForEach(groups, new ParallelOptions { MaxDegreeOfParallelism = threads }, g => RepairSet(g.Key, g.Select(x => x.f).ToList(), o, models, report));
        Log("done");
        return 0;
    }

    public static Options Parse(string[] a, Func<string, string?> opt)
    {
        var o = new Options { OutDir = opt("out") ?? "", ModelsDir = opt("models") ?? "models" };
        if (double.TryParse(opt("max-seconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms)) o.MaxSeconds = ms;
        if (int.TryParse(opt("beam"), out var b) && b > 0) o.Beam = b;
        if (a.Contains("--no-resume")) o.Resume = false;
        return o;
    }
}
