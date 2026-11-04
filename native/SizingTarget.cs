namespace HybridIntelligenceStudio;

/// <summary>
/// The PC the sizing pages budget memory for: this PC, or a Surface being previewed. The facts have the same shape either way, so every
/// budget view works unchanged; a previewed Surface's facts come from the device kit's profile and are estimates.
/// </summary>
internal sealed record SizingTarget(DeviceFacts? Facts, bool Preview, string? Label, string? GpuKind)
{
    /// <summary>A GPU with memory of its own loses only a little of it to the display and driver; Windows and apps live in system memory.</summary>
    internal const double DedicatedGpuReserveGiB = 1;

    internal bool DedicatedGpu => GpuKind == "discrete";

    /// <summary>"this PC" or "the previewed Surface", for the middle of a sentence.</summary>
    internal string Noun => Preview ? "the previewed Surface" : "this PC";

    /// <summary>The reserve a budget view starts with: every shared memory pool gives up the full default, a discrete GPU's own memory far less.</summary>
    internal double ReserveFor(BudgetSource source) =>
        source == BudgetSource.GpuDriverBudget && DedicatedGpu ? DedicatedGpuReserveGiB : Sizing.DefaultReserveGiB;

    /// <summary>
    /// This PC's own facts, or the previewed Surface's profile while one is shown. Facts are null while a preview's report is still loading,
    /// so the pages wait instead of budgeting this PC's memory for a different Surface.
    /// </summary>
    internal static SizingTarget For(DeviceFacts? thisPc, TargetInfo? target, DeviceReport? report)
    {
        if (target is not { Error: null, Simulated: true })
            return new SizingTarget(thisPc, false, null, report is { Simulated: false } real ? real.GpuKind : null);
        var shown = report is { Simulated: true } preview && preview.Label == target.Label ? preview : null;
        return new SizingTarget(shown?.ToFacts(), true, target.Label, shown?.GpuKind);
    }
}
