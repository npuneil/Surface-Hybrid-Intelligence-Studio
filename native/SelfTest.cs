using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HybridIntelligenceStudio;

/// <summary>Deterministic checks of planner arithmetic, platform semantics, parsing, privacy and exports. No network or UI.</summary>
internal static class SelfTest
{
    private sealed record Result(string Name, bool Passed, string Detail);

    internal static int Run(AppOptions options)
    {
        var results = new List<Result>();
        void Check(string name, bool passed, string detail = "") => results.Add(new Result(name, passed, detail));
        void Guard(string name, Action action)
        {
            try { action(); }
            catch (Exception exception) { Check(name, false, exception.Message); }
        }

        var device = new DeviceFacts("Surface Laptop Ultra", "Microsoft", "Test CPU", 20, ["Test RTX GPU"],
            137438953472, 68699033600, [new GpuBudget("Test RTX GPU", 64000)], "test", null, null, null, null);
        var baseInput = new SizingInput(14, 4, 8192, 0.19, 2, 62.5, 6, 2);

        Guard("sizing", () =>
        {
            Check("weight-14b-4bit", Near(Sizing.WeightGiB(14, 4), 7e9 / 1073741824d), $"{Sizing.WeightGiB(14, 4)}");
            Check("weight-1b-16bit", Near(Sizing.WeightGiB(1, 16), 2e9 / 1073741824d));
            Check("weight-32b-8bit", Near(Sizing.WeightGiB(32, 8), 32e9 / 1073741824d));
            Check("weight-scales-with-bits", Near(Sizing.WeightGiB(8, 16), 4 * Sizing.WeightGiB(8, 4)));
            Check("kv-scales-with-context", Near(Sizing.KvGiB(8192, 0.19), 8.192 * 0.19));

            var r = Sizing.Estimate(baseInput);
            double working = Sizing.WeightGiB(14, 4) + 8.192 * 0.19 + 2;
            Check("working-set-includes-allowances", Near(r.WorkingSetGiB, working), $"{r.WorkingSetGiB}");
            Check("required-includes-headroom", Near(r.RequiredGiB, working + 2));
            Check("usable-subtracts-reserve", Near(r.UsableGiB, 56.5));
            Check("14b-4bit-fits-gpu-budget", r.Verdict == SizingVerdict.Fits);

            var xl = Sizing.Estimate(new SizingInput(32, 16, 8192, 0.25, 2.5, 62.5, 6, 2));
            Check("32b-16bit-exceeds-gpu-budget", xl.Verdict == SizingVerdict.DoesNotFit && xl.MarginGiB < 0, $"{xl.MarginGiB}");

            double w = Sizing.WeightGiB(1, 8);
            var exact = Sizing.Estimate(new SizingInput(1, 8, 1000, 0, 0, 10, 0, 10 - w - 0));
            Check("boundary-exact-fit-is-tight", exact.Verdict == SizingVerdict.Tight && exact.FitsEstimate, $"{exact.MarginGiB}");
            var over = Sizing.Estimate(new SizingInput(1, 8, 1000, 0, 0, 10, 0, 10 - w + 0.001));
            Check("boundary-just-over-does-not-fit", over.Verdict == SizingVerdict.DoesNotFit);
            var tight = Sizing.Estimate(new SizingInput(1, 8, 1000, 0, 0, 10, 0, 10 - w - 0.5));
            Check("under-one-gib-margin-is-tight", tight.Verdict == SizingVerdict.Tight);
            var roomy = Sizing.Estimate(new SizingInput(1, 8, 1000, 0, 0, 10, 0, 10 - w - 1.5));
            Check("over-one-gib-margin-fits", roomy.Verdict == SizingVerdict.Fits);
            Check("zero-headroom-valid", Sizing.Estimate(baseInput with { HeadroomGiB = 0 }).IsValid);
            Check("zero-allowances-valid", Sizing.Estimate(baseInput with { KvGiBPer1kTokens = 0, RuntimeOverheadGiB = 0 }).IsValid);

            var invalid = new (string, SizingInput)[]
            {
                ("params-zero", baseInput with { ParameterBillions = 0 }),
                ("params-negative", baseInput with { ParameterBillions = -1 }),
                ("params-nan", baseInput with { ParameterBillions = double.NaN }),
                ("params-infinite", baseInput with { ParameterBillions = double.PositiveInfinity }),
                ("bits-5", baseInput with { WeightBits = 5 }),
                ("bits-0", baseInput with { WeightBits = 0 }),
                ("context-zero", baseInput with { ContextTokens = 0 }),
                ("context-nan", baseInput with { ContextTokens = double.NaN }),
                ("kv-negative", baseInput with { KvGiBPer1kTokens = -0.1 }),
                ("kv-infinite", baseInput with { KvGiBPer1kTokens = double.PositiveInfinity }),
                ("runtime-negative", baseInput with { RuntimeOverheadGiB = -1 }),
                ("budget-zero", baseInput with { BudgetGiB = 0 }),
                ("budget-nan", baseInput with { BudgetGiB = double.NaN }),
                ("budget-negative", baseInput with { BudgetGiB = -5 }),
                ("reserve-equals-budget", baseInput with { ReserveGiB = 62.5 }),
                ("reserve-exceeds-budget", baseInput with { ReserveGiB = 70 }),
                ("reserve-negative", baseInput with { ReserveGiB = -1 }),
                ("headroom-negative", baseInput with { HeadroomGiB = -1 }),
                ("headroom-nan", baseInput with { HeadroomGiB = double.NaN }),
            };
            foreach (var (name, input) in invalid)
            {
                var result = Sizing.Estimate(input);
                Check("invalid-" + name, result.Verdict == SizingVerdict.Invalid && !string.IsNullOrEmpty(result.Error) && !result.FitsEstimate, result.Error ?? "");
            }

            Check("budget-gpu-view", Near(Sizing.ResolveBudget(BudgetSource.GpuDriverBudget, device, double.NaN) ?? 0, 62.5));
            Check("budget-windows-view", Near(Sizing.ResolveBudget(BudgetSource.WindowsVisibleMemory, device, double.NaN) ?? 0, 68699033600 / 1073741824d));
            double gpu = Sizing.ResolveBudget(BudgetSource.GpuDriverBudget, device, 0)!.Value;
            double win = Sizing.ResolveBudget(BudgetSource.WindowsVisibleMemory, device, 0)!.Value;
            foreach (var source in Enum.GetValues<BudgetSource>())
            {
                double? b = Sizing.ResolveBudget(source, device, 40);
                Check($"budget-{source}-never-summed", b is null || (!Near(b.Value, gpu + win) && b.Value < 128), $"{b}");
            }
            Check("installed-memory-is-not-a-budget-view", !Enum.GetNames<BudgetSource>().Any(n => n.Contains("Installed", StringComparison.OrdinalIgnoreCase)));
            Check("budget-missing-gpu-is-null", Sizing.ResolveBudget(BudgetSource.GpuDriverBudget, device with { GpuBudgets = [] }, 0) is null);
            Check("budget-no-device-is-null", Sizing.ResolveBudget(BudgetSource.WindowsVisibleMemory, null, 0) is null);
            Check("budget-custom-zero-is-null", Sizing.ResolveBudget(BudgetSource.Custom, device, 0) is null);
            Check("budget-custom-nan-is-null", Sizing.ResolveBudget(BudgetSource.Custom, device, double.NaN) is null);

            var bands = Sizing.Profiles.ToDictionary(p => p.Size);
            Check("band-S-1-4B", bands[TShirtSize.S] is { MinBillions: 1, MaxBillions: 4 });
            Check("band-M-7-8B", bands[TShirtSize.M] is { MinBillions: 7, MaxBillions: 8 });
            Check("band-L-14-20B", bands[TShirtSize.L] is { MinBillions: 14, MaxBillions: 20 });
            Check("band-XL-24-32B", bands[TShirtSize.XL] is { MinBillions: 24, MaxBillions: 32 });
            Check("band-XXL-65-72B", bands[TShirtSize.XXL] is { MinBillions: 65, MaxBillions: 72, DefaultBillions: 70 } && Near(bands[TShirtSize.XXL].KvGiBPer1kTokens, 0.31));
            Check("band-defaults-in-range", Sizing.Profiles.All(p => p.DefaultBillions >= p.MinBillions && p.DefaultBillions <= p.MaxBillions));
            Check("classify-boundaries", Sizing.Classify(1) == TShirtSize.S && Sizing.Classify(4) == TShirtSize.S && Sizing.Classify(7) == TShirtSize.M
                && Sizing.Classify(20) == TShirtSize.L && Sizing.Classify(32) == TShirtSize.XL && Sizing.Classify(65) == TShirtSize.XXL && Sizing.Classify(72) == TShirtSize.XXL);
            Check("classify-gaps-and-invalid", Sizing.Classify(5) is null && Sizing.Classify(10) is null && Sizing.Classify(40) is null
                && Sizing.Classify(120) is null && Sizing.Classify(double.NaN) is null);
            var one = Sizing.EstimateTogether([baseInput]);
            var single = Sizing.Estimate(baseInput);
            Check("together-one-equals-estimate", one.Verdict == single.Verdict && Near(one.RequiredGiB, single.RequiredGiB) && Near(one.UsableGiB, single.UsableGiB));
            var second = baseInput with { ParameterBillions = 8, KvGiBPer1kTokens = 0.13, RuntimeOverheadGiB = 1.5 };
            var pair = Sizing.EstimateTogether([baseInput, second]);
            Check("together-adds-working-sets-headroom-once", Near(pair.WorkingSetGiB, single.WorkingSetGiB + Sizing.Estimate(second).WorkingSetGiB)
                && Near(pair.RequiredGiB, pair.WorkingSetGiB + baseInput.HeadroomGiB) && Near(pair.UsableGiB, single.UsableGiB), $"{pair.RequiredGiB}");
            Check("together-mixed-budgets-invalid", !Sizing.EstimateTogether([baseInput, second with { BudgetGiB = 30 }]).IsValid);
            Check("together-empty-invalid", !Sizing.EstimateTogether([]).IsValid);
            Check("estimate-language-is-illustrative", r.Summary.Contains("Illustrative estimate") && !r.Summary.Contains("guarantee", StringComparison.OrdinalIgnoreCase)
                && Sizing.Caveat.Contains("not a procurement guarantee") && Sizing.Caveat.Contains("does not imply") && Sizing.Caveat.Contains("one model resident"));
        });

        Guard("platform", () =>
        {
            var platforms = PlatformGuide.Platforms;
            Check("platform-three-profiles", platforms.Select(p => p.Id).SequenceEqual(["rtx", "snapdragon", "intel"]));
            Check("platform-only-rtx-current", platforms.Count(p => p.CurrentDevice) == 1 && platforms.Single(p => p.CurrentDevice).Id == "rtx");
            foreach (var p in platforms.Where(p => !p.CurrentDevice))
            {
                Check($"platform-{p.Id}-labelled-pilot", p.Status.Contains("Recommended pilot") && p.Status.Contains("illustrative") && p.Status.Contains("not run on this laptop"));
                Check($"platform-{p.Id}-verified-per-sku", p.AcceleratorAndRuntime.Contains("verified per SKU"));
                Check($"platform-{p.Id}-has-verification", p.VerifyBeforePilot.Length >= 3);
                Check($"platform-{p.Id}-small-models", p.UseCases.All(u => u.Size is TShirtSize.S or TShirtSize.M));
            }
            Check("platform-rtx-heavier", platforms[0].UseCases.Any(u => u.Size == TShirtSize.L));
            var snap = platforms.Single(p => p.Id == "snapdragon");
            Check("snapdragon-qwen3-4b-small", snap.UseCases[0].Workflow.Contains("Qwen3-4B") && snap.UseCases[0].Size == TShirtSize.S);
            Check("snapdragon-qairt-geniex", snap.AcceleratorAndRuntime.Contains("QAIRT") && snap.AcceleratorAndRuntime.Contains("GenieX")
                && snap.VerifyBeforePilot.Any(v => v.Contains("same QAIRT version")));
            Check("snapdragon-speech-future-only", snap.UseCases.Any(u => u.Title.Contains("future") && u.Caveat.Contains("no audio")));
            Check("snapdragon-vendor-memory-not-planner", snap.VerifyBeforePilot.Any(v => v.Contains("not a planner fit result")));
            var intel = platforms.Single(p => p.Id == "intel");
            Check("intel-openvino-explicit-device", intel.AcceleratorAndRuntime.Contains("OpenVINO GenAI") && intel.AcceleratorAndRuntime.Contains("explicitly selected"));
            Check("intel-meeting-whisper", intel.UseCases.Any(u => u.Workflow.Contains("Whisper") && u.LiveScenarioKey == "meeting"));
            Check("foundry-list-not-proof", PlatformGuide.FoundryDistinction.Contains("does not prove"));
            Check("genie-separate-path", PlatformGuide.GenieDistinction.Contains("not the only way"));
            Check("no-inference-claim", PlatformGuide.NoInferenceHere.Contains("not run on this laptop") && PlatformGuide.NoInferenceHere.Contains("never switches providers automatically"));
            Check("runtime-not-inferred", PlatformGuide.RuntimeNotInferred.Contains("never inferred"));
            foreach (string term in PlatformGuide.ForbiddenClaims)
            {
                var hits = PlatformGuide.AllText().Where(t => Regex.IsMatch(t, $@"\b{Regex.Escape(term)}", RegexOptions.IgnoreCase)).ToArray();
                Check($"no-claim-{term.Replace(' ', '-')}", hits.Length == 0, string.Join(" | ", hits));
            }
            Check("sources-approved", platforms.SelectMany(p => p.Sources).All(s => ApprovedLinks.IsApproved(s.Url)));
            Check("links-reject-http", !ApprovedLinks.IsApproved("http://github.com/openvinotoolkit/openvino.genai"));
            Check("links-reject-lookalike", !ApprovedLinks.IsApproved("https://github.com.evil.example/openvinotoolkit/openvino.genai"));
            Check("links-reject-suffix", !ApprovedLinks.IsApproved(ApprovedLinks.OpenVinoGenAi + "/../../evil"));
            Check("links-reject-null", !ApprovedLinks.IsApproved(null) && !ApprovedLinks.IsApproved("javascript:alert(1)"));
        });

        Guard("sales", () =>
        {
            var w = SalesSizing.Workloads;
            Check("sales-workload-ids-unique", w.Select(x => x.Id).Distinct().Count() == w.Length && w.Length >= 8, $"{w.Length}");
            Check("sales-core-mapping", SalesSizing.Find("transcription")?.Size == TShirtSize.S && SalesSizing.Find("translation")?.Size == TShirtSize.M
                && SalesSizing.Find("coding")?.Size == TShirtSize.L && SalesSizing.Find("prototyping")?.Size == TShirtSize.XL
                && SalesSizing.Find("frontier") is { Size: TShirtSize.XXL, LiveOnThisLaptop: false, DemoId: null });
            Check("sales-every-size-represented", Enum.GetValues<TShirtSize>().All(s => w.Any(x => x.Size == s)));
            Check("sales-demo-ids-known", w.Where(x => x.DemoId is not null).All(x => DemoCatalog.KnownIds.Contains(x.DemoId))
                && w.Where(x => x.LiveOnThisLaptop).All(x => x.DemoId is not null));
            Check("sales-guidance-only-labelled", w.Where(x => !x.LiveOnThisLaptop).All(x => x.LiveNote.Contains("only") || x.LiveNote.Contains("estimate")));
            Check("sales-defaults-match-technical", Sizing.DefaultBits == 4 && Sizing.DefaultContextTokens == 8192 && Sizing.DefaultReserveGiB == 6 && Sizing.DefaultHeadroomGiB == 2);
            Check("sales-empty-selection-null", SalesSizing.Recommend([], device) is null && SalesSizing.Recommend(["unknown"], device) is null);

            var coding = SalesSizing.Recommend(["coding"], device)!;
            var engine = Sizing.Estimate(new SizingInput(14, 4, 8192, 0.19, 2, 62.5, 6, 2));
            Check("sales-uses-engine-arithmetic", coding.ThisLaptop.Result is { } cr && Near(cr.RequiredGiB, engine.RequiredGiB) && Near(cr.UsableGiB, engine.UsableGiB),
                $"{coding.ThisLaptop.Result?.RequiredGiB} vs {engine.RequiredGiB}");
            Check("sales-coding-large-comfortable-here", coding.Profile.Size == TShirtSize.L && coding.ThisLaptop.Fit == SalesFit.Comfortable);
            Check("sales-percent-from-engine", Near(coding.ThisLaptop.PercentUsed, engine.RequiredGiB / engine.UsableGiB * 100), $"{coding.ThisLaptop.PercentUsed}");
            var by = coding.Comparisons.ToDictionary(c => c.Device.Id);
            Check("sales-large-wont-fit-16gb", by["laptop16"].Fit == SalesFit.WontFit && by["laptop32"].Fit == SalesFit.Comfortable);
            Check("sales-talk-track-16gb-contrast", coding.TalkTrack.Contains("16 GB laptop would not have room") && coding.TalkTrack.Contains($"{Math.Round(coding.ThisLaptop.PercentUsed):0}%"), coding.TalkTrack);
            Check("sales-not-pilot-for-large", !coding.PilotCandidate);

            var small = SalesSizing.Recommend(["transcription"], device)!;
            Check("sales-small-fits-everywhere", small.Profile.Size == TShirtSize.S && small.ThisLaptop.Fit == SalesFit.Comfortable
                && small.Comparisons.All(c => c.Fit == SalesFit.Comfortable) && small.PilotCandidate, small.TalkTrack);
            Check("sales-small-suggests-verified-pilot", small.TalkTrack.Contains("Snapdragon or Intel AI PC pilot") && small.TalkTrack.Contains("verify"));
            var mixed = SalesSizing.Recommend(["transcription", "translation", "coding"], device)!;
            Check("sales-largest-workload-wins", mixed.Profile.Size == TShirtSize.L && mixed.Workloads.Count == 3, $"{mixed.Profile.Size}");
            Check("sales-names-joined", mixed.TalkTrack.StartsWith("Transcription & meeting notes, Translation and Local coding assistant need a Large (L)"), mixed.TalkTrack);
            var xl = SalesSizing.Recommend(["prototyping"], device)!;
            Check("sales-xl-fits-here-not-16gb", xl.ThisLaptop.Fit == SalesFit.Comfortable && xl.Comparisons.Single(c => c.Device.Id == "laptop16").Fit == SalesFit.WontFit);
            var unknown = SalesSizing.Recommend(["coding"], null)!;
            Check("sales-no-device-is-unknown", unknown.ThisLaptop.Fit == SalesFit.Unknown && unknown.TalkTrack.Contains("Connect to the local service"), unknown.TalkTrack);
            var tinyDevice = device with { GpuBudgets = [new GpuBudget("Test RTX GPU", 8 * 1024)] };
            Check("sales-small-budget-wont-fit", SalesSizing.Recommend(["coding"], tinyDevice)!.ThisLaptop.Fit == SalesFit.WontFit);
            Check("sales-budget-never-windows-or-installed", Near(coding.ThisLaptop.Input!.BudgetGiB, 62.5));

            // 100-level: talk tracks avoid jargon; all sales copy avoids unsupported claims.
            var tracks = w.Select(x => SalesSizing.Recommend([x.Id], device)!.TalkTrack)
                .Concat([mixed.TalkTrack, unknown.TalkTrack, SalesSizing.Recommend(w.Select(x => x.Id), device)!.TalkTrack]).ToArray();
            string[] jargon = ["GiB", "KV", "bit", "token", "quantiz", "parameter", "CUDA", "context window"];
            foreach (string term in jargon)
            {
                var hits = tracks.Where(t => Regex.IsMatch(t, $@"\b{Regex.Escape(term)}", RegexOptions.IgnoreCase)).ToArray();
                Check($"sales-no-jargon-{term.Replace(' ', '-')}", hits.Length == 0, string.Join(" | ", hits));
            }
            foreach (string term in PlatformGuide.ForbiddenClaims)
            {
                var hits = SalesSizing.AllText().Concat(tracks).Where(t => Regex.IsMatch(t, $@"\b{Regex.Escape(term)}", RegexOptions.IgnoreCase)).ToArray();
                Check($"sales-no-claim-{term.Replace(' ', '-')}", hits.Length == 0, string.Join(" | ", hits));
            }
            Check("sales-article-an-extra-large", xl.TalkTrack.Contains("needs an Extra large (XL) AI model"), xl.TalkTrack);
            var frontier = SalesSizing.Recommend(["frontier"], device)!;
            Check("sales-xxl-here-not-32gb", frontier.Profile.Size == TShirtSize.XXL && frontier.ThisLaptop.Fit == SalesFit.Comfortable
                && frontier.Comparisons.All(c => c.Fit == SalesFit.WontFit) && frontier.TalkTrack.Contains("a Double extra large (XXL)"), frontier.TalkTrack);
        });

        Guard("ultra", () =>
        {
            var configs = UltraSizing.Configurations;
            Check("ultra-published-configurations", configs.Select(c => c.MemoryGB).SequenceEqual([24, 32, 48, 64, 128]));
            Check("ultra-launch-share", Near(UltraSizing.LaunchShare, 79.9 / 128) && Math.Round(UltraSizing.LaunchShare * 100) == 62, $"{UltraSizing.LaunchShare}");
            Check("ultra-measured-share", UltraSizing.MeasuredShare(device) is double ms && Near(ms, 62.5 / 128), $"{UltraSizing.MeasuredShare(device)}");
            Check("ultra-measured-share-missing", UltraSizing.MeasuredShare(null) is null && UltraSizing.MeasuredShare(device with { GpuBudgets = [] }) is null
                && UltraSizing.MeasuredShare(device with { InstalledBytes = null }) is null);
            Check("ultra-this-pc-config", UltraSizing.ThisPcConfig(device) == 128 && UltraSizing.ThisPcConfig(device with { InstalledBytes = 96L << 30 }) is null);
            Check("ultra-custom-share-bounds", UltraSizing.Share(SplitBasis.Custom, device, 50) is double c50 && Near(c50, 0.5)
                && UltraSizing.Share(SplitBasis.Custom, device, 5) is null && UltraSizing.Share(SplitBasis.Custom, device, 95) is null
                && UltraSizing.Share(SplitBasis.Custom, device, double.NaN) is null);
            Check("ultra-model-sharing", UltraSizing.Models(SalesSizing.Workloads.Where(w => w.Id is "writing" or "translation" or "documents")).Count == 1
                && UltraSizing.Models(SalesSizing.Workloads).Count == SalesSizing.Workloads.Length - 2);
            Check("ultra-empty-null", UltraSizing.Plan([], false, false, SplitBasis.MicrosoftLaunch, 50, device) is null
                && UltraSizing.Plan(["unknown"], false, false, SplitBasis.MicrosoftLaunch, 50, device) is null);

            UltraPlan P(string[] ids, SplitBasis basis = SplitBasis.MicrosoftLaunch, bool together = false, bool longDocs = false) =>
                UltraSizing.Plan(ids, together, longDocs, basis, UltraSizing.DefaultCustomPercent, device)!;
            string Ladder(UltraPlan p) => string.Join(",", p.Configs.Select(c => $"{c.Config.MemoryGB}:{c.Fit}"));

            // Microsoft launch split (62%), one model at a time, 8K context.
            var coding = P(["coding"]);
            var codingEngine = Sizing.Estimate(SalesSizing.InputFor(TShirtSize.L, 48 * UltraSizing.LaunchShare));
            Check("ultra-same-engine", coding.For(48).Result is { } r48 && Near(r48.RequiredGiB, codingEngine.RequiredGiB) && Near(r48.UsableGiB, codingEngine.UsableGiB)
                && Near(coding.For(48).GpuGiB, 48 * 79.9 / 128) && Near(coding.For(128).GpuGiB, 79.9));
            Check("ultra-ms-S-24", P(["transcription"]).Recommended?.Config.MemoryGB == 24, Ladder(P(["transcription"])));
            Check("ultra-ms-M-32-24-little", P(["translation"]) is { Recommended.Config.MemoryGB: 32 } m && m.For(24).Fit == UltraFit.LittleRoom, Ladder(P(["translation"])));
            Check("ultra-ms-L-48-32-little", coding.Recommended?.Config.MemoryGB == 48 && coding.For(32).Fit == UltraFit.LittleRoom && coding.For(24).Fit == UltraFit.WontFit
                && coding.For(64).Fit == UltraFit.Roomy, Ladder(coding));
            Check("ultra-ms-XL-64-48-little", P(["prototyping"]) is { Recommended.Config.MemoryGB: 64 } x && x.For(48).Fit == UltraFit.LittleRoom, Ladder(P(["prototyping"])));
            Check("ultra-ms-XXL-128", P(["frontier"]) is { Recommended.Config.MemoryGB: 128 } f && f.For(64).Fit == UltraFit.WontFit, Ladder(P(["frontier"])));
            Check("ultra-one-at-a-time-largest-wins", P(["transcription", "translation", "coding"]).Recommended?.Config.MemoryGB == 48);

            // This PC's measured split (62.5 of 128 GiB); the 128 GB card uses the measured budget itself.
            var mt = P(["transcription"], SplitBasis.MeasuredThisPc);
            Check("ultra-measured-S-32", mt.Recommended?.Config.MemoryGB == 32 && mt.For(24).Fit == UltraFit.WontFit, Ladder(mt));
            Check("ultra-measured-128-is-this-pc", mt.For(128) is { MeasuredHere: true } here && Near(here.GpuGiB, 62.5) && !mt.For(64).MeasuredHere);
            Check("ultra-measured-XL-128", P(["prototyping"], SplitBasis.MeasuredThisPc) is { Recommended.Config.MemoryGB: 128 } mx && mx.For(64).Fit == UltraFit.LittleRoom);
            Check("ultra-measured-waits-without-device", UltraSizing.Plan(["coding"], false, false, SplitBasis.MeasuredThisPc, 50, null) is
                { Recommended: null, Headline: "Waiting for this PC's memory report", TalkTrack.Length: 0 } wait && wait.Configs.All(c => c.Fit == UltraFit.Unknown));
            Check("ultra-custom-tiny-share-no-room", P(["transcription"], SplitBasis.Custom) is { } cu
                && UltraSizing.Plan(["transcription"], false, false, SplitBasis.Custom, 10, device)!.For(24).Fit == UltraFit.WontFit && cu.Recommended is not null);

            // Together: shared models count once; distinct models add up.
            var shared = P(["writing", "translation", "documents"], together: true);
            Check("ultra-together-shared-model", shared.Models.Count == 1 && shared.Recommended?.Config.MemoryGB == 32
                && shared.TalkTrack.Contains("can share one AI model"), shared.TalkTrack);
            var both = P(["coding", "reasoning"], together: true);
            var bothEngine = Sizing.EstimateTogether([SalesSizing.InputFor(TShirtSize.L, 64 * UltraSizing.LaunchShare), SalesSizing.InputFor(TShirtSize.L, 64 * UltraSizing.LaunchShare)]);
            Check("ultra-together-sums", both.Models.Count == 2 && both.Recommended?.Config.MemoryGB == 64 && both.For(48).Fit == UltraFit.LittleRoom
                && Near(both.For(64).Result!.RequiredGiB, bothEngine.RequiredGiB), Ladder(both));
            Check("ultra-one-at-a-time-same-pair", P(["coding", "reasoning"]).Recommended?.Config.MemoryGB == 48);
            var everything = P(SalesSizing.Workloads.Select(w => w.Id).ToArray(), together: true);
            Check("ultra-together-everything-none", everything.Recommended is null && everything.SmallestThatHolds is null
                && everything.Headline == "No configuration holds all of these open together" && everything.TalkTrack.Contains("one at a time"), everything.TalkTrack);

            // Long documents (32K context).
            Check("ultra-long-coding-48", P(["coding"], longDocs: true) is { Recommended.Config.MemoryGB: 48 } lc && lc.For(32).Fit == UltraFit.WontFit);
            Check("ultra-long-xl-128", P(["prototyping"], longDocs: true) is { Recommended.Config.MemoryGB: 128 } lx && lx.For(64).Fit == UltraFit.LittleRoom, Ladder(P(["prototyping"], longDocs: true)));
            Check("ultra-long-pair-128", P(["coding", "reasoning"], together: true, longDocs: true).Recommended?.Config.MemoryGB == 128);

            // Recommendations never skip: every configuration above a roomy one is roomy too.
            foreach (var basis in Enum.GetValues<SplitBasis>())
                foreach (var w in SalesSizing.Workloads)
                {
                    var plan = P([w.Id], basis);
                    var fits = plan.Configs.Select(c => c.Fit).ToArray();
                    int first = Array.IndexOf(fits, UltraFit.Roomy);
                    Check($"ultra-monotone-{basis}-{w.Id}", first < 0 || fits.Skip(first).All(f => f == UltraFit.Roomy), Ladder(plan));
                }

            // Talk tracks: personalized step-up, no jargon, no unsupported claims.
            Check("ultra-step-up-coding", coding.TalkTrack.Contains("Plan on the 48 GB Surface Laptop Ultra") && coding.TalkTrack.Contains("The 32 GB configuration also holds it")
                && coding.TalkTrack.Contains("Step up to 64 GB to also cover Bigger-model prototyping"), coding.TalkTrack);
            var small = P(["transcription"]);
            Check("ultra-step-up-small", small.TalkTrack.Contains("Even the smallest configuration has room")
                && small.TalkTrack.Contains("Step up to 32 GB to also cover Translation, Writing & summaries, Ask your documents and Photos & scanned forms"), small.TalkTrack);
            Check("ultra-largest-no-step-up", !P(["frontier"]).TalkTrack.Contains("Step up"));
            Check("ultra-step-up-together", P(["transcription"], together: true).TalkTrack.Contains("Step up to 32 GB to also keep"), P(["transcription"], together: true).TalkTrack);
            var ultraTracks = Enum.GetValues<SplitBasis>().SelectMany(b => SalesSizing.Workloads.SelectMany(w => new[] { P([w.Id], b), P([w.Id], b, true, true) }))
                .Concat([both, shared, everything, P(["coding", "reasoning"], together: true, longDocs: true)])
                .Select(p => p.TalkTrack).ToArray();
            Check("ultra-tracks-say-estimate", ultraTracks.All(t => t.Contains("estimate") && t.Contains("confirm on the shipping configuration")));
            foreach (string term in new[] { "GiB", "KV", "bit", "token", "quantiz", "parameter", "CUDA", "context window" })
            {
                var hits = ultraTracks.Where(t => Regex.IsMatch(t, $@"\b{Regex.Escape(term)}", RegexOptions.IgnoreCase)).ToArray();
                Check($"ultra-no-jargon-{term.Replace(' ', '-')}", hits.Length == 0, string.Join(" | ", hits.Take(2)));
            }
            foreach (string term in PlatformGuide.ForbiddenClaims.Concat(["price of", "cheaper", "costs", "$"]))
            {
                var hits = UltraSizing.AllText().Concat(ultraTracks).Where(t => Regex.IsMatch(t, $@"(?<!\w){Regex.Escape(term)}", RegexOptions.IgnoreCase)).ToArray();
                Check($"ultra-no-claim-{term.Replace(' ', '-')}", hits.Length == 0, string.Join(" | ", hits.Take(2)));
            }
            Check("ultra-basis-notes-labelled", UltraSizing.BasisNote(SplitBasis.MicrosoftLaunch, device, 50).Contains("has not published")
                && UltraSizing.BasisNote(SplitBasis.MeasuredThisPc, device, 50).Contains("62.5 GB of its 128 GB")
                && UltraSizing.Footnote.Contains("Shared GPU memory is never counted") && UltraSizing.Footnote.Contains("39.6 GiB"));
            Check("ultra-spec-link-approved", ApprovedLinks.IsApproved(ApprovedLinks.SurfaceLaptopUltraSpecs));
        });

        Guard("learn", () =>
        {
            var lessons = CodingTrack.Lessons;
            Check("learn-eight-lessons", lessons.Length == 8, $"{lessons.Length}");
            Check("learn-ids-unique", lessons.Select(l => l.Id).Distinct().Count() == lessons.Length);
            foreach (var l in lessons)
            {
                bool complete = Regex.IsMatch(l.Id, "^[a-z-]+$") && l.Code.Length > 0 && l.Notes.Length >= 3
                    && !string.IsNullOrWhiteSpace(l.SayIt) && !string.IsNullOrWhiteSpace(l.Analogy) && !string.IsNullOrWhiteSpace(l.WhyItMatters);
                Check($"learn-lesson-complete-{l.Id}", complete);
                var q = l.Quiz;
                Check($"learn-quiz-valid-{l.Id}", q.Options.Length == 3 && q.Options.Distinct().Count() == 3 && q.Correct >= 0 && q.Correct < 3 && !string.IsNullOrWhiteSpace(q.Explanation));
                var order = CodingTrack.DisplayOrder(l);
                Check($"learn-order-permutation-{l.Id}", order.Order().SequenceEqual(Enumerable.Range(0, q.Options.Length)));
                Check($"learn-only-correct-answer-{l.Id}", Enumerable.Range(0, 3).Count(i => CodingTrack.IsCorrect(l, i)) == 1);
                string prompt = CodingTrack.TutorPrompt(l);
                Check($"learn-tutor-prompt-{l.Id}", prompt.Length <= 4000 && prompt.StartsWith(CodingTrack.TutorIntro) && prompt.Contains(l.Code), $"{prompt.Length}");
            }
            Check("learn-answer-not-always-first", lessons.Count(l => CodingTrack.DisplayOrder(l)[0] == l.Quiz.Correct) <= lessons.Length / 2);

            Lesson Get(string id) => CodingTrack.Find(id) ?? throw new InvalidOperationException("missing lesson " + id);
            string Answer(string id) => Get(id).Quiz.Options[Get(id).Quiz.Correct];
            Check("learn-functions-answer-computed", Answer("functions") == (10 * 3).ToString());
            Check("learn-functions-output-computed", Get("functions").Notes.Any(n => n.Meaning.Contains((240 * 5).ToString())));
            int listItems = Regex.Matches(Get("loops").Code.Split('\n')[0], "\"T-\\d+\"").Count;
            Check("learn-loops-answer-computed", listItems == 3 && Answer("loops").StartsWith("Three"), $"{listItems}");
            double weight = Sizing.WeightGiB(14, 4);
            Check("learn-ai-words-answer-matches-engine", Answer("ai-words").Contains($"{weight:F1}"), $"{weight}");
            Check("learn-ai-words-note-matches-engine", Get("ai-words").Notes.Any(n => n.Meaning.Contains($"{Math.Round(weight, 2)}")));
            Check("learn-ai-words-16bit-is-four-times", Near(Sizing.WeightGiB(14, 16), 4 * weight) && Get("ai-words").Quiz.Explanation.Contains("four times"));
            Check("learn-ai-words-say-it-matches-engine", Get("ai-words").SayIt.Contains($"{weight:F1} GiB"));
            Check("learn-sizing-link-default-14b", Get("ai-words").SizingLink is string link && Enum.TryParse<TShirtSize>(link, out var size)
                && Sizing.Profile(size).DefaultBillions == 14 && Get("ai-words").Code.Contains("parameters_billions = 14") && Get("ai-words").Code.Contains("bits = 4"));
            Check("learn-apis-loopback", Get("apis").Code.Contains("127.0.0.1") && Answer("apis") == "This computer");
            Check("learn-ai-code-matches-demo", Get("ai-code").Code.Contains("def prioritize_tickets(tickets):") && Answer("ai-code").Contains("Review and test"));
            Check("learn-never-executes", lessons.All(l => l.Code.Contains("subprocess") == false && l.Code.Contains("exec(") == false && l.Code.Contains("open(") == false));
            foreach (string term in PlatformGuide.ForbiddenClaims)
            {
                var hits = CodingTrack.AllText().Where(t => Regex.IsMatch(t, $@"\b{Regex.Escape(term)}", RegexOptions.IgnoreCase)).ToArray();
                Check($"learn-no-claim-{term.Replace(' ', '-')}", hits.Length == 0, string.Join(" | ", hits));
            }

            string dir = Path.Combine(options.RuntimeDirectory, "self-test-learning");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "learning.json");
            File.Delete(path);
            var fresh = LearningState.Load(path);
            Check("learn-store-missing-defaults", fresh.ShowTab && fresh.CompletedCount == 0 && fresh.LastLesson is null);
            var saved = new LearningState { ShowTab = false, LastLesson = "apis" };
            saved.Completed.Add("variables");
            saved.Completed.Add("apis");
            saved.Save(path);
            var loaded = LearningState.Load(path);
            Check("learn-store-round-trip", !loaded.ShowTab && loaded.LastLesson == "apis" && loaded.CompletedCount == 2 && loaded.Completed.SetEquals(["variables", "apis"]));
            File.WriteAllText(path, """{"show_tab":true,"last_lesson":"bogus","completed":["loops","bogus",7,null]}""");
            var filtered = LearningState.Load(path);
            Check("learn-store-drops-unknown", filtered.ShowTab && filtered.LastLesson is null && filtered.Completed.SetEquals(["loops"]));
            File.WriteAllText(path, "{not json");
            var corrupt = LearningState.Load(path);
            Check("learn-store-corrupt-defaults", corrupt.ShowTab && corrupt.CompletedCount == 0);
            File.WriteAllText(path, """{"show_tab":"no","completed":"loops"}""");
            var wrongTypes = LearningState.Load(path);
            Check("learn-store-wrong-types-defaults", wrongTypes.ShowTab && wrongTypes.CompletedCount == 0);
            File.WriteAllText(path, new string(' ', 70 * 1024) + "{}");
            Check("learn-store-oversize-defaults", LearningState.Load(path).ShowTab);
            Directory.Delete(dir, recursive: true);
        });

        Guard("stream", () =>
        {
            var token = RunEventParser.Parse("""{"type":"token","text":"Hello"}""");
            Check("parse-token", token.Kind == RunEventKind.Token && token.Text == "Hello");
            var activity = RunEventParser.Parse("""{"type":"activity","reasoning_characters":42,"text":"SECRET REASONING"}""");
            Check("activity-never-surfaces-text", activity.Kind == RunEventKind.Activity && activity.ReasoningCharacters == 42 && activity.Text is null);
            var done = RunEventParser.Parse("""{"type":"done","mode":"live","model":{"name":"Qwen2.5-Coder 14B","id":"qwen","provider":"CUDAExecutionProvider"},"ttft_ms":450,"total_ms":5000,"characters":900,"output_tokens":210,"tokens_per_second":42.0,"finish_reason":"STOP"}""");
            Check("parse-done-live", done.Metrics is { Mode: "live", Provider: "CUDAExecutionProvider", TtftMs: 450, OutputTokens: 210 });
            var rehearsal = RunEventParser.Parse("""{"type":"done","mode":"rehearsal","model":null,"ttft_ms":null,"total_ms":null,"characters":10,"output_tokens":null,"tokens_per_second":null,"finish_reason":"AUTHORED"}""");
            Check("parse-done-rehearsal-no-metrics", rehearsal.Metrics is { Mode: "rehearsal", TtftMs: null, TotalMs: null, Provider: null });
            Check("parse-error", RunEventParser.Parse("""{"type":"error","error":"boom"}""").Error == "boom");
            Check("parse-unknown-ignored", RunEventParser.Parse("""{"type":"future"}""").Kind == RunEventKind.Ignored);
            bool malformed = false;
            try { RunEventParser.Parse("not json"); } catch (JsonException) { malformed = true; }
            Check("parse-malformed-throws", malformed);
            using var status = JsonDocument.Parse("""{"app_id":"@app","token":"t","engine":{"state":"ready","detail":"d","model":{"name":"M","alias":"a","id":"i","provider":"CUDAExecutionProvider","reasoning":false},"models":[{"alias":"a","id":"i","label":"M","cached":true,"size_mb":9000}]},"device":{"installed_ram_bytes":137438953472,"os_visible_ram_bytes":68699033600,"gpu_memory":[{"name":"G","reported_budget_mib":64000}],"accelerators":["G"]},"assumptions":{"users":100}}""".Replace("@app", Brand.ServiceAppId));
            var snapshot = StatusParser.Parse(status.RootElement);
            Check("parse-status", snapshot.Engine.IsReady && snapshot.Device.PrimaryGpuBudgetMiB == 64000 && Near(snapshot.Device.InstalledGiB ?? 0, 128)
                && snapshot.Engine.Models[0].SizeMb == 9000);
        });

        Guard("business", () =>
        {
            Check("business-defaults-valid", BusinessCase.Validate(BusinessCase.Defaults) is null);
            Check("business-default-hours", Near(BusinessCase.ExpectedHours(BusinessCase.Defaults), 2640));
            var values = new Dictionary<string, double>(BusinessCase.Defaults);
            foreach (var (key, _, min, max, _) in BusinessCase.Fields)
            {
                Check($"business-{key}-min-valid", BusinessCase.Validate(new Dictionary<string, double>(values) { [key] = min }) is null);
                Check($"business-{key}-max-valid", BusinessCase.Validate(new Dictionary<string, double>(values) { [key] = max }) is null);
                Check($"business-{key}-below-invalid", BusinessCase.Validate(new Dictionary<string, double>(values) { [key] = min - 0.01 }) is not null);
                Check($"business-{key}-above-invalid", BusinessCase.Validate(new Dictionary<string, double>(values) { [key] = max + 0.01 }) is not null);
                Check($"business-{key}-nan-invalid", BusinessCase.Validate(new Dictionary<string, double>(values) { [key] = double.NaN }) is not null);
            }
            var missing = new Dictionary<string, double>(values);
            missing.Remove("premium");
            Check("business-missing-invalid", BusinessCase.Validate(missing) is not null);
            Check("business-extra-invalid", BusinessCase.Validate(new Dictionary<string, double>(values) { ["extra"] = 1 }) is not null);
        });

        Guard("evidence", () =>
        {
            var live = new RunRecord("brief", "T", "L", "P", "S", "live", "Answer", new RunMetrics("live", "Qwen2.5-Coder 14B", "id", "CUDAExecutionProvider", 450, 5000, 900, 210, 42, "STOP", 0),
                120, DateTimeOffset.Now, null);
            string liveJson = Evidence.Run(live, device, "test").ToJsonString();
            var liveNode = JsonNode.Parse(liveJson)!;
            Check("export-live-provenance", liveNode["metrics"]?["execution_provider"]?.GetValue<string>() == "CUDAExecutionProvider" && liveNode["source"]?.GetValue<string>() == "S"
                && liveNode["metric_caveat"] is not null && liveNode["device"]?["memory_views_note"] is not null);
            Check("export-live-reasoning-count-only", liveNode["private_reasoning_characters_withheld"]?.GetValue<long>() == 120 && !liveJson.Contains("reasoning_text"));
            Check("export-no-token-field", !HasProperty(liveNode, "token"));
            var rehearsal = live with { Mode = "rehearsal", Metrics = new RunMetrics("rehearsal", null, null, null, null, null, 10, null, null, "AUTHORED", null) };
            var rehearsalNode = JsonNode.Parse(Evidence.Run(rehearsal, device, "test").ToJsonString())!;
            Check("export-rehearsal-no-metrics", rehearsalNode["metrics"] is null && rehearsalNode["mode_label"]!.GetValue<string>().Contains("not model output"));
            var failed = live with { Error = "stopped", Metrics = null };
            Check("export-failed-no-metrics", JsonNode.Parse(Evidence.Run(failed, device, "test").ToJsonString())!["metrics"] is null);
            var sizing = Evidence.SizingExport(baseInput, Sizing.Estimate(baseInput), TShirtSize.L, BudgetSource.GpuDriverBudget);
            Check("export-sizing-caveat", sizing["caveat"]?.GetValue<string>() == Sizing.Caveat && sizing["result"]?["verdict"]?.GetValue<string>() == "Fits");
        });

        Guard("host", () =>
        {
            string? root = BackendHost.FindRoot(AppContext.BaseDirectory);
            Check("host-finds-project-root", root is not null && File.Exists(Path.Combine(root, "server.py")), root ?? "not found");
            if (root is not null)
                Check("host-python-candidates", BackendHost.PythonCandidates(root).Skip(BackendHost.ConfiguredPython() is null ? 0 : 1)
                    .All(p => p.EndsWith(Path.Combine(".venv", "Scripts", "python.exe"))));
            Check("assets-copied", File.Exists(AppIdentity.Asset(AppIdentity.IconFile)) && File.Exists(AppIdentity.Asset(AppIdentity.LogoFile)), AppIdentity.Asset(""));
        });

        Guard("edition", () =>
        {
            // Brand.cs is generated from edition.json (tools\Sync-Edition.ps1); a stale copy would mislabel the build.
            string path = Path.Combine(options.Root, "edition.json");
            Check("edition-manifest-present", File.Exists(path), path);
            if (!File.Exists(path)) return;
            using var manifest = JsonDocument.Parse(File.ReadAllText(path));
            var m = manifest.RootElement;
            string Field(string name, string? child = null)
            {
                if (!m.TryGetProperty(name, out var value)) return "";
                if (child is not null && (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(child, out value))) return "";
                return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            }
            Check("edition-schema", Field("schema") == "studio-edition/1", Field("schema"));
            Check("edition-tree-manifest-has-no-rules", !m.TryGetProperty("rules", out _) && !m.TryGetProperty("forbiddenTokens", out _));
            foreach (var (name, brand, expected) in new[]
            {
                ("id", Brand.EditionId, Field("id")),
                ("display-name", Brand.DisplayName, Field("displayName")),
                ("device", Brand.Device, Field("device")),
                ("window-title", Brand.WindowTitle, $"{Field("displayName")} - {Field("device")}"),
                ("mark", Brand.Mark, Field("mark")),
                ("mark-subtitle", Brand.MarkSubtitle, Field("markSubtitle")),
                ("code-name", Brand.CodeName, Field("codeName")),
                ("aumid", Brand.AppUserModelId, Field("appUserModelId")),
                ("service-app-id", Brand.ServiceAppId, Field("serviceAppId")),
                ("schema-prefix", Brand.SchemaPrefix, Field("schemaPrefix")),
                ("env-prefix", Brand.EnvPrefix, Field("envPrefix")),
            })
            {
                Check("edition-" + name, string.Equals(brand, expected, StringComparison.Ordinal), $"Brand.cs '{brand}' vs edition.json '{expected}'");
            }
            foreach (var (name, brand, expected) in new[]
            {
                ("accent", Brand.Accent, Field("accent", "base")),
                ("accent-deep", Brand.AccentDeep, Field("accent", "dark2")),
                ("accent-tint", Brand.AccentTint, Field("accent", "tint")),
            })
            {
                bool parses = true;
                try { _ = Ui.ParseHex(brand); } catch (FormatException) { parses = false; }
                Check("edition-" + name, parses && string.Equals(brand, expected, StringComparison.OrdinalIgnoreCase), $"Brand.cs '{brand}' vs edition.json '{expected}'");
            }
            Check("edition-assembly-is-code-name", typeof(Brand).Assembly.GetName().Name == Brand.CodeName && typeof(Brand).Namespace == Brand.CodeName,
                typeof(Brand).Assembly.GetName().Name ?? "");
            Check("edition-identity-wired", AppIdentity.AppUserModelId == Brand.AppUserModelId && AppIdentity.Title == Brand.WindowTitle
                && StatusParser.ExpectedAppId == Brand.ServiceAppId && BackendHost.PythonVariable == Brand.EnvPrefix + "PYTHON");
        });

        Guard("demos", () =>
        {
            Check("demo-known-ids-unique", DemoCatalog.KnownIds.Length == 12 && DemoCatalog.KnownIds.Distinct().Count() == 12);
            using var doc = JsonDocument.Parse("""
                {"demos":[
                  {"id":"meeting","title":"Meeting","category":"Listen","workload":"transcription","pitch":"p","proof":"q",
                   "input":{"kind":"audio","label":"Recording","media":"meeting.wav"},"source":"script","options":[],"prompt":"Summarise","prompt_label":"Instruction",
                   "editable":true,"rehearsal":true,"steps":[{"title":"Transcribe","role":"asr"},{"title":"Minutes","role":"chat"}]},
                  {"id":"vision","title":"Vision","category":"See","input":{"kind":"image","label":"Scan","media":"work-order.png"},"option_label":"Document",
                   "options":[{"id":"work-order","label":"Work order","prompt":"Read it","media":"work-order.png","rehearsal":true},{"id":"receipt","label":"Receipt","prompt":"Read receipt","media":"receipt.png","rehearsal":false}],
                   "default_option":"work-order","prompt":"Read it","editable":true,"rehearsal":true,"steps":[{"title":"Read","role":"vision"}]},
                  {"id":"code","title":"Code","category":"Build","input":{"kind":"text"},"prompt":"Write","editable":true,"rehearsal":true,"steps":[{"title":"Draft","role":"qwen2.5-coder-14b"}]},
                  {"title":"missing id is skipped"}],
                 "media":{"state":"ready","detail":""},
                 "engine":{"state":"ready","media":{"runtime":{"state":"active","detail":"ok"},"models":[
                   {"role":"asr","label":"Whisper","id":"w","available":true,"cached":true,"loaded":false,"size_mb":800},
                   {"role":"vision","label":"Qwen VL","id":"v","available":true,"cached":false,"loaded":false,"size_mb":5000}]}}}
                """);
            var catalog = DemoParser.ParseCatalog(doc.RootElement);
            var meeting = catalog.Find("meeting")!;
            var vision = catalog.Find("vision")!;
            var code = catalog.Find("code")!;
            Check("demo-parse-catalog", catalog.Demos.Count == 3 && meeting.InputKind == "audio" && meeting.InputMedia == "meeting.wav"
                && meeting.Steps.Count == 2 && catalog.RuntimeReady && catalog.MediaModels.Count == 2 && catalog.MediaModels[0].SizeMb == 800);
            Check("demo-parse-options", vision.Options.Count == 2 && vision.MediaFor("receipt") == "receipt.png" && vision.PromptFor("receipt") == "Read receipt"
                && vision.RehearsalFor("work-order") && !vision.RehearsalFor("receipt") && vision.MediaFor(null) == "work-order.png");

            var active = new ActiveModel("Phi", "chat", "phi", "CUDAExecutionProvider", false, null);
            var engine = new EngineState("ready", "", "chat", active, [new ModelChoice("qwen2.5-coder-14b", "q", "Qwen Coder", false, false, 9000, null)], null);
            Check("demo-blocker-no-engine", catalog.LiveBlocker(meeting, null) is not null);
            Check("demo-blocker-engine-loading", catalog.LiveBlocker(meeting, engine with { State = "loading" }) is not null);
            Check("demo-blocker-ready", catalog.LiveBlocker(meeting, engine) is null);
            Check("demo-blocker-media-model-not-cached", catalog.LiveBlocker(vision, engine)?.Contains("Qwen VL") == true);
            Check("demo-blocker-chat-model-not-cached", catalog.LiveBlocker(code, engine)?.Contains("Qwen Coder") == true);
            Check("demo-blocker-runtime-missing", (catalog with { RuntimeState = "missing", RuntimeDetail = "Run -Prepare" }).LiveBlocker(meeting, engine) == "Run -Prepare");
            Check("demo-blocker-media-rendering", (catalog with { MediaState = "rendering" }).LiveBlocker(meeting, engine) is not null
                && (catalog with { MediaState = "rendering" }).LiveBlocker(catalog.Find("code")! with { Steps = [new DemoStepInfo("Draft", "chat")] }, engine) is null);

            var step = DemoParser.ParseEvent("""{"type":"step","index":1,"state":"done","model":"Whisper","detail":"d","metrics":[{"label":"Audio","value":"42 s"}]}""");
            Check("demo-parse-step", step is { Kind: DemoEventKind.Step, Index: 1, State: "done", Model: "Whisper" } && step.Metrics![0].Value == "42 s");
            var activity = DemoParser.ParseEvent("""{"type":"activity","index":0,"reasoning_characters":321}""");
            Check("demo-parse-activity-count-only", activity is { Kind: DemoEventKind.Activity, ReasoningCharacters: 321, Text: null });
            var done = DemoParser.ParseEvent("""{"type":"done","mode":"live","total_ms":1234,"checks_passed":3,"checks_total":4,"steps_failed":0}""");
            Check("demo-parse-done", done.Summary is { Mode: "live", TotalMs: 1234, ChecksPassed: 3, ChecksTotal: 4, AllPassed: false });
            Check("demo-parse-check", DemoParser.ParseEvent("""{"type":"check","label":"Owner","value":"Dana","pass":true,"detail":"x"}""").Check is { Pass: true, Value: "Dana" });
            Check("demo-parse-unknown-ignored", DemoParser.ParseEvent("""{"type":"later"}""").Kind == DemoEventKind.Ignored);

            byte[] wav = new byte[44 + 32000];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
            Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
            BitConverter.GetBytes(16).CopyTo(wav, 16);
            BitConverter.GetBytes(32000).CopyTo(wav, 28);
            Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
            BitConverter.GetBytes(32000).CopyTo(wav, 40);
            Check("demo-wav-seconds", DemoParser.WavSeconds(wav) is double secs && Near(secs, 1), $"{DemoParser.WavSeconds(wav)}");
            Check("demo-wav-rejects-garbage", DemoParser.WavSeconds(new byte[64]) is null);

            var stepRecord = new DemoStepRecord("Transcribe", "Whisper") { State = "done", ReasoningCharacters = 77 };
            stepRecord.Text.Append("Transcript");
            var run = new DemoRunRecord(meeting, "live", null, "Summarise", false, DateTimeOffset.Now, [stepRecord],
                [new DemoCheck("Owner", "Dana", true, "x")], new DemoSummary("live", 1234, 1, 1, 0, null), null);
            string json = DemoEvidence.Run(run, device, "test").ToJsonString();
            var node = JsonNode.Parse(json)!;
            Check("demo-export-schema", node["schema"]?.GetValue<string>() == Brand.SchemaPrefix + ".demo-evidence/1" && node["outcome"]?.GetValue<string>() == "1 of 1 checks passed");
            Check("demo-export-no-token-or-reasoning", !HasProperty(node, "token") && !json.Contains("reasoning_text")
                && node["steps"]?[0]?["private_reasoning_characters_withheld"]?.GetValue<long>() == 77);
            var rehearsalRun = run with { Mode = "rehearsal", Checks = [], Summary = new DemoSummary("rehearsal", null, 0, 0, 0, null) };
            Check("demo-export-rehearsal-label", JsonNode.Parse(DemoEvidence.Run(rehearsalRun, device, "test").ToJsonString())!["mode_label"]!.GetValue<string>().Contains("not model output")
                && rehearsalRun.Outcome == "Rehearsal shown");
            var gradedWrong = run with { Summary = new DemoSummary("live", 900, 1, 2, 0, null) };
            var stepFailed = run with { Summary = new DemoSummary("live", 900, 0, 0, 1, null) };
            Check("demo-graded-wrong-still-ran-end-to-end", gradedWrong.RanEndToEnd && !gradedWrong.Passed
                && !stepFailed.RanEndToEnd && !(run with { Error = "x" }).RanEndToEnd && run.RanEndToEnd && run.Passed);
        });

        Guard("starter-writer", () =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "hybrid-studio-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                string ok = Path.Combine(temp, "ok");
                using var good = JsonDocument.Parse("""{"files":{"chat.py":"print('hi')\n","docs/a.md":"# A\n"}}""");
                Check("starter-writes-files", MainWindow.WriteStarter(ok, good.RootElement) == 2
                    && File.ReadAllText(Path.Combine(ok, "docs", "a.md")) == "# A\n" && File.ReadAllBytes(Path.Combine(ok, "chat.py"))[0] == (byte)'p');
                bool refused;
                try { MainWindow.WriteStarter(ok, good.RootElement); refused = false; }
                catch (IOException) { refused = true; }
                Check("starter-refuses-existing-folder", refused);
                string[] unsafePaths = ["../escape.py", "..\\escape.py", "C:\\Windows\\escape.py", "/escape.py", "docs/../../escape.py", "", "docs//a.py"];
                for (int i = 0; i < unsafePaths.Length; i++)
                {
                    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { files = new Dictionary<string, string> { ["safe.py"] = "x", [unsafePaths[i]] = "x" } }));
                    bool rejected;
                    try { MainWindow.WriteStarter(Path.Combine(temp, "bad" + i), doc.RootElement); rejected = false; }
                    catch (FormatException) { rejected = true; }
                    Check($"starter-rejects-unsafe-path-{i}", rejected && !Directory.Exists(Path.Combine(temp, "bad" + i)) && !File.Exists(Path.Combine(temp, "escape.py")), unsafePaths[i]);
                }
                using var empty = JsonDocument.Parse("""{"files":{}}""");
                bool emptyRejected;
                try { MainWindow.WriteStarter(Path.Combine(temp, "empty"), empty.RootElement); emptyRejected = false; }
                catch (FormatException) { emptyRejected = true; }
                Check("starter-rejects-empty", emptyRejected);
            }
            finally
            {
                try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch (IOException) { }
            }
        });

        Guard("toolkit", () =>
        {
            var tools = Toolkit.Tools;
            Check("toolkit-five-unique-tools", tools.Length == 5 && tools.Select(t => t.Id).Distinct().Count() == 5
                && tools.All(t => t.Repository.StartsWith("github.com/npuneil/", StringComparison.Ordinal) && t.Boundary.Length > 0));
            Check("toolkit-no-unbacked-claims", tools.All(t => !Regex.IsMatch(t.Summary + " " + t.Story, @"\bsav(e|es|ing|ings)\b|guarantee|TOPS|battery", RegexOptions.IgnoreCase)));
            string temp = Path.Combine(Path.GetTempPath(), "hybrid-studio-toolkit-" + Guid.NewGuid().ToString("N"));
            try
            {
                string studio = Path.Combine(temp, "Studio");
                Directory.CreateDirectory(studio);
                var bench = tools.Single(t => t.Id == "bench");
                bool configured = Environment.GetEnvironmentVariable(Brand.EnvPrefix + bench.EnvKey) is { Length: > 0 };
                if (!configured)
                {
                    var missing = Toolkit.Inspect(bench, studio);
                    Check("toolkit-missing-links-to-repo", !missing.Found && missing.Status == "Not found" && missing.Actions.Count == 1
                        && missing.Actions[0].Target == "https://" + bench.Repository && missing.Detail?.Contains(Brand.EnvPrefix + bench.EnvKey) == true);
                    string benchRoot = Path.Combine(temp, bench.Folder);
                    Directory.CreateDirectory(benchRoot);
                    File.WriteAllText(Path.Combine(benchRoot, bench.Marker), "");
                    var noResults = Toolkit.Inspect(bench, studio);
                    Check("toolkit-found-without-results", noResults.Found && noResults.Status == "No results yet" && noResults.Actions.Single().Id == "folder");
                    Directory.CreateDirectory(Path.Combine(benchRoot, "results"));
                    File.WriteAllText(Path.Combine(benchRoot, "results", "index.html"), "<html></html>");
                    var withResults = Toolkit.Inspect(bench, studio);
                    Check("toolkit-opens-existing-results", withResults.Status == "Found" && withResults.Actions[0] is { Id: "report", Primary: true }
                        && withResults.Actions[0].Target == Path.Combine(benchRoot, "results", "index.html"));
                }
                bool blocked;
                try { Toolkit.Open("https://example.com/not-allowed"); blocked = false; }
                catch (FileNotFoundException) { blocked = true; }
                Check("toolkit-open-rejects-unknown-targets", blocked);
            }
            finally
            {
                try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch (IOException) { }
            }
        });

        int failedCount = results.Count(r => !r.Passed);
        var report = new JsonObject
        {
            ["passed"] = failedCount == 0,
            ["total"] = results.Count,
            ["failed"] = failedCount,
            ["completed_at"] = DateTimeOffset.Now.ToString("O"),
            ["results"] = new JsonArray(results.Select(r => (JsonNode?)new JsonObject { ["name"] = r.Name, ["passed"] = r.Passed, ["detail"] = r.Detail }).ToArray()),
        };
        try
        {
            Directory.CreateDirectory(options.RuntimeDirectory);
            File.WriteAllText(Path.Combine(options.RuntimeDirectory, "self-test.json"), report.ToJsonString(Evidence.Options));
        }
        catch (IOException) { }
        foreach (var r in results.Where(r => !r.Passed)) Console.WriteLine($"FAIL {r.Name}: {r.Detail}");
        Console.WriteLine($"Self-test: {results.Count - failedCount}/{results.Count} passed");
        return failedCount == 0 ? 0 : 1;
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

    private static bool HasProperty(JsonNode? node, string name) => node switch
    {
        JsonObject o => o.Any(p => p.Key == name || HasProperty(p.Value, name)),
        JsonArray a => a.Any(n => HasProperty(n, name)),
        _ => false
    };
}
