using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Text;

namespace Forge.Core.Draw;

/// <summary>Measures text with the real Segoe fonts so a layout can shrink text until it fits its box. The width of a string is
/// taken once at 100 px (where hinting has no visible effect) and scaled to the wanted size, the same way the Python Forge
/// scaled PIL's 100 pt advances. Wrapping is greedy by words and the line height is 1.30 em.</summary>
public static class TextMeter
{
    /// <summary>Segoe UI ascent + descent is about 1.33 em; 1.30 matches what PowerPoint lays out.</summary>
    public const double LineHeight = 1.30;

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<(string Font, string Text), double> Cache = new();
    private static readonly Dictionary<string, Font> Fonts = new(StringComparer.Ordinal);
    private static Bitmap? _bitmap;
    private static Graphics? _graphics;
    private static StringFormat? _format;

    /// <summary>Advance width of <paramref name="s"/> at 100 px in the named family.</summary>
    public static double Width100(string fontName, string s)
    {
        if (s.Length == 0) return 0;
        if (Cache.TryGetValue((fontName, s), out double cached)) return cached;
        double width;
        lock (Gate)
        {
            _bitmap ??= new Bitmap(1, 1);
            _graphics ??= CreateGraphics(_bitmap);
            _format ??= CreateFormat();
            if (!Fonts.TryGetValue(fontName, out Font? font))
                Fonts[fontName] = font = new Font(fontName, 100f, FontStyle.Regular, GraphicsUnit.Pixel);
            width = _graphics.MeasureString(s, font, PointF.Empty, _format).Width;
        }
        if (Cache.Count > 40000) Cache.Clear();
        Cache[(fontName, s)] = width;
        return width;
    }

    private static Graphics CreateGraphics(Bitmap bitmap)
    {
        var g = Graphics.FromImage(bitmap);
        g.PageUnit = GraphicsUnit.Pixel;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        return g;
    }

    private static StringFormat CreateFormat()
    {
        var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
        return format;
    }

    /// <summary>Width in points of <paramref name="s"/> at <paramref name="pt"/>.</summary>
    public static double WidthPt(string fontName, string s, double pt) => Width100(fontName, s) * pt / 100.0;

    /// <summary>Greedy word wrap with real glyph advances. Paragraphs split on newline and words on a single space.</summary>
    public static List<string> WrapLines(string s, string fontName, double pt, double widthIn)
    {
        double avail = widthIn * 72.0;
        var lines = new List<string>();
        foreach (string para in s.Split('\n'))
        {
            string current = "";
            foreach (string word in para.Split(' '))
            {
                string candidate = current.Length > 0 ? current + " " + word : word;
                if (current.Length == 0 || WidthPt(fontName, candidate, pt) <= avail) current = candidate;
                else { lines.Add(current); current = word; }
            }
            lines.Add(current);
        }
        return lines;
    }

    /// <summary>The largest size at or under <paramref name="maxPt"/> (never below <paramref name="minPt"/>) at which the wrapped
    /// text fits the box. <c>Overflow</c> is true when even the minimum size does not fit.</summary>
    public static FitResult Fit(string s, string fontName, double widthIn, double heightIn, double maxPt, double minPt = 9.0)
    {
        double pt = maxPt;
        while (true)
        {
            var lines = WrapLines(s, fontName, pt, widthIn);
            double longest = 0;
            foreach (string line in lines)
                foreach (string word in line.Split(' '))
                    if (word.Length > 0) longest = Math.Max(longest, WidthPt(fontName, word, pt));
            bool fits = lines.Count * pt * LineHeight <= heightIn * 72.0 + 0.5 && longest <= widthIn * 72.0 + 0.5;
            if (fits) return new FitResult(pt, lines.Count, false);
            if (pt <= minPt) return new FitResult(pt, lines.Count, true);
            pt = Math.Max(minPt, pt - 1.0);
        }
    }
}

public readonly record struct FitResult(double Pt, int Lines, bool Overflow);
