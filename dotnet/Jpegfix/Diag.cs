namespace Jpegfix;

sealed partial class Repairer
{
    // Parameter grid re-ranked offline from recorded cost components.
    public static readonly double[] GridWm = { 0, 0.5, 1, 2, 4 };
    public static readonly int[] GridLook = { 0, 2, 4, 6, 8 };
    public static readonly double[] GridPen = { 4, 8, 14, 20, 30, 40 };
    // lookahead failure penalty: >0 = per remaining lookahead block (the search's FAILPEN), <0 = constant charged once, 0 = none
    public static readonly double[] GridFail = { 25, 12, 6, 0, -10, -20, -40 };
    public const int Classes = 4;       // 0 ins/luma, 1 ins/chroma, 2 no-ins/luma, 3 no-ins/chroma
    const int MaxLook = 8;

    public sealed class DiagStats
    {
        public readonly long[,,,,] Wins = new long[Classes, GridWm.Length, GridLook.Length, GridPen.Length, GridFail.Length];
        public readonly long[] Total = new long[Classes];
        public readonly long[] Unreachable = new long[Classes];
        public readonly long[,] RowTotal = new long[3, Classes], RowWins = new long[3, Classes];   // [MCU row 0 / 1 / 2+, class] at the search defaults
        public int Files, TruthInvalid;
        public void Add(DiagStats o)
        {
            for (int c = 0; c < Classes; c++)
            {
                Total[c] += o.Total[c]; Unreachable[c] += o.Unreachable[c];
                for (int r = 0; r < 3; r++) { RowTotal[r, c] += o.RowTotal[r, c]; RowWins[r, c] += o.RowWins[r, c]; }
                for (int w = 0; w < GridWm.Length; w++) for (int l = 0; l < GridLook.Length; l++) for (int p = 0; p < GridPen.Length; p++) for (int f = 0; f < GridFail.Length; f++)
                    Wins[c, w, l, p, f] += o.Wins[c, w, l, p, f];
            }
            Files += o.Files; TruthInvalid += o.TruthInvalid;
        }
    }

    /// <summary>Decoder-equivalent state key of the true path after each block (see Key); used to tell whether the search beam still contains the true state.</summary>
    public static List<(int, int, int, int, int)> TruthKeys(byte[] good)
    {
        var J = JpegParser.Parse(good);
        var goodU = JpegParser.Unstuff(good, J.ScanStart, out _);
        var zc = new int[goodU.Length + 1];
        for (int i = 0; i < goodU.Length; i++) zc[i + 1] = zc[i] + (goodU[i] == 0x0D ? 1 : 0);
        var dec = new BlockDecoder(J, null, null);
        var S = dec.InitialState(); var w = new byte[Win + 8];
        var keys = new List<(int, int, int, int, int)>();
        for (int n = 0; n < J.Blocks; n++)
        {
            int w0 = S.BitPos >> 3, s0 = S.BitPos & 7;
            for (int t = 0; t < Win + 8; t++) w[t] = w0 + t < goodU.Length ? goodU[w0 + t] : (byte)0;
            if (!dec.EvalBlock(S, w, s0, 8 * goodU.Length - 8 * w0)) break;
            S = dec.MakeChild(S, w0, Array.Empty<int>());
            int goodPos = S.BitPos, byteIdx = goodPos >> 3;
            bool inside = byteIdx < goodU.Length && goodU[byteIdx] == 0x0D && (goodPos & 7) >= 1;
            keys.Add((goodPos - 8 * (zc[byteIdx] + (inside ? 1 : 0)), inside ? (goodPos & 7) + 1 : 0, S.PY, S.PCb, S.PCr));
        }
        return keys;
    }

    sealed class Cand
    {
        public (int, int, int, int, int) Key;
        public int NIns;
        public double C1, M1;
        public readonly double[] Costs = new double[MaxLook], Mdls = new double[MaxLook];
        public int Ok;
        public bool Failed;
    }

    /// <summary>
    /// Follow the true path through a clean file. At every block enumerate all hypotheses exactly as the search does and
    /// check whether the true one (or an equivalent-future one) outranks every wrong one, for each parameter combination.
    /// </summary>
    public static DiagStats Diagnose(byte[] good, Model? model, float[]? refData = null)
    {
        var st = new DiagStats { Files = 1 };
        var J = JpegParser.Parse(good);
        var goodU = JpegParser.Unstuff(good, J.ScanStart, out _);
        var zc = new int[goodU.Length + 1];                          // zc[i] = number of 0x0D in goodU[0..i)
        for (int i = 0; i < goodU.Length; i++) zc[i + 1] = zc[i] + (goodU[i] == 0x0D ? 1 : 0);
        var bad = goodU.Where(b => b != 0x0D).ToArray();
        var dec = new BlockDecoder(J, model, refData);
        var rp = new Repairer(dec, bad);
        var S = dec.InitialState();
        var w = new byte[Win + 8]; var baseW = new byte[Win + 8]; var win2 = new byte[Win + 8];
        var cands = new List<Cand>();
        var okArr = new int[2];
        int nw = GridWm.Length, nl = GridLook.Length, np = GridPen.Length;
        int nf = GridFail.Length;
        int dw = Array.IndexOf(GridWm, Tunables.WM), dl = Array.IndexOf(GridLook, Tunables.Look), dp = Array.IndexOf(GridPen, Tunables.InsPen);
        var minC = new double[np * nf]; var minW = new double[np * nf];

        for (int n = 0; n < J.Blocks; n++)
        {
            int bi = n % 6, w0 = S.BitPos >> 3, s0 = S.BitPos & 7;
            for (int t = 0; t < Win + 8; t++) w[t] = w0 + t < goodU.Length ? goodU[w0 + t] : (byte)0;
            if (!dec.EvalBlock(S, w, s0, 8 * goodU.Length - 8 * w0)) { st.TruthInvalid++; break; }   // true block violates a hard rule
            var truthChild = dec.MakeChild(S, w0, Array.Empty<int>());
            int goodPos = truthChild.BitPos, byteIdx = goodPos >> 3;
            bool inside = byteIdx < goodU.Length && goodU[byteIdx] == 0x0D && (goodPos & 7) >= 1;
            int kTrue = zc[byteIdx] + (inside ? 1 : 0);
            var trueKey = (goodPos - 8 * kTrue, inside ? (goodPos & 7) + 1 : 0, truthChild.PY, truthChild.PCb, truthChild.PCr);
            // enumeration state: the true state expressed in damaged-stream terms (the 0x0D bytes before it are "already inserted")
            bool startsInside = s0 >= 1 && goodU[w0] == 0x0D;
            var E = S.Clone(); E.K = zc[w0] + (startsInside ? 1 : 0); E.RLast = startsInside ? w0 : -1;
            int cls = (kTrue > E.K ? 0 : 2) + (bi < 4 ? 0 : 1);
            st.Total[cls]++;
            int rowc = Math.Min(2, n / 6 / J.Mx); st.RowTotal[rowc, cls]++;

            cands.Clear();
            rp.Enumerate(E, true, baseW, win2, (S2, w0b, newIns) =>
            {
                var cd = new Cand { NIns = newIns.Length, C1 = dec.R.Cost, M1 = dec.R.Mdl };
                var C = dec.MakeChild(S2, w0b, newIns);
                rp.LookFrom(C, MaxLook, cd.Costs, cd.Mdls, okArr);
                cd.Ok = okArr[0]; cd.Failed = okArr[1] == 1; cd.Key = Key(C);
                cands.Add(cd);
            });
            bool any = false; foreach (var c in cands) if (c.Key == trueKey) { any = true; break; }
            if (!any)
            {
                st.Unreachable[cls]++;
                if (Environment.GetEnvironmentVariable("DIAG_DEBUG") != null)
                {
                    var near = cands.Where(c => c.Key.Item3 == trueKey.Item3 && c.Key.Item4 == trueKey.Item4 && c.Key.Item5 == trueKey.Item5)
                        .Select(c => c.Key.Item1 - trueKey.Item1 + "/" + c.Key.Item2 + "/ins" + c.NIns).Take(6);
                    Console.Error.WriteLine($"UNREACH n={n} bi={bi} insInBlock={kTrue - E.K} inside={inside} s0={s0} trueEnd={dec.R.End} trueKey=({trueKey.Item1 - 8 * w0},{trueKey.Item2}) cands={cands.Count} sameDCs=[{string.Join(" ", near)}]");
                }
                break;
            }              // cannot follow the true path further

                        for (int wi = 0; wi < nw; wi++) for (int li = 0; li < nl; li++)
            {
                int L = GridLook[li];
                for (int q = 0; q < np * nf; q++) { minC[q] = double.PositiveInfinity; minW[q] = double.PositiveInfinity; }
                for (int ci = 0; ci < cands.Count; ci++)
                {
                    var c = cands[ci];
                    double b = c.C1 + GridWm[wi] * c.M1;
                    int use = Math.Min(L, c.Ok);
                    for (int j = 0; j < use; j++) b += c.Costs[j] + GridWm[wi] * c.Mdls[j];
                    bool fl = c.Failed && c.Ok < L; bool correct = c.Key == trueKey;
                    for (int fi = 0; fi < nf; fi++)
                    {
                        double bf = b + (fl ? (GridFail[fi] > 0 ? GridFail[fi] * (L - c.Ok) : -GridFail[fi]) : 0);
                        for (int p = 0; p < np; p++)
                        {
                            double r = bf + GridPen[p] * c.NIns; int q = p * nf + fi;
                            if (correct) { if (r < minC[q]) minC[q] = r; } else if (r < minW[q]) minW[q] = r;
                        }
                    }
                }
                for (int p = 0; p < np; p++) for (int fi = 0; fi < nf; fi++) if (minC[p * nf + fi] < minW[p * nf + fi]) { st.Wins[cls, wi, li, p, fi]++; if (wi == dw && li == dl && p == dp && fi == 0) st.RowWins[rowc, cls]++; }
            }
            S = truthChild;
        }
        return st;
    }

}
