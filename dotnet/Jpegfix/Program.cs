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
  jpegfix cascade --out DIR [--models DIR] [--max-seconds S] [--variants N] [--beam N] [--id NAME] [--no-resume] damaged1.jpg [damaged2.jpg ...]   (the default pipeline: sizes of ONE picture, smallest first, each repaired size is the reference for the next)
  jpegfix cascade-gallery ROOT --out DIR [--models DIR] [--max-seconds S] [--variants N] [--threads N] [--limit N]   (the forum gallery layout: SEQ_[thumb_|preview_]NAME_EXT<hash>_extjpg files in album/Dir_N folders)
  jpegfix recover ROOT --out DIR [--threads N]   (the REAL gallery damage: undo the inserted 0x0D before every 0x0A; exact, no search)
  jpegfix survey ROOT [--threads N]   (how the gallery groups into pictures and which files can be handled)
  jpegfix cascade-dir IN_DIR --out DIR [--models DIR] [--max-seconds S] [--variants N] [--threads N] [--no-resume]   (files thumb-ID.jpg, preview-ID.jpg, orig-ID.jpg grouped by ID)
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
        case "cascade" when args.Length >= 2:
        {
            var co = Cascade.Parse(args, n => Opt(args, n));
            if (co.OutDir == "") { Console.Error.WriteLine("cascade needs --out DIR"); return 2; }
            var cfiles = new List<string>(); for (int i = 1; i < args.Length; i++) { if (args[i].StartsWith("--")) { if (args[i] != "--no-resume") i++; } else cfiles.Add(args[i]); }
            return Cascade.RunFiles(co, Opt(args, "id") ?? "picture", cfiles);
        }
        case "cascade-dir" when args.Length >= 2:
        {
            var co = Cascade.Parse(args, n => Opt(args, n));
            if (co.OutDir == "") { Console.Error.WriteLine("cascade-dir needs --out DIR"); return 2; }
            if (!int.TryParse(Opt(args, "threads"), out int cth) || cth < 1) cth = Math.Max(1, Environment.ProcessorCount / 2);
            return Cascade.RunDir(args[1], co, cth);
        }
        case "refcheck" when args.Length >= 4:      // refcheck clean-target.jpg nearest-repaired.jpg anchor-repaired.jpg: accuracy of each reference against the truth
        {
            var tgt = File.ReadAllBytes(args[1]); var tj = JpegParser.Parse(tgt); var nr = File.ReadAllBytes(args[2]); var an = File.ReadAllBytes(args[3]);
            string F((double l, double c, double u) e) => $"luma {e.l,5:F2} chroma {e.c,5:F2} unknown {100 * e.u,4:F1}%";
            var cb = Repairer.BuildCombinedReference(nr, an, tj, out double fb);
            Console.WriteLine($"{Path.GetFileName(args[1])}: nearest {F(Repairer.ReferenceError(Repairer.BuildReference(nr, tj), tgt))} | anchor {F(Repairer.ReferenceError(Repairer.BuildReference(an, tj), tgt))} | combined {F(Repairer.ReferenceError(cb, tgt))} (anchor-only {100 * fb:F0}%)");
            return 0;
        }
        case "quality" when args.Length >= 2:      // quality repaired.jpg [original.jpg]: internal seam score (no original needed), and closeness to the original if given
        {
            var qp = Planes.Render(File.ReadAllBytes(args[1])); var (qs, qd) = Quality.Measure(qp); string close = "";
            if (args.Length > 2) { var qt = Planes.Render(File.ReadAllBytes(args[2])); int cl = 0; for (int mi = 0; mi < qp.J.Mcus; mi++) if (qp.McuError(qt, mi) <= 6) cl++; close = $"  close {100.0 * cl / qp.J.Mcus:F1}%"; }
            if (args.Length > 3) { var rbytes = File.ReadAllBytes(args[1]); var aref = Repairer.BuildReference(File.ReadAllBytes(args[3]), qp.J); var (rl, rc, _) = Repairer.ReferenceError(aref, rbytes); close += $"  vs-thumb luma {rl:F2} chroma {rc:F2}"; }
            Console.WriteLine($"{Path.GetFileName(args[1])}: seam {qs:F2}  decoded {100 * qd:F0}%{close}");
            return 0;
        }
        case "recover" when args.Length >= 2:
        {
            var rout = Opt(args, "out"); if (rout == null) { Console.Error.WriteLine("recover needs --out DIR"); return 2; }
            if (!int.TryParse(Opt(args, "threads"), out int rth) || rth < 1) rth = 8;
            return Recover.Run(args[1], rout, rth);
        }
        case "unmangle" when args.Length >= 3:      // unmangle in out: undo the CR-before-LF damage, report whether the result is consistent
        {
            var ub = File.ReadAllBytes(args[1]); var uo = Unmangle.Apply(ub); File.WriteAllBytes(args[2], uo);
            Console.WriteLine($"{Path.GetFileName(args[1])}: removed {Unmangle.CountMarks(ub)} CRs before LF -> {uo.Length} bytes: {Unmangle.Check(uo, out int ub1, out int ud1)} ({ud1}/{ub1} blocks)");
            return 0;
        }
        case "survey2" when args.Length >= 2:
            if (!int.TryParse(Opt(args, "threads"), out int s2t) || s2t < 1) s2t = 8;
            return GallerySurvey2.Run(args[1], s2t);
        case "survey" when args.Length >= 2:
            if (!int.TryParse(Opt(args, "threads"), out int sth) || sth < 1) sth = 4;
            return Gallery.Survey(args[1], sth);
        case "cascade-gallery" when args.Length >= 2:
        {
            var co = Cascade.Parse(args, n => Opt(args, n));
            if (co.OutDir == "") { Console.Error.WriteLine("cascade-gallery needs --out DIR"); return 2; }
            if (!int.TryParse(Opt(args, "threads"), out int gth) || gth < 1) gth = Math.Max(1, Environment.ProcessorCount / 2);
            int.TryParse(Opt(args, "limit"), out int glim);
            return Cascade.RunGallery(args[1], co, gth, glim);
        }
        case "makeref" when args.Length >= 4:      // makeref sibling.jpg target.jpg out.bin
            File.WriteAllBytes(args[3], FloatBytes(Repairer.BuildReference(File.ReadAllBytes(args[1]), JpegParser.Parse(File.ReadAllBytes(args[2])))));
            return 0;
        case "repair" when args.Length >= 3:
            var buf = File.ReadAllBytes(args[1]);
            var mp = Opt(args, "model"); var rf = Opt(args, "ref");
            int.TryParse(Opt(args, "beam"), out int beam); int.TryParse(Opt(args, "max-blocks"), out int maxBlocks); double.TryParse(Opt(args, "max-seconds"), out double maxSec);
            float[]? refData = rf != null ? ToFloats(File.ReadAllBytes(rf)) : null;
            float[]? sibRef = null;
            if (Opt(args, "sibling") is string sibPath)       // a repaired smaller size of the same picture: reference for the search and for the post-processing
            {
                try
                {
                    var tj = JpegParser.Parse(buf);
                    if (Opt(args, "anchor") is string anchorPath) { sibRef = Repairer.BuildCombinedReference(File.ReadAllBytes(sibPath), File.ReadAllBytes(anchorPath), tj, out double afb); Console.Error.WriteLine($"reference combines the sibling with the smallest size ({100 * afb:F0}% of blocks from the smallest)"); }
                    else sibRef = Repairer.BuildReference(File.ReadAllBytes(sibPath), tj); refData ??= sibRef; Console.Error.WriteLine("using sibling reference " + sibPath); }
                catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException) { Console.Error.WriteLine("sibling reference unusable: " + ex.Message); }
            }
            var res = Repairer.Repair(buf, mp != null ? Model.Load(mp) : null, refData, beam, maxBlocks, maxSeconds: maxSec);
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
            if (RowFix.Enabled || args.Contains("--rowfix")) { repaired = RowFix.Apply(repaired, sibRef); Console.Error.WriteLine("row-shift correction and DC re-anchoring applied"); }
            File.WriteAllBytes(args[2], repaired);
            Console.Error.WriteLine($"inserted {res.List.Count} bytes");
            return res.Verified ? 0 : 1;
        default:
            Console.Error.WriteLine(Usage); return 2;
    }
}
catch (InvalidDataException ex) { Console.Error.WriteLine("error: " + ex.Message); return 3; }
