using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SurfaceDeviceKit;
using SurfaceDeviceKit.FoundryLocal;

// surface-engine: ask Foundry Local what this PC can really run for the kit's plans, and run it.
const string Usage = """
    surface-engine VERB [options]

      --eps                  Start Foundry Local; list its execution providers and which are registered; show the NVIDIA GPU libraries
      --plan [WORKLOAD]      Resolve the kit's plan against this PC's live catalog (default: every workload)
      --catalog [ALIAS]      List the live catalog's builds, optionally for one alias
      --generate WORKLOAD    Load the best model for the workload and answer --prompt
      --live-test            End-to-end check on this PC: start, plan, load, answer, cancel, fall back, restart

    Options
      --prompt TEXT          Prompt for --generate (default asks for a JSON object)
      --system TEXT          System message
      --max-tokens N         Reply limit (default 200)
      --alias ALIAS --lane L Force one model on one lane (npu, gpu or cpu) with --generate
      --guidance TYPE --guidance-file F   Constrain the reply (json_schema, regex or lark_grammar) with the grammar in file F
      --allow-download       Fetch missing execution providers and models (needs internet; off by default)
      --json                 Machine-readable output
    """;

var args2 = args.ToList();
bool Flag(string name) => args2.Remove(name);
string? Value(string name)
{
    int i = args2.IndexOf(name);
    if (i < 0) return null;
    if (i + 1 >= args2.Count) throw new ArgumentException($"{name} needs a value.");
    string v = args2[i + 1];
    args2.RemoveRange(i, 2);
    return v;
}
// A verb whose argument is optional: take the next token only if it is not another option.
string? OptionalValue(string name, out bool present)
{
    int i = args2.IndexOf(name);
    present = i >= 0;
    if (i < 0) return null;
    string? v = i + 1 < args2.Count && !args2[i + 1].StartsWith("--") ? args2[i + 1] : null;
    args2.RemoveRange(i, v is null ? 1 : 2);
    return v;
}

try
{
    if (Flag("--help") || Flag("-h") || Flag("-?") || args.Length == 0) { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
    bool json = Flag("--json");
    bool allowDownload = Flag("--allow-download");
    bool eps = Flag("--eps");
    bool liveTest = Flag("--live-test");
    string? plan = OptionalValue("--plan", out bool doPlan);
    string? catalogAlias = OptionalValue("--catalog", out bool doCatalog);
    string? generate = Value("--generate");
    string? prompt = Value("--prompt");
    string? system = Value("--system");
    int maxTokens = int.TryParse(Value("--max-tokens"), out int mt) ? mt : 200;
    string? forceAlias = Value("--alias");
    string? forceLane = Value("--lane");
    string? guidanceType = Value("--guidance");
    string? guidanceFile = Value("--guidance-file");
    if (args2.Count > 0) throw new ArgumentException($"Unknown option '{args2[0]}'.\n\n{Usage}");

    var stopwatch = Stopwatch.StartNew();
    void Say(string line) { if (!json) Console.WriteLine($"[{stopwatch.Elapsed.TotalSeconds,6:0.0}s] {line}"); }
    var service = new DeviceDetectionService();
    await using var engine = new FoundryEngine(service, new EngineOptions { AppName = "SurfaceDeviceKit" });
    var hardware = service.Detected.Profile;
    Say($"{hardware.Label} · {hardware.Headline} · {hardware.PcClassLabel} · {hardware.DeviceClassLabel}");

    if (liveTest) return await LiveTest.RunAsync(engine, service, allowDownload, Say);

    await engine.InitializeAsync(allowDownload, Say);

    if (eps)
    {
        Console.WriteLine(json ? engine.Describe().ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : Show.Providers(engine));
        return 0;
    }

    if (doCatalog)
    {
        var rows = (await engine.ListCatalogAsync()).Where(r => catalogAlias is null || r.Alias.Equals(catalogAlias, StringComparison.OrdinalIgnoreCase)).ToList();
        if (json) Console.WriteLine(new JsonArray(rows.Select(r => (JsonNode)r.Describe()).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        else foreach (var r in rows.OrderBy(r => r.Alias).ThenBy(r => r.Device))
                Console.WriteLine($"{r.Alias,-28} {r.Device,-4} {r.EpLabel,-18} {r.FileGb,6:0.0} GB  {(r.Cached ? "on this PC" : ""),-10} ctx {(r.ContextTokens?.ToString() ?? "?")}  {r.VariantId}");
        return 0;
    }

    if (doPlan)
    {
        var ids = plan is null ? service.Catalog.Workloads.Keys.ToList() : [plan];
        var plans = new List<ResolvedPlan>();
        foreach (string id in ids) plans.Add(await engine.ResolveAsync(id));
        if (json) Console.WriteLine(new JsonArray(plans.Select(p => (JsonNode)p.Describe()).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        else foreach (var p in plans) Console.WriteLine(Show.Plan(p, service.Detected.Plans[p.Workload]));
        return 0;
    }

    if (generate is not null)
    {
        LoadedModel model;
        var progress = (string s) => Say(s);
        if (forceAlias is not null)
        {
            var (resolved, why) = await engine.ResolveModelAsync(forceAlias, forceLane ?? hardware.UsableLanes.First().Id);
            if (resolved is null) throw new ArgumentException(why);
            model = await engine.LoadAsync(resolved, allowDownload, progress);
        }
        else model = (await engine.LoadBestAsync(generate, allowDownload, progress)).Model;
        Say($"Loaded {model.Label} on the {model.Device} ({model.Ep}): load {model.LoadSeconds:0.0} s, warm-up {model.WarmSeconds:0.0} s");
        var result = await engine.GenerateAsync(model, system ?? "You answer with a single JSON object and nothing else.",
            prompt ?? "Give the capital and the country of Paris as JSON with keys city and country.",
            new GenOptions
            {
                MaxTokens = maxTokens, Temperature = 0, GuidanceType = guidanceType,
                GuidanceData = guidanceFile is null ? null : await File.ReadAllTextAsync(guidanceFile),
            }, json ? null : piece => Console.Write(piece));
        if (json) Console.WriteLine(new JsonObject { ["text"] = result.Text, ["metrics"] = result.Describe(), ["model"] = model.Describe() }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        else Console.WriteLine($"\n\n{result.Summary}");
        return 0;
    }

    Console.WriteLine(Usage);
    return 2;
}
catch (Exception e) when (e is ArgumentException or KeyNotFoundException or IOException or JsonException or DownloadRequiredException or ModelLoadException)
{
    Console.Error.WriteLine(e.Message);
    return e is ArgumentException or KeyNotFoundException ? 2 : 3;
}

static class Show
{
    internal static string Gb(double gb) => gb >= 10 ? $"{gb:0.0} GB" : $"{gb:0.00} GB";

    internal static string Providers(FoundryEngine engine)
    {
        var lines = new List<string> { $"App data: {engine.AppDataDirectory}", $"Model cache: {engine.ModelCacheDirectory}", "Execution providers:" };
        foreach (var p in engine.Providers)
            lines.Add($"  {p.Name,-34} {(p.Registered ? "registered" : "not registered"),-15} {(p.Usable ? "usable" : "not usable"),-11} {(p.Cached ? "installed on this PC" : "")}");
        if (engine.Cuda is { } cuda) lines.Add($"NVIDIA GPU libraries: {cuda.State} - {cuda.Detail}");
        foreach (string note in engine.EpNotes) lines.Add("  note: " + note);
        return string.Join("\n", lines);
    }

    internal static string Plan(ResolvedPlan live, WorkloadPlan kit)
    {
        var lines = new List<string> { $"{live.Title}  (kit plan: {kit.Summary})" };
        if (live.Options.Count == 0) lines.Add("  nothing can run here");
        foreach (var o in live.Options)
            lines.Add($"  {(o == live.Primary ? ">" : " ")} {o.LaneLabel,-3} {o.Title,-30} {o.EpLabel,-17} {Gb(o.FileGb),8}  needs {o.RequiredGb,5:0.0} GB  {(o.Cached ? "on this PC" : "download")}");
        foreach (string note in live.Notes) lines.Add("  note: " + note);
        return string.Join("\n", lines);
    }
}
