using System.Text;
using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

/// <summary>
/// Build on Surface: the loaded model as a loopback OpenAI-compatible endpoint, copyable snippets and starter projects.
/// Starters are written as files for the builder to review and run; the studio never executes generated code.
/// </summary>
internal sealed partial class MainWindow
{
    private const string EndpointActivity = "An endpoint change";
    private const string EndpointTestActivity = "An endpoint test call";
    private JsonElement? _builderState;
    private bool _builderLoading;
    private string? _builderKey;
    private string? _lastStarterPath;
    private readonly List<(string Id, string Label, string Code)> _snippets = [];
    private readonly TextBlock _buildStateText = Id(Text("CHECKING", 12, Muted, FontWeights.SemiBold), "BuildEndpointState");
    private readonly Border _buildStatePill = new() { CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 4, 12, 5), HorizontalAlignment = HorizontalAlignment.Left, Background = Sand };
    private readonly TextBlock _buildRuntime = Id(Text("", 14, Ink, FontWeights.SemiBold), "BuildRuntime");
    private readonly TextBlock _buildBaseUrl = MonoText("-", "BuildBaseUrl", 15);
    private readonly TextBlock _buildModel = MonoText("-", "BuildModel", 13);
    private readonly TextBlock _buildDetail = Id(Small(""), "BuildEndpointDetail");
    private readonly Button _buildStart = Id(Primary("Start the local endpoint"), "BuildStartEndpoint");
    private readonly Button _buildStopEndpoint = Id(Secondary("Stop the endpoint"), "BuildStopEndpoint");
    private readonly Button _buildTest = Id(Secondary("Send a test call"), "BuildTestCall");
    private readonly Button _buildCopyUrl = Id(Secondary("Copy base URL"), "BuildCopyUrl");
    private readonly TextBlock _buildBlocker = Id(Small(""), "BuildBlocker");
    private readonly StackPanel _buildTestPanel = Id(Stack(8), "BuildTestPanel");
    private readonly TextBlock _buildTestMeta = Id(Small(""), "BuildTestMeta");
    private readonly TextBlock _buildTestRequest = MonoText("", "BuildTestRequest", 12);
    private readonly TextBlock _buildTestReply = Id(Set(Text("", 15, Ink), t => t.IsTextSelectionEnabled = true), "BuildTestReply");
    private readonly InfoBar _buildInfo = Id(new InfoBar { IsClosable = true }, "BuildInfo");
    private readonly ComboBox _buildSnippetPicker = Id(new ComboBox { Header = "Language or tool", MinWidth = 260 }, "BuildSnippetPicker");
    private readonly TextBox _buildSnippetCode = ReadOnlyBox("BuildSnippetCode", 220, mono: true);
    private readonly Button _buildCopySnippet = Id(Secondary("Copy code"), "BuildCopySnippet");
    private readonly TextBlock _buildSnippetNote = Id(Small(""), "BuildSnippetNote");
    private readonly StackPanel _buildKits = Id(Stack(16), "BuildKits");
    private readonly TextBlock _buildEnvNote = Id(Small(""), "BuildEnvNote");

    private string StarterDirectory => Path.Combine(ExportDirectory, "Starters");

    private FrameworkElement BuildBuild()
    {
        _buildStatePill.Child = _buildStateText;
        _buildTestPanel.Children.Add(_buildTestMeta);
        _buildTestPanel.Children.Add(Text("Reply from the model", 12, Muted, FontWeights.SemiBold));
        _buildTestPanel.Children.Add(CardBorder(_buildTestReply, 14, Paper));
        _buildTestPanel.Children.Add(Id(new Expander
        {
            Header = "The request your code would send",
            Content = _buildTestRequest,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        }, "BuildTestRequestExpander"));
        Show(_buildTestPanel, false);
        _buildKits.Children.Add(Body("Connect to the local service to load the starter kits."));
        _buildStart.Click += async (_, _) => await ChangeEndpointAsync("start");
        _buildStopEndpoint.Click += async (_, _) => await ChangeEndpointAsync("stop");
        _buildTest.Click += async (_, _) => await TestEndpointAsync();
        _buildCopyUrl.Click += (_, _) =>
        {
            if (_buildBaseUrl.Text.StartsWith("http", StringComparison.Ordinal))
            {
                CopyToClipboard(_buildBaseUrl.Text);
                ShowInfo(_buildInfo, "Copied", _buildBaseUrl.Text, InfoBarSeverity.Informational);
            }
        };
        _buildSnippetPicker.SelectionChanged += (_, _) => ShowSelectedSnippet();
        _buildCopySnippet.Click += (_, _) =>
        {
            if (_buildSnippetCode.Text.Length == 0) return;
            CopyToClipboard(_buildSnippetCode.Text);
            ShowInfo(_buildInfo, "Code copied", "Paste it into your editor. It calls this PC's loopback endpoint only.", InfoBarSeverity.Informational);
        };
        var openStarters = Id(new HyperlinkButton { Content = "Open the starters folder", Padding = new Thickness(0), Foreground = Accent }, "BuildOpenStarters");
        openStarters.Click += (_, _) => OpenStartersFolder();

        var steps = new AdaptiveGrid(240, 3, 14).With(
            StepCard("1", "Start the endpoint", "The model already on the GPU answers OpenAI-style requests on 127.0.0.1."),
            StepCard("2", "Copy code or create a starter", "Python, C#, JavaScript, PowerShell or curl - standard libraries only."),
            StepCard("3", "Run it in your terminal", "Prototype locally, then point the same code at a cloud base URL when a feature needs it."));

        var endpoint = Id(CardBorder(Stack(12,
            Row(10, Glyph("\uE968", 20, Accent), H2("Local endpoint")),
            Row(10, _buildStatePill, _buildRuntime),
            Stack(2, Small("Base URL"), _buildBaseUrl),
            Stack(2, Small("Model"), _buildModel),
            _buildDetail,
            new AdaptiveGrid(170, 4, 8).With(_buildStart, _buildStopEndpoint, _buildTest, _buildCopyUrl),
            _buildBlocker,
            _buildTestPanel), 22), "BuildEndpointCard");

        var snippetRow = new Grid { ColumnSpacing = 10 };
        snippetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        snippetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        snippetRow.Children.Add(_buildSnippetPicker);
        _buildCopySnippet.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(_buildCopySnippet, 1);
        snippetRow.Children.Add(_buildCopySnippet);
        var snippets = Id(CardBorder(Stack(12,
            Row(10, Glyph("\uE943", 20, Accent), H2("Call it from your code")),
            Body("The same request shape as the cloud APIs your developers already use. These snippets are filled in with this PC's endpoint and model."),
            snippetRow, _buildSnippetCode, _buildSnippetNote), 22), "BuildSnippetsCard");

        return Page(Stack(22,
            Stack(10, Eyebrow("Build on Surface"), H1("This laptop is an AI endpoint."),
                Body("Foundry Local and llama.cpp both serve the OpenAI chat-completions API on loopback. Point an OpenAI-compatible SDK at this PC, "
                    + "prototype with real models on the RTX GPU, then decide feature by feature what stays local and what moves to the cloud - "
                    + "by changing a base URL, not rewriting the app.")),
            steps,
            _buildInfo,
            TwoPane(endpoint, snippets, 470, 1040),
            CardBorder(Lead(12, Glyph("\uE72E", 20, Slate), Small(
                "Boundaries: loopback only (127.0.0.1) and unauthenticated, so any app on this PC can call it while it runs. It is off until you start it, "
                + "and stops when you stop it or close the studio. Your calls share the GPU with the studio, so concurrent requests slow each other down. "
                + "Starter projects are files you review and run yourself; the studio never executes them.", Ink)), 14, SlateTint),
            Stack(12, Eyebrow("Starter kits"), H2("Start from working code, not a blank file"),
                Body("Each kit is a small, readable project wired to this PC. Create one, open a terminal in its folder and run the command shown."),
                _buildEnvNote, openStarters, _buildKits)));
    }

    private static FrameworkElement StepCard(string number, string title, string detail) =>
        CardBorder(Lead(12, Set(Text(number, 26, Accent, FontWeights.SemiBold), t => t.FontFamily = Display), Stack(4, H3(title), Small(detail))), 18);

    private async Task RefreshBuilderAsync()
    {
        if (_builderLoading || _closed || _status is null) return;
        _builderLoading = true;
        try
        {
            var state = await _client.GetJsonAsync("/api/builder", _lifetime.Token);
            ApplyBuilderState(state);
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            ShowInfo(_buildInfo, "Builder hub unavailable", exception.Message + " Restart the local service with the updated studio files.", InfoBarSeverity.Error);
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // Closing.
        }
        finally
        {
            _builderLoading = false;
        }
    }

    private void ApplyBuilderState(JsonElement state)
    {
        bool firstLoad = _builderState is null;
        _builderState = state;
        _builderKey = BuilderKey(_status);
        var target = state.TryGetProperty("target", out var t) ? t : default;
        string runtime = target.ValueKind == JsonValueKind.Object ? Str(target, "runtime") : "";
        string endpointState = target.ValueKind == JsonValueKind.Object ? Str(target, "state") : "";
        string baseUrl = target.ValueKind == JsonValueKind.Object ? Str(target, "base_url") : "";
        string model = target.ValueKind == JsonValueKind.Object ? Str(target, "model") : "";
        _buildRuntime.Text = runtime;
        _buildBaseUrl.Text = baseUrl.Length > 0 ? baseUrl : "Not configured";
        _buildModel.Text = model.Length > 0 ? model : "No model loaded yet";
        var (pillBackground, pillForeground, pillText) = endpointState switch
        {
            "running" => (GreenTint, GreenInk, "RUNNING ON LOOPBACK"),
            "closed" => (AmberTint, AmberInk, "UNAVAILABLE"),
            "stopped" => (Sand, Muted, "OFF"),
            _ => (Sand, Muted, endpointState.ToUpperInvariant()),
        };
        _buildStatePill.Background = pillBackground;
        _buildStateText.Foreground = pillForeground;
        _buildStateText.Text = pillText;
        string detail = "";
        if (state.TryGetProperty("endpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Object)
        {
            string active = Str(endpoints, "active");
            if (endpoints.TryGetProperty(active, out var current) && current.ValueKind == JsonValueKind.Object) detail = Str(current, "detail");
            if (active == "large")
                detail += " Start and stop do not apply: llama-server serves the API for as long as this model is loaded.";
        }
        _buildDetail.Text = detail;

        _snippets.Clear();
        foreach (var s in Items(state, "snippets")) _snippets.Add((Str(s, "id"), Str(s, "label"), Str(s, "code")));
        string? selected = (_buildSnippetPicker.SelectedItem as ComboBoxItem)?.Tag as string;
        _buildSnippetPicker.Items.Clear();
        foreach (var (id, label, _) in _snippets) _buildSnippetPicker.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        int index = Math.Max(0, _snippets.FindIndex(s => s.Id == selected));
        _buildSnippetPicker.SelectedIndex = _snippets.Count > 0 ? index : -1;
        _buildSnippetNote.Text = Str(state, "snippet_note") is { Length: > 0 } note ? note
            : "Snippets are shown for you to copy. The studio does not run them.";
        ShowSelectedSnippet();

        if (state.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
            _buildEnvNote.Text = $"Starters read {Str(env, "base_url")} and {Str(env, "model")} if you set them, so the same code can target another loopback model or, after your own review, a cloud endpoint.";
        if (firstLoad || _buildKits.Children.Count <= 1) PopulateKits(state);
        UpdateBuildButtons();
    }

    private void ShowSelectedSnippet()
    {
        string? id = (_buildSnippetPicker.SelectedItem as ComboBoxItem)?.Tag as string;
        _buildSnippetCode.Text = _snippets.FirstOrDefault(s => s.Id == id).Code ?? "";
        _buildCopySnippet.IsEnabled = _buildSnippetCode.Text.Length > 0;
    }

    private void PopulateKits(JsonElement state)
    {
        _buildKits.Children.Clear();
        var grid = new AdaptiveGrid(330, 3, 16);
        foreach (var kit in Items(state, "kits"))
        {
            string id = Str(kit, "id");
            var create = Id(Primary("Create starter project"), "KitCreate_" + id);
            create.MinHeight = 40;
            create.Click += async (_, _) => await CreateStarterAsync(id);
            grid.With(Id(CardBorder(Stack(10,
                Eyebrow(Str(kit, "language")), H3(Str(kit, "title")), Body(Str(kit, "summary")),
                Small("Shows: " + Str(kit, "shows"), Ink),
                CardBorder(MonoText(Str(kit, "run"), "KitRun_" + id, 12), 10, Paper),
                create), 20), "Kit_" + id));
        }
        if (grid.Children.Count == 0) _buildKits.Children.Add(Body("No starter kits were provided by the local service."));
        else _buildKits.Children.Add(grid);
    }

    private async Task CreateStarterAsync(string kit)
    {
        _buildInfo.IsOpen = false;
        try
        {
            var result = await _client.PostJsonAsync("/api/builder/starter", new Dictionary<string, object> { ["kit"] = kit }, _lifetime.Token, TimeSpan.FromSeconds(15));
            string directory = Path.Combine(StarterDirectory, $"{kit}-{DateTime.Now:yyyyMMdd-HHmmss}");
            int written = WriteStarter(directory, result);
            _lastStarterPath = directory;
            string run = Items(_builderState ?? default, "kits").Where(k => Str(k, "id") == kit).Select(k => Str(k, "run")).FirstOrDefault() ?? "";
            bool running = BuilderEndpoint().State == "running";
            ShowInfo(_buildInfo, $"{Str(result, "title")} created ({written} files)",
                $"{directory}. Open a terminal there and run: {run}. It calls {Str(result, "base_url")} with model {Str(result, "model")}."
                + (running ? "" : " Start the local endpoint first."), InfoBarSeverity.Success);
            if (!_options.Smoke) Toolkit.Open(directory);
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            ShowInfo(_buildInfo, "Starter could not be created", exception.Message, InfoBarSeverity.Error);
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // Closing.
        }
    }

    /// <summary>Writes the service's files under <paramref name="directory"/> only; any path that escapes it is rejected.</summary>
    internal static int WriteStarter(string directory, JsonElement result)
    {
        if (!result.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            throw new FormatException("The service returned no starter files.");
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var planned = new List<(string Path, string Text)>();
        foreach (var file in files.EnumerateObject())
        {
            string relative = file.Name.Replace('/', Path.DirectorySeparatorChar);
            if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(p => p is "" or "." or ".."))
                throw new FormatException($"Rejected an unsafe starter path: {file.Name}");
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"Rejected an unsafe starter path: {file.Name}");
            planned.Add((full, file.Value.GetString() ?? ""));
        }
        if (planned.Count == 0) throw new FormatException("The service returned no starter files.");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("That starter folder already exists. Try again in a second.");
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (var (path, text) in planned)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, utf8);
        }
        return planned.Count;
    }

    private void OpenStartersFolder()
    {
        try
        {
            Directory.CreateDirectory(StarterDirectory);
            Toolkit.Open(StarterDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowInfo(_buildInfo, "Could not open the starters folder", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task ChangeEndpointAsync(string action)
    {
        if (LabBlocker(needsModel: false) is string blocker)
        {
            ShowInfo(_buildInfo, "The endpoint cannot change now", blocker, InfoBarSeverity.Warning);
            return;
        }
        _buildInfo.IsOpen = false;
        var token = BeginLab(EndpointActivity);
        try
        {
            var state = await _client.PostJsonAsync("/api/builder/endpoint", new Dictionary<string, object> { ["action"] = action }, token, TimeSpan.FromSeconds(60));
            ApplyBuilderState(state);
            if (action == "start")
                ShowInfo(_buildInfo, "Local endpoint running", $"{_buildBaseUrl.Text} is serving {_buildModel.Text} on loopback. Send a test call, or copy a snippet.", InfoBarSeverity.Success);
            else
                ShowInfo(_buildInfo, "Local endpoint stopped", "Apps on this PC can no longer call it.", InfoBarSeverity.Informational);
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            ShowInfo(_buildInfo, action == "start" ? "The endpoint did not start" : "The endpoint did not stop", exception.Message, InfoBarSeverity.Error);
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            EndLab();
        }
    }

    private async Task TestEndpointAsync()
    {
        if (LabBlocker() is string blocker)
        {
            ShowInfo(_buildInfo, "The test call cannot start", blocker, InfoBarSeverity.Warning);
            return;
        }
        _buildInfo.IsOpen = false;
        var token = BeginLab(EndpointTestActivity);
        _buildTestMeta.Text = "Calling the endpoint over HTTP, exactly as your code would...";
        _buildTestReply.Text = "";
        _buildTestRequest.Text = "";
        Show(_buildTestPanel, true);
        try
        {
            var result = await _client.PostJsonAsync("/api/builder/test", new Dictionary<string, object>(), token, TimeSpan.FromMinutes(5));
            string reply = Str(result, "reply").Trim();
            _buildTestReply.Text = reply.Length > 0 ? reply : "(The model returned no visible text.)";
            _buildTestRequest.Text = "POST " + Str(result, "base_url") + "/chat/completions\n"
                + (result.TryGetProperty("request", out var request) ? JsonSerializer.Serialize(request, Evidence.Options) : "");
            var usage = result.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
            var parts = new List<string> { $"HTTP 200 from {Str(result, "runtime")}" };
            if (StatusParser.Num(result, "ms") is double ms) parts.Add($"{ms / 1000:F2} s round trip");
            if (usage.ValueKind == JsonValueKind.Object && StatusParser.Num(usage, "completion_tokens") is double tokens) parts.Add($"{tokens:N0} completion tokens");
            if (Str(result, "finish_reason") is { Length: > 0 } finish) parts.Add("finish: " + finish);
            if (StatusParser.Num(result, "reasoning_characters_withheld") is double withheld && withheld > 0)
                parts.Add($"{withheld:N0} characters of private reasoning withheld");
            _buildTestMeta.Text = string.Join(" \u00B7 ", parts) + ". A real loopback call; nothing left this PC.";
            if (result.TryGetProperty("endpoints", out _)) await RefreshBuilderAsync();
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            _buildTestMeta.Text = "The test call did not complete.";
            ShowInfo(_buildInfo, "Test call failed", exception.Message, InfoBarSeverity.Error);
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            EndLab();
        }
    }

    private static string BuilderKey(StatusSnapshot? status) =>
        status is null ? "" : $"{status.Engine.State}|{status.Engine.Model?.Alias}|{status.Engine.Model?.Provider}";

    private void ApplyBuildStatus(StatusSnapshot status)
    {
        if (_currentSection == "build" && !_labRunning && BuilderKey(status) != _builderKey) _ = RefreshBuilderAsync();
        UpdateBuildButtons();
    }

    private (string State, string Active) BuilderEndpoint()
    {
        string endpointState = "", active = "foundry";
        if (_builderState is { } state)
        {
            if (state.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object) endpointState = Str(target, "state");
            if (state.TryGetProperty("endpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Object) active = Str(endpoints, "active");
        }
        return (endpointState, active);
    }

    private void UpdateBuildButtons()
    {
        var (endpointState, active) = BuilderEndpoint();
        string? blocker = LabBlocker(needsModel: false);
        bool foundry = active == "foundry";
        _buildStart.IsEnabled = blocker is null && foundry && endpointState == "stopped";
        _buildStopEndpoint.IsEnabled = blocker is null && foundry && endpointState == "running";
        _buildTest.IsEnabled = LabBlocker() is null && endpointState == "running";
        _buildCopyUrl.IsEnabled = _buildBaseUrl.Text.StartsWith("http", StringComparison.Ordinal);
        _buildBlocker.Text = _labRunning && _labActivity is EndpointActivity or EndpointTestActivity ? _labActivity + " is in progress..." : blocker ?? "";
        Show(_buildBlocker, _buildBlocker.Text.Length > 0);
    }
}
