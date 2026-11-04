namespace SurfaceDeviceKit;

/// <summary>Chooses models. For each lane the catalog's candidates are walked strongest to weakest, and a model stays on the ladder
/// only if its class is allowed on that lane and its weights (times the load overhead) fit the lane's memory budget.
/// The Foundry Local adapter re-checks the result against the live catalog before anything loads.</summary>
public static class Planner
{
    public static IReadOnlyDictionary<string, WorkloadPlan> PlanAll(DeviceProfile profile, Catalog catalog) =>
        catalog.Workloads.Keys.ToDictionary(id => id, id => Plan(profile, catalog, id));

    public static WorkloadPlan Plan(DeviceProfile profile, Catalog catalog, string workloadId)
    {
        if (!catalog.Workloads.TryGetValue(workloadId, out var workload))
            throw new KeyNotFoundException($"Unknown workload '{workloadId}'. Known: {string.Join(", ", catalog.Workloads.Keys)}");

        var lanePlans = profile.Lanes.Select(lane => PlanLane(lane, workload, catalog)).ToList();
        var primary = lanePlans.FirstOrDefault(l => l.Choice is not null);
        var others = lanePlans.Where(l => l != primary && l.Choice is not null).ToList();
        return new WorkloadPlan(workload.Id, workload.Title, workload.Why, primary?.Lane, primary?.Choice, lanePlans, Summarize(catalog, primary, others));
    }

    public static LanePlan PlanLane(LaneInfo lane, WorkloadSpec workload, Catalog catalog)
    {
        var ladder = new List<ModelChoice>();
        if (lane.Available)
        {
            int cap = catalog.ModelClasses.Rank(lane.ClassCap);
            foreach (string alias in workload.Candidates)
            {
                if (!catalog.Models.TryGetValue(alias, out var model)) continue;
                if (catalog.ModelClasses.Rank(model.Class) > cap) continue;
                if (model.SizeFor(lane.Id) is not { } size) continue;
                double required = catalog.LoadCost(size.Gb);
                if (required > lane.BudgetGb) continue;
                ladder.Add(new ModelChoice(alias, model.Title, model.Class, model.Params,
                    Classifier.R(size.Gb), size.Estimated, Classifier.R(required)));
            }
        }

        string reason = ladder.Count > 0 ? ""
            : lane.Available ? $"Nothing in the list fits {lane.BudgetGb:0.0} GB on this lane."
            : lane.Note;
        return new LanePlan(lane.Id, lane.Kind, lane.BudgetGb, ladder.FirstOrDefault(), ladder, reason);
    }

    private static string Summarize(Catalog catalog, LanePlan? primary, List<LanePlan> others)
    {
        if (primary?.Choice is not { } pick) return "Nothing for this workload fits this device.";
        string text = $"{pick.Title} on the {catalog.Lanes[primary.Lane].Label} ({pick.SizeGb:0.0} GB model file{(pick.SizeEstimated ? ", size estimated" : "")})";
        if (others.Count > 0)
            text += ". Fallback: " + string.Join(", then ", others.Select(l => $"{l.Choice!.Title} on the {catalog.Lanes[l.Lane].Label}")) + ".";
        return text;
    }
}
