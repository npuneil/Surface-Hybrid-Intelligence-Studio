using System.Diagnostics;
using System.Globalization;
using System.Text;
using Forge.Core.Harness;

namespace Forge.Engine;

/// <summary>One reading from a power sensor: seconds since the meter started, and watts.</summary>
public readonly record struct PowerSample(double Seconds, double Watts);

/// <summary>The arithmetic of the meter, apart from the process that feeds it, so it can be tested with a made-up stream of readings.</summary>
public static class PowerMath
{
    /// <summary>Reads one line of <c>nvidia-smi --query-gpu=power.draw --format=csv,noheader,nounits</c>. "[N/A]" and "[Not Supported]" are not readings.</summary>
    public static bool TryParseWatts(string? line, out double watts)
    {
        watts = 0;
        string text = (line ?? "").Trim();
        if (text.Length == 0 || text[0] == '[' || text.StartsWith("N/A", StringComparison.OrdinalIgnoreCase)) return false;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out watts)) return false;
        return double.IsFinite(watts) && watts is >= 0 and < 2000;
    }

    /// <summary>Energy over a run: the sensor's power integrated with the trapezoid rule (the first reading is held back to the start and
    /// the last forward to the end), plus a fixed allowance for the rest of the platform. Under two readings is not a measurement.</summary>
    public static EnergyReading Integrate(IReadOnlyList<PowerSample> samples, double durationSeconds, double platformWatts, string basis)
    {
        var ordered = samples.Where(s => s.Seconds >= 0 && s.Seconds <= durationSeconds).OrderBy(s => s.Seconds).ToList();
        if (durationSeconds <= 0 || ordered.Count < 2)
            return EnergyReading.NotMeasured("the GPU power sensor gave fewer than two readings during the run");

        double joules = ordered[0].Watts * ordered[0].Seconds;
        for (int i = 1; i < ordered.Count; i++)
            joules += (ordered[i - 1].Watts + ordered[i].Watts) / 2.0 * (ordered[i].Seconds - ordered[i - 1].Seconds);
        joules += ordered[^1].Watts * (durationSeconds - ordered[^1].Seconds);
        joules += platformWatts * durationSeconds;
        return new EnergyReading(true, joules / 3600.0, basis);
    }
}

/// <summary>Measures the GPU's board power with <c>nvidia-smi</c> while a deck is drafted. Only used when the model runs on an NVIDIA GPU:
/// the NPU and CPU lanes have no sensor the deck can honestly quote, so they report "no power meter on this device".</summary>
public sealed class NvidiaSmiPowerMeter : IEnergyMeter, IDisposable
{
    public const int IntervalMs = 250;
    public static readonly string Basis = $"GPU board power (nvidia-smi, {IntervalMs} ms) + {Economics.PlatformOverheadW:0} W platform allowance";

    private readonly string _exe;
    private readonly int _gpuIndex;
    private readonly object _gate = new();
    private readonly List<PowerSample> _samples = [];
    private readonly Stopwatch _clock = new();
    private Process? _process;
    private bool _collecting;
    private bool _exitHooked;

    private NvidiaSmiPowerMeter(string exe, int gpuIndex)
    {
        _exe = exe;
        _gpuIndex = gpuIndex;
    }

    /// <summary>Where nvidia-smi is, or null. It ships with the NVIDIA driver; the System32 copy is the usual one on Windows on Arm and x64.</summary>
    public static string? FindNvidiaSmi()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        };
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim().Trim('"'), "nvidia-smi.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>A meter for this PC's NVIDIA GPU, or null with the reason when the GPU does not report its power draw.</summary>
    public static NvidiaSmiPowerMeter? TryCreate(out string why, int gpuIndex = 0)
    {
        string? exe = FindNvidiaSmi();
        if (exe is null) { why = "nvidia-smi is not installed"; return null; }
        try
        {
            using var probe = Process.Start(Info(exe, gpuIndex, loop: false));
            if (probe is null) { why = "nvidia-smi did not start"; return null; }
            probe.BeginErrorReadLine();
            var output = probe.StandardOutput.ReadToEndAsync();
            if (!output.Wait(5000))
            {
                try { probe.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                why = "nvidia-smi did not answer within 5 seconds";
                return null;
            }
            string first = output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (!PowerMath.TryParseWatts(first, out _)) { why = "this GPU does not report its power draw to nvidia-smi"; return null; }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            why = "nvidia-smi could not be run: " + exception.Message;
            return null;
        }
        why = "";
        return new NvidiaSmiPowerMeter(exe, gpuIndex);
    }

    private static ProcessStartInfo Info(string exe, int gpuIndex, bool loop)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[] { "--query-gpu=power.draw", "--format=csv,noheader,nounits", "-i", gpuIndex.ToString(CultureInfo.InvariantCulture) })
            info.ArgumentList.Add(argument);
        if (loop)
        {
            info.ArgumentList.Add("-lms");
            info.ArgumentList.Add(IntervalMs.ToString(CultureInfo.InvariantCulture));
        }
        return info;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_process is not null) throw new InvalidOperationException("The power meter is already running.");
            _samples.Clear();
            _clock.Restart();
            var process = Process.Start(Info(_exe, _gpuIndex, loop: true)) ?? throw new InvalidOperationException("nvidia-smi did not start.");
            process.OutputDataReceived += OnLine;
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            _collecting = true;
            if (!_exitHooked)
            {
                // A loop that outlives the app would keep sampling for ever.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Kill();
                _exitHooked = true;
            }
        }
    }

    private void OnLine(object sender, DataReceivedEventArgs e)
    {
        if (!PowerMath.TryParseWatts(e.Data, out double watts)) return;
        lock (_gate)
        {
            if (_collecting) _samples.Add(new PowerSample(_clock.Elapsed.TotalSeconds, watts));
        }
    }

    public EnergyReading Stop()
    {
        List<PowerSample> samples;
        double duration;
        lock (_gate)
        {
            if (_process is null) return EnergyReading.NotMeasured("the power meter was not running");
            _collecting = false;
            duration = _clock.Elapsed.TotalSeconds;
            samples = [.. _samples];
        }
        Kill();
        return PowerMath.Integrate(samples, duration, Economics.PlatformOverheadW, Basis);
    }

    private void Kill()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
            _collecting = false;
        }
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(1500);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }

    public void Dispose() => Kill();
}
