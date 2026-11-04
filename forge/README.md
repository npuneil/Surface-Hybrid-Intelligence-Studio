# Offline PPTX Forge

A standalone Windows app (WinUI 3) that writes PowerPoint decks with a model running on the Surface itself, on the NPU,
GPU or CPU. It needs no network once the model is on the PC, no Python and no server, and keeps its own folders:
decks go to `Documents\Offline PPTX Forge` and its speed log and logs to `%LOCALAPPDATA%\OfflinePptxForge`.

It is built on the [Surface device kit](../devicekit/README.md): the kit reads the Surface and plans the model, Foundry
Local runs it in-process, and the Forge drafts, checks, repairs, grades and renders the deck.

## Which model writes the decks

Every Surface gets its own choice. For the Forge's workload (`structured-json`: small JSON objects a model must keep
valid), the kit walks the catalog's models strongest first on each lane and keeps those whose class the lane allows and
whose weights fit the lane's memory budget; the device's preferred lane gives the recommended model. The app then
checks that against Foundry Local's live catalog on the PC, and falls back down the plan if a model will not load. This
is the Forge's own `--list` output for the sample devices:

| Surface | Package | Decks are written by |
| --- | --- | --- |
| Surface Laptop 7 and Surface Pro 11 (Snapdragon X Plus or X Elite, 16 to 64 GB) | Snapdragon-arm64 | Qwen2.5 7B on the NPU |
| Snapdragon X2 Plus and X2 Elite Surfaces, 16 GB and more | Snapdragon-arm64 | Qwen2.5 7B on the NPU |
| Snapdragon X2 Plus Surface, 8 GB | Snapdragon-arm64 | Qwen2.5 0.5B on the NPU |
| Surface Pro 9 5G (Microsoft SQ3, 16 GB) | Snapdragon-arm64 | Qwen2.5 7B on the GPU |
| Intel Core Ultra 200V and Series 3, 16 to 64 GB | Intel-x64 | Qwen2.5 7B on the NPU |
| Surface Laptop 6 (Core Ultra 7 165H), Surface Pro 9 (Core i7-1255U), Surface Laptop 4 (AMD Ryzen), 16 GB | Intel-x64 | Qwen2.5 7B on the GPU |
| Surface Go 3 (Pentium Gold, 8 GB) | Intel-x64 | Qwen2.5 0.5B on the CPU |
| Surface Laptop Studio 2 (RTX 4050, 16 GB) | NVIDIA-x64 | Phi-4 mini on the GPU |
| Surface Laptop Studio 2 (RTX 4060 or RTX 2000 Ada, 32 or 64 GB), RTX laptops with 24 GB or more | NVIDIA-x64 | Qwen2.5 7B on the GPU |
| Surface Laptop Ultra (RTX Spark N1X) | NVIDIA-arm64 | Qwen2.5-Coder 14B on the GPU |
| A Snapdragon Surface running an x64 package under emulation | Snapdragon-arm64 (it is running an x64 one) | Phi-4 mini on the CPU |

The last row is why the package matters: an x64 build on a Snapdragon Surface cannot reach the NPU. The app shows a
banner when it is not the package this PC takes. Only the Surface Laptop Ultra row has been run on the device; the
others are planned from each sample's hardware, and the X2, Core Ultra 200V, Series 3 and 24 GB RTX samples are
representative configurations rather than specific Surface SKUs.

How a deck is written also adapts to the model, and the deck page says why:

- **Slide by slide** is the default everywhere: one short call per slide, so any model on any device can do it.
  Writing the whole story or the whole deck in one reply happens only when asked for, and only with a large model whose
  window holds 16,000 or 12,000 tokens.
- **Constrained decoding** keeps every slide valid JSON. It stays on unless a model refuses it on this PC, which is
  remembered.
- **Repairs:** up to 2 passes of 6 slides. A model measured below 12 tokens per second gets 1 pass of 3, to keep the
  wait short.
- **Waiting time** comes from earlier decks with the same model on this PC, or from one measured slide.

## Packages

Build one self-contained package per silicon family, because the native AI runtimes differ by family and architecture:

| Package | For |
| --- | --- |
| Snapdragon-arm64 | Surface Pro and Surface Laptop with Snapdragon X or Snapdragon X2, and Surface Pro 9 with 5G (Microsoft SQ3) |
| Intel-x64 | Surfaces with an Intel processor (Core Ultra, Core or Pentium) or an AMD processor, and no NVIDIA GPU |
| NVIDIA-arm64 | Surface Laptop Ultra (NVIDIA RTX Spark) |
| NVIDIA-x64 | Surface Laptop Studio, Surface Laptop Studio 2 and Surface Book with an NVIDIA GPU |

```powershell
# All four, each started once to check it (self-test, the package it names, and its window)
powershell -NoProfile -ExecutionPolicy Bypass -File forge\tools\Build-ForgePackages.ps1 -Zip

# One package, with a test plan added to it
powershell -NoProfile -ExecutionPolicy Bypass -File forge\tools\Build-ForgePackages.ps1 -Package NVIDIA-arm64 -Include <path>\TEST-PLAN.md
```

Packages land in `forge\dist\OfflinePptxForge-<package>` (zips: `OfflinePptxForge-<package>-2.0.0.zip`). Each holds
`README-FIRST.txt`, `Test-ThisPC.cmd` and `Install-Shortcut.cmd`. Pass `-Dotnet` to use a particular `dotnet.exe`, and
`-SkipStartCheck` to skip the start check. Arm64 packages are start-checked only on Arm64 Windows.

## Test

On any Surface, run `Test-ThisPC.cmd` in the package folder. It runs every check, makes two real decks with the model
this PC takes (downloading it the first time), and saves a results zip to the desktop. It ends with READY or NOT READY.
The same check from a shell (it uses the Release build, else the package in `forge\dist` for this PC's processor;
`-Exe` picks another):

```powershell
forge\tools\Test-Forge.ps1 -Live -AllowDownload -Share
```

Without `-Live` it skips the model and the decks. The engine's own tests need no model:

```powershell
dotnet run --project forge\Forge.Tests -c Release            # all areas: pptx, layouts, grade, harness, engine
dotnet run --project forge\Forge.Tests -c Release -- engine  # one area
```

## Command line

`OfflinePptxForge.exe --help` lists everything. The common ones:

| Option | What it does |
| --- | --- |
| `--probe [--simulate ID] [--json]` | What this PC (or a sample Surface) can do, and the deck model plan |
| `--list [--json]` | The sample Surfaces, with the package and deck model each one takes |
| `--self-test [--json]` | Fonts, briefs, renderer, `.pptx` writer and this PC's plan; no model |
| `--simulate ID` | Open showing another Surface's capabilities. Decks are still made on this PC. |
| `--brief FILE` | Open with this brief JSON selected, for example one exported with the Hybrid Intelligence Studio's **Export deck brief (JSON)** |
| `--section deck\|pc` | Start on the deck page or on the capability page |
| `--out DIR`, `--data DIR` | Where decks, and the speed log and logs, are saved |
| `--allow-download` | Let the app fetch missing execution providers and models |
| `--smoke`, `--smoke-live` | Check the window in-process (no model), or also make a real deck and cancel one; `--report FILE`, `--capture DIR` |

Another demo can hand a brief over without a command line: save it as JSON in `%LOCALAPPDATA%\OfflinePptxForge\inbox`
(or `<--data DIR>\inbox`), then open or focus the Forge. The newest brief that arrived is selected and listed under
**Handed over by another app**; at start-up only one saved in the last 15 minutes is selected, and older ones are just
listed.
The Studio does not use the inbox yet: its **Prepare deck in Forge** button still opens the separate Offline PPTX Forge.

Avoid `--app-data` in tests: it moves Foundry Local's whole data folder, so execution providers and models are
downloaded again. The model cache is shared on purpose: the Forge uses the first of its own folder and those of other
Surface demos on the PC (the Sales Kit and the Hybrid Intelligence Studio among them) that already holds models, so a
model one of them downloaded is not downloaded again.

`forge-cli` (`forge\Forge.Cli`) drives the same engine without a window: `--plan` lists what this PC can run with the
settings and expected wait for each model, `--brief ID` writes a deck, `--emulate ID` writes it with the model another
Surface would use, and `--live-test` runs end to end.

## Layout

| Path | What it is |
| --- | --- |
| `app\OfflinePptxForge` | The WinUI 3 app |
| `Forge.Engine` | Detect, plan, load, measure and write decks (`ForgeSession`, `RunPolicy`) |
| `Forge.Core` | Briefs, the slide schema and harness, the renderer and the `.pptx` writer |
| `Forge.Cli` | `forge-cli` |
| `Forge.Tests` | Tests that need no model |
| `tools` | `Build-ForgePackages.ps1`, `Test-Forge.ps1`, `Install-Shortcut.ps1` |

For a development build of the app:

```powershell
dotnet build forge\app\OfflinePptxForge\OfflinePptxForge.csproj -c Release -r win-arm64 -p:Platform=ARM64
forge\tools\Install-Shortcut.ps1 -Launch    # Start menu shortcut (a forge\dist package for this PC's processor, else the Release build); -Desktop adds one there too
```
