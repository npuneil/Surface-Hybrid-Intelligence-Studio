using Microsoft.AI.Foundry.Local;

namespace SurfaceDeviceKit.FoundryLocal;

public sealed partial class FoundryEngine
{
    private readonly Dictionary<string, IModel?> _modelsByAlias = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    /// <summary>Variants that failed to start during this run. They are left out of later plans.</summary>
    public IReadOnlyCollection<string> FailedVariants => _failed;

    /// <summary>Leaves a variant out of later plans, for example one the user turned off, or to exercise the fallback in a test.</summary>
    public void Exclude(string variantId) => _failed.Add(variantId);

    /// <summary>Lets a variant back into plans.</summary>
    public void Include(string variantId) => _failed.Remove(variantId);

    /// <summary>
    /// Every model that can run on this PC for the workload, strongest first: lanes in this device's order, and inside a lane
    /// the catalog's candidates in order. A candidate counts only if the live Foundry Local catalog has a build for that
    /// lane's device on a registered provider, and its real file size (times the load overhead) fits the lane's memory budget.
    /// </summary>
    public async Task<ResolvedPlan> ResolveAsync(string workload, CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);
        var catalog = Service.Catalog;
        if (!catalog.Workloads.TryGetValue(workload, out var spec))
            throw new KeyNotFoundException($"Unknown workload '{workload}'. Known: {string.Join(", ", catalog.Workloads.Keys)}");

        var profile = Hardware.Profile;
        var options = new List<ResolvedModel>();
        var notes = new List<string>();
        foreach (var lane in profile.Lanes)
        {
            string label = catalog.Lanes[lane.Id].Label;
            if (!lane.Available)
            {
                if (lane.Id != "cpu" && lane.Note.Length > 0) notes.Add($"{label}: {lane.Note}");
                continue;
            }
            var reasons = new List<string>();
            int found = 0;
            foreach (string alias in spec.Candidates)
            {
                var (model, why) = await ResolveOnLaneAsync(spec.Id, alias, lane, enforceClassCap: true, ct);
                if (model is not null) { options.Add(model); found++; }
                else if (why is not null) reasons.Add(why);
            }
            if (found > 0) continue;
            if (_laneEpProblems.TryGetValue(lane.Id, out string? problem)) notes.Add($"{label}: {problem}");
            else notes.Add($"{label}: nothing in the list for this workload has a build that runs here. {string.Join(" ", reasons.Distinct().Take(2))}".TrimEnd());
        }
        return new ResolvedPlan(spec.Id, spec.Title, options, notes);
    }

    /// <summary>Resolves one specific model on one lane, for a model the user picked by hand or a test that forces a class.</summary>
    public async Task<(ResolvedModel? Model, string Why)> ResolveModelAsync(string alias, string laneId, CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);
        var lane = Hardware.Profile.Lane(laneId) ?? throw new KeyNotFoundException($"No lane '{laneId}'. Known: {string.Join(", ", Hardware.Profile.Lanes.Select(l => l.Id))}");
        if (!lane.Available) return (null, lane.Note.Length > 0 ? lane.Note : $"The {laneId} lane is not available on this PC.");
        var (model, why) = await ResolveOnLaneAsync("manual", alias, lane, enforceClassCap: false, ct);
        return (model, model is null ? why ?? $"{alias} cannot run on the {laneId} lane." : "");
    }

    private async Task<IModel?> GetModelAsync(string alias, CancellationToken ct)
    {
        if (_modelsByAlias.TryGetValue(alias, out var known)) return known;
        IModel? model = null;
        try { model = await Catalog.GetModelAsync(alias, ct); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log($"Catalog lookup for {alias} failed: {exception.Message}");
        }
        _modelsByAlias[alias] = model;
        return model;
    }

    private async Task<(ResolvedModel? Model, string? Why)> ResolveOnLaneAsync(string workload, string alias, LaneInfo lane, bool enforceClassCap, CancellationToken ct)
    {
        var catalog = Service.Catalog;
        catalog.Models.TryGetValue(alias, out var spec);
        string title = spec?.Title ?? alias;
        if (enforceClassCap && spec is not null && catalog.ModelClasses.Rank(spec.Class) > catalog.ModelClasses.Rank(lane.ClassCap)) return (null, null);

        var model = await GetModelAsync(alias, ct);
        if (model is null) return (null, $"{title} is not in this PC's model catalog.");

        string device = catalog.Lanes[lane.Id].DeviceType;
        string laneLabel = device.ToUpperInvariant();
        IModel? best = null;
        double bestGb = 0;
        bool bestCached = false;
        int bestScore = int.MaxValue;
        string? why = null;
        foreach (var variant in model.Variants)
        {
            var runtime = variant.Info.Runtime;
            if (!string.Equals(runtime?.DeviceType.ToString(), device, StringComparison.OrdinalIgnoreCase)) continue;
            string ep = EngineFormat.NormEp(runtime?.ExecutionProvider);
            int rank = IndexOf(lane.Eps, ep);
            if (rank < 0) continue;
            if (!_usable.Contains(ep)) { why ??= $"{title} needs the {EngineFormat.EpLabel(ep)} provider, which is not registered."; continue; }
            if (_failed.Contains(variant.Id)) { why ??= $"{title} did not start on the {laneLabel} earlier in this run."; continue; }

            double gb = (variant.Info.FileSizeMb ?? 0) / 1024.0;
            if (gb <= 0 && spec?.SizeFor(lane.Id) is { } estimate) gb = estimate.Gb;
            double required = catalog.LoadCost(gb);
            if (required > lane.BudgetGb)
            {
                why ??= $"{title} needs {required:0.0} GB on the {laneLabel}, which has {lane.BudgetGb:0.0} GB.";
                continue;
            }

            bool cached = await variant.IsCachedAsync(ct);
            int score = rank * 2 + (cached ? 0 : 1);
            if (score < bestScore || (score == bestScore && best is not null && variant.Info.Version > best.Info.Version))
            {
                best = variant;
                bestScore = score;
                bestGb = gb;
                bestCached = cached;
            }
        }
        if (best is null) return (null, why ?? $"{title} has no {laneLabel} build on this PC.");

        string bestEp = EngineFormat.NormEp(best.Info.Runtime?.ExecutionProvider);
        long? window = best.Info.ContextLength is > 0 ? best.Info.ContextLength : bestCached ? await ModelWindow.ReadAsync(best, ct) : null;
        return (new ResolvedModel
        {
            Workload = workload, Alias = alias, Title = title, Class = spec?.Class ?? "?", ParamsB = spec?.Params ?? 0,
            Lane = lane.Id, Device = laneLabel, Ep = bestEp, EpLabel = EngineFormat.EpLabel(bestEp), VariantId = best.Id,
            FileGb = bestGb, RequiredGb = catalog.LoadCost(bestGb), Cached = bestCached, Task = best.Info.Task ?? "",
            ContextTokens = window, Variant = best,
        }, null);
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Every model in this PC's live catalog with its builds, for diagnostics.</summary>
    public async Task<IReadOnlyList<ResolvedModel>> ListCatalogAsync(CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);
        var rows = new List<ResolvedModel>();
        foreach (var model in await Catalog.ListModelsAsync(ct))
            foreach (var variant in model.Variants)
            {
                var runtime = variant.Info.Runtime;
                string ep = EngineFormat.NormEp(runtime?.ExecutionProvider);
                string device = (runtime?.DeviceType.ToString() ?? "?").ToUpperInvariant();
                Service.Catalog.Models.TryGetValue(model.Alias, out var spec);
                bool cached = await variant.IsCachedAsync(ct);
                rows.Add(new ResolvedModel
                {
                    Workload = spec?.Task ?? "", Alias = model.Alias, Title = spec?.Title ?? variant.Info.DisplayName ?? model.Alias, Class = spec?.Class ?? "?",
                    ParamsB = spec?.Params ?? 0, Lane = device.ToLowerInvariant(), Device = device, Ep = ep, EpLabel = EngineFormat.EpLabel(ep),
                    VariantId = variant.Id, FileGb = (variant.Info.FileSizeMb ?? 0) / 1024.0, Cached = cached,
                    Task = variant.Info.Task ?? "", Variant = variant,
                    ContextTokens = variant.Info.ContextLength is > 0 ? variant.Info.ContextLength : cached ? await ModelWindow.ReadAsync(variant, ct) : null,
                });
            }
        return rows;
    }
}
