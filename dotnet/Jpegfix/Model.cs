using System.Text.Json;

namespace Jpegfix;

/// <summary>Coefficient-size statistics: P(size of AC coef k | size of coef k-1), separately for luma and chroma.</summary>
sealed class Model
{
    const int Ctx = 5, Syms = 11;
    public double[][] Cnt = { new double[64 * Ctx * Syms], new double[64 * Ctx * Syms] };
    public double[][] Tot = { new double[64 * Ctx], new double[64 * Ctx] };
    float[][] lg = { new float[64 * Ctx * Syms], new float[64 * Ctx * Syms] };
    public bool Ready;

    public void Train(int ci, byte[] sz)
    {
        int m = ci == 0 ? 0 : 1;
        for (int k = 1; k < 64; k++)
        {
            int idx = k * Ctx + Math.Min((int)sz[k - 1], 4);
            Cnt[m][idx * Syms + sz[k]]++; Tot[m][idx]++;
        }
    }

    public void Refresh()
    {
        for (int m = 0; m < 2; m++) for (int idx = 0; idx < 64 * Ctx; idx++) for (int s = 0; s < Syms; s++)
            lg[m][idx * Syms + s] = (float)(-Math.Log2((Cnt[m][idx * Syms + s] + 0.3) / (Tot[m][idx] + 0.3 * Syms)) + s);
        Ready = true;
    }

    /// <summary>Bits needed to code the AC part of a block with coefficient sizes <paramref name="sz"/> (incl. raw magnitude bits).</summary>
    public double Bits(int ci, byte[] sz)
    {
        var l = lg[ci == 0 ? 0 : 1]; double bits = 0;
        for (int k = 1; k < 64; k++) bits += l[(k * Ctx + (sz[k - 1] > 4 ? 4 : sz[k - 1])) * Syms + sz[k]];
        return bits;
    }

    /// <summary>this += sign * other (counts only; call Refresh afterwards).</summary>
    public void Add(Model o, double sign = 1)
    {
        for (int m = 0; m < 2; m++)
        {
            for (int i = 0; i < Cnt[m].Length; i++) Cnt[m][i] += sign * o.Cnt[m][i];
            for (int i = 0; i < Tot[m].Length; i++) Tot[m][i] += sign * o.Tot[m][i];
        }
    }

    // JSON layout matches the one written by jpegfix.js: { cnt: [[..],[..]], tot: [[..],[..]] }
    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, double[][]> { ["cnt"] = Cnt, ["tot"] = Tot }));
    }

    public static Model Load(string path)
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, double[][]>>(File.ReadAllText(path))!;
        var m = new Model();
        for (int i = 0; i < 2; i++) { d["cnt"][i].CopyTo(m.Cnt[i], 0); d["tot"][i].CopyTo(m.Tot[i], 0); }
        m.Refresh();
        return m;
    }
}
