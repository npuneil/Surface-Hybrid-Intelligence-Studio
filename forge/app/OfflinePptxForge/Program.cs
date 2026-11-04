using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace OfflinePptxForge;

/// <summary>What was asked on the command line. A plain launch has none of these.</summary>
internal sealed class AppOptions
{
    public bool Help { get; init; }
    public bool List { get; init; }
    public bool Probe { get; init; }
    public bool SelfTest { get; init; }
    public bool Json { get; init; }
    public bool Smoke { get; init; }
    public bool SmokeLive { get; init; }
    public bool Windowed { get; init; }
    public string? Simulate { get; init; }
    public string? Section { get; init; }
    public string? BriefFile { get; init; }
    public string? Capture { get; init; }
    public string? Report { get; init; }
    public string? DataDirectory { get; init; }
    public string? AppDataDirectory { get; init; }
    public string? OutputRoot { get; init; }
    public bool AllowDownload { get; init; }
    public int WindowWidth { get; init; }
    public int WindowHeight { get; init; }

    /// <summary>Anything that talks to a console instead of opening a window.</summary>
    public bool Headless => Help || List || Probe || SelfTest;

    public string DataDir => DataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Brand.DataFolder);

    public static AppOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
        }
        int width = 0, height = 0;
        if (Value("--window-size")?.Split('x', 'X') is [var w, var h]
            && int.TryParse(w, out int pw) && int.TryParse(h, out int ph) && pw is >= 600 and <= 4000 && ph is >= 560 and <= 3000)
            (width, height) = (pw, ph);
        bool smoke = args.Contains("--smoke") || args.Contains("--smoke-live");
        return new AppOptions
        {
            Help = args.Contains("--help") || args.Contains("-h") || args.Contains("-?"),
            List = args.Contains("--list"),
            Probe = args.Contains("--probe"),
            SelfTest = args.Contains("--self-test"),
            Json = args.Contains("--json"),
            Smoke = smoke,
            SmokeLive = args.Contains("--smoke-live"),
            Windowed = smoke || width > 0 || args.Contains("--windowed"),
            Simulate = Value("--simulate"),
            Section = Value("--section"),
            BriefFile = Value("--brief"),
            Capture = Value("--capture"),
            Report = Value("--report"),
            DataDirectory = Value("--data"),
            AppDataDirectory = Value("--app-data"),
            OutputRoot = Value("--out"),
            AllowDownload = args.Contains("--allow-download"),
            WindowWidth = width,
            WindowHeight = height,
        };
    }
}

internal static class Program
{
    internal static int ExitCode { get; set; }

    [STAThread]
    private static int Main(string[] args)
    {
        _ = SetCurrentProcessExplicitAppUserModelID(Brand.AppUserModelId);
        var options = AppOptions.Parse(args);
        try
        {
            if (options.Headless)
            {
                _ = AttachConsole(-1);
                return Headless.Run(options);
            }
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                GC.KeepAlive(new ForgeApp(options));
            });
            return ExitCode;
        }
        catch (Exception exception)
        {
            LogError(options, exception);
            return 1;
        }
    }

    internal static void LogError(AppOptions options, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(options.DataDir);
            File.AppendAllText(Path.Combine(options.DataDir, "app-errors.log"), $"{DateTime.UtcNow:O}\n{exception}\n\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}

public sealed partial class ForgeApp : Application
{
    private readonly AppOptions _options;
    private MainWindow? _window;

    internal ForgeApp(AppOptions options)
    {
        _options = options;
        UnhandledException += (_, e) =>
        {
            Program.LogError(_options, e.Exception);
            e.Handled = true;
            _window?.ShowFault(e.Exception);
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(_options);
        // Smoke runs show the window without taking focus, so they never interrupt whoever is using the PC.
        if (_options.Smoke) _window.AppWindow.Show(false);
        else _window.Activate();
    }
}

internal static class Brand
{
    internal const string Name = "Offline PPTX Forge";
    internal const string DataFolder = "OfflinePptxForge";
    internal const string AppUserModelId = "SurfaceHybridIntelligence.OfflinePptxForge";
    internal const string Tagline = "Writes a PowerPoint deck on this PC. Nothing leaves it.";

    internal static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);
}
