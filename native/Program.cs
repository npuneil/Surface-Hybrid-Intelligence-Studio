using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HybridIntelligenceStudio;

internal sealed record AppOptions(
    int Port, string Root, bool AllowBackendStart, bool Smoke, bool SmokeLive, bool ExitAfterSmoke, bool Windowed, string? Section,
    int WindowWidth = 1440, int WindowHeight = 940)
{
    internal string RuntimeDirectory => Path.Combine(Root, ".runtime", "native");

    internal static AppOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        int port = int.TryParse(Value("--port"), out int p) && p is > 0 and < 65536 ? p : 8790;
        string root = Value("--root") is string r && Directory.Exists(r) ? Path.GetFullPath(r)
            : BackendHost.FindRoot(AppContext.BaseDirectory)
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Brand.CodeName);
        bool smoke = args.Contains("--smoke");
        int width = 1440, height = 940;
        bool sized = false;
        if (Value("--window-size")?.Split('x', 'X') is [var w, var h]
            && int.TryParse(w, out int pw) && int.TryParse(h, out int ph) && pw is >= 900 and <= 4000 && ph is >= 640 and <= 3000)
        {
            (width, height, sized) = (pw, ph, true);
        }
        return new AppOptions(port, root, !args.Contains("--no-backend-start"), smoke, smoke && args.Contains("--smoke-live"),
            args.Contains("--exit-after-smoke"), smoke || sized || args.Contains("--windowed"), Value("--section"), width, height);
    }
}

internal static class AppIdentity
{
    internal const string AppUserModelId = Brand.AppUserModelId;
    internal const string Title = Brand.WindowTitle;
    internal const string IconFile = "AppIcon.ico";
    internal const string LogoFile = "AppLogo.png";

    internal static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);
}

internal static class Program
{
    internal static int ExitCode { get; set; }

    [STAThread]
    private static int Main(string[] args)
    {
        _ = SetCurrentProcessExplicitAppUserModelID(AppIdentity.AppUserModelId);
        var options = AppOptions.Parse(args);
        try
        {
            if (args.Contains("--self-test"))
            {
                _ = AttachConsole(-1);
                return SelfTest.Run(options);
            }
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                GC.KeepAlive(new ShowcaseApp(options));
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
            Directory.CreateDirectory(options.RuntimeDirectory);
            File.AppendAllText(Path.Combine(options.RuntimeDirectory, "app-errors.log"), $"{DateTime.UtcNow:O}\n{exception}\n\n");
        }
        catch (IOException) { }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}

public sealed partial class ShowcaseApp : Application
{
    private readonly AppOptions _options;
    private MainWindow? _window;

    internal ShowcaseApp(AppOptions options)
    {
        _options = options;
        UnhandledException += (_, e) => Program.LogError(_options, e.Exception);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(_options);
        _window.Activate();
    }
}
