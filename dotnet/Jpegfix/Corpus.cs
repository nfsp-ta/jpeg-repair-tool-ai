namespace Jpegfix;

sealed record Item(string Name, string Kind, string Id, byte[] Good, JpegInfo J);

/// <summary>Loads clean JPEGs named like the gallery files (kind-id.jpg), from one or more comma-separated directories.</summary>
static class Corpus
{
    public static List<Item> Load(string dirs)
    {
        var items = new List<Item>();
        var list = dirs.Split(',', StringSplitOptions.RemoveEmptyEntries);
        for (int d = 0; d < list.Length; d++)
            foreach (var f in Directory.GetFiles(list[d], "*.jpg").OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(f); var stem = Path.GetFileNameWithoutExtension(f);
                int dash = stem.IndexOf('-');
                var kind = dash > 0 ? stem[..dash] : stem; var id = dash > 0 ? stem[(dash + 1)..] : stem;
                var good = File.ReadAllBytes(f);
                try { items.Add(new Item((d > 0 ? Path.GetFileName(list[d].TrimEnd('/')) + "/" : "") + name, kind, id, good, JpegParser.Parse(good))); }
                catch (InvalidDataException ex) { Console.Error.WriteLine($"skip {name}: {ex.Message}"); }
            }
        return items;
    }

    /// <summary>Indices of the items to evaluate: those of the given kind, thinned to an evenly spread sample of <paramref name="limit"/>.</summary>
    public static List<int> Pick(List<Item> all, string? kind, int limit)
    {
        var idx = Enumerable.Range(0, all.Count).Where(i => kind == null || all[i].Kind == kind).ToList();
        if (limit > 0 && idx.Count > limit) idx = Enumerable.Range(0, limit).Select(i => idx[(int)((long)i * idx.Count / limit)]).ToList();
        return idx;
    }
}
