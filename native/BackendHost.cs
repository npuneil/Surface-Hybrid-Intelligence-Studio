using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace HybridIntelligenceStudio;

/// <summary>
/// Reuses a running showcase backend, or starts and owns one. Only an owned backend is ever terminated.
/// The owned backend is tied to this process through a kill-on-close job object, so it cannot outlive the app.
/// </summary>
internal sealed class BackendHost : IDisposable
{
    private Process? _process;
    private StreamWriter? _stdout;
    private StreamWriter? _stderr;
    private IntPtr _job;

    internal BackendHost(string root, int port)
    {
        Root = root;
        Port = port;
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
    }

    internal string Root { get; }
    internal int Port { get; }
    internal Uri BaseUri { get; }
    internal bool Owned => _process is not null;
    internal string Lifecycle { get; private set; } = "Not connected";

    internal static string? FindRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "server.py")) && File.Exists(Path.Combine(dir.FullName, "content.py"))
                && File.Exists(Path.Combine(dir.FullName, "local_engine.py")))
                return dir.FullName;
        return null;
    }

    internal const string PythonVariable = Brand.EnvPrefix + "PYTHON";

    internal static string? ConfiguredPython()
    {
        string? configured = Environment.GetEnvironmentVariable(PythonVariable)?.Trim().Trim('"');
        return string.IsNullOrEmpty(configured) ? null : configured;
    }

    internal static IEnumerable<string> PythonCandidates(string root)
    {
        if (ConfiguredPython() is { } configured)
            yield return configured;
        yield return Path.Combine(root, ".venv", "Scripts", "python.exe");
        yield return Path.Combine(Path.GetDirectoryName(root) ?? root, "hydrafusion-autorouting-demo", ".venv", "Scripts", "python.exe");
    }

    internal async Task EnsureAsync(BackendClient client, bool allowStart, IProgress<string> progress, CancellationToken cancellationToken)
    {
        progress.Report("Connecting to the local GPU service");
        try
        {
            await client.GetStatusAsync(cancellationToken, TimeSpan.FromSeconds(3));
            Lifecycle = $"Reusing the showcase service already running on port {Port}; it is left running when this app closes.";
            return;
        }
        catch (BackendException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            if (!allowStart)
                throw new BackendException($"No showcase service is running on port {Port} and automatic start is disabled (--no-backend-start).");
        }

        if (ConfiguredPython() is { } configuredPython && !File.Exists(configuredPython))
            throw new BackendException($"{PythonVariable} points to a missing file: {configuredPython}");
        string python = PythonCandidates(Root).FirstOrDefault(File.Exists)
            ?? throw new BackendException($"The Foundry Local Python environment was not found. Set {PythonVariable}, create the project .venv (see README), or provide ..\\hydrafusion-autorouting-demo\\.venv.");
        string logs = Path.Combine(Root, ".runtime", "native");
        Directory.CreateDirectory(logs);
        // The crash-safe cleanup promise depends on the job; refuse to start an owned service without it.
        try { TieChildrenToThisProcess(); }
        catch (Win32Exception exception)
        {
            throw new BackendException($"Could not set up automatic cleanup for an owned local service, so it was not started: {exception.Message}");
        }
        _stdout = new StreamWriter(Path.Combine(logs, "backend-stdout.log"), append: false) { AutoFlush = true };
        _stderr = new StreamWriter(Path.Combine(logs, "backend-stderr.log"), append: false) { AutoFlush = true };
        progress.Report("Starting the local GPU service");
        var info = new ProcessStartInfo(python)
        {
            WorkingDirectory = Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-u", Path.Combine(Root, "server.py"), "--port", Port.ToString() })
            info.ArgumentList.Add(argument);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Write(_stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Write(_stderr, e.Data);
        process.Start();
        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Lifecycle = $"Started by this app (PID {process.Id}); stopped automatically when this app closes. Logs: {logs}";

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                // Another launcher may have won the port race; reuse its identified service instead of failing.
                try
                {
                    await client.GetStatusAsync(cancellationToken, TimeSpan.FromSeconds(3));
                    _process = null;
                    process.Dispose();
                    Lifecycle = $"Reusing the showcase service already running on port {Port}; it is left running when this app closes.";
                    return;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    throw new BackendException($"The local service exited during start (code {process.ExitCode}). See {Path.Combine(logs, "backend-stderr.log")}.");
                }
            }
            try
            {
                await client.GetStatusAsync(cancellationToken, TimeSpan.FromSeconds(2));
                return;
            }
            catch (BackendException) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(400, cancellationToken);
            }
        }
        throw new BackendException("The local service did not respond within 60 seconds.");
    }

    private static void Write(StreamWriter? writer, string? line)
    {
        if (line is null || writer is null) return;
        try { lock (writer) writer.WriteLine(line); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Places this app in a kill-on-close job before starting the service, so the service (and any interpreter
    /// child it spawns) is created inside the job and is terminated even if this app ends unexpectedly.
    /// </summary>
    /// <exception cref="Win32Exception">Job creation, configuration or assignment failed; carries the Win32 error code.</exception>
    private void TieChildrenToThisProcess()
    {
        if (_job != IntPtr.Zero) return;
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length))
            {
                int error = Marshal.GetLastWin32Error();
                CloseHandle(job);
                throw new Win32Exception(error, "SetInformationJobObject (kill-on-close) failed");
            }
            using var self = Process.GetCurrentProcess();
            if (!AssignProcessToJobObject(job, self.Handle))
            {
                int error = Marshal.GetLastWin32Error();
                CloseHandle(job);
                throw new Win32Exception(error, "AssignProcessToJobObject failed");
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        _job = job;
    }

    public void Dispose()
    {
        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(5000))
                        LogCleanupFailure($"Owned service PID {process.Id} did not exit within 5 seconds of termination; the job object will end it when this app exits.");
                }
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException or AggregateException)
            {
                LogCleanupFailure($"Could not terminate owned service tree: {exception}");
            }
            process.Dispose();
            _process = null;
        }
        _stdout?.Dispose();
        _stderr?.Dispose();
        _stdout = _stderr = null;
        // The job handle intentionally stays open: closing it would terminate this app itself.
    }

    private void LogCleanupFailure(string message)
    {
        try
        {
            string dir = Path.Combine(Root, ".runtime", "native");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app-errors.log"), $"{DateTime.UtcNow:O} backend cleanup\n{message}\n\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(message);
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
