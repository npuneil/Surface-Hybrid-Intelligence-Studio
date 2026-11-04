using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SurfaceDeviceKit.FoundryLocal;

public sealed record CudaSupplementStatus(string State, string Detail, string? Path = null);

/// <summary>
/// The Foundry Local CUDA provider bundle for Windows ARM64 ships cuDNN 9 without its tensor-IR and convolution engine
/// libraries, and some chat models (Phi-4-mini) build their attention through cuDNN. This preloads NVIDIA's own
/// redistributable cuDNN 9.26 + NVRTC 13.4 (pinned by version and SHA-256, downloaded from PyPI only when the user asks)
/// before Foundry Local loads the provider. A copy another Surface demo app already prepared is reused.
/// Ported from the Surface Sales Kit, which ported it from the CES 2027 station.
/// </summary>
internal static class CudaSupplement
{
    internal const string Tag = "cudnn-9.26.1.1_nvrtc-13.4.92";
    private const string Sentinel = "cudnn_cnn64_9.dll";
    private static readonly string[] PreloadOrder =
    [
        "nvrtc64_130_0.dll", "nvrtc-builtins64_134.dll", "cudnn64_9.dll", "cudnn_graph64_9.dll",
        "cudnn_engines_tensor_ir64_9.dll", "cudnn_engines_precompiled64_9.dll", "cudnn_engines_runtime_compiled64_9.dll",
        "cudnn_heuristic64_9.dll", "cudnn_ops64_9.dll", "cudnn_cnn64_9.dll", "cudnn_adv64_9.dll", "cudnn_ext64_9.dll",
    ];

    private sealed record Wheel(string Name, string Version, string File, string Sha256, string Prefix);

    private static readonly Wheel[] Arm64Wheels =
    [
        new("nvidia-cudnn-cu13", "9.26.1.1", "nvidia_cudnn_cu13-9.26.1.1-py3-none-win_arm64.whl",
            "f1f2ec54a923ca02c9d8aee02a168da87067fae9ac0a408bb9ccf4936fe4a9f7", "nvidia/cudnn/bin/"),
        new("nvidia-cuda-nvrtc", "13.4.92", "nvidia_cuda_nvrtc-13.4.92-py3-none-win_arm64.whl",
            "1620066e967e93119d67338628935cbb1196b53a1474b07f14b7b77aba477284", "nvidia/cu13/bin/arm64/"),
    ];

    private static readonly List<IntPtr> Handles = [];
    private static string? _preloaded;

    internal static bool Applies(DeviceProfile device) =>
        device.Gpus.Any(g => g.Vendor == "nvidia") && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    internal static string RuntimeDirectory(string appData) => Path.Combine(appData, "gpu-media-runtime", Tag);

    internal static string? ActiveBundleBin(string appData)
    {
        string root = Path.Combine(appData, "ep", "cuda-ep");
        try
        {
            string name = File.ReadAllText(Path.Combine(root, "active")).Trim();
            if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0) return null;
            string bin = Path.Combine(root, "bundles", name, "bin");
            return Directory.Exists(bin) ? bin : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>True unless the active CUDA bundle already carries the full cuDNN set. No bundle yet also counts as needed.</summary>
    internal static bool Needed(string appData) => ActiveBundleBin(appData) is not { } bin || !File.Exists(Path.Combine(bin, Sentinel));

    internal static bool IsInstalled(string target) =>
        File.Exists(Path.Combine(target, "manifest.json")) && PreloadOrder.All(n => File.Exists(Path.Combine(target, "bin", n)));

    /// <summary>Finds (or, when allowed, downloads) the supplement and preloads it. Must run before FoundryLocalManager.CreateAsync.</summary>
    internal static async Task<CudaSupplementStatus> EnsureAsync(string appData, IEnumerable<string> otherAppData, bool allowDownload, Action<string>? progress, CancellationToken ct)
    {
        const string Active = "NVIDIA cuDNN 9.26 and NVRTC 13.4 loaded for local models on the NVIDIA GPU.";
        if (_preloaded is not null) return new("active", Active, _preloaded);
        if (!Needed(appData)) return new("not_needed", "The CUDA provider bundle already includes the full cuDNN set.");
        string target = RuntimeDirectory(appData);
        if (!IsInstalled(target))
        {
            string? existing = otherAppData.Select(RuntimeDirectory).FirstOrDefault(IsInstalled);
            if (existing is not null) target = existing;
            else if (!allowDownload)
                return new("missing", "NVIDIA GPU libraries are not on this PC yet. Allow downloads to fetch NVIDIA cuDNN 9.26 and NVRTC 13.4 (about 210 MB).");
            else await InstallAsync(target, progress, ct);
        }
        Preload(target);
        return new("active", Active, Path.Combine(target, "bin"));
    }

    private static void Preload(string target)
    {
        string bin = Path.Combine(target, "bin");
        NativeMethods.AddDllDirectory(bin);
        foreach (string name in PreloadOrder)
        {
            IntPtr handle = NativeMethods.LoadLibraryExW(Path.Combine(bin, name), IntPtr.Zero,
                NativeMethods.LoadLibrarySearchDllLoadDir | NativeMethods.LoadLibrarySearchDefaultDirs);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException($"Could not load {name} from the NVIDIA GPU libraries (Win32 error {Marshal.GetLastWin32Error()}).");
            Handles.Add(handle);
        }
        _preloaded = bin;
    }

    private static async Task InstallAsync(string target, Action<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string staging = Path.Combine(Path.GetDirectoryName(target)!, ".staging-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(staging, "bin"));
        Directory.CreateDirectory(Path.Combine(staging, "licenses"));
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SurfaceDeviceKit/1.0 (+https://pypi.org)");
            foreach (var wheel in Arm64Wheels)
            {
                progress?.Invoke($"Looking up {wheel.Name}=={wheel.Version} on PyPI...");
                string meta = await http.GetStringAsync($"https://pypi.org/pypi/{wheel.Name}/{wheel.Version}/json", ct);
                string? url = (JsonNode.Parse(meta)?["urls"] as JsonArray)?
                    .FirstOrDefault(u => u?["filename"]?.GetValue<string>() == wheel.File)?["url"]?.GetValue<string>();
                if (url is null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"PyPI does not list {wheel.File}.");
                string file = Path.Combine(staging, wheel.File);
                await DownloadAsync(http, url, file, wheel.Name, progress, ct);
                string actual = Convert.ToHexStringLower(await HashAsync(file, ct));
                if (!string.Equals(actual, wheel.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{wheel.File} failed SHA-256 verification (got {actual}); nothing was installed.");
                using (var archive = ZipFile.OpenRead(file))
                {
                    foreach (var entry in archive.Entries)
                    {
                        string full = entry.FullName.Replace('\\', '/');
                        if (full.StartsWith(wheel.Prefix, StringComparison.Ordinal) && full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                            && !full[wheel.Prefix.Length..].Contains('/'))
                            entry.ExtractToFile(Path.Combine(staging, "bin", entry.Name), overwrite: true);
                        else if (full.EndsWith(".dist-info/licenses/LICENSE", StringComparison.Ordinal))
                            entry.ExtractToFile(Path.Combine(staging, "licenses", wheel.Name + "-LICENSE.txt"), overwrite: true);
                    }
                }
                File.Delete(file);
            }
            string[] missing = PreloadOrder.Where(n => !File.Exists(Path.Combine(staging, "bin", n))).ToArray();
            if (missing.Length > 0) throw new InvalidOperationException("The NVIDIA GPU libraries are incomplete: " + string.Join(", ", missing));
            var manifest = new JsonObject
            {
                ["tag"] = Tag,
                ["installed_at"] = DateTimeOffset.UtcNow.ToString("O"),
                ["wheels"] = new JsonArray(Arm64Wheels.Select(w => (JsonNode)new JsonObject { ["name"] = w.Name, ["version"] = w.Version, ["file"] = w.File, ["sha256"] = w.Sha256 }).ToArray()),
                ["reason"] = "Foundry Local CUDA provider bundle lacks cuDNN tensor-IR and convolution engine libraries.",
            };
            File.WriteAllText(Path.Combine(staging, "manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task DownloadAsync(HttpClient http, string url, string path, string label, Action<string>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(path);
        byte[] buffer = new byte[1 << 20];
        long done = 0;
        int lastPercent = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            int percent = total is > 0 ? (int)(done * 100 / total.Value) : -1;
            if (percent != lastPercent && percent % 5 == 0) progress?.Invoke($"Downloading {label}: {percent}%");
            lastPercent = percent;
        }
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, ct);
    }
}
