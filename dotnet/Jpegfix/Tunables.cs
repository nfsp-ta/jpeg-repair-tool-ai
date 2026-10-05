using System.Globalization;

namespace Jpegfix;

/// <summary>Search constants. LOOK, INS_PEN, W_M, FAILPEN, REFW, METRIC can be overridden by environment variables.</summary>
static class Tunables
{
    static double Env(string name, double def) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

    public static readonly double WM = Env("W_M", 1);          // weight of the coefficient-statistics term
    public static readonly int Look = (int)Env("LOOK", 2);       // blocks of lookahead used to rank a hypothesis
    public static readonly double FailPen = Env("FAILPEN", 25);  // per un-decodable lookahead block
    public static readonly double InsPen = Env("INS_PEN", 40);    // cost charged per inserted byte
    public static readonly double RefW = Env("REFW", 1);
    public static readonly string Chroma = Environment.GetEnvironmentVariable("CHROMA") ?? "seam";  // chroma block score: dc | seam | both
    public static readonly bool PlainMetric = (Environment.GetEnvironmentVariable("METRIC") ?? "grad") == "plain";

    // Beam selection: children of one parent are ranked by cost + full lookahead and at most PerParent survive; parents then compete
    // on cost + LookW * lookahead (LookW=1, PerParent large = the original single-level behaviour).
    public static readonly double LookW = Env("LOOKW", 0.25);
    public static readonly int PerParent = (int)Env("PERPARENT", 1000);

    // Wider beam for the first MCU rows, where blocks have no row above and only the left neighbour to judge them by.
    public static readonly int FirstMul = (int)Env("FIRSTMUL", 1);
    public static readonly int FirstRows = (int)Env("FIRSTROWS", 1);

    // Adaptive beam: besides the BeamW best states, keep every state whose selection score is within BeamDelta of the best, up to BeamMax states.
    public static readonly double BeamDelta = Env("BEAMDELTA", 80);
    public static readonly int BeamMax = (int)Env("BEAMMAX", 64);

    // Stage 2 (Refine): edit the insertion list (move, drop, add near an existing insertion) when a window of S2WIN blocks scores better, with S2PEN per net insertion.
    public static readonly bool Refine = Env("REFINE", 0) != 0;
    public static readonly double S2Pen = Env("S2PEN", 5);
    public static readonly int S2Win = (int)Env("S2WIN", 12);
    public static readonly int S2Range = (int)Env("S2RANGE", 24);
    public static readonly int S2Passes = (int)Env("S2PASSES", 3);

    public const int Win = 400;          // bytes of lookahead window per block decode
    public const double Cap = 60;        // cap on the cost of any single block
    public const double PairTrigger = 22;
    public const int PairRank = 2;
    public const int PairSpan = 56;
}
