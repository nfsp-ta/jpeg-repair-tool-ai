namespace Jpegfix;

/// <summary>Quantised coefficients of every block (zigzag order, index 0 = absolute DC value) so blocks can be moved and the scan re-encoded.</summary>
sealed class CoefImage
{
    public readonly JpegInfo J;
    public readonly short[][] Blk;       // per block: 64 values, zigzag order, [0] = absolute (not differential) DC
    public int Decoded;                  // number of blocks that decoded; the rest are empty
    public CoefImage(JpegInfo j) { J = j; Blk = new short[j.Blocks][]; }

    static int Peek16(byte[] w, int bp) { int i = bp >> 3; return (((w[i] << 16) | (w[i + 1] << 8) | w[i + 2]) >> (8 - (bp & 7))) & 0xFFFF; }

    /// <summary>Plain sequential decode of the scan (no repairs) until a block fails; same validity rules as the search decoder.</summary>
    public static CoefImage Decode(byte[] jpeg, out byte[] unstuffed, out int rawEnd)
    {
        var J = JpegParser.Parse(jpeg); var img = new CoefImage(J);
        var data = JpegParser.Unstuff(jpeg, J.ScanStart, out rawEnd); unstuffed = data;
        var d = new byte[data.Length + 8]; Array.Copy(data, d, data.Length);
        int bp = 0, limit = 8 * data.Length; var pred = new int[3];
        for (int n = 0; n < J.Blocks; n++)
        {
            int bi = n % 6, ci = bi < 4 ? 0 : bi - 3; var q = J.Qz[ci]; var blk = new short[64];
            int e = J.DcLut[ci][Peek16(d, bp)], len = e >> 8; if (len == 0) break;
            int p = bp + len, s = e & 255; if (s > 11) break;
            int dc = pred[ci];
            if (s != 0) { int v = Peek16(d, p) >> (16 - s); p += s; if (v < (1 << (s - 1))) v -= (1 << s) - 1; dc += v; }
            if (dc * q[0] > 1100 || dc * q[0] < -1100) break;
            blk[0] = (short)dc;
            int k = 1; bool ok = true;
            while (k < 64)
            {
                if (p > limit) { ok = false; break; }
                e = J.AcLut[ci][Peek16(d, p)]; len = e >> 8; if (len == 0) { ok = false; break; }
                p += len; int rs = e & 255, r = rs >> 4; s = rs & 15;
                if (s == 0) { if (r == 15) { k += 16; if (k >= 64) { ok = false; break; } continue; } if (r == 0) break; ok = false; break; }
                k += r; if (k > 63 || s > 10) { ok = false; break; }
                int v = Peek16(d, p) >> (16 - s); p += s; if (v < (1 << (s - 1))) v -= (1 << s) - 1;
                if (Math.Abs(v * q[k]) > 1500) { ok = false; break; }
                blk[k++] = (short)v;
            }
            if (!ok || p > limit) break;
            pred[ci] = dc; img.Blk[n] = blk; bp = p; img.Decoded = n + 1;
        }
        return img;
    }

    // ---- encoder (the file's own Huffman tables, recovered from the lookup tables)
    static (int code, int len)[] EncTable(ushort[] lut)
    {
        var t = new (int code, int len)[256];
        for (int j = 0; j < 65536; j++)
            if (lut[j] != 0 && (j == 0 || lut[j - 1] != lut[j])) { int len = lut[j] >> 8; t[lut[j] & 255] = (j >> (16 - len), len); }
        return t;
    }

    sealed class BitWriter
    {
        readonly List<byte> o = new(); uint acc; int n;
        public void Put(int code, int len) { for (int i = len - 1; i >= 0; i--) { acc = (acc << 1) | (uint)((code >> i) & 1); if (++n == 8) { o.Add((byte)acc); acc = 0; n = 0; } } }
        public byte[] Finish() { while (n != 0) { acc = (acc << 1) | 1; if (++n == 8) { o.Add((byte)acc); acc = 0; n = 0; } } return o.ToArray(); }
    }

    static int Category(int v) { v = Math.Abs(v); int s = 0; while (v != 0) { s++; v >>= 1; } return s; }

    /// <summary>
    /// Encode the scan with MCU slot p taking its blocks from MCU <paramref name="canvas"/>[p] (or MCU p when -1 or not decoded; undecoded
    /// MCUs become flat blocks). DC differences are recomputed from the absolute values, so moved blocks keep their own DC.
    /// </summary>
    public byte[] EncodeScan(int[]? canvas)
    {
        var dcT = new (int code, int len)[3][]; var acT = new (int code, int len)[3][];
        for (int i = 0; i < 3; i++) { dcT[i] = EncTable(J.DcLut[i]); acT[i] = EncTable(J.AcLut[i]); }
        var w = new BitWriter(); var pred = new int[3]; var flat = new short[64];
        for (int p = 0; p < J.Mcus; p++)
        {
            int src = canvas != null && canvas[p] >= 0 && canvas[p] * 6 + 5 < Decoded ? canvas[p] : p;
            for (int bi = 0; bi < 6; bi++)
            {
                int ci = bi < 4 ? 0 : bi - 3; var blk = src * 6 + bi < Decoded ? Blk[src * 6 + bi] : flat;
                int dc = blk == flat ? pred[ci] : blk[0], diff = Math.Clamp(dc - pred[ci], -2047, 2047); pred[ci] += diff;
                int s = Category(diff); w.Put(dcT[ci][s].code, dcT[ci][s].len);
                if (s != 0) w.Put(diff >= 0 ? diff : diff + (1 << s) - 1, s);
                int run = 0;
                for (int k = 1; k < 64; k++)
                {
                    int v = blk[k]; if (v == 0) { run++; continue; }
                    while (run > 15) { w.Put(acT[ci][0xF0].code, acT[ci][0xF0].len); run -= 16; }
                    int c = Category(v); int sym = (run << 4) | c; w.Put(acT[ci][sym].code, acT[ci][sym].len);
                    w.Put(v >= 0 ? v : v + (1 << c) - 1, c); run = 0;
                }
                if (run > 0) w.Put(acT[ci][0].code, acT[ci][0].len);
            }
        }
        return JpegParser.Restuff(w.Finish());
    }

    public static byte[] Assemble(byte[] jpeg, CoefImage img, int rawEnd, int[]? canvas)
    {
        var scan = img.EncodeScan(canvas);
        var o = new byte[img.J.ScanStart + scan.Length + (jpeg.Length - rawEnd)];
        Array.Copy(jpeg, o, img.J.ScanStart); scan.CopyTo(o, img.J.ScanStart); Array.Copy(jpeg, rawEnd, o, img.J.ScanStart + scan.Length, jpeg.Length - rawEnd);
        return o;
    }

    /// <summary>Header of the input file + re-encoded scan + the input's tail (EOI).</summary>
    public static byte[] Rewrite(byte[] jpeg, int[]? canvas)
    {
        var img = Decode(jpeg, out _, out int rawEnd);
        var scan = img.EncodeScan(canvas);
        var o = new byte[img.J.ScanStart + scan.Length + (jpeg.Length - rawEnd)];
        Array.Copy(jpeg, o, img.J.ScanStart); scan.CopyTo(o, img.J.ScanStart); Array.Copy(jpeg, rawEnd, o, img.J.ScanStart + scan.Length, jpeg.Length - rawEnd);
        return o;
    }
}
