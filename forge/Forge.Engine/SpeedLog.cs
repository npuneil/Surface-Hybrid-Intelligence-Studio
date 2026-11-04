using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Engine;

/// <summary>Whether a model on this PC has been seen to follow a JSON schema while decoding.</summary>
public enum GuidanceState { Unknown, Verified, Rejected }

/// <summary>What one real slide measured for one model variant on this PC.</summary>
public sealed record SpeedEntry
{
    public string Variant { get; init; } = "";
    public string Model { get; init; } = "";
    public string Lane { get; init; } = "";
    public string Ep { get; init; } = "";
    public double DecodeTokensPerSecond { get; init; }
    public double PrefillTokensPerSecond { get; init; }
    public double FirstTokenSeconds { get; init; }
    public int SlideTokens { get; init; }
    public GuidanceState Guidance { get; init; }
    public DateTimeOffset MeasuredAt { get; init; }
    public int Samples { get; init; } = 1;
}

/// <summary>One finished deck: how long the model took to draft it.</summary>
public sealed record DeckRunRecord(string Variant, int Slides, double Seconds, string Mode, DateTimeOffset When);

public sealed record Eta(double Seconds, string Basis);

/// <summary>What this PC has measured about itself, kept between sessions so the next deck can say how long it will take and which
/// settings work here. Lives in the app's data folder; a missing or damaged file is just an empty log.</summary>
public sealed class SpeedLog
{
    public const int KeepRuns = 40;
    /// <summary>Slides take a little longer than one clean draft: repair passes and the longer prompts of later slides.</summary>
    public const double RepairAllowance = 1.25;
    private const int MaxWeight = 4;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, SpeedEntry> _entries = new(StringComparer.Ordinal);
    private readonly List<DeckRunRecord> _runs = [];

    public SpeedLog(string? path = null) => Path = path;

    public string? Path { get; }

    /// <summary>Why the file could not be read or written, for the log pane. Empty when all is well.</summary>
    public string Problem { get; private set; } = "";

    private sealed class FileShape
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, SpeedEntry> Variants { get; set; } = [];
        public List<DeckRunRecord> Runs { get; set; } = [];
    }

    public static SpeedLog Load(string path)
    {
        var log = new SpeedLog(path);
        if (!File.Exists(path)) return log;
        try
        {
            var file = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Json);
            foreach (var (key, entry) in file?.Variants ?? [])
                if (entry is not null && key.Length > 0) log._entries[key] = entry with { Variant = key };
            log._runs.AddRange((file?.Runs ?? []).Where(r => r is not null && r.Slides > 0 && r.Seconds > 0).TakeLast(KeepRuns));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log.Problem = $"The speed log at {path} could not be read ({exception.Message.Split('\n')[0].Trim()}); starting a new one.";
            log._entries.Clear();
            log._runs.Clear();
        }
        return log;
    }

    public SpeedEntry? Entry(string variant)
    {
        lock (_gate) return _entries.GetValueOrDefault(variant);
    }

    public IReadOnlyList<DeckRunRecord> Runs
    {
        get { lock (_gate) return [.. _runs]; }
    }

    /// <summary>Folds a new measurement into the old one: speeds are averaged over the last few samples so one noisy slide does not rule, and
    /// a new answer about constrained decoding replaces the old one while "unknown" never erases it.</summary>
    public SpeedEntry Record(SpeedEntry measured)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(measured.Variant, out var old))
            {
                int weight = Math.Min(old.Samples, MaxWeight);
                double Blend(double before, double now) => now <= 0 ? before : before <= 0 ? now : (before * weight + now) / (weight + 1);
                measured = measured with
                {
                    DecodeTokensPerSecond = Blend(old.DecodeTokensPerSecond, measured.DecodeTokensPerSecond),
                    PrefillTokensPerSecond = Blend(old.PrefillTokensPerSecond, measured.PrefillTokensPerSecond),
                    FirstTokenSeconds = Blend(old.FirstTokenSeconds, measured.FirstTokenSeconds),
                    SlideTokens = measured.SlideTokens > 0 ? (int)Math.Round(Blend(old.SlideTokens, measured.SlideTokens)) : old.SlideTokens,
                    Guidance = measured.Guidance == GuidanceState.Unknown ? old.Guidance : measured.Guidance,
                    Samples = old.Samples + 1,
                };
            }
            _entries[measured.Variant] = measured;
            return measured;
        }
    }

    public void RecordRun(DeckRunRecord run)
    {
        if (run.Slides <= 0 || run.Seconds <= 0) return;
        lock (_gate)
        {
            _runs.Add(run);
            if (_runs.Count > KeepRuns) _runs.RemoveRange(0, _runs.Count - KeepRuns);
        }
    }

    /// <summary>How long a deck of this many slides should take. Earlier decks on this model beat an estimate from one slide; with neither, null.</summary>
    public Eta? Estimate(string variant, int slides)
    {
        if (slides <= 0) return null;
        lock (_gate)
        {
            var runs = _runs.Where(r => r.Variant == variant).TakeLast(5).ToList();
            if (runs.Count > 0)
            {
                double perSlide = Median(runs.Select(r => r.Seconds / r.Slides));
                return new Eta(perSlide * slides, $"median of {runs.Count} earlier deck{(runs.Count == 1 ? "" : "s")} on this PC");
            }
            if (_entries.TryGetValue(variant, out var entry) && entry is { DecodeTokensPerSecond: > 0, SlideTokens: > 0 })
            {
                double perSlide = (entry.FirstTokenSeconds + entry.SlideTokens / entry.DecodeTokensPerSecond) * RepairAllowance;
                return new Eta(perSlide * slides, "estimated from one measured slide");
            }
            return null;
        }
    }

    /// <summary>Writes the log next to the app's other data. Writes a temporary file first so a crash never leaves half a log.</summary>
    public bool Save()
    {
        if (Path is null) return false;
        string json;
        lock (_gate) json = JsonSerializer.Serialize(new FileShape { Variants = new(_entries), Runs = [.. _runs] }, Json);
        try
        {
            string? folder = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            string temp = Path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, Path, overwrite: true);
            Problem = "";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Problem = $"The speed log could not be saved: {exception.Message}";
            return false;
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
