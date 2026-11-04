using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace SurfaceDeviceKit.FoundryLocal;

internal static class NativeMethods
{
    internal const uint LoadLibrarySearchDllLoadDir = 0x100;
    internal const uint LoadLibrarySearchDefaultDirs = 0x1000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr AddDllDirectory(string newDirectory);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);
}

/// <summary>Minimal append-only logger for Foundry Local diagnostics.</summary>
internal sealed class FileLogger(string path) : ILogger
{
    private readonly object _gate = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Information;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        try
        {
            lock (_gate) File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {logLevel}: {formatter(state, exception)}{(exception is null ? "" : " " + exception)}\n");
        }
        catch (IOException) { }
    }
}
