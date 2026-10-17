namespace HybridIntelligenceStudio;

internal enum TShirtSize { S, M, L, XL }

internal enum BudgetSource { GpuDriverBudget, WindowsVisibleMemory, Custom }

internal enum SizingVerdict { Invalid, Fits, Tight, DoesNotFit }

internal sealed record SizeProfile(
    TShirtSize Size,
    string Name,
    double MinBillions,
    double MaxBillions,
    double DefaultBillions,
    string FamilyExamples,
    double KvGiBPer1kTokens,
    double RuntimeOverheadGiB,
    string TypicalWork);

internal sealed record SizingInput(
    double ParameterBillions,
    int WeightBits,
    double ContextTokens,
    double KvGiBPer1kTokens,
    double RuntimeOverheadGiB,
    double BudgetGiB,
    double ReserveGiB,
    double HeadroomGiB);

internal sealed record SizingResult(
    SizingVerdict Verdict,
    string? Error,
    double WeightGiB,
    double KvGiB,
    double AllowanceGiB,
    double WorkingSetGiB,
    double UsableGiB,
    double RequiredGiB,
    double MarginGiB)
{
    internal bool IsValid => Verdict != SizingVerdict.Invalid;
    internal bool FitsEstimate => Verdict is SizingVerdict.Fits or SizingVerdict.Tight;

    internal string Headline => Verdict switch
    {
        SizingVerdict.Fits => "Fits this budget (estimate)",
        SizingVerdict.Tight => "Fits, but tight (estimate)",
        SizingVerdict.DoesNotFit => "Exceeds this budget (estimate)",
        _ => "Check the inputs"
    };

    internal string Summary => Verdict switch
    {
        SizingVerdict.Invalid => Error ?? "Inputs are invalid.",
        SizingVerdict.DoesNotFit =>
            $"Illustrative estimate: the working set plus headroom needs {RequiredGiB:F1} GiB but only {UsableGiB:F1} GiB is usable after the reserve ({-MarginGiB:F1} GiB short).",
        _ =>
            $"Illustrative estimate: the working set plus headroom needs {RequiredGiB:F1} GiB of {UsableGiB:F1} GiB usable after the reserve ({MarginGiB:F1} GiB spare). Memory fit only; runtime and accelerator support must still be verified."
    };
}

/// <summary>Deterministic, illustrative memory arithmetic. Not a procurement guarantee or support claim.</summary>
internal static class Sizing
{
    internal const double BytesPerGiB = 1073741824d;
    internal const double TightMarginGiB = 1.0;
    private const double Tolerance = 1e-9;

    internal static readonly SizeProfile[] Profiles =
    [
        new(TShirtSize.S, "Small", 1, 4, 3.8, "Phi-4-mini / Qwen3-4B class", 0.14, 1.0,
            "Note drafting, summaries, classification on lighter devices"),
        new(TShirtSize.M, "Medium", 7, 8, 8, "Qwen2.5 7B / Llama 3.1 8B class", 0.13, 1.5,
            "Knowledge drafts, richer summaries, assistant workflows"),
        new(TShirtSize.L, "Large", 14, 20, 14, "Qwen2.5-Coder 14B / GPT-OSS 20B class", 0.19, 2.0,
            "Code drafting, reasoning, longer briefing work"),
        new(TShirtSize.XL, "Extra large", 24, 32, 32, "~24-32B dense class (e.g. Qwen2.5 32B)", 0.25, 2.5,
            "Prototyping heavier models; plan memory carefully"),
    ];

    internal static readonly int[] SupportedBits = [4, 8, 16];

    // Shared defaults: the technical view resets to these and the sales view always uses them.
    internal const int DefaultBits = 4;
    internal const double DefaultContextTokens = 8192;
    internal const double DefaultReserveGiB = 6;
    internal const double DefaultHeadroomGiB = 2;

    internal static SizeProfile Profile(TShirtSize size) => Profiles.First(p => p.Size == size);

    /// <summary>Returns the illustrative band a parameter count falls in, or null when it is between bands.</summary>
    internal static TShirtSize? Classify(double parameterBillions)
    {
        if (!double.IsFinite(parameterBillions)) return null;
        foreach (var profile in Profiles)
            if (parameterBillions >= profile.MinBillions && parameterBillions <= profile.MaxBillions) return profile.Size;
        return null;
    }

    internal static double WeightGiB(double parameterBillions, int bits) =>
        parameterBillions * 1e9 * bits / 8d / BytesPerGiB;

    internal static double KvGiB(double contextTokens, double kvGiBPer1kTokens) =>
        contextTokens / 1000d * kvGiBPer1kTokens;

    /// <summary>
    /// Resolves exactly one memory view. Views are never summed: Windows-visible memory and the GPU
    /// driver budget are different reported views of the same machine. Installed physical memory is not a budget.
    /// </summary>
    internal static double? ResolveBudget(BudgetSource source, DeviceFacts? device, double customGiB) => source switch
    {
        BudgetSource.GpuDriverBudget => device?.PrimaryGpuBudgetMiB is long mib && mib > 0 ? mib / 1024d : null,
        BudgetSource.WindowsVisibleMemory => device?.OsVisibleBytes is long bytes && bytes > 0 ? bytes / BytesPerGiB : null,
        BudgetSource.Custom => double.IsFinite(customGiB) && customGiB > 0 ? customGiB : null,
        _ => null
    };

    internal static SizingResult Estimate(SizingInput input)
    {
        string? error = Validate(input);
        if (error is not null) return Invalid(error);
        double weight = WeightGiB(input.ParameterBillions, input.WeightBits);
        double kv = KvGiB(input.ContextTokens, input.KvGiBPer1kTokens);
        double allowance = kv + input.RuntimeOverheadGiB;
        double working = weight + allowance;
        double usable = input.BudgetGiB - input.ReserveGiB;
        double required = working + input.HeadroomGiB;
        double margin = usable - required;
        if (!double.IsFinite(working) || !double.IsFinite(margin)) return Invalid("The estimate is not a finite number.");
        var verdict = margin < -Tolerance ? SizingVerdict.DoesNotFit
            : margin < TightMarginGiB ? SizingVerdict.Tight
            : SizingVerdict.Fits;
        return new SizingResult(verdict, null, weight, kv, allowance, working, usable, required, margin);
    }

    internal static string? Validate(SizingInput input)
    {
        if (!double.IsFinite(input.ParameterBillions) || input.ParameterBillions <= 0 || input.ParameterBillions > 1000)
            return "Parameter count must be a number above 0 and up to 1,000 billion.";
        if (!SupportedBits.Contains(input.WeightBits)) return "Weight precision must be 4, 8 or 16 bits.";
        if (!double.IsFinite(input.ContextTokens) || input.ContextTokens < 1 || input.ContextTokens > 1_048_576)
            return "Context must be between 1 and 1,048,576 tokens.";
        if (!double.IsFinite(input.KvGiBPer1kTokens) || input.KvGiBPer1kTokens < 0 || input.KvGiBPer1kTokens > 16)
            return "KV-cache allowance must be between 0 and 16 GiB per 1,000 tokens.";
        if (!double.IsFinite(input.RuntimeOverheadGiB) || input.RuntimeOverheadGiB < 0 || input.RuntimeOverheadGiB > 512)
            return "Runtime allowance must be between 0 and 512 GiB.";
        if (!double.IsFinite(input.BudgetGiB) || input.BudgetGiB <= 0)
            return "Memory budget is unavailable or zero. Choose another budget view or enter a custom budget above 0.";
        if (input.BudgetGiB > 4096) return "Memory budget must be 4,096 GiB or less.";
        if (!double.IsFinite(input.ReserveGiB) || input.ReserveGiB < 0)
            return "OS and app reserve must be 0 GiB or more.";
        if (input.ReserveGiB >= input.BudgetGiB)
            return "The OS and app reserve consumes the entire budget; nothing is left for a model.";
        if (!double.IsFinite(input.HeadroomGiB) || input.HeadroomGiB < 0)
            return "Headroom must be 0 GiB or more.";
        return null;
    }

    private static SizingResult Invalid(string error) =>
        new(SizingVerdict.Invalid, error, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);

    internal const string Formula = "weight GiB = parameters (billions) x 1e9 x bits / 8 / 2^30";

    internal const string Caveat =
        "Illustrative planning estimate, not a procurement guarantee. A memory fit does not imply that a model, " +
        "runtime or accelerator provider is supported on a device, and says nothing about quality or throughput. " +
        "Each estimate is for one model resident at a time, not several models together.";
}
