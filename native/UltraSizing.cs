namespace HybridIntelligenceStudio;

/// <summary>How each configuration's memory is assumed to split between dedicated GPU memory and Windows.</summary>
internal enum SplitBasis { MicrosoftLaunch, MeasuredThisPc, Custom }

internal enum UltraFit { Unknown, Roomy, LittleRoom, WontFit }

/// <summary>A Surface Laptop Ultra memory configuration from Microsoft's published tech specs.</summary>
internal sealed record UltraConfiguration(int MemoryGB, string Processor);

/// <summary>One AI model the selected workloads keep on the GPU; several workloads can share one model.</summary>
internal sealed record UltraModel(string Key, TShirtSize Size, IReadOnlyList<SalesWorkload> Workloads);

internal sealed record UltraConfigFit(
    UltraConfiguration Config, double GpuGiB, double WindowsGiB, bool MeasuredHere,
    UltraFit Fit, double PercentUsed, SizingResult? Result);

internal sealed record UltraPlan(
    IReadOnlyList<SalesWorkload> Workloads,
    IReadOnlyList<UltraModel> Models,
    bool Together,
    bool LongDocuments,
    SplitBasis Basis,
    double GpuShare,
    IReadOnlyList<UltraConfigFit> Configs,
    UltraConfigFit? Recommended,
    UltraConfigFit? SmallestThatHolds,
    string Headline,
    string TalkTrack)
{
    internal UltraConfigFit For(int memoryGB) => Configs.First(c => c.Config.MemoryGB == memoryGB);
}

/// <summary>
/// Personalizes the Surface Laptop Ultra memory configuration for the selected sales workloads. Uses the same
/// <see cref="Sizing.Estimate"/> arithmetic and defaults as the sales and technical views, against each
/// configuration's assumed dedicated GPU memory. Memory-only estimate: no pricing, speed or support claims.
/// </summary>
internal static class UltraSizing
{
    /// <summary>Memory options from Microsoft's tech specs. Processor pairings are from launch coverage.</summary>
    internal static readonly UltraConfiguration[] Configurations =
    [
        new(24, "Entry configuration at launch: 18-core CPU, 5,120-core GPU."),
        new(32, "Processor varies; check the configurator."),
        new(48, "Processor varies; check the configurator."),
        new(64, "Processor varies; check the configurator."),
        new(128, "20-core CPU, 6,144-core GPU at launch."),
    ];

    // Microsoft's launch Task Manager screenshot of a 128 GB unit shows 79.9 GB of dedicated GPU memory.
    // Task Manager's GB are binary (GiB), so the share is 79.9 / 128.
    internal const double LaunchDedicatedGiB = 79.9;
    internal const double LaunchInstalledGiB = 128;
    internal const double LaunchShare = LaunchDedicatedGiB / LaunchInstalledGiB;
    internal const double MinCustomPercent = 10, MaxCustomPercent = 90, DefaultCustomPercent = 50;

    /// <summary>A configuration is recommended only when the estimate uses at most this share of the memory left for AI.</summary>
    internal const double RecommendMaxPercent = 80;
    internal const double LongDocumentTokens = 32768;

    internal const string Footnote =
        "Memory options from Microsoft's Surface Laptop Ultra tech specs: 24, 32, 48, 64 or 128 GB. " +
        "Same arithmetic as the Technical view: 4-bit weights, an 8,192-token context (32,768 for long documents), " +
        "a 6 GiB reserve for Windows, apps and other GPU work, and 2 GiB of headroom. " +
        "A configuration is recommended only when the estimate uses at most 80% of the memory left for AI, because real 4-bit model files " +
        "are larger than this estimate: the Llama 3.3 70B file this app loads is 39.6 GiB against 32.6 GiB estimated. " +
        "Writing, translation and documents share one general model; every other workload counts as its own model. " +
        "Shared GPU memory is never counted. GB are as Task Manager shows them. " +
        "Memory-only estimate: it says nothing about price, response time or model support. Confirm processor pairings in the configurator.";

    internal static string ModelKey(SalesWorkload w) => w.Id is "writing" or "translation" or "documents" ? "general" : w.Id;

    internal static IReadOnlyList<UltraModel> Models(IEnumerable<SalesWorkload> workloads) =>
        workloads.GroupBy(ModelKey).Select(g => new UltraModel(g.Key, g.Max(w => w.Size), g.ToArray())).ToArray();

    internal static double? Share(SplitBasis basis, DeviceFacts? device, double customPercent) => basis switch
    {
        SplitBasis.MicrosoftLaunch => LaunchShare,
        SplitBasis.MeasuredThisPc => MeasuredShare(device),
        SplitBasis.Custom => double.IsFinite(customPercent) && customPercent >= MinCustomPercent && customPercent <= MaxCustomPercent
            ? customPercent / 100 : null,
        _ => null
    };

    /// <summary>This PC's driver-reported GPU budget as a share of installed memory, or null when not reported.</summary>
    internal static double? MeasuredShare(DeviceFacts? device) =>
        device?.PrimaryGpuBudgetMiB is long mib && mib > 0 && device.InstalledGiB is double installed && installed > 0 && mib / 1024d < installed
            ? mib / 1024d / installed : null;

    /// <summary>The published configuration matching this PC's installed memory, or null.</summary>
    internal static int? ThisPcConfig(DeviceFacts? device) =>
        device?.InstalledGiB is double installed && Configurations.FirstOrDefault(c => Math.Abs(c.MemoryGB - installed) < 0.5) is { } match
            ? match.MemoryGB : null;

    internal static UltraPlan? Plan(IEnumerable<string> selectedIds, bool together, bool longDocuments, SplitBasis basis,
        double customPercent, DeviceFacts? device)
    {
        var ids = selectedIds.ToHashSet(StringComparer.Ordinal);
        var selected = SalesSizing.Workloads.Where(w => ids.Contains(w.Id)).ToArray();
        if (selected.Length == 0) return null;
        double? share = Share(basis, device, customPercent);
        var models = Models(selected);
        var configs = Evaluate(models, together, longDocuments, basis, share, device);
        var recommended = configs.FirstOrDefault(c => c.Fit == UltraFit.Roomy);
        var smallest = configs.FirstOrDefault(c => c.Fit is UltraFit.Roomy or UltraFit.LittleRoom);
        string headline = share is null
            ? basis == SplitBasis.MeasuredThisPc ? "Waiting for this PC's memory report" : $"Enter a GPU share from {MinCustomPercent:0}% to {MaxCustomPercent:0}%"
            : recommended is not null ? $"Recommended: {recommended.Config.MemoryGB} GB Surface Laptop Ultra"
            : smallest is not null ? $"Only the {smallest.Config.MemoryGB} GB configuration holds this, with little room to spare"
            : together && models.Count > 1 ? "No configuration holds all of these open together" : "No configuration holds this model";
        string talk = share is null ? "" : TalkTrack(selected, models, configs, recommended, smallest, together, longDocuments, basis, share, device);
        return new UltraPlan(selected, models, together, longDocuments, basis, share ?? double.NaN, configs, recommended, smallest, headline, talk);
    }

    internal static UltraConfigFit[] Evaluate(IReadOnlyList<UltraModel> models, bool together, bool longDocuments, SplitBasis basis,
        double? share, DeviceFacts? device)
    {
        int? here = ThisPcConfig(device);
        double context = longDocuments ? LongDocumentTokens : Sizing.DefaultContextTokens;
        IReadOnlyList<UltraModel> resident = together ? models : [models.MaxBy(m => m.Size)!];
        return Configurations.Select(c =>
        {
            if (share is not double s) return new UltraConfigFit(c, double.NaN, double.NaN, false, UltraFit.Unknown, double.NaN, null);
            bool measured = basis == SplitBasis.MeasuredThisPc && here == c.MemoryGB && device?.PrimaryGpuBudgetMiB is > 0;
            double gpu = measured ? device!.PrimaryGpuBudgetMiB!.Value / 1024d : c.MemoryGB * s;
            var inputs = resident.Select(m => SalesSizing.InputFor(m.Size, gpu) with { ContextTokens = context }).ToArray();
            var result = Sizing.EstimateTogether(inputs);
            if (!result.IsValid)
            {
                bool noRoom = gpu <= Sizing.DefaultReserveGiB;
                return new UltraConfigFit(c, gpu, c.MemoryGB - gpu, measured, noRoom ? UltraFit.WontFit : UltraFit.Unknown, double.NaN, result);
            }
            double percent = result.RequiredGiB / result.UsableGiB * 100;
            var fit = result.Verdict == SizingVerdict.DoesNotFit ? UltraFit.WontFit
                : result.Verdict == SizingVerdict.Fits && percent <= RecommendMaxPercent ? UltraFit.Roomy
                : UltraFit.LittleRoom;
            return new UltraConfigFit(c, gpu, c.MemoryGB - gpu, measured, fit, percent, result);
        }).ToArray();
    }

    internal static string FitLabel(UltraConfigFit fit, UltraConfigFit? recommended) => fit.Fit switch
    {
        UltraFit.Roomy => fit.Config.MemoryGB == recommended?.Config.MemoryGB ? "Recommended" : "Plenty of room",
        UltraFit.LittleRoom => "Little room",
        UltraFit.WontFit => "Won't fit",
        _ => "Not known yet"
    };

    internal static string BasisNote(SplitBasis basis, DeviceFacts? device, double customPercent) => basis switch
    {
        SplitBasis.MicrosoftLaunch =>
            $"GPU share: Microsoft's launch Task Manager screenshot of a 128 GB Surface Laptop Ultra shows {LaunchDedicatedGiB:0.0} GB of dedicated GPU memory " +
            $"({LaunchShare * 100:0}% of installed memory). Microsoft has not published the split for other configurations, so each one assumes the same {LaunchShare * 100:0}%. " +
            "Confirm on the shipping unit.",
        SplitBasis.MeasuredThisPc => MeasuredShare(device) is double s
            ? $"GPU share: this PC's driver reports {device!.PrimaryGpuBudgetMiB!.Value / 1024d:0.#} GB of its {device.InstalledGiB:0} GB as dedicated GPU memory ({s * 100:0}%). " +
              $"Each configuration assumes the same share. The split is set in firmware and can differ between units: Microsoft's launch unit showed {LaunchShare * 100:0}%."
            : "GPU share: waiting for this PC's driver to report its GPU memory budget.",
        _ => Share(SplitBasis.Custom, device, customPercent) is double c
            ? $"GPU share: custom, {c * 100:0}% of installed memory as dedicated GPU memory on each configuration."
            : $"GPU share: enter a custom share from {MinCustomPercent:0}% to {MaxCustomPercent:0}%."
    };

    private static string TalkTrack(IReadOnlyList<SalesWorkload> selected, IReadOnlyList<UltraModel> models, UltraConfigFit[] configs,
        UltraConfigFit? recommended, UltraConfigFit? smallest, bool together, bool longDocuments, SplitBasis basis, double? share, DeviceFacts? device)
    {
        string names = SalesSizing.JoinNames(selected);
        var largest = Sizing.Profile(models.Max(m => m.Size));
        string sized = $"{SalesSizing.Article(largest.Name)} {largest.Name} ({largest.Size}) AI model";
        var parts = new List<string>
        {
            together && models.Count > 1 ? $"Keeping {names} ready at the same time means {models.Count} AI models open together."
            : together && selected.Count > 1 ? $"{names} can share one AI model: {sized}."
            : selected.Count > 1 ? $"{names}, run one at a time, need up to {sized}."
            : $"{names} needs {sized}."
        };
        if (longDocuments) parts.Add("This plan allows for long documents.");

        if (recommended is not null)
        {
            parts.Add($"Plan on the {recommended.Config.MemoryGB} GB Surface Laptop Ultra: that uses about {Math.Round(recommended.PercentUsed):0}% of the memory it sets aside for AI (estimate).");
            var below = configs.TakeWhile(c => c != recommended).ToArray();
            if (below.LastOrDefault(c => c.Fit == UltraFit.LittleRoom) is { } tight)
                parts.Add($"The {tight.Config.MemoryGB} GB configuration also holds it, but with little room to spare once real model files and other apps are counted.");
            else if (below.Length == 0)
                parts.Add("Even the smallest configuration has room for it.");
            else
                parts.Add("Smaller configurations would not have room for it (estimate).");

            int index = Array.IndexOf(configs, recommended);
            if (index + 1 < configs.Length)
            {
                var next = configs[index + 1].Config;
                var adds = SalesSizing.Workloads.Where(w => !selected.Contains(w)).Where(w =>
                    Evaluate(Models([.. selected, w]), together, longDocuments, basis, share, device)
                        .FirstOrDefault(c => c.Fit == UltraFit.Roomy)?.Config.MemoryGB == next.MemoryGB).ToArray();
                if (adds.Length > 0)
                    parts.Add(together
                        ? $"Step up to {next.MemoryGB} GB to also keep {SalesSizing.JoinNames(adds)} open."
                        : $"Step up to {next.MemoryGB} GB to also cover {SalesSizing.JoinNames(adds)}.");
            }
        }
        else if (smallest is not null)
        {
            parts.Add($"Only the {smallest.Config.MemoryGB} GB Surface Laptop Ultra holds that, and with little room to spare (estimate).");
            parts.Add(together && models.Count > 1 ? "Run the AI models one at a time, or pick fewer workloads." : "Pick a smaller AI model for comfortable room.");
        }
        else
        {
            parts.Add(together && models.Count > 1
                ? "No Surface Laptop Ultra configuration holds all of that at once (estimate). Run the AI models one at a time, or pick fewer workloads."
                : "No Surface Laptop Ultra configuration holds that AI model (estimate). Pick a smaller AI model.");
        }
        parts.Add("Memory estimate only; confirm on the shipping configuration before ordering.");
        return string.Join(" ", parts);
    }

    internal static IEnumerable<string> AllText() =>
        Configurations.Select(c => c.Processor)
            .Concat([Footnote,
                BasisNote(SplitBasis.MicrosoftLaunch, null, DefaultCustomPercent),
                BasisNote(SplitBasis.MeasuredThisPc, null, DefaultCustomPercent),
                BasisNote(SplitBasis.Custom, null, DefaultCustomPercent),
                BasisNote(SplitBasis.Custom, null, double.NaN)]);
}
