namespace HybridIntelligenceStudio;

/// <summary>A broad, 100-level workload a presenter can pick without knowing model or memory details.</summary>
internal sealed record SalesWorkload(
    string Id,
    string Name,
    string Glyph,
    string Plain,
    TShirtSize Size,
    string ModelClass,
    string? DemoId,
    bool HasLiveDemo,
    string LiveNote);

internal enum SalesFit { Unknown, Comfortable, Tight, WontFit }

internal sealed record SalesDevice(string Id, string Name, string Detail);

internal sealed record SalesDeviceFit(SalesDevice Device, SalesFit Fit, double PercentUsed, SizingInput? Input, SizingResult? Result);

/// <summary>The memory the leader view budgets for one PC, where that number comes from, and how much Windows and apps keep for themselves.</summary>
internal sealed record SalesBudget(double? Gib, string Detail, double ReserveGiB);

internal sealed record SalesRecommendation(
    IReadOnlyList<SalesWorkload> Workloads,
    SizeProfile Profile,
    SalesDeviceFit ThisPc,
    IReadOnlyList<SalesDeviceFit> Comparisons,
    bool PilotCandidate,
    string TalkTrack);

/// <summary>
/// Presenter-facing sizing. Maps everyday workloads to a T-shirt size, then runs the exact same
/// <see cref="Sizing.Estimate"/> arithmetic as the technical view with its default settings.
/// </summary>
internal static class SalesSizing
{
    internal static readonly SalesWorkload[] Workloads =
    [
        new("transcription", "Transcription & meeting notes", "\uE720",
            "Turn meetings, calls and dictation into text and action items.", TShirtSize.S,
            "Whisper-class speech model plus a small text model", "meeting", true,
            "Live demo: a synthetic meeting recording is transcribed by a speech model, then turned into minutes. Microphone capture is not part of this app."),
        new("field", "Field & frontline notes", "\uE707",
            "Clean up quick notes from technicians and frontline staff.", TShirtSize.S,
            "Small 3-4B text model class", "voice-memo", true,
            "Live demo: a synthetic voice memo becomes a structured work order."),
        new("translation", "Translation", "\uF2B7",
            "Translate emails, documents and chat between languages.", TShirtSize.M,
            "7-8B multilingual model class", "translate", true,
            "Live demo: an online-banking maintenance notice is translated into your choice of language. A fluent reviewer still checks the meaning."),
        new("writing", "Writing & summaries", "\uE70F",
            "Draft emails, briefs and summaries from notes.", TShirtSize.M,
            "7-8B general model class", "brief", true,
            "Live demo: an executive brief from three synthetic sources. Redaction and inbox triage demos sit alongside it."),
        new("documents", "Ask your documents", "\uE8A5",
            "Answer questions from approved company documents.", TShirtSize.M,
            "7-8B model class with document search", "ask-docs", true,
            "Live demo: a search model finds the right synthetic policies, then the chat model answers with citations."),
        new("vision", "Photos & scanned forms", "\uE722",
            "Describe photos and sort scanned forms for a person to check.", TShirtSize.M,
            "About 7B vision-language model class", "vision", true,
            "Live demo: a vision model reads a synthetic scanned form or receipt into structured data."),
        new("coding", "Local coding assistant", "\uE943",
            "Draft, explain and review code on the laptop.", TShirtSize.L,
            "Qwen2.5-Coder 14B class", "code", true,
            "Live demo: a code model drafts code on the PC shown; the studio picks the code model that fits that PC's memory and processors."),
        new("reasoning", "Analysis & reasoning", "\uE82F",
            "Work through multi-step questions, plans and analysis.", TShirtSize.L,
            "GPT-OSS 20B class", "reasoning", true,
            "Live demo: a reasoning model works through a scheduling puzzle, and the app checks its answer."),
        new("prototyping", "Bigger-model prototyping", "\uE950",
            "Try larger open models for prototypes and evaluation.", TShirtSize.XL,
            "24-32B model class", "faceoff", false,
            "Memory estimate only on this page. The related face-off demo compares two chat models side by side."),
        new("frontier", "Largest open models (70B class)", "\uE81E",
            "Evaluate the biggest open models that still fit on a laptop's GPU.", TShirtSize.XXL,
            "Llama 3.3 70B class", null, false,
            "Memory estimate only on this page. Models this large need a high-memory configuration with a GPU that has memory to match."),
    ];

    internal static readonly string[] DefaultSelection = ["coding"];

    internal const string HereId = "this";

    private const string GpuDetail = "GPU memory budget reported by the driver";
    private const string DedicatedGpuDetail = "Dedicated graphics memory reported by the driver";
    private const string GpuPreviewDetail = "GPU memory budget from the previewed Surface's profile";
    private const string SystemDetail = "Memory Windows reports for this PC";
    private const string SystemPreviewDetail = "Memory Windows would see on the previewed Surface";
    private const string PendingDetail = "Memory not reported yet";

    /// <summary>The PC shown: this PC, or the Surface being previewed.</summary>
    internal static SalesDevice Here(bool preview, string detail) => new(HereId, preview ? "The previewed Surface" : "This PC", detail);

    /// <summary>
    /// The memory a model can use on the PC shown: the GPU's own budget when it reports one, otherwise the memory Windows can see, because a PC
    /// without separate graphics memory (Snapdragon, Intel, AMD) runs its models out of that one shared pool. Installed memory is never a budget.
    /// </summary>
    internal static SalesBudget BudgetFor(DeviceFacts? device, bool preview, string? gpuKind = null)
    {
        var target = new SizingTarget(device, preview, null, gpuKind);
        if (Sizing.ResolveBudget(BudgetSource.GpuDriverBudget, device, double.NaN) is double gpu)
            return new SalesBudget(gpu, preview ? GpuPreviewDetail : target.DedicatedGpu ? DedicatedGpuDetail : GpuDetail,
                target.ReserveFor(BudgetSource.GpuDriverBudget));
        if (Sizing.ResolveBudget(BudgetSource.WindowsVisibleMemory, device, double.NaN) is double windows)
            return new SalesBudget(windows, preview ? SystemPreviewDetail : SystemDetail, Sizing.DefaultReserveGiB);
        return new SalesBudget(null, PendingDetail, Sizing.DefaultReserveGiB);
    }

    /// <summary>Illustrative memory sizes for comparison; they describe no specific device or accelerator.</summary>
    internal static readonly (SalesDevice Device, double BudgetGiB)[] Comparisons =
    [
        (new("laptop32", "A 32 GB laptop", "Illustrative 32 GB memory size"), 32),
        (new("laptop16", "A 16 GB laptop", "Illustrative 16 GB memory size"), 16),
    ];

    private static (SalesDevice Device, double BudgetGiB) Smallest => Comparisons.Single(c => c.Device.Id == "laptop16");

    internal const string Footnote =
        "Same arithmetic as the Technical view, using its default settings and one model at a time; the largest workload sets the size. " +
        "Memory fit only: it does not confirm that a model or runtime is supported on a device, or how quickly it responds. " +
        "The 16 GB and 32 GB laptops are illustrative memory sizes with the same reserve for Windows and apps. " +
        "A PC whose GPU has memory of its own keeps a smaller reserve, because Windows and apps live in system memory.";

    internal static SalesWorkload? Find(string id) => Workloads.FirstOrDefault(w => w.Id == id);

    /// <summary>Technical-view defaults for one size against one budget view.</summary>
    internal static SizingInput InputFor(TShirtSize size, double budgetGiB, double reserveGiB = Sizing.DefaultReserveGiB)
    {
        var p = Sizing.Profile(size);
        return new SizingInput(p.DefaultBillions, Sizing.DefaultBits, Sizing.DefaultContextTokens, p.KvGiBPer1kTokens,
            p.RuntimeOverheadGiB, budgetGiB, reserveGiB, Sizing.DefaultHeadroomGiB);
    }

    internal static SalesDeviceFit Evaluate(SalesDevice device, TShirtSize size, double? budgetGiB, double reserveGiB = Sizing.DefaultReserveGiB)
    {
        if (budgetGiB is not double budget || !double.IsFinite(budget) || budget <= 0)
            return new SalesDeviceFit(device, SalesFit.Unknown, double.NaN, null, null);
        var input = InputFor(size, budget, reserveGiB);
        var result = Sizing.Estimate(input);
        var fit = result.Verdict switch
        {
            SizingVerdict.Fits => SalesFit.Comfortable,
            SizingVerdict.Tight => SalesFit.Tight,
            SizingVerdict.DoesNotFit => SalesFit.WontFit,
            _ => SalesFit.Unknown
        };
        double percent = result.IsValid && result.UsableGiB > 0 ? result.RequiredGiB / result.UsableGiB * 100 : double.NaN;
        return new SalesDeviceFit(device, fit, percent, input, result);
    }

    /// <summary>Returns null when no known workload is selected. The largest selected size wins (one model at a time).</summary>
    internal static SalesRecommendation? Recommend(IEnumerable<string> selectedIds, DeviceFacts? device, bool preview = false, string? gpuKind = null)
    {
        var selected = Workloads.Where(w => selectedIds.Contains(w.Id)).ToArray();
        if (selected.Length == 0) return null;
        var size = selected.Max(w => w.Size);
        var profile = Sizing.Profile(size);
        var budget = BudgetFor(device, preview, gpuKind);
        var here = Evaluate(Here(preview, budget.Detail), size, budget.Gib, budget.ReserveGiB);
        var others = Comparisons.Select(c => Evaluate(c.Device, size, c.BudgetGiB)).ToArray();
        bool pilot = size is TShirtSize.S or TShirtSize.M && others.Single(o => o.Device.Id == Smallest.Device.Id).Fit is SalesFit.Comfortable or SalesFit.Tight;
        return new SalesRecommendation(selected, profile, here, others, pilot, TalkTrack(selected, profile, here, others, pilot, preview));
    }

    internal static string FitLabel(SalesFit fit) => fit switch
    {
        SalesFit.Comfortable => "Comfortable",
        SalesFit.Tight => "Tight fit",
        SalesFit.WontFit => "Won't fit",
        _ => "Not known yet"
    };

    /// <summary>
    /// What one workload means for the platforms a seller meets, from the same arithmetic as the cards: a 16 GB AI PC and, when the memory report
    /// is in, the PC shown.
    /// </summary>
    internal static string PlatformFit(SalesWorkload w, SalesDeviceFit? here = null, bool preview = false)
    {
        bool fits16 = Evaluate(Smallest.Device, w.Size, Smallest.BudgetGiB).Fit is SalesFit.Comfortable or SalesFit.Tight;
        bool hereFits = here?.Fit is SalesFit.Comfortable or SalesFit.Tight;
        string subject = preview ? "The previewed Surface" : "This PC";
        return fits16
            ? "Good pilot candidate for a 16 GB AI PC (estimate)." + (hereFits ? $" {subject} has room too (estimate)." : "")
            : "A 16 GB AI PC would not have room for this size (estimate); plan on a higher-memory Surface." + (hereFits ? $" {subject} has room for it (estimate)." : "");
    }

    internal static string JoinNames(IReadOnlyList<SalesWorkload> workloads) => workloads.Count switch
    {
        0 => "",
        1 => workloads[0].Name,
        2 => $"{workloads[0].Name} and {workloads[1].Name}",
        _ => string.Join(", ", workloads.Take(workloads.Count - 1).Select(w => w.Name)) + " and " + workloads[^1].Name
    };

    private static string TalkTrack(IReadOnlyList<SalesWorkload> workloads, SizeProfile profile, SalesDeviceFit here,
        IReadOnlyList<SalesDeviceFit> others, bool pilot, bool preview)
    {
        string subject = preview ? "The previewed Surface" : "This PC";
        string noun = preview ? "the previewed Surface" : "this PC";
        string verb = workloads.Count == 1 ? "needs" : "need";
        string first = $"{JoinNames(workloads)} {verb} {Article(profile.Name)} {profile.Name} ({profile.Size}) AI model.";
        string second = here.Fit switch
        {
            SalesFit.Comfortable => $"{subject} has room for that size, using about {Math.Round(here.PercentUsed):0}% of the memory available for AI (estimate).",
            SalesFit.Tight => $"{subject} can hold that size, but with little room to spare (about {Math.Round(here.PercentUsed):0}% of the memory available for AI, estimate).",
            SalesFit.WontFit => $"That size is more than {noun}'s memory budget allows in this estimate; pick a smaller workload or model.",
            _ => preview ? "The previewed Surface's memory profile is still loading." : "Connect to the local service to check this PC's memory."
        };
        var by = others.ToDictionary(o => o.Device.Id);
        bool fits16 = by.TryGetValue("laptop16", out var l16) && l16.Fit is SalesFit.Comfortable or SalesFit.Tight;
        bool fits32 = by.TryGetValue("laptop32", out var l32) && l32.Fit is SalesFit.Comfortable or SalesFit.Tight;
        bool hereFits = here.Fit is SalesFit.Comfortable or SalesFit.Tight;
        string third;
        if (!fits16 && !fits32 && hereFits)
            third = $"Neither a 16 GB nor a 32 GB laptop would have room for it (estimate). That is where {noun} stands out.";
        else if (!fits16 && hereFits)
            third = $"A 16 GB laptop would not have room for it (estimate). That is where {noun} stands out.";
        else if (fits16 && pilot)
            third = "It also fits a 16 GB laptop (estimate), so a pilot on a 16 GB AI PC is worth exploring; verify the model, runtime and accelerator on that device first. Position a higher-memory Surface when the firm also wants bigger models.";
        else
            third = "Check the technical view before committing to a device.";
        return $"{first} {second} {third}";
    }

    internal static string Article(string word) => word.Length > 0 && "AEIOUaeiou".Contains(word[0]) ? "an" : "a";

    /// <summary>
    /// Larger llama.cpp models run only on a PC whose service lists them as available, so a workload names one only when this PC really offers it.
    /// A previewed Surface runs nothing, so it never does.
    /// </summary>
    internal static string LargeModelNote(string workloadId, IEnumerable<ModelChoice>? models, bool preview)
    {
        string? alias = workloadId switch { "prototyping" => "qwen3-coder-30b", "frontier" => "llama3.3-70b", _ => null };
        if (preview || alias is null) return "";
        if (models?.FirstOrDefault(m => m.Alias == alias && m.IsLlamaCpp && m.Available) is not { } row) return "";
        string need = row.NeedGib is double gib ? $"about {gib:0.0} GiB of free GPU memory" : "";
        string needs = row.Exclusive ? "it needs the GPU to itself" + (need.Length > 0 ? " and " + need : "") : need.Length > 0 ? "it needs " + need : "";
        return $" {row.Label} also runs in the Live model selector on this PC" + (needs.Length > 0 ? "; " + needs : "") + ".";
    }

    internal static IEnumerable<string> AllText()
    {
        var comfortable = new SalesDeviceFit(Here(false, ""), SalesFit.Comfortable, 50, null, null);
        return Workloads.SelectMany(w => new[] { w.Name, w.Plain, w.ModelClass, w.LiveNote, PlatformFit(w), PlatformFit(w, comfortable), PlatformFit(w, comfortable, true) })
            .Concat(Comparisons.Select(c => c.Device.Name + " " + c.Device.Detail))
            .Concat([Here(false, "").Name, Here(true, "").Name, GpuDetail, DedicatedGpuDetail, GpuPreviewDetail, SystemDetail, SystemPreviewDetail, PendingDetail, Footnote]);
    }
}

