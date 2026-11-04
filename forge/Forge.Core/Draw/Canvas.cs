using Forge.Core.Ir;

namespace Forge.Core.Draw;

/// <summary>Draws onto one slide's element list and records every text and icon for the density, overflow and lint checks.
/// This is the C# port of the Python Forge's Canvas: same parameter names and defaults, so the layout code reads the same.
/// Shapes, aligns and anchors are strings ("rounded", "l", "m") on purpose, to keep the layout port mechanical.</summary>
public sealed class Canvas
{
    private readonly SlideIr _slide;

    public Canvas(SlideIr slide, Theme theme)
    {
        _slide = slide;
        T = theme;
    }

    public Theme T { get; }
    public SlideIr Slide => _slide;
    public int Index => _slide.Index;
    public SlideReport Report => _slide.Report;

    /// <summary>Colour blend, see <see cref="J.Mix"/>.</summary>
    public static string Mix(string a, string b, double t) => J.Mix(a, b, t);

    public static ShapeKind ParseShape(string? shape) => (shape ?? "rounded").ToLowerInvariant() switch
    {
        "rect" => ShapeKind.Rect,
        "ellipse" => ShapeKind.Ellipse,
        "chevron" => ShapeKind.Chevron,
        "pentagon" => ShapeKind.Pentagon,
        "hexagon" => ShapeKind.Hexagon,
        "triangle" => ShapeKind.Triangle,
        "arrow" => ShapeKind.Arrow,
        "donut" => ShapeKind.Donut,
        "trapezoid" => ShapeKind.Trapezoid,
        "diamond" => ShapeKind.Diamond,
        "arc" => ShapeKind.BlockArc,
        _ => ShapeKind.Rounded,
    };

    /// <summary>A filled and/or outlined shape. <paramref name="radius"/> is the corner radius in inches (default: the theme's).
    /// <paramref name="gradient"/> is two stops and <paramref name="gradientAngle"/> uses the Python Forge's convention
    /// (degrees counter-clockwise from left-to-right), converted here to the file's clockwise angle. A shadow is skipped on dark themes.</summary>
    public BoxEl Box(double x, double y, double w, double h, string? fill = null, string? line = null, double lineW = 1.0,
        string shape = "rounded", double? radius = null, double? alpha = null, (string From, string To)? gradient = null,
        double gradientAngle = 90, bool shadow = false, Glow? glow = null, bool dash = false, double rotation = 0)
    {
        var kind = ParseShape(shape);
        double adj = kind == ShapeKind.Rounded ? Math.Max(0.0, Math.Min(0.5, (radius ?? T.Radius) / Math.Max(0.01, Math.Min(w, h)))) : 0;
        var el = new BoxEl(x, y, w, h)
        {
            Kind = kind,
            CornerAdj = adj,
            Fill = gradient is null ? fill : null,
            Gradient = gradient is { } gr ? new GradientFill(gr.From, gr.To, ((360.0 - gradientAngle) % 360.0 + 360.0) % 360.0) : null,
            Line = line,
            LineWidthPt = lineW,
            Dash = dash && line is not null,
            AlphaPct = alpha,
            RotationDeg = rotation,
            Shadow = shadow && !T.Dark,
            Glow = glow,
        };
        _slide.Elements.Add(el);
        return el;
    }

    public LineEl Line(double x1, double y1, double x2, double y2, string? color = null, double width = 1.5, bool arrow = false, bool dash = false, double? alpha = null)
    {
        var el = new LineEl(x1, y1, x2, y2) { Color = color ?? T.Muted, WidthPt = width, Arrow = arrow, Dash = dash, AlphaPct = alpha };
        _slide.Elements.Add(el);
        return el;
    }

    /// <summary>Text that is shrunk one point at a time until it fits its box (never below <paramref name="minSize"/>).
    /// Records the fitted size and whether it overflowed under <paramref name="role"/> ("title", "takeaway", "body", "detail"
    /// and so on; "ornament" text is left out of the word count).</summary>
    public TextEl Text(double x, double y, double w, double h, string? s, double size = 18, string? color = null, string? font = null,
        string align = "l", string anchor = "t", double minSize = 9, string role = "body", bool italic = false,
        double? spacing = null, bool caps = false)
    {
        string text = s ?? "";
        if (caps) text = text.ToUpperInvariant();
        string family = font ?? T.FontBody;
        var fit = TextMeter.Fit(text, family, w - 0.02, h, size, minSize);
        var el = new TextEl(x, y, w, h, text)
        {
            Pt = fit.Pt,
            Color = color ?? T.Text,
            Font = family,
            Align = ParseAlign(align),
            Anchor = ParseAnchor(anchor),
            Italic = italic,
            LineSpacing = spacing,
        };
        _slide.Elements.Add(el);
        Report.Texts.Add(new TextRecord(role, J.Words(text), fit.Pt, size, fit.Overflow, text));
        return el;
    }

    /// <summary>One Segoe Fluent Icons glyph centred in a square box.</summary>
    public TextEl Icon(double x, double y, double size, string name, string? color = null)
    {
        var el = new TextEl(x, y, size, size, Icons.Glyph(name))
        {
            Pt = size * 72 * 0.62,
            Color = color ?? T.Text,
            Font = Icons.FontName,
            Align = HAlign.Center,
            Anchor = VAnchor.Middle,
            WordWrap = false,
            IsIcon = true,
        };
        _slide.Elements.Add(el);
        Report.Icons.Add(name);
        return el;
    }

    /// <summary>A filled circle with an icon inside.</summary>
    public void Badge(double cx, double cy, double d, string name, string? fill = null, string? color = null, string? ring = null, Glow? glow = null)
    {
        Box(cx - d / 2, cy - d / 2, d, d, fill: fill ?? T.Primary, shape: "ellipse", line: ring, lineW: 2, glow: T.Dark ? glow : null);
        Icon(cx - d * 0.36, cy - d * 0.36, d * 0.72, name, color ?? T.OnAccent);
    }

    /// <summary>A bar chart PowerPoint can edit. Values and labels come from the layout; colours from the theme.</summary>
    public ChartEl Chart(ChartEl chart)
    {
        _slide.Elements.Add(chart);
        return chart;
    }

    public void Issue(string kind, string detail = "") => Report.Issues.Add(new Issue(kind, detail.Length > 200 ? detail[..200] : detail));

    private static HAlign ParseAlign(string a) => a switch { "c" => HAlign.Center, "r" => HAlign.Right, _ => HAlign.Left };

    private static VAnchor ParseAnchor(string a) => a switch { "m" => VAnchor.Middle, "b" => VAnchor.Bottom, _ => VAnchor.Top };
}
