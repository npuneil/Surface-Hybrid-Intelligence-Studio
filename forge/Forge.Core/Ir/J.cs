using System.Globalization;
using System.Text.Json.Nodes;

namespace Forge.Core.Ir;

/// <summary>Helpers for the loose JSON the layouts read. They mirror the Python Forge's g(), items(), num() and mix()
/// so a layout port reads the same way: a missing or wrongly typed field never throws, it falls back.</summary>
public static class J
{
    /// <summary>First of <paramref name="keys"/> whose value is present and not an empty string, as text. Otherwise "".</summary>
    public static string G(JsonNode? x, params string[] keys) => GD(x, "", keys);

    /// <summary>Like <see cref="G"/> with an explicit default.</summary>
    public static string GD(JsonNode? x, string fallback, params string[] keys)
    {
        if (x is JsonObject o)
            foreach (string key in keys)
                if (o.TryGetPropertyValue(key, out var node) && Text(node) is { Length: > 0 } s) return s;
        return fallback;
    }

    /// <summary>The raw node for the first key that is present and not null.</summary>
    public static JsonNode? Node(JsonNode? x, params string[] keys)
    {
        if (x is JsonObject o)
            foreach (string key in keys)
                if (o.TryGetPropertyValue(key, out var node) && node is not null) return node;
        return null;
    }

    /// <summary>A JSON value as the text Python's str() would give for scalars.</summary>
    public static string Text(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out string? s)) return s ?? "";
            if (v.TryGetValue<bool>(out bool b)) return b ? "True" : "False";
            if (v.TryGetValue<long>(out long l)) return l.ToString(CultureInfo.InvariantCulture);
            if (v.TryGetValue<double>(out double d)) return d.ToString("R", CultureInfo.InvariantCulture);
            return v.ToString();
        }
        return node.ToJsonString();
    }

    /// <summary>The list under <paramref name="key"/>, at most <paramref name="hi"/> entries. A dictionary becomes its values,
    /// a scalar becomes a one-item list, and a non-object entry becomes {firstField: text}.</summary>
    public static List<JsonObject> Items(JsonNode? d, string key, int hi, params string[] fields)
    {
        string first = fields.Length > 0 ? fields[0] : "label";
        var output = new List<JsonObject>();
        if (d is not JsonObject o || !o.TryGetPropertyValue(key, out var value) || value is null) return output;
        IEnumerable<JsonNode?> source = value switch
        {
            JsonArray a => a,
            JsonObject obj => obj.Select(p => p.Value),
            _ => [value],
        };
        foreach (var x in source.Take(hi))
            output.Add(x is JsonObject xo ? xo : new JsonObject { [first] = Text(x) });
        return output;
    }

    /// <summary>A number from a JSON value or string ("1,200 tok/s" gives 1200, "40%" gives 40). Anything else gives the default.</summary>
    public static double Num(JsonNode? node, double fallback = 0)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out double d)) return d;
            if (v.TryGetValue<long>(out long l)) return l;
            if (v.TryGetValue<string>(out string? s)) return Num(s, fallback);
        }
        return fallback;
    }

    public static double Num(string? s, double fallback = 0)
    {
        if (s is null) return fallback;
        string t = s.Replace(",", "").Trim();
        int space = t.IndexOf(' ');
        if (space >= 0) t = t[..space];
        t = t.TrimEnd('%');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
    }

    /// <summary>The number at <paramref name="key"/> in a JSON object.</summary>
    public static double NumAt(JsonNode? d, string key, double fallback = 0) => d is JsonObject o && o.TryGetPropertyValue(key, out var n) ? Num(n, fallback) : fallback;

    public static bool Flag(JsonNode? d, string key) =>
        d is JsonObject o && o.TryGetPropertyValue(key, out var n) && n is JsonValue v
        && (v.TryGetValue<bool>(out bool b) ? b : v.TryGetValue<string>(out string? s) && s is { Length: > 0 } && !s.Equals("false", StringComparison.OrdinalIgnoreCase));

    /// <summary>Blend hex colour <paramref name="a"/> toward <paramref name="b"/> by <paramref name="t"/> (0 to 1).</summary>
    public static string Mix(string a, string b, double t)
    {
        a = a.TrimStart('#'); b = b.TrimStart('#');
        var result = new System.Text.StringBuilder(6);
        for (int i = 0; i < 6; i += 2)
        {
            int x = Convert.ToInt32(a.Substring(i, 2), 16), y = Convert.ToInt32(b.Substring(i, 2), 16);
            result.Append(((int)Math.Round(x + (y - x) * t, MidpointRounding.ToEven)).ToString("X2", CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    /// <summary>A point on an ellipse: angle starts at <paramref name="start"/> degrees and goes clockwise in steps of 360/n.</summary>
    public static (double X, double Y) Polar(double cx, double cy, double rx, double ry, int i, int n, double start = -90)
    {
        double a = (start + 360.0 * i / n) * Math.PI / 180.0;
        return (cx + rx * Math.Cos(a), cy + ry * Math.Sin(a));
    }

    /// <summary>Visible word count the way the Python Forge counts it.</summary>
    public static int Words(string? s) =>
        (s ?? "").Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(w => w.Trim().Length > 0);
}
