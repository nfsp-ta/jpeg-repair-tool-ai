namespace Jpegfix;

/// <summary>
/// Leave-one-out statistics models. For file i the model is trained on every file of the same group (scope: kind = same size
/// kind, all = every file) EXCEPT all files showing the same image id (so a synthetic copy or another size of the test image
/// never trains the model that is used to test it).
/// </summary>
sealed class LooModels
{
    readonly string scope;
    readonly string[] kinds, ids;
    readonly Model[] own;
    readonly Dictionary<string, Model> totals = new();

    public LooModels(IReadOnlyList<(byte[] Good, string Kind, string Id)> items, string scope)
    {
        this.scope = scope;
        kinds = items.Select(i => i.Kind).ToArray(); ids = items.Select(i => i.Id).ToArray();
        own = new Model[items.Count];
        if (scope == "none") return;
        Parallel.For(0, items.Count, i => { own[i] = new Model(); Repairer.Train(items[i].Good, own[i]); });
        for (int i = 0; i < items.Count; i++)
        {
            var g = Group(i);
            if (!totals.TryGetValue(g, out var t)) totals[g] = t = new Model();
            t.Add(own[i]);
        }
    }

    string Group(int i) => scope == "all" ? "" : kinds[i];

    public Model? For(int i)
    {
        if (scope == "none") return null;
        var m = new Model(); m.Add(totals[Group(i)]);
        for (int j = 0; j < own.Length; j++) if (ids[j] == ids[i] && Group(j) == Group(i)) m.Add(own[j], -1);
        if (m.Tot[0].Sum() + m.Tot[1].Sum() <= 0) return null;
        m.Refresh();
        return m;
    }
}
