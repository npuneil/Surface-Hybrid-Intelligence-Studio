namespace SurfaceDeviceKit;

/// <summary>
/// Apps built on the kit ship one package per silicon family, because the native AI runtimes differ by family and architecture:
/// Snapdragon on Arm64 (QNN for the NPU), NVIDIA on Arm64 (Surface Laptop Ultra, CUDA for Arm64), Intel and AMD on x64 (OpenVINO, DirectML)
/// and NVIDIA on x64 (RTX laptops such as Surface Laptop Studio, CUDA). <see cref="For"/> names the package a PC should run from what Windows
/// reports. It reads the OS architecture rather than the process's, so an x64 copy running under emulation on an Arm64 Surface still learns
/// that the PC wants an Arm64 package.
/// </summary>
public static class SiliconPackage
{
    public const string SnapdragonArm64 = "Snapdragon-arm64";
    public const string IntelX64 = "Intel-x64";
    public const string NvidiaArm64 = "NVIDIA-arm64";
    public const string NvidiaX64 = "NVIDIA-x64";

    /// <summary>Every package, in the order a release lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [SnapdragonArm64, IntelX64, NvidiaArm64, NvidiaX64];

    /// <summary>The package made for this device.</summary>
    public static string For(DeviceProfile profile)
    {
        if (profile.Os.Arch == "arm64") return profile.Cpu.Vendor == "nvidia" ? NvidiaArm64 : SnapdragonArm64;
        return profile.Gpus.Any(g => g.Vendor == "nvidia" && g.Kind is "discrete" or "unified") ? NvidiaX64 : IntelX64;
    }

    /// <summary>The package a build makes: <paramref name="family"/> is the build's silicon stamp (snapdragon, intel or nvidia, any case)
    /// and <paramref name="architecture"/> the process architecture. A build without a known family is "dev-arm64" or "dev-x64".</summary>
    public static string Name(string? family, string architecture) => $"{Family(family) ?? "dev"}-{architecture}";

    /// <summary>The canonical family name ("Snapdragon", "Intel" or "NVIDIA") for a build stamp in any case, or null.</summary>
    public static string? Family(string? stamp) => stamp?.Trim().ToLowerInvariant() switch
    {
        "snapdragon" => "Snapdragon",
        "intel" => "Intel",
        "nvidia" => "NVIDIA",
        _ => null,
    };

    /// <summary>For people: "Snapdragon (Arm64)", "Intel (x64, also for AMD)" and so on. Other names come back unchanged.</summary>
    public static string Describe(string package) => package switch
    {
        SnapdragonArm64 => "Snapdragon (Arm64)",
        IntelX64 => "Intel (x64, also for AMD)",
        NvidiaArm64 => "NVIDIA (Arm64, Surface Laptop Ultra)",
        NvidiaX64 => "NVIDIA (x64, Surface Laptop Studio and Surface Book)",
        var other => other,
    };
}
