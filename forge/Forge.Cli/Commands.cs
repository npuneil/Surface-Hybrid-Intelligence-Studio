using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Harness;
using Forge.Core.Ir;
using Forge.Core.Layouts;
using Forge.Core.Spec;
using Forge.Engine;
using SurfaceDeviceKit;
using SurfaceDeviceKit.FoundryLocal;

namespace Forge.Cli;

/// <summary>One method per verb. Every verb that touches a model goes through <see cref="ForgeSession"/>, the same class the app uses, so what
/// this prints is what the app would do on this PC.</summary>
internal sealed class Commands(Options o, Ui ui, DeviceDetectionService devices, CancellationToken ct)
{
    private sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static string Pretty(JsonNode node) => SpecJson.Pretty(node);

    private static string Line(string head, string? detail) => detail is { Length: > 0 } ? $"{head} - {detail}" : head;

    private ForgeSession NewSession() => new(new SessionOptions
    {
        Devices = devices,
        DataDirectory = o.DataDir,
        AppDataDirectory = o.AppDataDir,
        OutputRoot = o.Out,
        Log = o.Verbose ? line => ui.Say("  " + line) : null,
    });

    // ---- No model needed ----

    public int Probe()
    {
        var report = devices.Effective;
        Console.WriteLine(o.Json ? DeviceJson.Serialize(report) : DeviceReportText.Render(report, devices.Catalog));
        return 0;
    }

    public int ListDevices()
    {
        var all = devices.SimulatedDevices;
        if (o.Json)
        {
            Console.WriteLine(Pretty(new JsonArray([.. all.Select(d => (JsonNode?)new JsonObject { ["id"] = d.Id, ["group"] = d.Group, ["label"] = d.Label, ["note"] = d.Note })])));
            return 0;
        }
        foreach (var group in all.GroupBy(d => d.Group))
        {
            Console.WriteLine(group.Key);
            foreach (var d in group) Console.WriteLine($"  {d.Id,-26} {d.Label}");
        }
        return 0;
    }

    public int Briefs()
    {
        var all = BundledBriefs.All;
        if (o.Json) Console.WriteLine(Pretty(new JsonArray([.. all.Select(b => (JsonNode?)b.ToJson())])));
        else
            foreach (var b in all)
                Console.WriteLine($"{b.Id,-22} {b.PitchType,-9} {b.Client,-20} {b.SlideMin}-{b.SlideMax} slides  {b.Facts.Count} facts  template {b.TemplateHint}");
        return 0;
    }

    public int Render(string specPath)
    {
        if (!File.Exists(specPath)) throw new ArgumentException($"{specPath} does not exist.");
        var parsed = SpecJson.Parse(File.ReadAllText(specPath));
        if (parsed.Spec is null) throw new ArgumentException($"{specPath} is not a deck spec: {parsed.Error}");
        var theme = o.Theme ?? Theme.Surface;
        var deck = DeckBuilder.Build(parsed.Spec, theme);
        string stem = Path.GetFileNameWithoutExtension(specPath);
        if (stem.EndsWith(".spec", StringComparison.OrdinalIgnoreCase)) stem = stem[..^5];
        var files = ForgeOutput.Write(o.Out ?? ForgeOutput.DefaultRoot, new Brief { Id = stem, Client = stem }, deck, parsed.Spec, null, !o.NoPreview, ui.Say);
        if (o.Json)
        {
            Console.WriteLine(Pretty(new JsonObject { ["pptx"] = files.Pptx, ["folder"] = files.Folder, ["slides"] = deck.Slides.Count, ["theme"] = theme.Key, ["contactSheet"] = files.ContactSheet }));
            return 0;
        }
        Console.WriteLine($"Deck       {files.Pptx}");
        Console.WriteLine($"           {deck.Slides.Count} slides in the {theme.Key} theme, {new FileInfo(files.Pptx).Length / 1024:N0} KB");
        if (files.ContactSheet is not null) Console.WriteLine($"Previews   {files.PreviewFolder}  (and {Path.GetFileName(files.ContactSheet)})");
        return 0;
    }

    public int SelfTest()
    {
        var results = ForgeSelfTest.Run(devices);
        if (!o.Json) foreach (var r in results) Console.WriteLine(Line($"{(r.Passed ? "PASS" : "FAIL")}  {r.Name}", r.Detail));
        return Summary(results);
    }

    private int Summary(List<SelfTestResult> results)
    {
        int failed = results.Count(r => !r.Passed);
        if (o.Json)
            Console.WriteLine(Pretty(new JsonObject
            {
                ["passed"] = results.Count - failed,
                ["failed"] = failed,
                ["checks"] = new JsonArray([.. results.Select(r => (JsonNode?)new JsonObject { ["name"] = r.Name, ["passed"] = r.Passed, ["detail"] = r.Detail })]),
            }));
        else Console.WriteLine($"\n{results.Count - failed} of {results.Count} checks passed{(failed > 0 ? $", {failed} failed" : "")}.");
        return failed == 0 ? 0 : 1;
    }

    // ---- Needs Foundry Local ----

    public async Task<int> PlanAsync()
    {
        await using var session = NewSession();
        await session.StartAsync(o.AllowDownload, ui.Say, ct);
        var plan = await session.PlanAsync(ct);
        var pc = session.Hardware;
        var kit = plan.Device.Plans[ForgeSession.Workload];
        var request = o.Request;

        if (o.Json)
        {
            var options = new JsonArray();
            foreach (var option in plan.Models.Options)
            {
                var speed = session.Speed.Entry(option.VariantId);
                var json = option.Describe();
                json["settings"] = Show.SettingsJson(RunPolicy.Decide(ModelFacts.From(option), speed, request));
                json["speed"] = Show.SpeedJson(speed);
                if (session.Speed.Estimate(option.VariantId, Show.TypicalSlides) is { } eta)
                    json["eta"] = new JsonObject { ["slides"] = Show.TypicalSlides, ["seconds"] = Math.Round(eta.Seconds), ["basis"] = eta.Basis };
                options.Add(json);
            }
            Console.WriteLine(Pretty(new JsonObject
            {
                ["thisPc"] = pc.Headline,
                ["simulated"] = devices.IsSimulated ? plan.Device.Profile.Label : null,
                ["kitPlan"] = kit.Summary,
                ["default"] = plan.Default?.VariantId,
                ["options"] = options,
                ["notes"] = new JsonArray([.. plan.Models.Notes.Select(n => (JsonNode?)JsonValue.Create(n))]),
            }));
            return 0;
        }

        Console.WriteLine($"{pc.Label} · {pc.Headline} · {pc.PcClassLabel} · {pc.DeviceClassLabel}");
        if (devices.IsSimulated) Console.WriteLine($"Simulated: {plan.Device.Profile.Label} (display and planning only)");
        Console.WriteLine($"Kit plan for {(devices.IsSimulated ? plan.Device.Profile.Label : "this PC")}: {kit.Summary}");
        Console.WriteLine($"Models this PC can run for the Forge ({plan.Models.Title}), from the live Foundry Local catalog:");
        Console.WriteLine(Show.Options(session, plan, request, Show.TypicalSlides));
        if (plan.Default is { } first)
        {
            Console.WriteLine($"\nWhy these settings for {first.Title}:");
            foreach (string why in RunPolicy.Decide(ModelFacts.From(first), session.Speed.Entry(first.VariantId), request).Why) Console.WriteLine("  - " + why);
        }
        return 0;
    }

    public async Task<int> BriefAsync(string idOrPath)
    {
        var brief = ResolveBrief(idOrPath);
        await using var session = NewSession();
        var model = await LoadModelAsync(session);

        var settings = RunPolicy.Decide(model.Facts, model.Speed, o.Request);
        ui.Say("Settings: " + Show.Settings(settings));
        foreach (string why in settings.Why) ui.Say("  - " + why);
        int slides = (brief.SlideMin + brief.SlideMax) / 2;
        if (session.Speed.Estimate(model.Variant, slides) is { } eta)
            ui.Say($"Expected: about {Provenance.Duration(eta.Seconds)} for {slides} slides ({eta.Basis}).");

        var run = await session.RunAsync(model, brief, new ForgeRunOptions(o.Request, o.Theme?.Key, !o.NoPreview, o.Out), ProgressLine(), ct);
        Console.WriteLine(o.Json ? Pretty(Show.RunJson(run, model)) : Show.Run(run, model));
        return 0;
    }

    public async Task<int> DraftAsync(string text)
    {
        await using var session = NewSession();
        var model = await LoadModelAsync(session);
        var draft = await session.DraftBriefAsync(model, text, new DraftOptions(o.Client, o.Pitch, o.Theme?.Key, o.Provenance), ct);
        var brief = draft.Brief;
        string json = Pretty(brief.ToJson());
        if (o.BriefOut is not null) File.WriteAllText(o.BriefOut, json);
        if (o.Json || o.BriefOut is null) Console.WriteLine(json);
        else Console.WriteLine($"Brief for {brief.Client} ({brief.PitchType}, {brief.SlideMin}-{brief.SlideMax} slides, {brief.Facts.Count} facts) saved to {o.BriefOut}");
        foreach (string dropped in brief.Dropped) ui.Say("Dropped because the request did not support it: " + dropped);
        return 0;
    }

    public async Task<int> LiveTestAsync(string? briefId)
    {
        await using var session = NewSession();
        var pc = session.Hardware;
        ui.Say($"{pc.Label} · {pc.Headline} · {pc.PcClassLabel} · {pc.DeviceClassLabel}");
        var results = await ForgeLiveTest.RunAsync(session,
            new LiveTestOptions(briefId ?? "zava_technical", o.Model, o.Lane, o.AllowDownload, !o.NoCancel, o.Request, o.Out), ui.Say, ct);
        return Summary(results);
    }

    // ---- Shared steps ----

    private static Brief ResolveBrief(string idOrPath)
    {
        if (File.Exists(idOrPath))
        {
            try { return Brief.Load(idOrPath); }
            catch (Exception e) when (e is JsonException or FormatException) { throw new ArgumentException($"{idOrPath} is not a brief: {e.Message}"); }
        }
        return BundledBriefs.Find(idOrPath)
            ?? throw new ArgumentException($"'{idOrPath}' is neither a brief file nor a bundled brief ({string.Join(", ", BundledBriefs.All.Select(b => b.Id))}).");
    }

    /// <summary>Loads the model the person asked for, the one an emulated device would use, or the best one this PC has; then measures it if due.</summary>
    private async Task<PreparedModel> LoadModelAsync(ForgeSession session)
    {
        var prepare = new PrepareOptions(o.AllowDownload, o.Calibrate, o.RetestGuidance);
        await session.StartAsync(o.AllowDownload, ui.Say, ct);
        PreparedModel model;
        if (o.Model is not null)
        {
            string lane = o.Lane ?? session.Hardware.UsableLanes.FirstOrDefault()?.Id ?? "cpu";
            var (resolved, why) = await session.Engine.ResolveModelAsync(o.Model, lane, ct);
            if (resolved is null) throw new ArgumentException(why);
            model = await session.PrepareAsync(resolved, prepare, ui.Say, ct);
        }
        else if (o.Emulate is not null)
        {
            var sim = SimulatedProfiles.Find(o.Emulate) ?? throw new ArgumentException($"No simulated device '{o.Emulate}'. Try --list.");
            var report = DeviceDetectionService.Analyze(sim.Raw, devices.Catalog, "simulated", sim.Id, sim.Label);
            var (resolved, why) = await session.ResolveEmulationAsync(report, ct);
            if (resolved is null) throw new InvalidOperationException(why);
            ui.Say($"Emulating {sim.Label}: {resolved.Summary}.{(why.Length > 0 ? " " + why : "")} The deck still states this PC.");
            model = await session.PrepareAsync(resolved, prepare, ui.Say, ct);
        }
        else model = await session.PrepareBestAsync(prepare, ui.Say, ct);

        var loaded = model.Model;
        ui.Say($"{model.Facts.Title} is ready on the {loaded.Device} ({loaded.Ep}): load {loaded.LoadSeconds:0.0} s, warm-up {loaded.WarmSeconds:0.0} s, window {(loaded.ContextTokens is > 0 ? $"{loaded.ContextTokens:N0} tokens" : "not reported")}.");
        if (model.FellBack) foreach (string problem in model.Problems) ui.Say("  fell back: " + problem);
        if (model.Calibration is { } calibration) ui.Say("  " + calibration.Summary);
        return model;
    }

    private IProgress<HarnessProgress> ProgressLine()
    {
        var last = ("", -1);
        return new Inline<HarnessProgress>(p =>
        {
            if (p.Phase.Length == 0 || (p.Phase, p.Step) == last) return;
            last = (p.Phase, p.Step);
            ui.Say($"{p.Phase} {p.Step}/{p.Total}: {p.Message}");
        });
    }
}
