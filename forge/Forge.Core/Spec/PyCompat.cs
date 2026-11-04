using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Forge.Core.Spec;

/// <summary>The few Python behaviours lint and grade depend on: str() and truthiness of loosely typed JSON, float() parsing,
/// round(), "%g", repr(float), and the whitespace and slicing rules. Lint messages go back to a language model and scores are
/// compared against decks the Python Forge graded, so these match Python to the last character instead of approximating it.</summary>
public static class Py
{
    /// <summary>The Python type a parsed JSON value has. A number is Int unless its text has a fraction or exponent.</summary>
    public enum Kind { None, Bool, Int, Float, Str, List, Dict }

    public static Kind KindOf(JsonNode? node)
    {
        switch (node)
        {
            case null: return Kind.None;
            case JsonObject: return Kind.Dict;
            case JsonArray: return Kind.List;
            case JsonValue v:
                if (v.TryGetValue<string>(out _)) return Kind.Str;
                string raw = v.ToJsonString();
                if (raw is "true" or "false") return Kind.Bool;
                if (raw == "null") return Kind.None;
                return raw.AsSpan().IndexOfAny(".eE") >= 0 ? Kind.Float : Kind.Int;
            default: return Kind.None;
        }
    }

    /// <summary>True for int and float, false for bool (Python's isinstance(v, (int, float)) and not bool).</summary>
    public static bool IsNumber(JsonNode? node) => KindOf(node) is Kind.Int or Kind.Float;

    public static bool Truthy(JsonNode? node) => KindOf(node) switch
    {
        Kind.None => false,
        Kind.Bool => node!.ToJsonString() == "true",
        Kind.Int => node!.ToJsonString().TrimStart('-') != "0",
        Kind.Float => ToDouble(node!) != 0,
        Kind.Str => node!.GetValue<string>().Length > 0,
        Kind.List => ((JsonArray)node!).Count > 0,
        _ => ((JsonObject)node!).Count > 0,
    };

    /// <summary>str(value).</summary>
    public static string Str(JsonNode? node) => KindOf(node) switch
    {
        Kind.None => "None",
        Kind.Bool => node!.ToJsonString() == "true" ? "True" : "False",
        Kind.Int => IntText(node!),
        Kind.Float => FloatRepr(ToDouble(node!)),
        Kind.Str => node!.GetValue<string>(),
        _ => Repr(node),
    };

    /// <summary>str(value or ""): falsy values (None, 0, "", [], {}) give the empty string.</summary>
    public static string StrOrEmpty(JsonNode? node) => Truthy(node) ? Str(node) : "";

    /// <summary>str(obj.get(key, absent)): a key that is present with a null value gives "None", a missing key gives <paramref name="absent"/>.</summary>
    public static string StrGet(JsonObject obj, string key, string absent) => obj.TryGetPropertyValue(key, out var node) ? Str(node) : absent;

    public static string Repr(JsonNode? node)
    {
        switch (KindOf(node))
        {
            case Kind.Str: return StrRepr(node!.GetValue<string>());
            case Kind.List: return "[" + string.Join(", ", ((JsonArray)node!).Select(Repr)) + "]";
            case Kind.Dict: return "{" + string.Join(", ", ((JsonObject)node!).Select(p => StrRepr(p.Key) + ": " + Repr(p.Value))) + "}";
            default: return Str(node);
        }
    }

    /// <summary>len(value) for a string (in code points), list or dict; 0 for anything else.</summary>
    public static int Len(JsonNode? node) => node switch
    {
        JsonArray a => a.Count,
        JsonObject o => o.Count,
        _ when KindOf(node) == Kind.Str => Len(node!.GetValue<string>()),
        _ => 0,
    };

    /// <summary>len(s): Python counts code points, so an emoji is one, not two UTF-16 units.</summary>
    public static int Len(string s)
    {
        int count = 0;
        for (int i = 0; i < s.Length; i++, count++)
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
        return count;
    }

    /// <summary>s[:count] by code points.</summary>
    public static string Head(string s, int count)
    {
        int i = 0;
        for (int taken = 0; i < s.Length && taken < count; taken++)
            i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
        return s[..i];
    }

    /// <summary>str.isspace for one character: .NET's whitespace plus the four ASCII separators Python also counts.</summary>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || (c >= '\x1c' && c <= '\x1f');

    public static string Strip(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsSpace(s[start])) start++;
        while (end > start && IsSpace(s[end - 1])) end--;
        return s[start..end];
    }

    /// <summary>len(s.split()).</summary>
    public static int WordCount(string s)
    {
        int words = 0;
        bool inWord = false;
        foreach (char c in s)
        {
            if (IsSpace(c)) inWord = false;
            else if (!inWord) { inWord = true; words++; }
        }
        return words;
    }

    /// <summary>float(value) with a fallback when Python would raise: None, lists and dicts, and unparseable strings.</summary>
    public static double Float(JsonNode? node, double fallback = 0.0) => KindOf(node) switch
    {
        Kind.Bool => node!.ToJsonString() == "true" ? 1.0 : 0.0,
        Kind.Int or Kind.Float => ToDouble(node!),
        Kind.Str => TryParseFloat(node!.GetValue<string>(), out double v) ? v : fallback,
        _ => fallback,
    };

    private static readonly Regex FloatSyntax = new(
        @"\A[+-]?(?:[0-9]+(?:_[0-9]+)*(?:\.(?:[0-9]+(?:_[0-9]+)*)?)?|\.[0-9]+(?:_[0-9]+)*)(?:[eE][+-]?[0-9]+(?:_[0-9]+)*)?\z",
        RegexOptions.CultureInvariant);

    /// <summary>float() strips the ASCII blanks and every Unicode space, but not the ASCII separators U+001C..U+001F that str.strip() also removes.</summary>
    private static string StripForFloat(string s)
    {
        static bool Blank(char c) => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r' || (c > '\x7f' && IsSpace(c));
        int start = 0, end = s.Length;
        while (start < end && Blank(s[start])) start++;
        while (end > start && Blank(s[end - 1])) end--;
        return s[start..end];
    }

    /// <summary>float(text): surrounding whitespace, underscores between digits, inf/infinity/nan and non-ASCII decimal digits are accepted as in Python.</summary>
    public static bool TryParseFloat(string text, out double value)
    {
        string s = AsciiDigits(StripForFloat(text));
        value = 0;
        string unsigned = s.TrimStart('+', '-');
        if (s.Length - unsigned.Length <= 1 && unsigned.Length > 0)
        {
            bool negative = s.StartsWith('-');
            if (unsigned.Equals("inf", StringComparison.OrdinalIgnoreCase) || unsigned.Equals("infinity", StringComparison.OrdinalIgnoreCase))
            {
                value = negative ? double.NegativeInfinity : double.PositiveInfinity;
                return true;
            }
            if (unsigned.Equals("nan", StringComparison.OrdinalIgnoreCase)) { value = double.NaN; return true; }
        }
        if (!FloatSyntax.IsMatch(s)) return false;
        return double.TryParse(s.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Maps every Unicode decimal digit (Arabic-Indic, fullwidth, and so on) to its ASCII digit, as float() and int() do before parsing.</summary>
    public static string AsciiDigits(string s)
    {
        if (!s.Any(c => c > '\x7f' && char.IsDigit(c))) return s;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c > '\x7f' && char.IsDigit(c) ? (char)('0' + CharUnicodeInfo.GetDecimalDigitValue(c)) : c);
        return sb.ToString();
    }

    /// <summary>round(x, digits) for digits >= 0: half-to-even on the exact binary value (so round(2.675, 2) is 2.67 and round(0.35, 1) is 0.3).</summary>
    public static double Round(double x, int digits)
    {
        if (!double.IsFinite(x) || x == 0) return x;
        var (d, e) = Exact(x);
        int shift = e + digits;
        BigInteger q;
        if (shift >= 0) q = d * BigInteger.Pow(10, shift);
        else
        {
            var divisor = BigInteger.Pow(10, -shift);
            q = BigInteger.DivRem(d, divisor, out var remainder);
            int cmp = (remainder * 2).CompareTo(divisor);
            if (cmp > 0 || (cmp == 0 && !q.IsEven)) q += 1;
        }
        string text = (x < 0 ? "-" : "") + q.ToString(CultureInfo.InvariantCulture) + "E" + (-digits).ToString(CultureInfo.InvariantCulture);
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>round(x) to an integer value, half-to-even.</summary>
    public static double Round(double x) => Math.Round(x, MidpointRounding.ToEven);

    /// <summary>format(x, "g"): six significant digits, trailing zeros dropped, exponent form below 1e-4 and from 1e6 up.</summary>
    public static string FormatG(double v, int precision = 6)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        string sign = double.IsNegative(v) ? "-" : "";
        if (v == 0) return sign + "0";
        var (d, e) = Exact(v);
        string all = d.ToString(CultureInfo.InvariantCulture);
        int exp10 = all.Length - 1 + e;
        BigInteger q;
        if (all.Length > precision)
        {
            var divisor = BigInteger.Pow(10, all.Length - precision);
            q = BigInteger.DivRem(d, divisor, out var remainder);
            int cmp = (remainder * 2).CompareTo(divisor);
            if (cmp > 0 || (cmp == 0 && !q.IsEven)) q += 1;
            if (q == BigInteger.Pow(10, precision)) { q /= 10; exp10++; }
        }
        else q = d * BigInteger.Pow(10, precision - all.Length);
        string digits = q.ToString(CultureInfo.InvariantCulture);
        string trimmed = digits.TrimEnd('0');
        if (exp10 < -4 || exp10 >= precision)
            return sign + trimmed[0] + (trimmed.Length > 1 ? "." + trimmed[1..] : "") + "e" + (exp10 < 0 ? "-" : "+")
                + Math.Abs(exp10).ToString("00", CultureInfo.InvariantCulture);
        if (exp10 >= 0)
        {
            string fraction = digits[(exp10 + 1)..].TrimEnd('0');
            return sign + digits[..(exp10 + 1)] + (fraction.Length > 0 ? "." + fraction : "");
        }
        return sign + "0." + new string('0', -exp10 - 1) + trimmed;
    }

    /// <summary>repr(float): the shortest digits that round-trip, "1e-05" and "1e+16" for exponents outside -4..15, ".0" on whole numbers.</summary>
    public static string FloatRepr(double v)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        string sign = double.IsNegative(v) ? "-" : "";
        if (v == 0) return sign + "0.0";
        string r = Math.Abs(v).ToString("R", CultureInfo.InvariantCulture);
        int ePos = r.IndexOfAny(['E', 'e']);
        string mantissa = ePos >= 0 ? r[..ePos] : r;
        int exponent = ePos >= 0 ? int.Parse(r[(ePos + 1)..], CultureInfo.InvariantCulture) : 0;
        int dot = mantissa.IndexOf('.');
        string digits = mantissa.Replace(".", "");
        int pointPos = dot >= 0 ? dot : mantissa.Length;
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..].TrimEnd('0');
        pointPos -= lead;
        int decpt = pointPos + exponent;
        if (decpt <= -4 || decpt > 16)
            return sign + digits[0] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (decpt - 1 < 0 ? "-" : "+")
                + Math.Abs(decpt - 1).ToString("00", CultureInfo.InvariantCulture);
        if (decpt <= 0) return sign + "0." + new string('0', -decpt) + digits;
        if (decpt >= digits.Length) return sign + digits + new string('0', decpt - digits.Length) + ".0";
        return sign + digits[..decpt] + "." + digits[decpt..];
    }

    /// <summary>A JSON number node that prints the way Python's json.dumps would: floats keep their ".0".</summary>
    public static JsonNode FloatNode(double v) =>
        double.IsFinite(v) ? JsonNode.Parse(FloatRepr(v))! : JsonValue.Create(FloatRepr(v));

    /// <summary>|x| as Digits * 10^Exp10, exactly.</summary>
    private static (BigInteger Digits, int Exp10) Exact(double x)
    {
        long bits = BitConverter.DoubleToInt64Bits(x) & long.MaxValue;
        int exp = (int)(bits >> 52);
        long mantissa = bits & ((1L << 52) - 1);
        if (exp == 0) exp = 1; else mantissa |= 1L << 52;
        exp -= 1075;
        if (mantissa == 0) return (BigInteger.Zero, 0);
        return exp >= 0 ? (new BigInteger(mantissa) << exp, 0) : (new BigInteger(mantissa) * BigInteger.Pow(5, -exp), exp);
    }

    private static double ToDouble(JsonNode node) => double.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string IntText(JsonNode node)
    {
        string raw = node.ToJsonString();
        return raw == "-0" ? "0" : raw;
    }

    private static string StrRepr(string s)
    {
        char quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder().Append(quote);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == quote || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\t') sb.Append("\\t");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c < ' ' || c == '\x7f') sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            else if (c < '\x7f') sb.Append(c);
            else
            {
                int length = char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
                int cp = length == 2 ? char.ConvertToUtf32(c, s[i + 1]) : c;
                var category = CharUnicodeInfo.GetUnicodeCategory(s, i);
                bool printable = category is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                    or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator);
                if (printable) sb.Append(s, i, length);
                else if (cp <= 0xff) sb.Append("\\x").Append(cp.ToString("x2", CultureInfo.InvariantCulture));
                else if (cp <= 0xffff) sb.Append("\\u").Append(cp.ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append("\\U").Append(cp.ToString("x8", CultureInfo.InvariantCulture));
                i += length - 1;
            }
        }
        return sb.Append(quote).ToString();
    }
}
