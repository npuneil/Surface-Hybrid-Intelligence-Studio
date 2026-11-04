using System.Text.Json;
using SurfaceDeviceKit;

// surface-device: detect this PC, or pretend to be another, and print what it can do.
const string Usage = """
    surface-device [options]

      (no options)           Report for this PC
      --json                 Print JSON instead of text
      --raw                  Print the raw hardware the probe collected (JSON)
      --raw-file FILE        Classify raw hardware captured on another PC (output of --raw)
      --list                 List the simulated devices
      --simulate ID          Report for a simulated device
      --plan WORKLOAD        Only the plan for one workload
      --all-simulated        With --json: every simulated device in one object
      --golden               Print the golden vectors (the structural answers the Python port is tested against)
      --check-golden FILE    Compare this build's answers with a golden-vectors file; exit 1 on any difference
      --self-test            Check the catalog and the known answers
      --catalog FILE         Use a different device-catalog.json
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

try
{
    if (Flag("--help") || Flag("-h") || Flag("-?")) { Console.WriteLine(Usage); return 0; }
    bool json = Flag("--json");
    bool raw = Flag("--raw");
    bool list = Flag("--list");
    bool all = Flag("--all-simulated");
    bool golden = Flag("--golden");
    string? checkGolden = Value("--check-golden");
    bool selfTest = Flag("--self-test");
    string? rawFile = Value("--raw-file");
    string? simulate = Value("--simulate");
    string? plan = Value("--plan");
    string? catalogFile = Value("--catalog");
    if (args2.Count > 0) throw new ArgumentException($"Unknown option '{args2[0]}'.\n\n{Usage}");

    var catalog = catalogFile is null ? Catalog.Default : Catalog.Parse(File.ReadAllText(catalogFile));

    if (selfTest)
    {
        var results = DeviceKitSelfTest.Run(catalog);
        foreach (var r in results) Console.WriteLine($"{(r.Passed ? "PASS" : "FAIL")}  {r.Name}{(r.Passed || r.Detail.Length == 0 ? "" : "  <- " + r.Detail)}");
        int failed = results.Count(r => !r.Passed);
        Console.WriteLine($"\nDevice kit self-test: {results.Count - failed}/{results.Count} checks passed");
        return failed == 0 ? 0 : 1;
    }

    if (golden) { Console.WriteLine(GoldenVectors.Build(catalog)); return 0; }

    if (checkGolden is not null)
    {
        var diffs = GoldenVectors.Compare(File.ReadAllText(checkGolden), GoldenVectors.Build(catalog));
        foreach (string d in diffs.Take(40)) Console.WriteLine(d);
        Console.WriteLine(diffs.Count == 0 ? "Golden vectors match." : $"{diffs.Count} difference(s) from {checkGolden}. Regenerate with --golden if the change was intended.");
        return diffs.Count == 0 ? 0 : 1;
    }

    if (list)
    {
        foreach (var g in SimulatedProfiles.All.GroupBy(d => d.Group))
        {
            Console.WriteLine($"{g.Key}");
            foreach (var d in g) Console.WriteLine($"  {d.Id,-34} {d.Label}");
        }
        return 0;
    }

    if (all)
    {
        var map = SimulatedProfiles.All.ToDictionary(d => d.Id, d => DeviceDetectionService.Analyze(d.Raw, catalog, "simulated", d.Id, d.Label));
        Console.WriteLine(DeviceJson.Serialize(map));
        return 0;
    }

    DeviceReport report;
    if (rawFile is not null)
        report = DeviceDetectionService.Analyze(DeviceJson.ParseRaw(File.ReadAllText(rawFile)), catalog, "detected");
    else if (simulate is not null)
    {
        var sim = SimulatedProfiles.Find(simulate) ?? throw new ArgumentException($"No simulated device '{simulate}'. Try --list.");
        report = DeviceDetectionService.Analyze(sim.Raw, catalog, "simulated", sim.Id, sim.Label);
    }
    else
    {
        var service = new DeviceDetectionService(catalog);
        if (raw) { Console.WriteLine(DeviceJson.Serialize(service.Detected.Profile.Raw)); return 0; }
        report = service.Detected;
    }

    if (plan is not null)
    {
        var one = report.Plans.TryGetValue(plan, out var p) ? p : throw new ArgumentException($"Unknown workload '{plan}'. Known: {string.Join(", ", report.Plans.Keys)}");
        Console.WriteLine(json ? DeviceJson.Serialize(one) : $"{one.Title}\n  {one.Summary}\n{string.Join("\n", one.Lanes.Select(l => $"  {l.Lane,-3} budget {l.BudgetGb,5:0.0} GB  ladder: {(l.Ladder.Count > 0 ? string.Join(" > ", l.Ladder.Select(m => m.Alias)) : l.Reason)}"))}");
        return 0;
    }

    Console.WriteLine(json ? DeviceJson.Serialize(report) : DeviceReportText.Render(report, catalog));
    return 0;
}
catch (Exception e) when (e is ArgumentException or KeyNotFoundException or IOException or JsonException or InvalidDataException)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}
