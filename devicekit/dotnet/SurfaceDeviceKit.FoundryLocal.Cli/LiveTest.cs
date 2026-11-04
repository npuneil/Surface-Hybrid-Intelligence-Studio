using System.Text.Json;
using SurfaceDeviceKit;
using SurfaceDeviceKit.FoundryLocal;

/// <summary>
/// End-to-end check of the adapter on the PC it runs on. A step that cannot run here (a model not downloaded, no second
/// model to fall back to) is reported as SKIP with the reason; it is never counted as a pass.
/// </summary>
static class LiveTest
{
    private const string JsonSystem = "You answer with one JSON object and nothing else.";
    private const string JsonPrompt = "Give the city Paris and its country as JSON with the keys city and country.";

    internal static async Task<int> RunAsync(FoundryEngine engine, DeviceDetectionService service, bool allowDownload, Action<string> say)
    {
        int passed = 0, failed = 0, skipped = 0;
        void Pass(string name, string detail = "") { passed++; say($"PASS  {name}{(detail.Length > 0 ? "  - " + detail : "")}"); }
        void Fail(string name, string detail) { failed++; say($"FAIL  {name}  - {detail}"); }
        void Skip(string name, string why) { skipped++; say($"SKIP  {name}  - {why}"); }
        void Check(string name, bool ok, string detail = "") { if (ok) Pass(name, detail); else Fail(name, detail); }
        async Task Step(string name, Func<Task> body)
        {
            try { await body(); }
            catch (Exception exception) { Fail(name, exception.GetType().Name + ": " + exception.Message); }
        }

        var hardware = service.Detected.Profile;
        ResolvedPlan? plan = null;
        LoadedModel? model = null;

        await Step("Foundry Local starts", async () =>
        {
            await engine.InitializeAsync(allowDownload, say);
            Check("Foundry Local starts", engine.State == EngineState.Ready, engine.Status);
            say(Show.Providers(engine));
        });
        if (engine.State != EngineState.Ready) return Finish();

        await Step("Each lane has a provider", () =>
        {
            foreach (var lane in hardware.UsableLanes.Where(l => l.Id != "cpu"))
            {
                var usable = lane.Eps.Where(engine.UsableEps.Contains).ToList();
                if (usable.Count > 0) Pass($"The {lane.Id.ToUpperInvariant()} lane has a registered provider", string.Join(", ", usable));
                else if (!allowDownload) Skip($"The {lane.Id.ToUpperInvariant()} lane has a registered provider", $"none of {string.Join(", ", lane.Eps)} is registered; rerun with --allow-download");
                else Fail($"The {lane.Id.ToUpperInvariant()} lane has a registered provider", $"none of {string.Join(", ", lane.Eps)} could be registered");
            }
            return Task.CompletedTask;
        });

        await Step("Live plans agree with the kit", async () =>
        {
            var disagreements = new List<string>();
            foreach (var (id, kit) in service.Detected.Plans)
            {
                var live = await engine.ResolveAsync(id);
                say($"{live.Title}: " + (live.Options.Count == 0 ? "nothing can run here" : string.Join(" > ", live.Options.Take(4).Select(o => $"{o.Title} [{o.LaneLabel}{(o.Cached ? "" : ", download")}]"))));
                if (id == "structured-json") plan = live;
                if (live.Primary is { } first)
                {
                    bool onKitLadder = kit.Lanes.FirstOrDefault(l => l.Lane == first.Lane)?.Ladder.Any(m => m.Alias == first.Alias) == true;
                    if (!onKitLadder) disagreements.Add($"{id}: {first.Alias} on {first.Lane} is not on the kit's ladder");
                }
            }
            Check("Every live first choice is on the kit's ladder for its lane", disagreements.Count == 0, string.Join("; ", disagreements));
        });
        if (plan is null || plan.Options.Count == 0)
        {
            Fail("A model for structured-json can run here", plan is null ? "no plan" : string.Join(" ", plan.Notes));
            return Finish();
        }

        if (!allowDownload && plan.PrimaryOnThisPc is null)
        {
            Skip("Load and answer", $"nothing for structured-json is on this PC yet (best: {plan.Primary!.Summary}); rerun with --allow-download");
            return Finish();
        }

        await Step("Loads the best model that is on this PC", async () =>
        {
            var outcome = await engine.LoadBestAsync("structured-json", allowDownload, say);
            model = outcome.Model;
            Check("Loads the best model that is on this PC", true,
                $"{model.Label} on the {model.Device} ({model.Ep}); load {model.LoadSeconds:0.0} s, warm-up {model.WarmSeconds:0.0} s{(outcome.FellBack ? "; fell back from " + plan.Primary!.Title : "")}");
        });
        if (model is null) return Finish();

        await Step("Answers in JSON", async () =>
        {
            var answer = await engine.GenerateAsync(model, JsonSystem, JsonPrompt, new GenOptions { MaxTokens = 80, Temperature = 0 });
            say("  " + answer.Text.Replace("\n", " "));
            Check("Answers", answer.Text.Length > 0 && !answer.Cancelled, answer.Summary);
            Check("The answer is JSON with city and country", TryReadJson(answer.Text, out var doc) && doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("city", out _) && doc.RootElement.TryGetProperty("country", out _), answer.Text.Length > 120 ? answer.Text[..120] : answer.Text);
        });

        await Step("Measures speed", async () =>
        {
            var longer = await engine.GenerateAsync(model, "", "List the planets of the solar system, one short fact each.", new GenOptions { MaxTokens = 160, Temperature = 0 });
            Check("Reports a decode speed", longer.TokensPerSecond > 0, $"{longer.Summary}{(longer.TokensMeasured ? "" : " (token count approximated from streamed pieces)")}");
        });

        await Step("Preflight", async () =>
        {
            var small = await engine.PreflightAsync(model, "", "Reply with OK.", 16);
            string big = string.Join(' ', Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 14000));
            var large = await engine.PreflightAsync(model, "", big, 16);
            if (small is null || large is null) { Skip("Preflight measures the prompt", "the runtime did not report token counts for this model"); return; }
            say($"  short prompt: {small.PromptTokens} tokens, fits={small.Fits}; large prompt: {large.PromptTokens} tokens, fits={large.Fits}, context limit {large.ContextLimitTokens}");
            Check("Preflight counts tokens and sees a prompt that does not fit", small.Fits && large.PromptTokens > small.PromptTokens * 100 && (large.Fits == (large.DeficitTokens <= 0)),
                $"context limit {large.ContextLimitTokens} tokens (model reports {(model.ContextTokens?.ToString() ?? "no value")})");
        });

        await Step("Cancel", async () =>
        {
            using var cts = new CancellationTokenSource();
            int pieces = 0;
            var stopped = await engine.GenerateAsync(model, "", "Write a long story about a lighthouse keeper.", new GenOptions { MaxTokens = 400, Temperature = 0.2f },
                _ => { if (++pieces == 5) cts.Cancel(); }, cts.Token);
            Check("Cancelling returns what had arrived", stopped.Cancelled && stopped.Text.Length > 0 && stopped.OutputTokens < 300, $"{stopped.OutputTokens} tokens before the stop");
            var again = await engine.GenerateAsync(model, JsonSystem, JsonPrompt, new GenOptions { MaxTokens = 80, Temperature = 0 });
            Check("It answers again after a cancel", again.Text.Length > 0 && !again.Cancelled, again.Summary);
        });

        string firstChoice = plan.Primary!.VariantId;
        await Step("Fallback", async () =>
        {
            engine.Exclude(firstChoice);
            try
            {
                await engine.UnloadAsync(model);
                var next = await engine.LoadBestAsync("structured-json", allowDownload, say);
                Check("With the first choice ruled out, the next option starts", next.Model.Id != firstChoice,
                    $"{next.Model.Label} on the {next.Model.Device} ({next.Model.Ep})");
                var reply = await engine.GenerateAsync(next.Model, JsonSystem, JsonPrompt, new GenOptions { MaxTokens = 80, Temperature = 0 });
                Check("The fallback answers", reply.Text.Length > 0, reply.Summary);
                await engine.UnloadAsync(next.Model);
            }
            catch (ModelLoadException exception)
            {
                Skip("With the first choice ruled out, the next option starts", "no other option is on this PC: " + exception.Message);
            }
            finally { engine.Include(firstChoice); }
        });

        await Step("CPU lane", async () =>
        {
            var smallestCpu = (await engine.ListCatalogAsync()).Where(m => m.Device == "CPU" && m.Cached && m.IsChat).OrderBy(m => m.FileGb).FirstOrDefault();
            if (smallestCpu is null) { Skip("The CPU lane runs a model", "no CPU chat model is on this PC yet; rerun with --allow-download"); return; }
            var (forced, why) = await engine.ResolveModelAsync(smallestCpu.Alias, "cpu");
            if (forced is null) { Fail("The CPU lane runs a model", why); return; }
            var cpu = await engine.LoadAsync(forced, allowDownload, say);
            var reply = await engine.GenerateAsync(cpu, JsonSystem, JsonPrompt, new GenOptions { MaxTokens = 80, Temperature = 0 });
            Check("The CPU lane runs a model", reply.Text.Length > 0 && cpu.Device == "CPU", reply.Summary);
            await engine.UnloadAsync(cpu);
        });

        await Step("Restart", async () =>
        {
            await engine.RestartAsync(allowDownload, say);
            Check("Foundry Local restarts", engine.State == EngineState.Ready, engine.Status);
            var again = await engine.ResolveAsync("structured-json");
            Check("The plan is the same after a restart", again.Primary?.VariantId == plan.Primary?.VariantId, again.Primary?.Summary ?? "no plan");
            var outcome = await engine.LoadBestAsync("structured-json", allowDownload, say);
            var reply = await engine.GenerateAsync(outcome.Model, JsonSystem, JsonPrompt, new GenOptions { MaxTokens = 80, Temperature = 0 });
            Check("It answers after a restart", reply.Text.Length > 0, reply.Summary);
        });

        return Finish();

        int Finish()
        {
            engine.UnloadAllAsync().GetAwaiter().GetResult();
            say($"Live test on {hardware.Label}: {passed} passed, {failed} failed, {skipped} skipped");
            return failed == 0 ? 0 : 1;
        }
    }

    private static bool TryReadJson(string text, out JsonDocument doc)
    {
        string t = text.Trim();
        if (t.StartsWith("```"))
        {
            int open = t.IndexOf('\n');
            int close = t.LastIndexOf("```", StringComparison.Ordinal);
            if (open >= 0 && close > open) t = t[(open + 1)..close].Trim();
        }
        try { doc = JsonDocument.Parse(t); return true; }
        catch (JsonException) { doc = null!; return false; }
    }
}
