using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Forge.Core.Spec;

/// <summary>Finds and removes text in a script an English deck never uses. A small model sometimes slips into Chinese in the middle of a
/// sentence, and constrained decoding keeps the JSON valid but not the language. A run the brief itself contains (a client's name written
/// in Japanese, say) is not a slip and is left alone.</summary>
public static class Script
{
    /// <summary>True for the letters and marks of the scripts a slip lands in: Cyrillic, Hebrew, Arabic, the Indic and South-East Asian scripts,
    /// Hangul, kana, Han and the full-width forms. Latin, Greek (µ, Ω, π), symbols, digits and spaces (a full-width space included) are
    /// ordinary deck text.</summary>
    public static bool IsForeign(Rune rune)
    {
        if (Rune.IsWhiteSpace(rune) || Rune.IsDigit(rune)) return false;
        int c = rune.Value;
        return c is (>= 0x0400 and <= 0x052F) or (>= 0x0590 and <= 0x077F) or (>= 0x0900 and <= 0x109F)
            or (>= 0x1100 and <= 0x11FF) or (>= 0x3000 and <= 0x9FFF) or (>= 0xAC00 and <= 0xD7AF)
            or (>= 0xF900 and <= 0xFAFF) or (>= 0xFF00 and <= 0xFFEF) or (>= 0x20000 and <= 0x2FA1F);
    }

    /// <summary>The text the brief contains, which a deck may quote in any script.</summary>
    public static string AllowedText(Brief brief) =>
        string.Join("\n", new[] { brief.Client, brief.Audience, brief.Objective, brief.Context }.Concat(brief.Facts).Concat(brief.Guardrails));

    /// <summary>The runs of foreign text in <paramref name="text"/>. A run is a stretch of foreign characters, with any spaces between them.</summary>
    public static List<string> Runs(string? text)
    {
        var runs = new List<string>();
        if (string.IsNullOrEmpty(text)) return runs;
        var current = new StringBuilder();
        var gap = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsForeign(rune))
            {
                if (current.Length > 0) current.Append(gap);
                gap.Clear();
                current.Append(rune.ToString());
            }
            else if (current.Length > 0 && Rune.IsWhiteSpace(rune)) gap.Append(rune.ToString());
            else
            {
                if (current.Length > 0) runs.Add(current.ToString());
                current.Clear();
                gap.Clear();
            }
        }
        if (current.Length > 0) runs.Add(current.ToString());
        return runs;
    }

    private static bool Allowed(string run, string allowed) => allowed.Length > 0 && allowed.Contains(run, StringComparison.Ordinal);

    /// <summary>The first run of foreign text the brief does not contain, or "" when the text is clean.</summary>
    public static string Find(string? text, string allowed = "") => Runs(text).FirstOrDefault(r => !Allowed(r, allowed)) ?? "";

    private static readonly Regex EmptyBrackets = new(@"\(\s*\)|\[\s*\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Blanks = new(@"[ \t]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BeforePunctuation = new(@" +([,.;:!?])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DoubledComma = new(@",(?: *,)+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Edges = new(@"^[ ,;:]+|[ ,;:\-–—/]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The text without its foreign runs (the ones the brief does not contain), with the spaces and commas they leave behind tidied.</summary>
    public static string Strip(string text, string allowed = "")
    {
        var runs = Runs(text).Where(r => !Allowed(r, allowed)).Distinct().OrderByDescending(r => r.Length).ToList();
        if (runs.Count == 0) return text;
        foreach (string run in runs) text = text.Replace(run, " ", StringComparison.Ordinal);
        text = EmptyBrackets.Replace(text, " ");
        text = Blanks.Replace(text, " ");
        text = BeforePunctuation.Replace(text, "$1");
        text = DoubledComma.Replace(text, ",");
        return Edges.Replace(text, "").Trim();
    }

    /// <summary>Strips every string in the tree in place and returns how many strings changed.</summary>
    public static int Scrub(JsonNode? node, string allowed = "")
    {
        int changed = 0;
        switch (node)
        {
            case JsonObject o:
                foreach (string key in o.Select(p => p.Key).ToList())
                {
                    if (o[key] is JsonValue v && v.TryGetValue<string>(out string? s))
                    {
                        string cleaned = Strip(s, allowed);
                        if (cleaned != s) { o[key] = cleaned; changed++; }
                    }
                    else changed += Scrub(o[key], allowed);
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out string? s))
                    {
                        string cleaned = Strip(s, allowed);
                        if (cleaned != s) { a[i] = cleaned; changed++; }
                    }
                    else changed += Scrub(a[i], allowed);
                }
                break;
        }
        return changed;
    }
}
