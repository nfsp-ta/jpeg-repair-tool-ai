namespace Jpegfix;

/// <summary>
/// The real gallery files were damaged by a text-mode transfer that inserted a 0x0D before every 0x0A (LF -> CRLF). Lone 0x0D bytes of the
/// original data are untouched. Undoing it = remove one 0x0D that directly precedes a 0x0A. (If the converter did not add a CR where one
/// was already present, an original CRLF pair is wrongly stripped: that is a single missing 0x0D, which the beam search can repair.)
/// </summary>
public static class Unmangle
{
    public static byte[] Apply(byte[] b)
    {
        var o = new byte[b.Length]; int n = 0;
        for (int i = 0; i < b.Length; i++) { if (b[i] == 0x0D && i + 1 < b.Length && b[i + 1] == 0x0A) continue; o[n++] = b[i]; }
        return o.AsSpan(0, n).ToArray();
    }

    public static int CountMarks(byte[] b) { int n = 0; for (int i = 0; i + 1 < b.Length; i++) if (b[i] == 0x0D && b[i + 1] == 0x0A) n++; return n; }

    /// <summary>How trustworthy a (possibly repaired) file is, without the original: parse, decode every block, and check the entropy data ends exactly where the file ends.</summary>
    public static string Check(byte[] b, out int blocks, out int decoded)
    {
        blocks = decoded = 0;
        JpegInfo J;
        try { J = JpegParser.Parse(b); } catch (InvalidDataException ex) { return "header: " + System.Text.RegularExpressions.Regex.Replace(ex.Message, @"\d+", "N"); }
        try
        {
            var img = CoefImage.Decode(b, out _, out _); blocks = J.Blocks; decoded = img.Decoded;
            if (img.Decoded < J.Blocks) return "partial";
            int left = img.DataBits - img.EndBit;
            return left >= 0 && left < 8 ? "consistent" : "inconsistent-end";
        }
        catch (InvalidDataException ex) { return "scan: " + System.Text.RegularExpressions.Regex.Replace(ex.Message, @"\d+", "N"); }
    }
}
