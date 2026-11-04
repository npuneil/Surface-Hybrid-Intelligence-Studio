using System.Text;
using System.Text.Json;
using Forge.Cli;
using Forge.Core.Harness;
using SurfaceDeviceKit;
using SurfaceDeviceKit.FoundryLocal;

// forge-cli: the Offline PPTX Forge without the window. It drives the same ForgeSession the app drives.
internal static class Program
{
    private const string Usage = """
        forge-cli VERB [options]

        Verbs
          --probe                  Detect this PC and show what it can do: lanes, capabilities and the model plan for every workload
          --list                   List the simulated Surface devices (for --simulate and --emulate)
          --briefs                 List the bundled briefs
          --plan                   What this PC can run for the Forge, from the live Foundry Local catalog, with the settings and wait for each model
          --brief ID|FILE          Write a deck from a bundled brief or a brief .json file
          --draft TEXT             Turn a request in plain words into a brief
          --render SPEC.json       Render a saved deck spec to .pptx and previews (no model)
          --self-test              Check fonts, briefs, the renderer, the .pptx writer and this PC's plan (no model)
          --live-test [BRIEF]      End to end with a real model on this PC: start, load, measure, write a deck, cancel (default brief zava_technical)

        Options
          --simulate ID            Show another Surface device's capabilities and plan, with --probe or --plan only
          --emulate ID             Write the deck with the model that device would use, as near as this PC can run it
          --model ALIAS --lane L   Force one model on one lane (npu, gpu or cpu)
          --mode guided|arc|oneshot   How the deck is written; arc and oneshot need a large model with a big window
          --repairs N              Repair passes, 0 to 3
          --guidance on|off        Constrained decoding
          --theme KEY              microsoft, surface or surface_ultra (default: the brief's choice)
          --out DIR                Where decks are saved (default: Documents\Offline PPTX Forge)
          --no-preview             Skip the slide PNGs
          --calibrate | --no-calibrate   Measure one slide now, or never (default: when this model has no recent measurement)
          --retest-guidance        Ask for constrained decoding again even if the model refused it before
          --allow-download         Fetch missing execution providers and models (needs internet; off by default)
          --client TEXT  --pitch sales|technical  --provenance  --brief-out FILE   For --draft
          --no-cancel              Skip the cancel check in --live-test
          --data DIR               Where the speed log lives (default: %LOCALAPPDATA%\OfflinePptxForge)
          --app-data DIR           Where Foundry Local keeps its data
          --verbose                Show the engine's own log
          --json                   Machine-readable output

        A deck always states the PC it was made on and the model that wrote it. --simulate and --emulate never change that.
        Exit codes: 0 done, 1 a check failed, 2 bad usage, 3 could not run, 130 cancelled.
        """;

    private static async Task<int> Main(string[] argv)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        try
        {
            if (argv.Length == 0)
            {
                Console.WriteLine(Usage);
                return 2;
            }
            var args = new CliArgs(argv);
            if (args.Flag("--help") || args.Flag("-h") || args.Flag("-?"))
            {
                Console.WriteLine(Usage);
                return 0;
            }

            bool probe = args.Flag("--probe"), list = args.Flag("--list"), briefs = args.Flag("--briefs");
            bool plan = args.Flag("--plan"), selfTest = args.Flag("--self-test");
            string? liveBrief = args.OptionalValue("--live-test", out bool liveTest);
            string? brief = args.Value("--brief"), draft = args.Value("--draft"), render = args.Value("--render");
            var options = Options.Parse(args);
            args.RequireEmpty(Usage);

            int verbs = new[] { probe, list, briefs, plan, selfTest, liveTest, brief is not null, draft is not null, render is not null }.Count(v => v);
            if (verbs != 1) throw new ArgumentException(verbs == 0 ? $"Say what to do.\n\n{Usage}" : $"Choose one verb.\n\n{Usage}");

            var devices = new DeviceDetectionService();
            if (options.Simulate is not null)
            {
                if (!probe && !plan)
                    throw new ArgumentException("--simulate changes the capability view only, so it works with --probe and --plan. A deck is always made on this PC: use --emulate ID to write one with the model that device would use.");
                if (SimulatedProfiles.Find(options.Simulate) is null) throw new ArgumentException($"No simulated device '{options.Simulate}'. Try --list.");
                devices.Simulate(options.Simulate);
            }
            if (options.Emulate is not null && SimulatedProfiles.Find(options.Emulate) is null) throw new ArgumentException($"No simulated device '{options.Emulate}'. Try --list.");

            var commands = new Commands(options, new Ui(options.Json), devices, cancel.Token);
            if (probe) return commands.Probe();
            if (list) return commands.ListDevices();
            if (briefs) return commands.Briefs();
            if (selfTest) return commands.SelfTest();
            if (render is not null) return commands.Render(render);
            if (plan) return await commands.PlanAsync();
            if (brief is not null) return await commands.BriefAsync(brief);
            if (draft is not null) return await commands.DraftAsync(draft);
            return await commands.LiveTestAsync(liveBrief);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException
                                      or DownloadRequiredException or ModelLoadException or HarnessException or ModelOutputException or PromptTooLongException)
        {
            Console.Error.WriteLine(e.Message);
            if (e is ModelLoadException load)
                foreach (string problem in load.Problems.Where(p => p != e.Message)) Console.Error.WriteLine("  " + problem);
            return 3;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 4;
        }
    }
}
