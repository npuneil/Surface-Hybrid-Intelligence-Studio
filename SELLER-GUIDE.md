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

**Larger-model walkthrough:** Start with **Qwen2.5-Coder 14B**, then use the
on-device model selector to load **GPT-OSS 20B**. Wait for ready status before
running. The workspace keeps one chat model resident at a time; the Live demos
face-off loads both. GPT-OSS can spend longer reasoning
before the answer appears; activity counts are real, while private reasoning text
is neither displayed nor saved. A larger parameter count is not an accuracy claim.

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
- On the **T-shirt sizing** Sales tab, **Show it live** jumps straight to the
  demo for the workload the customer picked.

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
wants the arithmetic. S (1-4B), M (7-8B), L (14-20B), and XL (24-32B)
describe nominal model-size bands, not accuracy tiers. Show how precision,
context, runtime/KV allowances and operating headroom change the estimated
working set. A model whose weights fit may still fail to load or be unsuitable.
Never sum the unit's installed, OS-visible and driver-reported memory views.

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
