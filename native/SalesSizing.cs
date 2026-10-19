namespace HybridIntelligenceStudio;

/// <summary>A broad, 100-level workload a seller can pick without knowing model or memory details.</summary>
internal sealed record SalesWorkload(
    string Id,
    string Name,
    string Glyph,
    string Plain,
    TShirtSize Size,
    string ModelClass,
    string? DemoId,
    bool LiveOnThisLaptop,
    string LiveNote);

internal enum SalesFit { Unknown, Comfortable, Tight, WontFit }

internal sealed record SalesDevice(string Id, string Name, string Detail);

internal sealed record SalesDeviceFit(SalesDevice Device, SalesFit Fit, double PercentUsed, SizingInput? Input, SizingResult? Result);

internal sealed record SalesRecommendation(
    IReadOnlyList<SalesWorkload> Workloads,
    SizeProfile Profile,
    SalesDeviceFit ThisLaptop,
    IReadOnlyList<SalesDeviceFit> Comparisons,
    bool PilotCandidate,
    string TalkTrack);

/// <summary>
/// Seller-facing sizing. Maps everyday workloads to a T-shirt size, then runs the exact same
/// <see cref="Sizing.Estimate"/> arithmetic as the technical view with its default settings.
/// </summary>
internal static class SalesSizing
{
    internal static readonly SalesWorkload[] Workloads =
    [
        new("transcription", "Transcription & meeting notes", "\uE720",
            "Turn meetings, calls and dictation into text and action items.", TShirtSize.S,
            "Whisper-class speech model plus a small text model", "meeting", true,
            "Live demo here: a synthetic meeting recording is transcribed by a speech model, then turned into minutes, on the RTX GPU. Microphone capture is not part of this app."),
        new("field", "Field & frontline notes", "\uE707",
            "Clean up quick notes from technicians and frontline staff.", TShirtSize.S,
            "Small 3-4B text model class", "voice-memo", true,
            "Live demo here: a synthetic voice memo becomes a structured work order on the RTX GPU."),
        new("translation", "Translation", "\uF2B7",
            "Translate emails, documents and chat between languages.", TShirtSize.M,
            "7-8B multilingual model class", "translate", true,
            "Live demo here: a customer notice is translated into your choice of language on the RTX GPU. A fluent reviewer still checks the meaning."),
        new("writing", "Writing & summaries", "\uE70F",
            "Draft emails, briefs and summaries from notes.", TShirtSize.M,
            "7-8B general model class", "brief", true,
            "Live demo here: an executive brief from three synthetic sources. Redaction and inbox triage demos sit alongside it."),
        new("documents", "Ask your documents", "\uE8A5",
            "Answer questions from approved company documents.", TShirtSize.M,
            "7-8B model class with document search", "ask-docs", true,
            "Live demo here: a search model finds the right synthetic policies, then the chat model answers with citations."),
        new("vision", "Photos & scanned forms", "\uE722",
            "Describe photos and sort scanned forms for a person to check.", TShirtSize.M,
            "About 7B vision-language model class", "vision", true,
            "Live demo here: a vision model reads a synthetic scanned form or receipt into structured data on the RTX GPU."),
        new("coding", "Local coding assistant", "\uE943",
            "Draft, explain and review code on the laptop.", TShirtSize.L,
            "Qwen2.5-Coder 14B class", "code", true,
            "Live demo here: Qwen2.5-Coder 14B drafts code on the RTX GPU."),
        new("reasoning", "Analysis & reasoning", "\uE82F",
            "Work through multi-step questions, plans and analysis.", TShirtSize.L,
            "GPT-OSS 20B class", "reasoning", true,
            "Live demo here: GPT-OSS 20B works through a scheduling puzzle, and the app checks its answer."),
        new("prototyping", "Bigger-model prototyping", "\uE950",
            "Try larger open models for prototypes and evaluation.", TShirtSize.XL,
            "24-32B model class", "faceoff", false,
            "Memory estimate only on this page. When its weights are installed, Qwen3-Coder 30B-A3B also runs in the Live model selector. The related face-off demo runs two large models side by side here."),
        new("frontier", "Largest open models (70B class)", "\uE81E",
            "Evaluate the biggest open models that still fit on a laptop's GPU.", TShirtSize.XXL,
            "Llama 3.3 70B class", null, false,
            "Memory estimate only on this page. When its weights are installed, Llama 3.3 70B also runs in the Live model selector; it needs the GPU to itself and about 43.6 GiB of free GPU memory on this laptop."),
    ];

    internal static readonly string[] DefaultSelection = ["coding"];

    internal static readonly SalesDevice ThisLaptop = new("this", "This Surface Laptop Ultra", "RTX GPU memory budget reported by the driver");

    /// <summary>Illustrative memory sizes for comparison; they describe no specific device or accelerator.</summary>
    internal static readonly (SalesDevice Device, double BudgetGiB)[] Comparisons =
    [
        (new("laptop32", "A 32 GB laptop", "Illustrative 32 GB memory size"), 32),
        (new("laptop16", "A 16 GB laptop", "Illustrative 16 GB memory size"), 16),
    ];

    internal const string Footnote =
        "Same arithmetic as the Technical view, using its default settings and one model at a time; the largest workload sets the size. " +
        "Memory fit only: it does not confirm that a model or runtime is supported on a device, or how quickly it responds. " +
        "The 16 GB and 32 GB laptops are illustrative memory sizes with the same reserve for Windows and apps.";

    internal static SalesWorkload? Find(string id) => Workloads.FirstOrDefault(w => w.Id == id);

    /// <summary>Technical-view defaults for one size against one budget view.</summary>
    internal static SizingInput InputFor(TShirtSize size, double budgetGiB)
    {
        var p = Sizing.Profile(size);
        return new SizingInput(p.DefaultBillions, Sizing.DefaultBits, Sizing.DefaultContextTokens, p.KvGiBPer1kTokens,
            p.RuntimeOverheadGiB, budgetGiB, Sizing.DefaultReserveGiB, Sizing.DefaultHeadroomGiB);
    }

    internal static SalesDeviceFit Evaluate(SalesDevice device, TShirtSize size, double? budgetGiB)
    {
        if (budgetGiB is not double budget || !double.IsFinite(budget) || budget <= 0)
            return new SalesDeviceFit(device, SalesFit.Unknown, double.NaN, null, null);
        var input = InputFor(size, budget);
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
    internal static SalesRecommendation? Recommend(IEnumerable<string> selectedIds, DeviceFacts? device)
    {
        var selected = Workloads.Where(w => selectedIds.Contains(w.Id)).ToArray();
        if (selected.Length == 0) return null;
        var size = selected.Max(w => w.Size);
        var profile = Sizing.Profile(size);
        var thisLaptop = Evaluate(ThisLaptop, size, Sizing.ResolveBudget(BudgetSource.GpuDriverBudget, device, double.NaN));
        var others = Comparisons.Select(c => Evaluate(c.Device, size, c.BudgetGiB)).ToArray();
        bool pilot = size is TShirtSize.S or TShirtSize.M;
        return new SalesRecommendation(selected, profile, thisLaptop, others, pilot, TalkTrack(selected, profile, thisLaptop, others, pilot));
    }

    internal static string FitLabel(SalesFit fit) => fit switch
    {
        SalesFit.Comfortable => "Comfortable",
        SalesFit.Tight => "Tight fit",
        SalesFit.WontFit => "Won't fit",
        _ => "Not known yet"
    };

    internal static string PlatformFit(SalesWorkload w) => w.Size is TShirtSize.S or TShirtSize.M
        ? "Good Snapdragon or Intel AI PC pilot candidate; this laptop has room too."
        : "Plan on this Surface Laptop Ultra. Snapdragon and Intel pilots in this studio use smaller S and M models.";

    internal static string JoinNames(IReadOnlyList<SalesWorkload> workloads) => workloads.Count switch
    {
        0 => "",
        1 => workloads[0].Name,
        2 => $"{workloads[0].Name} and {workloads[1].Name}",
        _ => string.Join(", ", workloads.Take(workloads.Count - 1).Select(w => w.Name)) + " and " + workloads[^1].Name
    };

    private static string TalkTrack(IReadOnlyList<SalesWorkload> workloads, SizeProfile profile, SalesDeviceFit thisLaptop,
        IReadOnlyList<SalesDeviceFit> others, bool pilot)
    {
        string verb = workloads.Count == 1 ? "needs" : "need";
        string first = $"{JoinNames(workloads)} {verb} {Article(profile.Name)} {profile.Name} ({profile.Size}) AI model.";
        string second = thisLaptop.Fit switch
        {
            SalesFit.Comfortable => $"This Surface Laptop Ultra has room for that size on its RTX GPU, using about {Math.Round(thisLaptop.PercentUsed):0}% of the memory available for AI (estimate).",
            SalesFit.Tight => $"This Surface Laptop Ultra can hold that size, but with little room to spare (about {Math.Round(thisLaptop.PercentUsed):0}% of the memory available for AI, estimate).",
            SalesFit.WontFit => "That size is more than this laptop's memory budget allows in this estimate; pick a smaller workload or model.",
            _ => "Connect to the local service to check this laptop's memory."
        };
        var by = others.ToDictionary(o => o.Device.Id);
        bool fits16 = by.TryGetValue("laptop16", out var l16) && l16.Fit is SalesFit.Comfortable or SalesFit.Tight;
        bool fits32 = by.TryGetValue("laptop32", out var l32) && l32.Fit is SalesFit.Comfortable or SalesFit.Tight;
        bool hereFits = thisLaptop.Fit is SalesFit.Comfortable or SalesFit.Tight;
        string third;
        if (!fits16 && !fits32 && hereFits)
            third = "Neither a 16 GB nor a 32 GB laptop would have room for it (estimate). That is where the Surface Laptop Ultra stands out.";
        else if (!fits16 && hereFits)
            third = "A 16 GB laptop would not have room for it (estimate). That is where the Surface Laptop Ultra stands out.";
        else if (fits16 && pilot)
            third = "It also fits a 16 GB laptop (estimate), so a Snapdragon or Intel AI PC pilot is worth exploring; verify the model on that device first. Position the Surface Laptop Ultra when the customer also wants bigger models.";
        else
            third = "Check the technical view before committing to a device.";
        return $"{first} {second} {third}";
    }

    internal static string Article(string word) => word.Length > 0 && "AEIOUaeiou".Contains(word[0]) ? "an" : "a";

    internal static IEnumerable<string> AllText() =>
        Workloads.SelectMany(w => new[] { w.Name, w.Plain, w.ModelClass, w.LiveNote, PlatformFit(w) })
            .Concat(Comparisons.Select(c => c.Device.Name + " " + c.Device.Detail))
            .Concat([ThisLaptop.Name, ThisLaptop.Detail, Footnote]);
}
