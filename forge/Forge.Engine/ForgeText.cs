using Forge.Core.Harness;

namespace Forge.Engine;

/// <summary>Plain-language descriptions of what the engine decided. The command line and the app both use these, so a setting reads the
/// same wherever it is shown.</summary>
public static class ForgeText
{
    /// <summary>A typical deck, used for waiting-time estimates before the brief's own slide range is known.</summary>
    public const int TypicalSlides = 10;

    public static string Gb(double gb) => gb >= 10 ? $"{gb:0.0} GB" : $"{gb:0.00} GB";

    public static string Settings(RunSettings s)
    {
        string how = s.Mode switch
        {
            HarnessMode.Arc => "whole story in one reply",
            HarnessMode.OneShot => "whole deck in one reply",
            _ => "slide by slide",
        };
        string guidance = s.UseGuidance ? "constrained decoding" : "no constrained decoding";
        string repairs = s.RepairPasses == 0 ? "no repair passes" : $"up to {s.RepairPasses} repair pass{(s.RepairPasses == 1 ? "" : "es")} of {s.RepairsPerPass}";
        return $"{how} · {guidance} · {repairs}";
    }

    public static string Speed(SpeedLog log, string variant, SpeedEntry? entry, int slides)
    {
        if (entry is null) return "not measured on this PC yet: the first deck measures it";
        string text = $"{entry.DecodeTokensPerSecond:0.0} tok/s decode, constrained decoding {entry.Guidance.ToString().ToLowerInvariant()}";
        return log.Estimate(variant, slides) is { } eta ? $"{text}; about {Provenance.Duration(eta.Seconds)} for {slides} slides ({eta.Basis})" : text;
    }
}
