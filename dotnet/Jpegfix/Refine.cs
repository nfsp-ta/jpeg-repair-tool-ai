namespace Jpegfix;

/// <summary>
/// Stage 2: refine the insertion list found by the beam search. For each insertion try moving it, dropping it, or adding one
/// beside it, and keep the edit whose window of following blocks scores best, charging S2PEN per net insertion (much lower
/// than the search's INS_PEN, because the evidence from the blocks that follow is now available).
/// </summary>
sealed partial class Repairer
{
    const double FailBlock = Tunables.Cap;      // cost of a block that does not decode

    sealed class Decoded
    {
        public State[] St = null!;
        public double[] Sc = null!, Cum = null!;
        public int[] StartBad = null!;
        public int Fail = -1;
        public int DataLen;
    }

    static Decoded DecodeAll(BlockDecoder dec, byte[] bad, List<int> list)
    {
        int N = dec.J.Blocks; var rep = Perturb.Build(bad, list);
        var d = new Decoded { St = new State[N + 1], Sc = new double[N], Cum = new double[N + 1], StartBad = new int[N], DataLen = bad.Length + list.Count };
        var S = dec.InitialState(); var w = new byte[Win + 8]; int k = 0;
        for (int n = 0; n < N; n++)
        {
            d.St[n] = S; int w0 = S.BitPos >> 3;
            while (k < list.Count && list[k] + k < w0) k++;
            d.StartBad[n] = w0 - k;
            Array.Copy(rep, w0, w, 0, Math.Min(w.Length, rep.Length - w0));
            if (!dec.EvalBlock(S, w, S.BitPos & 7, 8 * d.DataLen - 8 * w0))
            {
                d.Fail = n;
                for (int m = n; m < N; m++) { d.Sc[m] = FailBlock; d.StartBad[m] = int.MaxValue; d.St[m] = S; }
                break;
            }
            d.Sc[n] = dec.BScore();
            S = dec.MakeChild(S, w0, Array.Empty<int>());
        }
        d.St[N] = S;
        for (int n = 0; n < N; n++) d.Cum[n + 1] = d.Cum[n] + d.Sc[n];
        return d;
    }

    /// <summary>Window score of the candidate list minus that of the current list, or NaN when the edit changes nothing observable.</summary>
    static double EvalCandidate(BlockDecoder dec, byte[] bad, Decoded cur, List<int> cand, int eMin, int W, byte[] w)
    {
        int N = dec.J.Blocks;
        int lo = 0, hi = N - 1, b = 0;           // last block whose start (in damaged-stream bytes) lies before the edit
        while (lo <= hi) { int mid = (lo + hi) / 2; if (cur.StartBad[mid] < eMin) { b = mid; lo = mid + 1; } else hi = mid - 1; }
        var rep = Perturb.Build(bad, cand); int dataLen = bad.Length + cand.Count;
        var S = cur.St[b]; bool failed = false; int d0 = -1, end = N - 1; double sum = 0;
        for (int n = b; n <= end; n++)
        {
            double s;
            if (failed) s = FailBlock;
            else
            {
                int w0 = S.BitPos >> 3;
                Array.Copy(rep, w0, w, 0, Math.Min(w.Length, rep.Length - w0));
                if (!dec.EvalBlock(S, w, S.BitPos & 7, 8 * dataLen - 8 * w0)) { failed = true; s = FailBlock; }
                else { s = dec.BScore(); S = dec.MakeChild(S, w0, Array.Empty<int>()); }
            }
            if (d0 < 0)
            {
                if (s == cur.Sc[n]) { if (n - b > 8) return double.NaN; continue; }
                d0 = n; end = Math.Min(N - 1, n + W);
            }
            sum += s;
        }
        if (d0 < 0) return double.NaN;
        return sum - (cur.Cum[end + 1] - cur.Cum[d0]);
    }

    static bool Ends(Decoded d, int N) { int left = 8 * d.DataLen - d.St[N].BitPos; return d.Fail < 0 && left >= 0 && left < 8; }

    public static RepairResult Refine(RepairResult res, Model? model, float[]? refData = null)
    {
        var dec = new BlockDecoder(res.J, model, refData);
        var bad = res.Bad; var list = new List<int>(res.List); list.Sort();
        int N = res.J.Blocks, M = Tunables.S2Range, W = Tunables.S2Win; double pen = Tunables.S2Pen;
        var w = new byte[Win + 8];
        var cur = DecodeAll(dec, bad, list);
        for (int pass = 0; pass < Tunables.S2Passes; pass++)
        {
            bool changed = false;
            for (int i = 0; i < list.Count; i++)
            {
                int p = list[i]; double best = -1e-9; List<int>? bestL = null;
                void Try(List<int> cand, int eMin, int dCount)
                {
                    double diff = EvalCandidate(dec, bad, cur, cand, eMin, W, w);
                    if (double.IsNaN(diff)) return;
                    diff += pen * dCount;
                    if (diff < best) { best = diff; bestL = cand; }
                }
                var c0 = new List<int>(list); c0.RemoveAt(i); Try(c0, p, -1);
                for (int d = -M; d <= M; d++)
                {
                    int q = p + d; if (q < 0 || q > bad.Length) continue;
                    if (d != 0) { var c = new List<int>(list); c[i] = q; c.Sort(); Try(c, Math.Min(p, q), 0); }
                    var a = new List<int>(list) { q }; a.Sort(); Try(a, q, 1);
                }
                if (bestL != null)
                {
                    var nd = DecodeAll(dec, bad, bestL);
                    // the window cannot see damage further downstream: never accept an edit that makes the decode fail earlier or end inconsistent
                    bool okFail = cur.Fail < 0 ? nd.Fail < 0 : (nd.Fail < 0 || nd.Fail >= cur.Fail);
                    bool okEnd = !Ends(cur, N) || Ends(nd, N);
                    if (okFail && okEnd) { list = bestL; cur = nd; changed = true; }
                }
            }
            if (!changed) break;
        }
        int left = 8 * cur.DataLen - cur.St[N].BitPos;
        return new RepairResult
        {
            J = res.J, Bad = bad, RawEnd = res.RawEnd, List = list, StuckAt = cur.Fail, Total = cur.Fail >= 0 ? cur.Fail : N,
            Verified = cur.Fail < 0 && left >= 0 && left < 8, Seconds = res.Seconds, LostAt = res.LostAt, TimedOut = res.TimedOut,
        };
    }
}
