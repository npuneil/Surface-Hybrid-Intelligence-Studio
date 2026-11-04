using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Forge.Core.Ir;

namespace Forge.Core.Layouts;

/// <summary>The bits of Python the layouts lean on, so the port can keep the Python code's shape and its numbers: g() and
/// items() on loose JSON, truthiness, str(), float(), min/max with Python's NaN behaviour, and f"{x:,.0f}" / f"{x:g}"
/// formatting (which rounds the exact binary value half-to-even, unlike .NET's formatters).</summary>
internal static partial class LayoutPy
{
    /// <summary>x, y, w, h of the area under the title that every layout draws in.</summary>
    public static readonly (double X, double Y, double W, double H) Area = (0.6, 2.0, 12.13, 4.75);

    // ---- dict access -----------------------------------------------------------------------------------------------

    /// <summary>d.get(key): null when d is not an object, the key is missing or its value is null.</summary>
    public static JsonNode? Get(JsonNode? d, string key) => d is JsonObject o && o.TryGetPropertyValue(key, out var n) ? n : null;

    public static bool Has(JsonNode? d, string key) => d is JsonObject o && o.ContainsKey(key);

    /// <summary>The first of <paramref name="keys"/> whose value is neither null nor "" (g() in the Python), or null when there is none.</summary>
    public static JsonNode? Find(JsonNode? x, params string[] keys)
    {
        if (x is JsonObject o)
            foreach (string key in keys)
                if (o.TryGetPropertyValue(key, out var n) && n is not null && !IsEmptyString(n)) return n;
        return null;
    }

    /// <summary>g(x, *keys) as the raw value; "" when nothing is found, so Truthy/Str behave like Python's default.</summary>
    public static JsonNode Raw(JsonNode? x, params string[] keys) => Find(x, keys) ?? JsonValue.Create("")!;

    /// <summary>str(g(x, *keys)).</summary>
    public static string G(JsonNode? x, params string[] keys) => Find(x, keys) is { } n ? Str(n) : "";

    /// <summary>str(g(x, *keys, default=fallback)).</summary>
    public static string GD(JsonNode? x, string fallback, params string[] keys) => Find(x, keys) is { } n ? Str(n) : fallback;

    /// <summary>The icon name for g(x, key, default=fallback).</summary>
    public static string IconOf(JsonNode? x, string fallback, string key = "icon") => Find(x, key) is { } n ? IconName(n) : fallback;

    /// <summary>What glyph() accepts: text, or anything falsy (which becomes the default glyph). Other values are an error there.</summary>
    public static string IconName(JsonNode? raw)
    {
        if (raw is JsonValue v && v.GetValueKind() == JsonValueKind.String) return v.GetValue<string>();
        if (!Truthy(raw)) return "";
        throw new InvalidOperationException($"icon name must be text, got {TypeName(raw)}");
    }

    /// <summary>items(d, key, hi, fields): at most <paramref name="hi"/> entries; an object becomes its values, a scalar a one-item list,
    /// and a non-object entry becomes {first: str(entry)}. Python raises when d is not an object, and so does this.</summary>
    public static List<JsonObject> Items(JsonNode? d, string key, int hi, string first = "label")
    {
        if (d is not JsonObject o) throw new InvalidOperationException($"'{TypeName(d)}' object has no attribute 'get'");
        var v = Get(o, key);
        List<JsonNode?> source = !Truthy(v) ? [] : v switch
        {
            JsonObject vo => [.. vo.Select(p => p.Value)],
            JsonArray va => [.. va],
            _ => [v],
        };
        return [.. source.Take(hi).Select(x => x as JsonObject ?? new JsonObject { [first] = Str(x) })];
    }

    /// <summary>A string value is a dict with that label (the "if isinstance(x, str): x = {'label': x}" step).</summary>
    public static JsonNode? Labelled(JsonNode? x) =>
        x is JsonValue v && v.GetValueKind() == JsonValueKind.String ? new JsonObject { ["label"] = v.GetValue<string>() } : x;

    /// <summary>The operand of Python's str + str: anything else is a TypeError.</summary>
    public static string TextOnly(JsonNode? raw)
    {
        if (raw is JsonValue v && v.GetValueKind() == JsonValueKind.String) return v.GetValue<string>();
        throw new InvalidOperationException($"can only concatenate str (not \"{TypeName(raw)}\") to str");
    }

    // ---- truthiness, str(), types ----------------------------------------------------------------------------------

    private static bool IsEmptyString(JsonNode n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Length == 0;

    public static bool IsString(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String;

    /// <summary>int or float, which in Python includes bool.</summary>
    public static bool IsNumberOrBool(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;

    public static bool Truthy(JsonNode? n) => n switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.True => true,
            JsonValueKind.Number => NumberValue(v) != 0,
            _ => false,
        },
        _ => false,
    };

    public static string TypeName(JsonNode? n) => n switch
    {
        null => "NoneType",
        JsonObject => "dict",
        JsonArray => "list",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "str",
            JsonValueKind.True or JsonValueKind.False => "bool",
            JsonValueKind.Number => IsIntText(v.ToJsonString()) ? "int" : "float",
            _ => "NoneType",
        },
        _ => "object",
    };

    /// <summary>Python's str() of a JSON value: None, True, 5, 5.0, and the repr of lists and dicts.</summary>
    public static string Str(JsonNode? n) => n switch
    {
        null => "None",
        JsonArray or JsonObject => Repr(n),
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Number => NumberText(v),
            _ => "None",
        },
        _ => "None",
    };

    private static string Repr(JsonNode? n) => n switch
    {
        JsonArray a => "[" + string.Join(", ", a.Select(Repr)) + "]",
        JsonObject o => "{" + string.Join(", ", o.Select(p => StrRepr(p.Key) + ": " + Repr(p.Value))) + "}",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => StrRepr(v.GetValue<string>()),
        _ => Str(n),
    };

    private static string StrRepr(string s)
    {
        char quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder().Append(quote);
        foreach (Rune r in s.EnumerateRunes())
        {
            int cp = r.Value;
            if (cp == quote || cp == '\\') sb.Append('\\').Append((char)cp);
            else if (cp == '\n') sb.Append("\\n");
            else if (cp == '\r') sb.Append("\\r");
            else if (cp == '\t') sb.Append("\\t");
            else if (cp < 0x20 || (cp >= 0x7F && cp < 0xA1) || cp == 0xAD || !IsPrintable(r)) sb.Append(cp < 0x100 ? $"\\x{cp:x2}" : cp < 0x10000 ? $"\\u{cp:x4}" : $"\\U{cp:x8}");
            else sb.Append(r.ToString());
        }
        return sb.Append(quote).ToString();
    }

    private static bool IsPrintable(Rune r) => Rune.GetUnicodeCategory(r) is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
        or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
        or UnicodeCategory.SpaceSeparator) || r.Value == ' ';

    private static bool IsIntText(string raw) => raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0;

    private static double NumberValue(JsonValue v) => v.TryGetValue(out double d) ? d : double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture);

    /// <summary>float(n). Python integers are exact: -0 is plain 0, and one too large for a double is an error (a float literal that large is just infinity).</summary>
    private static double FloatOf(JsonValue v)
    {
        double d = NumberValue(v);
        if (!IsIntText(v.ToJsonString())) return d;
        if (double.IsInfinity(d)) throw new OverflowException("int too large to convert to float");
        return d == 0 ? 0 : d;
    }

    private static string NumberText(JsonValue v)
    {
        string raw = v.ToJsonString();
        if (IsIntText(raw)) return raw == "-0" ? "0" : raw;
        return FloatRepr(double.Parse(raw, CultureInfo.InvariantCulture));
    }

    // ---- numbers ---------------------------------------------------------------------------------------------------

    /// <summary>num(v, default): numbers and bools as they are, strings through <see cref="Num(string, double)"/>, anything else the default.</summary>
    public static double Num(JsonNode? n, double fallback = 0)
    {
        if (n is JsonValue v)
            switch (v.GetValueKind())
            {
                case JsonValueKind.Number: return FloatOf(v);
                case JsonValueKind.True: return 1;
                case JsonValueKind.False: return 0;
                case JsonValueKind.String: return Num(v.GetValue<string>(), fallback);
            }
        return fallback;
    }

    /// <summary>num() of a string: commas dropped, first space-separated word, trailing % dropped, then Python's float() grammar.</summary>
    public static double Num(string s, double fallback = 0)
    {
        string t = Strip(s.Replace(",", ""));
        int space = t.IndexOf(' ');
        if (space >= 0) t = t[..space];
        return PyFloat(t.TrimEnd('%')) ?? fallback;
    }

    /// <summary>What str.isspace() counts as white space: Unicode's, which includes the four information separators .NET leaves out.</summary>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\u001c' and <= '\u001f';

    /// <summary>s.strip()</summary>
    public static string Strip(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && IsSpace(s[a])) a++;
        while (b > a && IsSpace(s[b - 1])) b--;
        return s[a..b];
    }

    /// <summary>s.lower(): the simple lowercase mapping plus the one unconditional expansion (U+0130); Greek final sigma is not special-cased.</summary>
    public static string Lower(string s) => s.Replace("\u0130", "i\u0307").ToLowerInvariant();

    /// <summary>s.isdigit(): non-empty, every character a decimal digit or one of Unicode's other digits (superscripts, circled digits).</summary>
    public static bool IsDigitText(string s)
    {
        if (s.Length == 0) return false;
        for (int i = 0; i < s.Length; i += char.IsSurrogatePair(s, i) ? 2 : 1)
            if (CharUnicodeInfo.GetDigitValue(s, i) < 0) return false;
        return true;
    }

    /// <summary>The text float() actually parses: Unicode decimal digits become ASCII digits and Unicode spaces become spaces; any other
    /// non-ASCII character is left as '?', which no number contains.</summary>
    private static string AsciiNumber(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (Rune r in s.EnumerateRunes())
            sb.Append(r.Value < 128 ? (char)r.Value
                : Rune.IsWhiteSpace(r) ? ' '
                : Rune.GetUnicodeCategory(r) == UnicodeCategory.DecimalDigitNumber ? (char)('0' + (int)Rune.GetNumericValue(r))
                : '?');
        return sb.ToString();
    }

    [GeneratedRegex(@"^(?=\.?[0-9])(?:[0-9]+(?:_[0-9]+)*)?(?:\.(?:[0-9]+(?:_[0-9]+)*)?)?(?:[eE][+-]?[0-9]+(?:_[0-9]+)*)?$")]
    private static partial Regex FloatSyntax();

    private static double? PyFloat(string text)
    {
        string t = AsciiNumber(text).Trim(' ', '\t', '\n', '\v', '\f', '\r');
        double sign = 1;
        string body = t;
        if (body.Length > 0 && body[0] is '+' or '-')
        {
            if (body[0] == '-') sign = -1;
            body = body[1..];
        }
        if (body.Equals("inf", StringComparison.OrdinalIgnoreCase) || body.Equals("infinity", StringComparison.OrdinalIgnoreCase)) return sign * double.PositiveInfinity;
        if (body.Equals("nan", StringComparison.OrdinalIgnoreCase)) return double.NaN;
        if (!FloatSyntax().IsMatch(body)) return null;
        return double.TryParse(body.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? sign * v : null;
    }

    /// <summary>min(a, b) as Python does it: keeps a unless b &lt; a, so a NaN b is ignored.</summary>
    public static double Min(double a, double b) => b < a ? b : a;

    /// <summary>max(a, b) as Python does it: keeps a unless b &gt; a.</summary>
    public static double Max(double a, double b) => b > a ? b : a;

    /// <summary>max(values) as Python does it, for a non-empty list.</summary>
    public static double MaxOf(IEnumerable<double> values)
    {
        double best = double.NaN;
        bool first = true;
        foreach (double v in values)
        {
            if (first || v > best) best = v;
            first = false;
        }
        return best;
    }

    /// <summary>int(x): truncates toward zero; NaN and infinity are errors.</summary>
    public static double Int(double v)
    {
        if (double.IsNaN(v)) throw new ArithmeticException("cannot convert float NaN to integer");
        if (double.IsInfinity(v)) throw new OverflowException("cannot convert float infinity to integer");
        return Math.Truncate(v);
    }

    /// <summary>theme.accent(k) for a whole number of any size (Python's % is a floor modulo).</summary>
    public static string AccentAt(Theme t, double k)
    {
        var n = new BigInteger(k);
        int len = t.Accents.Length;
        return t.Accents[(int)(((n % len) + len) % len)];
    }

    public static string Mix(string a, string b, double t) => J.Mix(a, b, t);

    // ---- number formatting -----------------------------------------------------------------------------------------

    /// <summary>repr(float): the shortest text that round-trips, "5.0" for whole numbers, "1e-05" style beyond 1e16 and below 1e-4.</summary>
    public static string FloatRepr(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
        if (d == 0) return double.IsNegative(d) ? "-0.0" : "0.0";
        string s = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
        int exp = 0;
        int e = s.IndexOf('E');
        if (e >= 0)
        {
            exp = int.Parse(s[(e + 1)..], CultureInfo.InvariantCulture);
            s = s[..e];
        }
        int dot = s.IndexOf('.');
        string digits = dot >= 0 ? s.Remove(dot, 1) : s;
        int pointAt = dot >= 0 ? dot : s.Length;
        int lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0').TrimEnd('0');
        int decpt = pointAt - lead + exp;
        string sign = d < 0 ? "-" : "";
        if (decpt <= -4 || decpt > 16) return sign + ScientificText(digits, decpt - 1);
        if (decpt <= 0) return sign + "0." + new string('0', -decpt) + digits;
        if (decpt >= digits.Length) return sign + digits + new string('0', decpt - digits.Length) + ".0";
        return sign + digits[..decpt] + "." + digits[decpt..];
    }

    private static string ScientificText(string digits, int exp) =>
        (digits.Length == 1 ? digits : digits[0] + "." + digits[1..]) + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);

    /// <summary>f"{v:,.{decimals}f}" (or without the thousands separators). Rounds the exact binary value half to even, so
    /// 2.5 gives "2" and 0.125 gives "0.12" where .NET would give "3" and "0.13".</summary>
    public static string Fixed(double v, int decimals, bool thousands = false)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v < 0 ? "-inf" : "inf";
        string digits = ScaledRound(Math.Abs(v), decimals).ToString(CultureInfo.InvariantCulture);
        if (digits.Length <= decimals) digits = digits.PadLeft(decimals + 1, '0');
        string whole = digits[..^decimals];
        if (thousands) whole = Thousands(whole);
        return (double.IsNegative(v) ? "-" : "") + whole + (decimals > 0 ? "." + digits[^decimals..] : "");
    }

    private static string Thousands(string whole)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < whole.Length; i++)
        {
            if (i > 0 && (whole.Length - i) % 3 == 0) sb.Append(',');
            sb.Append(whole[i]);
        }
        return sb.ToString();
    }

    /// <summary>f"{v:g}": six significant digits, trailing zeros dropped, exponent form below 1e-4 and from 1e6.</summary>
    public static string General(double v, int precision = 6)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v < 0 ? "-inf" : "inf";
        string sign = double.IsNegative(v) ? "-" : "";
        if (v == 0) return sign + "0";
        double a = Math.Abs(v);
        int exp10 = (int)Math.Floor(Math.Log10(a));
        BigInteger low = BigInteger.Pow(10, precision - 1), high = low * 10;
        BigInteger n;
        while (true)
        {
            n = ScaledRound(a, precision - 1 - exp10);
            if (n >= high) exp10++;
            else if (n < low) exp10--;
            else break;
        }
        string digits = n.ToString(CultureInfo.InvariantCulture).TrimEnd('0');
        int decpt = exp10 + 1;
        if (decpt <= -4 || decpt > precision) return sign + ScientificText(digits, decpt - 1);
        if (decpt <= 0) return sign + "0." + new string('0', -decpt) + digits;
        if (decpt >= digits.Length) return sign + digits + new string('0', decpt - digits.Length);
        return sign + digits[..decpt] + "." + digits[decpt..];
    }

    /// <summary>round-half-even(a * 10^scale) for a finite a >= 0, computed on the exact binary value.</summary>
    private static BigInteger ScaledRound(double a, int scale)
    {
        long bits = BitConverter.DoubleToInt64Bits(a);
        int biased = (int)((bits >> 52) & 0x7FF);
        BigInteger mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (biased == 0) biased = 1; else mantissa |= 1L << 52;
        int exp2 = biased - 1075;
        BigInteger num = mantissa, den = BigInteger.One;
        if (exp2 >= 0) num <<= exp2; else den <<= -exp2;
        if (scale >= 0) num *= BigInteger.Pow(10, scale); else den *= BigInteger.Pow(10, -scale);
        var q = BigInteger.DivRem(num, den, out var rem);
        int cmp = (rem * 2).CompareTo(den);
        return cmp > 0 || (cmp == 0 && !q.IsEven) ? q + 1 : q;
    }
}
