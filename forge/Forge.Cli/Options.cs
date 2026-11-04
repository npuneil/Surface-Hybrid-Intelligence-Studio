using Forge.Core.Harness;
using Forge.Core.Ir;
using Forge.Engine;

namespace Forge.Cli;

/// <summary>The options every verb shares. A verb ignores the ones that do not apply to it.</summary>
internal sealed record Options
{
    public bool Json { get; init; }
    public bool Verbose { get; init; }
    public bool AllowDownload { get; init; }
    public string? Simulate { get; init; }
    public string? Emulate { get; init; }
    public string? Model { get; init; }
    public string? Lane { get; init; }
    public HarnessMode? Mode { get; init; }
    public int? Repairs { get; init; }
    public bool? Guidance { get; init; }
    public Theme? Theme { get; init; }
    public string? Out { get; init; }
    public bool NoPreview { get; init; }
    public bool? Calibrate { get; init; }
    public bool RetestGuidance { get; init; }
    public bool NoCancel { get; init; }
    public string? DataDir { get; init; }
    public string? AppDataDir { get; init; }
    public string? Client { get; init; }
    public string? Pitch { get; init; }
    public bool Provenance { get; init; }
    public string? BriefOut { get; init; }

    public RunRequest Request => new(Mode, Repairs, Guidance);

    public static Options Parse(CliArgs a)
    {
        bool calibrate = a.Flag("--calibrate"), skipCalibrate = a.Flag("--no-calibrate");
        if (calibrate && skipCalibrate) throw new ArgumentException("Use either --calibrate or --no-calibrate, not both.");
        return new Options
        {
            Json = a.Flag("--json"),
            Verbose = a.Flag("--verbose"),
            AllowDownload = a.Flag("--allow-download"),
            Simulate = a.Value("--simulate"),
            Emulate = a.Value("--emulate"),
            Model = a.Value("--model"),
            Lane = a.Value("--lane"),
            Mode = ParseMode(a.Value("--mode")),
            Repairs = a.Number("--repairs", 0, 3),
            Guidance = ParseGuidance(a.Value("--guidance")),
            Theme = ParseTheme(a.Value("--theme")),
            Out = a.Value("--out"),
            NoPreview = a.Flag("--no-preview"),
            Calibrate = calibrate ? true : skipCalibrate ? false : null,
            RetestGuidance = a.Flag("--retest-guidance"),
            NoCancel = a.Flag("--no-cancel"),
            DataDir = a.Value("--data"),
            AppDataDir = a.Value("--app-data"),
            Client = a.Value("--client"),
            Pitch = ParsePitch(a.Value("--pitch")),
            Provenance = a.Flag("--provenance"),
            BriefOut = a.Value("--brief-out"),
        };
    }

    private static HarnessMode? ParseMode(string? text) => text?.Trim().ToLowerInvariant().Replace("-", "") switch
    {
        null => null,
        "guided" => HarnessMode.Guided,
        "arc" => HarnessMode.Arc,
        "oneshot" => HarnessMode.OneShot,
        _ => throw new ArgumentException("--mode must be guided, arc or oneshot."),
    };

    private static bool? ParseGuidance(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null => null,
        "on" => true,
        "off" => false,
        _ => throw new ArgumentException("--guidance must be on or off."),
    };

    private static string? ParsePitch(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null => null,
        "sales" => "sales",
        "technical" => "technical",
        _ => throw new ArgumentException("--pitch must be sales or technical."),
    };

    private static Theme? ParseTheme(string? text)
    {
        if (text is null) return null;
        return Theme.All.FirstOrDefault(t => t.Key.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"--theme must be one of: {string.Join(", ", Theme.All.Select(t => t.Key))}.");
    }
}
