using System.Globalization;
using System.Text;

namespace Forge.Core.Pptx;

/// <summary>Small helpers for writing OOXML by hand: namespaces, EMU conversion, escaping and colour elements.</summary>
internal static class X
{
    public const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
    public const string NsC = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    public const string RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public const string Header = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
    public const string RootNs = $"xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\"";

    public static long Emu(double inches) => (long)Math.Round(inches * 914400.0, MidpointRounding.AwayFromZero);

    public static string I(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string D(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Escapes text for XML and drops characters XML 1.0 cannot carry.</summary>
    public static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    if (c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c < 0xD800) || (c >= 0xE000 && c <= 0xFFFD) || char.IsSurrogate(c)) sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    public static string Hex(string color)
    {
        string h = color.TrimStart('#').ToUpperInvariant();
        return h.Length >= 6 ? h[..6] : h.PadRight(6, '0');
    }

    /// <summary>An srgbClr element, with an alpha child when the colour is see-through. Alpha is percent opaque.</summary>
    public static string Clr(string color, double? alphaPct = null) =>
        alphaPct is { } a
            ? $"<a:srgbClr val=\"{Hex(color)}\"><a:alpha val=\"{(int)(Math.Max(0, Math.Min(100, a)) * 1000)}\"/></a:srgbClr>"
            : $"<a:srgbClr val=\"{Hex(color)}\"/>";

    public static string Solid(string color, double? alphaPct = null) => $"<a:solidFill>{Clr(color, alphaPct)}</a:solidFill>";

    public static string Rels(IEnumerable<(string Id, string Type, string Target)> rels)
    {
        var sb = new StringBuilder(Header);
        sb.Append($"<Relationships xmlns=\"{RelNs}\">");
        foreach (var (id, type, target) in rels)
            sb.Append($"<Relationship Id=\"{id}\" Type=\"{type}\" Target=\"{Esc(target)}\"/>");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    public static class Rel
    {
        private const string Office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        public const string OfficeDocument = Office + "officeDocument";
        public const string Core = "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties";
        public const string Extended = Office + "extended-properties";
        public const string Slide = Office + "slide";
        public const string SlideMaster = Office + "slideMaster";
        public const string SlideLayout = Office + "slideLayout";
        public const string NotesMaster = Office + "notesMaster";
        public const string NotesSlide = Office + "notesSlide";
        public const string Theme = Office + "theme";
        public const string PresProps = Office + "presProps";
        public const string ViewProps = Office + "viewProps";
        public const string TableStyles = Office + "tableStyles";
        public const string Chart = Office + "chart";
        public const string Package = Office + "package";
    }

    public static class Ct
    {
        private const string Pml = "application/vnd.openxmlformats-officedocument.presentationml.";
        public const string Presentation = Pml + "presentation.main+xml";
        public const string Slide = Pml + "slide+xml";
        public const string SlideLayout = Pml + "slideLayout+xml";
        public const string SlideMaster = Pml + "slideMaster+xml";
        public const string NotesMaster = Pml + "notesMaster+xml";
        public const string NotesSlide = Pml + "notesSlide+xml";
        public const string PresProps = Pml + "presProps+xml";
        public const string ViewProps = Pml + "viewProps+xml";
        public const string TableStyles = Pml + "tableStyles+xml";
        public const string Theme = "application/vnd.openxmlformats-officedocument.theme+xml";
        public const string Core = "application/vnd.openxmlformats-package.core-properties+xml";
        public const string Extended = "application/vnd.openxmlformats-officedocument.extended-properties+xml";
        public const string Chart = "application/vnd.openxmlformats-officedocument.drawingml.chart+xml";
        public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    }
}
