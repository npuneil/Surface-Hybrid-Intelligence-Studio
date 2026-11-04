using System.Globalization;
using System.Text.Json.Nodes;
using Forge.Core.Draw;
using Forge.Core.Ir;
using static Forge.Core.Layouts.LayoutPy;

namespace Forge.Core.Layouts;

/// <summary>stack, flow, hub and compare: layouts that show how parts relate. Same numbers, same draw order as infographics.py.</summary>
internal static class StructureLayouts
{
    public static void Stack(Canvas c, JsonNode d)
    {
        var (x, y, w, h) = Area;
        var t = c.T;
        var layers = Items(d, "layers", 6);
        var side = Raw(d, "side_label", "bracket");
        bool hasSide = Truthy(side);
        int n = Math.Max(1, layers.Count);
        double bw = w - (hasSide ? 2.0 : 0);
        double gap = 0.14;
        double bh = Min(1.05, (h - gap * (n - 1)) / n);
        double y0 = y + (h - (n * bh + (n - 1) * gap)) / 2;
        bool taper = Truthy(Get(d, "taper"));
        for (int i = 0; i < layers.Count; i++)
        {
            var L = layers[i];
            double yy = y0 + i * (bh + gap);
            string col = t.Accent(i);
            double inset = taper ? 0.18 * i : 0;
            c.Box(x + inset, yy, bw - 2 * inset, bh, fill: t.Card,
                line: t.Dark ? Mix(col, t.Card, 0.55) : t.CardLine, shadow: true);
            double s = bh - 0.2;
            c.Box(x + inset + 0.1, yy + 0.1, s, s, fill: col, radius: Min(0.12, s / 3), glow: t.Dark ? new Glow(col, 8, 35) : null);
            c.Icon(x + inset + 0.1 + s * 0.18, yy + 0.1 + s * 0.18, s * 0.64, IconOf(L, "layers"), t.OnAccent);
            double lx = x + inset + bh + 0.12;
            c.Text(lx, yy + 0.08, 3.3, bh - 0.16, G(L, "label", "name"), size: 21, font: t.FontHead, anchor: "m", role: "label");
            c.Text(lx + 3.45, yy + 0.08, bw - 2 * inset - (lx - x - inset) - 3.65, bh - 0.16, G(L, "detail", "desc"),
                size: 16, color: t.Subtext, anchor: "m", role: "detail");
        }
        if (hasSide)
        {
            double sx = x + bw + 0.3;
            double total = n * bh + (n - 1) * gap;
            c.Box(sx, y0, 0.08, total, fill: t.Primary, shape: "rect", glow: t.Dark ? new Glow(t.Primary, 10, 50) : null);
            c.Box(sx + 0.25, y0 + total / 2 - 0.75, 1.45, 1.5, fill: Mix(t.Primary, t.Bg, !t.Dark ? 0.85 : 0.75),
                line: t.Primary, lineW: 1.25);
            c.Text(sx + 0.33, y0 + total / 2 - 0.7, 1.29, 1.4, Str(side), size: 17, font: t.FontHead, color: t.Text,
                align: "c", anchor: "m", role: "label");
        }
    }

    public static void Flow(Canvas c, JsonNode d)
    {
        var (x, y, w, h) = Area;
        var t = c.T;
        var steps = Items(d, "steps", 6);
        int n = Math.Max(1, steps.Count);
        double gap = 0.55;
        double cw = (w - gap * (n - 1)) / n;
        double ch = Min(h - 0.1, 4.1);
        double y0 = y + (h - ch) / 2;
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            double xx = x + i * (cw + gap);
            string col = t.Accent(i);
            c.Box(xx, y0 + 0.5, cw, ch - 0.5, fill: t.Card, line: t.Dark ? Mix(col, t.Card, 0.5) : t.CardLine, shadow: true);
            c.Badge(xx + cw / 2, y0 + 0.5, 1.0, IconOf(s, "arrow"), fill: col, glow: new Glow(col, 10, 45));
            c.Text(xx + 0.2, y0 + 1.12, cw - 0.4, 0.3, (i + 1).ToString("D2", CultureInfo.InvariantCulture), size: 12, color: col,
                font: t.FontHead, align: "c", role: "ornament");
            c.Text(xx + 0.18, y0 + 1.45, cw - 0.36, 0.95, G(s, "label", "name"), size: 20, font: t.FontHead,
                align: "c", anchor: "m", role: "label");
            c.Text(xx + 0.2, y0 + 2.45, cw - 0.4, ch - 2.6, G(s, "detail", "desc"), size: 15, color: t.Subtext,
                align: "c", role: "detail");
            if (i < n - 1)
                c.Box(xx + cw + 0.15, y0 + 0.5 + (ch - 0.5) / 2 - 0.22, 0.25, 0.44, fill: t.Muted, shape: "chevron");
        }
    }

    public static void Hub(Canvas c, JsonNode d)
    {
        var (x, y, w, h) = Area;
        var t = c.T;
        var center = Get(d, "center");
        if (!Truthy(center)) center = new JsonObject { ["label"] = GD(d, "Local AI", "hub"), ["icon"] = "ai" };
        else if (IsString(center)) center = new JsonObject { ["label"] = Str(center), ["icon"] = "ai" };
        var spokes = Items(d, "spokes", 8);
        int n = Math.Max(1, spokes.Count);
        double cx = x + w / 2, cy = y + h / 2;
        double rx = w / 2 - 1.5, ry = h / 2 - 0.45;
        double nw = 2.75, nh = 0.82;
        var pts = Enumerable.Range(0, n).Select(i => J.Polar(cx, cy, rx, ry, i, n)).ToList();
        foreach (var (px, py) in pts)
            c.Line(cx, cy, px, py, color: Mix(t.Primary, t.Bg, 0.45), width: 1.75, dash: true);
        double D = 2.15;
        c.Box(cx - D / 2 - 0.22, cy - D / 2 - 0.22, D + 0.44, D + 0.44, fill: t.Primary, shape: "ellipse", alpha: 14);
        c.Box(cx - D / 2, cy - D / 2, D, D, fill: t.Primary, shape: "ellipse", glow: t.Dark ? new Glow(t.Primary, 18, 45) : null,
            shadow: true);
        c.Icon(cx - 0.42, cy - 0.78, 0.84, IconOf(center, "ai"), t.OnAccent);
        c.Text(cx - D / 2 + 0.2, cy + 0.08, D - 0.4, 0.75, G(center, "label", "name"), size: 18, font: t.FontHead,
            color: t.OnAccent, align: "c", anchor: "t", role: "label");
        for (int i = 0; i < spokes.Count; i++)
        {
            var s = spokes[i];
            var (px, py) = pts[i];
            string col = t.Accent(i + 1);
            c.Box(px - nw / 2, py - nh / 2, nw, nh, fill: t.Card, line: t.Dark ? Mix(col, t.Card, 0.4) : t.CardLine,
                shadow: true, radius: nh / 2);
            c.Badge(px - nw / 2 + nh / 2, py, nh - 0.16, IconOf(s, "idea"), fill: col);
            c.Text(px - nw / 2 + nh + 0.02, py - nh / 2 + 0.06, nw - nh - 0.14, nh - 0.12, G(s, "label", "name"),
                size: 15, font: t.FontHead, anchor: "m", role: "label");
        }
    }

    public static void Compare(Canvas c, JsonNode d)
    {
        var (x, y, w, h) = Area;
        var t = c.T;
        var left = Get(d, "left");
        if (!Truthy(left)) left = new JsonObject();
        var right = Get(d, "right");
        if (!Truthy(right)) right = new JsonObject();
        var verdict = Raw(d, "verdict", "conclusion");
        bool hasVerdict = Truthy(verdict);
        double vh = hasVerdict ? 0.78 : 0;
        double pw = (w - 1.0) / 2;
        double ph = h - vh - (hasVerdict ? 0.22 : 0);
        foreach (var (side, xx, hi) in new[] { (left, x, false), (right, x + pw + 1.0, true) })
        {
            string col = hi ? t.Primary : t.Muted;
            c.Box(xx, y, pw, ph, fill: t.Card, line: hi ? t.Primary : (!t.Dark ? t.CardLine : "2A3142"),
                lineW: hi ? 2.25 : 1, shadow: true, glow: hi && t.Dark ? new Glow(t.Primary, 10, 30) : null);
            c.Badge(xx + 0.65, y + 0.62, 0.82, IconOf(side, hi ? "laptop" : "cloud"), fill: col);
            c.Text(xx + 1.2, y + 0.22, pw - 1.4, 0.8, G(side, "title", "label"), size: 23, font: t.FontHead,
                anchor: "m", role: "label");
            var pts = Items(side, "points", 5);
            int rows = Math.Max(3, pts.Count);
            double rh = (ph - 1.45) / rows;
            for (int j = 0; j < pts.Count; j++)
            {
                var p = pts[j];
                double ry = y + 1.3 + j * rh;
                c.Icon(xx + 0.45, ry + rh / 2 - 0.17, 0.34, IconOf(p, hi ? "check" : "pin"), hi ? t.Primary : t.Muted);
                c.Text(xx + 0.95, ry, pw - 1.2, rh, G(p, "label", "text"), size: 17, color: hi ? t.Text : t.Subtext,
                    anchor: "m", role: "detail");
            }
        }
        c.Box(x + pw + 0.1, y + ph / 2 - 0.4, 0.8, 0.8, fill: t.Text, shape: "ellipse");
        c.Text(x + pw + 0.1, y + ph / 2 - 0.4, 0.8, 0.8, "VS", size: 16, font: t.FontHead, color: t.Bg, align: "c",
            anchor: "m", role: "ornament");
        if (hasVerdict)
        {
            double vy = y + ph + 0.22;
            c.Box(x, vy, w, vh, fill: t.Primary, glow: t.Dark ? new Glow(t.Primary, 10, 35) : null);
            c.Icon(x + 0.3, vy + vh / 2 - 0.22, 0.44, IconOf(d, "star", "verdict_icon"), t.OnAccent);
            c.Text(x + 0.95, vy + 0.06, w - 1.2, vh - 0.12, Str(verdict), size: 20, font: t.FontHead, color: t.OnAccent,
                anchor: "m", role: "takeaway");
        }
    }
}
