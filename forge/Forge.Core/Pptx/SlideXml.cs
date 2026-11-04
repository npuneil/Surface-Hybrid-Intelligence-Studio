using System.Text;
using Forge.Core.Ir;

namespace Forge.Core.Pptx;

/// <summary>Turns one <see cref="SlideIr"/> into slide XML. Shapes carry explicit fill, line and effects and no theme style reference,
/// so what PowerPoint draws is exactly what the IR says (and what the PNG preview draws).</summary>
internal static class SlideXml
{
    /// <summary>Writes the slide. <paramref name="chartRelIds"/> gives the relationship id of each chart element in order of appearance.</summary>
    public static string Write(SlideIr slide, IReadOnlyList<string> chartRelIds)
    {
        var sb = new StringBuilder(8192);
        sb.Append(X.Header);
        sb.Append($"<p:sld {X.RootNs}><p:cSld>");
        if (slide.Background is { } bg) sb.Append(Background(bg));
        sb.Append("<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>");
        sb.Append("<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>");
        int id = 2, chart = 0;
        foreach (var element in slide.Elements)
        {
            switch (element)
            {
                case BoxEl box: Box(sb, box, id); break;
                case LineEl line: Line(sb, line, id); break;
                case TextEl text: Text(sb, text, id); break;
                case ChartEl chartEl: Chart(sb, chartEl, id, chartRelIds[chart++]); break;
            }
            id++;
        }
        sb.Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
        return sb.ToString();
    }

    private static string Background(SlideBackground bg) =>
        "<p:bg><p:bgPr>" + (bg.To is { } to ? Gradient(new GradientFill(bg.From, to, bg.AngleDeg), null) : X.Solid(bg.From)) + "<a:effectLst/></p:bgPr></p:bg>";

    private static string Gradient(GradientFill g, double? alpha) =>
        "<a:gradFill rotWithShape=\"1\"><a:gsLst>"
        + $"<a:gs pos=\"0\">{X.Clr(g.From, alpha)}</a:gs><a:gs pos=\"100000\">{X.Clr(g.To, alpha)}</a:gs>"
        + $"</a:gsLst><a:lin ang=\"{(int)Math.Round(g.AngleDeg * 60000)}\" scaled=\"0\"/></a:gradFill>";

    private static string Preset(ShapeKind kind) => kind switch
    {
        ShapeKind.Rect => "rect",
        ShapeKind.Rounded => "roundRect",
        ShapeKind.Ellipse => "ellipse",
        ShapeKind.Chevron => "chevron",
        ShapeKind.Pentagon => "homePlate",
        ShapeKind.Hexagon => "hexagon",
        ShapeKind.Triangle => "triangle",
        ShapeKind.Arrow => "rightArrow",
        ShapeKind.Donut => "donut",
        ShapeKind.Trapezoid => "trapezoid",
        ShapeKind.Diamond => "diamond",
        ShapeKind.BlockArc => "blockArc",
        _ => "roundRect",
    };

    private static string Name(ShapeKind kind) => kind switch
    {
        ShapeKind.Rounded => "Rounded Rectangle",
        ShapeKind.Rect => "Rectangle",
        ShapeKind.Ellipse => "Oval",
        ShapeKind.BlockArc => "Block Arc",
        ShapeKind.Arrow => "Right Arrow",
        _ => kind.ToString(),
    };

    private static void Xfrm(StringBuilder sb, double x, double y, double w, double h, double rotation = 0, bool flipH = false, bool flipV = false)
    {
        sb.Append("<a:xfrm");
        if (rotation != 0) sb.Append($" rot=\"{(int)Math.Round(((rotation % 360) + 360) % 360 * 60000)}\"");
        if (flipH) sb.Append(" flipH=\"1\"");
        if (flipV) sb.Append(" flipV=\"1\"");
        sb.Append($"><a:off x=\"{X.I(X.Emu(x))}\" y=\"{X.I(X.Emu(y))}\"/><a:ext cx=\"{X.I(Math.Max(0, X.Emu(w)))}\" cy=\"{X.I(Math.Max(0, X.Emu(h)))}\"/></a:xfrm>");
    }

    private static void Box(StringBuilder sb, BoxEl b, int id)
    {
        sb.Append($"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{Name(b.Kind)} {id - 1}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr>");
        Xfrm(sb, b.X, b.Y, b.W, b.H, b.RotationDeg);
        sb.Append($"<a:prstGeom prst=\"{Preset(b.Kind)}\"><a:avLst>");
        if (b.Kind == ShapeKind.Rounded) sb.Append($"<a:gd name=\"adj\" fmla=\"val {(int)Math.Round(b.CornerAdj * 100000)}\"/>");
        sb.Append("</a:avLst></a:prstGeom>");
        if (b.Gradient is { } g) sb.Append(Gradient(g, b.AlphaPct));
        else if (b.Fill is { } fill) sb.Append(X.Solid(fill, b.AlphaPct));
        else sb.Append("<a:noFill/>");
        if (b.Line is { } line)
            sb.Append($"<a:ln w=\"{(int)Math.Round(b.LineWidthPt * 12700)}\">{X.Solid(line)}{(b.Dash ? "<a:prstDash val=\"dash\"/>" : "")}</a:ln>");
        else sb.Append("<a:ln><a:noFill/></a:ln>");
        if (b.Glow is not null || b.Shadow)
        {
            sb.Append("<a:effectLst>");
            if (b.Glow is { } glow)
                sb.Append($"<a:glow rad=\"{(int)(glow.RadiusPt * 12700)}\">{X.Clr(glow.Color, glow.AlphaPct)}</a:glow>");
            if (b.Shadow)
                sb.Append("<a:outerShdw blurRad=\"228600\" dist=\"50800\" dir=\"5400000\" algn=\"t\" rotWithShape=\"0\"><a:srgbClr val=\"000000\"><a:alpha val=\"14000\"/></a:srgbClr></a:outerShdw>");
            sb.Append("</a:effectLst>");
        }
        sb.Append("</p:spPr><p:txBody><a:bodyPr rtlCol=\"0\" anchor=\"ctr\"/><a:lstStyle/><a:p><a:endParaRPr lang=\"en-US\"/></a:p></p:txBody></p:sp>");
    }

    private static void Line(StringBuilder sb, LineEl l, int id)
    {
        double x = Math.Min(l.X1, l.X2), y = Math.Min(l.Y1, l.Y2);
        sb.Append($"<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"{id}\" name=\"Connector {id - 1}\"/><p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr><p:spPr>");
        Xfrm(sb, x, y, Math.Abs(l.X2 - l.X1), Math.Abs(l.Y2 - l.Y1), 0, l.X2 < l.X1, l.Y2 < l.Y1);
        sb.Append("<a:prstGeom prst=\"line\"><a:avLst/></a:prstGeom>");
        sb.Append($"<a:ln w=\"{(int)Math.Round(l.WidthPt * 12700)}\">{X.Solid(l.Color, l.AlphaPct)}");
        if (l.Dash) sb.Append("<a:prstDash val=\"dash\"/>");
        if (l.Arrow) sb.Append("<a:tailEnd type=\"triangle\" w=\"med\" len=\"med\"/>");
        sb.Append("</a:ln></p:spPr></p:cxnSp>");
    }

    private static void Text(StringBuilder sb, TextEl t, int id)
    {
        sb.Append($"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{(t.IsIcon ? "Icon" : "TextBox")} {id - 1}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr>");
        Xfrm(sb, t.X, t.Y, t.W, t.H);
        sb.Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr><p:txBody>");
        string anchor = t.Anchor switch { VAnchor.Middle => "ctr", VAnchor.Bottom => "b", _ => "t" };
        sb.Append($"<a:bodyPr wrap=\"{(t.WordWrap ? "square" : "none")}\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\" rtlCol=\"0\" anchor=\"{anchor}\"><a:noAutofit/></a:bodyPr><a:lstStyle/>");
        string algn = t.Align switch { HAlign.Center => "ctr", HAlign.Right => "r", _ => "l" };
        int size = (int)Math.Round(Math.Max(1, t.Pt) * 100);
        foreach (string para in t.Text.Split('\n'))
        {
            sb.Append($"<a:p><a:pPr algn=\"{algn}\">");
            if (t.LineSpacing is { } ls) sb.Append($"<a:lnSpc><a:spcPct val=\"{(int)Math.Round(ls * 100000)}\"/></a:lnSpc>");
            sb.Append("</a:pPr>");
            sb.Append($"<a:r><a:rPr lang=\"en-US\" sz=\"{size}\" i=\"{(t.Italic ? 1 : 0)}\" dirty=\"0\">{X.Solid(t.Color)}<a:latin typeface=\"{X.Esc(t.Font)}\"/></a:rPr><a:t>{X.Esc(para)}</a:t></a:r>");
            sb.Append("</a:p>");
        }
        sb.Append("</p:txBody></p:sp>");
    }

    private static void Chart(StringBuilder sb, ChartEl c, int id, string relId)
    {
        sb.Append($"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Chart {id - 1}\"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp=\"1\"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>");
        sb.Append($"<p:xfrm><a:off x=\"{X.I(X.Emu(c.X))}\" y=\"{X.I(X.Emu(c.Y))}\"/><a:ext cx=\"{X.I(X.Emu(c.W))}\" cy=\"{X.I(X.Emu(c.H))}\"/></p:xfrm>");
        sb.Append($"<a:graphic><a:graphicData uri=\"{X.NsC}\"><c:chart xmlns:c=\"{X.NsC}\" xmlns:r=\"{X.NsR}\" r:id=\"{relId}\"/></a:graphicData></a:graphic></p:graphicFrame>");
    }
}
