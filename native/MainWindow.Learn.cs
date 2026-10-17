using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

internal sealed partial class MainWindow
{
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush CodeBackground = Brush(0x24, 0x1F, 0x1C);
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush CodeInk = Brush(0xF3, 0xEE, 0xE7);
    private LearningState _learning = new();
    private Lesson _lesson = CodingTrack.Lessons[0];
    private readonly Dictionary<string, (Button Button, Border Mark)> _lessonButtons = [];
    private readonly ProgressBar _learnProgress = Id(new ProgressBar { Minimum = 0, Maximum = CodingTrack.Lessons.Length, Height = 8, Foreground = Accent }, "LearnProgressBar");
    private readonly TextBlock _learnProgressText = Id(Small(""), "LearnProgressText");
    private readonly Border _lessonThemePill = Id(Pill("", AccentTint, AccentDeep), "LessonTheme");
    private readonly Border _lessonDone = Id(Pill("LESSON COMPLETE", GreenTint, GreenInk), "LessonComplete");
    private readonly TextBlock _lessonTitle = Id(H2(""), "LessonTitle");
    private readonly TextBlock _lessonWhy = Id(Body("", Ink), "LessonWhy");
    private readonly TextBlock _lessonAnalogy = Id(Body("", Ink), "LessonAnalogy");
    private readonly TextBlock _lessonCode = Id(Set(Text("", 14, CodeInk), t => { t.FontFamily = Mono; t.IsTextSelectionEnabled = true; t.TextWrapping = TextWrapping.Wrap; t.LineHeight = 22; }), "LessonCode");
    private readonly StackPanel _lessonNotes = Id(Stack(10), "LessonNotes");
    private readonly TextBlock _lessonSayIt = Id(Set(Text("", 16, Ink), t => { t.LineHeight = 24; t.TextWrapping = TextWrapping.Wrap; }), "LessonSayIt");
    private readonly TextBlock _quizQuestion = Id(H3(""), "LessonQuizQuestion");
    private readonly RadioButtons _quizOptions = Id(new RadioButtons(), "LessonQuizOptions");
    private readonly Button _quizCheck = Id(Primary("Check answer"), "LessonQuizCheck");
    private readonly InfoBar _quizFeedback = Id(new InfoBar { IsClosable = false }, "LessonQuizFeedback");
    private readonly Button _lessonPrev = Id(Secondary("Previous"), "LessonPrevious");
    private readonly Button _lessonNext = Id(Secondary("Next lesson"), "LessonNext");
    private readonly Button _lessonTutor = Id(Secondary("Ask the local coding tutor"), "LessonAskTutor");
    private readonly HyperlinkButton _lessonSizing = Id(new HyperlinkButton { Content = "See this number in the Technical sizing view", Padding = new Thickness(0), Foreground = Accent }, "LessonOpenSizing");
    private readonly ToggleSwitch _learnToggle = Id(new ToggleSwitch { Header = "Show the optional 'Coding as a second language' tab", OnContent = "Shown", OffContent = "Hidden" }, "LearnTabToggle");
    private ScrollViewer? _learnScroller;
    private NavigationViewItemHeader? _learnHeader;
    private bool _syncingLearnToggle;

    private string LearningStatePath => _options.Smoke
        ? Path.Combine(_options.RuntimeDirectory, "smoke-learning.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Brand.CodeName, "learning.json");

    private void AddLearnNav()
    {
        if (_options.Smoke && File.Exists(LearningStatePath)) File.Delete(LearningStatePath);
        _learning = LearningState.Load(LearningStatePath);
        _learnHeader = new NavigationViewItemHeader { Content = "Optional" };
        _nav.MenuItems.Add(_learnHeader);
        AddNav("learn", "Coding as a 2nd language", "\uE7BE", "NavLearn");
    }

    private FrameworkElement BuildLearn()
    {
        var list = Stack(4);
        for (int i = 0; i < CodingTrack.Lessons.Length; i++)
        {
            var lesson = CodingTrack.Lessons[i];
            var mark = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), VerticalAlignment = VerticalAlignment.Center };
            var button = Id(new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 8, 10, 8),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Content = Lead(10, mark, Stack(0,
                    Set(Text(lesson.Title, 14, Ink, FontWeights.SemiBold), t => t.TextWrapping = TextWrapping.Wrap),
                    Small(lesson.Theme)))
            }, "LessonNav_" + lesson.Id);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Lesson {i + 1}: {lesson.Title}");
            button.Click += (_, _) => ShowLesson(lesson);
            _lessonButtons[lesson.Id] = (button, mark);
            list.Children.Add(button);
        }

        _quizCheck.Click += (_, _) => CheckAnswer();
        _lessonPrev.Click += (_, _) => StepLesson(-1);
        _lessonNext.Click += (_, _) => StepLesson(1);
        _lessonTutor.Click += (_, _) => AskTutor();
        _lessonSizing.Click += (_, _) => OpenSizingFromLesson();
        _learnToggle.IsOn = _learning.ShowTab;
        _learnToggle.Toggled += (_, _) => { if (!_syncingLearnToggle) SetLearnTabVisible(_learnToggle.IsOn); };
        var hide = Id(new HyperlinkButton { Content = "Hide this optional tab", Padding = new Thickness(0), Foreground = Muted }, "LearnHide");
        hide.Click += (_, _) => SetLearnTabVisible(false);
        var reset = Id(new HyperlinkButton { Content = "Reset progress", Padding = new Thickness(0), Foreground = Muted }, "LearnReset");
        reset.Click += (_, _) =>
        {
            _learning.Completed.Clear();
            SaveLearning();
            UpdateLearnProgress();
            ShowLesson(CodingTrack.Lessons[0]);
        };

        var listCard = CardBorder(Stack(10, H3("Lessons"), list), 16);
        var code = new Border { Background = CodeBackground, CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 14, 18, 14), Child = _lessonCode };
        var lessonCard = CardBorder(Stack(16,
            Row(8, _lessonThemePill, _lessonDone),
            _lessonTitle, _lessonWhy,
            CardBorder(Lead(12, Glyph("\uE82F", 18), Stack(2, Text("IN PLAIN ENGLISH", 11, Slate, FontWeights.SemiBold), _lessonAnalogy)), 14, SlateTint),
            Stack(8, Text("READ THE CODE", 11, Muted, FontWeights.SemiBold), code, Small("Displayed for learning only. This app never runs it.")),
            Stack(10, H3("Line by line"), _lessonNotes),
            CardBorder(Stack(6, Text("SAY IT TO A CUSTOMER", 11, AccentDeep, FontWeights.SemiBold), _lessonSayIt), 16, Sand),
            new Border { Height = 1, Background = Line },
            Stack(10, Text("QUICK CHECK", 11, Muted, FontWeights.SemiBold), _quizQuestion, _quizOptions, _quizCheck, _quizFeedback),
            new Border { Height = 1, Background = Line },
            Row(10, _lessonPrev, _lessonNext, _lessonTutor),
            Small("The tutor opens the Live GPU workspace with this code in the prompt; the local coding model explains it on the RTX GPU. Nothing is executed, and the explanation is a draft to check."),
            _lessonSizing), 26);
        Id(lessonCard, "LessonCard");

        var progress = CardBorder(Stack(8,
            Lead(12, Text("YOUR PROGRESS", 11, Muted, FontWeights.SemiBold), Set(Row(18, reset, hide), r => r.HorizontalAlignment = HorizontalAlignment.Right)),
            _learnProgress, _learnProgressText), 18);

        UpdateLearnProgress();
        ShowLesson(CodingTrack.Find(_learning.LastLesson) ?? CodingTrack.Lessons[0], save: false);
        _learnScroller = Page(Stack(22,
            Stack(10, Eyebrow("Optional module - technical ramp-up"), H1("Coding as a second language"), Body(CodingTrack.Promise)),
            progress,
            TwoPane(listCard, lessonCard, 300, 980)));
        return _learnScroller;
    }

    internal void ShowLesson(Lesson lesson, bool save = true)
    {
        _lesson = lesson;
        int index = Array.IndexOf(CodingTrack.Lessons, lesson);
        ((TextBlock)_lessonThemePill.Child).Text = $"LESSON {index + 1} OF {CodingTrack.Lessons.Length} - {lesson.Theme.ToUpperInvariant()}";
        _lessonTitle.Text = lesson.Title;
        _lessonWhy.Text = lesson.WhyItMatters;
        _lessonAnalogy.Text = lesson.Analogy;
        _lessonCode.Text = lesson.Code;
        _lessonSayIt.Text = "\u201C" + lesson.SayIt + "\u201D";
        _lessonNotes.Children.Clear();
        foreach (var note in lesson.Notes)
        {
            var codePill = new Border
            {
                Background = Sand, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 4),
                Child = Set(Text(note.Code, 12, Ink), t => { t.FontFamily = Mono; t.TextWrapping = TextWrapping.Wrap; }),
            };
            _lessonNotes.Children.Add(Stack(4, codePill, Set(Small(note.Meaning, Ink), t => t.TextWrapping = TextWrapping.Wrap)));
        }
        _quizQuestion.Text = lesson.Quiz.Question;
        _quizOptions.Items.Clear();
        var order = CodingTrack.DisplayOrder(lesson);
        for (int d = 0; d < order.Length; d++)
            _quizOptions.Items.Add(Id(new RadioButton { Content = lesson.Quiz.Options[order[d]], Tag = order[d] }, $"LessonQuizOption_{d}"));
        _quizOptions.SelectedIndex = -1;
        _quizFeedback.IsOpen = false;
        _lessonDone.Visibility = _learning.Completed.Contains(lesson.Id) ? Visibility.Visible : Visibility.Collapsed;
        _lessonPrev.IsEnabled = index > 0;
        _lessonNext.IsEnabled = index < CodingTrack.Lessons.Length - 1;
        _lessonSizing.Visibility = lesson.SizingLink is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateTutorButton();
        foreach (var (id, (button, _)) in _lessonButtons)
            button.Background = id == lesson.Id ? AccentTint : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _learning.LastLesson = lesson.Id;
        if (save) SaveLearning();
        _learnScroller?.ChangeView(null, 0, null, true);
    }

    private void StepLesson(int delta)
    {
        int index = Array.IndexOf(CodingTrack.Lessons, _lesson) + delta;
        if (index >= 0 && index < CodingTrack.Lessons.Length) ShowLesson(CodingTrack.Lessons[index]);
    }

    internal void OpenSizingFromLesson()
    {
        if (_lesson.SizingLink is not string s || !Enum.TryParse<TShirtSize>(s, out var size)) return;
        ResetSizing();
        _sizePicker.SelectedIndex = Array.FindIndex(Sizing.Profiles, p => p.Size == size);
        Navigate("sizing");
        ShowSizingTab(technical: true);
    }

    internal bool CheckAnswer()
    {
        if (_quizOptions.SelectedItem is not RadioButton { Tag: int original })
        {
            (_quizFeedback.Severity, _quizFeedback.Title, _quizFeedback.Message) = (InfoBarSeverity.Informational, "Pick an answer first", "");
            _quizFeedback.IsOpen = true;
            return false;
        }
        bool correct = CodingTrack.IsCorrect(_lesson, original);
        if (correct)
        {
            (_quizFeedback.Severity, _quizFeedback.Title, _quizFeedback.Message) = (InfoBarSeverity.Success, "Correct", _lesson.Quiz.Explanation);
            _learning.Completed.Add(_lesson.Id);
            SaveLearning();
            _lessonDone.Visibility = Visibility.Visible;
            UpdateLearnProgress();
        }
        else
        {
            (_quizFeedback.Severity, _quizFeedback.Title, _quizFeedback.Message) = (InfoBarSeverity.Warning, "Not quite", "Re-read the line-by-line notes and try again.");
        }
        _quizFeedback.IsOpen = true;
        return correct;
    }

    private void UpdateLearnProgress()
    {
        int done = _learning.CompletedCount;
        _learnProgress.Value = done;
        _learnProgressText.Text = done == CodingTrack.Lessons.Length
            ? $"All {done} lessons complete. You can read the code a local coding assistant drafts."
            : $"{done} of {CodingTrack.Lessons.Length} lessons complete. Answer a lesson's quick check correctly to complete it.";
        for (int i = 0; i < CodingTrack.Lessons.Length; i++)
        {
            var lesson = CodingTrack.Lessons[i];
            var (_, mark) = _lessonButtons[lesson.Id];
            bool complete = _learning.Completed.Contains(lesson.Id);
            mark.Background = complete ? GreenInk : Sand;
            mark.Child = complete
                ? Set(Glyph("\uE73E", 12, White), g => { g.HorizontalAlignment = HorizontalAlignment.Center; g.VerticalAlignment = VerticalAlignment.Center; })
                : Set(Text((i + 1).ToString(), 12, Ink, FontWeights.SemiBold), t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; });
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(_lessonButtons[lesson.Id].Button, complete ? "Complete" : "Not complete");
        }
    }

    private void UpdateTutorButton()
    {
        bool available = _scenarios.ContainsKey(CodingTrack.TutorScenario);
        _lessonTutor.IsEnabled = available;
        ToolTipService.SetToolTip(_lessonTutor, available
            ? "Opens the Live GPU workspace with this lesson's code in the prompt."
            : "The running local service does not offer the coding tutor.");
    }

    internal bool AskTutor()
    {
        if (!SelectScenario(CodingTrack.TutorScenario)) return false;
        _prompt.Text = CodingTrack.TutorPrompt(_lesson);
        Navigate("live");
        return true;
    }

    internal void SetLearnTabVisible(bool show)
    {
        _learning.ShowTab = show;
        SaveLearning();
        ApplyLearnVisibility();
    }

    private void ApplyLearnVisibility()
    {
        bool show = _learning.ShowTab;
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_navItems.TryGetValue("learn", out var item)) item.Visibility = visibility;
        if (_learnHeader is not null) _learnHeader.Visibility = visibility;
        if (_learnToggle.IsOn != show)
        {
            _syncingLearnToggle = true;
            _learnToggle.IsOn = show;
            _syncingLearnToggle = false;
        }
        if (!show && _currentSection == "learn") Navigate("playbook");
    }

    internal bool LearnTabVisible => _navItems.TryGetValue("learn", out var item) && item.Visibility == Visibility.Visible;

    private void SaveLearning()
    {
        try { _learning.Save(LearningStatePath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Program.LogError(_options, exception); }
    }
}
