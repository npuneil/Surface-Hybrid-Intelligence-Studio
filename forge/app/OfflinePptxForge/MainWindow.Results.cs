using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Forge.Core.Harness;
using Forge.Engine;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using static OfflinePptxForge.Ui;

namespace OfflinePptxForge;

// The right-hand side of the deck page: what to expect before a run, the numbers and the slides after it, and a log of what the engine did.
internal sealed partial class MainWindow
{
    private const int MaxLogLines = 400;

    private readonly TextBlock _kpiTitle = Id(H2("What to expect"), "KpiTitle");
    private readonly TextBlock _kpiLead = Id(Small(""), "KpiLead");
    private readonly StackPanel _kpiHost = Id(new StackPanel(), "KpiHost");
    private readonly Microsoft.UI.Xaml.Controls.Image _previewImage = Id(new Microsoft.UI.Xaml.Controls.Image { Stretch = Stretch.Uniform, MaxHeight = 420, HorizontalAlignment = HorizontalAlignment.Left }, "DeckPreview");
    private readonly WrapPanel _strip = Id(new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 }, "SlideStrip");
    private readonly TextBlock _previewNote = Id(Small(""), "PreviewNote");
    private readonly TextBlock _deckPath = Id(Set(Small(""), t => t.IsTextSelectionEnabled = true), "DeckPath");
    private readonly Button _openButton = Id(Primary("Open in PowerPoint"), "OpenDeckButton");
    private readonly Button _folderButton = Id(Secondary("Show in folder"), "ShowFolderButton");
    private readonly TextBlock _logText = Id(Set(Text("", 11, Muted), t => { t.FontFamily = Mono; t.IsTextSelectionEnabled = true; }), "LogText");
    private readonly ScrollViewer _logScroll = Id(new ScrollViewer { Height = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled }, "LogScroll");
    private readonly Queue<string> _logLines = new();
    private readonly List<Border> _stripChrome = [];
    private Border _resultCard = null!;
    private ForgeRun? _lastRun;
    private int _shownSlide = -1;
    private bool _logScrollQueued;

    private void BuildResultColumn()
    {
        _logScroll.Content = _logText;
        _openButton.Click += (_, _) => OpenDeck();
        _folderButton.Click += (_, _) => ShowInFolder();
        _resultCard = Id(CardBorder(Stack(12,
            Eyebrow("Your deck"),
            _previewImage,
            _previewNote,
            _strip,
            _deckPath,
            Row(10, _openButton, _folderButton))), "ResultCard");
        _resultCard.Visibility = Visibility.Collapsed;
        _rightCards.Children.Add(CardBorder(Stack(10, _kpiTitle, _kpiLead, _kpiHost)));
        _rightCards.Children.Add(_resultCard);
        _rightCards.Children.Add(CardBorder(Stack(8, Eyebrow("Activity"), CardBorder(_logScroll, 8, Paper))));
    }

    // ---- Numbers ----

    private static FrameworkElement Tile(string id, string label, string value, string note)
    {
        var body = Stack(2,
            Set(Small(label.ToUpperInvariant()), t => { t.FontWeight = FontWeights.SemiBold; t.CharacterSpacing = 60; }),
            Set(Text(value, 19, Ink, FontWeights.SemiBold), t => t.FontFamily = Display),
            note.Length > 0 ? Small(note) : Small(" "));
        return Id(CardBorder(body, 12, Paper), "Kpi_" + id);
    }

    private void FillKpis(string title, string lead, params FrameworkElement[] tiles)
    {
        _kpiTitle.Text = title;
        _kpiLead.Text = lead;
        _kpiLead.Visibility = lead.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _kpiHost.Children.Clear();
        _kpiHost.Children.Add(new AdaptiveGrid(150, 4, 10).With(tiles));
    }

    /// <summary>What the selected model is expected to do here, from its last measurement. Shown until a deck replaces it.</summary>
    private void ShowIdleKpis()
    {
        if (_lastRun is not null) return;
        var option = SelectedModel();
        if (option is null)
        {
            _kpiTitle.Text = "What to expect";
            _kpiLead.Text = "Pick a model to see what to expect.";
            _kpiLead.Visibility = Visibility.Visible;
            _kpiHost.Children.Clear();
            return;
        }
        var speed = _session.Speed.Entry(option.VariantId);
        var eta = _session.Speed.Estimate(option.VariantId, ForgeText.TypicalSlides);
        var lane = _devices.Detected.Profile.Lane(option.Lane);
        FillKpis("What to expect", "From this PC's own measurements. Nothing here is a vendor figure.",
            Tile("model", "Model", option.Title, $"{option.LaneLabel} · {option.EpLabel}"),
            Tile("speed", "Speed", speed is null ? "Not measured" : $"{speed.DecodeTokensPerSecond:0.#} tok/s", speed is null ? "The first deck measures it" : "decode, measured here"),
            Tile("eta", $"{ForgeText.TypicalSlides} slides", eta is null ? "Not known yet" : "About " + Provenance.Duration(eta.Seconds), eta is null ? "after the model is measured" : eta.Basis),
            Tile("memory", "Memory", ForgeText.Gb(option.FileGb), lane?.BudgetGb is { } budget ? $"model file · {ForgeText.Gb(budget)} budget on the {option.LaneLabel}" : "model file"));
    }

    private void ShowRunKpis(ForgeRun run)
    {
        var result = run.Result;
        var stats = result.Stats;
        string how = ForgeText.Settings(run.Settings);
        FillKpis("Your deck", how,
            Tile("grade", "Grade", result.Grade?.AutoGrade ?? "Not graded", result.Grade is { } grade ? $"{grade.AutoScore:0.#} of 100, scored by the Forge's own checks" : ""),
            Tile("slides", "Slides", run.Deck.Slides.Count.ToString(), $"{result.Repairs.Count} repair pass{(result.Repairs.Count == 1 ? "" : "es")} run"),
            Tile("time", "Drafting time", Provenance.Short(result.DraftSeconds), $"plus {Provenance.Short(run.BuildSeconds)} to build the file"),
            Tile("speed", "Speed", stats.TokensMeasured && stats.DecodeTokensPerSecond > 0 ? $"{stats.DecodeTokensPerSecond:0.#} tok/s" : "Not measured", "decode"),
            Tile("energy", "Energy", result.Energy.Measured ? Provenance.Wh(result.Energy.Wh) : "Not measured", result.Energy.Basis.Length > 0 ? result.Energy.Basis : "no power meter on this lane"),
            Tile("calls", "Model calls", Provenance.Count(stats.LlmCalls), $"{Provenance.Count(stats.CompletionTokens)} tokens written"),
            Tile("lint", "Checks left", result.LintRemaining.Count == 0 ? "None" : Provenance.Count(result.LintRemaining.Count), result.LintRemaining.Count == 0 ? "layout and wording" : "findings the repairs could not fix"));
    }

    // ---- The deck ----

    private async Task ShowResultAsync(ForgeRun run)
    {
        _lastRun = run;
        ShowRunKpis(run);
        _resultCard.Visibility = Visibility.Visible;
        _deckPath.Text = run.Files.Pptx;
        _strip.Children.Clear();
        _stripChrome.Clear();
        _shownSlide = -1;
        var previews = run.Files.Previews;
        _previewImage.Visibility = previews.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _previewNote.Text = previews.Count > 0
            ? run.Files.Renamed ? "The usual file name was open in PowerPoint, so this deck was saved under a numbered name." : ""
            : "Previews were switched off for this deck. Open it in PowerPoint to see the slides.";
        _previewNote.Visibility = _previewNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (previews.Count == 0)
        {
            _previewImage.Source = null;
            return;
        }
        for (int i = 0; i < previews.Count; i++)
        {
            int index = i;
            var thumb = new Microsoft.UI.Xaml.Controls.Image { Width = 112, Height = 63, Stretch = Stretch.UniformToFill };
            var button = new Button { Content = thumb, Padding = new Thickness(0), Background = Clear, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4) };
            Named(button, "Slide_" + (i + 1), $"Slide {i + 1}");
            button.Click += async (_, _) => await ShowSlideAsync(index);
            var chrome = new Border { BorderThickness = new Thickness(2), BorderBrush = Clear, CornerRadius = new CornerRadius(6), Child = button };
            _stripChrome.Add(chrome);
            _strip.Children.Add(chrome);
        }
        await ShowSlideAsync(0);
        _ = FillStripAsync(run);
    }

    /// <summary>Loads the small pictures after the big one is up, so the deck appears at once.</summary>
    private async Task FillStripAsync(ForgeRun run)
    {
        var previews = run.Files.Previews;
        for (int i = 0; i < previews.Count; i++)
        {
            if (_closed || !ReferenceEquals(_lastRun, run) || i >= _stripChrome.Count) return;
            try
            {
                var bytes = await File.ReadAllBytesAsync(previews[i]);
                if ((_stripChrome[i].Child as Button)?.Content is Microsoft.UI.Xaml.Controls.Image image) image.Source = await ImageFromBytesAsync(bytes, 224);
            }
            catch (Exception exception)
            {
                Program.LogError(_options, exception);
                AddLog($"The preview of slide {i + 1} could not be read: {exception.Message}");
            }
        }
    }

    private async Task ShowSlideAsync(int index)
    {
        if (_lastRun is not { } run || index < 0 || index >= run.Files.Previews.Count) return;
        _shownSlide = index;
        for (int i = 0; i < _stripChrome.Count; i++) _stripChrome[i].BorderBrush = i == index ? Accent : Clear;
        try
        {
            var bytes = await File.ReadAllBytesAsync(run.Files.Previews[index]);
            if (_shownSlide == index) _previewImage.Source = await ImageFromBytesAsync(bytes);
        }
        catch (Exception exception)
        {
            Program.LogError(_options, exception);
            _previewNote.Text = "The slide preview could not be read: " + exception.Message;
            _previewNote.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Decodes from memory so no preview file stays locked while the person has the deck open or deletes the folder.</summary>
    private static async Task<BitmapImage> ImageFromBytesAsync(byte[] bytes, int decodeWidth = 0)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var image = new BitmapImage();
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        await image.SetSourceAsync(stream);
        return image;
    }

    private void OpenDeck()
    {
        if (_lastRun is not { } run) return;
        try { Process.Start(new ProcessStartInfo(run.Files.Pptx) { UseShellExecute = true }); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Notify("The deck could not be opened", $"{exception.Message} It is saved at {run.Files.Pptx}.", InfoBarSeverity.Warning);
        }
    }

    private void ShowInFolder()
    {
        if (_lastRun is not { } run) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{run.Files.Pptx}\"") { UseShellExecute = true }); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Notify("The folder could not be opened", $"{exception.Message} The deck is in {run.Files.Folder}.", InfoBarSeverity.Warning);
        }
    }

    // ---- Activity log ----

    /// <summary>Appends a line to the activity log. UI thread only; background callers go through <see cref="Post"/>.</summary>
    private void AddLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        string text = line.Trim();
        if (text.Length > 400) text = text[..400] + "...";
        _logLines.Enqueue($"{DateTime.Now:HH:mm:ss}  {text}");
        while (_logLines.Count > MaxLogLines) _logLines.Dequeue();
        _logText.Text = string.Join('\n', _logLines);
        if (_logScrollQueued) return;
        _logScrollQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _logScrollQueued = false;
            _logScroll.ChangeView(null, _logScroll.ScrollableHeight, null, true);
        });
    }
}
