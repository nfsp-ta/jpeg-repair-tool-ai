namespace Jpegfix;

/// <summary>Poor man's profiler (PROF=1): inclusive time per hot spot, printed at the end of a repair. Off by default (the checks are static readonly).</summary>
static class Prof
{
    public static readonly bool On = Environment.GetEnvironmentVariable("PROF") == "1";
    public static readonly string[] Names = { "EvalBlock", "Idct", "MakeChild", "LookFrom", "Prune", "Enumerate (all)", "Consider (all)" };
    static readonly long[] ticks = new long[8], calls = new long[8];
    static readonly HashSet<(int, int, int, int, int, int)> seen = new(); static long seenCalls;
    public static void Seen((int, int, int, int, int, int) k) { seenCalls++; seen.Add(k); }
    public static long Start() => On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
    public static void Stop(int i, long t0) { if (On) { ticks[i] += System.Diagnostics.Stopwatch.GetTimestamp() - t0; calls[i]++; } }
    public static void Report(double totalSeconds)
    {
        if (!On) return;
        Console.Error.WriteLine($"PROF decode cache: {BlockDecoder.CacheHits:N0} hits, {BlockDecoder.CacheMisses:N0} misses");
        Console.Error.WriteLine($"PROF lookahead block decodes {seenCalls:N0}, distinct decoder states {seen.Count:N0} ({100.0 * seen.Count / Math.Max(1, seenCalls):F0}%)");
        for (int i = 0; i < Names.Length; i++)
            Console.Error.WriteLine($"PROF {Names[i],-16} {ticks[i] / (double)System.Diagnostics.Stopwatch.Frequency,8:F2}s ({100.0 * ticks[i] / System.Diagnostics.Stopwatch.Frequency / totalSeconds,5:F1}% of {totalSeconds:F0}s)  {calls[i],12:N0} calls  {(calls[i] == 0 ? 0 : 1e6 * ticks[i] / System.Diagnostics.Stopwatch.Frequency / calls[i]),7:F2} us each");
    }
}
