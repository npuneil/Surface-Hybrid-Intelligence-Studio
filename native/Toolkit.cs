using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace HybridIntelligenceStudio;

/// <summary>An action a companion tool offers. Target is a file, folder or URL handed to the Windows shell.</summary>
internal sealed record ToolAction(string Id, string Label, string? Target, string? CopyText = null, bool Primary = false);

/// <summary>A sibling local-AI project. The studio only detects and opens it; it never runs measurements on its behalf.</summary>
internal sealed record CompanionTool(string Id, string Title, string Repository, string Summary, string Story, string Boundary,
    string EnvKey, string Folder, string Marker, string Glyph);

internal sealed record ToolState(CompanionTool Tool, string? Root, string Status, bool Found, IReadOnlyList<ToolAction> Actions, string? Detail);

internal static class Toolkit
{
    internal const string HydraUrl = "http://127.0.0.1:8787";

    internal static readonly CompanionTool[] Tools =
    [
        new("forge", "Offline PPTX Forge", "github.com/npuneil/offline-pptx-forge",
            "Turns a brief into an editable PowerPoint deck with a local Qwen3-Coder-30B model through llama.cpp.",
            "Local AI producing a real Office file, not just chat text.",
            "Separate app and runtime. Review every slide before sharing.",
            "FORGE_ROOT", "Offline-PPTX-Forge", "forge_cli.py", "\uE8A5"),
        new("tokenomics", "Local AI Tokenomics", "github.com/npuneil/Local-AI-Tokenomics",
            "A WinUI dashboard that models where a fleet's AI work could run (cloud, subscription or on-device) under editable assumptions.",
            "Turns the hybrid conversation into a planning model a customer can change.",
            "Its figures are illustrative assumptions you enter, not measurements from this studio or promised savings.",
            "TOKENOMICS_ROOT", "Local-AI-Tokenomics", Path.Combine("src", "SurfaceTokenomics.App", "SurfaceTokenomics.App.csproj"), "\uE9D2"),
        new("headroom", "Local AI Headroom", "github.com/npuneil/Local-AI-Headroom",
            "Measures how much local AI this PC carries at once: several models resident on the GPU, answering side by side.",
            "Shows a PC running a whole kit of models together, not one model in isolation.",
            "The studio opens existing reports only. Run new measurements from Headroom with the studio's model unloaded.",
            "HEADROOM_ROOT", "Local-AI-Headroom", "Headroom.cmd", "\uE9F9"),
        new("bench", "Local AI Bench", "github.com/npuneil/Local-AI-Bench",
            "A local-only benchmark for one developer running many concurrent streams across resident models, with raw traces.",
            "Inspectable, repeatable numbers for builders who want more than one speed test.",
            "The studio opens existing results only. Benchmarks run from the Bench project itself.",
            "BENCH_ROOT", "Local-AI-Bench", "bench.py", "\uE9E9"),
        new("hydrafusion", "HydraFusion autorouting", "github.com/npuneil/hydrafusion-autorouting-demo",
            "A Copilot-style chat that scores each prompt and routes it on-device or to the cloud, with real Foundry Local inference.",
            "The hybrid router idea as a full chat experience.",
            "It loads its own model, so it shares the GPU with the studio. Start it only when the studio is idle.",
            "HYDRAFUSION_ROOT", "hydrafusion-autorouting-demo", "start-demo.ps1", "\uE8AB"),
    ];

    internal static string? ResolveRoot(CompanionTool tool, string studioRoot)
    {
        string? configured = Environment.GetEnvironmentVariable(Brand.EnvPrefix + tool.EnvKey)?.Trim().Trim('"');
        string root;
        try
        {
            root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Path.GetDirectoryName(studioRoot) ?? studioRoot, tool.Folder)
                : configured);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return File.Exists(Path.Combine(root, tool.Marker)) ? root : null;
    }

    internal static ToolState Inspect(CompanionTool tool, string studioRoot)
    {
        string? root = ResolveRoot(tool, studioRoot);
        if (root is null)
            return new ToolState(tool, null, "Not found", false, [new ToolAction("repo", "View on GitHub", "https://" + tool.Repository)],
                $"Clone it beside the studio, or set {Brand.EnvPrefix}{tool.EnvKey} to its folder.");
        var actions = new List<ToolAction>();
        string status = "Found";
        string? detail = null;
        switch (tool.Id)
        {
            case "forge":
                try
                {
                    actions.Add(new ToolAction("open", "Open Forge", PptxForge.FindExecutable(root), Primary: true));
                }
                catch (FileNotFoundException)
                {
                    status = "Not built";
                    detail = "Build app\\ForgeApp in Release mode using Forge's README.";
                }
                break;
            case "tokenomics":
                if (TokenomicsExe(root) is string exe) actions.Add(new ToolAction("open", "Open Tokenomics", exe, Primary: true));
                else
                {
                    status = "Not built";
                    detail = "Build src\\SurfaceTokenomics.App in Release mode using its README.";
                }
                break;
            case "headroom":
                string? report = Newest(root, "results", "report.html") ?? Newest(root, "reference", "report.html");
                if (report is not null)
                {
                    actions.Add(new ToolAction("report", "Latest report", report, Primary: true));
                    detail = "Latest report: " + Path.GetFileName(Path.GetDirectoryName(report));
                }
                else
                {
                    status = "No reports yet";
                    detail = "Run Headroom.cmd from its folder to produce a report.";
                }
                break;
            case "bench":
                string index = Path.Combine(root, "results", "index.html");
                if (File.Exists(index)) actions.Add(new ToolAction("report", "Open results", index, Primary: true));
                else
                {
                    status = "No results yet";
                    detail = "Run a benchmark from the Bench folder to produce results\\index.html.";
                }
                break;
            case "hydrafusion":
                string cmd = Path.Combine(root, "start-demo.cmd");
                bool listening = IsListening(8787);
                if (listening)
                {
                    status = "Running";
                    actions.Add(new ToolAction("browse", "Open in browser", HydraUrl, Primary: true));
                }
                else if (File.Exists(cmd)) actions.Add(new ToolAction("start", "Start demo", cmd, Primary: true));
                actions.Add(new ToolAction("copy", "Copy command", null,
                    $"pwsh -NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(root, "start-demo.ps1")}\""));
                detail = listening ? "Listening on " + HydraUrl + "." : "Starts a local web app on " + HydraUrl + ".";
                break;
        }
        actions.Add(new ToolAction("folder", "Open folder", root));
        return new ToolState(tool, root, status, true, actions, detail);
    }

    internal static string? TokenomicsExe(string root)
    {
        string bin = Path.Combine(root, "src", "SurfaceTokenomics.App", "bin");
        var builds = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? new[] { ("ARM64", "win-arm64"), ("x64", "win-x64") }
            : new[] { ("x64", "win-x64"), ("ARM64", "win-arm64") };
        foreach (var (platform, rid) in builds)
        {
            string path = Path.Combine(bin, platform, "Release", "net9.0-windows10.0.26100.0", rid, "SurfaceTokenomics.App.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string? Newest(string root, string folder, string file)
    {
        string directory = Path.Combine(root, folder);
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateDirectories(directory)
            .Select(d => Path.Combine(d, file))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    internal static bool IsListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromMilliseconds(250)) && client.Connected;
        }
        catch (Exception exception) when (exception is SocketException or AggregateException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Hands a file, folder or URL to the Windows shell through explorer.exe, so anything it starts is not part of the
    /// studio's kill-on-close job. Only paths the toolkit resolved itself, or its fixed loopback URL, are accepted.
    /// </summary>
    internal static void Open(string target)
    {
        bool url = target == HydraUrl || target.StartsWith("https://github.com/npuneil/", StringComparison.Ordinal);
        if (!url && !File.Exists(target) && !Directory.Exists(target))
            throw new FileNotFoundException("That item is no longer on disk. Refresh the companion tools and try again.", target);
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        using var handoff = Process.Start(new ProcessStartInfo(explorer, $"\"{target}\"") { UseShellExecute = false })
            ?? throw new IOException("Windows did not open " + target + ".");
    }
}
