using System.Text.RegularExpressions;

namespace Jpegfix;

/// <summary>
/// The forum gallery as mirrored to disk: files have no extension and are named
/// <c>SEQ_[thumb_|preview_]NAME_EXT&lt;32 hex&gt;_extEXT</c> (e.g. <c>4661_DSC_0051_JPG7852c3…_extjpg</c>), inside album/Dir_N folders. The three sizes of a picture are
/// normally consecutive sequence numbers (original, thumbnail, preview) with the same NAME. Some pictures lack a member; there are also non-image files.
/// </summary>
public static class Gallery
{
    static readonly Regex Rx = new(@"^(?<seq>\d+)_(?<pre>thumb_|preview_)?(?<base>.+?)(?<hash>[0-9a-f]{32})_ext(?<ext>[a-z0-9]+)$", RegexOptions.Compiled);

    public sealed record Member(string Kind, string Path, int Seq);
    public sealed class Group { public string Dir = "", Key = ""; public List<Member> Members = new(); }

    /// <summary>Group the files of one directory into pictures. Files that are not jpg images (by name) are returned in <paramref name="skipped"/>.</summary>
    public static List<Group> GroupDirectory(string dir, IEnumerable<string> files, List<string> skipped)
    {
        var parsed = new List<(string Path, int Seq, string Pre, string Base)>();
        foreach (var f in files)
        {
            var m = Rx.Match(System.IO.Path.GetFileName(f));
            if (!m.Success || m.Groups["ext"].Value != "jpg" || m.Groups["base"].Value.StartsWith("temp_orphans")) { skipped.Add(f); continue; }
            parsed.Add((f, int.Parse(m.Groups["seq"].Value), m.Groups["pre"].Value, m.Groups["base"].Value));
        }
        parsed.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        var used = new bool[parsed.Count]; var groups = new List<Group>();
        for (int i = 0; i < parsed.Count; i++)
        {
            if (used[i] || parsed[i].Pre != "") continue;       // start a group at each original
            var g = new Group { Dir = dir, Key = parsed[i].Base }; used[i] = true;
            g.Members.Add(new Member("orig", parsed[i].Path, parsed[i].Seq));
            for (int j = i + 1; j < parsed.Count && parsed[j].Seq <= parsed[i].Seq + 3; j++)
            {
                if (used[j] || parsed[j].Base != parsed[i].Base || parsed[j].Pre == "") continue;
                string kind = parsed[j].Pre == "thumb_" ? "thumb" : "preview";
                if (g.Members.Any(x => x.Kind == kind)) continue;
                used[j] = true; g.Members.Add(new Member(kind, parsed[j].Path, parsed[j].Seq));
            }
            groups.Add(g);
        }
        for (int i = 0; i < parsed.Count; i++)      // thumbnails/previews whose original is missing: their own group
            if (!used[i]) { var g = new Group { Dir = dir, Key = parsed[i].Base }; g.Members.Add(new Member(parsed[i].Pre == "thumb_" ? "thumb" : "preview", parsed[i].Path, parsed[i].Seq)); groups.Add(g); }
        return groups;
    }

    /// <summary>Walk a gallery root and group every directory's files into pictures.</summary>
    public static List<Group> Scan(string root, List<string> skipped)
    {
        var all = new List<Group>();
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            string[] files; try { files = Directory.GetFiles(dir); } catch (IOException) { continue; }
            if (files.Length == 0) continue;
            all.AddRange(GroupDirectory(dir, files, skipped));
        }
        return all;
    }

    /// <summary>jpegfix survey ROOT: how the gallery groups into pictures and which files the repairer can handle (reads every file once).</summary>
    public static int Survey(string root, int threads)
    {
        var skipped = new List<string>(); var groups = Scan(root, skipped);
        int files = groups.Sum(g => g.Members.Count);
        Console.WriteLine($"{groups.Count} pictures, {files} image files, {skipped.Count} other files skipped");
        Console.WriteLine("members per picture: " + string.Join(", ", groups.GroupBy(g => string.Join("+", g.Members.Select(m => m.Kind).OrderBy(k => k))).OrderByDescending(x => x.Count()).Select(x => $"{x.Key}: {x.Count()}")));
        var status = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(); var sizes = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var members = groups.SelectMany(g => g.Members).ToList(); long bytes = 0;
        Parallel.ForEach(members, new ParallelOptions { MaxDegreeOfParallelism = threads }, m =>
        {
            string st;
            try
            {
                var b = File.ReadAllBytes(m.Path); Interlocked.Add(ref bytes, b.Length);
                if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) st = "not a JPEG";
                else { var j = JpegParser.Parse(b); st = "ok"; sizes.AddOrUpdate($"{m.Kind} {j.Width}x{j.Height}", 1, (_, v) => v + 1); }
            }
            catch (InvalidDataException ex) { st = System.Text.RegularExpressions.Regex.Replace(ex.Message.Replace("header damaged (", "header damaged: ").TrimEnd(')'), @"\d+", "N"); if (st.Length > 60) st = st[..60]; }
            catch (IOException ex) { st = "read error: " + ex.Message; }
            status.AddOrUpdate($"{m.Kind}: {st}", 1, (_, v) => v + 1);
        });
        Console.WriteLine($"read {bytes / 1048576.0:F0} MB");
        foreach (var kv in status.OrderBy(k => k.Key)) Console.WriteLine($"  {kv.Key,-60} {kv.Value,6}");
        Console.WriteLine("dimensions (top 12): " + string.Join(", ", sizes.OrderByDescending(k => k.Value).Take(12).Select(k => $"{k.Key} x{k.Value}")));
        return 0;
    }
}

public static class GallerySurvey2
{
    /// <summary>jpegfix survey2 ROOT: apply the CR-before-LF inversion to every file and report how many then parse and decode consistently.</summary>
    public static int Run(string root, int threads)
    {
        var skipped = new List<string>(); var groups = Gallery.Scan(root, skipped);
        var members = groups.SelectMany(g => g.Members).ToList();
        var status = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var marks = new System.Collections.Concurrent.ConcurrentBag<(string, int)>();
        Parallel.ForEach(members, new ParallelOptions { MaxDegreeOfParallelism = threads }, m =>
        {
            var raw = File.ReadAllBytes(m.Path); var fixedB = Unmangle.Apply(raw);
            string st = Unmangle.Check(fixedB, out _, out _);
            status.AddOrUpdate($"{m.Kind}: {st}", 1, (_, v) => v + 1);
        });
        Console.WriteLine($"{members.Count} image files after undoing CR-before-LF:");
        foreach (var kv in status.OrderBy(k => k.Key)) Console.WriteLine($"  {kv.Key,-60} {kv.Value,6}");
        return 0;
    }
}

public static class Recover
{
    /// <summary>
    /// jpegfix recover ROOT --out OUT: undo the CR-before-LF damage in every file under ROOT (mirrored layout, new files only; the source is never modified).
    /// JPEGs get ".jpg" appended and are checked (parse, all blocks decode, entropy data ends exactly at the end); .php files are copied unchanged.
    /// </summary>
    public static int Run(string root, string outDir, int threads)
    {
        string full = Path.GetFullPath(root).TrimEnd('/'); Directory.CreateDirectory(outDir);
        var files = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var rows = new System.Collections.Concurrent.ConcurrentBag<string>(); var tally = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = threads }, f =>
        {
            var rel = Path.GetRelativePath(full, f); var raw = File.ReadAllBytes(f);
            string status, dest;
            if (f.EndsWith(".php", StringComparison.OrdinalIgnoreCase)) { status = "text, copied unchanged"; dest = Path.Combine(outDir, rel); }
            else
            {
                var fixedB = Unmangle.Apply(raw); bool jpeg = fixedB.Length > 3 && fixedB[0] == 0xFF && fixedB[1] == 0xD8;
                dest = Path.Combine(outDir, rel + (jpeg ? ".jpg" : ""));
                status = jpeg ? Unmangle.Check(fixedB, out _, out _) : "not a JPEG (inverted anyway)";
                if (jpeg && status.StartsWith("header: only") || status.StartsWith("header: restart")) status = "unchecked: " + status;
                raw = fixedB;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var tmp = dest + ".part"; File.WriteAllBytes(tmp, raw); File.Move(tmp, dest, true);
            tally.AddOrUpdate(status, 1, (_, v) => v + 1); rows.Add($"{rel}\t{status}\t{raw.Length}");
        });
        File.WriteAllLines(Path.Combine(outDir, "recover-report.tsv"), rows.OrderBy(r => r, StringComparer.Ordinal));
        Console.WriteLine($"{files.Count} files written to {outDir}:");
        foreach (var kv in tally.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Key,-70} {kv.Value,6}");
        return 0;
    }
}
