namespace HybridIntelligenceStudio;

internal sealed record SourceLink(string Label, string Url);

internal sealed record PlatformUseCase(string Title, TShirtSize Size, string Workflow, string Caveat, string? LiveScenarioKey);

internal sealed record PlatformProfile(
    string Id,
    string Name,
    string Tagline,
    bool CurrentDevice,
    string Status,
    string AcceleratorAndRuntime,
    PlatformUseCase[] UseCases,
    string[] VerifyBeforePilot,
    SourceLink[] Sources);

internal static class ApprovedLinks
{
    internal const string QualcommGenie = "https://github.com/qualcomm/ai-hub-apps/tree/main/tutorials/llm_on_genie";
    internal const string OpenVinoGenAi = "https://github.com/openvinotoolkit/openvino.genai";
    internal const string OpenVinoRequirements = "https://docs.openvino.ai/2026/about-openvino/release-notes-openvino/system-requirements.html";
    internal const string FoundryLocalSdk = "https://learn.microsoft.com/en-us/azure/foundry-local/reference/reference-sdk-current";
    internal const string QualcommWhisperBase = "https://github.com/qualcomm/ai-hub-models/tree/main/src/qai_hub_models/models/whisper_base";
    internal const string SurfaceLaptopUltraSpecs = "https://learn.microsoft.com/en-us/surface/tech-specs/surface-laptop-ultra-1stedition";

    internal static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        QualcommGenie, OpenVinoGenAi, OpenVinoRequirements, FoundryLocalSdk, QualcommWhisperBase, SurfaceLaptopUltraSpecs
    };

    /// <summary>Only exact, allow-listed HTTPS documentation URLs may be opened.</summary>
    internal static bool IsApproved(string? url) =>
        url is not null && All.Contains(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}

internal static class PlatformGuide
{
    internal const string FoundryDistinction =
        "The Foundry Local SDK reference lists Windows execution providers including CUDA, Vitis, QNN, OpenVINO and TensorRT. " +
        "A listed provider does not prove that every model works with every provider or device; verify the exact model variant per SKU.";

    internal const string GenieDistinction =
        "Qualcomm's target-specific QAIRT deployments (GenieX, or a validated legacy Genie bundle) are a separate, valid path; " +
        "Foundry Local is not the only way to run models on Windows. Qualcomm notes older Genie support is being deprecated in favour of GenieX.";

    internal const string NoInferenceHere =
        "Everything that runs live in this app runs on this laptop's RTX GPU through CUDA. Snapdragon and Intel examples are recommended pilots " +
        "described from official documentation; they were not run on this laptop, and this app never switches providers automatically.";

    internal const string RuntimeNotInferred =
        "Runtime libraries and provider support are never inferred from the CPU or GPU vendor name; each pilot names its runtime explicitly.";

    internal static readonly PlatformProfile[] Platforms =
    [
        new("rtx", "NVIDIA RTX - this Surface Laptop Ultra", "Heavier local models for builders and analysts",
            true,
            "Running live on this laptop: Qwen2.5-Coder 14B and GPT-OSS 20B CUDA variants via Foundry Local",
            "RTX GPU / CUDA execution provider (Foundry Local). One model resident at a time.",
            [
                new("Code drafting with a 14B coder", TShirtSize.L,
                    "Turn a short requirement into a reviewed first draft with tests; the draft is displayed, never executed.",
                    "Human review required; generated code is a draft.", "code"),
                new("Executive briefing from source notes", TShirtSize.L,
                    "Draft cited discovery questions from synthetic account notes, with a benefits-unproven close.",
                    "Facts limited to supplied sources.", "brief"),
                new("20B reasoning and model prototyping", TShirtSize.L,
                    "Switch to GPT-OSS 20B for reasoning-style drafts; private reasoning is counted, never shown or saved.",
                    "Larger XL models are memory estimates only in this app.", null),
            ],
            [
                "Model variant uses the CUDA execution provider (shown in run evidence)",
                "One model resident at a time; switching unloads the previous model",
                "Run metrics come from the actual run, not from a benchmark",
            ],
            [new("Foundry Local SDK reference", ApprovedLinks.FoundryLocalSdk)]),
        new("snapdragon", "Snapdragon X-series Windows PCs", "Mobile field teams with intermittent connectivity",
            false,
            "Recommended pilot (illustrative) - not run on this laptop",
            "An approved target-specific QAIRT deployment (GenieX, or a validated legacy Genie bundle), or a Foundry Local QNN variant where one exists for the exact model. Supported accelerator and runtime verified per SKU.",
            [
                new("Field-service note drafting, offline after preparation", TShirtSize.S,
                    "A small text model (Qualcomm's Windows tutorial uses Qwen3-4B) drafts a shift handover from technician notes. Model and runtime bundle are staged before going offline.",
                    "Bundle must list the exact device as compatible; preparation needs connectivity.", "field"),
                new("Speech transcription pilot (future)", TShirtSize.S,
                    "Transcribe short dictated notes for later drafting. Qualcomm publishes a Whisper-Base export reference with a Voice AI SDK deployment route.",
                    "Future pilot guidance only; no audio capture or transcription is implemented in this app.", null),
                new("Image-assisted inspection notes", TShirtSize.S,
                    "A small vision model suggests a description of an inspection photo for the technician to confirm.",
                    "Vision model and runtime support must be verified per SKU; human confirmation required.", null),
            ],
            [
                "Device appears in the model card's compatible-device list",
                "Minimum QAIRT version met; compile and run with the same QAIRT version",
                "Deployment route confirmed (GenieX preferred; legacy Genie only if validated)",
                "Vendor memory guidance (12 GB+ for 3B+ models, 16 GB+ for 7B+ at 4,096 context) is model-specific, not a planner fit result",
                "Measure task time, accuracy and user experience in the pilot",
            ],
            [
                new("Qualcomm AI Hub: LLM on Genie (Windows)", ApprovedLinks.QualcommGenie),
                new("Qualcomm Whisper-Base export", ApprovedLinks.QualcommWhisperBase),
                new("Foundry Local SDK reference", ApprovedLinks.FoundryLocalSdk),
            ]),
        new("intel", "Intel Core Ultra Windows PCs", "Knowledge workers, meetings and service desks",
            false,
            "Recommended pilot (illustrative) - not run on this laptop",
            "OpenVINO GenAI pipelines with an explicitly selected CPU or GPU device, or a Foundry Local OpenVINO variant where one exists. Supported accelerator and runtime verified per SKU.",
            [
                new("Meeting transcription and action summary", TShirtSize.S,
                    "A Whisper-class speech pipeline transcribes, then a small Qwen or Llama model drafts an action register for review.",
                    "Exported model, operator and device support must be verified.", "meeting"),
                new("Service desk and knowledge drafts", TShirtSize.M,
                    "A 7-8B model drafts replies and knowledge articles from approved internal content.",
                    "Content governance and accuracy review stay with the customer.", null),
                new("Visual document triage", TShirtSize.S,
                    "A small vision-language pipeline proposes a category for scanned forms before human routing.",
                    "Requires a supported exported vision model on the installed CPU/GPU runtime.", null),
            ],
            [
                "Device listed in the OpenVINO system requirements for the release used",
                "Pipeline device (CPU or GPU) selected explicitly and recorded",
                "Exported model and operators validated on the target SKU",
                "Measure task time, accuracy and user experience in the pilot",
            ],
            [
                new("OpenVINO GenAI", ApprovedLinks.OpenVinoGenAi),
                new("OpenVINO system requirements", ApprovedLinks.OpenVinoRequirements),
                new("Foundry Local SDK reference", ApprovedLinks.FoundryLocalSdk),
            ]),
    ];

    internal static IEnumerable<string> AllText() => Platforms.SelectMany(p =>
        new[] { p.Name, p.Tagline, p.Status, p.AcceleratorAndRuntime }
            .Concat(p.UseCases.SelectMany(u => new[] { u.Title, u.Workflow, u.Caveat }))
            .Concat(p.VerifyBeforePilot)
            .Concat(p.Sources.Select(s => s.Label)))
        .Concat([FoundryDistinction, GenieDistinction, NoInferenceHere, RuntimeNotInferred]);

    internal static readonly string[] ForbiddenClaims =
        ["NPU", "battery", "faster", "slower", "outperform", "savings", "replace", "only Foundry", "guarantee"];
}
