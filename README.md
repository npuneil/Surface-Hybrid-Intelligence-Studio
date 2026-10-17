# Surface Hybrid Intelligence Studio | Surface Laptop Ultra

A native **WinUI 3** executive showcase: live local GPU inference, twelve
one-click **live demos** (speech, vision, document search, translation, coding
and reasoning on the RTX GPU), interactive T-shirt model sizing, platform-fit
use cases, and an assumption-driven capacity calculator. The original web
presentation remains available as a companion.
Independent seller-enablement prototype, not a product announcement or endorsement.
The conceptual device illustration is original, not a product photograph.

## Run on this device

From the repository root:

```powershell
.\Start-NativeShowcase.ps1
```

The native application uses real WinUI controls, not an embedded browser.
Its C# interface talks to the existing loopback Python inference service so the
verified CUDA model selection, stream filtering, and calculation rules stay
consistent. **Qwen2.5-Coder 14B** and **GPT-OSS 20B** are the live model choices;
Snapdragon and Intel recommendations are separate planning content, not
claims of having run those platforms on this machine. This build targets
Windows ARM64; platform guidance is not a cross-platform deployment package.

The app reuses an identified showcase service already running on
`127.0.0.1:8790` and leaves that external service running when it closes.
If none exists, it starts the local Python service itself and stops that
owned service on exit; a Windows job object also prevents an orphan if the
app crashes. Startup logs are in `.runtime\native\backend-*.log`.
`-Port`, `-Windowed`, `-Section sizing`, and `-Rebuild` are supported by
`Start-NativeShowcase.ps1`; no browser or extra terminal must remain open.
The launcher builds the native executable if it is missing. A .NET 10 SDK
and Windows App SDK NuGet packages are required for a build, not for every launch.

The original blue **HI (Hybrid Intelligence) monogram** appears in native window/taskbar
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
selected run's evidence as JSON. **Show it live** on the Sales sizing tab opens
the matching demo.

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
translation, writing, a local coding assistant or reasoning. The app recommends
the largest size those workloads need, shows the fit on this unit's RTX GPU
budget versus illustrative 32 GB and 16 GB laptops, and gives a jargon-free
talk track whose percentages come from the same engine. **Technical** is the
full planner described below; *See the technical sizing* hands the selected
size across with identical results.

Sizing is a transparent planning estimate, not a benchmark or procurement
guarantee. Start with a model band, then adjust precision, context, working-set
allowances, reserve and headroom against **one** appropriate memory budget.

| Size | Nominal parameter band | Example workload |
|---|---|---|
| S | 1-4B | Bounded note drafting, short summaries |
| M | 7-8B | Service-desk drafts, richer document assistance |
| L | 14-20B | Code drafting and structured reasoning pilots |
| XL | 24-32B | Larger-model evaluation on a qualified configuration |

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

### Larger models

The model selector offers **Qwen2.5-Coder 14B** (default) and **GPT-OSS 20B**
(reasoning). Select a model, wait for its ready status, then run either AI story.
In this workspace, switching unloads the other chat models first. The Live
demos tab can load both side by side for the face-off.
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

For a standalone installation on compatible Windows ARM64 NVIDIA hardware:

```powershell
git clone https://github.com/npuneil/Surface-Hybrid-Intelligence-Studio.git
Set-Location Surface-Hybrid-Intelligence-Studio
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\Start-Showcase.ps1 -Prepare
.\Start-NativeShowcase.ps1
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
The runtime may maintain its own model caches and logs.

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
| `mark`, `markSubtitle`, `logo` | Navigation header and the generated monogram icon/logo |
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
and every Live demos launch, rehearsal and hand-off.
The live option additionally runs every workflow, including the coding tutor,
on the actual CUDA model, then runs the full live-demo pre-flight. It requires
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
