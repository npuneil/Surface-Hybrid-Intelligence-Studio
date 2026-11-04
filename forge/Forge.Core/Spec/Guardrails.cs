using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Forge.Core.Spec;

/// <summary>The claims a pilot deck must not make, matched on slide text and speaker notes. Grade skips matches that sit
/// after a negation ("not an air-gap claim" is compliant); lint flags every match so the model rewrites it positively.</summary>
public static class Guardrails
{
    public const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Rubric guardrails in the order grade and lint check them. Names go into messages and the grade detail.</summary>
    public static IReadOnlyList<(string Name, string Pattern)> Patterns { get; } =
    [
        ("air-gap claim", @"air[- ]?gap"),
        ("TOPS claim", @"\bTOPS\b"),
        ("Copilot+ PC claim", @"copilot\s?(\+|plus)\s?(pc|certif)"),
        ("dollar figure", @"\$\s?\d"),
        ("ROI claim", @"\bROI\b"),
        ("replacement claim", @"replac\w*\s+(microsoft 365 |m365 )?copilot|replac\w*\s+(the )?subscription"),
        ("dedicated VRAM claim", @"dedicated vram|128\s?gb (of )?vram|192\s?gb"),
        ("certified offline claim", @"certified offline|fully offline guaranteed"),
    ];

    /// <summary>Extra checks lint adds to the rubric ones; grade does not score them.</summary>
    public static IReadOnlyList<(string Name, string Pattern)> ExtraBanned { get; } =
    [
        ("unhedged offline claim", @"works (fully )?offline|runs (fully )?offline|no internet (needed|required)"),
    ];

    public static Regex Negation { get; } = new(
        @"\b(not|no|never|without|nor|isn't|aren't|doesn't|don't|avoid|instead of|rather than)\b", Options);

    private static readonly Regex SentenceBreak = new(@"(?<=[.!?;])[\s\x1c-\x1f]+", RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, Regex> Compiled = new(StringComparer.Ordinal);

    public static Regex Compile(string pattern) => Compiled.GetOrAdd(pattern, p => new Regex(p, Options));

    /// <summary>Matches that are asserted, not negated: the sentence text (trimmed, at most 90 characters) of every match that has no
    /// negation word earlier in its sentence.</summary>
    public static List<string> AffirmativeHits(string pattern, IEnumerable<string> texts)
    {
        var regex = Compile(pattern);
        var hits = new List<string>();
        foreach (string text in texts)
            foreach (string sentence in SentenceBreak.Split(text))
                foreach (Match m in regex.Matches(sentence))
                    if (!Negation.IsMatch(sentence[..m.Index]))
                        hits.Add(Py.Head(Py.Strip(sentence), 90));
        return hits;
    }
}
