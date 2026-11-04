using System.Text.Json;
using Forge.Core.Harness;
using Forge.Core.Ir;
using Forge.Core.Spec;
using Forge.Engine;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using static OfflinePptxForge.Ui;

namespace OfflinePptxForge;

// Step 2: what the deck is about. A seller picks a pre-staged or hand-off brief, or types a request and the local model drafts a brief
// from it. Either way the brief is shown the way the Forge will use it, with what was detected on this PC added.
internal sealed partial class MainWindow
{
    private const int MinRequestChars = 20;
    private const int MaxInboxBriefs = 8;
    private const string OpenTag = "open";

    private readonly SelectorBar _briefTabs = Id(new SelectorBar(), "BriefModeBar");
    private readonly ComboBox _briefCombo = Id(new ComboBox { Header = "Brief", HorizontalAlignment = HorizontalAlignment.Stretch }, "BriefCombo");
    private readonly StackPanel _briefPick = new() { Spacing = 8 };
    private readonly StackPanel _briefWrite = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly StackPanel _briefSummary = Id(new StackPanel { Spacing = 8 }, "BriefSummary");
    private readonly TextBox _clientBox = Id(new TextBox { Header = "Client (optional)", PlaceholderText = "Who is the deck for?" }, "ClientBox");
    private readonly ComboBox _pitchCombo = Combo("Kind of pitch", "PitchCombo", ("Sales", "sales"), ("Technical", "technical"));
    private readonly TextBox _requestBox = Id(new TextBox
    {
        Header = "What should the deck say?", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110, MaxHeight = 220,
        PlaceholderText = "Who it is for, what they need, and the numbers that matter.",
    }, "RequestBox");
    private readonly TextBlock _requestHint = Id(Small(""), "RequestHint");
    private readonly CheckBox _provenanceCheck = Id(new CheckBox { Content = "Add a slide that says this deck was made on this PC", IsChecked = true }, "ProvenanceCheck");
    private readonly Button _draftButton = Id(Secondary("Draft the brief"), "DraftButton");

    private readonly Dictionary<string, Brief> _briefByTag = [];
    private readonly Dictionary<string, DateTime> _inboxSeen = [];
    private readonly List<(string Path, Brief Brief, DateTime Stamp)> _inbox = [];
    private readonly List<string> _openedFiles = [];
    private string _pickedTag = "";
    private bool _fillingBriefs;
    private bool _writing;
    private bool _commandLineBriefHandled;
    private Brief? _drafted;
    private string _draftedFrom = "";

    private bool PromptReady => _requestBox.Text.Trim().Length >= MinRequestChars;

    private UIElement BriefCard()
    {
        _briefTabs.Items.Add(new SelectorBarItem { Text = "Pre-staged", Tag = "pick" });
        _briefTabs.Items.Add(new SelectorBarItem { Text = "Write your own", Tag = "write" });
        _briefTabs.SelectedItem = _briefTabs.Items[0];
        _briefTabs.SelectionChanged += (sender, _) =>
        {
            _writing = sender.SelectedItem?.Tag as string == "write";
            _briefPick.Visibility = _writing ? Visibility.Collapsed : Visibility.Visible;
            _briefWrite.Visibility = _writing ? Visibility.Visible : Visibility.Collapsed;
            RefreshBriefSummary();
            UpdateBuildEnabled();
        };

        _briefPick.Children.Add(_briefCombo);
        _briefCombo.SelectionChanged += async (_, _) => await OnBriefChosenAsync();
        RefillBriefs(null);

        _requestBox.TextChanged += (_, _) => OnDraftInputsChanged();
        _clientBox.TextChanged += (_, _) => OnDraftInputsChanged();
        _pitchCombo.SelectionChanged += (_, _) => OnDraftInputsChanged();
        _provenanceCheck.Checked += (_, _) => OnDraftInputsChanged();
        _provenanceCheck.Unchecked += (_, _) => OnDraftInputsChanged();
        _draftButton.Click += async (_, _) => await DraftOnlyAsync();
        _briefWrite.Children.Add(Stack(8, _clientBox, _pitchCombo, _requestBox, _requestHint, _provenanceCheck, _draftButton));
        UpdateRequestHint();

        RefreshBriefSummary();
        return CardBorder(Stack(10,
            StepHeader(2, "Brief", "What the deck is about. Numbers come from the brief or from this PC, never from the model."),
            _briefTabs, _briefPick, _briefWrite, _briefSummary));
    }

    // ---- Which brief ----

    private static string PitchLabel(Brief brief) => brief.IsTechnical ? "technical" : "sales";

    private Brief? PickedBrief() => _briefByTag.TryGetValue(_pickedTag, out var brief) ? brief : null;

    /// <summary>The brief the next deck will use: the picked one, or the one drafted from the typed request.</summary>
    private Brief? CurrentBrief() => _writing ? _drafted : PickedBrief();

    private bool BriefReady() => _writing ? PromptReady : PickedBrief() is not null;

    private void RefillBriefs(string? select)
    {
        _fillingBriefs = true;
        try
        {
            _briefCombo.Items.Clear();
            _briefByTag.Clear();
            void Header(string text) => _briefCombo.Items.Add(new ComboBoxItem { Content = text.ToUpperInvariant(), IsEnabled = false, FontWeight = FontWeights.SemiBold, FontSize = 11 });
            void Add(string tag, Brief brief, string label)
            {
                _briefByTag[tag] = brief;
                _briefCombo.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
            }
            Header("Pre-staged, fictional clients");
            foreach (var brief in BundledBriefs.All) Add("bundled:" + brief.Id, brief, $"{brief.Client} · {PitchLabel(brief)}");
            if (_inbox.Count > 0)
            {
                Header("Handed over by another app");
                foreach (var (path, brief, stamp) in _inbox) Add("inbox:" + path, brief, $"{brief.Client} · {PitchLabel(brief)} · {stamp.ToLocalTime():t}");
            }
            if (_openedFiles.Count > 0)
            {
                Header("Opened from a file");
                foreach (string path in _openedFiles)
                    if (TryLoadBrief(path, out var brief, out _)) Add("file:" + path, brief, $"{brief.Client} · {PitchLabel(brief)} · {Path.GetFileName(path)}");
            }
            _briefCombo.Items.Add(new ComboBoxItem { Content = "Open another brief (.json)...", Tag = OpenTag });

            string pick = select is not null && _briefByTag.ContainsKey(select) ? select
                : _briefByTag.ContainsKey(_pickedTag) ? _pickedTag
                : "bundled:" + BundledBriefs.All[0].Id;
            _pickedTag = pick;
            _briefCombo.SelectedItem = _briefCombo.Items.OfType<ComboBoxItem>().First(i => i.Tag as string == pick);
        }
        finally { _fillingBriefs = false; }
    }

    private async Task OnBriefChosenAsync()
    {
        if (_fillingBriefs) return;
        string tag = (_briefCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        if (tag == OpenTag)
        {
            await OpenBriefFileAsync();
            return;
        }
        if (!_briefByTag.TryGetValue(tag, out var brief)) return;
        _pickedTag = tag;
        PreferTemplateOf(brief);
        RefreshBriefSummary();
        UpdateBuildEnabled();
    }

    /// <summary>A brief says which look suits it. The seller can still pick another one afterwards.</summary>
    private void PreferTemplateOf(Brief brief)
    {
        if (Theme.All.Any(t => t.Key.Equals(brief.TemplateHint, StringComparison.OrdinalIgnoreCase)))
            SelectTemplate(Theme.All.First(t => t.Key.Equals(brief.TemplateHint, StringComparison.OrdinalIgnoreCase)).Key);
    }

    private async Task OpenBriefFileAsync()
    {
        string before = _pickedTag;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                RefillBriefs(before);
                return;
            }
            if (TryLoadBrief(file.Path, out var brief, out string error))
            {
                if (!_openedFiles.Contains(file.Path)) _openedFiles.Add(file.Path);
                RefillBriefs("file:" + file.Path);
                PreferTemplateOf(brief);
            }
            else
            {
                RefillBriefs(before);
                Notify("That file is not a brief", $"{Path.GetFileName(file.Path)}: {error}", InfoBarSeverity.Warning);
            }
        }
        catch (Exception exception)
        {
            Program.LogError(_options, exception);
            RefillBriefs(before);
            Notify("The file picker could not open", exception.Message, InfoBarSeverity.Warning);
        }
        RefreshBriefSummary();
        UpdateBuildEnabled();
    }

    private static bool TryLoadBrief(string path, out Brief brief, out string error)
    {
        brief = new Brief();
        try
        {
            brief = Brief.Load(path);
            error = "";
            return true;
        }
        catch (Exception e) when (e is JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }
    }

    // ---- Hand-off from other apps ----

    /// <summary>Briefs other apps leave in the inbox folder. An app writes one there before it opens or focuses this one, so a seller lands
    /// on the brief that app chose; at start-up a brief that is already old is listed but not selected.</summary>
    private void RescanInbox()
    {
        LoadCommandLineBrief();
        string folder = Path.Combine(_options.DataDir, "inbox");
        (string Path, Brief Brief, DateTime Stamp)? newest = null;
        bool changed = false;
        try
        {
            if (Directory.Exists(folder))
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.json"))
                {
                    DateTime stamp = file.LastWriteTimeUtc;
                    if (_inboxSeen.TryGetValue(file.FullName, out var seen) && seen == stamp) continue;
                    _inboxSeen[file.FullName] = stamp;
                    if (!TryLoadBrief(file.FullName, out var brief, out string error))
                    {
                        AddLog($"Ignored {file.Name} in the inbox: {error}");
                        continue;
                    }
                    _inbox.RemoveAll(i => i.Path == file.FullName);
                    _inbox.Add((file.FullName, brief, stamp));
                    changed = true;
                    if (newest is null || stamp > newest.Value.Stamp) newest = (file.FullName, brief, stamp);
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AddLog("The brief inbox could not be read: " + e.Message);
            return;
        }
        if (!changed || newest is not { } latest) return;
        _inbox.Sort((a, b) => b.Stamp.CompareTo(a.Stamp));
        if (_inbox.Count > MaxInboxBriefs) _inbox.RemoveRange(MaxInboxBriefs, _inbox.Count - MaxInboxBriefs);

        bool takeIt = !_running && (_ready || DateTime.UtcNow - latest.Stamp < TimeSpan.FromMinutes(15));
        RefillBriefs(takeIt ? "inbox:" + latest.Path : null);
        if (!takeIt)
        {
            RefreshBriefSummary();
            return;
        }
        _briefTabs.SelectedItem = _briefTabs.Items[0];
        PreferTemplateOf(latest.Brief);
        ShowSection("deck");
        RefreshBriefSummary();
        UpdateBuildEnabled();
        Notify("Brief received", $"{latest.Brief.Client} ({PitchLabel(latest.Brief)}) is ready. Pick a model if you want another, then make the deck.", InfoBarSeverity.Success);
    }

    private void LoadCommandLineBrief()
    {
        if (_commandLineBriefHandled || _options.BriefFile is not { } path) return;
        _commandLineBriefHandled = true;
        string full = Path.GetFullPath(path);
        if (TryLoadBrief(full, out var brief, out string error))
        {
            _openedFiles.Add(full);
            RefillBriefs("file:" + full);
            PreferTemplateOf(brief);
            RefreshBriefSummary();
        }
        else Notify("The brief on the command line could not be opened", $"{Path.GetFileName(full)}: {error}", InfoBarSeverity.Warning);
    }

    // ---- The brief as the Forge will use it ----

    /// <summary>The story the deck would tell, from the real PC and the selected model's last measurement. Used only to show the brief.</summary>
    private DeviceStory PreviewStory()
    {
        var option = SelectedModel();
        if (option is null) return DeviceStory.Generic;
        var speed = _session.Speed.Entry(option.VariantId);
        return StoryBuilder.Build(_session.Hardware, ModelFacts.From(option),
            new StoryMeasurements(speed?.DecodeTokensPerSecond ?? 0, speed?.Guidance == GuidanceState.Verified));
    }

    private void RefreshBriefSummary()
    {
        BuildBriefSummary();
        UpdateBuildEnabled();
    }

    private void BuildBriefSummary()
    {
        _briefSummary.Children.Clear();
        var source = CurrentBrief();
        if (source is null)
        {
            _briefSummary.Children.Add(Small(_writing
                ? "Type what the deck is for. The local model turns it into a brief first, and you can read the brief before the deck is written."
                : "Pick a brief to see what the deck will cover."));
            return;
        }
        var story = PreviewStory();
        var adapted = BriefAdapter.Adapt(source, story);

        var pills = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        pills.Children.Add(Pill(source.Client, AccentTint, AccentDeep));
        pills.Children.Add(Pill(source.IsTechnical ? "Technical" : "Sales", SlateTint, Slate));
        pills.Children.Add(Pill($"{source.SlideMin} to {source.SlideMax} slides", SlateTint, Slate));
        if (source.Source == "prompt") pills.Children.Add(Pill("Drafted from your words", GreenTint, GreenInk));
        else if (!_writing && _pickedTag.StartsWith("inbox:", StringComparison.Ordinal)) pills.Children.Add(Pill("Handed over", GreenTint, GreenInk));
        _briefSummary.Children.Add(pills);

        if (_writing && _drafted is not null && DraftKey() != _draftedFrom)
            _briefSummary.Children.Add(Pill("You changed the request: the brief is drafted again when you make the deck", AmberTint, AmberInk));

        _briefSummary.Children.Add(Labelled("Audience", adapted.Audience));
        _briefSummary.Children.Add(Labelled("Goal", adapted.Objective));

        var facts = Stack(3);
        facts.Children.Add(Set(Small("From the brief"), t => t.FontWeight = FontWeights.SemiBold));
        foreach (string fact in adapted.Facts.Take(source.Facts.Count)) facts.Children.Add(Set(Small("· " + fact), t => t.IsTextSelectionEnabled = true));
        var detected = adapted.Facts.Skip(source.Facts.Count).ToList();
        facts.Children.Add(Set(Small("Detected on this PC"), t => { t.FontWeight = FontWeights.SemiBold; t.Margin = new Thickness(0, 6, 0, 0); }));
        foreach (string fact in detected) facts.Children.Add(Set(Small("· " + fact), t => t.IsTextSelectionEnabled = true));
        if (SelectedModel() is { } option && _session.Speed.Entry(option.VariantId) is null)
            facts.Children.Add(Small("The model's measured speed is added after its first deck."));
        _briefSummary.Children.Add(Id(Disclosure($"The facts the deck can use ({adapted.Facts.Count})", facts), "BriefFacts"));

        foreach (string warning in BriefAdapter.Warnings(source, story))
            _briefSummary.Children.Add(CardBorder(Small(warning, AmberInk), 10, AmberTint));
        if (source.Dropped.Count > 0)
        {
            var dropped = Stack(3);
            foreach (string note in source.Dropped) dropped.Children.Add(Set(Small("· " + note), t => t.IsTextSelectionEnabled = true));
            _briefSummary.Children.Add(Id(Disclosure($"Left out because your words did not contain them ({source.Dropped.Count})", dropped), "BriefDropped"));
        }
    }

    private static UIElement Labelled(string label, string value) =>
        Stack(1, Set(Small(label.ToUpperInvariant()), t => { t.FontWeight = FontWeights.SemiBold; t.CharacterSpacing = 60; }),
            Set(Body(value, Ink), t => t.IsTextSelectionEnabled = true));

    private static Expander Disclosure(string header, UIElement content) => new()
    {
        Header = header, Content = content, IsExpanded = false,
        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };

    // ---- Writing your own ----

    private string DraftKey() => string.Join("\u001f", _clientBox.Text.Trim(), TagOf(_pitchCombo), _provenanceCheck.IsChecked == true, _requestBox.Text.Trim());

    private void OnDraftInputsChanged()
    {
        UpdateRequestHint();
        if (_writing) RefreshBriefSummary();
        UpdateBuildEnabled();
    }

    private void UpdateRequestHint()
    {
        int length = _requestBox.Text.Trim().Length;
        _requestHint.Text = length == 0
            ? "Plain words are fine. Only numbers you type here, or this PC's own measurements, can appear in the deck."
            : length < MinRequestChars ? $"A little more, please: at least {MinRequestChars} characters ({length} so far)."
            : "Ready. The numbers you typed are the only ones the brief may keep.";
    }

    private DraftOptions DraftOptionsNow() => new(
        _clientBox.Text.Trim().Length > 0 ? _clientBox.Text.Trim() : null, TagOf(_pitchCombo), _template, _provenanceCheck.IsChecked == true);
}
