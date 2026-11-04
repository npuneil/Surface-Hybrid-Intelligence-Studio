using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using Forge.Core.Draw;
using Forge.Core.Ir;

namespace Forge.Core.Preview;

/// <summary>Draws a slide from the same IR the PPTX writer uses, so the preview shows what the deck will look like without needing
/// PowerPoint. Shapes follow the PowerPoint preset geometries; glows and shadows are approximated with soft strokes.</summary>
public static class PngRenderer
{
    public static byte[] ToPng(DeckIr deck, int index, int widthPx = 1280)
    {
        using var bmp = RenderSlide(deck, index, widthPx);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    public static void SavePng(DeckIr deck, int index, string path, int widthPx = 1280)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, ToPng(deck, index, widthPx));
    }

    /// <summary>A contact sheet of the whole deck, <paramref name="columns"/> slides per row.</summary>
    public static Bitmap RenderSheet(DeckIr deck, int columns = 3, int cellWidthPx = 640)
    {
        int n = deck.Slides.Count, rows = (n + columns - 1) / columns;
        int cellH = (int)Math.Round(cellWidthPx * deck.HeightIn / deck.WidthIn), pad = 12;
        var sheet = new Bitmap(columns * (cellWidthPx + pad) + pad, rows * (cellH + pad) + pad);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(40, 40, 44));
        for (int i = 0; i < n; i++)
        {
            using var slide = RenderSlide(deck, i, cellWidthPx);
            g.DrawImage(slide, pad + i % columns * (cellWidthPx + pad), pad + i / columns * (cellH + pad), cellWidthPx, cellH);
        }
        return sheet;
    }

    public static Bitmap RenderSlide(DeckIr deck, int index, int widthPx = 1280)
    {
        var slide = deck.Slides[index];
        double s = widthPx / deck.WidthIn;
        int heightPx = (int)Math.Round(deck.HeightIn * s);
        var bmp = new Bitmap(widthPx, heightPx, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.PageUnit = GraphicsUnit.Pixel;

        DrawBackground(g, slide.Background, widthPx, heightPx, deck.Theme.Bg);
        foreach (var element in slide.Elements)
        {
            switch (element)
            {
                case BoxEl b: DrawBox(g, b, s); break;
                case LineEl l: DrawLine(g, l, s); break;
                case TextEl t: DrawText(g, t, s); break;
                case ChartEl c: DrawChart(g, c, s); break;
            }
        }
        return bmp;
    }

    // ---- colour ----------------------------------------------------------------------------

    private static Color Rgb(string hex, double? alphaPct = null)
    {
        string h = X(hex);
        int a = alphaPct is { } p ? (int)Math.Round(Math.Clamp(p, 0, 100) * 2.55) : 255;
        return Color.FromArgb(a, Convert.ToInt32(h[..2], 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16));
    }

    private static string X(string hex) => hex.TrimStart('#').PadRight(6, '0');

    private static void DrawBackground(Graphics g, SlideBackground? bg, int w, int h, string fallback)
    {
        if (bg is null) { g.Clear(Rgb(fallback)); return; }
        if (bg.To is null) { g.Clear(Rgb(bg.From)); return; }
        using var brush = new LinearGradientBrush(new Rectangle(0, 0, w, h), Rgb(bg.From), Rgb(bg.To), (float)bg.AngleDeg, false);
        g.FillRectangle(brush, 0, 0, w, h);
    }

    // ---- shapes ----------------------------------------------------------------------------

    private static GraphicsPath ShapePath(BoxEl b, RectangleF r)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        float l = r.Left, t = r.Top, rt = r.Right, bt = r.Bottom, w = r.Width, h = r.Height;
        float hc = l + w / 2, vc = t + h / 2, ss = Math.Min(w, h);
        switch (b.Kind)
        {
            case ShapeKind.Rect:
                path.AddRectangle(r);
                break;
            case ShapeKind.Rounded:
            {
                float rad = (float)Math.Min(b.CornerAdj, 0.5) * ss, d = rad * 2;
                if (d < 0.5f) { path.AddRectangle(r); break; }
                path.AddArc(l, t, d, d, 180, 90);
                path.AddArc(rt - d, t, d, d, 270, 90);
                path.AddArc(rt - d, bt - d, d, d, 0, 90);
                path.AddArc(l, bt - d, d, d, 90, 90);
                path.CloseFigure();
                break;
            }
            case ShapeKind.Ellipse:
                path.AddEllipse(r);
                break;
            case ShapeKind.Chevron:
            {
                float x1 = Math.Min(ss * 0.5f, w), x2 = rt - x1;
                path.AddPolygon(new PointF[] { new(l, t), new(x2, t), new(rt, vc), new(x2, bt), new(l, bt), new(x1 + l, vc) });
                break;
            }
            case ShapeKind.Pentagon:
            {
                float x1 = rt - Math.Min(ss * 0.5f, w);
                path.AddPolygon(new PointF[] { new(l, t), new(x1, t), new(rt, vc), new(x1, bt), new(l, bt) });
                break;
            }
            case ShapeKind.Hexagon:
            {
                float x1 = Math.Min(ss * 0.25f, w / 2), x2 = rt - x1;
                path.AddPolygon(new PointF[] { new(l, vc), new(l + x1, t), new(x2, t), new(rt, vc), new(x2, bt), new(l + x1, bt) });
                break;
            }
            case ShapeKind.Triangle:
                path.AddPolygon(new PointF[] { new(l, bt), new(hc, t), new(rt, bt) });
                break;
            case ShapeKind.Arrow:
            {
                float dx = Math.Min(ss * 0.5f, w), x1 = rt - dx, dy = h * 0.25f;
                path.AddPolygon(new PointF[] { new(l, vc - dy), new(x1, vc - dy), new(x1, t), new(rt, vc), new(x1, bt), new(x1, vc + dy), new(l, vc + dy) });
                break;
            }
            case ShapeKind.Donut:
            {
                float dr = ss * 0.25f;
                path.AddEllipse(r);
                path.AddEllipse(l + dr, t + dr, Math.Max(0.1f, w - 2 * dr), Math.Max(0.1f, h - 2 * dr));
                break;
            }
            case ShapeKind.Trapezoid:
            {
                float x2 = Math.Min(ss * 0.25f, w / 2);
                path.AddPolygon(new PointF[] { new(l, bt), new(l + x2, t), new(rt - x2, t), new(rt, bt) });
                break;
            }
            case ShapeKind.Diamond:
                path.AddPolygon(new PointF[] { new(l, vc), new(hc, t), new(rt, vc), new(hc, bt) });
                break;
            case ShapeKind.BlockArc:
            {
                float dr = ss * 0.25f;
                path.AddArc(r.X, r.Y, w, h, 180, 180);
                path.AddArc(l + dr, t + dr, Math.Max(0.1f, w - 2 * dr), Math.Max(0.1f, h - 2 * dr), 0, -180);
                path.CloseFigure();
                break;
            }
            default:
                path.AddRectangle(r);
                break;
        }
        return path;
    }

    private static void DrawBox(Graphics g, BoxEl b, double s)
    {
        if (b.W <= 0 || b.H <= 0) return;
        var rect = new RectangleF((float)(b.X * s), (float)(b.Y * s), (float)(b.W * s), (float)(b.H * s));
        var saved = g.Save();
        if (b.RotationDeg != 0)
        {
            g.TranslateTransform(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            g.RotateTransform((float)b.RotationDeg);
            g.TranslateTransform(-(rect.X + rect.Width / 2), -(rect.Y + rect.Height / 2));
        }
        using var path = ShapePath(b, rect);
        if (b.Shadow) DrawShadow(g, path, s);
        if (b.Glow is { } glow) DrawGlow(g, path, glow, s);
        if (b.Gradient is { } gr)
        {
            using var brush = new LinearGradientBrush(rect, Rgb(gr.From, b.AlphaPct), Rgb(gr.To, b.AlphaPct), (float)gr.AngleDeg, false);
            g.FillPath(brush, path);
        }
        else if (b.Fill is { } fill)
        {
            using var brush = new SolidBrush(Rgb(fill, b.AlphaPct));
            g.FillPath(brush, path);
        }
        if (b.Line is { } line)
        {
            using var pen = new Pen(Rgb(line), (float)Math.Max(0.5, b.LineWidthPt / 72.0 * s)) { LineJoin = LineJoin.Round };
            if (b.Dash) pen.DashPattern = [4f, 3f];
            g.DrawPath(pen, path);
        }
        g.Restore(saved);
    }

    private static void DrawShadow(Graphics g, GraphicsPath path, double s)
    {
        // PowerPoint's soft shadow (about 18 pt blur, 4 pt down, 14% black) as nine stacked translucent strokes.
        float dy = (float)(4.0 / 72.0 * s);
        using var moved = (GraphicsPath)path.Clone();
        using var matrix = new Matrix();
        matrix.Translate(0, dy);
        moved.Transform(matrix);
        const int steps = 9;
        for (int k = steps; k >= 1; k--)
        {
            using var pen = new Pen(Color.FromArgb(3, 0, 0, 0), (float)(2 * 9.0 * k / steps / 72.0 * s)) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, moved);
        }
        using var brush = new SolidBrush(Color.FromArgb(24, 0, 0, 0));
        g.FillPath(brush, moved);
    }

    private static void DrawGlow(Graphics g, GraphicsPath path, Glow glow, double s)
    {
        const int steps = 8;
        double radiusPx = glow.RadiusPt / 72.0 * s;
        int per = Math.Max(1, (int)Math.Round(glow.AlphaPct * 2.55 / steps * 1.6));
        for (int k = steps; k >= 1; k--)
        {
            using var pen = new Pen(Color.FromArgb(Math.Min(255, per), Rgb(glow.Color)), (float)(radiusPx * 2 * k / steps)) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
        }
    }

    private static void DrawLine(Graphics g, LineEl l, double s)
    {
        float widthPx = (float)Math.Max(0.5, l.WidthPt / 72.0 * s);
        var color = Rgb(l.Color, l.AlphaPct);
        using var pen = new Pen(color, widthPx);
        if (l.Dash) pen.DashPattern = [4f, 3f];
        PointF a = new((float)(l.X1 * s), (float)(l.Y1 * s)), b = new((float)(l.X2 * s), (float)(l.Y2 * s));
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.01) return;
        if (!l.Arrow) { g.DrawLine(pen, a, b); return; }
        double head = Math.Max(widthPx * 3.2, 6), ux = dx / len, uy = dy / len;
        var baseCentre = new PointF((float)(b.X - ux * head * 0.8), (float)(b.Y - uy * head * 0.8));
        g.DrawLine(pen, a, baseCentre);
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, new PointF[] { b, new((float)(b.X - ux * head - uy * head / 2), (float)(b.Y - uy * head + ux * head / 2)), new((float)(b.X - ux * head + uy * head / 2), (float)(b.Y - uy * head - ux * head / 2)) });
    }

    // ---- text ------------------------------------------------------------------------------

    private static readonly Dictionary<(string, bool), double> LineRatio = new();

    /// <summary>The font's real line pitch (ascent + descent + gap) as a multiple of its em size, which is what PowerPoint uses at 100% spacing.</summary>
    private static double LineRatioOf(string family, bool italic)
    {
        lock (LineRatio)
        {
            if (LineRatio.TryGetValue((family, italic), out double cached)) return cached;
            double ratio = 1.33;
            try
            {
                using var ff = new FontFamily(family);
                var style = italic ? FontStyle.Italic : FontStyle.Regular;
                if (!ff.IsStyleAvailable(style)) style = FontStyle.Regular;
                ratio = ff.GetLineSpacing(style) / (double)ff.GetEmHeight(style);
            }
            catch (ArgumentException) { }
            return LineRatio[(family, italic)] = ratio;
        }
    }

    private static void DrawText(Graphics g, TextEl t, double s)
    {
        if (t.Text.Length == 0) return;
        double px = t.Pt / 72.0 * s;
        var lines = t.WordWrap ? TextMeter.WrapLines(t.Text, t.Font, t.Pt, t.W) : [.. t.Text.Split('\n')];
        double lineH = px * LineRatioOf(t.Font, t.Italic) * (t.LineSpacing ?? 1.0);
        double total = lines.Count * lineH, boxX = t.X * s, boxY = t.Y * s, boxW = t.W * s, boxH = t.H * s;
        double y = t.Anchor switch { VAnchor.Middle => boxY + (boxH - total) / 2, VAnchor.Bottom => boxY + boxH - total, _ => boxY };
        using var font = new Font(t.Font, (float)px, t.Italic ? FontStyle.Italic : FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Rgb(t.Color));
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
        foreach (string line in lines)
        {
            double width = TextMeter.WidthPt(t.Font, line, t.Pt) / 72.0 * s;
            double x = t.Align switch { HAlign.Center => boxX + (boxW - width) / 2, HAlign.Right => boxX + boxW - width, _ => boxX };
            g.DrawString(line, font, brush, (float)x, (float)y, format);
            y += lineH;
        }
    }

    // ---- chart -----------------------------------------------------------------------------

    private static void DrawChart(Graphics g, ChartEl c, double s)
    {
        int n = c.Categories.Count;
        if (n == 0) return;
        double max = c.AxisMax > 0 ? c.AxisMax : Math.Max(1e-9, c.Values.DefaultIfEmpty(1).Max() * 1.3);
        double px = c.LabelPt / 72.0 * s;
        double x0 = c.X * s, y0 = c.Y * s, w = c.W * s, h = c.H * s;
        double labelW = c.Categories.Max(cat => TextMeter.WidthPt(c.Font, cat, c.LabelPt)) / 72.0 * s + 0.15 * s;
        double plotX = x0 + labelW + 0.1 * s, plotW = Math.Max(10, w - labelW - 0.1 * s);
        double band = h / n, bar = band / 1.55;
        using var font = new Font(c.Font, (float)px, FontStyle.Regular, GraphicsUnit.Pixel);
        using var bold = new Font(c.Font, (float)px, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Rgb(c.TextColor));
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
        double lineH = px * LineRatioOf(c.Font, false);
        for (int i = 0; i < n; i++)
        {
            double value = i < c.Values.Count ? c.Values[i] : 0;
            double cy = y0 + band * i + band / 2;
            double bw = Math.Max(0, plotW * value / max);
            using (var fill = new SolidBrush(Rgb(c.Highlighted.Contains(i) ? c.HighlightColor : c.BarColor)))
                g.FillRectangle(fill, (float)plotX, (float)(cy - bar / 2), (float)bw, (float)bar);
            double lw = TextMeter.WidthPt(c.Font, c.Categories[i], c.LabelPt) / 72.0 * s;
            g.DrawString(c.Categories[i], font, text, (float)(plotX - 0.1 * s - lw), (float)(cy - lineH / 2), format);
            g.DrawString(FormatNumber(value, c.NumberFormat), bold, text, (float)(plotX + bw + 0.06 * s), (float)(cy - lineH / 2), format);
        }
    }

    /// <summary>Formats like the number formats the bar layout writes: "#,##0", "#,##0.0" and an optional quoted suffix.</summary>
    internal static string FormatNumber(double value, string format)
    {
        int quote = format.IndexOf('"');
        string suffix = quote >= 0 ? format[(quote + 1)..].TrimEnd('"') : "";
        string core = quote >= 0 ? format[..quote] : format;
        int dot = core.IndexOf('.');
        int decimals = dot >= 0 ? core[(dot + 1)..].Count(ch => ch == '0') : 0;
        string text = value.ToString((core.Contains(',') ? "#,##0" : "0") + (decimals > 0 ? "." + new string('0', decimals) : ""), CultureInfo.InvariantCulture);
        return text + suffix;
    }
}
