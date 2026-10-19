using System.Diagnostics;
using System.Text.Json;

namespace HybridIntelligenceStudio;

internal static class PptxForge
{
    internal static string ResolveRoot(string studioRoot)
    {
        string? configured = Environment.GetEnvironmentVariable(Brand.EnvPrefix + "FORGE_ROOT")?.Trim().Trim('"');
        string root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetDirectoryName(studioRoot) ?? studioRoot, "Offline-PPTX-Forge")
            : configured);
        if (!File.Exists(Path.Combine(root, "forge_cli.py")))
            throw new FileNotFoundException("Offline PPTX Forge was not found. Install it beside the studio, or set "
                + Brand.EnvPrefix + "FORGE_ROOT to its repository folder. You can still export a deck brief here.");
        return root;
    }

    internal static string FindExecutable(string root)
    {
        foreach (string configuration in new[] { Path.Combine("ARM64", "Release"), "Release" })
        {
            string path = Path.Combine(root, "app", "ForgeApp", "bin", configuration,
                "net10.0-windows10.0.19041.0", "win-arm64", "OfflinePptxForge.exe");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("The Offline PPTX Forge desktop app has not been built. Build app\\ForgeApp in Release mode using Forge's README, or export a deck brief and use its CLI.");
    }

    internal const string ProcessName = "OfflinePptxForge";

    internal static bool IsRunning()
    {
        var running = Process.GetProcessesByName(ProcessName);
        foreach (var p in running) p.Dispose();
        return running.Length > 0;
    }

    /// <summary>
    /// Starts Forge through the Windows shell so it is not a member of this app's kill-on-close job:
    /// closing the studio must not close a separate app the user may be building a deck in.
    /// Forge finds its repository root from its own executable path.
    /// </summary>
    internal static void Launch(string exe)
    {
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        using var handoff = Process.Start(new ProcessStartInfo(explorer, $"\"{exe}\"") { UseShellExecute = false })
            ?? throw new IOException("Windows did not start Offline PPTX Forge.");
    }

    internal static Dictionary<string, object> CreateBrief(Scenario scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario.Industry) || string.IsNullOrWhiteSpace(scenario.Client)
            || string.IsNullOrWhiteSpace(scenario.DeckObjective) || string.IsNullOrWhiteSpace(scenario.Source))
            throw new ArgumentException("An industry scenario needs its industry, fictional client, objective and source notes.");
        return new Dictionary<string, object>
        {
            ["id"] = "studio_" + scenario.Key.Replace('-', '_'),
            ["client"] = scenario.Client,
            ["pitch_type"] = "sales",
            ["audience"] = scenario.Industry + " operations and IT leaders; fictional demonstration only.",
            ["objective"] = scenario.DeckObjective,
            ["context"] = "Synthetic industry workflow from " + Brand.DisplayName + ". " + scenario.Prompt
                + " This brief is source material, not a completed deck or evidence that a pilot ran.",
            ["facts"] = scenario.Source.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries),
            ["required_topics"] = new Dictionary<string, string[]>
            {
                ["workflow"] = [scenario.Industry, "draft", "review"],
                ["grounding"] = ["source", "synthetic", "unknown"],
                ["pilot"] = ["pilot", "measure", "review"],
            },
            ["guardrails"] = new[]
            {
                "Fictional demonstration only. Use only supplied facts; preserve missing and unverified information.",
                "Do not invent numbers, policy, approvals, decisions, deadlines or a pilot duration or headcount.",
                "Do not claim measured benefits, savings, ROI, performance, certification or replacement of cloud subscriptions.",
                "Human review is required. This is drafting support, not an automated operational or eligibility decision.",
                "Studio text inference and Forge deck generation are separate local runtimes; do not attribute one runtime's results to the other.",
                "Review every generated slide against these facts before sharing; deck generation is not factual verification.",
            },
            ["slide_range"] = new[] { 8, 10 },
            ["template_hint"] = "surface",
        };
    }

    internal static string SaveBrief(Scenario scenario, string directory)
    {
        var brief = CreateBrief(scenario);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, brief["id"] + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(brief, Evidence.Options));
        return path;
    }
}
