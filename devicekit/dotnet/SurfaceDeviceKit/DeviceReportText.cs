using System.Text;

namespace SurfaceDeviceKit;

/// <summary>A plain-text view of a <see cref="DeviceReport"/> for consoles and log panes.</summary>
public static class DeviceReportText
{
    public static string Render(DeviceReport report, Catalog? catalog = null, bool capabilities = true, bool plans = true)
    {
        catalog ??= Catalog.Default;
        var p = report.Profile;
        var sb = new StringBuilder();
        string simulated = p.Source == "simulated" ? "  [SIMULATED: display and planning only]" : "";
        sb.AppendLine($"{p.Headline}{simulated}");
        sb.AppendLine($"  Class      {p.PcClassLabel} · {p.DeviceClassLabel}");
        sb.AppendLine($"  OS         {p.Os.Version} · {p.Os.Arch}{(p.Os.Emulated ? $" (this {p.Os.ProcessArch} build is emulated)" : $" (native {p.Os.ProcessArch} build)")}");
        sb.AppendLine($"  CPU        {p.Cpu.Name} · {p.Cpu.LogicalCores} logical cores");
        foreach (var g in p.Gpus)
            sb.AppendLine($"  GPU        {g.Name} · {g.Kind}{(g.VramGb >= 0.5 ? $" · {g.VramGb:0.0} GB" : "")}");
        if (p.Gpus.Count == 0) sb.AppendLine("  GPU        none reported");
        foreach (var n in p.Npus)
            sb.AppendLine($"  NPU        {n.Name} · {(n.RatedTops is { } t ? $"rated {t:0} TOPS (vendor figure)" : "no rating")}{(n.LlmCapable ? "" : " · not used for language models")}");
        if (p.Npus.Count == 0) sb.AppendLine("  NPU        none reported");
        sb.AppendLine($"  Memory     {p.Memory.InstalledGb:0.#} GB installed · {p.Memory.VisibleGb:0.#} GB visible to Windows");
        sb.AppendLine($"  Storage    {p.Storage.FreeGb:0} GB free of {p.Storage.TotalGb:0} GB on {p.Storage.Drive}");

        sb.AppendLine();
        sb.AppendLine("Lanes, preferred first");
        foreach (var l in p.Lanes)
        {
            string state = l.Available ? $"budget {l.BudgetGb,5:0.0} GB · up to {l.ClassCap}-class · EP {string.Join(", ", l.Eps)}" : "not available";
            sb.AppendLine($"  {catalog.Lanes[l.Id].Label,-4} {state}");
            sb.AppendLine($"       {l.Device} — {l.Note}");
        }

        if (capabilities)
        {
            sb.AppendLine();
            sb.AppendLine("Capabilities");
            foreach (var c in p.Capabilities)
                sb.AppendLine($"  {Mark(c.Status)} {c.Title,-32} {c.Detail}  [{c.Basis}]");
        }

        if (plans)
        {
            sb.AppendLine();
            sb.AppendLine("Plans");
            foreach (var (id, plan) in report.Plans)
                sb.AppendLine($"  {id,-16} {plan.Summary}");
        }
        return sb.ToString();
    }

    private static string Mark(string status) => status switch
    {
        CapabilityMatrix.Available => "[ok]     ",
        CapabilityMatrix.Limited => "[limited]",
        _ => "[no]     ",
    };
}
