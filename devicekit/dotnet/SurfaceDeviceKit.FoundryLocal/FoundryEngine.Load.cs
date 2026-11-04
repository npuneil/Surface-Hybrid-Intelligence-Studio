using System.Diagnostics;
using Microsoft.AI.Foundry.Local;

namespace SurfaceDeviceKit.FoundryLocal;

/// <summary>What <see cref="FoundryEngine.LoadBestAsync"/> ended up doing.</summary>
public sealed record LoadOutcome(LoadedModel Model, ResolvedPlan Plan, IReadOnlyList<string> Problems)
{
    /// <summary>True when the model that started is not the first choice, so the screen can say so.</summary>
    public bool FellBack => Plan.Primary is { } first && first.VariantId != Model.Id;
}

public sealed partial class FoundryEngine
{
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private readonly Dictionary<string, LoadedModel> _loaded = new(StringComparer.Ordinal);

    public IReadOnlyList<LoadedModel> LoadedModels
    {
        get { lock (_loaded) return _loaded.Values.ToList(); }
    }

    /// <summary>Downloads (only when allowed), loads and warms the model. Loading the same variant twice returns the one already loaded.</summary>
    public async Task<LoadedModel> LoadAsync(ResolvedModel choice, bool allowDownload = false, Action<string>? progress = null, CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);
        await _modelGate.WaitAsync(ct);
        try
        {
            lock (_loaded)
                if (_loaded.TryGetValue(choice.VariantId, out var ready)) return ready;
            var model = choice.Variant;
            try
            {
                bool cached = await model.IsCachedAsync(ct);
                if (!cached)
                {
                    if (!allowDownload) throw new DownloadRequiredException(choice);
                    progress?.Invoke($"Downloading {choice.Title} ({EngineFormat.Gb(choice.FileGb)})...");
                    await model.DownloadAsync(p => progress?.Invoke($"Downloading {choice.Title}: {p:0}%"), ct);
                }
                progress?.Invoke($"Loading {choice.Title} on the {choice.LaneLabel}...");
                var clock = Stopwatch.StartNew();
                if (!await model.IsLoadedAsync(ct)) await model.LoadAsync(ct);
                var loaded = new LoadedModel { Model = model, Choice = choice, WasCached = cached, LoadSeconds = clock.Elapsed.TotalSeconds };
                if (loaded.ContextTokens is not > 0) loaded.ContextTokens = await ModelWindow.ReadAsync(model, ct);
                if (choice.IsChat)
                {
                    progress?.Invoke($"Warming up {choice.Title}...");
                    clock.Restart();
                    await GenerateCoreAsync(loaded, "", "Reply with OK.", new GenOptions { MaxTokens = 4, AllowEmpty = true, Temperature = 0 }, null, ct);
                    loaded.WarmSeconds = clock.Elapsed.TotalSeconds;
                }
                lock (_loaded) _loaded[choice.VariantId] = loaded;
                Log($"Loaded {choice.VariantId} on {choice.Device}/{choice.EpLabel}: load {loaded.LoadSeconds:0.0} s, warm {loaded.WarmSeconds:0.0} s");
                return loaded;
            }
            catch (OperationCanceledException) { throw; }
            catch (DownloadRequiredException) { throw; }
            catch (Exception exception)
            {
                Log($"Load {choice.VariantId} failed: {exception}");
                _failed.Add(choice.VariantId);
                try { if (await model.IsLoadedAsync(CancellationToken.None)) await model.UnloadAsync(CancellationToken.None); }
                catch (Exception unload) { Log("Unload after a failed start also failed: " + unload.Message); }
                throw new ModelLoadException($"{choice.Title} could not start on the {choice.LaneLabel}: {EngineFormat.FirstLine(exception.Message)}",
                    [$"{choice.Title} could not start on the {choice.LaneLabel}: {EngineFormat.FirstLine(exception.Message)}"]);
            }
        }
        finally { _modelGate.Release(); }
    }

    /// <summary>
    /// Plans the workload and loads the strongest option that starts. A model that is not downloaded is skipped unless downloads are
    /// allowed, and one that fails to start (a driver or provider problem) is left out while the next option, such as the same
    /// model on another device, is tried. Throws <see cref="ModelLoadException"/> with the reasons when nothing starts.
    /// </summary>
    public async Task<LoadOutcome> LoadBestAsync(string workload, bool allowDownload = false, Action<string>? progress = null, int maxAttempts = 3, CancellationToken ct = default)
    {
        var plan = await ResolveAsync(workload, ct);
        var problems = new List<string>();
        ResolvedModel? needsDownload = null;
        int attempts = 0;
        foreach (var option in plan.Options)
        {
            if (!option.Cached && !allowDownload) { needsDownload ??= option; continue; }
            if (attempts++ >= maxAttempts) break;
            if (problems.Count > 0) progress?.Invoke($"{problems[^1]} Trying {option.Summary}...");
            try
            {
                var loaded = await LoadAsync(option, allowDownload, progress, ct);
                return new LoadOutcome(loaded, plan, problems);
            }
            catch (ModelLoadException failure) { problems.AddRange(failure.Problems); }
        }

        string message;
        if (plan.Options.Count == 0) message = "No model for this workload can run on this PC. " + string.Join(" ", plan.Notes);
        else if (problems.Count == 0 && needsDownload is not null)
            message = $"Nothing for this workload is on this PC yet. The best option, {needsDownload.Summary}. Allow downloads to fetch it.";
        else message = string.Join(" ", problems.Take(3));
        throw new ModelLoadException(message.Trim(), problems);
    }

    public async Task UnloadAsync(LoadedModel model, CancellationToken ct = default)
    {
        await _modelGate.WaitAsync(ct);
        try
        {
            lock (_loaded) _loaded.Remove(model.Id);
            try { if (await model.Model.IsLoadedAsync(CancellationToken.None)) await model.Model.UnloadAsync(CancellationToken.None); }
            catch (Exception exception) { Log($"Unload {model.Id} failed: {exception.Message}"); }
        }
        finally { _modelGate.Release(); }
    }

    public async Task UnloadAllAsync()
    {
        foreach (var model in LoadedModels) await UnloadAsync(model);
    }

    /// <summary>Stops Foundry Local and starts it again, for example to pick up providers installed in the meantime.</summary>
    public async Task RestartAsync(bool allowDownload = false, Action<string>? progress = null, CancellationToken ct = default)
    {
        await _initGate.WaitAsync(ct);
        try
        {
            await StopCoreAsync();
            await StartCoreAsync(allowDownload, progress, ct);
        }
        finally { _initGate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        foreach (var model in LoadedModels)
        {
            try { if (await model.Model.IsLoadedAsync(CancellationToken.None)) await model.Model.UnloadAsync(CancellationToken.None); }
            catch (Exception exception) { Log($"Unload {model.Id} failed: {exception.Message}"); }
        }
        lock (_loaded) _loaded.Clear();
        _modelsByAlias.Clear();
        _catalog = null;
        _eps = [];
        _usable.Clear();
        _usable.Add("cpu");
        try { if (FoundryLocalManager.IsInitialized) FoundryLocalManager.Instance.Dispose(); }
        catch (Exception exception) { Log("Foundry Local dispose failed: " + exception.Message); }
        State = EngineState.NotStarted;
    }

    public async ValueTask DisposeAsync()
    {
        await _initGate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _initGate.Release(); }
    }
}
