# Surface device kit

Reads the Surface (or any Windows PC) an app runs on, classifies it, and plans which local model to run on which chip
(NPU, GPU or CPU) for each kind of work. The Offline PPTX Forge in this repo and the Surface Hybrid Intelligence Sales
Kit are built on it, so any demo that uses it shows the same answers for the same Surface.

Probing needs no admin rights, no WMI and no network. It reads the registry, the list of present devices (SetupAPI)
and the memory API, plus the free space on the model drive and whether PowerPoint is installed. The only process it may
start is `nvidia-smi`, and only when the registry gives no video memory figure for an NVIDIA GPU. The rules live in one
file, `catalog/device-catalog.json`, which both the C# kit and the Python port read.

## What it answers

- **Profile:** product name, processor, memory, GPUs and NPU, OS architecture (and whether the app runs under x64
  emulation), PC class (Builder PC, Copilot+ PC class, AI PC with an NPU, AI PC with an NVIDIA GPU or Windows 11 PC),
  the lanes a model can run on (npu, gpu, cpu) with their execution providers and memory budgets, and a capability list
  for the UI.
- **Plans** for each workload: `structured-json`, `chat`, `code`, `reasoning`, `speech-to-text`, `embeddings` and
  `vision`. Each plan names the primary lane and model, and a ladder of smaller models per lane to fall back to.
- **Package:** which of the four silicon packages the PC takes (`SiliconPackage.For`). Apps ship one package per
  family because the native AI runtimes differ: Snapdragon-arm64 (QNN for the NPU), Intel-x64 (also AMD; OpenVINO and
  DirectML), NVIDIA-arm64 (Surface Laptop Ultra, CUDA) and NVIDIA-x64 (Surface Laptop Studio, CUDA).
- **Simulation:** 25 sample devices, from Surface Go 3 to Surface Laptop Ultra, so one PC can show what another
  Surface would get. Simulation changes what is shown and planned, never what really runs.

The PC class is the kit's reading of the hardware, not a certification: Copilot+ PC features depend on the exact SKU
(for example, Snapdragon X2 Plus Surfaces are Copilot+ PCs only with 16 GB or more), so confirm on the exact SKU.

## Layout

| Path | What it is |
| --- | --- |
| `catalog/device-catalog.json` | The rules: silicon, memory tiers, lanes, execution providers, models and workloads |
| `dotnet/SurfaceDeviceKit` | The C# kit (`net10.0`): probe, classifier, planner, capability matrix, sample devices, `SiliconPackage` |
| `dotnet/SurfaceDeviceKit.FoundryLocal` | Runs the plans with Foundry Local in-process (Windows): execution providers, the live catalog, downloads, loading with fallback, measured generation |
| `dotnet/SurfaceDeviceKit.Cli` | `surface-device`: prints the report for this PC or a sample device |
| `dotnet/SurfaceDeviceKit.FoundryLocal.Cli` | `surface-engine`: resolves plans against Foundry Local on this PC and runs them |
| `python/surface_devicekit` | The Python port, with the same answers |
| `vectors/simulated-profiles.json` | The sample devices |
| `vectors/golden-profiles.json` | Expected answers that pin the C# kit and the Python port together |

## Use it from C#

Reference the kit, and the Foundry Local adapter if the app loads models:

```xml
<ProjectReference Include="..\devicekit\dotnet\SurfaceDeviceKit\SurfaceDeviceKit.csproj" />
<ProjectReference Include="..\devicekit\dotnet\SurfaceDeviceKit.FoundryLocal\SurfaceDeviceKit.FoundryLocal.csproj" />
```

```csharp
using SurfaceDeviceKit;

var devices = DeviceKit.Service;                  // probes this PC once, on first use
var profile = devices.Detected.Profile;
Console.WriteLine($"{profile.Headline}: {profile.PcClassLabel}");
Console.WriteLine(SiliconPackage.For(profile));   // for example NVIDIA-arm64

devices.Simulate("snapdragon-x-elite-16");        // show and plan for another Surface
Console.WriteLine(devices.Plan("chat").Summary);  // Effective: the simulated device if set, else this PC
devices.ClearSimulation();
```

Run a plan on this PC:

```csharp
using SurfaceDeviceKit.FoundryLocal;

await using var engine = new FoundryEngine(options: new EngineOptions { AppName = "MyDemo" });
await engine.InitializeAsync(allowDownload: true);
var loaded = await engine.LoadBestAsync("chat", allowDownload: true);   // falls back down the ladder if needed
var reply = await engine.GenerateAsync(loaded.Model, "Answer in one sentence.", "What is an NPU?");
Console.WriteLine($"{reply.Text}\n{reply.Summary}");                    // model, chip, first words, tokens per second
```

`AppName` is the folder under `%LOCALAPPDATA%` for the app's logs, models and execution providers. A model that
another Surface demo app already downloaded (`EngineOptions.SharedAppData`) is reused rather than downloaded again.
Set `SURFACE_DEVICEKIT_SIMULATE` to a sample device id to start an app in simulation.

## Use it from Python

```python
from surface_devicekit import DeviceDetectionService, to_dict

service = DeviceDetectionService()
report = service.effective          # the simulated device if one is set, otherwise this PC
payload = to_dict(report)           # JSON-ready, shaped like the C# output
```

## Command line

```powershell
# This PC, a sample device, or one workload's plan (C#; the Python port takes the same options)
dotnet run --project devicekit\dotnet\SurfaceDeviceKit.Cli -- --list
dotnet run --project devicekit\dotnet\SurfaceDeviceKit.Cli -- --simulate snapdragon-x2-plus-8 --plan chat
cd devicekit\python; python -m surface_devicekit --json

# What Foundry Local can really run here, and an end-to-end check (loads models)
dotnet run --project devicekit\dotnet\SurfaceDeviceKit.FoundryLocal.Cli -- --plan
dotnet run --project devicekit\dotnet\SurfaceDeviceKit.FoundryLocal.Cli -- --live-test --allow-download
```

`surface-device --raw` captures a PC's raw hardware as JSON, and `--raw-file FILE` classifies such a capture on
another PC, which helps when a tester's Surface is classified unexpectedly.

## Test

```powershell
dotnet run --project devicekit\dotnet\SurfaceDeviceKit.Cli -- --self-test
dotnet run --project devicekit\dotnet\SurfaceDeviceKit.Cli -- --check-golden devicekit\vectors\golden-profiles.json
cd devicekit\python; python -m unittest discover -s tests
```

After a deliberate rule change in the catalog, regenerate the golden vectors with `--golden` and check that the Python
port still matches them.

## Copies in other repos

The Surface Hybrid Intelligence Sales Kit carries a copy: `src/SurfaceDeviceKit` (with the catalog and the sample
devices in its `data` folder) and `src/SurfaceDeviceKit.FoundryLocal`. Change the kit here first, then copy it across,
so both apps keep giving the same answers.
