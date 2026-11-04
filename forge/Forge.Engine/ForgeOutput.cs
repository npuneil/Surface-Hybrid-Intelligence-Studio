using System.Text.Json.Nodes;
using Forge.Core.Harness;
using Forge.Core.Ir;
using Forge.Core.Pptx;
using Forge.Core.Preview;
using Forge.Core.Spec;

namespace Forge.Engine;

/// <summary>Everything one run wrote.</summary>
public sealed record DeckFiles(string Folder, string Pptx, string SpecJson, string MetricsJson, string BriefJson, string PreviewFolder, string? ContactSheet, IReadOnlyList<string> Previews)
{
    /// <summary>True when the deck had to be saved under a numbered name because the usual one was open in PowerPoint.</summary>
    public bool Renamed { get; init; }

    /// <summary>The numbers written to the metrics file, for the app to show.</summary>
    public JsonObject? Metrics { get; init; }
}

/// <summary>Where a deck goes and what goes with it. A deck that is open in PowerPoint cannot be replaced, so it is saved next to it under a
/// numbered name instead of failing a run that already took minutes.</summary>
public static class ForgeOutput
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Offline PPTX Forge");

    /// <summary><paramref name="metrics"/> is called once the deck is saved, with its final path and how long saving took, because the deck's
    /// render time includes the save and the file may have been renamed.</summary>
    public static DeckFiles Write(string root, Brief brief, DeckIr deck, JsonObject spec, Func<string, double, JsonObject>? metrics, bool previews, Action<string>? log = null)
    {
        string slug = BriefDrafter.Slug(string.IsNullOrWhiteSpace(brief.Id) ? brief.Client : brief.Id);
        if (slug.Length == 0) slug = "deck";
        string folder = Path.Combine(root, slug);
        Directory.CreateDirectory(folder);

        string stem = slug;
        string pptx = Path.Combine(folder, stem + ".pptx");
        bool renamed = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int n = 2; ; n++)
        {
            try
            {
                PptxWriter.Save(deck, pptx);
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && n <= 50)
            {
                try { File.Delete(pptx + ".tmp"); } catch (IOException) { }
                stem = $"{slug}-{n}";
                pptx = Path.Combine(folder, stem + ".pptx");
                renamed = true;
                log?.Invoke($"{Path.GetFileName(folder)}\\{slug}.pptx is open in another program, so this deck is saved as {stem}.pptx.");
            }
        }

        string specPath = Path.Combine(folder, stem + ".spec.json");
        string briefPath = Path.Combine(folder, stem + ".brief.json");
        string metricsPath = Path.Combine(folder, stem + ".metrics.json");
        double saveSeconds = clock.Elapsed.TotalSeconds;
        File.WriteAllText(specPath, SpecJson.Pretty(spec));
        File.WriteAllText(briefPath, SpecJson.Pretty(brief.ToJson()));
        JsonObject? numbers = metrics?.Invoke(pptx, saveSeconds);
        if (numbers is not null) File.WriteAllText(metricsPath, SpecJson.Pretty(numbers));

        string previewFolder = Path.Combine(folder, stem + "-preview");
        var pngs = new List<string>();
        string? sheet = null;
        if (previews)
        {
            Directory.CreateDirectory(previewFolder);
            for (int i = 0; i < deck.Slides.Count; i++)
            {
                string png = Path.Combine(previewFolder, $"slide-{i + 1:00}.png");
                PngRenderer.SavePng(deck, i, png);
                pngs.Add(png);
            }
            sheet = Path.Combine(folder, stem + "-contact-sheet.png");
            using var bitmap = PngRenderer.RenderSheet(deck);
            bitmap.Save(sheet, System.Drawing.Imaging.ImageFormat.Png);
        }
        return new DeckFiles(folder, pptx, specPath, numbers is null ? "" : metricsPath, briefPath, previewFolder, sheet, pngs) { Renamed = renamed, Metrics = numbers };
    }
}
