using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Forge.Core.Harness;
using Forge.Core.Ir;
using Forge.Core.Layouts;
using Forge.Core.Spec;
using Forge.Engine;
using SurfaceDeviceKit;
using SurfaceDeviceKit.FoundryLocal;

namespace Forge.Tests;

/// <summary>Forge.Engine without Foundry Local or a power sensor: the glue between the device kit, the runtime and the harness, driven by a fake
/// chat backend that plays a loaded model. What needs the real runtime (loading, the live catalog, nvidia-smi) is covered by the command line's
/// live test, because a unit test cannot honestly stand in for it.</summary>
public static class EngineTests
{
    private static bool Verbose => Environment.GetEnvironmentVariable("FORGE_TEST_VERBOSE") == "1";

    public static int Run()
    {
        var check = new Check("engine");
        var tests = new (string Name, Action<Check> Test)[]
        {
            ("llm-fit", LlmFit), ("llm-guidance", LlmGuidance), ("llm-fallback", LlmFallback), ("llm-info", LlmInfoMapping), ("model-window", ModelWindows),
            ("story-devices", StoryOverDevices), ("story-names", StoryNames), ("story-silicon", StorySilicon), ("story-facts", StoryFacts),
            ("power", Power), ("policy", Policy), ("speed", SpeedMath), ("speed-file", SpeedFile),
            ("calibrate", Calibrate), ("output", Output), ("attach", Attach), ("session", SessionRun), ("honest", Honest),
        };
        foreach (var (name, test) in tests)
        {
            try { test(check); }
            catch (Exception ex) { check.That(false, name + " crashed", ex.ToString()); }
        }
        return check.Finish();
    }

    // ---- fakes and helpers ----

    /// <summary>A loaded model that answers from a function and records what it was asked.</summary>
    private sealed class FakeBackend : IChatBackend
    {
        public PromptFit? Fit { get; set; } = new(true, 900, 1600, 32768, 0);
        public Func<string, string, GenOptions, GenerationResult> Answer { get; set; } = (_, _, _) => Said("{}");
        public List<GenOptions> Options { get; } = [];
        public int Preflights { get; private set; }

        public Task<PromptFit?> PreflightAsync(string system, string prompt, int maxTokens, CancellationToken ct)
        {
            Preflights++;
            return Task.FromResult(Fit);
        }

        public Task<GenerationResult> GenerateAsync(string system, string prompt, GenOptions options, Action<string>? onText, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Options.Add(options);
            var result = Answer(system, prompt, options);
            onText?.Invoke(result.Text);
            return Task.FromResult(result);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "forge-engine-" + Guid.NewGuid().ToString("N")[..8]);
        public TempDir() => Directory.CreateDirectory(Dir);
        public string At(string name) => Path.Combine(Dir, name);

        public void Dispose()
        {
            try { Directory.Delete(Dir, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>About 40 tokens per second of decode, as <see cref="ScriptedLlm.Say"/> does.</summary>
    private static GenerationResult Said(string text, int tokens = 120, bool measured = true) =>
        new(text, 0.4, 3.4, tokens, measured, false, "Fake 7B", "QNN", "NPU", 3.0);

    private static LlmInfo Info(bool guidance = true, int context = 32768) => new("Fake 7B", "npu", "NPU", "Foundry Local (QNN)", context, "M", 7, guidance);

    private static LlmRequest Plain(string user = "hello") => new("system", user);

    private static LlmRequest WithSchema() => new("system", "user") { Schema = new JsonObject { ["type"] = "object" }, MaxTokens = 700, Temperature = 0.4f };

    private static LlmReply Chat(ILlm llm, LlmRequest request) => llm.ChatAsync(request).GetAwaiter().GetResult();

    private static T? Catch<T>(Action action) where T : Exception
    {
        try { action(); return null; }
        catch (T ex) { return ex; }
    }

    private static ResolvedModel Resolved(string alias, string title, string cls, string lane, string epLabel, double paramsB, double fileGb, long? context) => new()
    {
        Workload = ForgeSession.Workload, Alias = alias, Title = title, Class = cls, ParamsB = paramsB, Lane = lane, Device = lane.ToUpperInvariant(),
        Ep = epLabel + "ExecutionProvider", EpLabel = epLabel, VariantId = $"{alias}-{lane}-{epLabel.ToLowerInvariant()}:1",
        FileGb = fileGb, RequiredGb = fileGb * 1.3, Cached = true, ContextTokens = context,
    };

    private static ResolvedModel Qwen7(long? context = 32768) => Resolved("qwen2.5-7b", "Qwen2.5 7B", "M", "npu", "QNN", 7, 4.8, context);

    private static DeviceReport Report(string id)
    {
        var sim = SimulatedProfiles.Find(id) ?? throw new KeyNotFoundException(id);
        return DeviceDetectionService.Analyze(sim.Raw, null, "simulated", sim.Id, sim.Label);
    }

    private static string EpLabelOf(LaneInfo lane)
    {
        string eps = string.Join(" ", lane.Eps).ToLowerInvariant();
        return eps.Contains("qnn") ? "QNN" : eps.Contains("openvino") ? "OpenVINO" : eps.Contains("cuda") ? "CUDA" : lane.Id == "cpu" ? "CPU" : "DirectML";
    }

    /// <summary>The model the kit's plan picks first for the Forge on a device, as the story sees it.</summary>
    private static ModelFacts? PrimaryFacts(DeviceReport report)
    {
        var plan = report.Plans[ForgeSession.Workload];
        if (plan.Primary is not { } choice || plan.PrimaryLane is not { } laneId || report.Profile.Lane(laneId) is not { } lane) return null;
        return new ModelFacts(choice.Title, laneId, laneId.ToUpperInvariant(), EpLabelOf(lane), choice.Class, choice.ParamsB, choice.SizeGb, 32768);
    }

    private static readonly Regex SlidePrompt = new(@"SLIDE (\d+) of (\d+): \[(\w+)\] (\w+)", RegexOptions.CultureInvariant);

    /// <summary>What a well-behaved model writes for the slide a guided prompt asks about, read from the prompt text (a loaded model sees only text).</summary>
    private static string SlideReply(string system, string prompt)
    {
        var m = SlidePrompt.Match(prompt);
        if (!m.Success) throw new InvalidOperationException("not a slide prompt: " + prompt[..Math.Min(80, prompt.Length)]);
        var request = new LlmRequest(system, prompt);
        return DeckScript.Slide(m.Groups[3].Value, m.Groups[4].Value, DeckScript.TopicWords(request), int.Parse(m.Groups[1].Value)).ToJsonString();
    }

    private static FakeBackend Writer() => new() { Answer = (system, prompt, _) => Said(SlideReply(system, prompt)) };

    private static DeviceDetectionService Pc(string simulatedId) => new(probe: () => SimulatedProfiles.Find(simulatedId)!.Raw);

    private static ForgeSession NewSession(TempDir temp, DeviceDetectionService devices, List<string>? log = null) => new(new SessionOptions
    {
        Devices = devices, DataDirectory = temp.At("data"), OutputRoot = temp.At("decks"), Log = log is null ? null : log.Add,
    });

    // ---- FoundryLlm ----

    private static void LlmFit(Check c)
    {
        var backend = new FakeBackend { Fit = new PromptFit(false, 9000, 9700, 8192, 1508) };
        var thrown = Catch<PromptTooLongException>(() => Chat(new FoundryLlm(Info(), backend), Plain()));
        c.That(thrown is { PromptTokens: 9000, ContextTokens: 8192, DeficitTokens: 1508 }, "a prompt the window cannot hold is refused with the runtime's own numbers", thrown?.Message ?? "no exception");
        c.That(backend.Options.Count == 0, "nothing is generated for a prompt that does not fit");

        backend = new FakeBackend { Fit = new PromptFit(false, 100, 500, 0, 0) };
        var reply = Chat(new FoundryLlm(Info(), backend), Plain());
        c.That(backend.Options.Count == 1 && reply.Text == "{}", "a runtime that does not report its window cannot call a prompt too long");

        backend = new FakeBackend { Fit = new PromptFit(true, 812, 1512, 32768, 0) };
        reply = Chat(new FoundryLlm(Info(), backend), Plain());
        c.That(reply.PromptTokens == 812 && reply.TokensMeasured && reply.CompletionTokens == 120 && reply.DecodeSeconds == 3.0 && reply.FirstTokenSeconds == 0.4 && reply.TotalSeconds == 3.4,
            "the runtime's own token counts and timings reach the harness", $"{reply}");

        backend = new FakeBackend { Fit = null };
        reply = Chat(new FoundryLlm(Info(), backend), new LlmRequest(new string('s', 70), new string('u', 70)));
        c.That(reply.PromptTokens == 40 && !reply.TokensMeasured, "with no count from the runtime the prompt is estimated at 3.5 characters per token and flagged as not measured", $"{reply.PromptTokens} {reply.TokensMeasured}");

        backend = new FakeBackend { Fit = new PromptFit(true, 0, 0, 32768, 0) };
        reply = Chat(new FoundryLlm(Info(), backend), Plain("x"));
        c.That(!reply.TokensMeasured && reply.PromptTokens > 0, "a zero count is not a measurement either");

        backend = new FakeBackend { Answer = (_, _, _) => Said("{}", measured: false) };
        reply = Chat(new FoundryLlm(Info(), backend), Plain());
        c.That(!reply.TokensMeasured, "output tokens the runtime only estimated are not a measurement");

        backend = new FakeBackend();
        var llm = new FoundryLlm(Info(), backend);
        Chat(llm, Plain());
        Chat(llm, Plain());
        c.That(backend.Preflights == 2, "every request is checked against the window first");

        backend = new FakeBackend { Answer = (_, _, _) => Said("{}") with { Cancelled = true } };
        reply = Chat(new FoundryLlm(Info(), backend), WithSchema());
        c.That(reply.Cancelled, "a cancelled generation says so");
    }

    private static void LlmGuidance(Check c)
    {
        var backend = new FakeBackend();
        var llm = new FoundryLlm(Info(), backend);
        var request = WithSchema();
        var reply = Chat(llm, request);
        var sent = backend.Options[0];
        c.That(sent.GuidanceType == "json_schema" && sent.GuidanceData == request.Schema!.ToJsonString(), "a schema goes to the runtime as constrained decoding", $"{sent.GuidanceType}");
        c.That(sent.MaxTokens == 700 && sent.Temperature == 0.4f, "token limit and temperature are passed along");
        c.That(reply.Guided && llm.GuidedReplies == 1 && !llm.GuidanceRejected && llm.Info.SupportsGuidance, "a reply under the schema counts as guided");

        backend = new FakeBackend();
        llm = new FoundryLlm(Info(), backend);
        reply = Chat(llm, Plain());
        c.That(backend.Options[0].GuidanceType is null && backend.Options[0].GuidanceData is null && !reply.Guided && llm.GuidedReplies == 0, "a request with no schema is not constrained");

        backend = new FakeBackend { Answer = (_, _, _) => Said("{}") with { Cancelled = true } };
        llm = new FoundryLlm(Info(), backend);
        Chat(llm, WithSchema());
        c.That(llm.GuidedReplies == 0, "a cancelled reply is not proof that constrained decoding works");

        backend = new FakeBackend();
        llm = new FoundryLlm(Info(), backend, guidanceKnownRejected: true);
        reply = Chat(llm, WithSchema());
        c.That(backend.Options.Count == 1 && backend.Options[0].GuidanceType is null && !reply.Guided && !llm.Info.SupportsGuidance && llm.GuidanceRejected,
            "a model known to refuse constrained decoding is not asked again");

        c.That(Info(guidance: false).SupportsGuidance == false && new FoundryLlm(Info(guidance: false), backend).Info.SupportsGuidance == false, "a model that never offered constrained decoding keeps saying so");
    }

    private static void LlmFallback(Check c)
    {
        var log = new List<string>();
        var backend = new FakeBackend
        {
            Answer = (_, _, options) => options.GuidanceType is not null ? throw new ModelOutputException("guidance_type is not supported here") : Said("{}"),
        };
        var llm = new FoundryLlm(Info(), backend, log.Add);
        var reply = Chat(llm, WithSchema());
        c.That(backend.Options.Count == 2 && backend.Options[0].GuidanceType == "json_schema" && backend.Options[1].GuidanceType is null, "a refused schema is retried without it");
        c.That(!reply.Guided && reply.Text == "{}" && llm.GuidanceRejected && !llm.Info.SupportsGuidance && llm.GuidedReplies == 0, "the retried reply is plain and the refusal is remembered");
        c.That(log.Any(l => l.Contains("Constrained decoding failed")), "the app is told why", string.Join(" | ", log));

        Chat(llm, WithSchema());
        c.That(backend.Options.Count == 3 && backend.Options[2].GuidanceType is null, "once refused, the schema is not sent again");

        backend = new FakeBackend { Answer = (_, _, _) => throw new ModelOutputException("the model is gone") };
        llm = new FoundryLlm(Info(), backend);
        var failure = Catch<ModelOutputException>(() => Chat(llm, WithSchema()));
        c.That(failure is not null && backend.Options.Count == 2, "a model that fails both ways surfaces the failure", failure?.Message ?? "no exception");
        c.That(!llm.GuidanceRejected && llm.Info.SupportsGuidance, "a model that is simply failing is not mistaken for one that cannot be constrained");

        backend = new FakeBackend { Answer = (_, _, options) => options.GuidanceType is not null ? throw new OperationCanceledException() : Said("{}") };
        llm = new FoundryLlm(Info(), backend);
        var cancelled = Catch<OperationCanceledException>(() => Chat(llm, WithSchema()));
        c.That(cancelled is not null && backend.Options.Count == 1 && !llm.GuidanceRejected, "cancelling is not a reason to retry or to blame the schema");

        backend = new FakeBackend();
        using var source = new CancellationTokenSource();
        source.Cancel();
        var early = Catch<OperationCanceledException>(() => new FoundryLlm(Info(), backend).ChatAsync(Plain(), null, source.Token).GetAwaiter().GetResult());
        c.That(early is not null && backend.Preflights == 0 && backend.Options.Count == 0, "a request that is already cancelled does no work");
    }

    private static void LlmInfoMapping(Check c)
    {
        var info = FoundryLlm.InfoFor(Qwen7());
        c.That(info.Model == "Qwen2.5 7B" && info.Lane == "npu" && info.Device == "NPU" && info.Runtime == "Foundry Local (QNN)" && info.ContextTokens == 32768
            && info.ModelClass == "M" && info.ParamsB == 7 && info.SupportsGuidance, "the harness is told the model that is really loaded", $"{info}");
        c.That(FoundryLlm.InfoFor(Qwen7(null)).ContextTokens == 0, "an unknown window is 0");
        c.That(FoundryLlm.InfoFor(Qwen7(long.MaxValue)).ContextTokens == int.MaxValue, "a huge window is clamped, not wrapped");
        c.That(FoundryLlm.InfoFor(Qwen7(-5)).ContextTokens == 0, "a nonsense window is 0");
        c.That(FoundryLlm.InfoFor(Qwen7(null), 131072).ContextTokens == 131072 && FoundryLlm.InfoFor(Qwen7(null), 0).ContextTokens == 0 && ModelFacts.From(Qwen7(null), 32768).ContextTokens == 32768,
            "a window read from the loaded build fills in what the catalog left blank");
    }

    /// <summary>Foundry Local's catalog often has no context window, but every build ships a genai_config.json that states it.</summary>
    private static void ModelWindows(Check c)
    {
        using var temp = new TempDir();
        string Write(string folder, string json)
        {
            string dir = temp.At(folder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "genai_config.json"), json);
            return dir;
        }
        c.That(ModelWindow.FromFolder(Write("a", """{"model":{"context_length":32768},"search":{"max_length":4096}}""")) == 32768, "the model's own window is read from genai_config.json");
        c.That(ModelWindow.FromFolder(Write("b", """{"model":{"type":"phi3"},"search":{"max_length":131072}}""")) == 131072, "a build that only states a search length uses it");
        Write("c\\v1", """{"model":{"context_length":65536}}""");
        c.That(ModelWindow.FromFolder(temp.At("c")) == 65536, "a config one folder down is found");
        c.That(ModelWindow.FromFolder(Write("d", """{"model":{"context_length":0},"search":{"max_length":-1}}""")) is null, "a window that is not a positive number is not a window");
        c.That(ModelWindow.FromFolder(Write("e", """{"model":{"context_length":"big"}}""")) is null, "a window that is not a number is ignored");
        Directory.CreateDirectory(temp.At("empty"));
        c.That(ModelWindow.FromFolder(Write("f", "{ not json")) is null && ModelWindow.FromFolder(temp.At("missing")) is null && ModelWindow.FromFolder(temp.At("empty")) is null,
            "a broken file, a missing folder and an empty folder all mean unknown");
    }

    // ---- StoryBuilder ----

    private static void StoryOverDevices(Check c)
    {
        int built = 0;
        var without = new List<string>();
        foreach (var sim in SimulatedProfiles.All)
        {
            var report = DeviceDetectionService.Analyze(sim.Raw, null, "simulated", sim.Id, sim.Label);
            var facts = PrimaryFacts(report);
            if (facts is null) { without.Add(sim.Id); continue; }
            built++;
            var story = StoryBuilder.Build(report.Profile, facts);
            if (Verbose) Console.WriteLine($"    {sim.Id,-30} {story.Name,-28} | {story.Silicon,-28} | {story.Engine,-15} | {facts.Title} ({facts.Lane}) | {story.Facts[0]}");

            string everything = string.Join("\n", story.Facts.Concat(story.TechnicalFacts).Concat(story.Keywords).Append(story.Name).Append(story.Silicon).Append(story.Engine).Append(story.Runtime));
            c.That(story.Name.Length > 0 && !story.Name.Contains(',') && !Regex.IsMatch(story.Name, @"\d(?:st|nd|rd|th) Edition", RegexOptions.IgnoreCase), $"{sim.Id}: the name reads as a person says it", story.Name);
            c.That(story.Engine.Length is > 0 and <= 15, $"{sim.Id}: the engine label fits a diagram", story.Engine);
            c.That(!Regex.IsMatch(everything, @"\bTOPS\b|Copilot", RegexOptions.IgnoreCase), $"{sim.Id}: the story never mentions TOPS or Copilot+");
            c.That(story.Keywords.Count >= 3 && story.Keywords.All(k => k.Length >= 3 && k == k.ToLowerInvariant()), $"{sim.Id}: keywords are lower case and long enough for the substring check", string.Join(",", story.Keywords));
            c.That(story.Facts.Count == 2 && story.Facts[0].StartsWith("Device: ", StringComparison.Ordinal) && Regex.IsMatch(story.Facts[0], @"\d+ GB") && story.Facts[1].Contains(facts.Title) && story.Facts[1].Contains(story.Engine),
                $"{sim.Id}: the two device facts name the PC, its memory, the model and the engine", string.Join(" | ", story.Facts));
            c.That(story.Lane == facts.Lane && story.Model == facts.Title && story.Noun.Length > 0, $"{sim.Id}: the story carries the model and lane that run");
            c.That(story.Runtime == (facts.Lane == "cpu" ? "Foundry Local on the CPU" : $"Foundry Local with the {facts.EpLabel} execution provider"), $"{sim.Id}: the runtime line matches the lane", story.Runtime);
            c.That(!story.TechnicalFacts.Any(f => f.Contains("tokens per second")), $"{sim.Id}: nothing measured means no speed claim");
        }
        c.That(built >= SimulatedProfiles.All.Count - 3, "nearly every simulated device has a model for the Forge", $"{built} of {SimulatedProfiles.All.Count}; none for: {string.Join(", ", without)}");
    }

    private static DeviceProfile Product(string maker, string product, bool surface = false) =>
        new() { Manufacturer = maker, Product = product, IsSurface = surface };

    private static void StoryNames(Check c)
    {
        var names = new (string Maker, string Product, bool Surface, string Expected)[]
        {
            ("Microsoft Corporation", "Surface Laptop, 7th Edition", true, "Surface Laptop 7"),
            ("Microsoft Corporation", "Surface Pro, 11th Edition", true, "Surface Pro 11"),
            ("Microsoft Corporation", "Surface Laptop Studio 2", true, "Surface Laptop Studio 2"),
            ("Microsoft Corporation", "Surface Laptop 4  ", true, "Surface Laptop 4"),
            ("ASUSTeK COMPUTER INC.", "ROG Zephyrus G14 GA403", false, "ASUS ROG Zephyrus G14 GA403"),
            ("Dell Inc.", "XPS 13 9345", false, "Dell XPS 13 9345"),
            ("HP", "HP EliteBook Ultra G1q", false, "HP EliteBook Ultra G1q"),
            ("LENOVO", "21N2", false, "Lenovo 21N2"),
            ("To Be Filled By O.E.M.", "To Be Filled By O.E.M.", false, "this PC"),
            ("System manufacturer", "System Product Name", false, "this PC"),
            ("QEMU", "Standard PC (Q35 + ICH9, 2009)", false, "this PC"),
            ("Microsoft Corporation", "Virtual Machine", false, "this PC"),
            ("Dell Inc.", "", false, "this PC"),
            ("", "Mystery Box 3", false, "Mystery Box 3"),
        };
        foreach (var (maker, product, surface, expected) in names)
        {
            string got = StoryBuilder.Name(Product(maker, product, surface));
            c.That(got == expected, $"name of '{product}' by '{maker}'", $"expected '{expected}', got '{got}'");
        }

        var makers = new (string Raw, string Expected)[]
        {
            ("Dell Inc.", "Dell"), ("HP Inc.", "HP"), ("Hewlett-Packard", "HP"), ("LENOVO", "Lenovo"), ("ASUSTeK COMPUTER INC.", "ASUS"),
            ("Micro-Star International Co., Ltd.", "MSI"), ("LG Electronics", "LG"), ("Samsung Electronics Co., Ltd.", "Samsung"), ("Acer", "Acer"),
            ("Microsoft Corporation", ""), ("System manufacturer", ""), ("To Be Filled By O.E.M.", ""), ("", ""),
        };
        foreach (var (raw, expected) in makers)
            c.That(StoryBuilder.Maker(raw) == expected, $"maker '{raw}'", $"expected '{expected}', got '{StoryBuilder.Maker(raw)}'");

        var nouns = new (string Name, string Expected)[]
        {
            ("Surface Laptop 7", "Laptop"), ("Surface Laptop Studio 2", "Laptop"), ("Surface Book 3", "Laptop"), ("Surface Pro 11", "Surface Pro"),
            ("Surface Go 3", "Surface Go"), ("Dell XPS 13 9345", "PC"), ("this PC", "PC"),
        };
        foreach (var (name, expected) in nouns)
            c.That(StoryBuilder.Noun(name) == expected, $"noun for '{name}'", StoryBuilder.Noun(name));
    }

    private static DeviceProfile WithCpu(string cpu) => new() { Cpu = new CpuInfo(cpu, "", "", "", "", 0, false) };

    private static void StorySilicon(Check c)
    {
        var cpus = new (string Raw, string Expected)[]
        {
            ("Snapdragon(R) X Plus - X1P64100 - Qualcomm(R) Oryon(TM) CPU", "Snapdragon X Plus"),
            ("Snapdragon(R) X Elite - X1E80100 - Qualcomm(R) Oryon(TM) CPU", "Snapdragon X Elite"),
            ("Snapdragon(R) X2 Elite Extreme - X2E94100 - Qualcomm(R) Oryon(TM) CPU", "Snapdragon X2 Elite Extreme"),
            ("Snapdragon(R) X2 Plus - X2P42100", "Snapdragon X2 Plus"),
            ("Snapdragon(R) X - X1-26-100 - Qualcomm(R) Oryon(TM) CPU", "Snapdragon X"),
            ("Intel(R) Core(TM) Ultra 7 268V", "Intel Core Ultra 7 268V"),
            ("13th Gen Intel(R) Core(TM) i7-1355U", "Intel Core i7-1355U"),
            ("Intel(R) Pentium(R) Gold 6500Y CPU @ 1.10GHz", "Intel Pentium Gold 6500Y"),
            ("AMD Ryzen 7 4980U with Radeon Graphics", "AMD Ryzen 7 4980U"),
            ("NVIDIA RTX Spark N1X (6144-core GPU, 20-core CPU)", "NVIDIA RTX Spark"),
            ("Microsoft SQ3", "Microsoft SQ3"),
            ("", ""),
        };
        foreach (var (raw, expected) in cpus)
        {
            string got = StoryBuilder.Silicon(WithCpu(raw));
            c.That(got == expected, $"silicon of '{raw}'", $"expected '{expected}', got '{got}'");
        }
    }

    private static void StoryFacts(Check c)
    {
        var snapdragon = Report("snapdragon-x-plus-16").Profile;
        var facts = new ModelFacts("Qwen2.5 7B", "npu", "NPU", "QNN", "M", 7, 4.8, 32768);
        var plain = StoryBuilder.Build(snapdragon, facts);
        c.That(plain.Facts[0] == "Device: Surface Laptop 7 with Snapdragon X Plus and 16 GB installed memory.", "the device sentence", plain.Facts[0]);
        c.That(plain.Facts[1] == "The deck is drafted by Qwen2.5 7B on the Snapdragon NPU.", "the model sentence", plain.Facts[1]);
        c.That(plain.Name == "Surface Laptop 7" && plain.Silicon == "Snapdragon X Plus" && plain.Engine == "Snapdragon NPU" && plain.Noun == "Laptop" && !plain.UnifiedMemory,
            "the parts of the story", $"{plain.Name} | {plain.Silicon} | {plain.Engine} | {plain.Noun}");
        c.That(plain.Runtime == "Foundry Local with the QNN execution provider", "runtime", plain.Runtime);
        c.That(plain.TechnicalFacts.Any(f => f.Contains("4.8 GB model file") && f.Contains("32,768-token context window")), "the model file and window", string.Join(" | ", plain.TechnicalFacts));
        c.That(!plain.TechnicalFacts.Any(f => f.Contains("tokens per second") || f.Contains("Constrained")) && plain.MeasuredTokensPerSecond == 0, "nothing measured, nothing claimed");

        var measured = StoryBuilder.Build(snapdragon, facts, new StoryMeasurements(41.7, true));
        c.That(measured.TechnicalFacts.Any(f => f.Contains("decodes at about 42 tokens per second on the Snapdragon NPU")), "a measured speed is quoted", string.Join(" | ", measured.TechnicalFacts));
        c.That(measured.TechnicalFacts.Any(f => f.StartsWith("Constrained decoding", StringComparison.Ordinal)) && measured.MeasuredTokensPerSecond == 41.7, "verified constrained decoding is quoted");
        var slow = StoryBuilder.Build(snapdragon, facts, new StoryMeasurements(0.4, false));
        c.That(!slow.TechnicalFacts.Any(f => f.Contains("tokens per second")), "a speed under one token per second is not worth a sentence");

        var unknownWindow = StoryBuilder.Build(snapdragon, facts with { ContextTokens = 0, FileGb = 0 });
        c.That(!unknownWindow.TechnicalFacts.Any(f => f.Contains("model file") || f.Contains("context window")), "an unknown size and window are left out");

        var ultra = Report("nvidia-n1x-128").Profile;
        var gpuFacts = new ModelFacts("Qwen2.5-Coder 14B", "gpu", "GPU", "CUDA", "L", 14, 9.0, 32768);
        var unified = StoryBuilder.Build(ultra, gpuFacts);
        c.That(unified.UnifiedMemory && unified.Silicon == "NVIDIA RTX Spark" && unified.Engine == "RTX Spark GPU", "the Surface Laptop Ultra story", $"{unified.Name} | {unified.Silicon} | {unified.Engine}");
        c.That(unified.Facts[0].Contains("128 GB of unified memory") && unified.TechnicalFacts.Any(f => f.Contains("share one 128 GB memory pool")), "unified memory is said once as unified", $"{unified.Facts[0]} | {string.Join(" | ", unified.TechnicalFacts)}");
        c.That(unified.TechnicalFacts.Contains("The memory budget for AI models on the RTX Spark GPU is up to 60.5 GB.") && plain.TechnicalFacts.Contains("The memory budget for AI models on the Snapdragon NPU is up to 7 GB."),
            "the memory an AI model may use is the lane's own budget", $"{string.Join(" | ", unified.TechnicalFacts)} || {string.Join(" | ", plain.TechnicalFacts)}");
        var noNpu = StoryBuilder.Build(ultra, gpuFacts with { Lane = "npu", Device = "NPU" });
        c.That(!noNpu.TechnicalFacts.Any(f => f.Contains("memory budget")), "a lane this PC does not have gets no budget sentence", string.Join(" | ", noNpu.TechnicalFacts));
        c.That(unified.TechnicalFacts.Contains("Windows shows 64 GB of the 128 GB installed as visible memory.") && plain.TechnicalFacts.Contains("Windows shows 15.6 GB of the 16 GB installed as visible memory."),
            "the memory Windows shows is quoted next to the memory installed, so a slide never has to guess it", $"{string.Join(" | ", unified.TechnicalFacts)} || {string.Join(" | ", plain.TechnicalFacts)}");

        var cpuFacts = new ModelFacts("Phi-4 mini", "cpu", "CPU", "CPU", "S", 3.8, 3.7, 4096);
        var cpuStory = StoryBuilder.Build(snapdragon, cpuFacts);
        c.That(cpuStory.Engine == "CPU" && cpuStory.Runtime == "Foundry Local on the CPU" && cpuStory.Keywords.Contains("cpu"), "a CPU run says CPU everywhere", $"{cpuStory.Engine} | {cpuStory.Runtime} | {string.Join(",", cpuStory.Keywords)}");

        var vm = StoryBuilder.Build(WithCpu("Intel(R) Xeon(R) CPU") with { Product = "Virtual Machine", Memory = new MemoryInfo(8, 8, 8) }, cpuFacts);
        c.That(vm.Name == "this PC" && !vm.Keywords.Contains("this pc") && vm.Facts[0].StartsWith("Device: this PC", StringComparison.Ordinal), "an unnamed PC is 'this PC' and not a keyword", $"{vm.Facts[0]} | {string.Join(",", vm.Keywords)}");
        c.That(!vm.TechnicalFacts.Any(f => f.Contains("visible memory")), "when Windows shows all the memory there is nothing to say about it", string.Join(" | ", vm.TechnicalFacts));
    }

    // ---- Power meter arithmetic ----

    private static void Power(Check c)
    {
        var good = new (string Line, double Watts)[] { ("12.5", 12.5), (" 7 ", 7), ("0", 0), ("1999.9", 1999.9), ("3.25\r", 3.25) };
        foreach (var (line, watts) in good)
            c.That(PowerMath.TryParseWatts(line, out double got) && got == watts, $"'{line.Trim()}' is a reading", $"{got}");
        string?[] bad = ["[N/A]", "[Not Supported]", "N/A", "n/a", "", "   ", null, "abc", "2000", "2500", "-3", "NaN", "Infinity", "12 W"];
        foreach (string? line in bad)
            c.That(!PowerMath.TryParseWatts(line, out _), $"'{line}' is not a reading");

        const string basis = "test basis";
        var flat = PowerMath.Integrate([new(0.5, 10), new(1.5, 10)], 2, 10, basis);
        c.That(flat.Measured && Math.Abs(flat.Wh - 40.0 / 3600) < 1e-9 && flat.Basis == basis, "a steady 10 W for 2 s plus a 10 W platform is 40 J", $"{flat.Wh * 3600} J");

        var ramp = PowerMath.Integrate([new(0.5, 10), new(1.5, 20)], 2, 10, basis);
        c.That(Math.Abs(ramp.Wh * 3600 - 50.0) < 1e-9, "the first reading is held back to the start, the last forward to the end, the rest is a trapezoid", $"{ramp.Wh * 3600} J");

        var shuffled = PowerMath.Integrate([new(1.5, 20), new(0.5, 10)], 2, 10, basis);
        c.That(Math.Abs(shuffled.Wh - ramp.Wh) < 1e-12, "readings are put in time order first");

        var outside = PowerMath.Integrate([new(0.5, 10), new(1.5, 20), new(9, 500), new(-1, 500)], 2, 10, basis);
        c.That(Math.Abs(outside.Wh - ramp.Wh) < 1e-12, "readings from outside the run are ignored");

        c.That(!PowerMath.Integrate([new(1, 10)], 2, 10, basis).Measured, "one reading is not a measurement");
        c.That(!PowerMath.Integrate([], 2, 10, basis).Measured, "no readings is not a measurement");
        c.That(!PowerMath.Integrate([new(0.5, 10), new(1.5, 20)], 0, 10, basis).Measured, "a run of no length is not a measurement");
        var none = PowerMath.Integrate([], 2, 10, basis);
        c.That(none.Basis.Contains("fewer than two readings"), "the reason says what was missing", none.Basis);

        c.That(NvidiaSmiPowerMeter.Basis == "GPU board power (nvidia-smi, 250 ms) + 10 W platform allowance", "the basis names the sensor and the allowance", NvidiaSmiPowerMeter.Basis);
    }

    // ---- RunPolicy ----

    private static ModelFacts Model(string cls, int context, string title = "Test 7B") => new(title, "npu", "NPU", "QNN", cls, 7, 4.8, context);

    private static SpeedEntry Speed(double tokensPerSecond, GuidanceState guidance = GuidanceState.Unknown) =>
        new() { Variant = "v", Model = "Test 7B", DecodeTokensPerSecond = tokensPerSecond, Guidance = guidance };

    private static void Policy(Check c)
    {
        var standard = RunPolicy.Decide(Model("M", 32768), null);
        c.That(standard is { Mode: HarnessMode.Guided, RepairPasses: 2, RepairsPerPass: 6, UseGuidance: true }, "the default is slide by slide with two repair passes and constrained decoding", $"{standard}");
        c.That(standard.Why.Count >= 3 && standard.Why.Any(w => w.StartsWith("Slide by slide", StringComparison.Ordinal)), "every setting has its reason", string.Join(" | ", standard.Why));

        var arcMedium = RunPolicy.Decide(Model("M", 32768), null, new RunRequest(Mode: HarnessMode.Arc));
        c.That(arcMedium.Mode == HarnessMode.Guided && arcMedium.Why.Any(w => w.Contains("needs a large model") && w.Contains("medium-sized")), "a medium model cannot write the whole story at once", string.Join(" | ", arcMedium.Why));
        c.That(RunPolicy.Decide(Model("S", 32768), null, new RunRequest(Mode: HarnessMode.OneShot)).Mode == HarnessMode.Guided, "nor can a small one write the whole deck");

        var arcLarge = RunPolicy.Decide(Model("L", 32768), null, new RunRequest(Mode: HarnessMode.Arc));
        c.That(arcLarge.Mode == HarnessMode.Arc && arcLarge.Why.Any(w => w.Contains("as asked")), "a large model with room can", string.Join(" | ", arcLarge.Why));
        c.That(RunPolicy.Decide(Model("XL", 16000), null, new RunRequest(Mode: HarnessMode.Arc)).Mode == HarnessMode.Arc, "16,000 tokens is enough for the whole story");
        var arcTight = RunPolicy.Decide(Model("L", 15999), null, new RunRequest(Mode: HarnessMode.Arc));
        c.That(arcTight.Mode == HarnessMode.Guided && arcTight.Why.Any(w => w.Contains("15,999") && w.Contains("16,000")), "15,999 is not", string.Join(" | ", arcTight.Why));
        c.That(RunPolicy.Decide(Model("L", 12000), null, new RunRequest(Mode: HarnessMode.OneShot)).Mode == HarnessMode.OneShot, "12,000 tokens is enough for the whole deck in one reply");
        c.That(RunPolicy.Decide(Model("L", 12000), null, new RunRequest(Mode: HarnessMode.Arc)).Mode == HarnessMode.Guided, "but not for the whole story");
        var unknown = RunPolicy.Decide(Model("XL", 0), null, new RunRequest(Mode: HarnessMode.OneShot));
        c.That(unknown.Mode == HarnessMode.Guided && unknown.Why.Any(w => w.Contains("not known")), "an unknown window is never trusted with a long prompt", string.Join(" | ", unknown.Why));
        c.That(RunPolicy.Decide(Model("L", 32768), null, new RunRequest(Mode: HarnessMode.Guided)).Mode == HarnessMode.Guided, "slide by slide can always be asked for");

        var slow = RunPolicy.Decide(Model("M", 32768), Speed(8));
        c.That(slow is { RepairPasses: 1, RepairsPerPass: 3 } && slow.Why.Any(w => w.Contains("about 8 tokens per second")), "a slow model gets a smaller repair budget", string.Join(" | ", slow.Why));
        var slowButAsked = RunPolicy.Decide(Model("M", 32768), Speed(8), new RunRequest(RepairPasses: 3));
        c.That(slowButAsked is { RepairPasses: 3, RepairsPerPass: 3 }, "unless the person asked for more", $"{slowButAsked}");
        c.That(RunPolicy.Decide(Model("M", 32768), Speed(12)) is { RepairPasses: 2, RepairsPerPass: 6 }, "12 tokens per second is not slow");
        c.That(RunPolicy.Decide(Model("M", 32768), Speed(0)) is { RepairPasses: 2, RepairsPerPass: 6 }, "no speed measured is not slow");
        c.That(RunPolicy.Decide(Model("M", 32768), null, new RunRequest(RepairPasses: 7)).RepairPasses == 3, "repair passes are capped at 3");
        var noRepairs = RunPolicy.Decide(Model("M", 32768), null, new RunRequest(RepairPasses: -2));
        c.That(noRepairs.RepairPasses == 0 && noRepairs.Why.Contains("No repair passes."), "and cannot go below none", string.Join(" | ", noRepairs.Why));

        c.That(RunPolicy.Decide(Model("M", 32768), Speed(40, GuidanceState.Rejected)).UseGuidance == false, "a model that refused constrained decoding is not asked again");
        c.That(RunPolicy.Decide(Model("M", 32768), Speed(40, GuidanceState.Rejected), new RunRequest(UseGuidance: true)).UseGuidance, "unless the person asks to try again");
        c.That(RunPolicy.Decide(Model("M", 32768), Speed(40, GuidanceState.Verified)).UseGuidance, "a model that follows the schema keeps using it");
        var off = RunPolicy.Decide(Model("M", 32768), Speed(40, GuidanceState.Verified), new RunRequest(UseGuidance: false));
        c.That(!off.UseGuidance && off.Why.Any(w => w.Contains("off, as asked")), "and it can be turned off", string.Join(" | ", off.Why));

        var options = RunPolicy.Decide(Model("L", 32768), Speed(8, GuidanceState.Rejected), new RunRequest(Mode: HarnessMode.Arc)).ToOptions(Theme.SurfaceUltra);
        c.That(options is { Mode: HarnessMode.Arc, MaxRepairPasses: 1, MaxRepairsPerPass: 3, UseGuidance: false } && options.Theme == Theme.SurfaceUltra, "the settings become harness options", $"{options}");
    }

    // ---- SpeedLog ----

    private static void SpeedMath(Check c)
    {
        var log = new SpeedLog();
        c.That(log.Entry("v") is null && log.Estimate("v", 10) is null && log.Runs.Count == 0, "an empty log knows nothing");

        var first = log.Record(new SpeedEntry { Variant = "v", Model = "M", DecodeTokensPerSecond = 40, FirstTokenSeconds = 1, SlideTokens = 120, Guidance = GuidanceState.Verified, MeasuredAt = DateTimeOffset.UtcNow });
        c.That(first.Samples == 1 && first.DecodeTokensPerSecond == 40, "the first measurement is kept as it is");
        var second = log.Record(new SpeedEntry { Variant = "v", DecodeTokensPerSecond = 20, FirstTokenSeconds = 3, SlideTokens = 60 });
        c.That(second.Samples == 2 && second.DecodeTokensPerSecond == 30 && second.FirstTokenSeconds == 2 && second.SlideTokens == 90, "a second one is averaged in", $"{second.DecodeTokensPerSecond} {second.FirstTokenSeconds} {second.SlideTokens}");
        var third = log.Record(new SpeedEntry { Variant = "v", DecodeTokensPerSecond = 60 });
        c.That(third.Samples == 3 && Math.Abs(third.DecodeTokensPerSecond - 40) < 1e-9, "weighted by how many came before", $"{third.DecodeTokensPerSecond}");
        c.That(third.SlideTokens == 90 && Math.Abs(third.FirstTokenSeconds - 2) < 1e-9, "a measurement with no value for a field keeps the old one", $"{third.SlideTokens} {third.FirstTokenSeconds}");
        c.That(third.Guidance == GuidanceState.Verified, "an unknown answer about constrained decoding never erases a known one");
        c.That(log.Record(new SpeedEntry { Variant = "v", Guidance = GuidanceState.Rejected }).Guidance == GuidanceState.Rejected, "a new known answer replaces the old one");

        for (int i = 0; i < 10; i++) log.Record(new SpeedEntry { Variant = "w", DecodeTokensPerSecond = 100 });
        var heavy = log.Record(new SpeedEntry { Variant = "w", DecodeTokensPerSecond = 0 + 10 });
        c.That(Math.Abs(heavy.DecodeTokensPerSecond - 82) < 1e-9, "old samples weigh at most four, so a PC that changes is noticed", $"{heavy.DecodeTokensPerSecond}");

        c.That(log.Estimate("nothing", 10) is null, "no data, no estimate");
        var one = new SpeedLog();
        one.Record(new SpeedEntry { Variant = "v", DecodeTokensPerSecond = 40, FirstTokenSeconds = 1, SlideTokens = 120 });
        var fromSlide = one.Estimate("v", 10)!;
        c.That(Math.Abs(fromSlide.Seconds - (1 + 120 / 40.0) * SpeedLog.RepairAllowance * 10) < 1e-9 && fromSlide.Basis == "estimated from one measured slide", "one measured slide gives an estimate with a repair allowance", $"{fromSlide.Seconds} {fromSlide.Basis}");
        c.That(one.Estimate("v", 0) is null && one.Estimate("v", -3) is null, "a deck of no slides has no estimate");
        one.Record(new SpeedEntry { Variant = "noTokens", DecodeTokensPerSecond = 40 });
        c.That(one.Estimate("noTokens", 10) is null, "a speed with no slide length is not enough");

        var now = DateTimeOffset.UtcNow;
        one.RecordRun(new DeckRunRecord("v", 10, 100, "Guided", now));
        one.RecordRun(new DeckRunRecord("v", 10, 200, "Guided", now));
        var fromRuns = one.Estimate("v", 12)!;
        c.That(Math.Abs(fromRuns.Seconds - 15 * 12) < 1e-9 && fromRuns.Basis == "median of 2 earlier decks on this PC", "earlier decks beat a one-slide estimate", $"{fromRuns.Seconds} {fromRuns.Basis}");
        one.RecordRun(new DeckRunRecord("v", 10, 150, "Guided", now));
        c.That(one.Estimate("v", 10)!.Seconds == 150 && one.Estimate("v", 10)!.Basis == "median of 3 earlier decks on this PC", "the median of three");
        one.RecordRun(new DeckRunRecord("v", 0, 50, "Guided", now));
        one.RecordRun(new DeckRunRecord("v", 5, 0, "Guided", now));
        c.That(one.Runs.Count == 3, "a run with no slides or no time is not recorded");
        one.RecordRun(new DeckRunRecord("other", 10, 10, "Guided", now));
        c.That(one.Estimate("v", 10)!.Seconds == 150, "runs of another model do not count");

        var many = new SpeedLog();
        for (int i = 1; i <= SpeedLog.KeepRuns + 15; i++) many.RecordRun(new DeckRunRecord("v", 1, i, "Guided", now));
        c.That(many.Runs.Count == SpeedLog.KeepRuns && many.Runs[0].Seconds == 16 && many.Runs[^1].Seconds == SpeedLog.KeepRuns + 15, "only the latest runs are kept", $"{many.Runs.Count} from {many.Runs[0].Seconds}");
        for (int i = 0; i < 7; i++) many.RecordRun(new DeckRunRecord("v", 1, 1000, "Guided", now));
        c.That(many.Estimate("v", 1)!.Seconds == 1000, "the estimate uses the five latest of this model", $"{many.Estimate("v", 1)!.Seconds}");
    }

    private static void SpeedFile(Check c)
    {
        using var temp = new TempDir();
        string path = temp.At("nested\\speed.json");

        var missing = SpeedLog.Load(path);
        c.That(missing.Problem.Length == 0 && missing.Runs.Count == 0, "no file is just an empty log");

        var log = SpeedLog.Load(path);
        var stamp = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        log.Record(new SpeedEntry { Variant = "qwen:1", Model = "Qwen2.5 7B", Lane = "npu", Ep = "QNN", DecodeTokensPerSecond = 33.5, PrefillTokensPerSecond = 900, FirstTokenSeconds = 0.8, SlideTokens = 140, Guidance = GuidanceState.Verified, MeasuredAt = stamp, Samples = 3 });
        log.RecordRun(new DeckRunRecord("qwen:1", 11, 220.5, "Guided", stamp));
        c.That(log.Save() && File.Exists(path) && !File.Exists(path + ".tmp") && log.Problem.Length == 0, "saving creates the folder and leaves no temporary file");

        string json = File.ReadAllText(path);
        c.That(json.Contains("\"decodeTokensPerSecond\": 33.5") && json.Contains("\"guidance\": \"verified\"") && json.Contains("\"variants\""), "the file is readable: camelCase with named states", json[..Math.Min(200, json.Length)]);

        var back = SpeedLog.Load(path);
        var entry = back.Entry("qwen:1");
        c.That(entry is { Model: "Qwen2.5 7B", Lane: "npu", Ep: "QNN", DecodeTokensPerSecond: 33.5, PrefillTokensPerSecond: 900, SlideTokens: 140, Samples: 3, Guidance: GuidanceState.Verified, Variant: "qwen:1" } && entry.MeasuredAt == stamp,
            "an entry survives a round trip", $"{entry}");
        c.That(back.Runs.Count == 1 && back.Runs[0] is { Slides: 11, Seconds: 220.5, Mode: "Guided" }, "so does a run");

        File.WriteAllText(path, "{ this is not json");
        var broken = SpeedLog.Load(path);
        c.That(broken.Problem.Contains("could not be read") && broken.Entry("qwen:1") is null && broken.Runs.Count == 0, "a damaged file is an empty log with a reason", broken.Problem);
        c.That(broken.Save() && SpeedLog.Load(path).Problem.Length == 0, "and the next save replaces it");

        File.WriteAllText(path, "[1, 2, 3]");
        c.That(SpeedLog.Load(path).Problem.Length > 0, "a file of the wrong shape is damaged too");

        File.WriteAllText(path, """{"version":1,"variants":{"":{"model":"nameless"},"ok":{"model":"fine","decodeTokensPerSecond":5}},"runs":[{"variant":"ok","slides":0,"seconds":10,"mode":"Guided","when":"2026-10-30T12:00:00+00:00"},{"variant":"ok","slides":4,"seconds":10,"mode":"Guided","when":"2026-10-30T12:00:00+00:00"}]}""");
        var tidy = SpeedLog.Load(path);
        c.That(tidy.Entry("")?.Model is null && tidy.Entry("ok") is { Variant: "ok", Model: "fine" } && tidy.Runs.Count == 1, "entries without a name and runs without slides are dropped on load", $"{tidy.Problem}");

        var nowhere = new SpeedLog();
        c.That(!nowhere.Save() && nowhere.Problem.Length == 0, "a log with no file saves nothing and complains about nothing");

        string blocker = temp.At("blocker");
        File.WriteAllText(blocker, "a file where a folder should be");
        var cannot = new SpeedLog(Path.Combine(blocker, "speed.json"));
        c.That(!cannot.Save() && cannot.Problem.Contains("could not be saved"), "a log that cannot be saved says so", cannot.Problem);
    }

    // ---- Calibrator ----

    private static ScriptedLlm CalibrationModel(DeviceStory story, Func<LlmRequest, int, LlmReply?>? special = null, bool guidance = true, int context = 32768)
    {
        var adapted = BriefAdapter.Adapt(BundledBriefs.Find("zava_technical")!, story);
        var arc = Arcs.For(adapted, Arcs.Reserved(adapted));
        return new ScriptedLlm(ScriptedLlm.Model(context: context, guidance: guidance), (request, n) =>
            special?.Invoke(request, n) ?? ScriptedLlm.Say(DeckScript.Reply(request, arc), guided: request.Schema is not null));
    }

    private static void Calibrate(Check c)
    {
        var story = StoryBuilder.Build(Report("snapdragon-x-plus-16").Profile, new ModelFacts("Qwen2.5 7B", "npu", "NPU", "QNN", "M", 7, 4.8, 32768));

        var llm = CalibrationModel(story);
        var result = Calibrator.RunAsync(llm, story).GetAwaiter().GetResult();
        c.That(llm.Requests.Count == 1 && llm.Requests[0].Schema is not null && llm.Requests[0].Purpose.StartsWith("slide ", StringComparison.Ordinal), "calibration writes one real slide with the schema", $"{llm.Requests.Count} request(s)");
        c.That(result is { Completed: true, SlideParsed: true, Guidance: GuidanceState.Verified }, "a model that writes a good slide under the schema is verified", result.Summary);
        c.That(Math.Abs(result.DecodeTokensPerSecond - 40) < 1e-9 && Math.Abs(result.PrefillTokensPerSecond - 2250) < 1e-9 && result.FirstTokenSeconds == 0.4 && result.SlideTokens == 120 && result.PromptTokens == 900,
            "speeds come from the model's own counts", $"{result.DecodeTokensPerSecond} {result.PrefillTokensPerSecond}");
        c.That(result.Summary.Contains("40 tokens per second") && result.Summary.Contains("constrained decoding verified"), "the summary says what was found", result.Summary);

        var facts = new ModelFacts("Qwen2.5 7B", "npu", "NPU", "QNN", "M", 7, 4.8, 32768);
        var entry = result.ToEntry(facts, "qwen:1");
        c.That(entry is { Variant: "qwen:1", Model: "Qwen2.5 7B", Lane: "npu", Ep: "QNN", Guidance: GuidanceState.Verified, SlideTokens: 120, Samples: 1 } && (DateTimeOffset.UtcNow - entry.MeasuredAt).TotalMinutes < 1, "the result becomes a log entry", $"{entry}");

        llm = CalibrationModel(story, (request, _) => ScriptedLlm.Say("{\"title\":\"x\"}", guided: false));
        result = Calibrator.RunAsync(llm, story).GetAwaiter().GetResult();
        c.That(result.Completed && result.Guidance == GuidanceState.Rejected, "a reply that did not follow the schema means constrained decoding was refused", result.Summary);
        c.That(result.Summary.Contains("refused"), "and the summary says so", result.Summary);

        llm = CalibrationModel(story, guidance: false);
        result = Calibrator.RunAsync(llm, story).GetAwaiter().GetResult();
        c.That(llm.Requests[0].Schema is null && result is { Completed: true, Guidance: GuidanceState.Unknown }, "a model without constrained decoding is not asked for it and not blamed for it", result.Summary);

        llm = CalibrationModel(story, (_, _) => ScriptedLlm.Say("this is not json", guided: true));
        result = Calibrator.RunAsync(llm, story).GetAwaiter().GetResult();
        c.That(result is { Completed: true, SlideParsed: false, Guidance: GuidanceState.Unknown } && result.Summary.Contains("did not parse"), "a guided reply that does not parse proves nothing either way", result.Summary);

        llm = CalibrationModel(story, (_, _) => new LlmReply(DeckScript.Slide("kpis", "x", [], 2).ToJsonString(), 0.4, 3.4, 900, 120, 3.0, true, TokensMeasured: false));
        result = Calibrator.RunAsync(llm, story).GetAwaiter().GetResult();
        c.That(result.Completed && result.DecodeTokensPerSecond == 0 && result.SlideTokens == 0 && result.PrefillTokensPerSecond == 0 && result.Summary.Contains("did not report token counts"),
            "estimated token counts never become a speed", result.Summary);

        llm = CalibrationModel(story, (_, _) => throw new ModelOutputException("the model fell over"));
        result = Calibrator.RunAsync(llm, story).GetAwaiter().GetResult();
        c.That(!result.Completed && result.Summary.Contains("the model fell over") && result.DecodeTokensPerSecond == 0, "a model that fails is a failed calibration, not a crash", result.Summary);

        llm = CalibrationModel(story, (_, n) => n < 3 ? throw new PromptTooLongException(5000, 4096, 1000) : null);
        result = Calibrator.RunAsync(llm, story).GetAwaiter().GetResult();
        c.That(result.Completed && llm.Requests.Count == 4 && llm.Requests[3].User.Length < llm.Requests[0].User.Length, "a prompt that is too long is retried at a higher compaction level until it fits", $"{llm.Requests.Count} requests");

        llm = CalibrationModel(story, (_, _) => throw new PromptTooLongException(5000, 4096, 1000));
        result = Calibrator.RunAsync(llm, story).GetAwaiter().GetResult();
        c.That(!result.Completed && llm.Requests.Count == 4, "and the smallest prompt is the last one tried", $"{llm.Requests.Count} requests");

        var roomy = CalibrationModel(story);
        Calibrator.RunAsync(roomy, story).GetAwaiter().GetResult();
        var tight = CalibrationModel(story, context: 1700);
        Calibrator.RunAsync(tight, story).GetAwaiter().GetResult();
        c.That(tight.Requests[0].User.Length < roomy.Requests[0].User.Length, "a small window gets a shorter prompt from the start", $"{tight.Requests[0].User.Length} vs {roomy.Requests[0].User.Length}");

        using var source = new CancellationTokenSource();
        source.Cancel();
        var cancelled = Catch<OperationCanceledException>(() => Calibrator.RunAsync(CalibrationModel(story), story, source.Token).GetAwaiter().GetResult());
        c.That(cancelled is not null, "calibration can be cancelled");
        c.That(CalibrationResult.Failed("nope") is { Completed: false, Summary: "nope" }, "a failed result carries the reason");
    }

    // ---- ForgeOutput ----

    private static void Output(Check c)
    {
        using var temp = new TempDir();
        var brief = BundledBriefs.Find("zava_technical")!;
        var spec = Fixtures.SampleSpec();
        var deck = DeckBuilder.Build(spec, Theme.Surface);
        var log = new List<string>();
        string slug = BriefDrafter.Slug(brief.Id);

        var files = ForgeOutput.Write(temp.At("decks"), brief, deck, spec, (path, seconds) => new JsonObject { ["pptx"] = Path.GetFileName(path), ["save_seconds"] = seconds }, previews: true, log.Add);
        c.That(Path.GetFileName(files.Folder) == slug && files.Pptx == Path.Combine(files.Folder, slug + ".pptx") && !files.Renamed, "a deck goes to a folder named for its brief", files.Pptx);
        c.That(File.Exists(files.Pptx) && File.Exists(files.SpecJson) && File.Exists(files.BriefJson) && File.Exists(files.MetricsJson), "the deck, its spec, its brief and its numbers are saved together");
        c.That(PptxPackage.Validate(File.ReadAllBytes(files.Pptx)).Count == 0, "the deck is a valid package");
        c.That(files.Previews.Count == deck.Slides.Count && files.Previews.All(File.Exists) && files.ContactSheet is not null && File.Exists(files.ContactSheet), "every slide has a preview and there is a contact sheet", $"{files.Previews.Count} of {deck.Slides.Count}");
        c.That(files.Previews[0].EndsWith("slide-01.png", StringComparison.Ordinal) && Directory.Exists(files.PreviewFolder) && files.PreviewFolder.EndsWith(slug + "-preview", StringComparison.Ordinal), "previews are numbered in a folder beside the deck");
        c.That(Brief.Load(files.BriefJson).Id == brief.Id && JsonNode.Parse(File.ReadAllText(files.SpecJson))!["slides"] is JsonArray, "the sidecar files read back");
        c.That(files.Metrics?["pptx"]?.GetValue<string>() == slug + ".pptx" && JsonNode.Parse(File.ReadAllText(files.MetricsJson))!["pptx"]!.GetValue<string>() == slug + ".pptx", "the numbers are written with the final file name");
        c.That(!Directory.GetFiles(files.Folder, "*.tmp", SearchOption.AllDirectories).Any(), "no temporary files are left");

        var noPreviews = ForgeOutput.Write(temp.At("decks2"), brief, deck, spec, null, previews: false);
        c.That(noPreviews.Previews.Count == 0 && noPreviews.ContactSheet is null && !Directory.Exists(noPreviews.PreviewFolder) && noPreviews.MetricsJson.Length == 0 && noPreviews.Metrics is null && File.Exists(noPreviews.Pptx),
            "previews and numbers are optional");

        // PowerPoint holds the deck it has open, so replacing it fails. The run must not be lost.
        using (new FileStream(files.Pptx, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var again = ForgeOutput.Write(temp.At("decks"), brief, deck, spec, (path, _) => new JsonObject { ["pptx"] = Path.GetFileName(path) }, previews: false, log.Add);
            c.That(again.Renamed && Path.GetFileName(again.Pptx) == slug + "-2.pptx" && File.Exists(again.Pptx), "a deck that is open in PowerPoint is saved next to it as -2", again.Pptx);
            c.That(again.SpecJson.EndsWith(slug + "-2.spec.json", StringComparison.Ordinal) && File.Exists(again.SpecJson) && File.Exists(again.BriefJson) && File.Exists(again.MetricsJson), "and so are its other files");
            c.That(again.Metrics?["pptx"]?.GetValue<string>() == slug + "-2.pptx", "and the numbers name the file that was written");
            c.That(!File.Exists(files.Pptx + ".tmp") && !File.Exists(again.Pptx + ".tmp"), "the failed attempt leaves no temporary file");
            c.That(log.Any(l => l.Contains("is open in another program") && l.Contains(slug + "-2.pptx")), "the app is told why the name changed", string.Join(" | ", log));
            c.That(PptxPackage.Validate(File.ReadAllBytes(again.Pptx)).Count == 0, "the renamed deck is a valid package");
        }

        var custom = brief;
        var blank = new Brief { Id = "", Client = "Northwind Traders" };
        var named = ForgeOutput.Write(temp.At("decks3"), blank, deck, spec, null, previews: false);
        c.That(Path.GetFileName(named.Folder) == "northwind_traders", "a brief with no id is named for its client", named.Folder);
        var neither = ForgeOutput.Write(temp.At("decks3"), new Brief { Id = "  ", Client = "" }, deck, spec, null, previews: false);
        c.That(Path.GetFileName(neither.Folder) == "custom", "and a brief with neither is 'custom'", neither.Folder);
        _ = custom;
    }

    // ---- ForgeSession ----

    private static LoadedModel Loaded(ResolvedModel? model = null) => new() { Choice = model ?? Qwen7() };

    private static void Attach(Check c)
    {
        using var temp = new TempDir();
        var log = new List<string>();
        var session = NewSession(temp, Pc("snapdragon-x-plus-16"), log);
        var model = Qwen7();
        var loaded = Loaded(model);
        string variant = model.VariantId;

        var backend = Writer();
        var first = session.AttachAsync(loaded, backend).GetAwaiter().GetResult();
        c.That(first.Calibration is { Completed: true } && backend.Options.Count == 1, "a model with no measurement is measured with one slide", $"{backend.Options.Count} calls");
        c.That(session.Speed.Entry(variant) is { Guidance: GuidanceState.Verified, Lane: "npu", Model: "Qwen2.5 7B" } && first.Speed?.DecodeTokensPerSecond > 0, "the measurement is kept", $"{first.Speed}");
        c.That(File.Exists(temp.At("data\\speed.json")) && SpeedLog.Load(temp.At("data\\speed.json")).Entry(variant) is not null, "and saved for the next session");
        c.That(first.Story.TechnicalFacts.Any(f => f.Contains("tokens per second")) && first.Story.TechnicalFacts.Any(f => f.StartsWith("Constrained decoding", StringComparison.Ordinal)) && first.Story.MeasuredTokensPerSecond > 0,
            "the story quotes what was measured", string.Join(" | ", first.Story.TechnicalFacts));
        c.That(first.Facts.Title == "Qwen2.5 7B" && first.Variant == variant && !first.FellBack && first.Problems.Count == 0, "the prepared model describes what is loaded");

        backend = Writer();
        var second = session.AttachAsync(loaded, backend).GetAwaiter().GetResult();
        c.That(second.Calibration is null && backend.Options.Count == 0, "a model measured recently is not measured again");

        backend = Writer();
        var forced = session.AttachAsync(loaded, backend, new PrepareOptions(Calibrate: true)).GetAwaiter().GetResult();
        c.That(forced.Calibration is not null && backend.Options.Count == 1 && session.Speed.Entry(variant)!.Samples == 2, "unless asked to", $"{session.Speed.Entry(variant)!.Samples} samples");

        using var fresh = new TempDir();
        var other = NewSession(fresh, Pc("snapdragon-x-plus-16"));
        backend = Writer();
        var never = other.AttachAsync(loaded, backend, new PrepareOptions(Calibrate: false)).GetAwaiter().GetResult();
        c.That(never.Calibration is null && backend.Options.Count == 0 && never.Speed is null && !never.Story.TechnicalFacts.Any(f => f.Contains("tokens per second")), "measuring can be turned off, and then nothing is claimed");

        var old = new SpeedEntry { Variant = variant, Model = "Qwen2.5 7B", Lane = "npu", DecodeTokensPerSecond = 33, FirstTokenSeconds = 1, SlideTokens = 100, Guidance = GuidanceState.Verified, MeasuredAt = DateTimeOffset.UtcNow.AddDays(-40) };
        other.Speed.Record(old);
        backend = Writer();
        var stale = other.AttachAsync(loaded, backend).GetAwaiter().GetResult();
        c.That(stale.Calibration is not null && backend.Options.Count == 1, "a measurement older than 30 days is measured again");

        using var refusing = new TempDir();
        var third = NewSession(refusing, Pc("snapdragon-x-plus-16"));
        third.Speed.Record(old with { Guidance = GuidanceState.Rejected, MeasuredAt = DateTimeOffset.UtcNow });
        backend = Writer();
        var refused = third.AttachAsync(loaded, backend).GetAwaiter().GetResult();
        c.That(refused.Calibration is null && refused.Llm.GuidanceRejected && !refused.Llm.Info.SupportsGuidance, "a model that refused constrained decoding is not asked again", $"{refused.Llm.GuidanceRejected}");
        c.That(!refused.Story.TechnicalFacts.Any(f => f.StartsWith("Constrained decoding", StringComparison.Ordinal)), "and the story does not claim it");

        backend = Writer();
        var retest = third.AttachAsync(loaded, backend, new PrepareOptions(RetestGuidance: true)).GetAwaiter().GetResult();
        c.That(retest.Calibration is { Guidance: GuidanceState.Verified } && backend.Options.Count == 1 && backend.Options[0].GuidanceType == "json_schema" && !retest.Llm.GuidanceRejected && third.Speed.Entry(variant)!.Guidance == GuidanceState.Verified,
            "unless the person asks to try again, and then the new answer replaces the old", $"{third.Speed.Entry(variant)!.Guidance}");

        using var rough = new TempDir();
        var fourth = NewSession(rough, Pc("snapdragon-x-plus-16"), log);
        var broken = new FakeBackend { Answer = (_, _, _) => throw new ModelOutputException("no answer") };
        var failed = fourth.AttachAsync(loaded, broken).GetAwaiter().GetResult();
        c.That(failed.Calibration is { Completed: false } && fourth.Speed.Entry(variant) is null && failed.Story.MeasuredTokensPerSecond == 0, "a failed measurement is not kept and does not stop the deck", failed.Calibration?.Summary ?? "");
        c.That(log.Count > 0, "the app hears what the measurement found");
    }

    private static void SessionRun(Check c)
    {
        using var temp = new TempDir();
        var log = new List<string>();
        var session = NewSession(temp, Pc("snapdragon-x-plus-16"), log);
        var backend = Writer();
        var model = session.AttachAsync(Loaded(), backend).GetAwaiter().GetResult();
        var brief = BundledBriefs.Find("zava_technical")!;
        var progress = new List<HarnessProgress>();

        var run = session.RunAsync(model, brief, new ForgeRunOptions(Template: "surface_ultra"), new Progress(progress)).GetAwaiter().GetResult();

        var adapted = BriefAdapter.Adapt(brief, model.Story);
        var arc = Arcs.For(adapted, Arcs.Reserved(adapted));
        c.That(run.Result.Mode == HarnessMode.Guided && run.Deck.Slides.Count == arc.Count + 1, "the deck is written slide by slide, plus the measured slide", $"{run.Deck.Slides.Count} slides for an arc of {arc.Count}");
        c.That(run.Result.LintRemaining.Count == 0 && run.Result.GuidedDecoding, "a model that follows the prompt leaves nothing for lint and used the schema", string.Join("; ", run.Result.LintRemaining.Select(i => $"{i.Slide}:{i.Code}")));
        c.That(run.Settings.Mode == HarnessMode.Guided && run.Settings.RepairPasses == 2 && run.Settings.UseGuidance && run.Settings.Why.Count > 0, "the run settings come from the policy and carry their reasons");
        c.That(File.Exists(run.Files.Pptx) && run.Files.Previews.Count == run.Deck.Slides.Count && File.Exists(run.Files.MetricsJson), "the files are saved", run.Files.Folder);
        c.That(run.Files.Folder.StartsWith(temp.At("decks"), StringComparison.OrdinalIgnoreCase), "into the session's output folder");
        c.That(PptxPackage.Validate(File.ReadAllBytes(run.Files.Pptx)).Count == 0, "the deck is a valid package");
        c.That(progress.Count > 0, "progress is reported");

        var metrics = run.Files.Metrics!;
        c.That(metrics["template"]?.GetValue<string>() == "surface_ultra" && metrics["lane"]?.GetValue<string>() == "npu" && metrics["model"]?.GetValue<string>() == "Qwen2.5 7B", "the numbers name the template, lane and model that ran", metrics.ToJsonString()[..Math.Min(300, metrics.ToJsonString().Length)]);
        c.That(metrics["device"]?.GetValue<string>() == "Surface Laptop 7 with Snapdragon X Plus", "and the PC", metrics["device"]?.ToJsonString() ?? "");
        c.That(run.Result.Energy is { Measured: false } && run.Result.MeasuredFact is not null && !run.Result.MeasuredFact.Contains(" Wh"), "no power meter on the NPU lane, so no energy is claimed", run.Result.Energy.Basis);

        string remembered = temp.At("data\\speed.json");
        var reloaded = SpeedLog.Load(remembered);
        c.That(reloaded.Runs.Count == 1 && reloaded.Runs[0].Variant == model.Variant && reloaded.Runs[0].Slides == run.Deck.Slides.Count && reloaded.Runs[0].Mode == "Guided" && reloaded.Runs[0].Seconds > 0, "the finished deck is remembered for the next ETA", $"{reloaded.Runs.Count}");
        c.That(reloaded.Entry(model.Variant) is { Guidance: GuidanceState.Verified, DecodeTokensPerSecond: > 0, SlideTokens: > 0 } && model.Speed is { DecodeTokensPerSecond: > 0 }, "and so is what it taught about the model");
        c.That(reloaded.Estimate(model.Variant, 11) is { Seconds: > 0 } estimate && estimate.Basis.StartsWith("median of 1 earlier deck", StringComparison.Ordinal), "the next deck gets an ETA from it");

        var plain = session.RunAsync(model, brief, new ForgeRunOptions(Template: "no-such-template", Previews: false, OutputRoot: temp.At("elsewhere"))).GetAwaiter().GetResult();
        c.That(plain.Files.Folder.StartsWith(temp.At("elsewhere"), StringComparison.OrdinalIgnoreCase) && plain.Files.Previews.Count == 0 && plain.Files.Metrics!["template"]!.GetValue<string>() == "surface", "an unknown template falls back to Surface, and the output folder can be chosen per run", plain.Files.Metrics?["template"]?.ToJsonString() ?? "");

        var arcAsked = session.RunAsync(model, brief, new ForgeRunOptions(new RunRequest(Mode: HarnessMode.Arc, RepairPasses: 0), Previews: false)).GetAwaiter().GetResult();
        c.That(arcAsked.Result.Mode == HarnessMode.Guided && arcAsked.Settings.RepairPasses == 0 && arcAsked.Settings.Why.Any(w => w.Contains("needs a large model")), "asking for a mode the model cannot do falls back, and says why", string.Join(" | ", arcAsked.Settings.Why));
    }

    private sealed class Progress(List<HarnessProgress> items) : IProgress<HarnessProgress>
    {
        public void Report(HarnessProgress value) => items.Add(value);
    }

    /// <summary>A deck always tells the truth about the PC it was made on, whatever the capability view is showing.</summary>
    private static void Honest(Check c)
    {
        using var temp = new TempDir();
        var devices = Pc("snapdragon-x-plus-16");
        var session = NewSession(temp, devices);
        devices.Simulate("nvidia-n1x-128");
        c.That(devices.IsSimulated && devices.Effective.Profile.SimulatedId == "nvidia-n1x-128" && session.Hardware.Source == "detected" && session.Hardware.Product.Contains("Surface Laptop"), "the session keeps the real PC while a device is simulated", $"{session.Hardware.Product}");

        var model = session.AttachAsync(Loaded(), Writer(), new PrepareOptions(Calibrate: false)).GetAwaiter().GetResult();
        c.That(model.Story.Name == "Surface Laptop 7" && model.Story.Silicon == "Snapdragon X Plus" && model.Story.Engine == "Snapdragon NPU" && !model.Story.UnifiedMemory, "the story names the real PC, not the simulated one", $"{model.Story.Name} | {model.Story.Silicon} | {model.Story.Engine}");

        var run = session.RunAsync(model, BundledBriefs.Find("contoso_sales")!, new ForgeRunOptions(Previews: false)).GetAwaiter().GetResult();
        string everything = File.ReadAllText(run.Files.SpecJson) + File.ReadAllText(run.Files.MetricsJson);
        c.That(!Regex.IsMatch(everything, @"RTX Spark|Ultra", RegexOptions.IgnoreCase) || everything.Contains("Surface Laptop 7"), "nothing about the simulated device reaches the deck", "");
        c.That(run.Files.Metrics!["device"]!.GetValue<string>().Contains("Surface Laptop 7"), "the numbers record the real PC", run.Files.Metrics["device"]!.ToJsonString());
        c.That(!Regex.IsMatch(everything, @"\bTOPS\b|Copilot\+?", RegexOptions.IgnoreCase), "the deck never mentions TOPS or Copilot+");

        devices.ClearSimulation();
        c.That(!devices.IsSimulated && session.Hardware.Source == "detected", "clearing the simulation changes nothing about the real PC");
    }
}
