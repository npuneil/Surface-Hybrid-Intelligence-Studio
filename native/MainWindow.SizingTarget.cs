using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

/// <summary>How the two sizing pages follow the PC shown: this PC, or a Surface previewed from the catalog.</summary>
internal sealed partial class MainWindow
{
    // What the person chose themselves. While these are unset the pages follow the device: the first memory view it has, and the reserve that suits it.
    private bool _budgetSourceChosen;
    private bool _reserveEdited;
    private bool _pickingBudgetSource;

    /// <summary>The PC the sizing pages budget memory for right now.</summary>
    internal SizingTarget SizingNow => SizingTarget.For(_status?.Device, _status?.Target, _deviceReport);

    /// <summary>
    /// The Surface Laptop Ultra planner measures a split of one unified memory pool, so only a PC whose GPU draws on such a pool can offer its own
    /// measured split. Every other PC, and any previewed Surface, plans from the launch split or a custom share.
    /// </summary>
    private DeviceFacts? UltraDevice =>
        !SizingNow.Preview && _deviceReport is { Simulated: false } report && report.Lane("gpu") is { Available: true, Kind: "gpu-unified" }
            ? _status?.Device : null;

    /// <summary>Opens only the memory views this PC has, and selects the first of them unless the person picked one that is still open.</summary>
    private void ChooseBudgetSource(SizingTarget target)
    {
        bool known = target.Facts is not null;
        int first = -1;
        for (int i = 0; i < _budgetSource.Items.Count; i++)
        {
            if (_budgetSource.Items[i] is not RadioButton { Tag: BudgetSource source } item) continue;
            // Until the PC's facts are in, no view is ruled out; the budget box says it is waiting.
            item.IsEnabled = !known || source == BudgetSource.Custom || Sizing.ResolveBudget(source, target.Facts, double.NaN) is not null;
            if (item.IsEnabled && first < 0) first = i;
        }
        if (_budgetSourceChosen && _budgetSource.SelectedItem is RadioButton { IsEnabled: true }) return;
        _budgetSourceChosen = false;
        if (first < 0 || _budgetSource.SelectedIndex == first) return;
        _pickingBudgetSource = true;
        try { _budgetSource.SelectedIndex = first; }
        finally { _pickingBudgetSource = false; }
    }

    private void OnBudgetSourceChanged()
    {
        if (!_pickingBudgetSource) _budgetSourceChosen = true;
        SyncBudgetBox();
        RecomputeSizing();
    }

    private static string BudgetNote(BudgetSource source, SizingTarget target)
    {
        var device = target.Facts;
        string installed = device?.InstalledGiB is double i ? $"{Math.Round(i):N0} GB installed physical memory" : "Installed memory";
        string note = source switch
        {
            BudgetSource.GpuDriverBudget when device is null => target.Preview
                ? "The previewed Surface's memory profile is still loading."
                : "GPU budget not reported yet. Choose another view or enter a custom budget.",
            BudgetSource.GpuDriverBudget => device.PrimaryGpuBudgetMiB is long mib
                ? target.Preview
                    ? $"GPU memory budget in the previewed Surface's profile: {mib:N0} MiB = {mib / 1024d:F2} GiB (estimate)."
                    : $"Driver-reported GPU budget: {mib:N0} MiB = {mib / 1024d:F2} GiB. Best view for GPU inference here." +
                      (target.DedicatedGpu ? " This is the GPU's own memory." : " Not dedicated VRAM.")
                : $"There is no separate GPU memory budget on {target.Noun}: its GPU shares system memory. Use the Windows-visible view.",
            BudgetSource.WindowsVisibleMemory => device?.OsVisibleGiB is double w
                ? $"Windows-visible memory: {w:F2} GiB" + (target.Preview ? " (estimate from the previewed Surface's profile)" : "") + ". " +
                  (device.PrimaryGpuBudgetMiB is null
                      ? $"Models on {target.Noun} run from this one shared pool."
                      : "A different view of the same machine; never added to the GPU budget.")
                : "Windows-visible memory not reported yet.",
            _ => "Enter any budget, for example a banker, branch or operations device's memory. Useful for Snapdragon, Intel, AMD and NVIDIA sizing conversations."
        };
        if (source == BudgetSource.GpuDriverBudget && target.DedicatedGpu && device?.PrimaryGpuBudgetMiB is not null)
            note += " The reserve starts at 1 GiB because Windows and apps use system memory, not the GPU's own.";
        return note + $" {installed} is shown for context and is not used as a budget.";
    }

    /// <summary>The PC shown changed (a preview started or ended, or its report arrived): follow it on both pages.</summary>
    internal void RefreshSizingForDevice()
    {
        if (_salesView is null) return;
        SyncBudgetBox();
        RecomputeSizing();
        RecomputeSales();
    }

    /// <summary>The live-demo badge for one workload, from what the demo catalog says about the PC shown.</summary>
    private (string Text, Brush Fill, Brush Ink) SalesLiveState(SalesWorkload workload, bool preview)
    {
        if (!workload.HasLiveDemo || workload.DemoId is not string id) return ("GUIDANCE ONLY", Sand, Muted);
        if (_demoCatalog.Find(id)?.Fit is not { } fit) return ("LIVE DEMO - CHECKING", Sand, Muted);
        return (preview, fit.State) switch
        {
            (false, "ready") => ("LIVE DEMO HERE", GreenTint, GreenInk),
            (false, "limited") => ("LIVE DEMO HERE - LIMITED", AmberTint, AmberInk),
            (false, _) => ("NOT ON THIS PC", Sand, Muted),
            (true, "ready") => ("WOULD RUN - PREVIEW", GreenTint, GreenInk),
            (true, "limited") => ("PARTLY - PREVIEW", AmberTint, AmberInk),
            _ => ("NOT ON THE PREVIEWED SURFACE", Sand, Muted),
        };
    }
}
