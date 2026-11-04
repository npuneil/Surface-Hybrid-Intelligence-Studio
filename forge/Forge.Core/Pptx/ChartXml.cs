using System.IO.Compression;
using System.Text;
using Forge.Core.Ir;

namespace Forge.Core.Pptx;

/// <summary>A clustered bar chart part plus the small workbook that backs it, so "Edit Data" works in PowerPoint.</summary>
internal static class ChartXml
{
    private const string Sheet = "Sheet1";

    public static string Write(ChartEl c)
    {
        int n = Math.Max(1, c.Categories.Count);
        var cats = c.Categories.Count == 0 ? ["n/a"] : c.Categories;
        var vals = c.Values.Count == 0 ? [0.0] : c.Values;
        string text = X.Solid(c.TextColor);
        string font = $"<a:latin typeface=\"{X.Esc(c.Font)}\"/>";
        int labelSz = (int)Math.Round(c.LabelPt * 100);
        var sb = new StringBuilder(4096);
        sb.Append(X.Header);
        sb.Append($"<c:chartSpace xmlns:c=\"{X.NsC}\" xmlns:a=\"{X.NsA}\" xmlns:r=\"{X.NsR}\">");
        sb.Append("<c:date1904 val=\"0\"/><c:roundedCorners val=\"0\"/><c:chart><c:autoTitleDeleted val=\"1\"/><c:plotArea><c:layout/>");
        sb.Append("<c:barChart><c:barDir val=\"bar\"/><c:grouping val=\"clustered\"/><c:varyColors val=\"0\"/>");
        sb.Append("<c:ser><c:idx val=\"0\"/><c:order val=\"0\"/>");
        sb.Append($"<c:tx><c:strRef><c:f>{Sheet}!$B$1</c:f><c:strCache><c:ptCount val=\"1\"/><c:pt idx=\"0\"><c:v>Series</c:v></c:pt></c:strCache></c:strRef></c:tx>");
        sb.Append($"<c:spPr>{X.Solid(c.BarColor)}</c:spPr><c:invertIfNegative val=\"0\"/>");
        foreach (int i in c.Highlighted.Distinct().Where(i => i >= 0 && i < n).Order())
            sb.Append($"<c:dPt><c:idx val=\"{i}\"/><c:invertIfNegative val=\"0\"/><c:bubble3D val=\"0\"/><c:spPr>{X.Solid(c.HighlightColor)}</c:spPr></c:dPt>");
        sb.Append($"<c:dLbls><c:numFmt formatCode=\"{X.Esc(c.NumberFormat)}\" sourceLinked=\"0\"/><c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr>");
        sb.Append($"<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"{labelSz}\" b=\"1\">{text}{font}</a:defRPr></a:pPr><a:endParaRPr lang=\"en-US\"/></a:p></c:txPr>");
        sb.Append("<c:dLblPos val=\"outEnd\"/><c:showLegendKey val=\"0\"/><c:showVal val=\"1\"/><c:showCatName val=\"0\"/><c:showSerName val=\"0\"/><c:showPercent val=\"0\"/><c:showBubbleSize val=\"0\"/></c:dLbls>");
        sb.Append($"<c:cat><c:strRef><c:f>{Sheet}!$A$2:$A${n + 1}</c:f><c:strCache><c:ptCount val=\"{n}\"/>");
        for (int i = 0; i < n; i++) sb.Append($"<c:pt idx=\"{i}\"><c:v>{X.Esc(cats[i])}</c:v></c:pt>");
        sb.Append("</c:strCache></c:strRef></c:cat>");
        sb.Append($"<c:val><c:numRef><c:f>{Sheet}!$B$2:$B${n + 1}</c:f><c:numCache><c:formatCode>General</c:formatCode><c:ptCount val=\"{n}\"/>");
        for (int i = 0; i < n; i++) sb.Append($"<c:pt idx=\"{i}\"><c:v>{X.D(i < vals.Count ? vals[i] : 0)}</c:v></c:pt>");
        sb.Append("</c:numCache></c:numRef></c:val></c:ser>");
        sb.Append("<c:gapWidth val=\"55\"/><c:axId val=\"500000001\"/><c:axId val=\"500000002\"/></c:barChart>");
        sb.Append("<c:catAx><c:axId val=\"500000001\"/><c:scaling><c:orientation val=\"maxMin\"/></c:scaling><c:delete val=\"0\"/><c:axPos val=\"l\"/>");
        sb.Append("<c:numFmt formatCode=\"General\" sourceLinked=\"0\"/><c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>");
        sb.Append("<c:spPr><a:ln><a:noFill/></a:ln></c:spPr>");
        sb.Append($"<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"{labelSz}\">{text}{font}</a:defRPr></a:pPr><a:endParaRPr lang=\"en-US\"/></a:p></c:txPr>");
        sb.Append("<c:crossAx val=\"500000002\"/><c:crosses val=\"autoZero\"/><c:auto val=\"1\"/><c:lblAlgn val=\"ctr\"/><c:lblOffset val=\"100\"/><c:noMultiLvlLbl val=\"0\"/></c:catAx>");
        sb.Append("<c:valAx><c:axId val=\"500000002\"/><c:scaling><c:orientation val=\"minMax\"/>");
        if (c.AxisMax > 0) sb.Append($"<c:max val=\"{X.D(c.AxisMax)}\"/><c:min val=\"0\"/>");
        sb.Append("</c:scaling><c:delete val=\"1\"/><c:axPos val=\"t\"/><c:numFmt formatCode=\"General\" sourceLinked=\"0\"/>");
        sb.Append("<c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/><c:crossAx val=\"500000001\"/><c:crosses val=\"autoZero\"/><c:crossBetween val=\"between\"/></c:valAx>");
        sb.Append("<c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr></c:plotArea><c:plotVisOnly val=\"1\"/><c:dispBlanksAs val=\"gap\"/></c:chart>");
        sb.Append("<c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr>");
        sb.Append($"<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=\"{labelSz}\">{text}{font}</a:defRPr></a:pPr><a:endParaRPr lang=\"en-US\"/></a:p></c:txPr>");
        sb.Append("<c:externalData r:id=\"rId1\"><c:autoUpdate val=\"0\"/></c:externalData></c:chartSpace>");
        return sb.ToString();
    }

    /// <summary>A minimal workbook (one sheet: categories in column A, values in column B) for the chart's embedded data.</summary>
    public static byte[] Workbook(ChartEl c)
    {
        int n = Math.Max(1, c.Categories.Count);
        var sheet = new StringBuilder(X.Header);
        sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        sheet.Append("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t></t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t>Series</t></is></c></row>");
        for (int i = 0; i < n; i++)
        {
            string cat = i < c.Categories.Count ? c.Categories[i] : "n/a";
            double v = i < c.Values.Count ? c.Values[i] : 0;
            sheet.Append($"<row r=\"{i + 2}\"><c r=\"A{i + 2}\" t=\"inlineStr\"><is><t>{X.Esc(cat)}</t></is></c><c r=\"B{i + 2}\"><v>{X.D(v)}</v></c></row>");
        }
        sheet.Append("</sheetData></worksheet>");

        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string rels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", X.Header
                + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
                + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
            Add(zip, "_rels/.rels", X.Rels([("rId1", X.Rel.OfficeDocument, "xl/workbook.xml")]));
            Add(zip, "xl/workbook.xml", X.Header + $"<workbook xmlns=\"{main}\" xmlns:r=\"{rels}\"><sheets><sheet name=\"{Sheet}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels", X.Rels([
                ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "worksheets/sheet1.xml"),
                ("rId2", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml")]));
            Add(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
            Add(zip, "xl/styles.xml", X.Header + $"<styleSheet xmlns=\"{main}\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
                + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
                + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
                + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
                + "<cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs></styleSheet>");
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(new UTF8Encoding(false).GetBytes(content));
    }
}
