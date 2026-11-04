using System.Text.Json.Nodes;
using Forge.Core.Ir;
using Forge.Core.Spec;

namespace Forge.Core.Harness;

/// <summary>How a deck is written. <see cref="Guided"/> asks the model for one slide at a time with a fixed layout and a schema that
/// guarantees the shape: the mode that works on every device class. <see cref="Arc"/> is harness v1.1 (the whole story in one call)
/// and <see cref="OneShot"/> is harness v1.0; both need a large model with room in its context window.</summary>
public enum HarnessMode { Guided, Arc, OneShot }

public sealed record HarnessOptions
{
    public HarnessMode Mode { get; init; } = HarnessMode.Guided;
    public int MaxRepairPasses { get; init; } = 2;
    public int MaxRepairsPerPass { get; init; } = 6;
    /// <summary>Visible words on a slide above which the lint flags it. The prompt asks for 30 or fewer and the grade rewards a light deck,
    /// but a finding costs a model call and a model that wrote 43 words rarely cuts to 32, so only a clearly text-heavy slide is flagged.</summary>
    public int WordLimit { get; init; } = 48;
    /// <summary>Lets the runtime constrain decoding to a schema. Turned off by the harness itself when a runtime rejects it.</summary>
    public bool UseGuidance { get; init; } = true;
    /// <summary>The theme used for the dry render that lint reads; the real render may use another with the same geometry.</summary>
    public Theme? Theme { get; init; }
}

public sealed record HarnessProgress(string Phase, int Step, int Total, string Message, int Tokens);

/// <summary>Measures energy while a deck is being drafted. Only a real measurement counts: a meter that cannot measure reports so.</summary>
public interface IEnergyMeter
{
    void Start();
    EnergyReading Stop();
}

public sealed record EnergyReading(bool Measured, double Wh, string Basis)
{
    public static EnergyReading NotMeasured(string reason) => new(false, 0, reason);
}

public sealed class HarnessStats
{
    public int LlmCalls { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public double GenerateSeconds { get; set; }
    public double DecodeSeconds { get; set; }
    public int DecodeTokens { get; set; }
    public double FirstTokenSeconds { get; set; }
    public bool TokensMeasured { get; set; } = true;
    public int Retries { get; set; }

    /// <summary>Decode speed across all calls, tokens per second; zero when nothing was measured.</summary>
    public double DecodeTokensPerSecond => DecodeTokens > 0 && DecodeSeconds > 0.05 ? DecodeTokens / DecodeSeconds : 0;
}

public sealed record RepairRecord(int Pass, int IssuesBefore, int IssuesAfter, bool Accepted, IReadOnlyList<int> FixedSlides, IReadOnlyList<string> Codes);

public sealed class HarnessResult
{
    public required JsonObject Spec { get; init; }
    /// <summary>The text the model wrote (every slide's reply joined, for guided decks).</summary>
    public required string Raw { get; init; }
    public bool StrictJson { get; init; } = true;
    /// <summary>The brief as the model saw it: device facts added and, when a measured slide was inserted, the measured fact.</summary>
    public required Brief Brief { get; init; }
    public HarnessMode Mode { get; init; }
    public bool GuidedDecoding { get; init; }
    public int ContextLevel { get; init; }
    public required HarnessStats Stats { get; init; }
    public List<RepairRecord> Repairs { get; init; } = [];
    /// <summary>What the lint found in the draft before any repair.</summary>
    public List<LintIssue> DraftIssues { get; init; } = [];
    public List<LintIssue> LintRemaining { get; init; } = [];
    public GradeResult? Grade { get; init; }
    public IReadOnlyList<SlideSummary> RenderSummary { get; init; } = [];
    public EnergyReading Energy { get; init; } = EnergyReading.NotMeasured("no meter");
    /// <summary>The sentence added to the graded brief for the measured slide; null when the deck has none.</summary>
    public string? MeasuredFact { get; init; }
    public double DraftSeconds { get; init; }
    public LlmInfo Model { get; init; } = LlmInfo.Unknown;
    public List<string> Warnings { get; init; } = [];
}

/// <summary>The harness could not produce a deck, for example because the model's window cannot hold even the smallest prompt.</summary>
public sealed class HarnessException(string message, Exception? inner = null) : Exception(message, inner);
