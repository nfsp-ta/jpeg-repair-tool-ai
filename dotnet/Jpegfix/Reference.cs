namespace Jpegfix;

sealed partial class Repairer
{
    /// <summary>Decode a clean JPEG sequentially into full-resolution Y and half-resolution Cb/Cr planes (padded to whole MCUs).</summary>
    static int DecodePlanes(byte[] clean, out JpegInfo J, out byte[] Y, out byte[] Cb, out byte[] Cr)
    {
        J = JpegParser.Parse(clean);
        var data = JpegParser.Unstuff(clean, J.ScanStart, out _);
        var dec = new BlockDecoder(J, null, null);
        var rp = new Repairer(dec, data);
        int W = J.Mx * 16, CW = J.Mx * 8;
        Y = new byte[W * J.My * 16]; Cb = new byte[CW * J.My * 8]; Cr = new byte[CW * J.My * 8];
        var S = dec.InitialState(); var w = new byte[Win + 8]; int doneBlocks = 0;
        for (int n = 0; n < J.Blocks; n++)
        {
            rp.FillWindow(S, w);
            if (S.BitPos >> 3 >= data.Length || !dec.EvalBlock(S, w, S.BitPos & 7, 8 * data.Length - 8 * (S.BitPos >> 3))) break;     // a damaged sibling: use what decoded, the rest is unknown (NaN in the reference)
            doneBlocks = n + 1;
            int bi = n % 6, mcu = n / 6, mx = mcu % J.Mx, my = mcu / J.Mx;
            byte[] plane; int stride, x0, y0;
            if (bi < 4) { plane = Y; stride = W; x0 = mx * 16 + (bi & 1) * 8; y0 = my * 16 + (bi >> 1) * 8; }
            else { plane = bi == 4 ? Cb : Cr; stride = CW; x0 = mx * 8; y0 = my * 8; }
            for (int r = 0; r < 8; r++) Array.Copy(dec.R.Pix, r * 8, plane, (y0 + r) * stride + x0, 8);
            S = dec.MakeChild(S, S.BitPos >> 3, Array.Empty<int>());
        }
        return doneBlocks / 6;
    }

    static double Bilinear(byte[] p, int stride, int w, int h, double fx, double fy)
    {
        fx = Math.Clamp(fx, 0, w - 1); fy = Math.Clamp(fy, 0, h - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        double ax = fx - x0, ay = fy - y0;
        return (p[y0 * stride + x0] * (1 - ax) + p[y0 * stride + x1] * ax) * (1 - ay) + (p[y1 * stride + x0] * (1 - ax) + p[y1 * stride + x1] * ax) * ay;
    }

    /// <summary>
    /// Reference for repairing <paramref name="target"/> from a clean (or already repaired) sibling image of the same picture at another
    /// size: the mean of the resampled sibling over each 8x8 block of the target. Layout matches --ref: luma block means
    /// (2*Mx x 2*My), then Cb, then Cr block means (Mx x My).
    /// </summary>
    public static float[] BuildReference(byte[] sibling, JpegInfo target)
    {
        int okMcus = DecodePlanes(sibling, out var S, out var Y, out var Cb, out var Cr);
        bool Unknown(double tx, double ty) { int mx = (int)(tx * (double)S.Width / target.Width) / 16, my = (int)(ty * (double)S.Height / target.Height) / 16; return Math.Min(my, S.My - 1) * S.Mx + Math.Min(mx, S.Mx - 1) >= okMcus; }
        int SW = S.Mx * 16, SCW = S.Mx * 8;
        int lw = S.Width, lh = S.Height, cw = (S.Width + 1) / 2, ch = (S.Height + 1) / 2;
        double sx = (double)S.Width / target.Width, sy = (double)S.Height / target.Height;
        int gx = target.Mx * 2, gy = target.My * 2, cx = target.Mx, cy = target.My;
        var res = new float[gx * gy + 2 * cx * cy];
        for (int by = 0; by < gy; by++) for (int bx = 0; bx < gx; bx++)
        {
            double sum = 0;
            for (int r = 0; r < 8; r++) for (int c = 0; c < 8; c++)
                sum += Bilinear(Y, SW, lw, lh, (bx * 8 + c + 0.5) * sx - 0.5, (by * 8 + r + 0.5) * sy - 0.5);
            res[by * gx + bx] = (float)(sum / 64);
            if (Unknown(bx * 8 + 4, by * 8 + 4)) res[by * gx + bx] = float.NaN;
        }
        for (int comp = 0; comp < 2; comp++)
        {
            var plane = comp == 0 ? Cb : Cr; int off = gx * gy + comp * cx * cy;
            for (int by = 0; by < cy; by++) for (int bx = 0; bx < cx; bx++)
            {
                double sum = 0;
                for (int r = 0; r < 8; r++) for (int c = 0; c < 8; c++)
                {
                    double X = (2 * (bx * 8 + c) + 1.0) * sx, Yc = (2 * (by * 8 + r) + 1.0) * sy;       // target luma coordinates of the chroma sample centre
                    sum += Bilinear(plane, SCW, cw, ch, X / 2 - 0.5, Yc / 2 - 0.5);
                }
                res[off + by * cx + bx] = (float)(sum / 64);
                if (Unknown(bx * 16 + 8, by * 16 + 8)) res[off + by * cx + bx] = float.NaN;
            }
        }
        return res;
    }
}

sealed partial class Repairer
{
    /// <summary>
    /// Compare a repaired JPEG with the original block by block (decoded pixels): returns how many leading blocks are identical
    /// and how many blocks overall are identical, and how many are visually close (mean pixel error <= 6) (a block shifted by a missed byte counts as wrong; blocks after a stall count as wrong).
    /// </summary>
    public static void CompareBlocks(byte[] repaired, byte[] good, out int prefix, out int equal, out int total, out int close)
    {
        var J = JpegParser.Parse(good);
        var dg = JpegParser.Unstuff(good, J.ScanStart, out _);
        var dr = JpegParser.Unstuff(repaired, J.ScanStart, out _);
        var decG = new BlockDecoder(J, null, null); var decR = new BlockDecoder(J, null, null);
        var rg = new Repairer(decG, dg); var rr = new Repairer(decR, dr);
        var sg = decG.InitialState(); var sr = decR.InitialState();
        var wg = new byte[Win + 8]; var wr = new byte[Win + 8];
        total = J.Blocks; prefix = 0; equal = 0; close = 0; bool inPrefix = true, rOk = true;
        for (int n = 0; n < J.Blocks; n++)
        {
            rg.FillWindow(sg, wg);
            decG.EvalBlock(sg, wg, sg.BitPos & 7, 8 * dg.Length - 8 * (sg.BitPos >> 3));
            var pg = (byte[])decG.R.Pix.Clone(); int dcG = decG.R.Dc;
            sg = decG.MakeChild(sg, sg.BitPos >> 3, Array.Empty<int>());
            bool same = false, near = false;
            if (rOk)
            {
                rr.FillWindow(sr, wr);
                if (decR.EvalBlock(sr, wr, sr.BitPos & 7, 8 * dr.Length - 8 * (sr.BitPos >> 3)))
                {
                    same = decR.R.Dc == dcG && decR.R.Pix.AsSpan().SequenceEqual(pg);
                    int ad = 0; for (int k = 0; k < 64; k++) ad += Math.Abs(decR.R.Pix[k] - pg[k]);
                    near = ad <= 6 * 64;                       // mean absolute pixel error of at most 6 levels: visually the same
                    sr = decR.MakeChild(sr, sr.BitPos >> 3, Array.Empty<int>());
                }
                else rOk = false;
            }
            if (near) close++;
            if (same) { equal++; if (inPrefix) prefix++; } else inPrefix = false;
        }
    }
}
