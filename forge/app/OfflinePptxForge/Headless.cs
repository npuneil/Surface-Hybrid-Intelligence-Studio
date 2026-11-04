using System.Text;
using System.Text.Json.Nodes;
using Forge.Engine;
using SurfaceDeviceKit;

namespace OfflinePptxForge;

/// <summary>The checks that need no window: what this PC is, and whether the Forge's own parts work on it. A person on a new Surface can run
/// these from a terminal before opening the app (OfflinePptxForge.exe --probe, --self-test).</summary>
internal static class Headless
{
    private const string Usage = """
        OfflinePptxForge.exe [options]

        With no options the app opens. These need no window and write to the console:
          --probe [--simulate ID] [--json]   Detect this PC and show what it can do: lanes, capabilities and the model plan
          --list [--json]                    The sample Surfaces --simulate accepts, with the package and deck model each one takes
          --self-test [--json]               Check fonts, briefs, the renderer, the .pptx writer and this PC's plan (no model)
          --help

        Window options:
          --simulate ID         Open with another Surface's capabilities shown (display and planning only; decks are still made on this PC)
          --section deck|pc     Start on the deck page or on the capability page
          --brief FILE          Open with this brief JSON selected (for example one the Hybrid Intelligence Studio exported)
          --window-size WxH     Open at a fixed size, at least 600x560 (and do not maximise)
          --out DIR             Where decks are saved (default: Documents\Offline PPTX Forge)
          --data DIR            Where the speed log lives (default: %LOCALAPPDATA%\OfflinePptxForge)
          --app-data DIR        Where Foundry Local keeps its data
          --allow-download      Let the app fetch missing execution providers and models
          --smoke               Check the window in-process (no model), then exit; --report FILE saves the results, --capture DIR saves screenshots
          --smoke-live          The same, then write a real deck with the model on this PC and cancel one

        For everything else (briefs, drafting, rendering a saved spec) use forge-cli, which drives the same engine.
        Exit codes: 0 done, 1 a check failed, 2 bad usage, 3 could not run.
        """;

    internal static int Run(AppOptions options)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (options.Help)
            {
                Console.WriteLine(Usage);
                return 0;
            }
            if (options.List) return List(options);
            var devices = new DeviceDetectionService();
            if (options.Simulate is not null)
            {
                if (!options.Probe) throw new ArgumentException("--simulate changes the capability view only, so it works with --probe or in the window.");
                if (SimulatedProfiles.Find(options.Simulate) is null) throw new ArgumentException($"No simulated device '{options.Simulate}'.");
                devices.Simulate(options.Simulate);
            }
            if (options.Probe)
            {
                var report = devices.Effective;
                var real = devices.Detected.Profile;
                var package = new JsonObject
                {
                    ["name"] = ForgePackage.Name,
                    ["silicon"] = ForgePackage.Silicon,
                    ["recommended"] = SiliconPackage.For(report.Profile),
                    ["note"] = devices.IsSimulated ? null : ForgePackage.Mismatch(real)?.Message,
                };
                if (options.Json)
                {
                    var node = JsonNode.Parse(DeviceJson.Serialize(report))!.AsObject();
                    node["package"] = package;
                    Console.WriteLine(node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    Console.WriteLine(DeviceReportText.Render(report, devices.Catalog));
                    Console.WriteLine($"Package: {ForgePackage.Line(report.Profile, devices.IsSimulated)}");
                    if (package["note"] is { } note) Console.WriteLine($"  {note}");
                }
                return 0;
            }
            return SelfTest(options, devices);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            Program.LogError(options, e);
            return 3;
        }
    }

    /// <summary>Each sample Surface goes through the same classifier and planner as a real probe, from its own hardware only (no facts from this PC),
    /// so the list says what that Surface itself should show.</summary>
    private static int List(AppOptions options)
    {
        var catalog = Catalog.Default;
        var rows = SimulatedProfiles.All.Select(device =>
        {
            var report = DeviceDetectionService.Analyze(device.Raw, catalog, "simulated", device.Id, device.Label);
            var plan = report.Plans.TryGetValue(ForgeSession.Workload, out var p) ? p : null;
            return (device, report.Profile, plan);
        }).ToList();
        if (options.Json)
        {
            Console.WriteLine(new JsonArray([.. rows.Select(r => (JsonNode?)new JsonObject
            {
                ["id"] = r.device.Id,
                ["label"] = r.device.Label,
                ["package"] = SiliconPackage.For(r.Profile),
                ["pcClass"] = r.Profile.PcClassLabel,
                ["deviceClass"] = r.Profile.DeviceClassLabel,
                ["lane"] = r.plan?.PrimaryLane,
                ["model"] = r.plan?.Primary?.Alias,
                ["modelTitle"] = r.plan?.Primary?.Title,
            })]).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        foreach (var (device, profile, plan) in rows)
        {
            string deck = plan?.Primary is { } m ? $"{m.Title} on the {plan.PrimaryLane?.ToUpperInvariant()}" : "no deck model";
            Console.WriteLine($"{device.Id,-31} {SiliconPackage.For(profile),-17} {deck,-32} {device.Label}");
        }
        return 0;
    }

    private static int SelfTest(AppOptions options, DeviceDetectionService devices)
    {
        var results = ForgeSelfTest.Run(devices);
        int failed = results.Count(r => !r.Passed);
        if (options.Json)
        {
            Console.WriteLine(new JsonObject
            {
                ["passed"] = results.Count - failed,
                ["failed"] = failed,
                ["checks"] = new JsonArray([.. results.Select(r => (JsonNode?)new JsonObject { ["name"] = r.Name, ["passed"] = r.Passed, ["detail"] = r.Detail })]),
            }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            foreach (var r in results) Console.WriteLine($"{(r.Passed ? "PASS" : "FAIL")}  {r.Name}{(r.Detail.Length > 0 ? " - " + r.Detail : "")}");
            Console.WriteLine($"\n{results.Count - failed} of {results.Count} checks passed{(failed > 0 ? $", {failed} failed" : "")}.");
        }
        return failed == 0 ? 0 : 1;
    }
}
