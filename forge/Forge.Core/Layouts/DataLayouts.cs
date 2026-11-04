using System.Text.Json.Nodes;
using Forge.Core.Draw;
using Forge.Core.Ir;
using static Forge.Core.Layouts.LayoutPy;

namespace Forge.Core.Layouts;

/// <summary>kpis, analogy, bars, timeline, matrix and memory: layouts built around numbers and short labels.</summary>
internal static class DataLayouts
{
    public static void Kpis(Canvas c, JsonNode d)
    {
        var (x, y, w, h) = Area;
        var t = c.T;
        var its = Items(d, "items", 4, "value");
        int n = Math.Max(1, its.Count);
        double gap = 0.35;
        double cw = (w - gap * (n - 1)) / n;
        double ch = Min(h, 3.9);
        double y0 = y + (h - ch) / 2;
        for (int i = 0; i < its.Count; i++)
        {
            var it = its[i];
            double xx = x + i * (cw + gap);
            string col = t.Accent(i);
            c.Box(xx, y0, cw, ch, fill: t.Card, line: t.Dark ? Mix(col, t.Card, 0.5) : t.CardLine, shadow: true);
            c.Box(xx, y0, cw, 0.09, fill: col, shape: "rect");
            c.Badge(xx + 0.7, y0 + 0.75, 0.78, IconOf(it, "chart"), fill: col, glow: new Glow(col, 8, 40));
            c.Text(xx + 0.3, y0 + 1.3, cw - 0.6, 1.35, G(it, "value", "number"), size: 60, font: t.FontNumber,
                color: !(t.Key == "microsoft" && col == "FFB900") ? col : t.Text, anchor: "m", role: "value");
            c.Text(xx + 0.3, y0 + 2.65, cw - 0.6, 0.8, G(it, "label", "caption"), size: 17, anchor: "t", role: "label");
            var src = Raw(it, "source", "footnote");
            if (Truthy(src))
                c.Text(xx + 0.3, y0 + ch - 0.45, cw - 0.6, 0.35, Str(src), size: 10, color: t.Muted, role: "source");
        }
    }

    public static void Analogy(Canvas c, JsonNode d)
    {
        var (x, y, w, h) = Area;
        var t = c.T;
        var concept = Get(d, "concept");
        if (!Truthy(concept)) concept = new JsonObject();
        var analog = Get(d, "analog");
        if (!Truthy(analog)) analog = Get(d, "analogy");
        if (!Truthy(analog)) analog = new JsonObject();
        concept = Labelled(concept);
        analog = Labelled(analog);
        var pairs = Items(d, "pairs", 4, "concept");
        string lc = t.Primary, rc = t.Accent(2);
        double colw = (w - 1.5) / 2;
        c.Badge(x + 0.6, y + 0.55, 1.05, IconOf(concept, "ai"), fill: lc, glow: new Glow(lc, 12, 45));
        c.Text(x + 1.3, y + 0.1, colw - 1.35, 0.9, G(concept, "label", "name"), size: 24, font: t.FontHead,
            anchor: "m", role: "label");
        c.Badge(x + w - 0.6, y + 0.55, 1.05, IconOf(analog, "home"), fill: rc, glow: new Glow(rc, 12, 45));
        c.Text(x + colw + 1.5, y + 0.1, colw - 1.35, 0.9, G(analog, "label", "name"), size: 24, font: t.FontHead,
            align: "r", anchor: "m", role: "label");
        c.Text(x + colw, y + 0.25, 1.5, 0.6, "is like", size: 16, color: t.Muted, align: "c", anchor: "m", italic: true,
            role: "ornament");
        int n = Math.Max(1, pairs.Count);
        double top = y + 1.4;
        double rh = Min(0.92, (y + h - top - 0.12 * (n - 1)) / n);
        for (int j = 0; j < pairs.Count; j++)
        {
            var p = pairs[j];
            double ry = top + j * (rh + 0.12);
            c.Box(x, ry, colw, rh, fill: !t.Dark ? Mix(lc, t.Bg, 0.88) : t.Card, line: lc, lineW: 1.25, radius: rh / 2);
            c.Text(x + 0.3, ry + 0.04, colw - 0.6, rh - 0.08, G(p, "concept", "left", "from"), size: 17,
                anchor: "m", align: "c", role: "detail");
            c.Line(x + colw + 0.08, ry + rh / 2, x + colw + 1.42, ry + rh / 2, color: t.Muted, width: 2, arrow: true, dash: true);
            c.Box(x + colw + 1.5, ry, colw, rh, fill: !t.Dark ? Mix(rc, t.Bg, 0.86) : t.Card, line: rc,
                lineW: 1.25, radius: rh / 2);
            c.Text(x + colw + 1.8, ry + 0.04, colw - 0.6, rh - 0.08, G(p, "analog", "right", "to"), size: 17,
                anchor: "m", align: "c", role: "detail");
        }
    }

    public static void Bars(Canvas c, JsonNode d)
    {
        var (x, y, w, h) = Area;
        var t = c.T;
        var data = Items(d, "bars", 8, "label").Where(b => Truthy(Raw(b, "label"))).ToList();
        var unitRaw = Raw(d, "unit");
        string unit = Str(unitRaw);
        string hl = G(d, "highlight");
        var cats = data.Select(b => G(b, "label")).ToList();
        if (cats.Count == 0) cats.Add("n/a");
        var vals = data.Select(b => Num(Raw(b, "value"))).ToList();
        if (vals.Any(v => !double.IsFinite(v))) throw new ArgumentException("chart values must be finite numbers");
        double cwid = w * 0.64;
        var highlighted = new List<int>();
        for (int i = 0; i < data.Count; i++)
            if (G(data[i], "label") == hl || (hl.Length == 0 && i == 0)) highlighted.Add(i);
        int decimals = vals.All(double.IsInteger) ? 0 : vals.All(v => double.IsInteger(v * 10)) ? 1 : 2;
        string numberFormat = "#,##0" + (decimals > 0 ? "." + new string('0', decimals) : "") + (Truthy(unitRaw) ? "\" " + unit + "\"" : "");
        c.Chart(new ChartEl(x, y, cwid, h)
        {
            Categories = cats,
            Values = vals.Count > 0 ? vals : [0],
            Highlighted = highlighted,
            BarColor = Mix(t.Muted, t.Bg, 0.35),
            HighlightColor = t.Primary,
            TextColor = t.Text,
            Font = t.FontBody,
            LabelPt = 15,
            NumberFormat = numberFormat,
            AxisMax = vals.Count > 0 && MaxOf(vals) > 0 ? MaxOf(vals) * 1.3 : 0,
        });
        string catText = string.Join(" ", cats);
        c.Report.Texts.Add(new TextRecord("label", J.Words(catText), 15, 15, false, catText));
        // callout
        double cx = x + cwid + 0.3;
        double cw = w - cwid - 0.3;
        c.Box(cx, y + 0.2, cw, h - 0.4, fill: t.Card, line: !t.Dark ? t.CardLine : Mix(t.Primary, t.Card, 0.5), shadow: true);
        JsonNode top = data.FirstOrDefault(b => G(b, "label") == hl)
            ?? (data.Count > 0 ? data[0] : new JsonObject { ["value"] = "", ["label"] = "" });
        c.Badge(cx + 0.7, y + 0.95, 0.85, IconOf(d, "speed"), fill: t.Primary, glow: new Glow(t.Primary, 10, 40));
        var val = Raw(top, "value");
        string vtxt;
        if (IsNumberOrBool(val) || IsDigits(Str(val)))
        {
            double fv = Num(val);
            vtxt = double.IsInteger(fv) ? Fixed(fv, 0, true) : Fixed(fv, 2, true).TrimEnd('0').TrimEnd('.');
        }
        else vtxt = Str(val);
        c.Text(cx + 0.35, y + 1.55, cw - 0.7, 1.1, vtxt, size: 54, font: t.FontNumber, color: t.Primary, role: "value");
        c.Text(cx + 0.35, y + 2.6, cw - 0.7, 0.4, $"{unit}  |  {G(top, "label")}".Trim(' ', '|'), size: 14, color: t.Subtext,
            role: "label");
        c.Text(cx + 0.35, y + 3.1, cw - 0.7, h - 3.45, G(d, "caption"), size: 15, color: t.Text, role: "detail");
    }

    /// <summary>str(val).replace('.', '', 1).isdigit()</summary>
    private static bool IsDigits(string s)
    {
        int dot = s.IndexOf('.');
        if (dot >= 0) s = s.Remove(dot, 1);
        return IsDigitText(s);
    }

    public static void Timeline(Canvas c, JsonNode d)
    {
        var (x, y, w, h) = Area;
        var t = c.T;
        var ms = Items(d, "milestones", 6, "label");
        int n = Math.Max(1, ms.Count);
        double ly = y + h / 2;
        c.Box(x + 0.2, ly - 0.035, w - 0.4, 0.07, fill: !t.Dark ? t.Muted : Mix(t.Primary, t.Bg, 0.5), shape: "rect");
        double cw = Min(2.9, 2 * (w - 0.6) / n - 0.25);
        for (int i = 0; i < ms.Count; i++)
        {
            var m = ms[i];
            double px = x + 0.3 + (w - 0.6) * (i + 0.5) / n;
            string col = t.Accent(i);
            bool up = i % 2 == 0;
            double cy0 = up ? ly - 0.42 - 1.5 : ly + 0.42;
            c.Line(px, ly, px, cy0 + (up ? 1.5 : 0), color: col, width: 1.5);
            c.Box(px - 0.2, ly - 0.2, 0.4, 0.4, fill: col, shape: "ellipse", glow: t.Dark ? new Glow(col, 10, 50) : null);
            c.Box(px - cw / 2, cy0, cw, 1.5, fill: t.Card, line: t.Dark ? Mix(col, t.Card, 0.4) : t.CardLine, shadow: true);
            c.Icon(px - cw / 2 + 0.15, cy0 + 0.15, 0.4, IconOf(m, "calendar"), col);
            c.Text(px - cw / 2 + 0.62, cy0 + 0.12, cw - 0.75, 0.45, G(m, "when", "date"), size: 15, font: t.FontHead,
                color: col, anchor: "m", role: "label");
            c.Text(px - cw / 2 + 0.15, cy0 + 0.62, cw - 0.3, 0.8, G(m, "label", "event"), size: 15, role: "detail");
        }
    }

    public static void Matrix(Canvas c, JsonNode d)
    {
        var (x, y, w, h) = Area;
        var t = c.T;
        var qs = Items(d, "quadrants", 4);
        while (qs.Count < 4) qs.Add(new JsonObject { ["label"] = "" });
        double hl = Int(Num(Get(d, "highlight"), 1));
        double gx = x + 0.55, gy = y, gw = w - 0.55, gh = h - 0.5;
        c.Line(gx - 0.25, gy + gh, gx - 0.25, gy, color: t.Muted, width: 2, arrow: true);
        c.Line(gx, gy + gh + 0.22, gx + gw, gy + gh + 0.22, color: t.Muted, width: 2, arrow: true);
        c.Text(gx, gy + gh + 0.27, gw, 0.3, TextOnly(Raw(d, "x_axis")) + "  \u2192", size: 13, color: t.Subtext, align: "r", role: "label");
        c.Text(gx, gy - 0.02, 4, 0.28, G(d, "y_axis"), size: 13, color: t.Subtext, role: "label");
        double qw = (gw - 0.15) / 2, qh = (gh - 0.45) / 2;
        for (int i = 0; i < Math.Min(4, qs.Count); i++)
        {
            var q = qs[i];
            double qx = gx + (i % 2) * (qw + 0.15);
            double qy = gy + 0.35 + (i / 2) * (qh + 0.15);
            bool on = i == hl;
            string col = t.Accent(i);
            c.Box(qx, qy, qw, qh, fill: on ? t.Primary : t.Card, line: on ? null : t.CardLine, shadow: true,
                glow: on && t.Dark ? new Glow(t.Primary, 12, 40) : null);
            c.Icon(qx + 0.25, qy + 0.25, 0.5, IconOf(q, "idea"), on ? t.OnAccent : col);
            c.Text(qx + 0.9, qy + 0.2, qw - 1.1, 0.6, G(q, "label", "name"), size: 20, font: t.FontHead,
                color: on ? t.OnAccent : t.Text, anchor: "m", role: "label");
            c.Text(qx + 0.25, qy + 0.9, qw - 0.5, qh - 1.0, G(q, "detail", "desc"), size: 15,
                color: on ? t.OnAccent : t.Subtext, role: "detail");
        }
    }

    public static void Memory(Canvas c, JsonNode d)
    {
        var (x, y, w, h) = Area;
        var t = c.T;
        double cap = Num(Get(d, "capacity"), 0);
        if (cap == 0) cap = 1.0;
        string unit = GD(d, "GB", "unit");
        var blocks = Items(d, "blocks", 6);
        var sizes = blocks.Select(b => Max(0.0, Num(Raw(b, "size", "value")))).ToList();
        double used = 0;
        foreach (double s in sizes) used += s;
        double scale = 1.0;
        if (used > cap)
        {
            if (used == 0) throw new DivideByZeroException("float division by zero");
            scale = cap / used;
            c.Issue("memory_overcommit", $"{General(used)} > {General(cap)}");
        }
        c.Text(x, y, 6, 0.6, $"{Fixed(cap, 0, true)} {unit}", size: 34, font: t.FontNumber, color: t.Primary, role: "value");
        c.Text(x + 3.2, y + 0.12, w - 3.2, 0.5, GD(d, "available budget", "capacity_label"), size: 15,
            color: t.Subtext, anchor: "m", role: "label");
        double by = y + 1.35, bh = 1.25;
        c.Box(x, by, w, bh, fill: t.Card, line: !t.Dark ? t.CardLine : "2A3142", radius: 0.12);
        double cx = x;
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            double s = sizes[i];
            double bw = w * s * scale / cap;
            if (bw <= 0.02) continue;
            string col = t.Accent(i);
            c.Box(cx + 0.03, by + 0.06, bw - 0.06, bh - 0.12, fill: col, radius: 0.1, glow: t.Dark ? new Glow(col, 6, 35) : null);
            string lbl = G(b, "label", "name");
            if (bw > 1.6)
            {
                c.Text(cx + 0.12, by + 0.12, bw - 0.24, 0.55, lbl, size: 15, font: t.FontHead, color: t.OnAccent, role: "label");
                c.Text(cx + 0.12, by + 0.68, bw - 0.24, 0.45, $"{General(s)} {unit}", size: 14, color: t.OnAccent, role: "value");
            }
            else
            {
                double ly = i % 2 == 0 ? by - 0.65 : by + bh + 0.08;
                c.Line(cx + bw / 2, i % 2 == 0 ? by : by + bh, cx + bw / 2, ly + (i % 2 == 0 ? 0.6 : 0), color: col);
                c.Text(cx + bw / 2 - 1.1, ly, 2.2, 0.6, $"{lbl} {General(s)} {unit}", size: 13, align: "c", role: "label");
            }
            cx += bw;
        }
        double free = cap - used * scale;
        if (free > 0.01 * cap)
        {
            double fw = w * free / cap;
            c.Box(cx + 0.03, by + 0.06, fw - 0.06, bh - 0.12, line: t.Muted, dash: true, radius: 0.1);
            if (fw > 1.4)
                c.Text(cx + 0.15, by + 0.3, fw - 0.3, 0.6, $"Headroom {Fixed(free, 1, true)} {unit}", size: 15, color: t.Subtext,
                    align: "c", anchor: "m", role: "label");
        }
        for (int k = 0; k < 5; k++)
        {
            double tx = x + w * k / 4;
            c.Line(tx, by + bh + 0.75, tx, by + bh + 0.9, color: t.Muted, width: 1);
            c.Text(tx - 0.6, by + bh + 0.92, 1.2, 0.3, Fixed(cap * k / 4, 0, true), size: 11, color: t.Muted, align: "c",
                role: "ornament");
        }
        var capTxt = Raw(d, "caption");
        if (Truthy(capTxt))
        {
            c.Icon(x, y + h - 0.55, 0.42, "memory", t.Primary);
            c.Text(x + 0.55, y + h - 0.6, w - 0.6, 0.55, Str(capTxt), size: 16, color: t.Subtext, anchor: "m", role: "detail");
        }
    }
}
