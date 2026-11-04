using Forge.Core.Harness;
using Forge.Core.Ir;

namespace Forge.Engine;

/// <summary>What the person asked for in the app. Null means "choose for me".</summary>
public sealed record RunRequest(HarnessMode? Mode = null, int? RepairPasses = null, bool? UseGuidance = null);

/// <summary>The harness settings for one run, with the reason for each so the app can show why this PC and this model got them.</summary>
public sealed record RunSettings(HarnessMode Mode, int RepairPasses, int RepairsPerPass, bool UseGuidance, IReadOnlyList<string> Why)
{
    public HarnessOptions ToOptions(Theme? theme = null) => new()
    {
        Mode = Mode,
        MaxRepairPasses = RepairPasses,
        MaxRepairsPerPass = RepairsPerPass,
        UseGuidance = UseGuidance,
        Theme = theme,
    };
}

/// <summary>Chooses how the harness runs for the model that is loaded. The default is the same on every device, slide by slide, because that is
/// the mode that works for any model. The rest adapts to what was measured: a model that decodes slowly gets a smaller repair budget (each repair
/// is another wait), and a model that refused constrained decoding is not asked again. The whole-deck modes are only offered to large models
/// with the window to hold the deck.</summary>
public static class RunPolicy
{
    public const double SlowDecodeTokensPerSecond = 12;
    public const int ArcWindowTokens = 16000;
    public const int OneShotWindowTokens = 12000;

    public static RunSettings Decide(ModelFacts model, SpeedEntry? speed, RunRequest? request = null)
    {
        request ??= new RunRequest();
        var why = new List<string>();

        var mode = HarnessMode.Guided;
        if (request.Mode is { } asked and not HarnessMode.Guided)
        {
            int needed = asked == HarnessMode.Arc ? ArcWindowTokens : OneShotWindowTokens;
            string what = asked == HarnessMode.Arc ? "Whole story in one reply" : "Whole deck in one reply";
            bool large = model.Class is "L" or "XL";
            if (!large) why.Add($"{what} needs a large model; {model.Title} is a {ClassWords(model.Class)} one, so the deck is written slide by slide.");
            else if (model.ContextTokens <= 0) why.Add($"{what} needs a window of {needed:N0} tokens and {model.Title}'s is not known, so the deck is written slide by slide.");
            else if (model.ContextTokens < needed) why.Add($"{what} needs a window of {needed:N0} tokens; {model.Title} has {model.ContextTokens:N0}, so the deck is written slide by slide.");
            else
            {
                mode = asked;
                why.Add($"{what}, as asked: {model.Title} has a {model.ContextTokens:N0}-token window.");
            }
        }
        if (mode == HarnessMode.Guided)
            why.Add("Slide by slide: one short call per slide, so any model on any device can do it and a bad slide costs one call.");

        int passes = request.RepairPasses is { } asks ? Math.Clamp(asks, 0, 3) : 2;
        int perPass = 6;
        if (speed is { DecodeTokensPerSecond: > 0 and < SlowDecodeTokensPerSecond })
        {
            perPass = 3;
            if (request.RepairPasses is null) passes = 1;
            why.Add($"{model.Title} decodes at about {speed.DecodeTokensPerSecond:0} tokens per second here, so repairs are limited to {passes} pass{(passes == 1 ? "" : "es")} of up to {perPass} slides to keep the wait short.");
        }
        else why.Add(passes == 0 ? "No repair passes." : $"Up to {passes} repair pass{(passes == 1 ? "" : "es")} of up to {perPass} slides each.");

        bool guidance = request.UseGuidance ?? true;
        if (request.UseGuidance == false) why.Add("Constrained decoding is off, as asked: slides are checked and repaired instead.");
        else if (speed?.Guidance == GuidanceState.Rejected && request.UseGuidance != true)
        {
            guidance = false;
            why.Add($"{model.Title} refused constrained decoding on this PC, so it is off: slides are checked and repaired instead.");
        }
        else if (speed?.Guidance == GuidanceState.Rejected) why.Add("Trying constrained decoding again, as asked.");
        else if (speed?.Guidance == GuidanceState.Verified) why.Add("Constrained decoding is on and was verified on this model: every slide comes back as valid JSON.");
        else why.Add("Constrained decoding is on; the first slide shows whether this model accepts it.");

        return new RunSettings(mode, passes, perPass, guidance, why);
    }

    private static string ClassWords(string cls) => cls switch
    {
        "XS" => "very small",
        "S" => "small",
        "M" => "medium-sized",
        "L" => "large",
        "XL" => "extra-large",
        _ => "smaller",
    };
}
