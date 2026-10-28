# Banking playbook: Surface Hybrid Intelligence x Zava Financial

**Event:** Zava Financial executive briefing, theme *Hybrid AI for global banking*.
Zava Financial is a fictional firm, and the reference architecture is illustrative
rather than a description of any bank's systems. When you brief a real customer,
use its name as text only: no logos, marks or affiliation claims.

**Audience:** line-of-business leaders, CIOs, CDAOs, CISOs, model risk and technology teams.

**Positioning:** Hybrid AI for global banking. Client conversations, account data and NPI
stay inside the firm; only de-identified, minimized queries for complex questions
would go to Azure OpenAI; bankers review and approve every draft. This is a locally
demonstrated workflow, not a claim that every workload belongs on a PC. The device
earns its place through a measured pilot.

**Ground rules for the session:**
- Every bank, client, account and record is fictional and synthetic.
- Drafts only. Credit, lending, account-opening, dispute, fraud, AML, trade-booking,
  model-approval and investment decisions stay with authorized people.
- Not financial, legal or investment advice, and no suitability, risk-rating or
  suspicious-activity determination.
- No regulatory or compliance determination. De-identification removes structured
  identifiers only and is not certified. The app does not connect to core banking,
  CRM, payments or any system of record, and never calls a cloud service.

## The reference architecture (open with this)

| Tier | Button on the Firm overview | Say |
|---|---|---|
| Tier 1: Smart capture (the banker's Surface) | **Run client meeting capture** | "The client conversation is captured with consent and transcribed locally. Nothing has left the laptop." |
| Tier 2: In-firm drafting (local AI beside systems of record) | **Draft a KYC refresh checklist** | "Firm policy is applied inside the firm and a cited checklist comes back for the analyst. No risk rating is assigned." |
| Escalate only when needed | **Route a complex question** | "Only when a question needs frontier reasoning does the router remove NPI and minimize the context, and it shows every reason." |
| Tier 3: Cloud support (Azure OpenAI) | **Preview the cloud payload** | "This is the de-identified query that would go out, marked NOT SENT. Model support comes back; the client decision never leaves the firm." |

## Five-minute executive flow

| Time | Show | Say |
|---|---|---|
| 0:00 | Firm overview: reference architecture and Device evidence | "Client data stay in the firm: recordings, account data and NPI remain inside. Let me prove each tier." |
| 0:45 | **Run client meeting capture** (Client meeting to call notes and actions, live) | "A synthetic client meeting is transcribed and drafted into call notes on this GPU. It marks the wire-limit review 'Not assigned' instead of inventing an owner." |
| 2:00 | **Draft a KYC refresh checklist**, then **Route a complex question** | "The checklist waits for an analyst. For a complex question, only the de-identified query would reach the cloud." |
| 3:15 | T-shirt sizing leader view, then Business case | "Which banker workflow matters most to you? Replace these illustrative inputs with your own baseline." |
| 4:15 | Pilot close | "Let's scope a four-week pilot on synthetic client records, with bankers approving every draft and clear quality gates." |

Swap the scenario to match the room: **Card disputes** for consumer and card
operations, **Know your customer** for commercial and corporate onboarding,
**Treasury management** for payments, **Markets operations** for trade
settlement teams, **Wealth management** for advisor leadership and **Model risk
management** for model risk, data and AI governance teams.

Before the meeting, launch the app, wait for **Local AI ready**, and run both
stories once. Then open **Live demos** and press **Pre-flight: run every demo
live** (see below). Inspect the actual output; generation quality can vary. Confirm
the audience-facing device name, availability and approved specifications.
For a non-live walkthrough, explicitly choose **Authored rehearsal**.
Never present rehearsal as an inference or timing measurement.

**Memory correction:** This unit has **128 GB installed**, measured from physical
DIMM capacity. Approximately **64 GB is visible to Windows**; NVIDIA separately
reports a **64,000 MiB GPU budget**. These are distinct memory views, not separate
additive pools or a dedicated-VRAM claim. No memory partition settings are changed.
The Firm overview shows the hero specs (128 GB unified memory, 6,144-core
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
on llama.cpp CUDA. Plugged in, the 30B ran the code and scenario stories here at
83–91 tok/s decode (48–75 on battery), with every layer on the GPU. The app checks free dedicated GPU memory *before*
unloading anything: if another app's model server holds the memory, the switch is
refused, the current model keeps running and the message names the server to close.
The 70B needs about 43.6 GiB free, so close other local model stacks first. Say plainly
that the 120B (about 61.5 GiB) does not fit in this device's dedicated GPU memory.
**MAI Code 1.1 Flash** appears disabled with Microsoft's published figures, labelled
*Not measured by this app*. It ships through GitHub Copilot's local model support, not
this app. Plug in before showing large models.

## Show it live: the Live demos tab

**Before the session starts**, open **Live demos** and press **Pre-flight:
run every demo live**. On this unit a warm pre-flight takes about three
minutes; the first one of the day also loads every model. Green means the demo
ran and passed every engine check. Amber on the face-off usually means one
model answered wrong. That is a real, graded result and worth showing. Red
means a step did not run; read its message. The **Ready to present?** panel
lists anything still missing. If models or NVIDIA libraries are missing, run
`.\Start-Showcase.ps1 -Prepare` once on an approved network.

**In the session**, choose two or three demos that match what the room cares about:

| If they mention | Open | Say |
|---|---|---|
| Client meetings, branch operations | Client meeting to call notes and actions; Voice memo to work order | "That recording was transcribed on this laptop. The app checks the transcript against the script we wrote." |
| Forms, receipts, back-office paperwork | Scanned form to structured data | "A vision model reads the image here and returns data a system can use. The app checks the result against the document we generated." |
| Bank policy or IT questions | Ask your documents | "It answers only from the documents it found on this laptop and cites them, and the app checks those citations." |
| Multilingual customers, online-banking notices | Translate on the device | "Names, references and times come through exactly as written. The app checks that." |
| Customer privacy and NPI | Redact before it leaves the device | "Personal details are replaced on the laptop before anything is shared." |
| Bank developers, data science | Local coding assistant | "The code is drafted here and shown for review. This app never runs it." |
| Planning, analysis | Plan it out with a reasoning model | "A large reasoning model works through six constraints, and the app checks every one." |
| "How much can it run?" | Model face-off; Five models, one laptop | "These models are loaded together on one laptop, and each is doing real work right now." |

Rules for live demos:
- All inputs are synthetic and fictional. Do not type real customer records, NPI or
  confidential bank data into the instruction box.
- **Play rehearsal** is labelled *Rehearsal: no model ran*. It is a fallback
  walkthrough, not a measurement.
- If a live run fails, say so and move on. The app never substitutes rehearsal
  output for a failed run.
- Timings are this run on this laptop. They are not a comparison with any other
  device or with cloud AI.
- **Export** saves the run's evidence as JSON (models, timings, checks), with
  no reasoning text.
- On the **T-shirt sizing** Leader view tab, click the blue **Run live demo** button
  with the play icon to open and run the matching demo on the GPU. Use the
  separate checkboxes to select workloads for sizing. If the GPU is busy or
  not ready, the demo explains why it cannot start.

## Banking scenarios and a leave-behind deck

The **Banking scenarios** page has six fictional Zava Financial workflows: KYC
refresh, card dispute intake, model risk documentation readiness, treasury
onboarding, markets settlement breaks and wealth review preparation. Pick the one closest to the room, then:

1. **Run live workflow** to run its prompt on the GPU in front of them.
2. **Prepare deck in Forge** to stage a matching brief in Offline PPTX Forge. In
   Forge, select *<Client> - sales (custom)*, review the facts, then **Build deck**.
   Forge builds the deck locally; nothing is generated until you choose Build deck.

Say these are illustrative workflows at a fictional firm. Replace the fictional facts in Forge with real
ones only after the firm agrees and its privacy and model risk reviews allow it.

## Hybrid Intelligence: show it, then hand it to the builders

**Positioning:** Hybrid Intelligence means each request runs where it fits. Private,
fast and offline work stays on the PC; current information, organisation-wide data and
frontier scale go to the cloud. The PC does not replace the cloud.

The **Show it off** page has a five-step run of show, one button per step:

| Step | Button | Say |
|---|---|---|
| 1. Show the decision | Open the router (loads *Supplier payment with today's rates*) | "This client reply has an account number *and* needs today's exchange rates. The PC redacts the details first, so only the clean request would go to the cloud." |
| 2. Put a number on it | Run speed test | "That's how fast this model is generating on this laptop's GPU right now. One run here, not a benchmark." |
| 3. Prove it's an endpoint | Open Build on Surface | "Bank developers and data scientists call this like a cloud API, but it's on 127.0.0.1." Start the endpoint, then **Send a test call**. |
| 4. Make it theirs | Banking scenarios | Run the banking workflow closest to their role. |
| 5. Leave with a deck | Open PPTX Forge | Stage the matching brief and build the leave-behind locally. |

On the **Hybrid router**, press **Route** for the decision and its reasons (no model
runs), then **Run it on this PC** to stream the local answer or redaction from the GPU.
Every cloud payload is shown and marked **NOT SENT**: the studio never calls a cloud
service. If the redaction check blocks a payload, show that too: the PC caught it before
anything could leave. Try the *Branch ATM note while offline* preset to show the cloud step
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

## Questions that scope a pilot for the firm

1. Which repeated banker task (meeting prep, call notes, KYC refresh, settlement breaks, model documentation reviews) consumes meaningful time today?
2. Which teams handle customer records or NPI, and what is their actual connectivity pattern (branches, client sites, travel)?
3. Which model risk management, privacy, information security, third-party risk and model-license reviews must come first? These are questions for the firm's risk and legal teams.
4. Does the target shipping SKU run the required ARM64 apps, assistive technology and peripherals?
5. What quality, task-time and user-experience evidence would justify scale, and who decides?

## Right-size the model, then qualify the platform

Use the native **T-shirt sizing** planner to start the conversation, not to
declare a winning device. With line-of-business leaders, stay on the **Leader view** tab: tick the
workloads they mention (coding, transcription, translation and so on) and read
the "Say it like this" line. Switch to **Technical** only when an architect
wants the arithmetic. S (1-4B), M (7-8B), L (14-20B), XL (24-32B) and
XXL (65-72B)
describe nominal model-size bands, not accuracy tiers. Show how precision,
context, runtime/KV allowances and operating headroom change the estimated
working set. A model whose weights fit may still fail to load or be unsuitable.
Never sum the unit's installed, OS-visible and driver-reported memory views.

### Which memory configuration should they order?

Scroll below the Leader view results to **Surface Laptop Ultra configurations**. It
uses the same ticked workloads and marks one of the 24, 32, 48, 64 or 128 GB
options **Recommended**.

1. Tick what bankers, operations and technology staff will actually run. Ask whether those AI tools stay
   open together; if so, turn on **All open together**.
2. Turn on **Long documents** if they will work with long contracts, reports
   or transcripts.
3. Leave the GPU share on **Microsoft launch unit** for a bank
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
| Snapdragon | Branch walk-through or facilities notes into a reviewed summary | Windows ARM64 app and exact model/runtime bundle for the shipping SKU |
| Snapdragon | Transcription followed by a short summary | Speech pipeline, microphone/consent workflow, model license and target-specific runtime |
| Snapdragon | Image-assisted inspection | Supported vision model, image handling, human review and task-specific accuracy |
| Intel | Meeting transcription and summary | Exact Intel CPU/GPU, supported OpenVINO pipeline and audio/data policy |
| Intel | Bank service-desk or policy knowledge draft | Approved retrieval sources, citations, compatibility and response-quality gate |
| Intel | Visual-document triage | Supported vision/OCR pipeline, document formats and human-review rule |
| This RTX unit | Live 14B code draft or 20B structured reasoning | Exact cached CUDA variant, actual run evidence and output review |

These are workload-fit hypotheses, not exclusive capabilities or measured
vendor comparisons. The app runs the current RTX workflows; Snapdragon and
Intel cards are planning guidance, not demonstrations on those processors.
Speech, image processing, retrieval and external core-banking or CRM integration
are not implemented merely because a use-case card describes them.

**Presenter line:** "Right-size the workflow first. Then prove the exact model,
runtime and device together." Use the platform page's official source links
to qualify a proposed configuration; memory arithmetic alone is insufficient.

## Optional: ramp up with "Coding as a second language"

Bankers, operations and technology staff who want to follow developer conversations can take the
optional eight-lesson AI-literacy module. It teaches reading,
not writing, code, and ends with reviewing AI-written code. Use **Ask the
local coding tutor** to have the on-device coding model explain any lesson's code
in plain English. Treat its answer as a draft, like any generated output.
Hide the tab from the Banking playbook page if you do not need it.

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
studio never executes generated code. Production use still needs the firm's
own model licensing, security review and deployment plan.

**"How is AI accelerated?"** These models run on the NVIDIA RTX GPU through
CUDA. Inspect each run's model identity and execution-provider evidence.

**"Is this Microsoft 365 Copilot?"** No. This is a custom Foundry
Local prototype. Certification, licensing, integrations and availability are
separate questions.

**"Is the data completely isolated?"** The showcased model executes locally.
That is not a security or air-gap certification. Setup/catalog operations can
use the network, and minimal SDK process telemetry may remain.
Evaluate deployment controls with the firm's information security, privacy and model risk teams.

**"How much money will it save?"** The calculator estimates possible capacity,
not cash savings. Its device cost is an assumption, not a quote. Add software,
deployment, support and change-management costs in a complete business case.
Retain existing cloud subscriptions in the analysis.

**"Does it work disconnected?"** Do not claim a result not measured. Prepare
models first, then validate the exact workflow with connectivity intentionally
disabled in an approved pilot. This showcase does not change network settings.

**"Can we use real customer data?"** Not in this showcase. Every record is
synthetic. A pilot with real customer data needs the firm's privacy, information
security and model risk reviews first; this app makes no regulatory or compliance
determination.

**"Will it make credit or account decisions?"** No. Drafts support bankers,
analysts and advisors. Credit, lending, account, dispute, fraud, AML and
investment decisions stay with authorized people, and every draft is reviewed.

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
Agree acceptance gates with the firm before starting. Keep human review
in scope. Export actual run evidence and the current assumption-based case.
Use the results to determine whether to stop, adapt or scale.

**Partner opportunity:** Discovery, device selection, application compatibility
validation, deployment planning and lifecycle support, subject to the actual partner's
engagement scope. No unverified service entitlement or commercial offer is implied.
