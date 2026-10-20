# Seller playbook: Surface Laptop Ultra

**Positioning:** A locally demonstrated AI workflow, not a claim that every
workload belongs on a PC. The device earns its place through a customer pilot.

## Five-minute executive flow

| Time | Show | Say |
|---|---|---|
| 0:00 | Observed hardware evidence | "Let's look at what this device can actually do, not just a specification sheet." |
| 0:45 | Executive briefing, live local mode | "For selected workflows, a useful first draft can be generated here rather than by a cloud model." |
| 2:00 | Developer acceleration | "A bounded development request becomes a local code draft. Review and testing remain the developer's responsibility." |
| 3:15 | Customer-specific economics | "Which repeated task matters? Replace these illustrative inputs with your baseline and pilot measurements." |
| 4:15 | Pilot close | "Let's scope one workflow, ten users and two weeks, with clear quality and task-time gates." |

Before the meeting, launch the app, wait for **Local AI ready**, and run both
stories once. Then open **Live demos** and press **Pre-flight: run every demo
live** (see below). Inspect the actual output; generation quality can vary. Confirm
the customer-facing device name, availability and approved specifications.
For a non-live walkthrough, explicitly choose **Authored rehearsal**.
Never present rehearsal as an inference or timing measurement.

**Memory correction:** This unit has **128 GB installed**, measured from physical
DIMM capacity. Approximately **64 GB is visible to Windows**; NVIDIA separately
reports a **64,000 MiB GPU budget**. These are distinct memory views, not separate
additive pools or a dedicated-VRAM claim. No memory partition settings are changed.
The Executive overview shows the hero specs (128 GB unified memory, 6,144-core
Blackwell RTX GPU, 20-core CPU, 15-inch PixelSense Ultra display); open
**Device evidence** for the three memory views.

**Larger-model walkthrough:** Start with **Qwen2.5-Coder 14B**, then use the
on-device model selector to load **GPT-OSS 20B**. Wait for ready status before
running. The workspace keeps one chat model resident at a time; the Live demos
face-off loads both. GPT-OSS can spend longer reasoning
before the answer appears; activity counts are real, while private reasoning text
is neither displayed nor saved. A larger parameter count is not an accuracy claim.

**Going bigger (30B and up):** When Local-AI-Bench weights are installed, the
selector also lists **Qwen3-Coder 30B-A3B**, **Llama 3.3 70B** and **GPT-OSS 120B**
on llama.cpp CUDA. Plugged in, the 30B ran the code and industry stories here at
83–91 tok/s decode (48–75 on battery), with every layer on the GPU. The app checks free dedicated GPU memory *before*
unloading anything: if another app's model server holds the memory, the switch is
refused, the current model keeps running and the message names the server to close.
The 70B needs about 43.6 GiB free, so close other local model stacks first. Say plainly
that the 120B (about 61.5 GiB) does not fit in this device's dedicated GPU memory.
**MAI Code 1.1 Flash** appears disabled with Microsoft's published figures, labelled
*Not measured by this app*. It ships through GitHub Copilot's local model support, not
this app. Plug in before showing large models.

## Show it live: the Live demos tab

**Before the customer arrives**, open **Live demos** and press **Pre-flight:
run every demo live**. On this unit a warm pre-flight takes about three
minutes; the first one of the day also loads every model. Green means the demo
ran and passed every engine check. Amber on the face-off usually means one
model answered wrong. That is a real, graded result and worth showing. Red
means a step did not run; read its message. The **Ready to present?** panel
lists anything still missing. If models or NVIDIA libraries are missing, run
`.\Start-Showcase.ps1 -Prepare` once on an approved network.

**In the meeting**, choose two or three demos that match what the customer said:

| If they mention | Open | Say |
|---|---|---|
| Meetings, notes, field work | Meeting to minutes and actions; Voice memo to work order | "That recording was transcribed on this laptop. The app checks the transcript against the script we wrote." |
| Forms, receipts, paperwork | Scanned form to structured data | "A vision model reads the image here and returns data a system can use. The app checks the result against the document we generated." |
| Policies, HR or IT questions | Ask your documents | "It answers only from the documents it found on this laptop and cites them, and the app checks those citations." |
| Global teams, customer notices | Translate on the device | "Names, references and times come through exactly as written. The app checks that." |
| Privacy, compliance | Redact before it leaves the device | "Personal details are replaced on the laptop before anything is shared." |
| Developers | Local coding assistant | "The code is drafted here and shown for review. This app never runs it." |
| Planning, analysis | Plan it out with a reasoning model | "A large reasoning model works through six constraints, and the app checks every one." |
| "How much can it run?" | Model face-off; Five models, one laptop | "These models are loaded together on one laptop, and each is doing real work right now." |

Rules for live demos:
- All inputs are synthetic and fictional. Do not type customer data into the
  instruction box.
- **Play rehearsal** is labelled *Rehearsal: no model ran*. It is a fallback
  walkthrough, not a measurement.
- If a live run fails, say so and move on. The app never substitutes rehearsal
  output for a failed run.
- Timings are this run on this laptop. They are not a comparison with any other
  device or with cloud AI.
- **Export** saves the run's evidence as JSON (models, timings, checks), with
  no reasoning text.
- On the **T-shirt sizing** Sales tab, click the blue **Run live demo** button
  with the play icon to open and run the matching demo on the GPU. Use the
  separate checkboxes to select workloads for sizing. If the GPU is busy or
  not ready, the demo explains why it cannot start.

## Industry stories and a leave-behind deck

The **Industries** page has four fictional customers: Fabrikam Components
(manufacturing), Beacon Mutual (insurance operations), Northwind Retail (retail)
and Cedar Borough (public sector). Pick the one closest to the customer, then:

1. **Run live workflow** to run its prompt on the GPU in front of them.
2. **Prepare deck in Forge** to stage a matching brief in Offline PPTX Forge. In
   Forge, select *<Client> - sales (custom)*, review the facts, then **Build deck**.
   Forge builds the deck locally; nothing is generated until you choose Build deck.

Say these are illustrative customers. Replace the fictional facts in Forge
with real ones only after the customer agrees.

## Hybrid Intelligence: show it, then hand it to the builders

**Positioning:** Hybrid Intelligence means each request runs where it fits. Private,
fast and offline work stays on the PC; current information, organisation-wide data and
frontier scale go to the cloud. The PC does not replace the cloud.

The **Show it off** page has a five-step run of show, one button per step:

| Step | Button | Say |
|---|---|---|
| 1. Show the decision | Open the router (loads *Reply with today's rates*) | "This request has a customer's details *and* needs today's rates. The PC redacts the details first, so only the clean request would go to the cloud." |
| 2. Put a number on it | Run speed test | "That's how fast this model is generating on this laptop's GPU right now. One run here, not a benchmark." |
| 3. Prove it's an endpoint | Open Build on Surface | "Your developers call this like a cloud API, but it's on 127.0.0.1." Start the endpoint, then **Send a test call**. |
| 4. Make it theirs | Industry scenarios | Run the customer's closest industry workflow. |
| 5. Leave with a deck | Open PPTX Forge | Stage the matching brief and build the leave-behind locally. |

On the **Hybrid router**, press **Route** for the decision and its reasons (no model
runs), then **Run it on this PC** to stream the local answer or redaction from the GPU.
Every cloud payload is shown and marked **NOT SENT**: the studio never calls a cloud
service. If the redaction check blocks a payload, show that too: the PC caught it before
anything could leave. Try the *Field note while offline* preset to show the cloud step
being queued rather than failing. The rules catch structured identifiers (emails,
phone numbers, account numbers, credentials), not every name or context clue.

**Speedometer and leaderboard:** run the speed test on each model you load to fill the
session leaderboard. Numbers are this run on this laptop; never compare them with other
devices or cloud services. **Export results (JSON)** saves timings, not text.

**For developers**, open **Build on Surface**:
- Start the **Local endpoint** (off by default, this PC only, no key). Copy a snippet
  (Python, JavaScript, C#, PowerShell or curl) and run it in their own terminal.
- **Create** a starter kit: streaming chat, ask private documents, redact-then-cloud,
  a tool-using agent (Python) or a C# chat app. The studio writes the files and opens
  the folder; it never runs them. The redact-then-cloud kit uses the router's own policy
  and stops before any real cloud call.
- Stop the endpoint when finished; closing the studio also stops it.

**Companion tools** on the same page open Offline PPTX Forge, Local AI Tokenomics,
Local AI Headroom, Local AI Bench and HydraFusion autorouting when they are installed
beside the studio. Each tool's own claims and guardrails apply.

## Questions that qualify an opportunity

1. Which repeated workflow consumes meaningful time today?
2. Which users need a local option, and what is their actual connectivity pattern?
3. What data, model-license and security policies must the application meet?
4. Does the target shipping SKU run the required ARM64 apps and peripherals?
5. What quality, task-time and user-experience evidence would justify scale?

## Right-size the model, then qualify the platform

Use the native **T-shirt sizing** planner to start the conversation, not to
declare a winning device. With executives, stay on the **Sales** tab: tick the
workloads they mention (coding, transcription, translation and so on) and read
the "Say it like this" line. Switch to **Technical** only when an architect
wants the arithmetic. S (1-4B), M (7-8B), L (14-20B), XL (24-32B) and
XXL (65-72B)
describe nominal model-size bands, not accuracy tiers. Show how precision,
context, runtime/KV allowances and operating headroom change the estimated
working set. A model whose weights fit may still fail to load or be unsuitable.
Never sum the unit's installed, OS-visible and driver-reported memory views.

### Which memory configuration should they order?

Scroll below the Sales results to **Surface Laptop Ultra configurations**. It
uses the same ticked workloads and marks one of the 24, 32, 48, 64 or 128 GB
options **Recommended**.

1. Tick what the customer will actually run. Ask whether those AI tools stay
   open together; if so, turn on **All open together**.
2. Turn on **Long documents** if they will work with long contracts, reports
   or transcripts.
3. Leave the GPU share on **Microsoft launch unit** for a customer
   conversation. Switch to **This PC's measured split** to show what this
   demo unit reports (it is more conservative).
4. Read or copy **Say it like this**. It names the recommended configuration,
   any smaller one that only just holds the workload, and what stepping up
   would add.

Examples on the default settings: a coding assistant points to 48 GB; coding
plus reasoning open together points to 64 GB; a 70B-class model points to
128 GB. A "Little room" card is a warning, not a recommendation: real model
files are larger than the estimate. The planner is memory-only. Do not quote
it for price, response time or battery life, and confirm the configuration
and processor pairing in the configurator before an order.

| Platform | Pilot use case | What must be qualified |
|---|---|---|
| Snapdragon | Field notes into a reviewed shift summary | Windows ARM64 app and exact model/runtime bundle for the shipping SKU |
| Snapdragon | Transcription followed by a short summary | Speech pipeline, microphone/consent workflow, model license and target-specific runtime |
| Snapdragon | Image-assisted inspection | Supported vision model, image handling, human review and task-specific accuracy |
| Intel | Meeting transcription and summary | Exact Intel CPU/GPU, supported OpenVINO pipeline and audio/data policy |
| Intel | Service-desk or knowledge draft | Approved retrieval sources, citations, compatibility and response-quality gate |
| Intel | Visual-document triage | Supported vision/OCR pipeline, document formats and human-review rule |
| This RTX unit | Live 14B code draft or 20B structured reasoning | Exact cached CUDA variant, actual run evidence and output review |

These are workload-fit hypotheses, not exclusive capabilities or measured
vendor comparisons. The app runs the current RTX workflows; Snapdragon and
Intel cards are planning guidance, not demonstrations on those processors.
Speech, image processing, retrieval and external business-system integration
are not implemented merely because a use-case card describes them.

**Seller line:** "Right-size the workflow first. Then prove the exact model,
runtime and device together." Use the platform page's official source links
to qualify a proposed configuration; memory arithmetic alone is insufficient.

## Optional: ramp up with "Coding as a second language"

Sellers who want to follow developer and architect conversations can take the
optional eight-lesson module. It teaches reading,
not writing, code, and ends with reviewing AI-written code. Use **Ask the
local coding tutor** to have the on-device coding model explain any lesson's code
in plain English. Treat its answer as a draft, like any generated output.
Hide the tab from the Seller playbook page if you do not need it.

## Handle the important objections

**"Why not cloud AI?"** Keep cloud AI for the workloads and managed services
that suit it. This demonstrates an additional local route, not a replacement.
No cloud price, performance comparison or subscription reduction is claimed.

**"Does local AI replace the cloud?"** No. Hybrid Intelligence keeps private, fast
and offline work on the PC and uses the cloud for current information,
organisation-wide data and frontier scale. The Hybrid router shows the split and
its reasons; the studio itself never calls a cloud service.

**"Can our developers build on this?"** Yes. Build on Surface starts an
OpenAI-compatible endpoint on loopback (off by default, this PC only) and creates
small starter projects in Python and C#. Their code reviews and runs them; the
studio never executes generated code. Production use still needs the customer's
own model licensing, security review and deployment plan.

**"How is AI accelerated?"** These models run on the NVIDIA RTX GPU through
CUDA. Inspect each run's model identity and execution-provider evidence.

**"Is this Microsoft 365 Copilot / Copilot+?"** No. This is a custom Foundry
Local prototype. Certification, licensing, integrations and availability are
separate questions.

**"Is the data completely isolated?"** The showcased model executes locally.
That is not a security or air-gap certification. Setup/catalog operations can
use the network, and minimal SDK process telemetry may remain.
Evaluate deployment controls with the customer's security team.

**"How much money will it save?"** The calculator estimates possible capacity,
not cash savings. Its device cost is an assumption, not a quote. Add software,
deployment, support and change-management costs in a complete business case.
Retain existing cloud subscriptions in the analysis.

**"Does it work disconnected?"** Do not claim a result not measured. Prepare
models first, then validate the exact workflow with connectivity intentionally
disabled in an approved pilot. This showcase does not change network settings.

**"Why these models?"** The 14B coder provides a larger dedicated coding option;
the 20B model adds a larger reasoning option. Both are cached CUDA variants
verified for local inference. Parameter count alone proves neither quality nor
speed. Their identities and actual selected providers are inspectable on screen.

## Proposed pilot acceptance sheet

Select one synthetic or approved-data workflow. Record current task time,
output accuracy and quality rubric, connectivity conditions, actual shipping
SKU, app/peripheral compatibility, model/runtime licenses, required data
controls, total deployment cost and support ownership.

Run ten users for two weeks as a **proposal**, not a validated study design.
Agree acceptance gates with the customer before starting. Keep human review
in scope. Export actual run evidence and the current assumption-based case.
Use the results to determine whether to stop, adapt or scale.

**Partner opportunity:** Discovery, device selection, application compatibility
validation, deployment planning and lifecycle support, subject to the actual partner's
engagement scope. No unverified service entitlement or commercial offer is implied.
