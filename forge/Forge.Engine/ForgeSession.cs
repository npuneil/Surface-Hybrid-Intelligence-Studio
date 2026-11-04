using System.Diagnostics;
using System.Text.Json.Nodes;
using Forge.Core.Harness;
using Forge.Core.Ir;
using Forge.Core.Layouts;
using Forge.Core.Spec;
using SurfaceDeviceKit;
using SurfaceDeviceKit.FoundryLocal;

namespace Forge.Engine;

public sealed class SessionOptions
{
    /// <summary>Folder name under %LOCALAPPDATA% for this app's own data (speed log, logs, and models when no other app has them).</summary>
    public string AppName { get; init; } = "OfflinePptxForge";

    /// <summary>Other Surface demo apps on this PC. Models one of them has already downloaded are reused instead of downloaded again.</summary>
    public IReadOnlyList<string> SharedAppData { get; init; } = ["HybridIntelligenceStudio", "SurfaceSalesKit", "ASUAgenticShowcase", "SurfaceCES2027"];

    /// <summary>Forces the Foundry Local data folder; the model cache lives in its models folder.</summary>
    public string? AppDataDirectory { get; init; }

    /// <summary>Where the speed log lives. Defaults to %LOCALAPPDATA%\&lt;AppName&gt;.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Where decks are saved. Defaults to Documents\Offline PPTX Forge.</summary>
    public string? OutputRoot { get; init; }

    /// <summary>The device detection service. Defaults to the shared one, so a simulation chosen elsewhere in the app shows up here.</summary>
    public DeviceDetectionService? Devices { get; init; }

    public Action<string>? Log { get; init; }
}

/// <summary>What this PC can run for the Forge's workload, from the live Foundry Local catalog.</summary>
public sealed record SessionPlan(DeviceReport Device, ResolvedPlan Models, ResolvedModel? Default);

/// <param name="Calibrate">True measures one slide now, false never does, null measures when this model has no recent measurement.</param>
/// <param name="RetestGuidance">Ask for constrained decoding again even though an earlier run found this model refuses it.</param>
public sealed record PrepareOptions(bool AllowDownload = false, bool? Calibrate = null, bool RetestGuidance = false);

/// <summary>A model that is loaded and ready to write decks, with the story it will tell and what is known about its speed here.</summary>
public sealed class PreparedModel
{
    public required LoadedModel Model { get; init; }
    public required FoundryLlm Llm { get; init; }
    public required ModelFacts Facts { get; init; }
    public required DeviceStory Story { get; init; }
    public SpeedEntry? Speed { get; internal set; }
    public CalibrationResult? Calibration { get; init; }
    public bool FellBack { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];
    public string Variant => Model.Id;
}

public sealed record ForgeRunOptions(RunRequest? Request = null, string? Template = null, bool Previews = true, string? OutputRoot = null);

public sealed record ForgeRun(HarnessResult Result, DeckIr Deck, DeckFiles Files, RunSettings Settings, double BuildSeconds);

/// <summary>Everything the Forge does with a PC, in the order the app does it: detect, plan, load a model, measure it, write a deck.
/// The command line and the WinUI app both drive this class, so what one shows is what the other does.
/// Detection always describes the real PC. A simulated device only changes what the capability view and the plan show; a deck's story
/// is built from the hardware and model that actually ran, so a deck never claims a device it was not made on.</summary>
public sealed class ForgeSession : IAsyncDisposable
{
    /// <summary>The catalog workload the Forge's slides belong to: small JSON objects a model must keep valid.</summary>
    public const string Workload = "structured-json";
    private static readonly TimeSpan FreshMeasurement = TimeSpan.FromDays(30);

    private readonly Action<string>? _log;

    public ForgeSession(SessionOptions? options = null)
    {
        options ??= new SessionOptions();
        _log = options.Log;
        Devices = options.Devices ?? DeviceKit.Service;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DataDirectory = options.DataDirectory ?? Path.Combine(local, options.AppName);
        OutputRoot = options.OutputRoot ?? ForgeOutput.DefaultRoot;
        Engine = new FoundryEngine(Devices, new EngineOptions
        {
            AppName = options.AppName,
            SharedAppData = options.SharedAppData,
            AppDataDirectory = options.AppDataDirectory,
            LogsDirectory = Path.Combine(DataDirectory, "logs"),
            Log = Log,
        });
        Speed = SpeedLog.Load(Path.Combine(DataDirectory, "speed.json"));
        if (Speed.Problem.Length > 0) Log(Speed.Problem);
    }

    public DeviceDetectionService Devices { get; }
    public FoundryEngine Engine { get; }
    public SpeedLog Speed { get; }
    public string DataDirectory { get; }
    public string OutputRoot { get; set; }

    /// <summary>The real PC, whatever is being simulated.</summary>
    public DeviceProfile Hardware => Devices.Detected.Profile;

    private void Log(string line)
    {
        try { _log?.Invoke(line); }
        catch (Exception) { /* a logging callback must never break a run */ }
    }

    /// <summary>Starts Foundry Local and registers this PC's execution providers. Safe to call again, for example after downloads are allowed.</summary>
    public async Task StartAsync(bool allowDownload = false, Action<string>? progress = null, CancellationToken ct = default)
    {
        await Engine.InitializeAsync(allowDownload, progress, ct).ConfigureAwait(false);
        if (Engine.State == EngineState.Unavailable) throw new InvalidOperationException(Engine.Status);
    }

    /// <summary>The models this PC can run for the Forge, strongest first, with the one to start with.</summary>
    public async Task<SessionPlan> PlanAsync(CancellationToken ct = default)
    {
        var models = await Engine.ResolveAsync(Workload, ct).ConfigureAwait(false);
        return new SessionPlan(Devices.Effective, models, models.PrimaryOnThisPc ?? models.Primary);
    }

    /// <summary>The model a simulated device would use for the Forge, as near as this PC can run it: the same model on the first lane of
    /// this PC that has a build of it. The reason says when it is not the lane the simulated device would use.</summary>
    public async Task<(ResolvedModel? Model, string Why)> ResolveEmulationAsync(DeviceReport device, CancellationToken ct = default)
    {
        if (!device.Plans.TryGetValue(Workload, out var plan) || plan.Primary is null)
            return (null, $"{device.Profile.Label} has no model for this workload.");
        foreach (var lane in Hardware.UsableLanes)
        {
            var (model, _) = await Engine.ResolveModelAsync(plan.Primary.Alias, lane.Id, ct).ConfigureAwait(false);
            if (model is null) continue;
            string note = lane.Id == plan.PrimaryLane ? "" : $"{device.Profile.Label} would run {model.Title} on its {plan.PrimaryLane?.ToUpperInvariant()}; this PC runs it on the {model.LaneLabel}.";
            return (model, note);
        }
        return (null, $"{plan.Primary.Title} has no build that runs on this PC.");
    }

    // ---- Loading ----

    public async Task<PreparedModel> PrepareAsync(ResolvedModel choice, PrepareOptions? options = null, Action<string>? progress = null, CancellationToken ct = default)
    {
        options ??= new PrepareOptions();
        await StartAsync(options.AllowDownload, progress, ct).ConfigureAwait(false);
        progress?.Invoke($"Loading {choice.Title} on the {choice.LaneLabel}...");
        var loaded = await Engine.LoadAsync(choice, options.AllowDownload, progress, ct).ConfigureAwait(false);
        return await AttachAsync(loaded, new FoundryChatBackend(Engine, loaded), options, progress, false, [], ct).ConfigureAwait(false);
    }

    /// <summary>Loads the strongest model that starts on this PC, falling back down the plan when one fails.</summary>
    public async Task<PreparedModel> PrepareBestAsync(PrepareOptions? options = null, Action<string>? progress = null, CancellationToken ct = default)
    {
        options ??= new PrepareOptions();
        await StartAsync(options.AllowDownload, progress, ct).ConfigureAwait(false);
        var outcome = await Engine.LoadBestAsync(Workload, options.AllowDownload, progress, 3, ct).ConfigureAwait(false);
        return await AttachAsync(outcome.Model, new FoundryChatBackend(Engine, outcome.Model), options, progress, outcome.FellBack, outcome.Problems, ct).ConfigureAwait(false);
    }

    /// <summary>Everything after a model is loaded: wraps it for the harness, measures it with one real slide when that is due, and
    /// builds the story from this PC's real hardware. The chat backend is a parameter so tests can stand in for Foundry Local.</summary>
    public async Task<PreparedModel> AttachAsync(
        LoadedModel loaded, IChatBackend backend, PrepareOptions? options = null, Action<string>? progress = null,
        bool fellBack = false, IReadOnlyList<string>? problems = null, CancellationToken ct = default)
    {
        options ??= new PrepareOptions();
        var facts = ModelFacts.From(loaded.Choice, loaded.ContextTokens);
        string variant = loaded.Id;
        var known = Speed.Entry(variant);
        bool refused = known?.Guidance == GuidanceState.Rejected && !options.RetestGuidance;
        var llm = new FoundryLlm(FoundryLlm.InfoFor(loaded.Choice, loaded.ContextTokens), backend, Log, guidanceKnownRejected: refused);

        CalibrationResult? calibration = null;
        bool stale = known is null || DateTimeOffset.UtcNow - known.MeasuredAt > FreshMeasurement;
        if ((options.Calibrate ?? stale) || options.RetestGuidance)
        {
            progress?.Invoke($"Measuring {facts.Title} on this PC with one real slide...");
            var story = StoryBuilder.Build(Hardware, facts);
            calibration = await Calibrator.RunAsync(llm, story, ct).ConfigureAwait(false);
            Log(calibration.Summary);
            if (calibration.Completed)
            {
                Speed.Record(calibration.ToEntry(facts, variant));
                Speed.Save();
                if (Speed.Problem.Length > 0) Log(Speed.Problem);
            }
        }

        var speed = Speed.Entry(variant);
        var measured = new StoryMeasurements(speed?.DecodeTokensPerSecond ?? 0, speed?.Guidance == GuidanceState.Verified);
        return new PreparedModel
        {
            Model = loaded,
            Llm = llm,
            Facts = facts,
            Story = StoryBuilder.Build(Hardware, facts, measured),
            Speed = speed,
            Calibration = calibration,
            FellBack = fellBack,
            Problems = problems ?? [],
        };
    }

    // ---- Writing ----

    /// <summary>Turns a request typed in plain words into a brief with the loaded model.</summary>
    public Task<DraftResult> DraftBriefAsync(PreparedModel model, string prompt, DraftOptions? options = null, CancellationToken ct = default) =>
        BriefDrafter.DraftAsync(model.Llm, prompt, options, ct);

    /// <summary>Drafts, repairs, grades, renders and saves one deck.</summary>
    public async Task<ForgeRun> RunAsync(PreparedModel model, Brief brief, ForgeRunOptions? options = null, IProgress<HarnessProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new ForgeRunOptions();
        var settings = RunPolicy.Decide(model.Facts, model.Speed, options.Request);
        var theme = Theme.All.FirstOrDefault(t => t.Key.Equals(options.Template ?? brief.TemplateHint, StringComparison.OrdinalIgnoreCase)) ?? Theme.Surface;

        using var meter = PowerMeterFor(model);
        var result = await DeckHarness.RunAsync(model.Llm, model.Story, brief, settings.ToOptions(theme), meter, progress, ct).ConfigureAwait(false);

        var clock = Stopwatch.StartNew();
        var deck = DeckBuilder.Build(result.Spec, theme);
        double buildSeconds = clock.Elapsed.TotalSeconds;
        string root = options.OutputRoot ?? OutputRoot;
        var files = ForgeOutput.Write(root, brief, deck, result.Spec,
            (path, saveSeconds) => Metrics.Build(result, model.Story, theme.Key, buildSeconds + saveSeconds, path), options.Previews, Log);

        Remember(model, result, deck.Slides.Count);
        return new ForgeRun(result, deck, files, settings, buildSeconds);
    }

    /// <summary>The NVIDIA GPU's power sensor when the model runs there; every other lane has no sensor a deck may quote.</summary>
    private NvidiaSmiPowerMeter? PowerMeterFor(PreparedModel model)
    {
        if (model.Facts.Lane != "gpu" || Hardware.Lane("gpu") is not { Vendor: "nvidia" }) return null;
        var meter = NvidiaSmiPowerMeter.TryCreate(out string why);
        if (meter is null) Log($"No power meter for this run: {why}.");
        return meter;
    }

    /// <summary>What a finished deck taught about this model on this PC, kept for the next run's ETA and settings.</summary>
    private void Remember(PreparedModel model, HarnessResult result, int slides)
    {
        var stats = result.Stats;
        var guidance = model.Llm.GuidedReplies > 0 ? GuidanceState.Verified : model.Llm.GuidanceRejected ? GuidanceState.Rejected : GuidanceState.Unknown;
        if (stats.TokensMeasured && stats.DecodeTokensPerSecond > 0 && stats.LlmCalls > 0)
            model.Speed = Speed.Record(new SpeedEntry
            {
                Variant = model.Variant,
                Model = model.Facts.Title,
                Lane = model.Facts.Lane,
                Ep = model.Facts.EpLabel,
                DecodeTokensPerSecond = stats.DecodeTokensPerSecond,
                FirstTokenSeconds = stats.FirstTokenSeconds / stats.LlmCalls,
                SlideTokens = stats.CompletionTokens / stats.LlmCalls,
                Guidance = guidance,
                MeasuredAt = DateTimeOffset.UtcNow,
            });
        Speed.RecordRun(new DeckRunRecord(model.Variant, slides, result.DraftSeconds, result.Mode.ToString(), DateTimeOffset.UtcNow));
        Speed.Save();
        if (Speed.Problem.Length > 0) Log(Speed.Problem);
    }

    public Task UnloadAsync(PreparedModel model, CancellationToken ct = default) => Engine.UnloadAsync(model.Model, ct);

    public ValueTask DisposeAsync() => Engine.DisposeAsync();
}
