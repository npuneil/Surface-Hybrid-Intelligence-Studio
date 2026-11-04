using System.Runtime.InteropServices;
using Forge.Engine;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurfaceDeviceKit;
using static OfflinePptxForge.Ui;

namespace OfflinePptxForge;

/// <summary>The window: a title bar that names the real PC, and two pages. "Make a deck" drives the Forge engine; "This PC" shows what the
/// device detection service found. The title bar always names the real PC, even while another Surface is shown on the second page.</summary>
internal sealed partial class MainWindow : Window
{
    private readonly AppOptions _options;
    private readonly DeviceDetectionService _devices = DeviceKit.Service;
    private readonly ForgeSession _session;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Grid _root = new();
    private readonly InfoBar _simulationBar = Id(new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false, Margin = new Thickness(16, 0, 16, 8) }, "SimulationBanner");
    private readonly InfoBar _packageBar = Id(new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = true, IsOpen = false, Margin = new Thickness(16, 0, 16, 8) }, "PackageBanner");
    private readonly InfoBar _notice = Id(new InfoBar { IsClosable = true, IsOpen = false, Margin = new Thickness(16, 0, 16, 8) }, "NoticeBar");
    private readonly TextBlock _chipText = Id(Text("Detecting this PC...", 12, Muted, FontWeights.SemiBold), "TitleDeviceChip");
    private readonly TextBlock _engineText = Id(Text("Starting local AI", 12, Muted, FontWeights.SemiBold), "TitleEngineState");
    private readonly Border _enginePill = new() { CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3, 10, 4), Background = Sand, VerticalAlignment = VerticalAlignment.Center };
    private readonly SelectorBar _sections = Id(new SelectorBar { Margin = new Thickness(20, 0, 20, 4) }, "SectionBar");
    private readonly Dictionary<string, FrameworkElement> _pages = [];
    private SessionPlan? _plan;
    private string _engineProblem = "";
    private string _section = "deck";
    private bool _switching;
    private bool _closed;
    private bool _ready;
    private bool _packageChecked;

    internal MainWindow(AppOptions options)
    {
        _options = options;
        _session = new ForgeSession(new SessionOptions
        {
            Devices = _devices,
            DataDirectory = options.DataDirectory,
            AppDataDirectory = options.AppDataDirectory,
            OutputRoot = options.OutputRoot,
            Log = line => Post(() => AddLog(line)),
        });
        Title = Brand.Name;
        ConfigureWindow();
        Content = BuildShell();
        _root.Loaded += async (_, _) => await InitializeAsync();
        AppWindow.Closing += (_, _) => Shutdown();
        Closed += (_, _) => Shutdown();
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated && _ready) RescanInbox();
        };
    }

    private void ConfigureWindow()
    {
        string icon = Brand.Asset("AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 0x1F, 0x1A, 0x17);
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 0xF3, 0xEE, 0xE7);
        // AppWindow sizes are physical pixels; the options and the minimum are in DIPs, so scale them and keep the window inside the work area.
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        if (scale <= 0) scale = 1;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = _options.WindowWidth > 0 ? Math.Min((int)(_options.WindowWidth * scale), work.Width) : (int)(work.Width * 0.92);
        int height = _options.WindowHeight > 0 ? Math.Min((int)(_options.WindowHeight * scale), work.Height) : (int)(work.Height * 0.92);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(560 * scale);
            presenter.PreferredMinimumHeight = (int)(560 * scale);
            if (!_options.Windowed) presenter.Maximize();
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private Grid BuildShell()
    {
        _root.Background = Paper;
        _root.RequestedTheme = ElementTheme.Light;
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new Grid { Padding = new Thickness(16, 0, 150, 0), Background = Paper };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = Row(10, Glyph("\uE8A5", 18), Text(Brand.Name, 13, Ink, FontWeights.SemiBold));
        brand.VerticalAlignment = VerticalAlignment.Center;
        titleBar.Children.Add(brand);
        _chipText.TextWrapping = TextWrapping.NoWrap;
        _chipText.TextTrimming = TextTrimming.CharacterEllipsis;
        var chip = new Border
        {
            Background = Sand, CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(14, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Child = _chipText,
        };
        Grid.SetColumn(chip, 1);
        titleBar.Children.Add(chip);
        _engineText.TextWrapping = TextWrapping.NoWrap;
        _enginePill.Child = _engineText;
        Grid.SetColumn(_enginePill, 2);
        titleBar.Children.Add(_enginePill);
        _root.Children.Add(titleBar);
        SetTitleBar(titleBar);

        var banners = Stack(0, _packageBar, _simulationBar, _notice);
        Grid.SetRow(banners, 1);
        _root.Children.Add(banners);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(content, 2);
        _root.Children.Add(content);

        _sections.Items.Add(new SelectorBarItem { Text = "Make a deck", Tag = "deck", Icon = new FontIcon { Glyph = "\uE8A5" } });
        _sections.Items.Add(new SelectorBarItem { Text = "This PC", Tag = "pc", Icon = new FontIcon { Glyph = "\uE7F8" } });
        _sections.SelectedItem = _sections.Items[0];
        _sections.SelectionChanged += (sender, _) =>
        {
            if (!_switching && sender.SelectedItem?.Tag is string key) ShowSection(key);
        };
        content.Children.Add(_sections);

        var pages = new Grid();
        Grid.SetRow(pages, 1);
        _pages["deck"] = BuildDeckPage();
        _pages["pc"] = BuildDevicePage();
        foreach (var (key, page) in _pages)
        {
            page.Visibility = key == _section ? Visibility.Visible : Visibility.Collapsed;
            pages.Children.Add(page);
        }
        content.Children.Add(pages);
        return _root;
    }

    private void ShowSection(string key)
    {
        if (!_pages.ContainsKey(key)) return;
        _section = key;
        foreach (var (name, page) in _pages) page.Visibility = name == key ? Visibility.Visible : Visibility.Collapsed;
        _switching = true;
        try
        {
            foreach (var item in _sections.Items)
                if (item.Tag as string == key && !ReferenceEquals(_sections.SelectedItem, item)) _sections.SelectedItem = item;
        }
        finally { _switching = false; }
    }

    // ---- Start-up ----

    private async Task InitializeAsync()
    {
        try
        {
            string? unknown = null;
            await Task.Run(() =>
            {
                if (_options.Simulate is { } id)
                {
                    if (SimulatedProfiles.Find(id) is not null) _devices.Simulate(id);
                    else unknown = id;
                }
                _ = _devices.Detected;
            });
            _devices.Changed += (_, _) => Post(OnDevicesChanged);
            OnDevicesChanged();
            if (unknown is not null) Notify("Unknown simulated device", $"There is no simulated device '{unknown}', so this PC is shown. See the list on the This PC page.", InfoBarSeverity.Warning);
            if (_options.Section is "pc" or "deck") ShowSection(_options.Section);
            RescanInbox();
            _ready = true;
            _ = LoadThumbnailsAsync();
            await StartEngineAsync(_options.AllowDownload);
            if (_options.Smoke) await RunSmokeAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Program.LogError(_options, exception);
            ShowFault(exception);
            if (_options.Smoke)
            {
                Program.ExitCode = 1;
                Close();
            }
        }
    }

    private async Task StartEngineAsync(bool allowDownload)
    {
        SetEngine("Starting local AI", Sand, Muted);
        _engineProblem = "";
        try
        {
            await Task.Run(() => _session.StartAsync(allowDownload, line => Post(() => AddLog(line)), _lifetime.Token));
            _plan = await Task.Run(() => _session.PlanAsync(_lifetime.Token));
            SetEngine("Local AI ready", GreenTint, GreenInk);
            ClearNotice();
            OfferMissingRuntimes(allowDownload);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception exception)
        {
            _plan = null;
            _engineProblem = exception.Message;
            Program.LogError(_options, exception);
            SetEngine("Local AI unavailable", RedTint, RedInk);
            Notify("Local AI could not start", exception.Message + " The This PC page still works; making a deck needs the local model engine.", InfoBarSeverity.Warning);
        }
        RefreshModels();
        RebuildDevice();
    }

    /// <summary>A lane the detection says is usable but that has no model yet usually needs its execution provider downloaded once.</summary>
    private void OfferMissingRuntimes(bool allowedDownload)
    {
        if (allowedDownload || _plan is null) return;
        var covered = _plan.Models.Options.Select(o => o.Lane).ToHashSet();
        var missing = _devices.Detected.Profile.UsableLanes.Where(l => !covered.Contains(l.Id)).Select(l => l.Id.ToUpperInvariant()).ToList();
        if (missing.Count == 0) return;
        string lanes = string.Join(" and ", missing);
        var button = new Button { Content = "Get the " + lanes + " runtime" };
        Id(button, "DownloadRuntimeButton");
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            await StartEngineAsync(true);
        };
        Notify($"The {lanes} on this PC is not set up for models yet",
            "Windows reports it, but its execution provider is not installed. Fetching it needs the internet once; after that the Forge works offline.",
            InfoBarSeverity.Informational, button);
    }

    // ---- Status, notices and faults ----

    private void SetEngine(string text, Microsoft.UI.Xaml.Media.Brush back, Microsoft.UI.Xaml.Media.Brush fore)
    {
        _engineText.Text = text;
        _engineText.Foreground = fore;
        _enginePill.Background = back;
    }

    private void Notify(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational, Button? action = null)
    {
        _notice.Severity = severity;
        _notice.Title = title;
        _notice.Message = message;
        _notice.ActionButton = action;
        _notice.IsOpen = true;
    }

    private void ClearNotice()
    {
        _notice.IsOpen = false;
        _notice.ActionButton = null;
    }

    internal void ShowFault(Exception exception)
    {
        void Show() => Notify("Something went wrong", exception.Message + "  Details are in " + Path.Combine(_options.DataDir, "app-errors.log"), InfoBarSeverity.Error);
        if (DispatcherQueue.HasThreadAccess) Show();
        else DispatcherQueue.TryEnqueue(Show);
    }

    /// <summary>Runs on the UI thread whatever thread asks. Engine callbacks arrive from background threads.</summary>
    private void Post(Action action)
    {
        if (_closed) return;
        if (DispatcherQueue.HasThreadAccess) action();
        else DispatcherQueue.TryEnqueue(() => { if (!_closed) action(); });
    }

    private void OnDevicesChanged()
    {
        var real = _devices.Detected.Profile;
        _chipText.Text = real.Headline;
        ToolTipService.SetToolTip(_chipText, $"{real.Headline}\n{real.PcClassLabel} · {real.DeviceClassLabel}");
        if (!_packageChecked && ForgePackage.Mismatch(real) is { } package)
        {
            _packageBar.Title = package.Title;
            _packageBar.Message = package.Message;
            _packageBar.IsOpen = true;
        }
        _packageChecked = true;
        if (_devices.IsSimulated)
        {
            var shown = _devices.Effective.Profile;
            _simulationBar.Title = "Simulated: " + shown.Label;
            _simulationBar.Message = $"Display and planning only. Decks are still made on this PC ({real.Label}), and every deck states this PC.";
            _simulationBar.IsOpen = true;
        }
        else _simulationBar.IsOpen = false;
        RebuildDevice();
        RefreshModels();
    }

    private void Shutdown()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        _clock?.Stop();
        try { Task.Run(() => _session.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(6)); }
        catch (Exception exception) { Program.LogError(_options, exception); }
    }
}
