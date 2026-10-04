namespace Jpegfix;

/// <summary>Leave-one-out statistics models: for file i, the model trained on every other file of the same group.</summary>
sealed class LooModels
{
    readonly string scope;
    readonly string[] kinds;
    readonly Model[] own;
    readonly Dictionary<string, Model> totals = new();

    public LooModels(IReadOnlyList<(byte[] Good, string Kind)> items, string scope)
    {
        this.scope = scope;
        kinds = items.Select(i => i.Kind).ToArray();
        own = new Model[items.Count];
        if (scope == "none") return;
        for (int i = 0; i < items.Count; i++)
        {
            own[i] = new Model(); Repairer.Train(items[i].Good, own[i]);
            var g = scope == "all" ? "" : kinds[i];
            if (!totals.TryGetValue(g, out var t)) totals[g] = t = new Model();
            t.Add(own[i]);
        }
    }

    public Model? For(int i)
    {
        if (scope == "none") return null;
        var m = new Model(); m.Add(totals[scope == "all" ? "" : kinds[i]]); m.Add(own[i], -1);
        if (m.Tot[0].Sum() + m.Tot[1].Sum() <= 0) return null;
        m.Refresh();
        return m;
    }
}
