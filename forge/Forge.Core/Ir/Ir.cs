namespace Forge.Core.Ir;

public enum ShapeKind { Rect, Rounded, Ellipse, Chevron, Pentagon, Hexagon, Triangle, Arrow, Donut, Trapezoid, Diamond, BlockArc }

public enum HAlign { Left, Center, Right }

public enum VAnchor { Top, Middle, Bottom }

/// <summary>Soft glow around a shape: radius in points, alpha in percent.</summary>
public readonly record struct Glow(string Color, double RadiusPt, double AlphaPct);

/// <summary>Two-stop linear gradient. The angle follows PowerPoint (0 = left to right, 90 = top to bottom).</summary>
public readonly record struct GradientFill(string From, string To, double AngleDeg);

/// <summary>One drawn thing on a slide. Positions are in inches on a 13.333 x 7.5 slide; colors are RRGGBB hex.
/// The PPTX writer and the PNG preview both draw from these records, so the two always agree.</summary>
public abstract record Element;

public sealed record BoxEl(double X, double Y, double W, double H) : Element
{
    public string? Fill { get; init; }
    public string? Line { get; init; }
    public double LineWidthPt { get; init; } = 1.0;
    public ShapeKind Kind { get; init; } = ShapeKind.Rounded;
    /// <summary>Rounded-corner adjustment, 0 to 0.5 of the shorter side.</summary>
    public double CornerAdj { get; init; }
    public double? AlphaPct { get; init; }
    public GradientFill? Gradient { get; init; }
    public bool Shadow { get; init; }
    public Glow? Glow { get; init; }
    public bool Dash { get; init; }
    public double RotationDeg { get; init; }
}

public sealed record LineEl(double X1, double Y1, double X2, double Y2) : Element
{
    public string Color { get; init; } = "808080";
    public double WidthPt { get; init; } = 1.5;
    public bool Arrow { get; init; }
    public bool Dash { get; init; }
    public double? AlphaPct { get; init; }
}

/// <summary>A text box with zero margins and no autofit. <see cref="Pt"/> is the size after fitting. Icons are text boxes
/// holding one Segoe Fluent Icons glyph (<see cref="IsIcon"/>).</summary>
public sealed record TextEl(double X, double Y, double W, double H, string Text) : Element
{
    public double Pt { get; init; } = 18;
    public string Color { get; init; } = "000000";
    public string Font { get; init; } = "Segoe UI";
    public HAlign Align { get; init; } = HAlign.Left;
    public VAnchor Anchor { get; init; } = VAnchor.Top;
    public bool Italic { get; init; }
    public double? LineSpacing { get; init; }
    public bool WordWrap { get; init; } = true;
    public bool IsIcon { get; init; }
}

/// <summary>A horizontal clustered bar chart that PowerPoint can edit (the writer embeds the data as a workbook).</summary>
public sealed record ChartEl(double X, double Y, double W, double H) : Element
{
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<double> Values { get; init; } = [];
    /// <summary>Indexes of the bars drawn in <see cref="HighlightColor"/>.</summary>
    public IReadOnlyList<int> Highlighted { get; init; } = [];
    public string BarColor { get; init; } = "999999";
    public string HighlightColor { get; init; } = "0078D4";
    public string TextColor { get; init; } = "000000";
    public string Font { get; init; } = "Segoe UI";
    public double LabelPt { get; init; } = 15;
    public string NumberFormat { get; init; } = "#,##0";
    public double AxisMax { get; init; }
}

/// <summary>A slide background: solid when <see cref="To"/> is null, otherwise a two-stop linear gradient at <see cref="AngleDeg"/>
/// (file convention: clockwise from left-to-right, so 90 runs top to bottom).</summary>
public sealed record SlideBackground(string From, string? To = null, double AngleDeg = 90);

public sealed class SlideIr
{
    public int Index { get; init; }
    public string Layout { get; set; } = "";
    public string Notes { get; set; } = "";
    public SlideBackground? Background { get; set; }
    public List<Element> Elements { get; } = [];
    public SlideReport Report { get; init; } = new();
}

public sealed class DeckIr
{
    public double WidthIn { get; init; } = 13.333;
    public double HeightIn { get; init; } = 7.5;
    public string Title { get; set; } = "Offline PPTX Forge deck";
    public string Author { get; set; } = "Offline PPTX Forge";
    public string Comments { get; set; } = "";
    public required Theme Theme { get; init; }
    public List<SlideIr> Slides { get; } = [];

    public IReadOnlyList<SlideSummary> Summary => [.. Slides.Select(s => s.Report.Summarize(s.Index, s.Layout))];
}

/// <summary>A defect recorded while drawing a slide, for example a layout that threw and was replaced by a fallback.</summary>
public sealed record Issue(string Kind, string Detail);

public sealed record TextRecord(string Role, int Words, double Pt, double MaxPt, bool Overflow, string Text);

/// <summary>What one slide drew: every text with its fitted size, every icon, and any issue. Lint and grade read the summary.</summary>
public sealed class SlideReport
{
    public const string OrnamentRole = "ornament";

    public List<TextRecord> Texts { get; } = [];
    public List<string> Icons { get; } = [];
    public List<Issue> Issues { get; } = [];

    public void Clear() { Texts.Clear(); Icons.Clear(); Issues.Clear(); }

    public SlideSummary Summarize(int index, string layout)
    {
        var visible = Texts.Where(t => t.Role != OrnamentRole).ToList();
        return new SlideSummary(index, layout, visible.Sum(t => t.Words), visible.Count(t => t.Overflow),
            visible.Count(t => t.Pt < t.MaxPt - 2.5), Icons.Count, [.. Issues]);
    }
}

/// <summary>The per-slide numbers lint and grade work from. Ornament text (footers, page numbers) is left out of the word count.</summary>
public sealed record SlideSummary(int Index, string Layout, int Words, int Overflows, int Shrunk, int Icons, IReadOnlyList<Issue> Issues);
