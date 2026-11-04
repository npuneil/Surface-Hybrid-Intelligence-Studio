using System.Reflection;
using System.Runtime.InteropServices;
using SurfaceDeviceKit;

namespace OfflinePptxForge;

/// <summary>
/// The Forge ships as one package per silicon family (see <see cref="SiliconPackage"/>). The packages for one architecture hold the same app;
/// the family stamp (ForgeSilicon, set at build time) only decides which package this copy says it is, so the app can point a presenter to
/// the right one. A developer build has no family and never warns, except when it runs under emulation, which costs the GPU and NPU.
/// </summary>
internal static class ForgePackage
{
    /// <summary>"Snapdragon", "Intel", "NVIDIA", or "any" for a developer build.</summary>
    internal static string Silicon { get; } =
        SiliconPackage.Family(typeof(ForgePackage).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "ForgeSilicon")?.Value) ?? "any";

    internal static string Architecture => RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>For example "Snapdragon-arm64"; "dev-x64" for a build without a family.</summary>
    internal static string Name => SiliconPackage.Name(Silicon, Architecture);

    /// <summary>The package's name for people, or the bare name of a developer build.</summary>
    internal static string Label => Silicon == "any" ? $"developer build ({Architecture})" : SiliconPackage.Describe(Name);

    /// <summary>True when this copy is the package made for the PC, or a developer build for the PC's own architecture.</summary>
    internal static bool Fits(DeviceProfile real)
    {
        string want = SiliconPackage.For(real);
        return Name == want || (Silicon == "any" && want.EndsWith("-" + Architecture, StringComparison.Ordinal));
    }

    /// <summary>The line on the This PC page: which package this copy is and which one the shown device takes.</summary>
    internal static string Line(DeviceProfile shown, bool simulated)
    {
        string want = SiliconPackage.For(shown);
        if (simulated) return $"{shown.Label} takes the {SiliconPackage.Describe(want)} package. This copy: {Label}.";
        if (!Fits(shown)) return $"This copy: {Label}. This PC takes the {SiliconPackage.Describe(want)} package.";
        return Silicon == "any"
            ? $"This copy: {Label}. It runs natively here, and the package for this PC is {SiliconPackage.Describe(want)}."
            : $"This copy: {Label}. It is the package for this PC.";
    }

    /// <summary>Null when this copy is the right package for the real PC (or a developer build running natively).</summary>
    internal static (string Title, string Message)? Mismatch(DeviceProfile real)
    {
        string want = SiliconPackage.For(real);
        if (real.Os.Emulated)
            return ("Use the Arm64 package",
                $"This copy is the {Label}{(Silicon == "any" ? "" : " package")} and runs under x64 emulation on this Arm64 Surface, so local models can use only the CPU and decks take much longer. " +
                $"Open the {SiliconPackage.Describe(want)} package to run them on {(want == SiliconPackage.NvidiaArm64 ? "the NVIDIA GPU" : "the NPU or GPU")}.");
        if (Fits(real)) return null;
        return ("Another package fits this PC better",
            $"This copy is the {Label} package. It still makes decks here, but this PC matches the {SiliconPackage.Describe(want)} package, " +
            "and that package's test script checks the results this PC should give.");
    }
}
