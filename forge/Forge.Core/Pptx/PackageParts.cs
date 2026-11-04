using System.Text;
using Forge.Core.Ir;

namespace Forge.Core.Pptx;

/// <summary>The fixed parts of every deck: theme, slide master, blank layout, notes master and the small property parts.
/// The theme colours and fonts come from the Forge theme, so "Reset" and new shapes in PowerPoint stay on brand.</summary>
internal static class PackageParts
{
    private static string Grp() =>
        "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>"
        + "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    public static string ClrMap(bool dark) => dark
        ? "<p:clrMap bg1=\"dk1\" tx1=\"lt1\" bg2=\"dk2\" tx2=\"lt2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>"
        : "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>";

    public static string Theme(Theme t, string name)
    {
        string dk1 = t.Dark ? t.Bg : t.Text, lt1 = t.Dark ? t.Text : t.Bg;
        string dk2 = t.Dark ? t.Bg2 : t.Subtext, lt2 = t.Dark ? t.Subtext : t.Bg2;
        string C(string tag, string hex) => $"<a:{tag}><a:srgbClr val=\"{X.Hex(hex)}\"/></a:{tag}>";
        var sb = new StringBuilder(X.Header);
        sb.Append($"<a:theme xmlns:a=\"{X.NsA}\" name=\"{X.Esc(name)}\"><a:themeElements>");
        sb.Append($"<a:clrScheme name=\"{X.Esc(name)}\">{C("dk1", dk1)}{C("lt1", lt1)}{C("dk2", dk2)}{C("lt2", lt2)}");
        for (int i = 0; i < 5; i++) sb.Append(C($"accent{i + 1}", t.Accent(i)));
        sb.Append(C("accent6", t.Muted) + C("hlink", t.Primary) + C("folHlink", t.Muted) + "</a:clrScheme>");
        sb.Append($"<a:fontScheme name=\"{X.Esc(name)}\"><a:majorFont><a:latin typeface=\"{X.Esc(t.FontHead)}\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>");
        sb.Append($"<a:minorFont><a:latin typeface=\"{X.Esc(t.FontBody)}\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>");
        sb.Append("<a:fmtScheme name=\"Forge\"><a:fillStyleLst>");
        sb.Append("<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>");
        sb.Append("<a:gradFill rotWithShape=\"1\"><a:gsLst><a:gs pos=\"0\"><a:schemeClr val=\"phClr\"><a:tint val=\"90000\"/></a:schemeClr></a:gs><a:gs pos=\"100000\"><a:schemeClr val=\"phClr\"/></a:gs></a:gsLst><a:lin ang=\"5400000\" scaled=\"0\"/></a:gradFill>");
        sb.Append("<a:gradFill rotWithShape=\"1\"><a:gsLst><a:gs pos=\"0\"><a:schemeClr val=\"phClr\"/></a:gs><a:gs pos=\"100000\"><a:schemeClr val=\"phClr\"><a:shade val=\"90000\"/></a:schemeClr></a:gs></a:gsLst><a:lin ang=\"5400000\" scaled=\"0\"/></a:gradFill>");
        sb.Append("</a:fillStyleLst><a:lnStyleLst>");
        foreach (int w in new[] { 6350, 12700, 19050 })
            sb.Append($"<a:ln w=\"{w}\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>");
        sb.Append("</a:lnStyleLst><a:effectStyleLst>");
        for (int i = 0; i < 3; i++) sb.Append("<a:effectStyle><a:effectLst/></a:effectStyle>");
        sb.Append("</a:effectStyleLst><a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>");
        sb.Append("<a:solidFill><a:schemeClr val=\"phClr\"><a:tint val=\"95000\"/></a:schemeClr></a:solidFill>");
        sb.Append("<a:gradFill rotWithShape=\"1\"><a:gsLst><a:gs pos=\"0\"><a:schemeClr val=\"phClr\"/></a:gs><a:gs pos=\"100000\"><a:schemeClr val=\"phClr\"><a:shade val=\"92000\"/></a:schemeClr></a:gs></a:gsLst><a:lin ang=\"5400000\" scaled=\"0\"/></a:gradFill>");
        sb.Append("</a:bgFillStyleLst></a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>");
        return sb.ToString();
    }

    public static string SlideMaster(Theme t) =>
        X.Header + $"<p:sldMaster {X.RootNs}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>{Grp()}</p:spTree></p:cSld>"
        + ClrMap(t.Dark)
        + "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>"
        + "<p:txStyles>"
        + "<p:titleStyle><a:lvl1pPr algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\"><a:lnSpc><a:spcPct val=\"90000\"/></a:lnSpc><a:spcBef><a:spcPct val=\"0\"/></a:spcBef><a:buNone/><a:defRPr sz=\"4400\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mj-lt\"/><a:ea typeface=\"+mj-ea\"/><a:cs typeface=\"+mj-cs\"/></a:defRPr></a:lvl1pPr></p:titleStyle>"
        + "<p:bodyStyle><a:lvl1pPr marL=\"228600\" indent=\"-228600\" algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\"><a:lnSpc><a:spcPct val=\"90000\"/></a:lnSpc><a:spcBef><a:spcPts val=\"1000\"/></a:spcBef><a:buFont typeface=\"Arial\"/><a:buChar char=\"&#8226;\"/><a:defRPr sz=\"2800\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr></p:bodyStyle>"
        + "<p:otherStyle><a:defPPr><a:defRPr lang=\"en-US\"/></a:defPPr><a:lvl1pPr marL=\"0\" algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\"><a:defRPr sz=\"1800\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr></p:otherStyle>"
        + "</p:txStyles></p:sldMaster>";

    public static string BlankLayout() =>
        X.Header + $"<p:sldLayout {X.RootNs} type=\"blank\" preserve=\"1\"><p:cSld name=\"Blank\"><p:spTree>{Grp()}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    public static string NotesMaster(Theme t) =>
        X.Header + $"<p:notesMaster {X.RootNs}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>{Grp()}"
        + "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Slide Image Placeholder 1\"/><p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"sldImg\" idx=\"2\"/></p:nvPr></p:nvSpPr>"
        + "<p:spPr><a:xfrm><a:off x=\"685800\" y=\"1143000\"/><a:ext cx=\"5486400\" cy=\"3086100\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/><a:ln w=\"12700\"><a:solidFill><a:prstClr val=\"black\"/></a:solidFill></a:ln></p:spPr></p:sp>"
        + "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Notes Placeholder 2\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"body\" sz=\"quarter\" idx=\"3\"/></p:nvPr></p:nvSpPr>"
        + "<p:spPr><a:xfrm><a:off x=\"685800\" y=\"4400550\"/><a:ext cx=\"5486400\" cy=\"3600450\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>"
        + "<p:txBody><a:bodyPr vert=\"horz\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" rtlCol=\"0\"/><a:lstStyle/><a:p><a:pPr lvl=\"0\"/><a:r><a:rPr lang=\"en-US\"/><a:t>Click to edit Master text styles</a:t></a:r></a:p></p:txBody></p:sp>"
        + "</p:spTree></p:cSld>" + ClrMap(false)
        + "<p:notesStyle><a:lvl1pPr marL=\"0\" algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\"><a:defRPr sz=\"1200\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr></p:notesStyle></p:notesMaster>";

    public static string NotesSlide(string notes)
    {
        var sb = new StringBuilder(X.Header);
        sb.Append($"<p:notes {X.RootNs}><p:cSld><p:spTree>{Grp()}");
        sb.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Slide Image Placeholder 1\"/><p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"sldImg\"/></p:nvPr></p:nvSpPr><p:spPr/></p:sp>");
        sb.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Notes Placeholder 2\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/>");
        foreach (string para in notes.Replace("\r\n", "\n").Split('\n'))
            sb.Append($"<a:p><a:r><a:rPr lang=\"en-US\" dirty=\"0\"/><a:t>{X.Esc(para)}</a:t></a:r></a:p>");
        sb.Append("</p:txBody></p:sp></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>");
        return sb.ToString();
    }

    public static string Presentation(int slideCount, int notesMasterRel, int firstSlideRel, double widthIn, double heightIn)
    {
        var sb = new StringBuilder(X.Header);
        sb.Append($"<p:presentation {X.RootNs} saveSubsetFonts=\"1\"><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>");
        sb.Append($"<p:notesMasterIdLst><p:notesMasterId r:id=\"rId{notesMasterRel}\"/></p:notesMasterIdLst><p:sldIdLst>");
        for (int i = 0; i < slideCount; i++) sb.Append($"<p:sldId id=\"{256 + i}\" r:id=\"rId{firstSlideRel + i}\"/>");
        sb.Append($"</p:sldIdLst><p:sldSz cx=\"{X.I(X.Emu(widthIn))}\" cy=\"{X.I(X.Emu(heightIn))}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/>");
        sb.Append("<p:defaultTextStyle><a:defPPr><a:defRPr lang=\"en-US\"/></a:defPPr><a:lvl1pPr marL=\"0\" algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\"><a:defRPr sz=\"1800\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr></a:lvl1pPr></p:defaultTextStyle>");
        sb.Append("</p:presentation>");
        return sb.ToString();
    }

    public static string PresProps() => X.Header + $"<p:presentationPr {X.RootNs}/>";

    public static string ViewProps() =>
        X.Header + $"<p:viewPr {X.RootNs}><p:normalViewPr><p:restoredLeft sz=\"15620\"/><p:restoredTop sz=\"94660\"/></p:normalViewPr></p:viewPr>";

    public static string TableStyles() =>
        X.Header + $"<a:tblStyleLst xmlns:a=\"{X.NsA}\" def=\"{{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}}\"/>";

    public static string Core(DeckIr deck, DateTime utc)
    {
        string stamp = utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        return X.Header + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">"
            + $"<dc:title>{X.Esc(deck.Title)}</dc:title><dc:creator>{X.Esc(deck.Author)}</dc:creator><cp:lastModifiedBy>{X.Esc(deck.Author)}</cp:lastModifiedBy><dc:description>{X.Esc(deck.Comments)}</dc:description>"
            + $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{stamp}</dcterms:created><dcterms:modified xsi:type=\"dcterms:W3CDTF\">{stamp}</dcterms:modified></cp:coreProperties>";
    }

    public static string App(int slides) =>
        X.Header + "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">"
        + $"<TotalTime>0</TotalTime><Application>Offline PPTX Forge</Application><PresentationFormat>Widescreen</PresentationFormat><Slides>{slides}</Slides></Properties>";
}
