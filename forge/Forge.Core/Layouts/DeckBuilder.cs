using System.Text.Json.Nodes;
using Forge.Core.Draw;
using Forge.Core.Ir;
using static Forge.Core.Layouts.LayoutPy;

namespace Forge.Core.Layouts;

/// <summary>A spec (the JSON the Python Forge's render() takes) to a <see cref="DeckIr"/>: the same slides in the same draw order, minus the
/// file writing. A slide that cannot be drawn is replaced by a warning statement slide and gets a "fallback" issue, so one bad slide never
/// loses the deck.</summary>
public static class DeckBuilder
{
    public static DeckIr Build(JsonObject spec, Theme theme)
    {
        var deck = new DeckIr
        {
            Theme = theme,
            Title = GD(spec, "Offline PPTX Forge deck", "title"),
            Author = "Offline PPTX Forge",
            Comments = $"Generated offline; template={theme.Key}",
        };
        if (Get(spec, "slides") is JsonArray slides)
            for (int idx = 0; idx < slides.Count; idx++)
                deck.Slides.Add(BuildSlide(spec, theme, slides[idx], idx));
        return deck;
    }

    private static SlideIr BuildSlide(JsonObject spec, Theme t, JsonNode? raw, int idx)
    {
        var s = raw as JsonObject ?? new JsonObject { ["layout"] = "statement", ["title"] = Str(raw) };
        string layout = Strip(Lower(Has(s, "layout") ? Str(Get(s, "layout")) : "statement"));
        var slide = new SlideIr { Index = idx, Layout = layout, Background = Background(t) };
        var c = new Canvas(slide, t);
        try
        {
            Draw(c, s, spec, layout, idx);
            RequireFiniteGeometry(slide);
        }
        catch (Exception exc)
        {
            Fallback(c, s, spec, idx, exc);
        }
        var notes = Find(s, "notes", "speaker_notes");
        if (Truthy(notes)) slide.Notes = Str(notes);
        return slide;
    }

    private static void Draw(Canvas c, JsonObject s, JsonObject spec, string layout, int idx)
    {
        if (layout == "title") SlideChrome.Title(c, s, spec);
        else if (layout == "section") SlideChrome.Section(c, s, idx);
        else if (LayoutRegistry.All.TryGetValue(layout, out var draw))
        {
            SlideChrome.Draw(c, s, idx, spec);
            draw(c, Get(s, "data") is JsonObject data ? data : s);
        }
        else throw new InvalidOperationException($"unknown layout '{layout}'");
    }

    /// <summary>Throws what python-pptx would: an inch value that is not a number cannot become an EMU, so that slide falls back.</summary>
    private static void RequireFiniteGeometry(SlideIr slide)
    {
        foreach (var el in slide.Elements)
        {
            double[] v = el switch
            {
                BoxEl b => [b.X, b.Y, b.W, b.H],
                LineEl l => [l.X1, l.Y1, l.X2, l.Y2],
                TextEl x => [x.X, x.Y, x.W, x.H],
                ChartEl ch => [ch.X, ch.Y, ch.W, ch.H],
                _ => [],
            };
            if (v.Any(double.IsNaN)) throw new ArithmeticException("cannot convert float NaN to integer");
            if (v.Any(double.IsInfinity)) throw new OverflowException("cannot convert float infinity to integer");
        }
    }

    /// <summary>Replaces what the slide drew with its chrome and a warning statement, and records why.</summary>
    private static void Fallback(Canvas c, JsonObject s, JsonObject spec, int idx, Exception exc)
    {
        var slide = c.Slide;
        slide.Elements.Clear();
        slide.Report.Clear();
        c.Issue("fallback", $"{exc.GetType().Name}: {exc.Message}");
        try
        {
            var head = new JsonObject
            {
                ["title"] = (Find(s, "title") ?? JsonValue.Create("(untitled)")!).DeepClone(),
                ["takeaway"] = Raw(s, "takeaway").DeepClone(),
            };
            SlideChrome.Draw(c, head, idx, spec);
            StoryLayouts.Statement(c, new JsonObject
            {
                ["icon"] = "warning",
                ["text"] = (Find(s, "takeaway", "title") ?? JsonValue.Create("")!).DeepClone(),
            });
        }
        catch (Exception inner)
        {
            // Not even the warning slide could be drawn (the Python raises here); keep the deck and say so.
            slide.Elements.Clear();
            slide.Report.Clear();
            c.Issue("fallback", $"{inner.GetType().Name}: {inner.Message}");
        }
    }

    private static SlideBackground Background(Theme t) =>
        t.Bg2.Length > 0 && t.Bg2 != t.Bg
            ? new SlideBackground(t.Bg, t.Bg2, (360 - (t.Dark ? 135 : 90)) % 360)
            : new SlideBackground(t.Bg);
}
