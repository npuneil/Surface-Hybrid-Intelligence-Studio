# Surface Hybrid Intelligence Studio | Surface Laptop Ultra

A native **WinUI 3** executive showcase: live local GPU inference, twelve
one-click **live demos** (speech, vision, document search, translation, coding
and reasoning on the RTX GPU), a **Hybrid router** that shows what stays on the
PC and what would go to the cloud, a **GPU speedometer** and run of show, a
**Build on Surface** hub with a loopback OpenAI-compatible endpoint and starter
kits, interactive T-shirt model sizing, platform-fit use cases, and an
assumption-driven capacity calculator. The original web presentation remains
available as a companion.
Independent seller-enablement prototype, not a product announcement or endorsement.
The conceptual device illustration is original, not a product photograph.

## Download

The [latest release](https://github.com/npuneil/Surface-Hybrid-Intelligence-Studio/releases/latest) contains:

| File | What it is |
|---|---|
| `HybridIntelligenceStudio-<version>-win-arm64.zip` | The self-contained app (no .NET or Windows App SDK install needed), the local service, demo content and `Setup.cmd` |
| `HybridIntelligenceStudio-<version>-icon-pack.zip` | The app icon: PNG from 16 to 1024 px, a multi-size `.ico` and the `.svg` source, with a NOTICE |
| `SHA256SUMS.txt` | SHA-256 hashes of both zips |

Check the hash, unblock the zip (`Unblock-File`), extract it to a short path
such as `C:\Apps`, then double-click `Setup.cmd`. It finds an ARM64 Python 3.12
to 3.14 (3.13 tested), creates `.venv`, installs `requirements.txt` with the
tested dependency versions pinned in `constraints.txt`, adds the
desktop shortcut, then downloads every model the app uses and runs each live
demo once to check it. `.\Setup.ps1 -Prepare` does the same from a terminal
(Windows PowerShell 5.1 or PowerShell 7); `-NoShortcut`, `-Port` and
`-Python <path>` are optional. The pre-flight result is saved to
`.runtime\setup-prepare.json`. Requires Windows 11 on ARM64 with an NVIDIA GPU.
The app is not code-signed, so Windows may ask you to confirm the first launch.

## Run on this device

From the repository root:

```powershell
.\Start-NativeShowcase.ps1
```

The native application uses real WinUI controls, not an embedded browser.
Its C# interface talks to the existing loopback Python inference service so the
verified CUDA model selection, stream filtering, and calculation rules stay
consistent. **Qwen2.5-Coder 14B** and **GPT-OSS 20B** are the featured live models;
larger llama.cpp models are offered when their weights are installed (see *Larger models*).
Snapdragon and Intel recommendations are separate planning content, not
claims of having run those platforms on this machine. This build targets
Windows ARM64; platform guidance is not a cross-platform deployment package.

The app reuses an identified showcase service already running on
`127.0.0.1:8790` and leaves that external service running when it closes.
If none exists, it starts the local Python service itself and stops that
owned service on exit; a Windows job object also prevents an orphan if the
app crashes. Startup logs are in `.runtime\native\backend-*.log`.
`-Port`, `-Windowed`, `-Section <page>` (overview, live, demos, hybrid, industries,
showoff, build, sizing, platforms, business, playbook, learn), and `-Rebuild` are supported by
`Start-NativeShowcase.ps1`; no browser or extra terminal must remain open.
The launcher builds the native executable if it is missing. A .NET 10 SDK
and Windows App SDK NuGet packages are required for a build, not for every launch.

The original blue **laptop-and-spark icon** (Fluent-style, drawn for this project) appears in native window/taskbar
branding, the executable, and the companion browser tab. It is prototype
artwork, not official Microsoft brand artwork or an endorsement. The name, logo and
accent colours come from `edition.json` (see *Editions and scale-out harness*);
`.\tools\Sync-Edition.ps1` regenerates `native\Brand.cs` and the PNG/ICO/SVG
assets in `native\Assets`.
`.\tools\Install-NativeShortcut.ps1` installs or updates the
**Surface Hybrid Intelligence Studio** desktop shortcut with the native target,
app icon and matching Windows taskbar identity.

The live workspace offers five synthetic text workflows: executive briefing,
code drafting, **Snapdragon-fit field handover**, **Intel-fit meeting
actions**, and a plain-English **coding tutor** used by the optional lessons.
The field and meeting workflows demonstrate portable workflow patterns on the current
RTX device, not execution on a Snapdragon or Intel accelerator. They consume
supplied text fixtures; they do not record, transcribe, inspect images or
connect to business systems. Speech, images and document search are on the
**Live demos** tab.

### Live demos

The **Live demos** tab has a launch button for each of twelve demos that run
real models on the RTX GPU (Foundry Local CUDA variants only; no CPU or cloud
fallback). Every input is synthetic and fictional. The meeting and voice-memo
audio is spoken by Windows text-to-speech, and the form and receipt images are
drawn by Windows. Both are rendered once on the laptop by
`Render-DemoMedia.ps1` into `.runtime\demo-media`. Because the script is known,
the engine can grade live output against it.

| Demo | Runs on the GPU | Graded by the engine |
|---|---|---|
| Meeting to minutes and actions | Whisper medium, then Qwen2.5-Coder 14B | Word accuracy against the spoken script; unowned action marked |
| Voice memo to work order | Whisper medium, then Qwen2.5-Coder 14B | Valid JSON; fields match the memo |
| Scanned form to structured data | Qwen3-VL 4B (work order or receipt) | Valid JSON; fields or line-item arithmetic |
| Ask your documents | Qwen3-Embedding 0.6B, then Qwen2.5-Coder 14B | Right policy retrieved; answer cites only retrieved policies |
| Translate on the device | Qwen2.5-Coder 14B | Names, references and times kept unchanged |
| Executive brief from notes | Qwen2.5-Coder 14B | Source citations; no-benefit-claimed sentence |
| Redact before it leaves the device | Qwen2.5-Coder 14B | No synthetic identifier remains; business wording kept |
| Inbox triage to structured data | Qwen2.5-Coder 14B | Valid JSON for all six emails; categories match the labels |
| Local coding assistant | Qwen2.5-Coder 14B | A Python function was drafted (never executed) |
| Plan it out with a reasoning model | GPT-OSS 20B | Schedule satisfies all six constraints |
| Model face-off: 14B versus 20B | Both chat models, same prompt | Each answer graded separately |
| Five models, one laptop | Chat, reasoning, speech, vision, embedding | Each model's task; residency from Foundry Local, memory from Windows counters |

Each step shows its model, load time on first use, time to first token, speed
and total time. Answers under 20 tokens show *too short to measure* instead
of a speed. Reasoning is counted but its text is never shown, exported or saved. A wrong
answer is shown as a graded result, never hidden or replaced. **Play rehearsal**
replays authored sample output, labelled *Rehearsal: no model ran*. The
face-off, the five-model demo and non-default translation languages have no
rehearsal. Editing a live instruction skips checks that depend on the default
prompt. **Pre-flight: run every demo live** runs all twelve with their default
inputs and reports how many ran end to end and how many passed every check. On
this unit a warm pre-flight takes about three minutes. Export saves the
selected run's evidence as JSON. On the Sales sizing tab, each blue **Run live
demo** button with a play icon opens the matching demo and starts a live GPU
run. The separate checkboxes select workloads for sizing; the buttons do not
change that selection. If the GPU is busy or
not ready, the demo opens with the reason it cannot start; no rehearsal is substituted.

Demos load extra models beside the selected chat model and keep them resident,
so later runs skip loading. Selecting a model in the Live GPU workspace still
unloads the other chat models. Speech, vision and embedding models need two
items that `Start-Showcase.ps1 -Prepare` downloads once:
- the model weights;
- NVIDIA's cuDNN 9.26 and NVRTC 13.4 redistributables, about 210 MB of
  SHA-256-pinned wheels from PyPI. They go into the app-data folder, because the
  Foundry Local ARM64 CUDA bundle ships cuDNN without the sub-libraries that
  convolution and attention graphs need.

Nothing is installed into Python and no NVIDIA DLL is committed. The
**Ready to present?** panel shows what is still missing before you start.

### T-shirt sizing and platform fit

The **T-shirt sizing** page has two tabs. **Sales** (the default) is a
100-level view: pick broad workloads such as transcription and meeting notes,
translation, writing, a local coding assistant, reasoning or the largest open
models (70B class). The app recommends
the largest size those workloads need, shows the fit on this unit's RTX GPU
budget versus illustrative 32 GB and 16 GB laptops, and gives a jargon-free
talk track whose percentages come from the same engine. **Technical** is the
full planner described below; *See the technical sizing* hands the selected
size across with identical results.

#### Which Surface Laptop Ultra configuration?

Below the Sales results, the **Surface Laptop Ultra configurations** planner
turns the picked workloads into a memory recommendation across the 24, 32, 48,
64 and 128 GB options in Microsoft's tech specs. Each card runs the same
estimate (4-bit weights, 8,192-token context, 6 GiB reserve, 2 GiB headroom)
against that configuration's dedicated GPU memory and shows **Recommended**,
**Plenty of room**, **Little room** or **Won't fit**, plus a copyable talk track.

- **Recommended** is the smallest configuration whose estimate uses at most 80%
  of the memory left for AI. Real 4-bit model files are larger than the
  estimate (the Llama 3.3 70B file the app loads is 39.6 GiB against 32.6 GiB
  estimated), so a "Little room" fit is flagged rather than recommended.
- **AI models in use**: *One at a time* sizes for the largest model;
  *All open together* adds the models up. Writing, translation and documents
  share one general model.
- **Document length**: *Long documents* raises the context to 32,768 tokens.
- **GPU share**: the default is Microsoft's launch Task Manager image of a
  128 GB unit (79.9 GB dedicated GPU memory, 62%), applied to every
  configuration. *This PC's measured split* uses this unit's driver report
  instead (62.5 GB of 128 GB here, 49%); *Custom share* accepts 10-90%.
  Microsoft has not published the split for the smaller configurations.
- Processor pairings (24 GB entry with the 18-core CPU, 128 GB with the
  20-core CPU) come from launch coverage; confirm them in the configurator.

It is a memory-only estimate: no pricing, response-time, battery or support
claims. Sources: [Surface Laptop Ultra tech specs](https://learn.microsoft.com/en-us/surface/tech-specs/surface-laptop-ultra-1stedition),
Microsoft's launch Task Manager image of a 128 GB unit, and this unit's driver
report on the Device evidence page.

Sizing is a transparent planning estimate, not a benchmark or procurement
guarantee. Start with a model band, then adjust precision, context, working-set
allowances, reserve and headroom against **one** appropriate memory budget.

| Size | Nominal parameter band | Example workload |
|---|---|---|
| S | 1-4B | Bounded note drafting, short summaries |
| M | 7-8B | Service-desk drafts, richer document assistance |
| L | 14-20B | Code drafting and structured reasoning pilots |
| XL | 24-32B | Larger-model evaluation on a qualified configuration |
| XXL | 65-72B | Largest open models (Llama 3.3 70B class) on a large-memory configuration |

Raw weight estimate in GiB = billions of parameters × 1,000,000,000 ×
weight bits ÷ 8 ÷ 1,073,741,824. Runtime, context/KV cache, reserve and
headroom are additional assumptions. Quantization metadata, architecture,
activation memory, execution-provider behavior and concurrency can change
the actual working set. A fit indicator is not proof of model compatibility,
quality, speed, or support. Do not add Windows-visible memory and the GPU's
reported budget, and do not use 128 GB installed as proof of 128 GB GPU capacity.

**Snapdragon pilots:** field-note drafting, transcription followed by a reviewed
summary, and image-assisted inspection. Qualify the exact Windows ARM64 app,
shipping SKU, model artifact, license and accelerator/runtime route first.
Qualcomm's official Windows tutorial demonstrates target-specific Qwen3-4B
deployment; its model card and runtime requirements still govern each target.

**Intel pilots:** meeting transcription and summaries, service-desk/knowledge
drafts, and visual-document triage. Qualify the Intel device, exported model
and supported OpenVINO pipeline. A supported CPU path does not establish GPU
support, and not every Core or Core Ultra SKU has the same capabilities.

**This RTX device:** the live 14B coding and 20B reasoning paths use their
exact cached CUDA variants. The other use cases are pilot recommendations,
not new speech/image integrations or measured cross-device comparisons.

Official references:
- [Qualcomm: LLMs on Windows Snapdragon with target-specific runtime/model assets](https://github.com/qualcomm/ai-hub-apps/tree/main/tutorials/llm_on_genie)
- [Qualcomm: Whisper-Base export and deployment](https://github.com/qualcomm/ai-hub-models/tree/main/src/qai_hub_models/models/whisper_base)
- [OpenVINO GenAI pipelines and platform support](https://github.com/openvinotoolkit/openvino.genai)
- [Microsoft Foundry Local SDK and execution providers](https://learn.microsoft.com/en-us/azure/foundry-local/reference/reference-sdk-current)

### Optional: Coding as a second language

An optional onboarding tab for sellers who want to ramp up technically. Eight
short lessons teach reading code the way you learn a language: vocabulary
(variables), grammar (decisions, loops), sentences (functions, data/JSON),
conversation (APIs, AI sizing words) and fluency (reading AI-written code).
Each lesson has a plain-English analogy, line-by-line notes, a customer-ready
sentence and a quick check; a correct answer completes the lesson. Numbers in
lessons are computed by the same engine as the sizing page (for example 14B at
4-bit = 6.52 GiB of weights). **Ask the local coding tutor** opens the Live GPU
workspace with the lesson's code in the prompt so the local coding model explains it
on the RTX GPU. Code is displayed, never executed. Progress is stored only in
`%LOCALAPPDATA%\HybridIntelligenceStudio\learning.json`. Hide or restore the tab from
the lesson page or the **Seller playbook** page.

### Companion web presentation

```powershell
.\Start-Showcase.ps1
```

Open **http://127.0.0.1:8790**. Keep the launch terminal open. Ctrl+C stops only
the process started by the launcher. Ctrl+Enter runs the selected AI story.
The full-screen button expands the presentation. The seller playbook contains
the talk track and objection handling; **Print executive handout** prints a
clean, customer-facing summary with the calculator's current assumptions.
`.\tools\Install-NativeShortcut.ps1` adds a **Surface Hybrid Intelligence Studio** desktop shortcut.
`Executive-Handout.pdf` is the prepared single-page handout using illustrative
default assumptions; print again from the app after changing those inputs.
Launching again opens the existing showcase rather than starting a second
model process. Stop the original launch terminal before changing startup mode.

### Industry scenarios and Offline PPTX Forge

The **Industries** page adds four fictional customer stories, each a live workflow on
the local GPU model: Fabrikam Components (manufacturing), Beacon Mutual (insurance
operations), Northwind Retail (retail) and Cedar Borough (public sector). Names and
data are invented for demonstration; no real customer appears.

Each card offers:

- **Run live workflow**: opens the Live GPU workspace with the scenario's prompt.
- **Prepare deck in Forge**: writes a deck brief to Offline PPTX Forge's
  `briefs\custom\studio_<key>.json` and opens Forge. In Forge's Pre-staged mode select
  *<Client> - sales (custom)*, review the facts and template, then choose **Build deck**.
  Nothing is generated until you do. If Forge is already open, it is not launched again
  (Forge lists briefs at startup): use **Open another brief (.json)...** in that window.
- **Export deck brief (JSON)**: saves the same brief under the export folder's
  `forge-briefs` directory for use on another PC.

Forge is found beside this repository (`..\Offline-PPTX-Forge`) or at
`HYBRID_STUDIO_FORGE_ROOT`. Forge is started through Explorer, so closing the studio does
not close Forge. `.\Start-NativeShowcase.ps1 -Section industries` opens the page directly.

### Hybrid Intelligence: router, show-off tools and builder hub

Three pages turn "Hybrid Intelligence on Surface" from a slide into something people can
see, measure and build on. The Executive overview links to all three.

**Hybrid router** (`-Section hybrid`) shows where a request should run and why. A published,
deterministic policy (`hybrid.py`, standard library only) scans the text for identifiers and
credentials and for needs only a cloud service meets (current information, organisation-wide
data, beyond-device scale). It then picks one lane:

- **On this PC**: private data and nothing that needs the cloud, or credentials (cloud blocked),
  or the *offline* what-if (the cloud step is queued, never sent).
- **Hybrid**: personal data *and* a cloud need. The on-device model redacts first, then
  the policy re-scans the result and checks that the request itself survived (a model that
  answers or rewrites the request instead of redacting it is blocked). Only a payload that passes both checks is shown.
- **Cloud**: nothing sensitive and a real cloud need.

Six fictional presets cover each lane. **Route** is policy only (no model runs); **Run it on
this PC** streams the local answer or the redaction from the RTX GPU. Cloud payloads are
displayed and marked **NOT SENT**: the studio never calls a cloud service. A gallery of seven
hybrid patterns (local-first, redact-then-cloud, private knowledge, field, high volume,
right-size, same API) links each pattern to a demo or page you can run now. The rules catch
structured identifiers, not every name or context clue; they are not a data-loss-prevention product.

**Show it off** (`-Section showoff`) has:

- **GPU speedometer**: measures the loaded model's generation speed (output tokens after the
  first, over streaming time, or llama.cpp's own timings), first visible text, and reading speed
  (an estimate that includes request setup). One run on this PC, not a benchmark. **Export
  results (JSON)** saves timings and counts only, never generated text.
- **Session leaderboard**: one row per run, ranked by generation speed; cleared when the studio closes.
- **Five-minute run of show**: decision → number → endpoint → industry → deck, one button per step.
- **Companion tools**: finds and opens Offline PPTX Forge, Local AI Tokenomics, Local AI
  Headroom, Local AI Bench and HydraFusion autorouting when they are cloned beside the studio
  (or at `HYBRID_STUDIO_FORGE_ROOT`, `_TOKENOMICS_ROOT`, `_HEADROOM_ROOT`, `_BENCH_ROOT`,
  `_HYDRAFUSION_ROOT`); otherwise it links to their GitHub repositories. Tools start through
  Explorer, so they outlive the studio. Headroom and Bench open existing reports only.

**Build on Surface** (`-Section build`) is for developers:

- **Local endpoint**: an OpenAI-compatible API for the loaded model on
  `http://127.0.0.1:<studio port + 1000>/v1` (set `HYBRID_STUDIO_ENDPOINT_PORT` to change it).
  It is **off by default**, loopback only and unauthenticated (any app on this PC can call it
  while it runs), stops when you stop it or close the studio, and shares the GPU with the
  studio. Larger llama.cpp models already serve this API while loaded. **Send a test call**
  makes a real loopback request and shows the HTTP status and timing; private reasoning is never shown.
- **Code snippets**: copy-ready calls for the current endpoint and model (PowerShell, curl,
  Python, the OpenAI Python SDK, JavaScript, C# and the models list).
- **Starter kits**: five small projects written to
  `Documents\Surface Hybrid Intelligence Studio\Exports\Starters\<kit>-<time>`:

  | Kit | Run | Shows |
  |---|---|---|
  | Streaming chat (Python) | `python chat.py "..."` | Streaming from the local model |
  | Ask private documents (Python) | `python ask_docs.py "..."` | Retrieval with cited answers over synthetic policies |
  | Redact, then cloud (Python) | `python redact_then_cloud.py sample.txt` | The router policy, on-device redaction, both checks; `send_to_cloud()` is a stub |
  | Tool-using agent (Python) | `python agent.py` | A JSON-action agent with an allow-listed calculator, policy lookup and date |
  | Streaming chat (C# / .NET 10) | `dotnet run -- "..."` | `HttpClient` and `System.Text.Json`, no NuGet packages |

  Python kits need Python 3.10+ and the standard library only. Every kit reads
  `LOCAL_AI_BASE_URL` and `LOCAL_AI_MODEL` when set, so the same code can target another
  loopback model or, after your own review, an approved cloud endpoint. The studio writes
  the files and never runs them; it refuses to overwrite a non-empty folder.

One GPU job runs at a time: while a speed test, hybrid run or endpoint change is active, the
Live, Demos and Industries runs wait, and the other way round.

### Larger models

The model selector has two tiers, all running on the RTX GPU:

| Model | Runtime | Notes |
|---|---|---|
| **Qwen2.5-Coder 14B** (default), **GPT-OSS 20B** (reasoning) | Foundry Local CUDA | Featured models; already cached |
| **Qwen3-Coder 30B-A3B** | llama.cpp CUDA | Speech, vision and second-model demos can still run beside it |
| **Llama 3.3 70B**, **GPT-OSS 120B** | llama.cpp CUDA | Need the GPU to themselves: Foundry Local closes while selected |
| **MAI Code 1.1 Flash** (137B MoE) | Not available | Listed, disabled; see below |

30B+ weights come from the checksum-verified Local-AI-Bench collection
(`%USERPROFILE%\Local-AI-Bench\models\installed.json`) and its pinned `tools\llama\llama-server.exe`.
Set `HYBRID_STUDIO_LLAMA_HOME` if Local-AI-Bench is not in your user folder. Rules:

- **Dedicated GPU memory only.** Before anything unloads, the app checks free dedicated
  GPU memory (nvidia-smi) and Windows commit against the weights file plus an estimated
  context overhead. Memory the app itself would release counts toward the fit. If the
  model cannot fit, the switch is refused and the current model stays loaded. The
  message names any other llama-server holding memory; the app never stops another
  app's server.
- **Every layer on the GPU or no load.** The server runs with `-ngl all -fit off`; the app
  reads `offloaded N/N layers to GPU` from its log and measures the process's dedicated
  and shared GPU memory with Windows performance counters. More than 2 GiB of shared GPU
  memory is refused.
- **Attach, don't duplicate.** If a llama-server is already serving the same weights with
  every layer on the GPU (for example a local coding-model stack on port 8090), the app uses
  it and leaves it running. Otherwise it starts its own server, which stops when another
  model is selected or the app closes. Logs: `.runtime\llama-<alias>.log`.
- **After sleep.** Windows can page a llama-server's GPU memory out while the PC sleeps:
  nvidia-smi then reports 3.6 GiB used while the 30B server still holds 30.7 GiB. The
  app counts that paged-out memory as in use. Before attaching, it sends the server a
  one-token request to move its weights back onto the GPU, then measures (4.4 s for the
  30B here).

**Why shared GPU memory is not used.** This GPU reports 63,986 MB dedicated plus
49,132 MB shared (Windows RAM it can borrow), so CUDA lists about 110 GB. Measured on this
Surface Laptop Ultra (Oct 18, 2026, on battery power, llama.cpp b11573): Llama 3.3 70B with
28.3 GiB dedicated and 14.3 GiB shared loaded all 81/81 layers but decoded at
2.6–3.2 tok/s, and the load took Windows commit from 44.8 GiB available to 1.3 GiB.

**Measured in this app** (Oct 18, 2026): Qwen3-Coder 30B-A3B, all layers on the GPU,
29.6 GiB dedicated and 0.4 GiB shared. On AC power: decode 83–91 tok/s across the code,
insurance and manufacturing scenarios, prompt processing about 2,010 tok/s, TTFT
178–183 ms where the prompt was not already cached. Earlier on battery power the same
code scenario decoded at 48–75 tok/s, slower at lower charge. Plug in before a demo.

**Llama 3.3 70B** needs about 43.6 GiB of free dedicated memory and **GPT-OSS 120B**
about 61.5 GiB. The Windows desktop held about 7 GiB of the 62.5 GiB in these tests, so
the 120B is refused on this memory configuration; it does not fit in dedicated memory.

**MAI Code 1.1 Flash** is not installed: Microsoft delivers its local build through
GitHub Copilot's Windows ML provider, with no standalone download for this app. The app
shows Microsoft's published figures (137B total and 6.8B active parameters, a 53 GB
quantized build, 75.5 GB peak memory at 256K context) labelled **Not measured by this app**, with the
[source](https://commandline.microsoft.com/local-models-sandboxed-tools-github-windows/).

Select a model, wait for its ready status, then run either AI story. In this
workspace, switching unloads the other chat models first. The Live demos tab can
load both Foundry models side by side for the face-off.
Neither parameter count nor larger memory is presented as proof of better quality.

```powershell
.\Start-Showcase.ps1 -Model gpt-oss-20b
```

On an existing instance, `-Model` requests the same in-app model change.
Model acquisition is permitted only when the server was started with `-Prepare`.
The two featured CUDA models are already cached on this device.
Reasoning can increase time before visible answer text. The app shows actual
reasoning character-count activity without revealing or saving reasoning text.

Both launchers and the native app choose Python in this order: the
`HYBRID_STUDIO_PYTHON` environment variable (must point to an existing
`python.exe`), a project `.venv`, then an installed Foundry SDK environment in
sibling folder `..\hydrafusion-autorouting-demo\.venv` if one exists. They do not
modify that project. When present, the existing SDK data/cache folder
`%LOCALAPPDATA%\ASUAgenticShowcase` is reused for provider registration and its
`models` subfolder for the larger model weights; no ASU application source is changed.
Otherwise the SDK uses `%USERPROFILE%\.surface-hybrid-intelligence-studio`. SDK logs
for this showcase go to `.runtime\foundry-logs`. Providers must be registered
in each process, even when already cached. That SDK operation can check the
network and repair its cache. No Python packages are automatically installed.
Use `-Prepare` explicitly to allow missing model/initial provider acquisition
before presenting. `-RehearsalOnly` does not import the SDK or load a model.
`-NoBrowser` and `-Port 8791` are also available.

For a standalone installation from source on compatible Windows ARM64 NVIDIA hardware:

```powershell
git clone https://github.com/npuneil/Surface-Hybrid-Intelligence-Studio.git
Set-Location Surface-Hybrid-Intelligence-Studio
.\Setup.ps1 -Prepare                      # .venv, requirements.txt, every model checked
.\Start-NativeShowcase.ps1                # builds the native app on first run
.\tools\Install-NativeShortcut.ps1
```

The native build needs the .NET 10 SDK: the launchers use
`HYBRID_STUDIO_DOTNET` if set, then `dotnet.exe` on PATH or the standard install
locations. NuGet restores the Windows App SDK packages pinned in
`native\packages.lock.json` from the public dotnet feed in `native\NuGet.Config`
into the standard NuGet cache (override with `NUGET_PACKAGES`).
`.\tools\Install-NativeShortcut.ps1` then adds the desktop shortcut.

The companion web UI and backend calculator use only the Python standard
library; no npm, CDN, remote fonts, JavaScript dependencies or cloud-model
endpoint is used. The native front end additionally uses .NET and the Windows
App SDK.

## What is real

- Windows CIM inventory is collected at each server start. This device currently
  reports Microsoft Surface Laptop Ultra, **128 GB installed physical RAM**, 20 CPU cores,
  and NVIDIA RTX Spark N1X Blackwell RTX GPU. These are observed
  device identifiers, **not confirmed shipping-SKU specifications**.
  Installed capacity is the sum of `Win32_PhysicalMemory.Capacity` records:
  137,438,953,472 bytes (128 GiB; displayed as the device's 128 GB configuration).
  Windows reports about **64 GB visible**, from
  `Win32_OperatingSystem.TotalVisibleMemorySize`. The earlier build incorrectly
  treated the OS-usable view as installed capacity; this is corrected.
  `nvidia-smi` separately reports a **64,000 MiB GPU memory budget**. These are
  distinct views, not independently additive pools or a dedicated-VRAM claim.
  No memory partition settings are changed.
- Live inference uses Foundry Local SDK 2.1.0 and pinned CUDA GPU variants:
  `qwen2.5-coder-14b-instruct-cuda-gpu:4` and `gpt-oss-20b-cuda-gpu:1`.
  Both require `CUDAExecutionProvider`. The app refuses silent CPU or cloud
  fallback. Its former 7B TensorRT model is no longer the default or a selectable
  fallback. The historical 7B coding benchmark does not establish these larger
  CUDA models' accuracy or speed; every displayed result is from the current run.
- The Live demos add the pinned CUDA variants `openai-whisper-medium-cuda-gpu:3`,
  `qwen3-vl-4b-instruct-cuda-gpu:2` and `qwen3-embedding-0.6b-cuda-gpu:1`.
  The five-model demo reads residency from Foundry Local's loaded-model list and
  GPU memory from the Windows WDDM counters at that moment: this process's use and
  the whole adapter's use, which includes other apps. It is a snapshot, not a
  capacity limit.
- Every live run measures first visible SDK text and total generation wall time.
  Model loading is excluded; reasoning time is included. Output volume shows
  visible answer characters, with private reasoning activity counted separately, not tokens
  or an invented progress percentage. Evidence exports include SDK token usage
  where available. SDK output token counts can include reasoning. Exported token
  rate includes prompt processing and reasoning, not decode-only speed.
  No comparative performance is implied.
- The capacity calculator runs deterministic server-side arithmetic:
  people × tasks/day × minutes gained/task ÷ 60 × days/year × effective adoption.
  Capacity value = hours × loaded labor value. Assumed incremental hardware
  cost = people × assumed per-device difference. Default result: 2,640 hours,
  $158,400 illustrative capacity value and $60,000 assumed incremental hardware.

## Boundaries

All scenarios and account data are synthetic. Do not enter customer-confidential
data into an unapproved prototype. The generated code is displayed, never
executed. Generated asserts are examples, not verified passing tests.
Drafts require human review. Token-limit completions are labeled incomplete.
No email, file-system automation, service-desk or Microsoft 365 integration exists.

Rehearsal is explicitly selected, labeled **authored / no inference**, and tied
to the fixed sample prompt. Failures never substitute rehearsed text.
The presentation and device inventory focus on the RTX GPU and CUDA.
There is no fabricated cloud baseline, savings counter, battery test,
TOPS claim, Copilot+ certification or subscription replacement claim.
RAM is not described as dedicated GPU memory.

Local inference means the model executes on this PC; it does **not** mean the
application is certified air-gapped. Setup and SDK catalog/provider operations
may access the network. Non-essential SDK telemetry is disabled; the SDK may
still send a minimal ProcessInfo event. Validate disconnected behavior in a
separate controlled pilot before making an offline-availability claim.

The server binds only to 127.0.0.1 and rejects non-loopback Host headers.
POSTs require a per-process token and same-origin requests. Model text is
rendered as text, never HTML. No inputs or outputs are persisted automatically;
evidence and business-case JSON are saved only through explicit exports.
The runtime may maintain its own model caches and logs. The optional builder
endpoint is a separate, unauthenticated loopback API that is off until started;
starter kits are written only when you create one and are never executed by the studio.

Capacity value is not cash savings or an ROI forecast. Software, implementation,
support, financing and change-management costs are excluded. Adoption and time
improvements are assumptions until a pilot measures them. Cloud subscriptions
are unchanged. Confirm availability, approved positioning, model licensing,
exact shipping SKU and pricing with the appropriate channels before selling.

## Editions and scale-out harness

The studio is built to be re-badged for another partner, device or audience
without hand-editing a fork. `edition.json` is this tree's brand manifest:

| Field | Used for |
|---|---|
| `id`, `displayName`, `device` | Window title, title bar, exports folder, web title |
| `mark`, `markSubtitle`, `logo` | Navigation header and the generated icon/logo: `logo.style` is `monogram` (default; `logo.text` on a rounded square) or `device` (an original laptop-and-spark glyph from `tools\LogoGeometry.psd1`); optional `logo.backgroundEnd` adds a gradient |
| `accent`, `webAccent` | WinUI accent ramp and palette; web accent and focus colours |
| `codeName` | C# namespace, assembly/executable, project file, app-data folder |
| `appUserModelId`, `shortcutName` | Windows taskbar identity and desktop shortcut |
| `serviceAppId`, `schemaPrefix`, `envPrefix` | Service identity, export schema names, `<prefix>PYTHON`/`<prefix>DOTNET` |
| `repository`, `visibility` | Publishing target |

```powershell
.\tools\Sync-Edition.ps1             # regenerate Brand.cs, icon/logo, XAML/CSS accents
.\tools\Sync-Edition.ps1 -Check      # fail if generated files drifted from edition.json
.\tools\New-Edition.ps1 -Edition <id> -Destination ..\<folder>
.\tools\Test-Edition.ps1 -Path ..\<folder> -ForbiddenToken <word>
.\tools\New-ReleasePackage.ps1 -ForbiddenToken <word>   # release zips in .runtime\release
```

A target edition lives in `editions\<id>.json`: the same fields plus optional
`rules` (literal phrase rewrites for prose, each optionally limited by `files`
wildcards) and `forbiddenTokens`. `New-Edition.ps1` copies the tracked tree,
applies the rules, maps every source identity token to the target in a single
longest-match pass (a replacement is never re-replaced), renames paths such as
the project file, writes the target `edition.json` without its rules, then
regenerates `Brand.cs`, accents and assets. It fails if a forbidden token or any
source identity token remains in a file name or content, binaries included.
`-Force` refreshes an existing destination but keeps its `.git` folder, so an
edition repository can be regenerated from upstream and reviewed as a diff.
Editions have their own app identity and data folder, so several can be
installed side by side; give each its own service port, for example
`.\tools\Install-NativeShortcut.ps1 -Port 8792`.

`Test-Edition.ps1` is the conformance gate: manifest validation (including any
`editions\*.json` targets), sync check, leak scan, hygiene scan (user-profile
paths, user name, tokens, keys; binaries included), Python tests, native build,
self-test and UI smoke. `-Live` adds CUDA runs; `-Browser` runs the browser
check, refreshes `Executive-Handout.pdf` and scans it. Results are written to
`.runtime\edition-conformance.json`. The native self-test also fails if
`Brand.cs` disagrees with `edition.json`.

`New-ReleasePackage.ps1` builds the downloadable release for the current
edition: it stages the edition's files, exports the icon pack, builds the
self-contained native app for `win-arm64` without PDBs where the shortcut
expects it (`dotnet publish` would omit the app's `.pri` and `App.xbf`),
runs the hygiene scan on everything and the forbidden-token scan on text
files and the app's own binaries, then writes both zips, `SHA256SUMS.txt`
and `release-notes.md` (version from the project file). Publish with
`gh release create v<version> <both zips> SHA256SUMS.txt --notes-file release-notes.md`.

## Checks

```powershell
.\tests\Native-Smoke.ps1
.\tests\Native-Smoke.ps1 -Live
python -m unittest discover -s tests -v
.\tests\Browser-Smoke.ps1
.\tests\Browser-Smoke.ps1 -IncludeLive
.\tools\Test-Edition.ps1
```

Native checks exercise deterministic sizing boundaries, Sales-to-Technical
parity, coding-lesson answers and progress storage, scenario parsing,
platform claims, exports, native navigation, calculator, authored rehearsal,
every Live demos launch, rehearsal and hand-off, every Hybrid router preset's
lane, the starter-kit writer's path guards, companion-tool discovery and the
interlock between GPU jobs.
The live option additionally runs every workflow, including the coding tutor,
on the actual CUDA model, then runs the full live-demo pre-flight, a speed test,
a real call to the builder endpoint and a live hybrid redaction. It requires
all demo models to be prepared and allows up to 40 minutes by default.
Native reports and render-target screenshots are in `.runtime\native`.
`tests\Inspect-NativeUI.ps1` provides external Windows UI Automation inspection
and JSON snapshots for an explicitly selected app process.

The browser check uses installed Microsoft Edge and native DevTools, with no
downloads or extensions. It saves reports/screenshots under `.runtime`.
The live option exercises both AI stories on both featured larger CUDA models,
exports measured evidence, and restores the default 14B model. It requires a
running showcase with both models cached and checks this device's 128 GB / 64 GB
memory views. Both browser modes validate the single-page PDF layout.
See `SELLER-GUIDE.md` for the presentation.
