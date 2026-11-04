namespace SurfaceDeviceKit;

public sealed record SelfTestResult(string Name, bool Passed, string Detail);

/// <summary>Checks the catalog's internal consistency and a set of known-answer cases, so a change to the catalog or the rules that moves an answer is noticed.</summary>
public static class DeviceKitSelfTest
{
    public static List<SelfTestResult> Run(Catalog? catalog = null)
    {
        catalog ??= Catalog.Default;
        var results = new List<SelfTestResult>();
        void Check(string name, bool ok, string detail = "") => results.Add(new SelfTestResult(name, ok, detail));

        // Catalog consistency
        var missing = catalog.Workloads.Values.SelectMany(w => w.Candidates.Where(a => !catalog.Models.ContainsKey(a)).Select(a => $"{w.Id}:{a}")).ToList();
        Check("catalog: every workload candidate is a known model", missing.Count == 0, string.Join(", ", missing));
        var badFrom = catalog.Workloads.Values.Where(w => !w.Candidates.Contains(w.AvailableFrom)).Select(w => w.Id).ToList();
        Check("catalog: availableFrom is one of the candidates", badFrom.Count == 0, string.Join(", ", badFrom));
        var badOrder = catalog.LaneOrder.Values.SelectMany(o => o).Where(l => !catalog.Lanes.ContainsKey(l)).Distinct().ToList();
        Check("catalog: lane orders only name known lanes", badOrder.Count == 0, string.Join(", ", badOrder));
        string[] kinds = ["gpu-discrete", "gpu-unified", "gpu-integrated", "npu", "cpu"];
        var noCap = kinds.Where(k => !catalog.ModelClasses.LaneCap.ContainsKey(k) || catalog.Budget.Rule(k).Fraction <= 0).ToList();
        Check("catalog: every lane kind has a class cap and a budget rule", noCap.Count == 0, string.Join(", ", noCap));
        var badClass = catalog.Models.Values.Where(m => !catalog.ModelClasses.Order.Contains(m.Class)).Select(m => m.Alias).ToList();
        Check("catalog: model classes are known", badClass.Count == 0, string.Join(", ", badClass));
        Check("simulated profile ids are unique", SimulatedProfiles.All.Select(d => d.Id).Distinct().Count() == SimulatedProfiles.All.Count);

        var reports = new Dictionary<string, DeviceReport>();
        foreach (var d in SimulatedProfiles.All) reports[d.Id] = DeviceDetectionService.Analyze(d.Raw, catalog, "simulated", d.Id, d.Label); // a repeated id is reported by the check above, not thrown

        // Every device gets an answer, and every ladder obeys the rules
        var noJson = reports.Where(r => r.Value.Plans["structured-json"].Primary is null).Select(r => r.Key).ToList();
        Check("every simulated device has a structured-json model", noJson.Count == 0, string.Join(", ", noJson));
        var breaks = new List<string>();
        foreach (var (id, r) in reports)
            foreach (var (wid, plan) in r.Plans)
                foreach (var lane in plan.Lanes)
                {
                    var info = r.Profile.Lane(lane.Lane)!;
                    int cap = catalog.ModelClasses.Rank(info.ClassCap);
                    var order = catalog.Workloads[wid].Candidates;
                    int last = -1;
                    foreach (var m in lane.Ladder)
                    {
                        int at = order.IndexOf(m.Alias);
                        if (m.RequiredGb > lane.BudgetGb + 0.001 || catalog.ModelClasses.Rank(m.Class) > cap || at <= last) breaks.Add($"{id}/{wid}/{lane.Lane}/{m.Alias}");
                        last = at;
                    }
                }
        Check("ladders are ordered, within the class cap and within budget", breaks.Count == 0, string.Join(", ", breaks.Take(5)));

        // Known answers
        void Expect(string id, string deviceClass, string pcClass, string? lane, string? alias)
        {
            var r = reports[id];
            var plan = r.Plans["structured-json"];
            bool ok = r.Profile.DeviceClass == deviceClass && r.Profile.PcClass == pcClass && plan.PrimaryLane == lane && plan.Primary?.Alias == alias;
            Check($"{id}: {deviceClass} / {pcClass} / {lane}:{alias}", ok,
                ok ? "" : $"got {r.Profile.DeviceClass} / {r.Profile.PcClass} / {plan.PrimaryLane}:{plan.Primary?.Alias}");
        }
        Expect("nvidia-n1x-128", "gpu-first", "builder", "gpu", "qwen2.5-coder-14b");
        Expect("nvidia-n1x-64", "gpu-first", "builder", "gpu", "qwen2.5-coder-14b");
        Expect("nvidia-n1x-32", "gpu-first", "builder", "gpu", "qwen2.5-coder-14b");
        Expect("nvidia-dgpu-64", "gpu-first", "gpu", "gpu", "qwen2.5-7b");
        Expect("nvidia-dgpu-32", "gpu-first", "gpu", "gpu", "qwen2.5-7b");
        Expect("nvidia-dgpu-24", "gpu-first", "gpu", "gpu", "qwen2.5-7b");
        Expect("nvidia-dgpu-16", "gpu-first", "gpu", "gpu", "phi-4-mini");
        Expect("snapdragon-x-elite-64", "npu-first", "copilot-plus", "npu", "qwen2.5-7b");
        Expect("snapdragon-x-elite-16", "npu-first", "copilot-plus", "npu", "qwen2.5-7b");
        Expect("snapdragon-x2-plus-8", "npu-first", "npu", "npu", "qwen2.5-0.5b");
        Expect("snapdragon-x-elite-16-emulated", "cpu-only", "copilot-plus", "cpu", "phi-4-mini");
        Expect("snapdragon-sq3-16", "igpu-first", "windows", "gpu", "qwen2.5-7b");
        Expect("intel-lunar-lake-16", "npu-first", "copilot-plus", "npu", "qwen2.5-7b");
        Expect("intel-meteor-lake-16", "igpu-first", "npu", "gpu", "qwen2.5-7b");
        Expect("intel-12th-gen-16", "igpu-first", "windows", "gpu", "qwen2.5-7b");

        var n1x = reports["nvidia-n1x-128"].Profile;
        Check("n1x: the NPU is listed but not used", n1x.Npus.Count == 1 && !n1x.Npus[0].LlmCapable && !n1x.Lane("npu")!.Available);
        Check("n1x: graphics memory comes from the GPU, not the visible RAM", n1x.Lane("gpu")!.BudgetGb > 55 && n1x.Lane("cpu")!.BudgetGb < 35, $"gpu {n1x.Lane("gpu")!.BudgetGb}, cpu {n1x.Lane("cpu")!.BudgetGb}");
        Check("n1x: the RAM tier uses installed memory", n1x.Memory.Tier == 128);
        Check("meteor lake: a 11 TOPS NPU is not used for language models", !reports["intel-meteor-lake-16"].Profile.Lane("npu")!.Available);
        Check("emulated build reports limited native-build", reports["snapdragon-x-elite-16-emulated"].Profile.Capabilities.First(c => c.Id == "native-build").Status == CapabilityMatrix.Limited);
        Check("x2 plus with 8 GB is not Copilot+ class", reports["snapdragon-x2-plus-8"].Profile.Capabilities.First(c => c.Id == "copilot-plus").Status == CapabilityMatrix.Unavailable);
        Check("16 GB CPU lane budget: phi-4-mini fits, qwen2.5-7b does not",
            reports["snapdragon-x-elite-16-emulated"].Plans["chat"].Lanes.First(l => l.Lane == "cpu").Ladder.Select(m => m.Alias).ToList() is var cpuChat
            && cpuChat.Contains("phi-4-mini") && !cpuChat.Contains("qwen2.5-7b"), "");
        static string PackageById(string id) =>
            id.StartsWith("snapdragon-", StringComparison.Ordinal) ? SiliconPackage.SnapdragonArm64
            : id.StartsWith("nvidia-n1x-", StringComparison.Ordinal) ? SiliconPackage.NvidiaArm64
            : id.StartsWith("nvidia-", StringComparison.Ordinal) ? SiliconPackage.NvidiaX64
            : SiliconPackage.IntelX64;
        var wrongPackage = reports.Where(r => SiliconPackage.For(r.Value.Profile) != PackageById(r.Key))
            .Select(r => $"{r.Key} got {SiliconPackage.For(r.Value.Profile)}").ToList();
        Check("every simulated device maps to its silicon package", wrongPackage.Count == 0, string.Join(", ", wrongPackage));
        Check("build stamps name packages in any case", SiliconPackage.Name("snapdragon", "arm64") == SiliconPackage.SnapdragonArm64
            && SiliconPackage.Name("NVIDIA", "x64") == SiliconPackage.NvidiaX64 && SiliconPackage.Name("any", "x64") == "dev-x64" && SiliconPackage.Name(null, "arm64") == "dev-arm64");
        return results;
    }
}
