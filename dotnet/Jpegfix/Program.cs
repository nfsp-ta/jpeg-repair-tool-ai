using Jpegfix;

static string? Opt(string[] a, string name) { int i = Array.IndexOf(a, "--" + name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

static byte[] FloatBytes(float[] f) { var b = new byte[f.Length * 4]; Buffer.BlockCopy(f, 0, b, 0, b.Length); return b; }

static float[] ToFloats(byte[] b) { var f = new float[b.Length / 4]; Buffer.BlockCopy(b, 0, f, 0, f.Length * 4); return f; }

const string Usage = @"usage:
  jpegfix corrupt good.jpg bad.jpg
  jpegfix verify  a.jpg b.jpg
  jpegfix train   model.json clean1.jpg [clean2.jpg ...]
  jpegfix bench   dir [--beam N] [--max-seconds S] [--scope kind|all|none] [--kind thumb|preview|orig] [--limit N] [--threads N]
  jpegfix diag    dir [--scope kind|all|none] [--kind K] [--limit N] [--threads N] [--ref-from KIND]\n  jpegfix makeref sibling.jpg target.jpg out.bin     (reference for --ref from a clean/repaired sibling size)
  jpegfix repair  bad.jpg out.jpg [--beam N] [--model model.json] [--ref ref.bin] [--truth good.jpg] [--max-blocks N] [--max-seconds S] [--rowfix] [--sibling repaired-sibling.jpg]";

try
{
    if (args.Length == 0) { Console.Error.WriteLine(Usage); return 2; }
    switch (args[0])
    {
        case "corrupt" when args.Length >= 3:
            File.WriteAllBytes(args[2], File.ReadAllBytes(args[1]).Where(b => b != 0x0D).ToArray());
            return 0;
        case "verify" when args.Length >= 3:
            bool same = File.ReadAllBytes(args[1]).AsSpan().SequenceEqual(File.ReadAllBytes(args[2]));
            Console.WriteLine(same ? "IDENTICAL" : "DIFFERENT");
            return same ? 0 : 1;
        case "train" when args.Length >= 3:
            var m = new Model();
            foreach (var f in args.Skip(2))
            {
                try { Repairer.Train(File.ReadAllBytes(f), m); }
                catch (InvalidDataException ex) { Console.Error.WriteLine($"skip {f}: {ex.Message}"); }
            }
            m.Save(args[1]); Console.Error.WriteLine("model written to " + args[1]);
            return 0;
        case "bench" when args.Length >= 2:
            int.TryParse(Opt(args, "beam"), out int bb); double.TryParse(Opt(args, "max-seconds"), out double ms); int.TryParse(Opt(args, "limit"), out int lim);
            if (!int.TryParse(Opt(args, "threads"), out int th) || th < 1) th = Math.Max(1, Environment.ProcessorCount / 2);
            return Bench.Run(args[1], bb > 0 ? bb : 8, ms > 0 ? ms : 120, Opt(args, "scope") ?? "kind", Opt(args, "kind"), lim, th, Opt(args, "ref-from"));
        case "diag" when args.Length >= 2:
            int.TryParse(Opt(args, "limit"), out int dl);
            if (!int.TryParse(Opt(args, "threads"), out int dt) || dt < 1) dt = Math.Max(1, Environment.ProcessorCount / 2);
            return Bench.RunDiag(args[1], Opt(args, "scope") ?? "kind", Opt(args, "kind"), dl, dt, Opt(args, "ref-from"));
        case "perturb" when args.Length >= 2:
            int.TryParse(Opt(args, "limit"), out int pl);
            if (!int.TryParse(Opt(args, "threads"), out int pt) || pt < 1) pt = Math.Max(1, Environment.ProcessorCount / 2);
            return Perturb.Run(args[1], Opt(args, "scope") ?? "kind", Opt(args, "kind"), pl, pt);
        case "rowshift" when args.Length >= 4: return RowShift.Run(args[1], args[2], args[3]);
        case "reencode" when args.Length >= 3: File.WriteAllBytes(args[2], CoefImage.Rewrite(File.ReadAllBytes(args[1]), null)); return 0;
        case "rowfix" when args.Length >= 3: return RowFix.Run(args[1], args[2], args.Length > 3 && args[3] != "-" ? args[3] : null, args.Length > 4 ? args[4] : null);
        case "makeref" when args.Length >= 4:      // makeref sibling.jpg target.jpg out.bin
            File.WriteAllBytes(args[3], FloatBytes(Repairer.BuildReference(File.ReadAllBytes(args[1]), JpegParser.Parse(File.ReadAllBytes(args[2])))));
            return 0;
        case "repair" when args.Length >= 3:
            var buf = File.ReadAllBytes(args[1]);
            var mp = Opt(args, "model"); var rf = Opt(args, "ref");
            int.TryParse(Opt(args, "beam"), out int beam); int.TryParse(Opt(args, "max-blocks"), out int maxBlocks); double.TryParse(Opt(args, "max-seconds"), out double maxSec);
            var res = Repairer.Repair(buf, mp != null ? Model.Load(mp) : null, rf != null ? ToFloats(File.ReadAllBytes(rf)) : null, beam, maxBlocks, maxSeconds: maxSec);
            var truth = Opt(args, "truth");
            if (truth != null)
            {
                var t = Repairer.TruthList(File.ReadAllBytes(truth));
                var have = new HashSet<int>(res.List);
                int lastBad = res.List.Count > 0 ? res.List[^1] : 0;
                var tt = t.Where(x => x <= lastBad).ToList();
                Console.Error.WriteLine($"truth: {t.Count} insertions; found {res.List.Count}; of the truth up to the last found position, {tt.Count(have.Contains)}/{tt.Count} matched");
            }
            if (res.StuckAt >= 0) Console.Error.WriteLine($"STUCK at block {res.StuckAt} - search ran out of plausible candidates; writing partial result");
            else Console.Error.WriteLine(res.Verified ? "OK: decode ends exactly at end of file (consistent)" : "WARNING: result is not consistent with end of file");
            var repaired = Repairer.BuildOutput(buf, res);
            float[]? sibRef = null;
            if (Opt(args, "sibling") is string sibPath)
            {
                try { sibRef = Repairer.BuildReference(File.ReadAllBytes(sibPath), res.J); Console.Error.WriteLine("using sibling reference " + sibPath); }
                catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException) { Console.Error.WriteLine("sibling reference unusable: " + ex.Message); }
            }
            if (RowFix.Enabled || args.Contains("--rowfix")) { repaired = RowFix.Apply(repaired, sibRef); Console.Error.WriteLine("row-shift correction and DC re-anchoring applied"); }
            File.WriteAllBytes(args[2], repaired);
            Console.Error.WriteLine($"inserted {res.List.Count} bytes");
            return res.Verified ? 0 : 1;
        default:
            Console.Error.WriteLine(Usage); return 2;
    }
}
catch (InvalidDataException ex) { Console.Error.WriteLine("error: " + ex.Message); return 3; }
