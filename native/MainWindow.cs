using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

internal sealed partial class MainWindow : Window
{
    private readonly AppOptions _options;
    private readonly BackendClient _client;
    private readonly BackendHost _host;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Grid _root = new();
    private readonly NavigationView _nav = new();
    private readonly Dictionary<string, FrameworkElement> _pages = [];
    private readonly Dictionary<string, NavigationViewItem> _navItems = [];
    private readonly InfoBar _serviceInfo = Id(new InfoBar { IsClosable = true, Margin = new Thickness(16, 0, 16, 8) }, "ServiceInfoBar");
    private readonly TextBlock _enginePillText = Id(Text("Connecting", 12, Muted, FontWeights.SemiBold), "TitleEngineState");
    private readonly Border _enginePill = new() { CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3, 10, 4), Background = Sand, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private StatusSnapshot? _status;
    private Dictionary<string, Scenario> _scenarios = [];
    private bool _refreshing;
    private bool _closed;
    private string _currentSection = "overview";

    internal MainWindow(AppOptions options)
    {
        _options = options;
        _host = new BackendHost(options.Root, options.Port);
        _client = new BackendClient(_host.BaseUri);
        Title = AppIdentity.Title;
        ConfigureWindow();
        Content = BuildShell();
        _root.Loaded += OnLoaded;
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        AppWindow.Closing += (_, _) => Shutdown();
        Closed += (_, _) => Shutdown();
    }

    private string ExportDirectory => _options.Smoke
        ? Path.Combine(_options.RuntimeDirectory, "smoke-exports")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Brand.DisplayName, "Exports");

    private void ConfigureWindow()
    {
        string icon = AppIdentity.Asset(AppIdentity.IconFile);
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 0x1F, 0x1A, 0x17);
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 0xF3, 0xEE, 0xE7);
        // AppWindow sizes are physical pixels; options are DIPs, so scale and clamp to the work area.
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        if (scale <= 0) scale = 1;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Min((int)(_options.WindowWidth * scale), work.Width);
        int height = Math.Min((int)(_options.WindowHeight * scale), work.Height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(900 * scale);
            presenter.PreferredMinimumHeight = (int)(640 * scale);
            if (!_options.Windowed) presenter.Maximize();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
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
        var brand = Row(10, Logo(20, "TitleBarLogo"),
            Text(Brand.DisplayName, 13, Ink, FontWeights.SemiBold),
            Text(Brand.Device, 13, Muted));
        brand.VerticalAlignment = VerticalAlignment.Center;
        titleBar.Children.Add(brand);
        _enginePill.Child = _enginePillText;
        Grid.SetColumn(_enginePill, 2);
        titleBar.Children.Add(_enginePill);
        _root.Children.Add(titleBar);
        SetTitleBar(titleBar);

        Grid.SetRow(_serviceInfo, 1);
        _root.Children.Add(_serviceInfo);

        _nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Auto;
        _nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _nav.IsSettingsVisible = false;
        _nav.OpenPaneLength = 256;
        _nav.ExpandedModeThresholdWidth = 1100;
        _nav.CompactModeThresholdWidth = 640;
        _nav.Background = Paper;
        Id(_nav, "MainNavigation");
        _nav.PaneHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(12, 8, 0, 8),
            Children = { Logo(32, "NavLogo"), Stack(0, Id(Text(Brand.Mark, 15, Accent, FontWeights.Bold), "NavMark"), Small(Brand.MarkSubtitle)) }
        };
        AddNav("overview", "Executive overview", "\uE80F", "NavOverview");
        AddNav("live", "Live GPU workspace", "\uE945", "NavLive");
        AddNav("demos", "Live demos", "\uE768", "NavDemos");
        AddNav("hybrid", "Hybrid router", "\uE8AB", "NavHybrid");
        AddNav("industries", "Industry scenarios", "\uE821", "NavIndustries");
        AddNav("showoff", "Show it off", "\uEC4A", "NavShowOff");
        AddNav("build", "Build on Surface", "\uE943", "NavBuild");
        AddNav("sizing", "T-shirt sizing", "\uE9D9", "NavSizing");
        AddNav("platforms", "Platform fit", "\uE7F8", "NavPlatforms");
        AddNav("business", "Business case", "\uE8EF", "NavBusiness");
        AddNav("playbook", "Seller playbook", "\uE82D", "NavPlaybook");
        AddLearnNav();
        var evidence = new NavigationViewItem { Content = "Device evidence", Icon = Glyph("\uE9D2", 16, Muted), SelectsOnInvoked = false, Tag = "evidence" };
        Id(evidence, "NavDeviceEvidence");
        _nav.FooterMenuItems.Add(evidence);
        _nav.ItemInvoked += async (_, e) =>
        {
            if (e.InvokedItemContainer?.Tag as string == "evidence") await ShowDeviceEvidenceAsync();
        };
        _nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItemContainer?.Tag is string tag && _pages.ContainsKey(tag)) ShowSection(tag);
        };

        _pages["overview"] = BuildOverview();
        _pages["live"] = BuildLive();
        _pages["demos"] = BuildDemos();
        _pages["hybrid"] = BuildHybrid();
        _pages["industries"] = BuildIndustries();
        _pages["showoff"] = BuildShowOff();
        _pages["build"] = BuildBuild();
        _pages["sizing"] = BuildSizing();
        _pages["platforms"] = BuildPlatforms();
        _pages["business"] = BuildBusiness();
        _pages["playbook"] = BuildPlaybook();
        _pages["learn"] = BuildLearn();
        foreach (var (key, page) in _pages) Id(page, "Page_" + key);

        Grid.SetRow(_nav, 2);
        _root.Children.Add(_nav);
        Navigate(_options.Section is string s && _pages.ContainsKey(s) ? s : "overview");
        ApplyLearnVisibility();
        return _root;
    }

    private void AddNav(string tag, string label, string glyph, string id)
    {
        var item = new NavigationViewItem { Content = label, Icon = Glyph(glyph, 16, Accent), Tag = tag };
        Id(item, id);
        _navItems[tag] = item;
        _nav.MenuItems.Add(item);
    }

    internal void Navigate(string tag)
    {
        if (!_navItems.TryGetValue(tag, out var item)) return;
        if (!ReferenceEquals(_nav.SelectedItem, item)) _nav.SelectedItem = item;
        ShowSection(tag);
    }

    private void ShowSection(string tag)
    {
        if (_currentSection == "demos" && tag != "demos") StopDemoAudio();
        _currentSection = tag;
        if (!ReferenceEquals(_nav.Content, _pages[tag])) _nav.Content = _pages[tag];
        if (tag == "demos" && _status is not null) _ = RefreshDemoCatalogAsync();
        if (tag == "hybrid" && _status is not null) _ = LoadHybridCatalogAsync();
        if (tag == "build" && _status is not null && !_labRunning) _ = RefreshBuilderAsync();
        if (tag == "showoff") _ = RefreshToolsAsync();
    }

    private static Image Logo(double size, string id)
    {
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        string path = AppIdentity.Asset(AppIdentity.LogoFile);
        if (File.Exists(path)) image.Source = new BitmapImage(new Uri(path));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(image, Brand.DisplayName + " logo");
        return Id(image, id);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _root.Loaded -= OnLoaded;
        await ConnectAsync();
        if (_options.Smoke && !_lifetime.IsCancellationRequested) await RunSmokeAsync();
    }

    private async Task ConnectAsync()
    {
        var progress = new Progress<string>(message => SetEnginePill(message, Sand, Muted));
        try
        {
            await _host.EnsureAsync(_client, _options.AllowBackendStart, progress, _lifetime.Token);
            _scenarios = await _client.GetScenariosAsync(_lifetime.Token);
            PopulateScenarios();
            await RefreshStatusAsync();
            await RefreshDemoCatalogAsync();
            await LoadHybridCatalogAsync();
            _statusTimer.Start();
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            Program.LogError(_options, exception);
            ShowServiceError("Local GPU service unavailable", exception.Message + " Overview, sizing, platform and playbook sections still work.");
            SetEnginePill("Service offline", AccentTint, AccentDeep);
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // The window closed while connecting; cancellation and disposal are expected.
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshing || _closed) return;
        _refreshing = true;
        try
        {
            var status = await _client.GetStatusAsync(_lifetime.Token, TimeSpan.FromSeconds(5));
            bool wasOffline = _status is null || _serviceInfo.IsOpen;
            _status = status;
            if (wasOffline && _serviceInfo.Severity == InfoBarSeverity.Error) _serviceInfo.IsOpen = false;
            if (_scenarios.Count == 0)
            {
                _scenarios = await _client.GetScenariosAsync(_lifetime.Token);
                PopulateScenarios();
            }
            ApplyStatus(status);
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            SetEnginePill("Service offline", AccentTint, AccentDeep);
            ShowServiceError("Lost contact with the local GPU service", exception.Message);
            UpdateLiveButtons();
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // Closing: an in-flight status poll is cancelled or its client disposed.
        }
        finally
        {
            _refreshing = false;
            _statusTimer.Interval = TimeSpan.FromSeconds(_status?.Engine.IsReady == true && !_running && !_demoRunning && !_labRunning ? 5 : 1.5);
        }
    }

    private void ApplyStatus(StatusSnapshot status)
    {
        var engine = status.Engine;
        if (engine.IsReady) SetEnginePill($"{engine.Model!.Name} ready on GPU", GreenTint, GreenInk);
        else if (engine.State == "error") SetEnginePill("Model unavailable", AccentTint, AccentDeep);
        else if (engine.State == "disabled") SetEnginePill("Rehearsal only", AmberTint, AmberInk);
        else SetEnginePill(string.IsNullOrEmpty(engine.Detail) ? "Preparing model" : engine.Detail, AmberTint, AmberInk);
        ApplyOverviewStatus(status);
        ApplyLiveStatus(status);
        ApplyDemosStatus(status);
        ApplySizingStatus(status);
        ApplyBusinessStatus(status);
        ApplyLabStatus(status);
    }

    private void SetEnginePill(string text, Brush background, Brush foreground)
    {
        _enginePillText.Text = text;
        _enginePillText.Foreground = foreground;
        _enginePill.Background = background;
    }

    private void ShowServiceError(string title, string message)
    {
        _serviceInfo.Severity = InfoBarSeverity.Error;
        _serviceInfo.Title = title;
        _serviceInfo.Message = message;
        _serviceInfo.IsOpen = true;
    }

    private void Shutdown()
    {
        if (_closed) return;
        _closed = true;
        _statusTimer.Stop();
        _demoClockTimer.Stop();
        StopDemoAudio();
        try { _runCts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _demoCts?.Cancel(); } catch (ObjectDisposedException) { }
        StopLab();
        _lifetime.Cancel();
        _host.Dispose();
        _client.Dispose();
    }

    private static string Gb(double? gib, string unit = "GB") => gib is double v ? $"{Math.Round(v):N0} {unit}" : "Unavailable";

    private async Task ShowDeviceEvidenceAsync()
    {
        if (_evidenceDialogOpen) return;
        var d = _status?.Device;
        var panel = Stack(10);
        void Fact(string label, string? value, string id)
        {
            var grid = new Grid { ColumnSpacing = 16 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(Text(label, 13, Muted));
            var v = Id(Set(Text(string.IsNullOrWhiteSpace(value) ? "Unavailable" : value, 13, Ink, FontWeights.SemiBold), t => t.IsTextSelectionEnabled = true), id);
            Grid.SetColumn(v, 1);
            grid.Children.Add(v);
            panel.Children.Add(grid);
        }
        Fact("Device", d?.DisplayName, "EvidenceDevice");
        Fact("Processor", d?.Cpu, "EvidenceCpu");
        Fact("CPU cores", d?.Cores?.ToString(), "EvidenceCores");
        Fact("GPU", d is null ? null : string.Join(", ", d.Accelerators), "EvidenceGpu");
        Fact("Installed physical memory", d?.InstalledBytes is long ib ? $"{Gb(d.InstalledGiB)} ({ib:N0} bytes)" : null, "EvidenceInstalled");
        Fact("Windows-visible memory", d?.OsVisibleBytes is long vb ? $"{d.OsVisibleGiB:F1} GiB ({vb:N0} bytes)" : null, "EvidenceVisible");
        Fact("GPU memory budget", d?.PrimaryGpuBudgetMiB is long mib ? $"{mib:N0} MiB driver-reported ({mib / 1024d:F1} GiB)" : null, "EvidenceGpuBudget");
        Fact("Live model", _status?.Engine.Model is { } m ? $"{m.Name} ({m.Id})" : _status?.Engine.Detail, "EvidenceModel");
        Fact("Execution provider", _status?.Engine.Model?.Provider, "EvidenceProvider");
        Fact("Operating system", d is null ? null : $"{d.Os} {d.OsVersion}", "EvidenceOs");
        Fact("Observed", d?.ObservedAt, "EvidenceObserved");
        panel.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 6, 0, 6) });
        panel.Children.Add(Body("Installed, Windows-visible and GPU-budget figures are distinct reported views of one machine. They are never added together, and the GPU figure is a driver-reported budget, not dedicated VRAM."));
        panel.Children.Add(Small(d?.MemorySource ?? ""));
        panel.Children.Add(Small("GPU budget source: nvidia-smi memory.total. Inference runs on the RTX GPU through the CUDA execution provider; there is no silent CPU fallback."));
        panel.Children.Add(Small("Service: " + _host.Lifecycle));
        if (d?.Error is string err) panel.Children.Add(Small("Inventory error: " + err, AccentDeep));
        var dialog = new ContentDialog
        {
            Title = "Device evidence",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _root.XamlRoot,
            RequestedTheme = ElementTheme.Light,
        };
        Id(dialog, "DeviceEvidenceDialog");
        _evidenceDialogOpen = true;
        try { await dialog.ShowAsync(); }
        finally { _evidenceDialogOpen = false; }
    }

    private bool _evidenceDialogOpen;
}
