using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

/// <summary>
/// State shared by the Hybrid router, Build on Surface and Show it off pages. The service runs one GPU job at a
/// time, so a lab job interlocks with the Live workspace, Live demos and model switching.
/// </summary>
internal sealed partial class MainWindow
{
    private bool _labRunning;
    private string _labActivity = "";
    private CancellationTokenSource? _labCts;

    private bool ServiceOnline => _status is not null && !_serviceInfo.IsOpen;

    private string? LabBlocker(bool needsModel = true)
    {
        if (!ServiceOnline) return "The local service is not connected.";
        if (_labRunning) return _labActivity + " is running. Wait for it to finish.";
        if (_running) return "The Live GPU workspace is running. Wait for it to finish.";
        if (_demoRunning) return "A live demo is running. Wait for it to finish.";
        if (_switching) return "A model switch is in progress.";
        if (needsModel && _status?.Engine.IsReady != true) return _status?.Engine.Detail is { Length: > 0 } d ? d : "The model is not ready yet.";
        return null;
    }

    private CancellationToken BeginLab(string activity)
    {
        _labRunning = true;
        _labActivity = activity;
        _labCts?.Dispose();
        _labCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _statusTimer.Interval = TimeSpan.FromSeconds(3);
        UpdateLiveButtons();
        return _labCts.Token;
    }

    private void EndLab()
    {
        _labRunning = false;
        _labActivity = "";
        UpdateLiveButtons();
    }

    private void StopLab()
    {
        try { _labCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void UpdateLabButtons()
    {
        UpdateHybridButtons();
        UpdateBuildButtons();
        UpdateShowOffButtons();
    }

    private void ApplyLabStatus(StatusSnapshot status)
    {
        ApplyBuildStatus(status);
        ApplyShowOffStatus(status);
    }

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static Border WherePill(string where) => where switch
    {
        "device" => Pill("ON THIS PC", GreenTint, GreenInk),
        "cloud" => Pill("CLOUD", SlateTint, Slate),
        _ => Pill("PERSON", AmberTint, AmberInk),
    };

    private static (Brush Background, Brush Foreground) LaneBrushes(string? lane) => lane switch
    {
        "local" => (GreenTint, GreenInk),
        "hybrid" => (AmberTint, AmberInk),
        "cloud" => (SlateTint, Slate),
        _ => (Sand, Muted),
    };

    private static TextBlock MonoText(string value, string id, double size = 12.5) =>
        Id(Set(Text(value, size, Ink), t => { t.FontFamily = Mono; t.IsTextSelectionEnabled = true; }), id);

    private static TextBox ReadOnlyBox(string id, double minHeight, bool mono = false) => Id(new TextBox
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = minHeight,
        MaxHeight = 420,
        FontFamily = mono ? Mono : Display,
        FontSize = mono ? 12.5 : 14,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    }, id);

    private static string Str(JsonElement e, string name) => StatusParser.Str(e, name) ?? "";

    private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray() : [];

    private static TextBlock Strong(string value, double size = 14) => Text(value, size, Ink, FontWeights.SemiBold);

    private static void ShowInfo(InfoBar bar, string title, string message, InfoBarSeverity severity)
    {
        bar.Title = title;
        bar.Message = message;
        bar.Severity = severity;
        bar.IsOpen = true;
        bar.StartBringIntoView();
    }
}
