namespace Jpegfix;

/// <summary>Parsed baseline JPEG header (only the subset this tool supports).</summary>
sealed class JpegInfo
{
    public Dictionary<int, int[]> Qt = new();
    public Dictionary<int, ushort[]> Hf = new();
    public int Width, Height, Precision, Dri, ScanStart;
    public List<(int Id, int H, int V, int Tq)> Comps = new();
    public List<(int Cs, int Td, int Ta)> Scan = new();
    public int Mx, My, Mcus, Blocks;
    public ushort[][] DcLut = new ushort[3][];
    public ushort[][] AcLut = new ushort[3][];
    public int[][] Qz = new int[3][];
}

static class JpegParser
{
    public static readonly int[] ZZ = {
        0,1,8,16,9,2,3,10,17,24,32,25,18,11,4,5,12,19,26,33,40,48,41,34,27,20,13,6,7,14,21,28,
        35,42,49,56,57,50,43,36,29,22,15,23,30,37,44,51,58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63 };

    // (len << 8) | symbol, 0 = invalid
    static ushort[] BuildLut(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> syms)
    {
        var lut = new ushort[65536];
        int code = 0, k = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int i = 0; i < counts[len - 1]; i++)
            {
                int sym = syms[k++];
                int lo = code << (16 - len), hi = (code + 1) << (16 - len);
                for (int j = lo; j < hi && j < 65536; j++) lut[j] = (ushort)((len << 8) | sym);
                code++;
            }
            code <<= 1;
        }
        return lut;
    }

    public static JpegInfo Parse(byte[] buf)
    {
        try { return ParseCore(buf); }
        catch (IndexOutOfRangeException) { throw new InvalidDataException("header damaged (ran past end of data)"); }
        catch (ArgumentException) { throw new InvalidDataException("header damaged (bad segment length)"); }
    }

    static JpegInfo ParseCore(byte[] buf)
    {
        if (buf[0] != 0xFF || buf[1] != 0xD8) throw new InvalidDataException("not a JPEG");
        var J = new JpegInfo();
        int p = 2;
        for (; ; )
        {
            if (buf[p] != 0xFF) throw new InvalidDataException("header damaged (no marker at byte " + p + ")");
            while (buf[p] == 0xFF) p++;
            int m = buf[p++];
            if (m == 0xD9 || m == 0x00) throw new InvalidDataException("unexpected marker before SOS");
            int len = (buf[p] << 8) | buf[p + 1];
            var seg = new ReadOnlySpan<byte>(buf, p + 2, len - 2);
            if (m == 0xDB)
            {
                int o = 0;
                while (o < seg.Length)
                {
                    int pq = seg[o] >> 4, tq = seg[o] & 15; o++;
                    var t = new int[64];
                    for (int i = 0; i < 64; i++) { if (pq != 0) { t[i] = (seg[o] << 8) | seg[o + 1]; o += 2; } else t[i] = seg[o++]; }
                    J.Qt[tq] = t;
                }
            }
            else if (m == 0xC4)
            {
                int o = 0;
                while (o < seg.Length)
                {
                    int tc = seg[o] >> 4, th = seg[o] & 15; o++;
                    var counts = seg.Slice(o, 16); o += 16;
                    int tot = 0; for (int i = 0; i < 16; i++) tot += counts[i];
                    J.Hf[tc * 4 + th] = BuildLut(counts, seg.Slice(o, tot)); o += tot;
                }
            }
            else if (m == 0xC0 || m == 0xC1)
            {
                J.Precision = seg[0]; J.Height = (seg[1] << 8) | seg[2]; J.Width = (seg[3] << 8) | seg[4];
                for (int i = 0; i < seg[5]; i++) J.Comps.Add((seg[6 + 3 * i], seg[7 + 3 * i] >> 4, seg[7 + 3 * i] & 15, seg[8 + 3 * i]));
            }
            else if (m >= 0xC2 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
                throw new InvalidDataException("only baseline JPEG is supported");
            else if (m == 0xDD) J.Dri = (seg[0] << 8) | seg[1];
            else if (m == 0xDA)
            {
                for (int i = 0; i < seg[0]; i++) J.Scan.Add((seg[1 + 2 * i], seg[2 + 2 * i] >> 4, seg[2 + 2 * i] & 15));
                J.ScanStart = p + len;
                break;
            }
            p += len;
        }
        if (J.Dri != 0) throw new InvalidDataException("restart markers present (not supported)");
        if (J.Precision != 8 || J.Comps.Count != 3 || J.Scan.Count != 3) throw new InvalidDataException("only 8-bit 3-component images supported");
        var (y, cb, cr) = (J.Comps[0], J.Comps[1], J.Comps[2]);
        if (!(y.H == 2 && y.V == 2 && cb.H == 1 && cb.V == 1 && cr.H == 1 && cr.V == 1))
            throw new InvalidDataException("only 4:2:0 (2x2,1x1,1x1) supported");
        J.Mx = (J.Width + 15) / 16; J.My = (J.Height + 15) / 16;
        J.Mcus = J.Mx * J.My; J.Blocks = J.Mcus * 6;
        for (int i = 0; i < 3; i++)
        {
            var sc = J.Scan[i];
            var comp = J.Comps.First(c => c.Id == sc.Cs);
            if (!J.Hf.TryGetValue(sc.Td, out var dc) || !J.Hf.TryGetValue(4 + sc.Ta, out var ac) || !J.Qt.TryGetValue(comp.Tq, out var qz))
                throw new InvalidDataException("missing table");
            J.DcLut[i] = dc; J.AcLut[i] = ac; J.Qz[i] = qz;
        }
        return J;
    }

    /// <summary>Strip byte stuffing (FF 00 -> FF). <paramref name="end"/> = raw index of EOI/end of data.</summary>
    public static byte[] Unstuff(byte[] buf, int s, out int end)
    {
        int e = buf.Length;
        if (e >= 2 && buf[e - 2] == 0xFF && buf[e - 1] == 0xD9) e -= 2;
        var out_ = new byte[e - s]; int o = 0;
        for (int i = s; i < e; i++)
        {
            byte b = buf[i]; out_[o++] = b;
            if (b == 0xFF) { i++; if (i < e && buf[i] != 0x00) throw new InvalidDataException("unexpected marker inside scan at " + (i - 1)); }
        }
        end = e;
        return out_.AsSpan(0, o).ToArray();
    }

    public static byte[] Restuff(byte[] u)
    {
        int n = u.Length; foreach (var b in u) if (b == 0xFF) n++;
        var o = new byte[n]; int j = 0;
        foreach (var b in u) { o[j++] = b; if (b == 0xFF) o[j++] = 0; }
        return o;
    }
}
