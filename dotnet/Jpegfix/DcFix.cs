namespace Jpegfix;

/// <summary>
/// DC re-anchoring. After a wrong resync every later block of a component carries a constant DC offset (the DC predictor chain) until the next error.
/// Going through the decoded blocks in order, each MCU's DC is compared with its already-corrected neighbours above and to the left on the canvas (the
/// smaller difference of the two, so a straight horizontal or vertical edge is not mistaken for an offset). When DC_W consecutive MCUs agree on the same
/// non-trivial difference, that difference is taken as the offset, removed from them and from everything after.
/// </summary>
static class DcFix
{
    static double Env(string n, double d) => double.TryParse(Environment.GetEnvironmentVariable(n), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
    public static readonly bool Off = Environment.GetEnvironmentVariable("DC_OFF") != null;
    public static readonly int Win = (int)Env("DC_W", 10);
    public static readonly double Thresh = Env("DC_T", 16), Spread = Env("DC_SPREAD", 24);      // dequantised DC units (1/8 grey level)

    public static readonly double RefThresh = Env("DC_RT", 24), RefSpread = Env("DC_RSPREAD", 48); public static readonly int RefWin = (int)Env("DC_RW", 6);

    public static int Apply(CoefImage img, int[] canvas, float[]? rf = null)
    {
        if (Environment.GetEnvironmentVariable("DC_NOREF") != null) rf = null;
        var J = img.J; int N = J.Mcus, Mx = J.Mx, dm = img.Decoded / 6, adjustments = 0;
        var slot = new int[N]; Array.Fill(slot, -1);
        for (int p = 0; p < N; p++) if (canvas[p] >= 0 && canvas[p] < dm) slot[canvas[p]] = p;
        var q0 = new double[3]; for (int c = 0; c < 3; c++) q0[c] = J.Qz[c][0];
        var cdc = new double[dm * 6]; var done = new bool[dm];
        var off = new double[3]; var win = new List<(int mcu, double d)>[] { new(), new(), new() };

        double Cur(int mcu, int c, int part) => c == 0 ? part switch { 0 => (cdc[mcu * 6] + cdc[mcu * 6 + 1]) / 2, _ => (cdc[mcu * 6] + cdc[mcu * 6 + 2]) / 2 } : cdc[mcu * 6 + 3 + c];
        double Edge(int mcu, int c, int part) => c == 0 ? part switch { 0 => (cdc[mcu * 6 + 2] + cdc[mcu * 6 + 3]) / 2, _ => (cdc[mcu * 6 + 1] + cdc[mcu * 6 + 3]) / 2 } : cdc[mcu * 6 + 3 + c];

        for (int i = 0; i < dm; i++)
        {
            for (int bi = 0; bi < 6; bi++) { int c = bi < 4 ? 0 : bi - 3; cdc[i * 6 + bi] = img.Blk[i * 6 + bi][0] * q0[c] - off[c]; }
            done[i] = true; int p = slot[i]; if (p < 0) continue;
            int A = p - Mx >= 0 ? canvas[p - Mx] : -1, L = p % Mx != 0 ? canvas[p - 1] : -1;
            if (A >= dm || A >= 0 && !done[A]) A = -1; if (L >= dm || L >= 0 && !done[L]) L = -1;
            for (int c = 0; c < 3; c++)
            {
                if (rf != null)
                {
                    int gx = 2 * Mx, mx = p % Mx, my = p / Mx; double refDc;
                    if (c == 0) { double rs = 0; for (int b = 0; b < 4; b++) rs += rf[(2 * my + (b >> 1)) * gx + 2 * mx + (b & 1)]; refDc = (rs / 4 - 128) * 8; }
                    else refDc = (rf[gx * 2 * J.My + (c - 1) * Mx * J.My + my * Mx + mx] - 128) * 8;
                    if (double.IsNaN(refDc)) continue;
                    double curDc = c == 0 ? (cdc[i * 6] + cdc[i * 6 + 1] + cdc[i * 6 + 2] + cdc[i * 6 + 3]) / 4 : cdc[i * 6 + 3 + c];
                    var wr = win[c]; wr.Add((i, curDc - refDc)); if (wr.Count > RefWin) wr.RemoveAt(0);
                    if (wr.Count < RefWin) continue;
                    var sr = wr.Select(x => x.d).OrderBy(x => x).ToArray(); double mr = (sr[RefWin / 2 - 1] + sr[RefWin / 2]) / 2;
                    if (Math.Abs(mr) <= RefThresh || sr.Any(x => Math.Abs(x - mr) > RefSpread)) continue;
                    off[c] += mr; adjustments++;
                    foreach (var (m2, _) in wr) for (int bi = 0; bi < 6; bi++) if ((bi < 4 ? 0 : bi - 3) == c) cdc[m2 * 6 + bi] -= mr;
                    wr.Clear(); continue;
                }
                double? da = A >= 0 ? Cur(i, c, 0) - Edge(A, c, 0) : null, dl = L >= 0 ? Cur(i, c, 1) - Edge(L, c, 1) : null;
                double d; if (da == null && dl == null) continue; else if (da == null) d = dl!.Value; else if (dl == null) d = da.Value; else d = Math.Abs(da.Value) <= Math.Abs(dl.Value) ? da.Value : dl.Value;
                var w = win[c]; w.Add((i, d)); if (w.Count > Win) w.RemoveAt(0);
                if (w.Count < Win) continue;
                var s = w.Select(x => x.d).OrderBy(x => x).ToArray(); double med = (s[Win / 2 - 1] + s[Win / 2]) / 2;
                if (Math.Abs(med) <= Thresh || s.Any(x => Math.Abs(x - med) > Spread)) continue;
                off[c] += med; adjustments++;
                foreach (var (m, _) in w) for (int bi = 0; bi < 6; bi++) if ((bi < 4 ? 0 : bi - 3) == c) cdc[m * 6 + bi] -= med;
                for (int cc = 0; cc < 3; cc++) if (cc == c) w.Clear();
            }
        }
        for (int m = 0; m < dm; m++) for (int bi = 0; bi < 6; bi++) { int c = bi < 4 ? 0 : bi - 3; img.Blk[m * 6 + bi][0] = (short)Math.Round(cdc[m * 6 + bi] / q0[c]); }
        return adjustments;
    }
}
