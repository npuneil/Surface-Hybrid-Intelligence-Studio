using System.Globalization;
using System.Text.Json.Nodes;
using Forge.Core.Ir;
using Forge.Core.Layouts;

namespace Forge.Tests;

/// <summary>Layout parity with the Python Forge, from three angles:
/// <list type="number">
/// <item>the render_summary numbers in each fixture's metrics.json (words, icons, issues exact; overflows exact; shrunk within one);</item>
/// <item>fixtures\oracle\*.shapes.json: every shape python-pptx read back from a deck the ORIGINAL Python Forge rendered, compared in
/// draw order with the C# DeckIr elements (geometry, colours, text, roles, requested sizes, icons, issues, notes, background);</item>
/// <item>malformed and unusual input, which must never crash a deck.</item>
/// </list>
/// Fitted text sizes depend on the font metrics (PIL in Python, GDI+ here), so a size that differs by more than a point is
/// printed as a "metric drift" note; everything else must match.</summary>
public static class LayoutParity
{
    private const double GeometryTol = 0.01;
    private const double PtTol = 1.0;
    private const int MaxNotesPerDeck = 12;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run()
    {
        var check = new Check("layouts");
        Registry(check);
        NumericParity(check);
        SampleSpecEveryTheme(check);
        OracleShapes(check);
        EdgeCases(check);
        return check.Finish();
    }

    // ---- registry -------------------------------------------------------------------------------------------------------------

    private static void Registry(Check check)
    {
        string[] expected = ["stack", "flow", "hub", "compare", "kpis", "analogy", "bars", "timeline", "matrix", "memory", "cta", "statement", "canvas"];
        check.That(LayoutRegistry.Names.SequenceEqual(expected), "registry names", string.Join(",", LayoutRegistry.Names));
        check.That(LayoutRegistry.All.Count == expected.Length && expected.All(LayoutRegistry.All.ContainsKey), "registry functions");
    }

    // ---- 1. numbers the Python Forge recorded in metrics.json ------------------------------------------------------------------

    private static void NumericParity(Check check)
    {
        foreach (var fx in Fixtures.Decks())
        {
            var theme = Theme.Get(fx.Template);
            var deck = DeckBuilder.Build(fx.Spec, theme);
            var want = fx.Metrics["render_summary"]!.AsArray();
            var got = deck.Summary;
            check.That(got.Count == want.Count, $"{fx.Name}: slide count", $"{got.Count} vs {want.Count}");
            int deltas = 0;
            for (int i = 0; i < Math.Min(got.Count, want.Count); i++)
            {
                var w = want[i]!.AsObject();
                var g = got[i];
                string tag = $"{fx.Name} slide {i} ({g.Layout})";
                check.That(g.Index == i && g.Layout == w["layout"]!.GetValue<string>(), $"{tag}: layout", $"{g.Layout} vs {w["layout"]}");
                check.That(g.Words == w["words"]!.GetValue<int>(), $"{tag}: words", $"{g.Words} vs {w["words"]}");
                check.That(g.Icons == w["icons"]!.GetValue<int>(), $"{tag}: icons", $"{g.Icons} vs {w["icons"]}");
                check.That(g.Overflows == w["overflows"]!.GetValue<int>(), $"{tag}: overflows", $"{g.Overflows} vs {w["overflows"]}");
                int wantShrunk = w["shrunk"]!.GetValue<int>();
                check.That(Math.Abs(g.Shrunk - wantShrunk) <= 1, $"{tag}: shrunk", $"{g.Shrunk} vs {wantShrunk}");
                if (g.Shrunk != wantShrunk) { deltas++; Console.WriteLine($"  note [layouts] {tag}: shrunk {g.Shrunk} vs {wantShrunk} (font metrics)"); }
                var wantIssues = w["issues"]!.AsArray();
                string gotKinds = string.Join(",", g.Issues.Select(x => x.Kind));
                string wantKinds = string.Join(",", wantIssues.Select(x => x!["kind"]!.GetValue<string>()));
                check.That(gotKinds == wantKinds, $"{tag}: issue kinds", $"[{gotKinds}] vs [{wantKinds}]");
                foreach (var (have, exp) in g.Issues.Zip(wantIssues))
                    check.That(have.Detail == exp!["detail"]!.GetValue<string>(), $"{tag}: issue detail", $"{have.Detail} vs {exp["detail"]}");
            }
            Console.WriteLine($"  {fx.Name}: {got.Count} slides vs metrics.json, {deltas} shrunk difference(s) within +-1");
        }
    }

    private static void SampleSpecEveryTheme(Check check)
    {
        foreach (var theme in Theme.All)
        {
            var deck = DeckBuilder.Build(Fixtures.SampleSpec(), theme);
            var fallbacks = deck.Summary.SelectMany(s => s.Issues.Where(i => i.Kind == "fallback").Select(i => $"slide {s.Index} ({s.Layout}): {i.Detail}")).ToList();
            check.That(fallbacks.Count == 0, $"sample spec on {theme.Key}: no fallback", string.Join(" | ", fallbacks));
            var layouts = deck.Slides.Select(s => s.Layout).ToHashSet();
            check.That(LayoutRegistry.Names.All(layouts.Contains) && layouts.Contains("title"), $"sample spec on {theme.Key}: every layout drawn");
        }
    }

    // ---- 2. python-pptx read-back of decks the Python Forge rendered ------------------------------------------------------------

    private static void OracleShapes(Check check)
    {
        string dir = Path.Combine(Fixtures.Root, "oracle");
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.shapes.json").Order(StringComparer.Ordinal).ToList() : [];
        var fixtures = Fixtures.Decks().ToDictionary(f => f.Name, f => f.Spec);
        check.That(files.Count >= fixtures.Count, "oracle files present", $"{files.Count} in {dir}");
        foreach (string file in files)
        {
            string name = Path.GetFileName(file)[..^".shapes.json".Length];
            var oracle = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            JsonObject spec;
            if (fixtures.TryGetValue(name, out var fixtureSpec)) spec = fixtureSpec;
            else if (name.StartsWith("sample.", StringComparison.Ordinal)) spec = Fixtures.SampleSpec();
            else spec = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, name.Split('.')[0] + "_cases.spec.json")))!.AsObject();
            CompareDeck(check, name, oracle, DeckBuilder.Build(spec, Theme.Get(oracle["template"]!.GetValue<string>())));
        }
    }

    private static void CompareDeck(Check check, string name, JsonObject oracle, DeckIr deck)
    {
        var notes = new List<string>();
        int drifts = 0, shapes = 0, sizeDiffs = 0;
        double maxSizeDiff = 0;
        void Note(string text) { if (drifts++ < MaxNotesPerDeck) notes.Add($"  note [layouts] {name}: {text}"); }

        check.That(deck.Title == oracle["title"]!.GetValue<string>(), $"{name}: deck title", $"{deck.Title} vs {oracle["title"]}");
        check.That(deck.Author == oracle["author"]!.GetValue<string>(), $"{name}: deck author");
        check.That(deck.Comments == oracle["comments"]!.GetValue<string>(), $"{name}: deck comments", $"{deck.Comments} vs {oracle["comments"]}");
        var slides = oracle["slides"]!.AsArray();
        check.That(deck.Slides.Count == slides.Count, $"{name}: slide count", $"{deck.Slides.Count} vs {slides.Count}");
        for (int i = 0; i < Math.Min(slides.Count, deck.Slides.Count); i++)
        {
            var want = slides[i]!.AsObject();
            var slide = deck.Slides[i];
            var errors = new List<string>();
            string where = $"slide {i} ({slide.Layout})";

            if (slide.Layout != want["layout"]!.GetValue<string>()) errors.Add($"layout {slide.Layout} vs {want["layout"]}");
            if (BackgroundText(slide.Background) != want["bg"]?.GetValue<string>()) errors.Add($"background {BackgroundText(slide.Background)} vs {want["bg"]}");
            if (slide.Notes != want["notes"]!.GetValue<string>()) errors.Add($"notes '{slide.Notes}' vs '{want["notes"]}'");
            CompareReport(slide, want, errors.Add, text => Note($"{where}: {text}"));

            var wantShapes = want["sh"]!.AsArray();
            if (slide.Elements.Count != wantShapes.Count)
                errors.Add($"{slide.Elements.Count} elements vs {wantShapes.Count}: {FirstDifference(slide.Elements, wantShapes)}");
            for (int j = 0; j < Math.Min(slide.Elements.Count, wantShapes.Count); j++)
            {
                shapes++;
                var exp = wantShapes[j]!.AsObject();
                var diffs = Diff(exp, Describe(slide.Elements[j]), out double sizeDelta);
                foreach (string diff in diffs)
                    errors.Add($"shape {j} {exp["k"]} {Quote(exp)}: {diff}");
                if (Math.Abs(sizeDelta) > 0.005) { sizeDiffs++; maxSizeDiff = Math.Max(maxSizeDiff, Math.Abs(sizeDelta)); }
                // python-pptx stores sizes in hundredths of a point, so a rounding difference up to 0.01 is expected and not worth a note
                if (Math.Abs(sizeDelta) > 0.011) Note($"{where} shape {j} {Quote(exp)}: fitted size differs by {sizeDelta:+0.##;-0.##} pt{(Math.Abs(sizeDelta) > PtTol ? " (beyond the 1 pt tolerance)" : "")}");
            }
            check.That(errors.Count == 0, $"{name} {where}: matches the Python deck",
                string.Join(" | ", errors.Take(6)) + (errors.Count > 6 ? $" | (+{errors.Count - 6} more)" : ""));
        }
        foreach (string n in notes) Console.WriteLine(n);
        if (drifts > MaxNotesPerDeck) Console.WriteLine($"  note [layouts] {name}: ... and {drifts - MaxNotesPerDeck} more");
        Console.WriteLine($"  oracle {name}: {slides.Count} slides, {shapes} shapes compared; {sizeDiffs} fitted size(s) differ (max {maxSizeDiff:0.##} pt); {drifts} metric drift note(s)");
    }

    private static string Quote(JsonObject shape)
    {
        string? t = shape["t"]?.GetValue<string>();
        if (t is null || shape["k"]!.GetValue<string>() == "icon") return "";
        t = t.Replace("\n", "\\n");
        return $"\"{(t.Length > 28 ? t[..28] + "..." : t)}\"";
    }

    private static string FirstDifference(IReadOnlyList<Element> have, JsonArray want)
    {
        for (int j = 0; j < Math.Min(have.Count, want.Count); j++)
        {
            var exp = want[j]!.AsObject();
            var act = Describe(have[j]);
            if (exp["k"]!.GetValue<string>() != act["k"]!.GetValue<string>() || Diff(exp, act, out _).Count > 0)
                return $"first difference at shape {j}: expected {exp["k"]} {Quote(exp)}, got {act["k"]} {Quote(act)}";
        }
        return have.Count > want.Count ? $"extra {Describe(have[want.Count])["k"]}" : $"missing {want[have.Count]!["k"]}";
    }

    private static string? BackgroundText(SlideBackground? bg) => bg switch
    {
        null => null,
        { To: null } => "s:" + bg.From,
        _ => $"g:{bg.From}>{bg.To}@{bg.AngleDeg.ToString("0.###", Inv)}",
    };

    /// <summary>The texts (role, requested size, fitted size), icons and issues the Python Forge recorded for the slide.</summary>
    private static void CompareReport(SlideIr slide, JsonObject want, Action<string> err, Action<string> drift)
    {
        var tx = want["tx"]!.AsArray();
        var texts = slide.Report.Texts;
        if (texts.Count != tx.Count) err($"{texts.Count} text records vs {tx.Count}");
        for (int k = 0; k < Math.Min(texts.Count, tx.Count); k++)
        {
            var w = tx[k]!.AsArray();
            var t = texts[k];
            if (t.Role != w[0]!.GetValue<string>()) err($"text {k} role {t.Role} vs {w[0]}");
            if (t.Words != w[1]!.GetValue<int>()) err($"text {k} words {t.Words} vs {w[1]}");
            if (Math.Abs(t.MaxPt - w[3]!.GetValue<double>()) > 0.001) err($"text {k} requested size {t.MaxPt} vs {w[3]}");
            if (t.Overflow != (w[4]!.GetValue<int>() == 1)) drift($"text {k} overflow {t.Overflow} vs {w[4]}");
        }
        // Python records the raw value; a falsy non-string (0, [], {}, null) is a valid icon name there and means the default glyph.
        var wantIcons = want["ic"]!.AsArray()
            .Select(n => n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : "")
            .ToList();
        if (!slide.Report.Icons.SequenceEqual(wantIcons)) err($"icons [{string.Join(",", slide.Report.Icons)}] vs [{string.Join(",", wantIcons)}]");

        var wantIssues = want["sum"]!["issues"]!.AsArray();
        var issues = slide.Report.Issues;
        if (issues.Count != wantIssues.Count || issues.Zip(wantIssues).Any(p => p.First.Kind != p.Second![0]!.GetValue<string>()))
            err($"issues [{string.Join(",", issues.Select(i => i.Kind))}] vs [{string.Join(",", wantIssues.Select(i => i![0]))}]");
        else
            foreach (var (have, exp) in issues.Zip(wantIssues))
            {
                string wantDetail = exp![1]!.GetValue<string>();
                // A fallback detail starts with the exception class, which is Python's in the oracle and .NET's here; only the
                // message of the unknown-layout ValueError is portable.
                bool same = have.Kind != "fallback" ? have.Detail == wantDetail
                    : !wantDetail.StartsWith("ValueError: ", StringComparison.Ordinal) || have.Detail.EndsWith(wantDetail["ValueError: ".Length..], StringComparison.Ordinal);
                if (!same) err($"issue {have.Kind} detail '{have.Detail}' vs '{wantDetail}'");
            }

        var sum = want["sum"]!.AsObject();
        var summary = slide.Report.Summarize(slide.Index, slide.Layout);
        if (summary.Words != sum["words"]!.GetValue<int>()) err($"words {summary.Words} vs {sum["words"]}");
        if (summary.Icons != sum["icons"]!.GetValue<int>()) err($"icon count {summary.Icons} vs {sum["icons"]}");
        if (summary.Overflows != sum["over"]!.GetValue<int>()) drift($"overflows {summary.Overflows} vs {sum["over"]}");
        if (Math.Abs(summary.Shrunk - sum["shr"]!.GetValue<int>()) > 1) drift($"shrunk {summary.Shrunk} vs {sum["shr"]}");
    }

    private static string KindName(ShapeKind k) => k switch
    {
        ShapeKind.Rect => "rect", ShapeKind.Rounded => "rounded", ShapeKind.Ellipse => "ellipse", ShapeKind.Chevron => "chevron",
        ShapeKind.Pentagon => "pentagon", ShapeKind.Hexagon => "hexagon", ShapeKind.Triangle => "triangle", ShapeKind.Arrow => "arrow",
        ShapeKind.Donut => "donut", ShapeKind.Trapezoid => "trapezoid", ShapeKind.Diamond => "diamond", ShapeKind.BlockArc => "arc",
        _ => k.ToString(),
    };

    /// <summary>One C# element in the compact schema oracle_dump.py wrote for the python-pptx shapes.</summary>
    private static JsonObject Describe(Element element)
    {
        var o = new JsonObject();
        switch (element)
        {
            case BoxEl b:
                o["k"] = KindName(b.Kind);
                Frame(o, b.X, b.Y, b.W, b.H);
                if (b.Gradient is { } g) o["g"] = $"{g.From}>{g.To}@{g.AngleDeg.ToString("0.###", Inv)}";
                else if (b.Fill is not null) o["f"] = b.Fill;
                if (b.AlphaPct is { } a) o["a"] = a;
                if (b.Line is not null)
                {
                    o["l"] = b.Line;
                    o["lw"] = b.LineWidthPt;
                    if (b.Dash) o["d"] = 1;
                }
                if (b.Glow is { } gl) o["gl"] = $"{gl.Color},{gl.RadiusPt.ToString("0.###", Inv)},{gl.AlphaPct.ToString("0.###", Inv)}";
                if (b.Shadow) o["sh"] = 1;
                if (b.Kind == ShapeKind.Rounded) o["adj"] = b.CornerAdj;
                if (b.RotationDeg != 0) o["rot"] = b.RotationDeg;
                break;
            case LineEl l:
                o["k"] = "line";
                o["x"] = l.X1; o["y"] = l.Y1; o["x2"] = l.X2; o["y2"] = l.Y2;
                o["l"] = l.Color;
                o["lw"] = l.WidthPt;
                if (l.Dash) o["d"] = 1;
                if (l.AlphaPct is { } la) o["a"] = la;
                if (l.Arrow) o["ar"] = 1;
                break;
            case TextEl t:
                o["k"] = t.IsIcon ? "icon" : "text";
                Frame(o, t.X, t.Y, t.W, t.H);
                o["t"] = t.Text;
                o["pt"] = t.Pt;
                o["fn"] = t.Font;
                o["c"] = t.Color;
                if (t.Italic) o["i"] = 1;
                if (t.LineSpacing is { } ls) o["ls"] = ls;
                o["al"] = t.Align switch { HAlign.Center => "c", HAlign.Right => "r", _ => "l" };
                o["an"] = t.Anchor switch { VAnchor.Middle => "m", VAnchor.Bottom => "b", _ => "t" };
                if (!t.WordWrap) o["nw"] = 1;
                break;
            case ChartEl c:
                o["k"] = "chart";
                Frame(o, c.X, c.Y, c.W, c.H);
                o["cats"] = new JsonArray(c.Categories.Select(s => (JsonNode?)s).ToArray());
                o["vals"] = new JsonArray(c.Values.Select(v => (JsonNode?)v).ToArray());
                o["fmt"] = c.NumberFormat;
                o["lpt"] = c.LabelPt;
                o["fn"] = c.Font;
                o["tc"] = c.TextColor;
                o["bc"] = c.BarColor;
                if (c.AxisMax > 0) o["max"] = c.AxisMax;
                o["hi"] = new JsonArray(c.Highlighted.Select(v => (JsonNode?)v).ToArray());
                if (c.Highlighted.Count > 0) o["hc"] = c.HighlightColor;
                break;
            default:
                throw new InvalidOperationException($"unexpected element {element.GetType().Name}");
        }
        return o;

        static void Frame(JsonObject o, double x, double y, double w, double h) { o["x"] = x; o["y"] = y; o["w"] = w; o["h"] = h; }
    }

    private static readonly HashSet<string> Geometry = ["x", "y", "w", "h", "x2", "y2"];
    private static readonly HashSet<string> Colours = ["f", "l", "c", "tc", "bc", "hc"];
    private static readonly HashSet<string> Flags = ["sh", "d", "ar", "i", "nw"];
    // Chart settings the IR does not carry (the writer chooses them); they are in the dump for the writer's author.
    private static readonly HashSet<string> WriterOnly = ["gap", "cpt", "axpt", "rev", "bold", "min"];

    /// <summary>Mismatches between a python-pptx shape (expected) and a C# element (actual). The fitted text size is not a mismatch:
    /// it depends on the font metrics, so its difference is returned in <paramref name="sizeDelta"/> (C# minus Python) for the caller to report.</summary>
    private static List<string> Diff(JsonObject exp, JsonObject act, out double sizeDelta)
    {
        var errors = new List<string>();
        sizeDelta = 0;
        foreach (string key in exp.Select(p => p.Key).Union(act.Select(p => p.Key)))
        {
            if (WriterOnly.Contains(key)) continue;
            var e = exp[key];
            var a = act[key];
            if (e is null && a is null) continue;
            if (e is null || a is null) { errors.Add($"{key}: expected {Show(e)}, got {Show(a)}"); continue; }
            bool same;
            switch (key)
            {
                case "pt":
                    sizeDelta = Num(a) - Num(e);
                    continue;
                case "g": same = SameGradient(e.GetValue<string>(), a.GetValue<string>()); break;
                case "gl": same = SameGlow(e.GetValue<string>(), a.GetValue<string>()); break;
                case "vals": same = SameNumbers((JsonArray)e, (JsonArray)a); break;
                case var _ when e is JsonArray: same = Show(e) == Show(a); break;
                case var _ when Geometry.Contains(key): same = Math.Abs(Num(e) - Num(a)) <= GeometryTol; break;
                case var _ when Flags.Contains(key): same = (Num(e) != 0) == (Num(a) != 0); break;
                case var _ when Colours.Contains(key): same = string.Equals(e.GetValue<string>(), a.GetValue<string>(), StringComparison.OrdinalIgnoreCase); break;
                case var _ when e is JsonValue ev && ev.TryGetValue<string>(out _): same = e.GetValue<string>() == a.GetValue<string>(); break;
                default: same = Math.Abs(Num(e) - Num(a)) <= (key is "adj" or "ls" ? 0.001 : 0.01); break;
            }
            if (!same) errors.Add($"{key}: {Show(a)} vs {Show(e)}");
        }
        return errors;
    }

    private static string Show(JsonNode? n) => n is null ? "none" : n.ToJsonString();

    // Nodes parsed from the oracle wrap a JsonElement and nodes built here wrap an int or a double; the JSON text works for both.
    private static double Num(JsonNode n) => double.Parse(n.ToJsonString(), Inv);

    private static bool SameNumbers(JsonArray e, JsonArray a) =>
        e.Count == a.Count && e.Zip(a).All(p => Math.Abs(Num(p.First!) - Num(p.Second!)) <= 0.001 + 1e-9 * Math.Abs(Num(p.First!)));

    private static bool SameGradient(string e, string a)
    {
        var (ec, ea) = SplitAngle(e);
        var (ac, aa) = SplitAngle(a);
        return string.Equals(ec, ac, StringComparison.OrdinalIgnoreCase) && Math.Abs(ea - aa) <= 0.01;

        static (string, double) SplitAngle(string s) { int at = s.LastIndexOf('@'); return (s[..at], double.Parse(s[(at + 1)..], CultureInfo.InvariantCulture)); }
    }

    private static bool SameGlow(string e, string a)
    {
        string[] x = e.Split(','), y = a.Split(',');
        return x.Length == 3 && y.Length == 3 && string.Equals(x[0], y[0], StringComparison.OrdinalIgnoreCase)
            && Math.Abs(double.Parse(x[1], Inv) - double.Parse(y[1], Inv)) <= 0.01 && Math.Abs(double.Parse(x[2], Inv) - double.Parse(y[2], Inv)) <= 0.01;
    }

    // ---- 3. unusual and malformed input ---------------------------------------------------------------------------------------

    private static DeckIr Build(string json, Theme? theme = null) => DeckBuilder.Build(JsonNode.Parse(json)!.AsObject(), theme ?? Theme.Surface);

    private static void EdgeCases(Check check)
    {
        // section slide: drawn by the deck builder, not the registry
        var section = Build("""{"slides":[{"layout":" SECTION ","title":"Part two","takeaway":"Next"}]}""");
        var sectionSlide = section.Slides.Single();
        check.That(sectionSlide.Layout == "section" && sectionSlide.Report.Issues.Count == 0 && sectionSlide.Elements.Count > 3, "section slide drawn without issues");
        check.That(sectionSlide.Report.Texts.Any(t => t.Text == "00" && t.Role == SlideReport.OrnamentRole), "section slide numbers from the zero-based index");

        // unknown layout: the per-slide fallback keeps the deck whole
        var unknown = Build("""{"slides":[{"layout":"Zzz","title":"Hello","takeaway":"Still readable"},{"layout":"statement","title":"After","text":"ok"}]}""");
        check.That(unknown.Slides.Count == 2, "unknown layout: deck keeps both slides");
        var fb = unknown.Slides[0].Report.Issues;
        check.That(fb.Count == 1 && fb[0].Kind == "fallback" && fb[0].Detail.EndsWith("unknown layout 'zzz'", StringComparison.Ordinal), "unknown layout: fallback issue", string.Join(";", fb));
        check.That(unknown.Slides[0].Layout == "zzz" && unknown.Slides[0].Report.Icons.SequenceEqual(["warning"]), "unknown layout: warning statement drawn");
        check.That(unknown.Slides[0].Report.Texts.Any(t => t.Text == "Still readable") && unknown.Slides[1].Report.Issues.Count == 0, "unknown layout: takeaway shown, next slide unaffected");

        // data a layout cannot use must not crash the deck: it draws something sensible or goes through the fallback
        string[] malformed =
        [
            """{"layout":"kpis","title":"t","data":{"items":"abc"}}""",
            """{"layout":"bars","title":"t"}""",
            """{"layout":"stack","title":"t"}""",
            """{"layout":"compare","title":"t","data":{"left":"text","right":{"title":"R"}}}""",
            """{"layout":"canvas","title":"t","data":{"elements":[{"type":"box"},5]}}""",
            """{"layout":"memory","title":"t","data":{"capacity":0,"blocks":[{"label":"A","size":1}]}}""",
            """{"layout":"matrix","title":"t","data":{"x_axis":5}}""",
            """{"layout":"hub","title":"t","data":{"center":["x"],"spokes":7}}""",
            """{"layout":"timeline","title":"t","data":{"milestones":"later"}}""",
            """{"layout":"flow","title":"t","steps":null}""",
            """{"layout":"analogy","title":"t","data":[1,2]}""",
            """{"layout":"cta","title":"t","data":{"steps":{"a":1}}}""",
            """{"layout":"statement","title":"t","data":null,"text":["x",{"y":1}]}""",
            """{"layout":"title","title":"t","hero_icons":5}""",
        ];
        foreach (string slideJson in malformed)
        {
            try
            {
                var s = Build($$"""{"slides":[{{slideJson}}]}""").Slides.Single();
                var kinds = s.Report.Issues.Select(i => i.Kind).ToList();
                check.That(s.Elements.Count > 0 && (kinds.Count == 0 || kinds.SequenceEqual(["fallback"])), $"malformed input survives: {slideJson}", string.Join(",", kinds));
            }
            catch (Exception ex) { check.That(false, $"malformed input survives: {slideJson}", ex.GetType().Name + ": " + ex.Message); }
        }

        // slides that are not objects, and missing pieces
        var plain = Build("""{"slides":["Just a string",42,null,["a"]]}""");
        check.That(plain.Slides.Count == 4 && plain.Slides.All(s => s.Layout == "statement" && s.Report.Issues.Count == 0), "non-object slides become statements");
        check.That(plain.Slides[0].Report.Texts.Any(t => t.Role == "title" && t.Text == "Just a string"), "a string slide becomes the title");
        check.That(Build("""{}""").Slides.Count == 0 && Build("""{"slides":null}""").Slides.Count == 0, "no slides, no crash");
        var defaults = Build("""{"slides":[{"title":"x"}]}""");
        check.That(defaults.Title == "Offline PPTX Forge deck" && defaults.Comments == "Generated offline; template=surface" && defaults.Slides[0].Layout == "statement", "deck defaults");

        // notes, footer and background follow chrome() and _background()
        var noted = Build("""{"client":"Contoso","slides":[{"title":"a","notes":"First","speaker_notes":"Second"},{"title":"b","speaker_notes":"Alt"},{"title":"c","notes":0}]}""");
        check.That(noted.Slides.Select(s => s.Notes).SequenceEqual(["First", "Alt", ""]), "speaker notes", string.Join("|", noted.Slides.Select(s => s.Notes)));
        check.That(noted.Slides[0].Report.Texts.Any(t => t.Role == SlideReport.OrnamentRole && t.Text == Theme.Surface.Footer + "  |  Contoso"), "footer carries the client");
        var branded = Build("""{"client":"Fabrikam","slides":[{"title":"a"}]}""", Theme.Surface with { Footer = "Custom" });
        check.That(branded.Slides[0].Report.Texts.Any(t => t.Text == "Custom  |  Fabrikam"), "footer follows the theme passed in");
        check.That(BackgroundText(sectionSlide.Background) == "g:F7F5F2>ECE8E2@270", "light background gradient angle", BackgroundText(sectionSlide.Background) ?? "none");
        check.That(BackgroundText(Build("""{"slides":[{"title":"a"}]}""", Theme.SurfaceUltra).Slides[0].Background) == "g:07080C>121521@225", "dark background gradient angle");
        check.That(BackgroundText(Build("""{"slides":[{"title":"a"}]}""", Theme.Microsoft).Slides[0].Background) == "s:FFFFFF", "solid background when bg2 equals bg");

        // the bars layout draws exactly one editable chart
        var bars = Build("""{"slides":[{"layout":"bars","title":"b","data":{"bars":[{"label":"A","value":2},{"label":"B","value":5.5}],"unit":"ms","highlight":"B"}}]}""").Slides[0];
        var chart = bars.Elements.OfType<ChartEl>().SingleOrDefault();
        check.That(chart is not null && chart.Categories.SequenceEqual(["A", "B"]) && chart.Values.SequenceEqual([2d, 5.5]) && chart.Highlighted.SequenceEqual([1]), "bars chart data");
        check.That(chart is not null && chart.NumberFormat == "#,##0.0\" ms\"" && Math.Abs(chart.AxisMax - 7.15) < 1e-9, "bars chart number format and axis", chart?.NumberFormat ?? "");
    }
}
