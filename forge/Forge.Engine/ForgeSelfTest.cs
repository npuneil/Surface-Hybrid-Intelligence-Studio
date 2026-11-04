using System.Drawing.Text;
using System.IO.Compression;
using System.Xml.Linq;
using Forge.Core.Harness;
using Forge.Core.Ir;
using Forge.Core.Layouts;
using Forge.Core.Pptx;
using Forge.Core.Preview;
using Forge.Core.Spec;
using SurfaceDeviceKit;

namespace Forge.Engine;

/// <summary>Checks that need no model and no network: that this PC has what the Forge draws with, that a deck comes out as a well-formed
/// .pptx with previews, that the device kit's own answers hold, and what the Forge would plan for this PC. The command line and the app run
/// the same list, so "works on this Surface" means the same thing in both.</summary>
public static class ForgeSelfTest
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static List<SelfTestResult> Run(DeviceDetectionService? devices = null)
    {
        devices ??= DeviceKit.Service;
        var results = new List<SelfTestResult>();
        void Add(string name, bool ok, string detail = "") => results.Add(new SelfTestResult(name, ok, detail));
        void Try(string name, Func<(bool Ok, string Detail)> check)
        {
            try
            {
                var (ok, detail) = check();
                Add(name, ok, detail);
            }
            catch (Exception exception)
            {
                Add(name, false, $"{exception.GetType().Name}: {exception.Message.Split('\n')[0].Trim()}");
            }
        }

        Try("fonts the layouts are measured with are installed", Fonts);
        Try("bundled briefs load and have a story to tell", Briefs);

        JsonSampleDeck? sample = null;
        Try("the renderer lays out every layout", () =>
        {
            sample = new JsonSampleDeck(SelfTestDeck.Spec());
            int expected = SpecJson.SlideObjects(sample.Spec).Count;
            foreach (var theme in Theme.All)
            {
                var deck = DeckBuilder.Build(sample.Spec, theme);
                if (deck.Slides.Count != expected) return (false, $"{theme.Label}: {deck.Slides.Count} of {expected} slides");
                sample.Decks[theme.Key] = deck;
            }
            return (true, $"{expected} slides in each of {Theme.All.Count} themes");
        });

        foreach (var theme in Theme.All)
        {
            Try($"a {theme.Label} deck is a well-formed .pptx", () => Package(sample, theme));
            Try($"a {theme.Label} deck previews as PNG", () => Preview(sample, theme));
        }

        Try("a deck is saved with its sidecar files, previews and contact sheet", () => Output(sample));
        Try("the Documents folder can be written to", DocumentsWritable);

        Try("this PC is detected and has a plan for the Forge", () => Plan(devices));
        Try("the story a deck tells about this PC comes from the detection", () => Story(devices));

        var kit = DeviceKitSelfTest.Run(devices.Catalog);
        int failed = kit.Count(r => !r.Passed);
        Add("the device kit's own checks hold", failed == 0, failed == 0 ? $"{kit.Count} checks" : $"{failed} of {kit.Count} failed: {string.Join("; ", kit.Where(r => !r.Passed).Take(3).Select(r => r.Name))}");
        return results;
    }

    private sealed class JsonSampleDeck(System.Text.Json.Nodes.JsonObject spec)
    {
        public System.Text.Json.Nodes.JsonObject Spec { get; } = spec;
        public Dictionary<string, DeckIr> Decks { get; } = new(StringComparer.Ordinal);
    }

    private static (bool, string) Fonts()
    {
        using var installed = new InstalledFontCollection();
        var have = new HashSet<string>(installed.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var needed = Theme.All.SelectMany(t => new[] { t.FontHead, t.FontBody, t.FontNumber }).Append(Icons.FontName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = needed.Where(n => !have.Contains(n)).ToList();
        return (missing.Count == 0, missing.Count == 0 ? string.Join(", ", needed) : $"missing: {string.Join(", ", missing)}");
    }

    private static (bool, string) Briefs()
    {
        var briefs = BundledBriefs.All;
        if (briefs.Count < 5) return (false, $"{briefs.Count} briefs");
        foreach (var brief in briefs)
        {
            if (brief.Client.Length == 0 || brief.Facts.Count < 2) return (false, $"{brief.Id} has no client or too few facts");
            var adapted = BriefAdapter.Adapt(brief, DeviceStory.Generic);
            if (Arcs.For(adapted, Arcs.Reserved(adapted)).Count == 0) return (false, $"{brief.Id} has no slide arc");
        }
        return (true, string.Join(", ", briefs.Select(b => b.Client)));
    }

    private static (bool, string) Package(JsonSampleDeck? sample, Theme theme)
    {
        if (sample is null || !sample.Decks.TryGetValue(theme.Key, out var deck)) return (false, "the renderer check did not run");
        return CheckPackage(PptxWriter.ToBytes(deck), deck.Slides.Count);
    }

    /// <summary>The structural checks a .pptx must pass before PowerPoint is asked to open it: the required parts exist, every XML part is
    /// well formed, and there is one slide part per slide.</summary>
    internal static (bool Ok, string Detail) CheckPackage(byte[] bytes, int expectedSlides)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read); }
        catch (InvalidDataException) { return (false, "the file is not a zip package"); }
        using (zip)
        {
            var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
            foreach (string required in new[] { "[Content_Types].xml", "_rels/.rels", "ppt/presentation.xml", "ppt/slideMasters/slideMaster1.xml", "docProps/core.xml" })
                if (!names.Contains(required)) return (false, $"{required} is missing");
            int slides = names.Count(n => n.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && n.EndsWith(".xml", StringComparison.Ordinal));
            if (slides != expectedSlides) return (false, $"{slides} slide parts for {expectedSlides} slides");
            int parts = 0;
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal) || e.FullName.EndsWith(".rels", StringComparison.Ordinal)))
            {
                try
                {
                    using var stream = entry.Open();
                    XDocument.Load(stream);
                }
                catch (System.Xml.XmlException exception) { return (false, $"{entry.FullName} is not well-formed XML: {exception.Message}"); }
                parts++;
            }
            return (true, $"{slides} slides, {parts} XML parts, {bytes.Length / 1024} KB");
        }
    }

    private static (bool, string) Preview(JsonSampleDeck? sample, Theme theme)
    {
        if (sample is null || !sample.Decks.TryGetValue(theme.Key, out var deck)) return (false, "the renderer check did not run");
        for (int i = 0; i < deck.Slides.Count; i++)
        {
            var png = PngRenderer.ToPng(deck, i, 640);
            if (png.Length < 2000 || !png.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature)) return (false, $"slide {i + 1} did not render as a PNG");
        }
        using var sheet = PngRenderer.RenderSheet(deck, 3, 320);
        return (true, $"{deck.Slides.Count} slides and a {sheet.Width} x {sheet.Height} contact sheet");
    }

    private static (bool, string) Output(JsonSampleDeck? sample)
    {
        if (sample is null || !sample.Decks.TryGetValue(Theme.Surface.Key, out var deck)) return (false, "the renderer check did not run");
        string root = Path.Combine(Path.GetTempPath(), "forge-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var brief = new Brief { Id = "self_test", Client = "Self-test" };
            var files = ForgeOutput.Write(root, brief, deck, sample.Spec, null, previews: true);
            bool ok = File.Exists(files.Pptx) && File.Exists(files.SpecJson) && File.Exists(files.BriefJson)
                && files.Previews.Count == deck.Slides.Count && files.Previews.All(File.Exists) && files.ContactSheet is not null && File.Exists(files.ContactSheet);
            return (ok, ok ? $"{Path.GetFileName(files.Pptx)}, {files.Previews.Count} previews, contact sheet" : "a file was not written");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a scanner may hold a file for a moment; the folder is in the temp directory */ }
        }
    }

    private static (bool, string) DocumentsWritable()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (documents.Length == 0 || !Directory.Exists(documents)) return (false, "this account has no Documents folder; choose another folder for decks");
        string probe = Path.Combine(documents, $".forge-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "x");
            return (true, documents);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (false, $"{documents} cannot be written to ({exception.Message.Split('\n')[0].Trim()}); choose another folder for decks");
        }
        finally
        {
            try { File.Delete(probe); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static (bool, string) Plan(DeviceDetectionService devices)
    {
        var report = devices.Detected;
        var profile = report.Profile;
        if (!report.Plans.TryGetValue(ForgeSession.Workload, out var plan)) return (false, $"the catalog has no '{ForgeSession.Workload}' workload");
        string where = $"{profile.Label}: {profile.Headline}";
        return plan.Primary is null
            ? (true, $"{where}. No catalog model fits this PC's memory budget for the Forge ({plan.Summary})")
            : (true, $"{where}. Plan: {plan.Primary.Title} on the {plan.PrimaryLane?.ToUpperInvariant()} ({plan.Primary.Class}, about {plan.Primary.RequiredGb:0.0} GB needed)");
    }

    private static (bool, string) Story(DeviceDetectionService devices)
    {
        var profile = devices.Detected.Profile;
        var plan = devices.Detected.Plans[ForgeSession.Workload];
        var model = plan.Primary;
        var facts = new ModelFacts(model?.Title ?? "a local model", plan.PrimaryLane ?? "cpu", (plan.PrimaryLane ?? "cpu").ToUpperInvariant(), "", model?.Class ?? "S", model?.ParamsB ?? 0, model?.SizeGb ?? 0, 0);
        var story = StoryBuilder.Build(profile, facts);
        if (story.Name.Length == 0 || story.Facts.Count < 2 || !story.Facts[0].StartsWith("Device: ", StringComparison.Ordinal)) return (false, "the story has no device fact");
        if (story.Facts.Concat(story.TechnicalFacts).Any(f => f.Contains("TOPS", StringComparison.OrdinalIgnoreCase) || f.Contains("Copilot+", StringComparison.OrdinalIgnoreCase)))
            return (false, "the story makes a rating claim the Forge does not measure");
        return (true, story.Facts[0]);
    }
}
