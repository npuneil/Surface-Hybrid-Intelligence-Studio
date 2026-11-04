using System.IO.Compression;
using System.Text;
using Forge.Core.Ir;

namespace Forge.Core.Pptx;

/// <summary>Writes a <see cref="DeckIr"/> as a .pptx: native, editable shapes and text, speaker notes, and bar charts with their data
/// embedded. The package is built by hand (no PowerPoint, no Office SDK), so it works on any Surface and offline.</summary>
public static class PptxWriter
{
    public static void Save(DeckIr deck, string path)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = full + ".tmp";
        File.WriteAllBytes(temp, ToBytes(deck));
        File.Move(temp, full, overwrite: true);
    }

    public static byte[] ToBytes(DeckIr deck)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) Build(zip, deck);
        return ms.ToArray();
    }

    private static void Build(ZipArchive zip, DeckIr deck)
    {
        var theme = deck.Theme;
        var overrides = new List<(string Part, string Type)>();
        var binary = new List<(string Name, byte[] Data)>();
        int slideCount = deck.Slides.Count;

        void Part(string name, string contentType, string xml, bool register = true)
        {
            binary.Add((name, new UTF8Encoding(false).GetBytes(xml)));
            if (register) overrides.Add(("/" + name, contentType));
        }

        // Fixed parts.
        Part("docProps/core.xml", X.Ct.Core, PackageParts.Core(deck, DateTime.UtcNow));
        Part("docProps/app.xml", X.Ct.Extended, PackageParts.App(slideCount));
        Part("ppt/presProps.xml", X.Ct.PresProps, PackageParts.PresProps());
        Part("ppt/viewProps.xml", X.Ct.ViewProps, PackageParts.ViewProps());
        Part("ppt/tableStyles.xml", X.Ct.TableStyles, PackageParts.TableStyles());
        Part("ppt/theme/theme1.xml", X.Ct.Theme, PackageParts.Theme(theme, "Forge " + theme.Label));
        Part("ppt/theme/theme2.xml", X.Ct.Theme, PackageParts.Theme(theme, "Forge Notes"));
        Part("ppt/slideMasters/slideMaster1.xml", X.Ct.SlideMaster, PackageParts.SlideMaster(theme));
        Part("ppt/slideMasters/_rels/slideMaster1.xml.rels", "", X.Rels([("rId1", X.Rel.SlideLayout, "../slideLayouts/slideLayout1.xml"), ("rId2", X.Rel.Theme, "../theme/theme1.xml")]), register: false);
        Part("ppt/slideLayouts/slideLayout1.xml", X.Ct.SlideLayout, PackageParts.BlankLayout());
        Part("ppt/slideLayouts/_rels/slideLayout1.xml.rels", "", X.Rels([("rId1", X.Rel.SlideMaster, "../slideMasters/slideMaster1.xml")]), register: false);
        Part("ppt/notesMasters/notesMaster1.xml", X.Ct.NotesMaster, PackageParts.NotesMaster(theme));
        Part("ppt/notesMasters/_rels/notesMaster1.xml.rels", "", X.Rels([("rId1", X.Rel.Theme, "../theme/theme2.xml")]), register: false);

        // Slides, their notes and charts.
        var slideRels = new List<(string Id, string Type, string Target)>();
        int chartNumber = 0;
        for (int i = 0; i < slideCount; i++)
        {
            var slide = deck.Slides[i];
            int n = i + 1;
            var rels = new List<(string Id, string Type, string Target)> { ("rId1", X.Rel.SlideLayout, "../slideLayouts/slideLayout1.xml") };
            int rel = 2;
            if (slide.Notes.Trim().Length > 0)
            {
                rels.Add(($"rId{rel++}", X.Rel.NotesSlide, $"../notesSlides/notesSlide{n}.xml"));
                Part($"ppt/notesSlides/notesSlide{n}.xml", X.Ct.NotesSlide, PackageParts.NotesSlide(slide.Notes));
                Part($"ppt/notesSlides/_rels/notesSlide{n}.xml.rels", "", X.Rels([("rId1", X.Rel.NotesMaster, "../notesMasters/notesMaster1.xml"), ("rId2", X.Rel.Slide, $"../slides/slide{n}.xml")]), register: false);
            }
            var chartIds = new List<string>();
            foreach (var chart in slide.Elements.OfType<ChartEl>())
            {
                chartNumber++;
                string id = $"rId{rel++}";
                chartIds.Add(id);
                rels.Add((id, X.Rel.Chart, $"../charts/chart{chartNumber}.xml"));
                Part($"ppt/charts/chart{chartNumber}.xml", X.Ct.Chart, ChartXml.Write(chart));
                Part($"ppt/charts/_rels/chart{chartNumber}.xml.rels", "", X.Rels([("rId1", X.Rel.Package, $"../embeddings/Microsoft_Excel_Sheet{chartNumber}.xlsx")]), register: false);
                binary.Add(($"ppt/embeddings/Microsoft_Excel_Sheet{chartNumber}.xlsx", ChartXml.Workbook(chart)));
            }
            Part($"ppt/slides/slide{n}.xml", X.Ct.Slide, SlideXml.Write(slide, chartIds));
            Part($"ppt/slides/_rels/slide{n}.xml.rels", "", X.Rels(rels), register: false);
            slideRels.Add(($"rId{7 + i}", X.Rel.Slide, $"slides/slide{n}.xml"));
        }

        var presentationRels = new List<(string Id, string Type, string Target)>
        {
            ("rId1", X.Rel.SlideMaster, "slideMasters/slideMaster1.xml"),
            ("rId2", X.Rel.Theme, "theme/theme1.xml"),
            ("rId3", X.Rel.NotesMaster, "notesMasters/notesMaster1.xml"),
            ("rId4", X.Rel.PresProps, "presProps.xml"),
            ("rId5", X.Rel.ViewProps, "viewProps.xml"),
            ("rId6", X.Rel.TableStyles, "tableStyles.xml"),
        };
        presentationRels.AddRange(slideRels);
        Part("ppt/presentation.xml", X.Ct.Presentation, PackageParts.Presentation(slideCount, 3, 7, deck.WidthIn, deck.HeightIn));
        Part("ppt/_rels/presentation.xml.rels", "", X.Rels(presentationRels), register: false);
        Part("_rels/.rels", "", X.Rels([("rId1", X.Rel.OfficeDocument, "ppt/presentation.xml"), ("rId2", X.Rel.Core, "docProps/core.xml"), ("rId3", X.Rel.Extended, "docProps/app.xml")]), register: false);

        var types = new StringBuilder(X.Header);
        types.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        types.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        types.Append($"<Default Extension=\"xlsx\" ContentType=\"{X.Ct.Xlsx}\"/>");
        foreach (var (part, type) in overrides) types.Append($"<Override PartName=\"{part}\" ContentType=\"{type}\"/>");
        types.Append("</Types>");

        Write(zip, "[Content_Types].xml", new UTF8Encoding(false).GetBytes(types.ToString()));
        foreach (var (name, data) in binary) Write(zip, name, data);
    }

    private static void Write(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(data);
    }
}
