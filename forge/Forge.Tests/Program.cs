namespace Forge.Tests;

public static class Program
{
    private static readonly (string Name, Func<int> Run)[] Areas =
    [
        ("pptx", PptxPackage.Run),
        ("layouts", LayoutParity.Run),
        ("grade", GradeParity.Run),
        ("harness", HarnessTests.Run),
        ("engine", EngineTests.Run),
    ];

    public static int Main(string[] args)
    {
        var wanted = args.Length == 0 ? null : new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
        int failures = 0;
        foreach (var (name, run) in Areas)
        {
            if (wanted is not null && !wanted.Contains(name)) continue;
            Console.WriteLine($"== {name}");
            try { failures += run(); }
            catch (Exception ex) { failures++; Console.WriteLine($"  CRASH [{name}] {ex}"); }
        }
        Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
