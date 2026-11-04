using System.Text.Json.Nodes;
using Forge.Core.Draw;
using Forge.Core.Ir;
using static Forge.Core.Layouts.LayoutPy;

namespace Forge.Core.Layouts;

/// <summary>cta, statement and the free-form canvas DSL.</summary>
internal static class StoryLayouts
{
    public static void Cta(Canvas c, JsonNode d)
    {
        var (x, y, w, h) = Area;
        var t = c.T;
        var steps = Items(d, "steps", 4);
        var ask = Raw(d, "ask", "call_to_action");
        bool hasAsk = Truthy(ask);
        int n = Math.Max(1, steps.Count);
        double gap = 0.3;
        double cw = (w - gap * (n - 1)) / n;
        double ch = hasAsk ? h - 1.55 : h;
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            double xx = x + i * (cw + gap);
            string col = t.Accent(i);
            c.Box(xx, y, cw, ch, fill: t.Card, line: t.Dark ? Mix(col, t.Card, 0.5) : t.CardLine, shadow: true);
            c.Box(xx + 0.3, y + 0.3, 0.7, 0.7, fill: col, shape: "ellipse", glow: t.Dark ? new Glow(col, 8, 40) : null);
            c.Text(xx + 0.3, y + 0.3, 0.7, 0.7, (i + 1).ToString(), size: 22, font: t.FontHead, color: t.OnAccent, align: "c",
                anchor: "m", role: "ornament");
            c.Icon(xx + cw - 0.8, y + 0.38, 0.52, IconOf(s, "check"), col);
            c.Text(xx + 0.3, y + 1.15, cw - 0.6, 0.85, G(s, "label", "name"), size: 20, font: t.FontHead, role: "label");
            c.Text(xx + 0.3, y + 2.0, cw - 0.6, ch - 2.15, G(s, "detail", "desc"), size: 15, color: t.Subtext, role: "detail");
        }
        if (hasAsk)
        {
            double ay = y + h - 1.25;
            c.Box(x, ay, w, 1.25, fill: t.Primary, glow: t.Dark ? new Glow(t.Primary, 14, 40) : null,
                gradient: t.Key != "microsoft" ? (t.Primary, Mix(t.Primary, t.Accent(1), 0.6)) : null, gradientAngle: 0);
            c.Icon(x + 0.4, ay + 0.35, 0.55, IconOf(d, "flag", "ask_icon"), t.OnAccent);
            c.Text(x + 1.2, ay + 0.12, w - 1.5, 1.0, Str(ask), size: 24, font: t.FontHead, color: t.OnAccent, anchor: "m",
                role: "takeaway");
        }
    }

    public static void Statement(Canvas c, JsonNode d)
    {
        var (x, y, w, _) = Area;
        var t = c.T;
        c.Badge(x + w / 2, y + 0.75, 1.3, IconOf(d, "idea"), fill: t.Primary, glow: new Glow(t.Primary, 18, 50));
        c.Text(x + 0.8, y + 1.65, w - 1.6, 1.8, G(d, "text", "statement", "headline"), size: 40, font: t.FontHead,
            align: "c", anchor: "m", role: "label");
        var sub = Raw(d, "sub", "subtext");
        if (Truthy(sub))
            c.Text(x + 1.5, y + 3.55, w - 3.0, 0.9, Str(sub), size: 18, color: t.Subtext, align: "c", role: "detail");
    }

    /// <summary>The (fill, text) colours of a canvas element's "tone".</summary>
    private static (string Fill, string Fg) Tone(Theme t, JsonNode? raw)
    {
        string tone = Lower(Truthy(raw) ? Str(raw) : "card");
        if (tone == "primary") return (t.Primary, t.OnAccent);
        if (tone.StartsWith("accent", StringComparison.Ordinal)) return (AccentAt(t, Int(Num(tone[6..], 1))), t.OnAccent);
        if (tone == "muted") return (Mix(t.Muted, t.Bg, 0.2), t.Dark ? t.OnAccent : "FFFFFF");
        if (tone == "text") return (t.Text, t.Bg);
        return (t.Card, t.Text);
    }

    private static bool IsText(JsonNode? n, string text) => n is JsonValue v && IsString(v) && v.GetValue<string>() == text;

    /// <summary>Free-form infographic DSL on a 12x6 grid: box | pill | circle | hexagon | chevron | arrow | line | text | icon.</summary>
    public static void CanvasDsl(Canvas c, JsonNode d)
    {
        var (x, y, w, h) = Area;
        var t = c.T;
        double gx = w / 12.0, gy = h / 6.0;
        var raw = Get(d, "elements");
        List<JsonNode?> els = Truthy(raw) && raw is JsonArray arr ? [.. arr.Take(40)] : [];
        (double, double) P(double px, double py) => (x + Max(0, Min(12, px)) * gx, y + Max(0, Min(6, py)) * gy);

        string TypeOf(JsonNode? e, string fallback) => Lower(Has(e, "type") ? Str(Get(e, "type")) : fallback);
        var order = Enumerable.Range(0, els.Count)
            .OrderBy(i => els[i] is JsonObject ? (TypeOf(els[i], "") is "arrow" or "line" ? 0 : 1)
                : throw new InvalidOperationException($"'{TypeName(els[i])}' object has no attribute 'get'"))
            .ToList();

        foreach (int i in order)
        {
            var e = (JsonObject)els[i]!;
            string typ = TypeOf(e, "box");
            var (ex, ey) = P(Num(Get(e, "x")), Num(Get(e, "y")));
            double ew = Max(0.3, Min(12, Num(Get(e, "w"), 2))) * gx;
            double eh = Max(0.3, Min(6, Num(Get(e, "h"), 1))) * gy;
            var tone = Get(e, "tone");
            var (fill, fg) = Tone(t, tone);
            var label = Raw(e, "label", "text");
            bool hasLabel = Truthy(label);
            if (typ is "arrow" or "line")
            {
                double x2v = Has(e, "x2") ? Num(Get(e, "x2")) : Num(Get(e, "x")) + 2;
                double y2v = Has(e, "y2") ? Num(Get(e, "y2")) : Num(Get(e, "y"));
                var (x2, y2) = P(x2v, y2v);
                c.Line(ex, ey, x2, y2, color: Truthy(tone) ? fill : t.Muted, width: 2.25, arrow: typ == "arrow", dash: Truthy(Get(e, "dashed")));
                if (hasLabel)
                    c.Text((ex + x2) / 2 - 1.1, (ey + y2) / 2 - 0.42, 2.2, 0.36, Str(label), size: 12, color: t.Subtext,
                        align: "c", role: "detail");
            }
            else if (typ == "text")
            {
                int size = Lower(Has(e, "size") ? Str(Get(e, "size")) : "m") switch
                {
                    "s" => 14, "m" => 20, "l" => 30, "xl" => 44, _ => 20,
                };
                string align = (Has(e, "align") ? Str(Get(e, "align")) : "l");
                if (align.Length == 0) throw new IndexOutOfRangeException("string index out of range");
                // draw.py looks the first character up in its align table, which only knows l, c and r
                if (align[0] is not ('l' or 'c' or 'r')) throw new KeyNotFoundException($"'{align[0]}'");
                c.Text(ex, ey, ew, eh, Str(label), size: size, font: size >= 20 ? t.FontHead : t.FontBody,
                    color: tone is null || IsText(tone, "card") || IsText(tone, "text") ? t.Text : fill,
                    align: align[..1], role: size >= 20 ? "label" : "detail");
            }
            else if (typ == "icon")
            {
                c.Icon(ex, ey, Min(ew, eh), Has(e, "icon") ? IconName(Get(e, "icon")) : "idea",
                    !(tone is null || IsText(tone, "card")) ? fill : t.Primary);
            }
            else
            {
                string shape = typ switch { "circle" => "ellipse", "hexagon" => "hexagon", "chevron" => "chevron", _ => "rounded" };
                c.Box(ex, ey, ew, eh, fill: fill, line: fill == t.Card ? t.CardLine : null, shape: shape,
                    radius: typ == "pill" ? Min(ew, eh) / 2 : null, shadow: true,
                    glow: t.Dark && fill != t.Card ? new Glow(fill, 8, 35) : null);
                double pad = 0.12;
                var icon = Get(e, "icon");
                if (Truthy(icon))
                {
                    double isz = Min(0.55, eh * 0.55);
                    if (hasLabel)
                    {
                        c.Icon(ex + pad + 0.05, ey + eh / 2 - isz / 2, isz, IconName(icon), fg);
                        c.Text(ex + isz + 0.3, ey + pad, ew - isz - 0.4, eh - 2 * pad, Str(label), size: 16, font: t.FontHead,
                            color: fg, anchor: "m", role: "label");
                    }
                    else c.Icon(ex + ew / 2 - isz / 2, ey + eh / 2 - isz / 2, isz, IconName(icon), fg);
                }
                else if (hasLabel)
                    c.Text(ex + pad, ey + pad, ew - 2 * pad, eh - 2 * pad, Str(label), size: 16, font: t.FontHead, color: fg,
                        align: "c", anchor: "m", role: "label");
            }
        }
    }
}
