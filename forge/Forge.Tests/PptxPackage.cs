using System.IO.Compression;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Forge.Core.Draw;
using Forge.Core.Ir;
using Forge.Core.Pptx;
using Forge.Core.Preview;

namespace Forge.Tests;

/// <summary>Builds decks by hand (every shape, gradient, glow, notes, chart) and checks the package the writer produces: it must pass
/// the Open XML SDK validator, contain every text, and render to a non-blank preview. The validator lives in the tests only.</summary>
public static class PptxPackage
{
    public static string OutDir
    {
        get
        {
            string dir = Environment.GetEnvironmentVariable("FORGE_TEST_OUT") ?? Path.Combine(AppContext.BaseDirectory, "out");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static int Run()
    {
        var check = new Check("pptx");
        foreach (var theme in Theme.All)
        {
            var deck = HandMade(theme);
            byte[] bytes = PptxWriter.ToBytes(deck);
            File.WriteAllBytes(Path.Combine(OutDir, $"handmade-{theme.Key}.pptx"), bytes);
            Inspect(check, deck, bytes, theme.Key);
            for (int i = 0; i < deck.Slides.Count; i++)
            {
                using var bmp = PngRenderer.RenderSlide(deck, i, 960);
                check.That(bmp.Width == 960 && bmp.Height == 540, $"{theme.Key} slide {i + 1}: preview size {bmp.Width}x{bmp.Height}");
                check.That(DistinctColours(bmp) > 12, $"{theme.Key} slide {i + 1}: preview is not blank");
                PngRenderer.SavePng(deck, i, Path.Combine(OutDir, $"handmade-{theme.Key}-{i + 1}.png"), 1280);
            }
        }
        return check.Finish();
    }

    /// <summary>Everything the Open XML SDK reports for a package, as short messages. Empty means valid.</summary>
    public static List<string> Validate(byte[] pptx)
    {
        var problems = new List<string>();
        using var ms = new MemoryStream(pptx);
        using var doc = PresentationDocument.Open(ms, false);
        var validator = new OpenXmlValidator(FileFormatVersions.Office2019);
        foreach (var error in validator.Validate(doc).Take(25))
            problems.Add($"{error.Part?.Uri}: {error.Description} ({error.Path?.XPath})");
        foreach (var embedded in EmbeddedWorkbooks(pptx))
        {
            using var wb = new MemoryStream(embedded);
            using var sheet = SpreadsheetDocument.Open(wb, false);
            foreach (var error in new OpenXmlValidator(FileFormatVersions.Office2019).Validate(sheet).Take(10))
                problems.Add($"embedded workbook {error.Part?.Uri}: {error.Description}");
        }
        return problems;
    }

    private static IEnumerable<byte[]> EmbeddedWorkbooks(byte[] pptx)
    {
        using var zip = new ZipArchive(new MemoryStream(pptx), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("ppt/embeddings/", StringComparison.Ordinal)))
        {
            using var s = entry.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            yield return m.ToArray();
        }
    }

    private static void Inspect(Check check, DeckIr deck, byte[] bytes, string label)
    {
        var problems = Validate(bytes);
        check.That(problems.Count == 0, $"{label}: package validates", string.Join(" | ", problems.Take(5)));

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        check.That(names[0] == "[Content_Types].xml", $"{label}: [Content_Types].xml is the first entry");
        check.That(names.Count(n => Regex.IsMatch(n, @"^ppt/slides/slide\d+\.xml$")) == deck.Slides.Count, $"{label}: one slide part per slide");
        check.That(names.Count(n => Regex.IsMatch(n, @"^ppt/notesSlides/notesSlide\d+\.xml$")) == deck.Slides.Count(s => s.Notes.Trim().Length > 0), $"{label}: notes parts match slides with notes");
        int charts = deck.Slides.Sum(s => s.Elements.OfType<ChartEl>().Count());
        check.That(names.Count(n => Regex.IsMatch(n, @"^ppt/charts/chart\d+\.xml$")) == charts, $"{label}: one chart part per chart");
        check.That(names.Count(n => n.StartsWith("ppt/embeddings/", StringComparison.Ordinal)) == charts, $"{label}: one embedded workbook per chart");
        check.That(names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count, $"{label}: no duplicate part names");

        for (int i = 0; i < deck.Slides.Count; i++)
        {
            string xml = new StreamReader(zip.GetEntry($"ppt/slides/slide{i + 1}.xml")!.Open()).ReadToEnd();
            foreach (var text in deck.Slides[i].Elements.OfType<TextEl>())
                foreach (string line in text.Text.Split('\n'))
                    if (line.Length > 0)
                        check.That(xml.Contains(System.Security.SecurityElement.Escape(line), StringComparison.Ordinal), $"{label} slide {i + 1}: text present", line);
        }

        using var ms = new MemoryStream(bytes);
        using var doc = PresentationDocument.Open(ms, false);
        check.That(doc.PresentationPart!.SlideParts.Count() == deck.Slides.Count, $"{label}: SDK sees every slide");
        check.That(doc.PackageProperties.Title == deck.Title, $"{label}: title survives XML escaping", doc.PackageProperties.Title ?? "(null)");
    }

    private static int DistinctColours(System.Drawing.Bitmap bmp)
    {
        var seen = new HashSet<int>();
        for (int y = 0; y < bmp.Height; y += 7)
            for (int x = 0; x < bmp.Width; x += 7)
                seen.Add(bmp.GetPixel(x, y).ToArgb());
        return seen.Count;
    }

    private static DeckIr HandMade(Theme theme)
    {
        var deck = new DeckIr { Theme = theme, Title = "Hand-made <deck> & \"quotes\"" };

        var s1 = new SlideIr { Index = 0, Layout = "handmade", Notes = "First paragraph.\nSecond one has <angle brackets> & an ampersand.", Background = new SlideBackground(theme.Bg, theme.Bg2, 90) };
        var c = new Canvas(s1, theme);
        string[] shapes = ["rect", "rounded", "ellipse", "chevron", "pentagon", "hexagon", "triangle", "arrow", "donut", "trapezoid", "diamond", "arc"];
        for (int i = 0; i < shapes.Length; i++)
        {
            double x = 0.5 + i % 6 * 2.1, y = 0.5 + i / 6 * 1.5;
            c.Box(x, y, 1.8, 1.2, fill: theme.Accent(i), line: i % 3 == 0 ? theme.CardLine : null, shape: shapes[i], shadow: i % 2 == 0,
                alpha: i == 5 ? 60 : null, glow: i == 7 ? new Glow(theme.Primary, 8, 40) : null, dash: i == 2);
        }
        c.Box(0.5, 3.7, 3.5, 1.3, gradient: (theme.Primary, theme.Accent(2)), gradientAngle: 45, shape: "rounded", radius: 0.2);
        c.Line(4.4, 3.9, 7.5, 3.9, color: theme.Primary, width: 3, arrow: true);
        c.Line(4.4, 4.4, 7.5, 4.9, color: theme.Muted, width: 1.5, dash: true, alpha: 60);
        c.Text(0.5, 5.2, 7.5, 0.9, "Café — “smart quotes” & <tags> 100% | ✓", size: 24, role: "title");
        c.Text(0.5, 6.1, 7.5, 0.5, "Line one\nLine two", size: 14, role: "body", align: "c");
        c.Icon(8.2, 3.8, 0.8, Icons.Names.First(), theme.Primary);
        c.Badge(9.6, 4.2, 0.9, Icons.Names.Skip(3).First(), glow: new Glow(theme.Primary, 6, 50));
        deck.Slides.Add(s1);

        var s2 = new SlideIr { Index = 1, Layout = "bars", Background = new SlideBackground(theme.Bg) };
        var c2 = new Canvas(s2, theme);
        c2.Text(0.6, 0.4, 12, 0.8, "Chart slide without notes", size: 32, role: "title", align: "l");
        c2.Chart(new ChartEl(0.8, 1.6, 8, 4.5)
        {
            Categories = ["Alpha", "Bravo & co", "Charlie <3", "Delta"],
            Values = [12.5, 48, 7.25, 30],
            Highlighted = [1],
            BarColor = theme.Muted,
            HighlightColor = theme.Primary,
            TextColor = theme.Text,
            Font = theme.FontBody,
            NumberFormat = "#,##0.0\"x\"",
            AxisMax = 60,
        });
        deck.Slides.Add(s2);
        return deck;
    }
}
