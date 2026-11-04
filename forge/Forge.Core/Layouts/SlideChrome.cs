using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Draw;
using static Forge.Core.Layouts.LayoutPy;

namespace Forge.Core.Layouts;

/// <summary>What every slide gets around its layout (render.py): the ambient glow, title, takeaway and footer, plus the
/// title slide and the section divider, which are drawn by <see cref="DeckBuilder"/> instead of the layout registry.</summary>
internal static class SlideChrome
{
    private static readonly string[] MsColors = ["F25022", "7FBA00", "00A4EF", "FFB900"];

    private static void MsLogo(Canvas c, double x, double y, double s, double gap)
    {
        for (int k = 0; k < MsColors.Length; k++)
            c.Box(x + (k % 2) * (s + gap), y + (k / 2) * (s + gap), s, s, fill: MsColors[k], shape: "rect");
    }

    private static void Ambient(Canvas c, bool title = false)
    {
        var t = c.T;
        if (t.Motif == "surface_bloom")
        {
            c.Box(!title ? 9.6 : 7.2, -2.2, 6.5, 6.0, fill: Mix(t.Primary, t.Bg, 0.55), shape: "ellipse", alpha: 10);
            c.Box(!title ? 11.0 : 9.4, -0.6, 4.5, 4.2, fill: Mix(t.Accent(2), t.Bg, 0.4), shape: "ellipse", alpha: 9);
        }
        else if (t.Motif == "ultra_glow")
        {
            c.Box(!title ? 9.8 : 6.8, -3.0, 7.0, 7.0, fill: t.Primary, shape: "ellipse", alpha: 5);
            c.Box(!title ? 11.2 : 9.0, -1.5, 4.5, 4.5, fill: t.Accent(1), shape: "ellipse", alpha: 4);
        }
    }

    /// <summary>chrome(): the part of a content slide that is not its layout.</summary>
    public static void Draw(Canvas c, JsonNode s, int idx, JsonNode deck)
    {
        var t = c.T;
        Ambient(c);
        if (t.Motif == "ultra_glow")
            c.Box(0.6, 0.32, 0.75, 0.06, fill: t.Primary, shape: "rect", glow: new(t.Primary, 6, 60));
        else if (t.Motif == "surface_bloom")
            c.Box(0.6, 0.32, 0.38, 0.06, fill: t.Primary, shape: "rect");
        c.Text(0.6, 0.45, 12.1, 0.8, G(s, "title"), size: 32, font: t.FontHead, anchor: "m", role: "title");
        var takeaway = Raw(s, "takeaway", "subtitle");
        if (Truthy(takeaway))
            c.Text(0.6, 1.25, 12.1, 0.55, Str(takeaway), size: 17, color: t.Subtext, anchor: "t", role: "takeaway");
        double fy = 7.02;
        double fx = 0.6;
        if (t.Motif == "ms_squares")
        {
            MsLogo(c, 0.6, fy + 0.07, 0.09, 0.02);
            fx = 0.86;
        }
        var client = Raw(deck, "client");
        c.Text(fx, fy, 8, 0.3, t.Footer + (Truthy(client) ? "  |  " + Str(client) : ""), size: 10, color: t.Muted, role: "ornament");
        c.Text(11.9, fy, 0.83, 0.3, (idx + 1).ToString(CultureInfo.InvariantCulture), size: 10, color: t.Muted, align: "r", role: "ornament");
    }

    /// <summary>The hero icons of a title slide: the string entries of "hero_icons", at most four, padded with "idea".</summary>
    private static List<string> HeroIcons(JsonNode s)
    {
        var raw = Get(s, "hero_icons");
        List<string> hero = raw switch
        {
            _ when !Truthy(raw) => [],
            JsonArray a => [.. a.Where(IsString).Select(n => n!.GetValue<string>())],
            JsonObject o => [.. o.Select(p => p.Key)],
            JsonValue v when IsString(v) => [.. v.GetValue<string>().EnumerateRunes().Select(r => r.ToString())],
            _ => throw new InvalidOperationException($"'{TypeName(raw)}' object is not iterable"),
        };
        hero = [.. hero.Take(4)];
        if (hero.Count == 0) hero = ["cloud", "laptop", "shield", "ai"];
        while (hero.Count < 4) hero.Add("idea");
        return hero;
    }

    public static void Title(Canvas c, JsonNode s, JsonNode deck)
    {
        var t = c.T;
        Ambient(c, title: true);
        var hero = HeroIcons(s);
        if (t.Motif == "ms_squares")
        {
            MsLogo(c, 0.7, 0.62, 0.17, 0.03);
            c.Text(1.15, 0.58, 3, 0.42, "Microsoft", size: 19, font: t.FontHead, color: "737373", anchor: "m", role: "ornament");
            double sq = 1.75, gap = 0.16;
            double x0 = 8.75, y0 = 1.75;
            for (int k = 0; k < MsColors.Length; k++)
            {
                double bx = x0 + (k % 2) * (sq + gap), by = y0 + (k / 2) * (sq + gap);
                c.Box(bx, by, sq, sq, fill: MsColors[k], shape: "rect");
                c.Icon(bx + sq * 0.25, by + sq * 0.25, sq * 0.5, hero[k], "FFFFFF");
            }
        }
        else if (t.Motif == "surface_bloom")
        {
            c.Box(8.6, 1.55, 3.9, 3.9, fill: "FFFFFF", shape: "ellipse", shadow: true);
            c.Icon(9.5, 2.45, 2.1, hero[1], t.Primary);
            (double, double)[] orbit = [(8.15, 1.35), (11.75, 2.0), (8.35, 4.7), (11.55, 4.75)];
            for (int k = 0; k < orbit.Length; k++)
            {
                var (bx, by) = orbit[k];
                string col = t.Accent(k);
                c.Box(bx, by, 0.95, 0.95, fill: "FFFFFF", shape: "ellipse", shadow: true);
                c.Icon(bx + 0.2, by + 0.2, 0.55, k != 0 ? hero[(k + 2) % 4] : hero[0], col);
            }
        }
        else
        {
            double cx = 10.35, cy = 3.7;
            double[] radii = [2.35, 1.85, 1.35];
            for (int k = 0; k < radii.Length; k++)
            {
                double r = radii[k];
                c.Box(cx - r, cy - r, 2 * r, 2 * r, line: t.Accent(k), lineW: 1.25 + k * 0.5, shape: "ellipse", glow: new(t.Accent(k), 8, 30));
            }
            c.Box(cx - 0.95, cy - 0.95, 1.9, 1.9, fill: t.Card, shape: "ellipse", glow: new(t.Primary, 24, 45));
            c.Icon(cx - 0.68, cy - 0.7, 1.36, hero[1], t.Primary);
            (double, double)[] spokes = [(cx - 2.35, cy), (cx + 2.35, cy), (cx, cy - 2.35), (cx, cy + 2.35)];
            for (int k = 0; k < 4; k++)
            {
                var (px, py) = spokes[k];
                c.Box(px - 0.38, py - 0.38, 0.76, 0.76, fill: t.Card, line: t.Accent(k), lineW: 1.5, shape: "ellipse");
                c.Icon(px - 0.23, py - 0.23, 0.46, k != 1 ? hero[(k + 2) % 4] : hero[0], t.Accent(k));
            }
        }
        var clientRaw = Raw(deck, "client");
        var kicker = Find(s, "kicker");
        string? kickerText = kicker is not null
            ? (Truthy(kicker) ? Str(kicker) : null)
            : (Truthy(clientRaw) ? $"Prepared for {Str(clientRaw)}" : null);
        if (kickerText is not null)
            c.Text(0.7, 1.85, 7.4, 0.45, kickerText, size: 15, font: t.FontHead, color: t.Primary, caps: true, role: "takeaway");
        c.Text(0.7, 2.35, 7.0, 2.4, GD(s, G(deck, "title"), "title"), size: 50, font: t.FontHead, anchor: "t", role: "title");
        c.Text(0.7, 4.85, 7.2, 1.0, G(s, "subtitle", "takeaway"), size: 20, color: t.Subtext, role: "takeaway");
        c.Text(0.7, 6.6, 7.0, 0.35, GD(s, t.Footer, "presenter"), size: 12, color: t.Muted, role: "ornament");
    }

    public static void Section(Canvas c, JsonNode s, int idx)
    {
        var t = c.T;
        Ambient(c, title: true);
        c.Text(0.7, 1.6, 6, 2.2, GD(s, idx.ToString("D2", CultureInfo.InvariantCulture), "number"), size: 120, font: t.FontNumber,
            color: t.Primary, role: "ornament");
        c.Text(0.7, 4.0, 10, 1.2, G(s, "title"), size: 44, font: t.FontHead, role: "title");
        c.Text(0.7, 5.2, 10, 0.8, G(s, "takeaway", "subtitle"), size: 20, color: t.Subtext, role: "takeaway");
    }
}
