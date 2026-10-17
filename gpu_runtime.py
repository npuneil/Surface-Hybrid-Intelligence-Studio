"""GPU media runtime: completes cuDNN for speech, vision and embedding models, and measures memory.

The Foundry Local CUDA provider bundle for Windows ARM64 (CUDA 13.4.1, ONNX Runtime 1.30, GenAI 0.17.1) ships
cuDNN 9 without its convolution and tensor-IR engine sub-libraries. Language models never call cuDNN, so chat
works. Speech (convolution), vision (3-D convolution) and embedding (fused attention) graphs do, and cuDNN then
fails with CUDNN_STATUS_SUBLIBRARY_LOADING_FAILED. This module downloads NVIDIA's own redistributable wheels from
PyPI (pinned by version and SHA-256) into the app-data folder, and preloads the complete, version-matched set
before the provider loads. Nothing is installed into the Python environment and no DLL is committed to the repo.
"""
import ctypes
import hashlib
import json
import logging
import os
import platform
import shutil
import subprocess
import sys
import tempfile
import zipfile
from datetime import datetime, timezone
from pathlib import Path

TAG = "cudnn-9.26.1.1_nvrtc-13.4.92"
WHEELS = {
    "ARM64": (
        {"name": "nvidia-cudnn-cu13", "version": "9.26.1.1", "platform": "win_arm64",
         "file": "nvidia_cudnn_cu13-9.26.1.1-py3-none-win_arm64.whl",
         "sha256": "f1f2ec54a923ca02c9d8aee02a168da87067fae9ac0a408bb9ccf4936fe4a9f7",
         "prefix": "nvidia/cudnn/bin/"},
        {"name": "nvidia-cuda-nvrtc", "version": "13.4.92", "platform": "win_arm64",
         "file": "nvidia_cuda_nvrtc-13.4.92-py3-none-win_arm64.whl",
         "sha256": "1620066e967e93119d67338628935cbb1196b53a1474b07f14b7b77aba477284",
         "prefix": "nvidia/cu13/bin/arm64/"},
    ),
}
# Load order matters: NVRTC first (runtime-compiled engines), then cuDNN's core, graph and engine libraries.
PRELOAD = ("nvrtc64_130_0.dll", "nvrtc-builtins64_134.dll", "cudnn64_9.dll", "cudnn_graph64_9.dll",
           "cudnn_engines_tensor_ir64_9.dll", "cudnn_engines_precompiled64_9.dll",
           "cudnn_engines_runtime_compiled64_9.dll", "cudnn_heuristic64_9.dll", "cudnn_ops64_9.dll",
           "cudnn_cnn64_9.dll", "cudnn_adv64_9.dll", "cudnn_ext64_9.dll")
SENTINEL = "cudnn_cnn64_9.dll"
DOWNLOAD_HINT = ("Run Start-Showcase.ps1 -Prepare once to download NVIDIA's cuDNN 9.26 and NVRTC 13.4 "
                 "redistributables (about 210 MB) from PyPI.")

_handles = []
_dll_dirs = []
_preloaded = None


def architecture():
    machine = (os.environ.get("PROCESSOR_ARCHITEW6432") or platform.machine() or "").upper()
    return "ARM64" if machine in ("ARM64", "AARCH64") else machine


def runtime_dir(app_data):
    return Path(app_data) / "gpu-media-runtime" / TAG


def active_bundle_bin(app_data):
    """The bin folder of the CUDA provider bundle Foundry Local will load, or None if none is installed yet."""
    root = Path(app_data) / "ep" / "cuda-ep"
    try:
        name = (root / "active").read_text(encoding="utf-8").strip()
    except OSError:
        return None
    if not name or any(c in name for c in '\\/:*?"<>|') or name in (".", ".."):
        return None
    folder = root / "bundles" / name / "bin"
    return folder if folder.is_dir() else None


def supplement_needed(app_data):
    folder = active_bundle_bin(app_data)
    return folder is None or not (folder / SENTINEL).is_file()


def is_installed(target):
    target = Path(target)
    return (target / "manifest.json").is_file() and all((target / "bin" / name).is_file() for name in PRELOAD)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def extract(wheel, spec, staging):
    """Copy only the pinned wheel's DLLs and license into staging; refuse a wheel whose hash differs."""
    actual = sha256(wheel)
    if actual != spec["sha256"]:
        raise RuntimeError(f"{spec['file']} failed SHA-256 verification (got {actual}); nothing was installed.")
    copied = []
    with zipfile.ZipFile(wheel) as archive:
        for member in archive.namelist():
            name = member.rsplit("/", 1)[-1]
            if member.startswith(spec["prefix"]) and name.lower().endswith(".dll") and "/" not in member[len(spec["prefix"]):]:
                with archive.open(member) as source, open(staging / "bin" / name, "wb") as out:
                    shutil.copyfileobj(source, out)
                copied.append(name)
            elif member.endswith(".dist-info/licenses/LICENSE"):
                with archive.open(member) as source, open(staging / "licenses" / f"{spec['name']}-LICENSE.txt", "wb") as out:
                    shutil.copyfileobj(source, out)
    return copied


def install(target, python=None, log=logging.info):
    specs = WHEELS.get(architecture())
    if not specs:
        raise RuntimeError(f"The GPU media supplement is only verified on Windows ARM64 (this is {architecture()}).")
    target = Path(target)
    target.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=".staging-", dir=target.parent))
    try:
        (staging / "bin").mkdir()
        (staging / "licenses").mkdir()
        downloads = staging / "wheels"
        downloads.mkdir()
        for spec in specs:
            log(f"Downloading {spec['name']}=={spec['version']} from PyPI")
            subprocess.run([python or sys.executable, "-m", "pip", "download", "--disable-pip-version-check",
                            "--no-deps", "--only-binary=:all:", "--platform", spec["platform"],
                            "--dest", str(downloads), f"{spec['name']}=={spec['version']}"],
                           check=True, capture_output=True, text=True, timeout=1800)
            wheel = downloads / spec["file"]
            if not wheel.is_file():
                raise RuntimeError(f"pip did not produce the pinned wheel {spec['file']}.")
            extract(wheel, spec, staging)
        shutil.rmtree(downloads)
        missing = [name for name in PRELOAD if not (staging / "bin" / name).is_file()]
        if missing:
            raise RuntimeError("The supplement is incomplete: " + ", ".join(missing))
        (staging / "manifest.json").write_text(json.dumps({
            "tag": TAG, "installed_at": datetime.now(timezone.utc).isoformat(),
            "wheels": [{k: spec[k] for k in ("name", "version", "file", "sha256")} for spec in specs],
            "reason": "Foundry Local CUDA provider bundle lacks cuDNN convolution and tensor-IR engine libraries.",
        }, indent=2), encoding="utf-8")
        if target.exists():
            shutil.rmtree(target)
        staging.rename(target)
    except subprocess.CalledProcessError as exc:
        raise RuntimeError(f"pip download failed: {(exc.stderr or exc.stdout or '').strip()[-400:]}") from exc
    finally:
        if staging.exists():
            shutil.rmtree(staging, ignore_errors=True)


def preload(target):
    """Load the complete cuDNN set by full path so the provider's own imports bind to these versions."""
    global _preloaded
    if _preloaded is not None:
        return
    folder = Path(target) / "bin"
    _dll_dirs.append(os.add_dll_directory(str(folder)))
    for name in PRELOAD:
        _handles.append(ctypes.WinDLL(str(folder / name)))
    _preloaded = str(folder)


def ensure(app_data, allow_download):
    """Prepare and preload the supplement when needed. Must run before Foundry Local loads the CUDA provider."""
    base = {"version": TAG, "path": None}
    if os.name != "nt":
        return base | {"state": "unsupported", "detail": "The GPU media supplement is Windows-only."}
    if _preloaded is not None:
        return base | {"state": "active", "path": _preloaded,
                       "detail": "NVIDIA cuDNN 9.26 and NVRTC 13.4 supplement loaded for speech, vision and embeddings."}
    if not supplement_needed(app_data):
        return base | {"state": "not_needed", "detail": "The CUDA provider bundle already includes the full cuDNN set."}
    target = runtime_dir(app_data)
    if not is_installed(target):
        if not allow_download:
            return base | {"state": "missing", "detail": "GPU media libraries are not prepared. " + DOWNLOAD_HINT}
        install(target)
    preload(target)
    return base | {"state": "active", "path": str(target / "bin"),
                   "detail": "NVIDIA cuDNN 9.26 and NVRTC 13.4 supplement loaded for speech, vision and embeddings."}


class _MemoryCounters(ctypes.Structure):
    _fields_ = [("cb", ctypes.c_ulong), ("PageFaultCount", ctypes.c_ulong),
                ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t),
                ("PrivateUsage", ctypes.c_size_t)]


def process_private_bytes():
    if os.name != "nt":
        return None
    counters = _MemoryCounters()
    counters.cb = ctypes.sizeof(counters)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.GetCurrentProcess.restype = ctypes.c_void_p
    kernel32.K32GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ulong]
    if not kernel32.K32GetProcessMemoryInfo(kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
        return None
    return int(counters.PrivateUsage)


def gpu_memory_usage(pid=None):
    """GPU memory from the Windows WDDM counters.

    Returns this process's dedicated and shared (system-RAM spill) usage, plus the dedicated and shared usage
    of the busiest adapter as a whole, which includes every other app using that GPU.
    """
    pid = os.getpid() if pid is None else int(pid)
    command = (
        "$ErrorActionPreference='Stop';"
        "$p=(Get-Counter '\\GPU Process Memory(*)\\Dedicated Usage','\\GPU Process Memory(*)\\Shared Usage').CounterSamples;"
        f"$m=@($p | Where-Object {{ $_.InstanceName -like 'pid_{pid}_*' }});"
        "$md=($m | Where-Object { $_.Path -like '*\\dedicated usage' } | Measure-Object CookedValue -Sum).Sum;"
        "$ms=($m | Where-Object { $_.Path -like '*\\shared usage' } | Measure-Object CookedValue -Sum).Sum;"
        "$a=(Get-Counter '\\GPU Adapter Memory(*)\\Dedicated Usage','\\GPU Adapter Memory(*)\\Shared Usage').CounterSamples;"
        "$ad=$a | Where-Object { $_.Path -like '*\\dedicated usage' } | Sort-Object CookedValue -Descending | Select-Object -First 1;"
        "$as=($a | Where-Object { $_.InstanceName -eq $ad.InstanceName -and $_.Path -like '*\\shared usage' } | "
        "Measure-Object CookedValue -Sum).Sum;"
        "@{md=[long]$md; ms=[long]$ms; ad=[long]$ad.CookedValue; as=[long]$as} | ConvertTo-Json -Compress"
    )
    try:
        result = subprocess.run(["powershell.exe", "-NoProfile", "-Command", command],
                                capture_output=True, text=True, check=True, timeout=20)
        data = json.loads(result.stdout)
        return {"process_dedicated_bytes": int(data["md"]), "process_shared_bytes": int(data["ms"]),
                "adapter_dedicated_bytes": int(data["ad"]), "adapter_shared_bytes": int(data["as"]),
                "source": "Windows performance counters: GPU Process Memory and GPU Adapter Memory"}
    except (OSError, subprocess.SubprocessError, ValueError, KeyError, TypeError) as exc:
        logging.warning("GPU memory counters unavailable: %s", exc)
        return None
