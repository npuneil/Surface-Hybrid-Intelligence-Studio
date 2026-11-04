using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AI.Foundry.Local;
using FoundryConfiguration = Microsoft.AI.Foundry.Local.Configuration;
using FoundryLogLevel = Microsoft.AI.Foundry.Local.LogLevel;

namespace SurfaceDeviceKit.FoundryLocal;

/// <summary>
/// Runs Foundry Local in-process for the device kit's plans. The kit decides what should run where from the hardware;
/// this class checks that against what Foundry Local really offers on this PC (live catalog, registered execution
/// providers, real file sizes, what is already downloaded), loads it, and measures every generation.
/// Live runs always use the real hardware. A simulated device in the service only changes what is displayed and planned.
/// </summary>
public sealed partial class FoundryEngine : IAsyncDisposable
{
    public const string AppDataEnvironmentVariable = "SURFACE_DEVICEKIT_APPDATA";
    public const string SkipGpuLibrariesEnvironmentVariable = "SURFACE_DEVICEKIT_SKIP_GPU_LIBS";

    private readonly EngineOptions _options;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly HashSet<string> _usable = new(StringComparer.Ordinal) { "cpu" };
    private readonly List<string> _epNotes = [];
    private readonly Dictionary<string, string> _laneEpProblems = new(StringComparer.Ordinal);
    private EpInfo[] _eps = [];
    private ICatalog? _catalog;
    private string _logFile = "";

    public FoundryEngine(DeviceDetectionService? service = null, EngineOptions? options = null)
    {
        Service = service ?? DeviceKit.Service;
        _options = options ?? new EngineOptions();
    }

    public DeviceDetectionService Service { get; }

    /// <summary>The real PC, whatever the service is simulating.</summary>
    public DeviceReport Hardware => Service.Detected;

    public EngineState State { get; private set; } = EngineState.NotStarted;
    public string Status { get; private set; } = "Local AI has not started.";
    public string AppDataDirectory { get; private set; } = "";
    public string ModelCacheDirectory => AppDataDirectory.Length == 0 ? "" : Path.Combine(AppDataDirectory, "models");
    public string LogsDirectory => _options.LogsDirectory ?? Path.Combine(LocalAppData, _options.AppName, "logs");
    public CudaSupplementStatus? Cuda { get; private set; }
    public IReadOnlyList<string> EpNotes => _epNotes;
    public IReadOnlyCollection<string> UsableEps => _usable;
    public IReadOnlyList<EpState> Providers => _eps
        .Select(e => (e, token: EngineFormat.NormEp(e.Name)))
        .Select(x => new EpState(x.token, x.e.Name, x.e.IsRegistered, _usable.Contains(x.token), EpCached(x.token)))
        .ToList();

    /// <summary>Raised whenever the state or the status line changes. Runs on whatever thread did the work.</summary>
    public event Action<EngineState, string>? StatusChanged;

    internal static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private ICatalog Catalog => _catalog ?? throw new InvalidOperationException("The engine has not started. Call InitializeAsync first.");

    private void Report(EngineState state, string status, Action<string>? progress = null)
    {
        State = state;
        Status = status;
        progress?.Invoke(status);
        try { StatusChanged?.Invoke(state, status); }
        catch (Exception exception) { Log("StatusChanged handler failed: " + exception.Message); }
    }

    internal void Log(string line)
    {
        try { _options.Log?.Invoke(line); }
        catch (Exception) { /* a logging callback must never break the engine */ }
        try
        {
            if (_logFile.Length == 0)
            {
                Directory.CreateDirectory(LogsDirectory);
                _logFile = Path.Combine(LogsDirectory, "engine.log");
            }
            File.AppendAllText(_logFile, $"[{DateTimeOffset.Now:O}] {line}\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Starts Foundry Local and registers the execution providers this PC's lanes need. Providers already on the PC register
    /// without a network; a provider that is not there is downloaded only when <paramref name="allowDownload"/> is true.
    /// Providers must be registered before the catalog is read (the catalog lists only models the registered providers can run),
    /// so turning downloads on after a start restarts the engine.
    /// </summary>
    public async Task InitializeAsync(bool allowDownload = false, Action<string>? progress = null, CancellationToken ct = default)
    {
        await _initGate.WaitAsync(ct);
        try
        {
            if (_catalog is not null && allowDownload && HasRegistrableMissingEps())
            {
                Log("Downloads were turned on after a start; restarting Foundry Local so new providers appear in the catalog.");
                await StopCoreAsync();
            }
            await StartCoreAsync(allowDownload, progress, ct);
        }
        finally { _initGate.Release(); }
    }

    private async Task StartCoreAsync(bool allowDownload, Action<string>? progress, CancellationToken ct)
    {
        try
        {
            if (_catalog is null)
            {
                Report(EngineState.Starting, "Starting Foundry Local...", progress);
                if (AppDataDirectory.Length == 0) ChooseAppData();
            }
            // NVIDIA's libraries must be in the process before Foundry Local loads the CUDA provider.
            await EnsureCudaAsync(allowDownload, progress, ct);
            if (_catalog is null)
            {
                if (!FoundryLocalManager.IsInitialized)
                {
                    Directory.CreateDirectory(LogsDirectory);
                    var config = new FoundryConfiguration
                    {
                        AppName = new DirectoryInfo(AppDataDirectory).Name,
                        AppDataDir = AppDataDirectory,
                        ModelCacheDir = ModelCacheDirectory,
                        LogsDir = LogsDirectory,
                        LogLevel = FoundryLogLevel.Warning,
                        DisableNonessentialTelemetry = true,
                    };
                    await FoundryLocalManager.CreateAsync(config, new FileLogger(Path.Combine(LogsDirectory, "foundry-local.log")), ct);
                }
                try { _eps = FoundryLocalManager.Instance.DiscoverEps(); }
                catch (Exception exception)
                {
                    Log("DiscoverEps failed: " + exception);
                    _epNotes.Add("Could not list execution providers: " + EngineFormat.FirstLine(exception.Message));
                }
            }
            await RegisterEpsAsync(allowDownload, progress, ct);
            if (_catalog is null)
            {
                Report(EngineState.Starting, "Opening the model catalog...", progress);
                _catalog = await FoundryLocalManager.Instance.GetCatalogAsync(ct);
                Log($"Foundry Local ready; app data {AppDataDirectory}; usable providers {string.Join(",", _usable)}");
            }
            Report(EngineState.Ready, "Foundry Local is ready.", progress);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            Log("Engine start failed: " + exception);
            Report(EngineState.Unavailable, "Local AI could not start: " + EngineFormat.FirstLine(exception.Message), progress);
            throw;
        }
    }

    /// <summary>Starts with downloads off if nothing has started yet.</summary>
    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_catalog is null) await InitializeAsync(false, null, ct);
    }

    private void ChooseAppData()
    {
        string? forced = Environment.GetEnvironmentVariable(AppDataEnvironmentVariable);
        string chosen;
        if (!string.IsNullOrWhiteSpace(_options.AppDataDirectory)) chosen = Path.GetFullPath(_options.AppDataDirectory);
        else if (!string.IsNullOrWhiteSpace(forced)) chosen = Path.GetFullPath(forced);
        else
        {
            var candidates = new[] { _options.AppName }.Concat(_options.SharedAppData).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => Path.Combine(LocalAppData, name)).ToList();
            chosen = candidates.FirstOrDefault(HasModels) ?? candidates[0];
        }
        AppDataDirectory = chosen;
        Directory.CreateDirectory(ModelCacheDirectory);

        static bool HasModels(string directory)
        {
            string models = Path.Combine(directory, "models");
            return Directory.Exists(models) && Directory.EnumerateFileSystemEntries(models).Any();
        }
    }

    private async Task EnsureCudaAsync(bool allowDownload, Action<string>? progress, CancellationToken ct)
    {
        if (!CudaSupplement.Applies(Hardware.Profile) || Cuda?.State is "active" or "not_needed" or "skipped") return;
        if (Environment.GetEnvironmentVariable(SkipGpuLibrariesEnvironmentVariable) == "1")
        {
            // Diagnostics only: reproduces a PC without the NVIDIA libraries so the model fallback can be exercised.
            Cuda = new CudaSupplementStatus("skipped", $"NVIDIA GPU libraries skipped ({SkipGpuLibrariesEnvironmentVariable}=1).");
            Log(Cuda.Detail);
            return;
        }
        Report(EngineState.Starting, "Checking the NVIDIA GPU libraries...", progress);
        try
        {
            var others = new[] { _options.AppName }.Concat(_options.SharedAppData)
                .Select(name => Path.Combine(LocalAppData, name))
                .Where(directory => !string.Equals(directory, AppDataDirectory, StringComparison.OrdinalIgnoreCase));
            Cuda = await CudaSupplement.EnsureAsync(AppDataDirectory, others, allowDownload, s => Report(EngineState.Starting, s, progress), ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log("NVIDIA GPU libraries failed: " + exception);
            Cuda = new CudaSupplementStatus("error", "NVIDIA GPU libraries could not load: " + EngineFormat.FirstLine(exception.Message));
        }
        Log($"NVIDIA GPU libraries: {Cuda.State} - {Cuda.Detail}");
    }

    /// <summary>Registers, for each usable lane, the first provider on its list that this PC can register, and more only when asked.</summary>
    private async Task RegisterEpsAsync(bool allowDownload, Action<string>? progress, CancellationToken ct)
    {
        _epNotes.Clear();
        _laneEpProblems.Clear();
        var profile = Hardware.Profile;
        var lanes = profile.UsableLanes.Where(l => l.Id != "cpu").ToList();
        if (lanes.Count == 0)
        {
            _epNotes.Add(profile.Os.Emulated
                ? "This app runs under x64 emulation here, so local models use the CPU. The native Arm64 build uses the GPU and NPU."
                : "No GPU or NPU lane is available on this PC, so local models use the CPU.");
            return;
        }

        var manager = FoundryLocalManager.Instance;
        var wanted = lanes.SelectMany(l => l.Eps).Where(e => e != "cpu").Distinct().ToList();

        // Providers the runtime already has cost nothing.
        foreach (var info in _eps.Where(e => e.IsRegistered))
        {
            string token = EngineFormat.NormEp(info.Name);
            if (wanted.Contains(token) && _usable.Add(token)) _epNotes.Add($"{EngineFormat.EpLabel(token)} ready.");
        }

        foreach (var lane in lanes)
        {
            var list = lane.Eps.Where(e => e != "cpu").ToList();
            foreach (string token in list)
            {
                if (_usable.Contains(token))
                {
                    if (_options.RegisterAllEps) continue;
                    break;
                }
                var info = _eps.FirstOrDefault(e => EngineFormat.NormEp(e.Name) == token);
                if (info is null) continue;
                string label = EngineFormat.EpLabel(token);
                if (!allowDownload && !EpCached(token))
                {
                    _laneEpProblems.TryAdd(lane.Id, $"{label} is not installed on this PC yet. Allow downloads to add it.");
                    continue;
                }
                Report(EngineState.Starting, $"Preparing {label}...", progress);
                try
                {
                    var result = await manager.DownloadAndRegisterEpsAsync([info.Name],
                        (ep, pct) => Report(EngineState.Starting, $"Preparing {EngineFormat.EpLabel(ep)}: {pct:0}%", progress), ct);
                    bool ok = result.Success || (result.RegisteredEps?.Contains(info.Name, StringComparer.OrdinalIgnoreCase) ?? false);
                    if (ok)
                    {
                        _usable.Add(token);
                        _epNotes.Add($"{label} registered.");
                        _laneEpProblems.Remove(lane.Id);
                        if (!_options.RegisterAllEps) break;
                    }
                    else
                    {
                        _epNotes.Add($"{label} registration reported '{result.Status}'.");
                        _laneEpProblems.TryAdd(lane.Id, $"{label} could not register ({result.Status}).");
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Log($"Register {info.Name} failed: {exception}");
                    _epNotes.Add($"{label} could not register: {EngineFormat.FirstLine(exception.Message)}");
                    _laneEpProblems.TryAdd(lane.Id, $"{label} could not register: {EngineFormat.FirstLine(exception.Message)}");
                }
            }
            if (!list.Any(_usable.Contains) && !_laneEpProblems.ContainsKey(lane.Id) && list.Count > 0)
                _laneEpProblems[lane.Id] = $"Foundry Local does not offer a {string.Join(" or ", list.Select(EngineFormat.EpLabel))} provider for the {profile.Lane(lane.Id)?.Device ?? lane.Id}.";
        }
        if (Cuda is { State: not ("active" or "not_needed") }) _epNotes.Add(Cuda.Detail);
        try { _eps = manager.DiscoverEps(); }
        catch (Exception exception) { Log("DiscoverEps after registering failed: " + exception.Message); }
        foreach (string note in _epNotes) Log("EP: " + note);
    }

    /// <summary>True when a restart with downloads on would add a provider: a lane with no usable provider yet that the runtime offers one for
    /// (or, with RegisterAllEps, any missing provider). A lane already covered by its first working provider does not restart the engine,
    /// because a restart unloads every model.</summary>
    private bool HasRegistrableMissingEps() =>
        Hardware.Profile.UsableLanes.Where(l => l.Id != "cpu").Any(lane =>
        {
            var list = lane.Eps.Where(e => e != "cpu").ToList();
            if (!_options.RegisterAllEps && list.Any(_usable.Contains)) return false;
            return list.Any(token => !_usable.Contains(token) && _eps.Any(e => EngineFormat.NormEp(e.Name) == token));
        });

    private bool EpCached(string token)
    {
        if (WinMlEpInstalled(token)) return true;
        if (AppDataDirectory.Length == 0) return false;
        string root = Path.Combine(AppDataDirectory, "ep");
        if (!Directory.Exists(root)) return false;
        foreach (string directory in Directory.GetDirectories(root))
        {
            string name = Regex.Replace(Path.GetFileName(directory).ToLowerInvariant(), "[^a-z0-9]", "");
            if (name.EndsWith("ep", StringComparison.Ordinal)) name = name[..^2];
            if (name == token) return true;
        }
        return false;
    }

    // Windows ML installs some providers system-wide (for example "MicrosoftCorporationII.WinML.Qualcomm.QNN.EP.1"); those register offline.
    private static readonly Dictionary<string, string> WinMlEpTokens = new(StringComparer.Ordinal)
    {
        ["nvtensorrtrtx"] = "trtrtx", ["openvino"] = "openvino", ["qnn"] = "qnn", ["vitisai"] = "vitisai",
    };
    private static string[]? _winMlEpPackages;

    private bool WinMlEpInstalled(string token)
    {
        if (!WinMlEpTokens.TryGetValue(token, out string? part)) return false;
        _winMlEpPackages ??= ListWinMlEpPackages();
        return _winMlEpPackages.Any(name => name.Contains(part, StringComparison.Ordinal));
    }

    private string[] ListWinMlEpPackages()
    {
        try
        {
            return new Windows.Management.Deployment.PackageManager().FindPackagesForUser(string.Empty)
                .Select(package => package.Id.Name)
                .Where(name => name.StartsWith("MicrosoftCorporationII.WinML.", StringComparison.OrdinalIgnoreCase))
                .Select(name => Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]", ""))
                .ToArray();
        }
        catch (Exception exception)
        {
            Log("Windows ML provider packages could not be listed: " + exception.Message);
            return [];
        }
    }

    public JsonObject Describe() => new()
    {
        ["state"] = State.ToString(),
        ["status"] = Status,
        ["appData"] = AppDataDirectory,
        ["modelCache"] = ModelCacheDirectory,
        ["providers"] = new JsonArray(Providers.Select(p => (JsonNode)new JsonObject
        {
            ["name"] = p.Name, ["token"] = p.Token, ["registered"] = p.Registered, ["usable"] = p.Usable, ["installedOnThisPc"] = p.Cached,
        }).ToArray()),
        ["usable"] = new JsonArray(_usable.Order().Select(e => (JsonNode)e).ToArray()),
        ["notes"] = new JsonArray(_epNotes.Select(e => (JsonNode)e).ToArray()),
        ["cuda"] = Cuda is null ? null : new JsonObject { ["state"] = Cuda.State, ["detail"] = Cuda.Detail, ["path"] = Cuda.Path },
        ["failedVariants"] = new JsonArray(FailedVariants.Select(e => (JsonNode)e).ToArray()),
        ["loaded"] = new JsonArray(LoadedModels.Select(m => (JsonNode)m.Describe()).ToArray()),
    };
}
