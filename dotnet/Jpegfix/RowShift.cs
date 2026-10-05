namespace Jpegfix;

/// <summary>Decoded pixel planes of a JPEG (luma full size, chroma half size) plus which MCUs decoded.</summary>
sealed class Planes
{
    public readonly JpegInfo J;
    public readonly int W, H, CW;
    public readonly byte[] Y, Cb, Cr;
    public readonly bool[] Ok;
    public Planes(JpegInfo j)
    {
        J = j; W = j.Mx * 16; H = j.My * 16; CW = W / 2;
        Y = new byte[W * H]; Cb = new byte[CW * (H / 2)]; Cr = new byte[CW * (H / 2)]; Ok = new bool[j.Mcus];
        Array.Fill(Y, (byte)128); Array.Fill(Cb, (byte)128); Array.Fill(Cr, (byte)128);
    }

    /// <summary>Decode every block of the scan in order, with no repairs, until one fails to decode.</summary>
    public static Planes Render(byte[] jpeg)
    {
        var J = JpegParser.Parse(jpeg); var P = new Planes(J);
        var data = JpegParser.Unstuff(jpeg, J.ScanStart, out _);
        var rep = new byte[data.Length + Tunables.Win + 16]; Array.Copy(data, rep, data.Length);
        var dec = new BlockDecoder(J, null, null); var S = dec.InitialState(); var w = new byte[Tunables.Win + 8];
        for (int n = 0; n < J.Blocks; n++)
        {
            int w0 = S.BitPos >> 3; if (w0 >= data.Length) break;
            Array.Copy(rep, w0, w, 0, w.Length);
            if (!dec.EvalBlock(S, w, S.BitPos & 7, 8 * data.Length - 8 * w0)) break;
            int bi = n % 6, mcu = n / 6, mx = mcu % J.Mx, my = mcu / J.Mx; var pix = dec.R.Pix;
            if (bi < 4) { int x0 = mx * 16 + (bi & 1) * 8, y0 = my * 16 + (bi >> 1) * 8; for (int y = 0; y < 8; y++) Array.Copy(pix, y * 8, P.Y, (y0 + y) * P.W + x0, 8); }
            else { var pl = bi == 4 ? P.Cb : P.Cr; for (int y = 0; y < 8; y++) Array.Copy(pix, y * 8, pl, (my * 8 + y) * P.CW + mx * 8, 8); P.Ok[mcu] = true; }
            S = dec.MakeChild(S, w0, Array.Empty<int>());
        }
        return P;
    }

    public void CopyMcu(Planes src, int from, int to)
    {
        int fx = from % J.Mx, fy = from / J.Mx, tx = to % J.Mx, ty = to / J.Mx;
        for (int y = 0; y < 16; y++) Array.Copy(src.Y, (fy * 16 + y) * W + fx * 16, Y, (ty * 16 + y) * W + tx * 16, 16);
        for (int y = 0; y < 8; y++)
        {
            Array.Copy(src.Cb, (fy * 8 + y) * CW + fx * 8, Cb, (ty * 8 + y) * CW + tx * 8, 8);
            Array.Copy(src.Cr, (fy * 8 + y) * CW + fx * 8, Cr, (ty * 8 + y) * CW + tx * 8, 8);
        }
    }

    /// <summary>Mean absolute luma error of MCU m against the same MCU of another image.</summary>
    public double McuError(Planes o, int m)
    {
        int x0 = m % J.Mx * 16, y0 = m / J.Mx * 16; double s = 0;
        for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) s += Math.Abs(Y[(y0 + y) * W + x0 + x] - o.Y[(y0 + y) * W + x0 + x]);
        return s / 256;
    }

    /// <summary>Mean absolute chroma (Cb and Cr) error of MCU m.</summary>
    public double ChromaError(Planes o, int m)
    {
        int x0 = m % J.Mx * 8, y0 = m / J.Mx * 8; double s = 0;
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) { int i = (y0 + y) * CW + x0 + x; s += Math.Abs(Cb[i] - o.Cb[i]) + Math.Abs(Cr[i] - o.Cr[i]); }
        return s / 128;
    }

    public void WriteRgb(Stream o, int x0 = 0)
    {
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
        {
            double yy = Y[y * W + x], cb = Cb[(y / 2) * CW + x / 2] - 128, cr = Cr[(y / 2) * CW + x / 2] - 128;
            o.WriteByte(Clamp(yy + 1.402 * cr)); o.WriteByte(Clamp(yy - 0.344136 * cb - 0.714136 * cr)); o.WriteByte(Clamp(yy + 1.772 * cb));
        }
    }
    static byte Clamp(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : (int)(v + 0.5));
}

/// <summary>
/// Row-shift correction on a repaired image. A wrong resync displaces everything after it by a whole number of MCUs, so decoded MCU i
/// really belongs at slot i+s. Going top-down, each decoded row picks a shift per MCU (Viterbi: switching costs RS_P, the shift carries
/// over from the previous row) by how well its top strip continues the bottom strip of the already placed row above.
/// </summary>
static class RowShift
{
    static double Env(string n, double d) => double.TryParse(Environment.GetEnvironmentVariable(n), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
    public static readonly int K = (int)Env("RS_K", 6);
    public static readonly double Switch = Env("RS_P", 100), Missing = Env("RS_MISS", 60), Lambda = Env("RS_LAMBDA", 2);
    public static readonly double Bias = Env("RS_BIAS", 10);        // flat cost per MCU for any non-zero shift: no evidence, no shift
    public static readonly double GoodE = Env("RS_GOOD", 70), GateFrac = Env("RS_GATE", 0);   // a row is trusted only if this fraction of its MCUs has a seam cost below GoodE under the chosen shift
    public static readonly double MinGain = Env("RS_GAIN", 0);   // a row's shifts are kept only if they lower the seam evidence by this much per MCU
    public static readonly double Rel = Env("RS_REL", 1.01);     // a row's shifts are kept only if its seam evidence is below this fraction of the evidence with no shift
    public static readonly double CapE = Env("RS_CAP", 120);      // evidence from one MCU is capped, so garbage (everything mismatches) adds no preference

    /// <summary>Gradient-aware seam cost between the bottom of MCU a (above) and the top of MCU i, luma only.</summary>
    static double Seam(Planes P, int a, int i)
    {
        int W = P.W, Mx = P.J.Mx; var Y = P.Y;
        int ax = a % Mx * 16, ay = a / Mx * 16 + 15, ix = i % Mx * 16, iy = i / Mx * 16; double s = 0;
        for (int x = 0; x < 16; x++)
        {
            double t0 = Y[ay * W + ax + x], t1 = Y[(ay - 1) * W + ax + x], p0 = Y[iy * W + ix + x], p1 = Y[(iy + 1) * W + ix + x];
            s += Math.Abs((p0 - t0) - ((p1 - p0) + (t0 - t1)) / 2);
        }
        return s;
    }

    /// <summary>Chosen shift per decoded MCU, and the canvas (slot -> decoded MCU index, -1 empty).</summary>
    public static int[] Detect(Planes P, out int[] canvas)
    {
        int Mx = P.J.Mx, My = P.J.My, N = P.J.Mcus, K = Math.Max(1, Math.Min(RowShift.K, P.J.Mx / (int)Env("RS_DIV", 8))), ns = 2 * K + 1;   // search range scales with the width: a thumbnail row has only 8 MCUs
        canvas = new int[N]; Array.Fill(canvas, -1);
        var shift = new int[N]; int carry = 0;
        var dp = new double[Mx, ns]; var from = new int[Mx, ns]; var em = new double[Mx, ns]; var raw = new double[Mx, ns];
        for (int r = 0; r < My; r++)
        {
            for (int c = 0; c < Mx; c++)
            {
                int i = r * Mx + c;
                for (int si = 0; si < ns; si++)
                {
                    int s = si - K, p = i + s; double e;
                    if (p < 0 || p >= N) e = 3 * Missing;
                    else if (!P.Ok[i]) e = Missing;
                    else
                    {
                        int a = p - Mx;
                        e = a >= 0 && canvas[a] >= 0 && P.Ok[canvas[a]] ? Seam(P, canvas[a], i) : Missing;
                    }
                    if (e > CapE) e = CapE;
                    raw[c, si] = e;
                    e += Lambda * Math.Abs(s);
                    if (s != 0) e += Bias;
                    double best; int bf;
                    if (c == 0) { best = si == carry + K ? 0 : Switch; bf = si; }
                    else
                    {
                        int m = 0; for (int q = 1; q < ns; q++) if (dp[c - 1, q] < dp[c - 1, m]) m = q;
                        best = dp[c - 1, si]; bf = si;
                        if (dp[c - 1, m] + Switch < best) { best = dp[c - 1, m] + Switch; bf = m; }
                    }
                    em[c, si] = e;
                    dp[c, si] = best + e; from[c, si] = bf;
                }
            }
            int cur = 0; for (int q = 1; q < ns; q++) if (dp[Mx - 1, q] < dp[Mx - 1, cur]) cur = q;
            int good = 0;
            for (int c = Mx - 1; c >= 0; c--) { shift[r * Mx + c] = cur - K; if (em[c, cur] - Lambda * Math.Abs(cur - K) - (cur != K ? Bias : 0) < GoodE) good++; cur = from[c, cur]; }
            { double chosen = 0, zero = 0; int cc = cur; // cur is now the state before column 0 after backtracking; recompute from the chosen shifts
              for (int c = 0; c < Mx; c++) { chosen += raw[c, shift[r * Mx + c] + K]; zero += raw[c, K]; }
              if (chosen >= Rel * zero || zero - chosen < MinGain * Mx) for (int c = 0; c < Mx; c++) shift[r * Mx + c] = 0; }
            if (good < GateFrac * Mx) for (int c = 0; c < Mx; c++) shift[r * Mx + c] = carry;      // garbage row: no evidence, keep the carried shift
            for (int c = 0; c < Mx; c++) { int i = r * Mx + c, p = i + shift[i]; if (p >= 0 && p < N && P.Ok[i]) canvas[p] = i; }
            carry = shift[r * Mx + Mx - 1];
        }
        return shift;
    }

    public static Planes Compose(Planes P, int[] canvas)
    {
        var o = new Planes(P.J);
        for (int p = 0; p < P.J.Mcus; p++) { int src = canvas[p] >= 0 ? canvas[p] : p; o.CopyMcu(P, src, p); }
        return o;
    }

    /// <summary>rowshift repaired.jpg original.jpg out.ppm : correct row shifts in a repaired file, report against the original, write [stage 1 | corrected | original | shift map] as a PPM.</summary>
    public static int Run(string repairedPath, string originalPath, string outPpm)
    {
        var P = Planes.Render(File.ReadAllBytes(repairedPath)); var T = Planes.Render(File.ReadAllBytes(originalPath));
        var shift = Detect(P, out var canvas); var C = Compose(P, canvas);
        int N = P.J.Mcus, close0 = 0, close1 = 0, gain = 0, harm = 0, moved = 0, valid = P.Ok.Count(b => b);
        var hist = new SortedDictionary<int, int>();
        for (int m = 0; m < N; m++)
        {
            bool c0 = P.McuError(T, m) <= 6, c1 = C.McuError(T, m) <= 6;
            if (c0) close0++; if (c1) close1++; if (!c0 && c1) gain++; if (c0 && !c1) harm++;
            if (shift[m] != 0 && P.Ok[m]) { moved++; hist[shift[m]] = hist.GetValueOrDefault(shift[m]) + 1; }
        }
        Console.WriteLine($"{Path.GetFileName(repairedPath)}: {N} MCUs ({valid} decoded)  visually close: stage 1 {100.0 * close0 / N:F1}% -> shifted {100.0 * close1 / N:F1}%   gained {gain}, harmed {harm}   MCUs moved {moved}  shifts: " + string.Join(" ", hist.Select(k => $"{k.Key:+#;-#}:{k.Value}")));
        using var f = File.Create(outPpm);
        int W = P.W, H = P.H, W4 = W * 4;
        var hdr = System.Text.Encoding.ASCII.GetBytes($"P6\n{W4} {H}\n255\n"); f.Write(hdr);
        // each panel is written row by row, so render panels to buffers first
        byte[][] panels = new byte[4][];
        foreach (var (pl, k) in new[] { (P, 0), (C, 1), (T, 2) }) { var ms = new MemoryStream(); pl.WriteRgb(ms); panels[k] = ms.ToArray(); }
        var map = new byte[W * H * 3];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
        {
            int s = shift[(y / 16) * P.J.Mx + x / 16]; int o = (y * W + x) * 3; int v = Math.Min(255, 60 + 50 * Math.Abs(s));
            if (s == 0) { map[o] = map[o + 1] = map[o + 2] = 40; } else if (s > 0) { map[o] = (byte)v; } else { map[o + 2] = (byte)v; }
        }
        panels[3] = map;
        for (int y = 0; y < H; y++) for (int k = 0; k < 4; k++) f.Write(panels[k], y * W * 3, W * 3);
        return 0;
    }
}

static class RowFix
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("ROWFIX") == "1";

    /// <summary>The whole stage on in-memory bytes: shift detection, DC re-anchoring, re-encode. Returns the input unchanged if it cannot be processed.</summary>
    public static byte[] Apply(byte[] bytes)
    {
        try
        {
            var P = Planes.Render(bytes); RowShift.Detect(P, out var canvas);
            var img = CoefImage.Decode(bytes, out _, out int rawEnd); if (!DcFix.Off) DcFix.Apply(img, canvas);
            return CoefImage.Assemble(bytes, img, rawEnd, canvas);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException) { return bytes; }
    }

    /// <summary>rowfix repaired.jpg out.jpg [original.jpg]: detect row shifts, move the MCUs in the coefficient domain and re-encode the scan.</summary>
    public static int Run(string inPath, string outPath, string? originalPath)
    {
        var bytes = File.ReadAllBytes(inPath);
        var P = Planes.Render(bytes); var shift = RowShift.Detect(P, out var canvas);
        var img = CoefImage.Decode(bytes, out _, out int rawEnd); int dcAdj = DcFix.Off ? 0 : DcFix.Apply(img, canvas);
        var fixedBytes = CoefImage.Assemble(bytes, img, rawEnd, canvas); File.WriteAllBytes(outPath, fixedBytes);
        int moved = Enumerable.Range(0, shift.Length).Count(i => shift[i] != 0 && P.Ok[i]);
        string rep = "";
        if (originalPath != null)
        {
            var T = Planes.Render(File.ReadAllBytes(originalPath)); var F = Planes.Render(fixedBytes); int N = P.J.Mcus, c0 = 0, c1 = 0, k0 = 0, k1 = 0; double e0 = 0, e1 = 0;
            for (int m = 0; m < N; m++) { e0 += Math.Min(40, P.McuError(T, m) + P.ChromaError(T, m)); e1 += Math.Min(40, F.McuError(T, m) + F.ChromaError(T, m)); }
            for (int m = 0; m < N; m++) { if (P.McuError(T, m) <= 6) c0++; if (F.McuError(T, m) <= 6) c1++; if (P.McuError(T, m) <= 6 && P.ChromaError(T, m) <= 6) k0++; if (F.McuError(T, m) <= 6 && F.ChromaError(T, m) <= 6) k1++; }
            rep = $"  DC re-anchors {dcAdj}  visually close (luma) {100.0 * c0 / N:F1}% -> {100.0 * c1 / N:F1}%   luma+chroma {100.0 * k0 / N:F1}% -> {100.0 * k1 / N:F1}%   capped error {e0 / N:F2} -> {e1 / N:F2}";
        }
        Console.WriteLine($"{Path.GetFileName(inPath)}: moved {moved} MCUs, wrote {Path.GetFileName(outPath)} ({fixedBytes.Length} bytes){rep}");
        return 0;
    }
}

