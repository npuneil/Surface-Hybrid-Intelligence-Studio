using Forge.Core.Harness;
using Forge.Core.Ir;
using Forge.Core.Layouts;
using Forge.Core.Preview;
using Forge.Core.Spec;
using Forge.Engine;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurfaceDeviceKit.FoundryLocal;
using static OfflinePptxForge.Ui;

namespace OfflinePptxForge;

// The "Make a deck" page. Wide windows get the choices on the left (with the Make button pinned under them) and the result on the right;
// narrow ones stack them in one scroller with the Make button pinned at the bottom.
internal sealed partial class MainWindow
{
    private const double WideBreakpoint = 940;

    private readonly Grid _deckPage = new() { Padding = new Thickness(20, 4, 20, 14), ColumnSpacing = 18, RowSpacing = 10 };
    private readonly StackPanel _leftCards = new() { Spacing = 14 };
    // Disabling this one control disables every choice below it while a deck is being made.
    private readonly ContentControl _choices = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top, IsTabStop = false };
    private readonly StackPanel _rightCards = new() { Spacing = 14 };
    private readonly StackPanel _narrowStack = new() { Spacing = 14 };
    private readonly ScrollViewer _leftScroll = Id(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, Padding = new Thickness(0, 0, 12, 0) }, "DeckChoices");
    private readonly ScrollViewer _rightScroll = Id(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, Padding = new Thickness(0, 0, 4, 0) }, "DeckResult");
    private readonly ScrollViewer _narrowScroll = Id(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled }, "DeckNarrow");
    private Border _actionBar = null!;
    private bool? _wide;

    // Model
    private readonly ComboBox _modelCombo = Id(new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch }, "ModelCombo");
    private readonly TextBlock _modelNote = Id(Small(""), "ModelNote");
    private readonly CheckBox _emulateCheck = Id(new CheckBox { Visibility = Visibility.Collapsed }, "EmulateModelCheck");
    private readonly TextBlock _emulateNote = Id(Small(""), "EmulateNote");
    private ResolvedModel? _emulatedModel;
    private bool _fillingModels;
    private PreparedModel? _prepared;

    // Template
    private string _template = Theme.Surface.Key;
    private readonly Dictionary<string, Border> _templateChrome = [];
    private readonly Dictionary<string, Microsoft.UI.Xaml.Controls.Image> _templateImages = [];

    // Settings
    private readonly ComboBox _modeCombo = Combo("How to write it", "ModeCombo",
        ("Automatic: the Forge picks for this model", ""), ("Slide by slide", "Guided"), ("Whole story in one reply", "Arc"), ("Whole deck in one reply", "OneShot"));
    private readonly ComboBox _repairCombo = Combo("Repair passes", "RepairCombo",
        ("Automatic", ""), ("None", "0"), ("1 pass", "1"), ("2 passes", "2"), ("3 passes", "3"));
    private readonly ComboBox _guidanceCombo = Combo("Constrained decoding", "GuidanceCombo",
        ("Automatic", ""), ("On", "on"), ("Off", "off"));
    private readonly CheckBox _previewsCheck = Id(new CheckBox { Content = "Show the slides here when the deck is done", IsChecked = true }, "PreviewsCheck");
    private readonly TextBlock _policyText = Id(Small(""), "PolicyText");

    // Action bar
    private readonly Button _buildButton = Id(Primary("Make the deck"), "BuildButton");
    private readonly Button _stopButton = Id(Secondary("Stop"), "StopButton");
    private readonly ProgressBar _progress = Id(new ProgressBar { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed }, "RunProgress");
    private readonly TextBlock _status = Id(Body("Pick a model and a brief, then make the deck."), "StatusText");
    private readonly TextBlock _elapsed = Id(Small(""), "ElapsedText");

    private static ComboBox Combo(string header, string id, params (string Text, string Tag)[] items)
    {
        var combo = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (text, tag) in items) combo.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
        combo.SelectedIndex = 0;
        return Id(combo, id);
    }

    private static string TagOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private static UIElement StepHeader(int number, string title, string? hint = null)
    {
        var badge = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Accent, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = number.ToString(), Foreground = White, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var row = Row(10, badge, Set(H3(title), t => t.VerticalAlignment = VerticalAlignment.Center));
        return hint is null ? row : Stack(3, row, Small(hint));
    }

    private FrameworkElement BuildDeckPage()
    {
        _leftCards.Children.Add(ModelCard());
        _leftCards.Children.Add(BriefCard());
        _leftCards.Children.Add(TemplateCard());
        _leftCards.Children.Add(SettingsCard());
        _choices.Content = _leftCards;
        _actionBar = ActionBar();
        BuildResultColumn();
        _deckPage.SizeChanged += (_, e) => ApplyDeckLayout(e.NewSize.Width);
        ApplyDeckLayout(1400);
        _buildButton.Click += async (_, _) => await BuildAsync();
        _stopButton.Click += (_, _) => StopRun();
        UpdateBuildEnabled();
        return Id(_deckPage, "DeckPage");
    }

    private void ApplyDeckLayout(double width)
    {
        bool wide = width >= WideBreakpoint;
        if (_wide == wide) return;
        _wide = wide;
        _deckPage.Children.Clear();
        _leftScroll.Content = null;
        _rightScroll.Content = null;
        _narrowScroll.Content = null;
        _narrowStack.Children.Clear();
        _deckPage.ColumnDefinitions.Clear();
        _deckPage.RowDefinitions.Clear();
        _deckPage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _deckPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(_actionBar, 0);
        Grid.SetRow(_actionBar, 1);
        if (wide)
        {
            _deckPage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(450) });
            _deckPage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _leftScroll.Content = _choices;
            _rightScroll.Content = _rightCards;
            Grid.SetColumn(_leftScroll, 0);
            Grid.SetRow(_leftScroll, 0);
            Grid.SetColumn(_rightScroll, 1);
            Grid.SetRow(_rightScroll, 0);
            Grid.SetRowSpan(_rightScroll, 2);
            _deckPage.Children.Add(_leftScroll);
            _deckPage.Children.Add(_rightScroll);
        }
        else
        {
            _deckPage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _narrowStack.Children.Add(_choices);
            _narrowStack.Children.Add(_rightCards);
            _narrowScroll.Content = _narrowStack;
            Grid.SetColumn(_narrowScroll, 0);
            Grid.SetRow(_narrowScroll, 0);
            _deckPage.Children.Add(_narrowScroll);
        }
        _deckPage.Children.Add(_actionBar);
    }

    // ---- 1. Model ----

    private UIElement ModelCard()
    {
        _modelCombo.SelectionChanged += (_, _) =>
        {
            if (!_fillingModels) OnModelChanged();
        };
        _emulateCheck.Checked += async (_, _) => await OnEmulateChangedAsync();
        _emulateCheck.Unchecked += async (_, _) => await OnEmulateChangedAsync();
        return CardBorder(Stack(10,
            StepHeader(1, "Model", "It runs on this PC. Nothing is sent anywhere."),
            _modelCombo, _modelNote, _emulateCheck, _emulateNote));
    }

    private IEnumerable<ResolvedModel> ModelOptions()
    {
        if (_plan is null) yield break;
        foreach (var option in _plan.Models.Options) yield return option;
        if (_emulatedModel is not null && _plan.Models.Options.All(o => o.VariantId != _emulatedModel.VariantId)) yield return _emulatedModel;
    }

    private ResolvedModel? SelectedModel()
    {
        string? variant = (_modelCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return variant is null ? null : ModelOptions().FirstOrDefault(o => o.VariantId == variant);
    }

    /// <summary>Fills the model list from this PC's live plan. A simulated device never adds models this PC cannot run.</summary>
    private void RefreshModels(string? select = null)
    {
        string? next = UpdateEmulateCheck((_modelCombo.SelectedItem as ComboBoxItem)?.Tag as string);
        string? keep = select ?? next;
        _fillingModels = true;
        try
        {
            _modelCombo.Items.Clear();
            var options = ModelOptions().ToList();
            foreach (var option in options)
            {
                string suffix = (option.VariantId == _plan?.Default?.VariantId ? " · recommended" : "") + (option.Cached ? "" : " · needs a download");
                _modelCombo.Items.Add(new ComboBoxItem { Tag = option.VariantId, Content = $"{option.Title} · {option.LaneLabel} · {option.EpLabel}{suffix}" });
            }
            var pick = options.FirstOrDefault(o => o.VariantId == keep) ?? _plan?.Default ?? options.FirstOrDefault();
            if (pick is not null)
                _modelCombo.SelectedItem = _modelCombo.Items.OfType<ComboBoxItem>().First(i => i.Tag as string == pick.VariantId);
            _modelCombo.IsEnabled = options.Count > 0;
        }
        finally { _fillingModels = false; }
        OnModelChanged();
    }

    private void OnModelChanged()
    {
        var option = SelectedModel();
        if (option is null)
        {
            _modelNote.Text = _plan is null
                ? (_engineProblem.Length > 0 ? "The local model engine is not available: " + _engineProblem : "The local model engine is starting.")
                : "Foundry Local has no model for the Forge that fits this PC.";
        }
        else
        {
            string speed = ForgeText.Speed(_session.Speed, option.VariantId, _session.Speed.Entry(option.VariantId), ForgeText.TypicalSlides);
            _modelNote.Text = $"{option.Summary}. {char.ToUpperInvariant(speed[0])}{speed[1..]}.";
        }
        UpdatePolicy();
        ShowIdleKpis();
        RefreshBriefSummary();
        UpdateBuildEnabled();
    }

    // Emulation: while another Surface is shown, the seller may load the model that Surface would use, as near as this PC can run it.
    private string? _emulatedFor;
    private bool _settingEmulate;
    // The model chosen before emulation began. Ending emulation goes back to it rather than staying on the emulated model,
    // which may be one of this PC's own models that still needs a download.
    private string? _beforeEmulation;

    /// <summary>
    /// Ends emulation and returns the model to select next: the one chosen before it began (else the recommended one),
    /// unless the seller picked another model while emulating.
    /// </summary>
    private string? EndEmulation(string? current)
    {
        string? next = _emulatedModel is not null && current == _emulatedModel.VariantId
            ? _beforeEmulation ?? _plan?.Default?.VariantId
            : current;
        _emulatedModel = null;
        _emulatedFor = null;
        _beforeEmulation = null;
        _emulateNote.Text = "";
        return next;
    }

    /// <summary>Shows the emulate box for a simulated device, ending emulation when the shown device changes. Returns the model to keep selected.</summary>
    private string? UpdateEmulateCheck(string? current)
    {
        bool simulated = _devices.IsSimulated;
        string? shown = simulated ? _devices.Effective.Profile.SimulatedId : null;
        string? next = current;
        _settingEmulate = true;
        try
        {
            if (!simulated || _emulatedFor != shown)
            {
                next = EndEmulation(current);
                _emulateCheck.IsChecked = false;
            }
            _emulateCheck.Visibility = simulated ? Visibility.Visible : Visibility.Collapsed;
            if (simulated) _emulateCheck.Content = $"Use the model {_devices.Effective.Profile.Label} would use";
            _emulateNote.Visibility = _emulateNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _settingEmulate = false; }
        return next;
    }

    private async Task OnEmulateChangedAsync()
    {
        if (_settingEmulate || _plan is null || _running) return;
        if (_emulateCheck.IsChecked != true)
        {
            string? next = EndEmulation((_modelCombo.SelectedItem as ComboBoxItem)?.Tag as string);
            _emulateNote.Visibility = Visibility.Collapsed;
            RefreshModels(next);
            return;
        }
        try
        {
            var device = _devices.Effective;
            var (model, why) = await Task.Run(() => _session.ResolveEmulationAsync(device, _lifetime.Token));
            if (model is null)
            {
                _emulateNote.Text = why;
                _emulateNote.Visibility = Visibility.Visible;
                _settingEmulate = true;
                try { _emulateCheck.IsChecked = false; }
                finally { _settingEmulate = false; }
                return;
            }
            _beforeEmulation = (_modelCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            _emulatedModel = model;
            _emulatedFor = device.Profile.SimulatedId;
            _emulateNote.Text = (why.Length > 0 ? why + " " : "") + "The deck still says it was made on this PC.";
            _emulateNote.Visibility = Visibility.Visible;
            RefreshModels(model.VariantId);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Program.LogError(_options, exception);
            _emulateNote.Text = exception.Message;
            _emulateNote.Visibility = Visibility.Visible;
        }
    }

    // ---- 3. Template ----

    private UIElement TemplateCard()
    {
        var themes = new WrapPanel { HorizontalSpacing = 10, VerticalSpacing = 10 };
        foreach (var theme in Theme.All)
        {
            var image = new Microsoft.UI.Xaml.Controls.Image { Width = 150, Height = 84, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
            var label = Text(theme.Label, 12, Ink, FontWeights.SemiBold);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var button = new Button
            {
                Content = Stack(5, new Border { CornerRadius = new CornerRadius(6), Child = image, Background = Sand }, label),
                Background = Clear, BorderThickness = new Thickness(0), Padding = new Thickness(5),
            };
            Named(button, "Template_" + theme.Key, theme.Label + " template");
            ToolTipService.SetToolTip(button, theme.Look);
            var chrome = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), BorderBrush = Clear, Child = button };
            button.Click += (_, _) => SelectTemplate(theme.Key);
            _templateChrome[theme.Key] = chrome;
            _templateImages[theme.Key] = image;
            themes.Children.Add(chrome);
        }
        SelectTemplate(_template);
        return CardBorder(Stack(10, StepHeader(3, "Look", "The deck's colours, fonts and background."), themes));
    }

    private void SelectTemplate(string key)
    {
        _template = key;
        foreach (var (name, chrome) in _templateChrome) chrome.BorderBrush = name == key ? Accent : Clear;
    }

    private async Task LoadThumbnailsAsync()
    {
        try
        {
            var rendered = await Task.Run(() =>
            {
                var spec = SelfTestDeck.Spec();
                return Theme.All.Select(t => (t.Key, Png: PngRenderer.ToPng(DeckBuilder.Build(spec, t), 0, 320))).ToList();
            });
            foreach (var (key, png) in rendered)
                if (_templateImages.TryGetValue(key, out var image)) image.Source = await ImageFromBytesAsync(png);
        }
        catch (Exception exception)
        {
            Program.LogError(_options, exception);
            AddLog("Template previews are unavailable: " + exception.Message);
        }
    }

    // ---- 4. Settings ----

    private UIElement SettingsCard()
    {
        _modeCombo.SelectionChanged += (_, _) => UpdatePolicy();
        _repairCombo.SelectionChanged += (_, _) => UpdatePolicy();
        _guidanceCombo.SelectionChanged += (_, _) => UpdatePolicy();
        var body = Stack(10, _modeCombo, _repairCombo, _guidanceCombo, _previewsCheck, _policyText);
        var expander = new Expander
        {
            Header = Stack(2, StepHeader(4, "Settings"), Small("Automatic is right for most decks.")),
            Content = body, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Card, BorderBrush = Line, CornerRadius = new CornerRadius(12),
        };
        return Id(expander, "SettingsExpander");
    }

    private RunRequest SelectedRequest() => new(
        TagOf(_modeCombo) switch { "Guided" => HarnessMode.Guided, "Arc" => HarnessMode.Arc, "OneShot" => HarnessMode.OneShot, _ => null },
        int.TryParse(TagOf(_repairCombo), out int passes) ? passes : null,
        TagOf(_guidanceCombo) switch { "on" => true, "off" => false, _ => null });

    private void UpdatePolicy()
    {
        var option = SelectedModel();
        if (option is null)
        {
            _policyText.Text = "";
            return;
        }
        var settings = RunPolicy.Decide(ModelFacts.From(option), _session.Speed.Entry(option.VariantId), SelectedRequest());
        _policyText.Text = "With these settings: " + ForgeText.Settings(settings) + "." + (settings.Why.Count > 0 ? "\n" + string.Join("\n", settings.Why) : "");
    }

    // ---- Action bar ----

    private Border ActionBar()
    {
        _stopButton.Visibility = Visibility.Collapsed;
        var buttons = Row(10, _buildButton, _stopButton, Set(_elapsed, t => t.VerticalAlignment = VerticalAlignment.Center));
        return CardBorder(Stack(8, buttons, _progress, _status), 14);
    }

    // Until a run starts, the status line says what Make the deck will do; after that it reports the run.
    private bool _statusIdle = true;

    private void SetStatus(string text)
    {
        _statusIdle = false;
        _status.Text = text;
    }

    private string IdleStatus()
    {
        if (_plan is null) return _engineProblem.Length > 0 ? "Local AI is not available, so no deck can be made. " + _engineProblem : "Starting local AI...";
        if (SelectedModel() is not { } model) return "Pick a model, then make the deck.";
        if (!BriefReady()) return _writing ? $"Write at least {MinRequestChars} characters about the deck, then make it." : "Pick a brief, then make the deck.";
        string download = model.Cached ? "" : ", which downloads first";
        return _writing
            ? $"Ready. Your words become a brief first, then the deck is made on {model.Title} on the {model.LaneLabel}{download}."
            : $"Ready. {CurrentBrief()?.Client} on {model.Title} on the {model.LaneLabel}{download}.";
    }

    private void UpdateBuildEnabled()
    {
        _buildButton.IsEnabled = !_running && _plan is not null && SelectedModel() is not null && BriefReady();
        _draftButton.IsEnabled = !_running && _plan is not null && SelectedModel() is not null && PromptReady;
        if (_statusIdle) _status.Text = IdleStatus();
    }
}
