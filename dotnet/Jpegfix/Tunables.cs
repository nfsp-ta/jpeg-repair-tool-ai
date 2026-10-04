using System.Globalization;

namespace Jpegfix;

/// <summary>Search constants. LOOK, INS_PEN, W_M, FAILPEN, REFW, METRIC can be overridden by environment variables.</summary>
static class Tunables
{
    static double Env(string name, double def) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

    public static readonly double WM = Env("W_M", 1);          // weight of the coefficient-statistics term
    public static readonly int Look = (int)Env("LOOK", 4);       // blocks of lookahead used to rank a hypothesis
    public static readonly double FailPen = Env("FAILPEN", 25);  // per un-decodable lookahead block
    public static readonly double InsPen = Env("INS_PEN", 20);    // cost charged per inserted byte
    public static readonly double RefW = Env("REFW", 1);
    public static readonly string Chroma = Environment.GetEnvironmentVariable("CHROMA") ?? "seam";  // chroma block score: dc | seam | both
    public static readonly bool PlainMetric = (Environment.GetEnvironmentVariable("METRIC") ?? "grad") == "plain";

    public const int Win = 400;          // bytes of lookahead window per block decode
    public const double Cap = 60;        // cap on the cost of any single block
    public const double PairTrigger = 22;
    public const int PairRank = 2;
    public const int PairSpan = 56;
}
