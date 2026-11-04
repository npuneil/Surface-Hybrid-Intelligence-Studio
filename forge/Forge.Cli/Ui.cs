using System.Diagnostics;

namespace Forge.Cli;

/// <summary>Progress lines stamped with the time since the command started, so a long run shows where its minutes went. Silent with --json.</summary>
internal sealed class Ui(bool quiet)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public double Seconds => _clock.Elapsed.TotalSeconds;

    public void Say(string line)
    {
        if (!quiet) Console.WriteLine($"[{Seconds,6:0.0}s] {line}");
    }
}
