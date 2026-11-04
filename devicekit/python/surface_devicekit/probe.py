"""Reads the hardware facts from Windows: registry, SetupAPI (present devices only) and a few Win32 calls.
No WMI, no admin rights, no network. The only process it may start is nvidia-smi, and only when the registry has no video memory figure.

Everything Windows-specific is imported or loaded inside a function, so this module imports on any platform; probe() raises
PlatformNotSupportedError anywhere else."""

from __future__ import annotations

import contextlib
import ctypes
import dataclasses
import os
import platform
import re
import shutil
import subprocess
import sys
import sysconfig
import uuid
from collections.abc import Iterator
from functools import lru_cache
from os import PathLike
from typing import Any, NamedTuple

from .catalog import compile_ignore_case
from .dotnet import clean, equals_ignore_case, round_half_even, trim
from .models import RawGpu, RawHardware, RawNpu

_DISPLAY_CLASS = "4d36e968-e325-11ce-bfc1-08002be10318"
_SYSTEM_CLASS = "4d36e97d-e325-11ce-bfc1-08002be10318"
_COMPUTE_ACCELERATOR_CLASS = "f01a9d53-3ff6-48d2-9f97-c8a7004be10c"
# "Neural" is not used: Intel's GNA (Gaussian & Neural Accelerator) is a low-power audio block, not an NPU.
_NPU_NAME = compile_ignore_case(r"\bNPU\b|AI Boost|Hexagon|\bIPU\b")
_STALE_ADAPTER = compile_ignore_case(r"Basic (Render|Display)|Remote Display|Hyper-V|Virtual|Indirect Display")
_FLOAT = re.compile(r"[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?")
_INTEGER = re.compile(r"[+-]?[0-9]+")

_GIB = 1024.0 * 1024 * 1024
_DIGCF_PRESENT = 0x2
_SPDRP_DEVICEDESC = 0x0
_SPDRP_DRIVER = 0x9
_INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value
_CLASS_KEY = r"SYSTEM\CurrentControlSet\Control\Class" + "\\"
_MACHINE_NAMES = {0xAA64: "arm64", 0x8664: "x64", 0x014C: "x86", 0x01C4: "arm"}
_ENVIRONMENT_ARCHITECTURES = {"ARM64": "arm64", "AMD64": "x64", "X86": "x86", "ARM": "arm"}
_PLATFORM_ARCHITECTURES = {"win-arm64": "arm64", "win-amd64": "x64", "win32": "x86", "win-arm32": "arm"}


class PlatformNotSupportedError(NotImplementedError):
    """The hardware probe was asked to run somewhere other than Windows."""


class _GUID(ctypes.Structure):
    _fields_ = [("Data1", ctypes.c_uint32), ("Data2", ctypes.c_uint16), ("Data3", ctypes.c_uint16), ("Data4", ctypes.c_ubyte * 8)]


class _SP_DEVINFO_DATA(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.c_uint32), ("ClassGuid", _GUID), ("DevInst", ctypes.c_uint32), ("Reserved", ctypes.c_void_p)]


class _MEMORYSTATUSEX(ctypes.Structure):
    _fields_ = [("dwLength", ctypes.c_uint32), ("dwMemoryLoad", ctypes.c_uint32)] + [
        (name, ctypes.c_uint64)
        for name in ("ullTotalPhys", "ullAvailPhys", "ullTotalPageFile", "ullAvailPageFile", "ullTotalVirtual", "ullAvailVirtual", "ullAvailExtendedVirtual")
    ]


class _Device(NamedTuple):
    name: str
    driver: str
    provider: str
    vram_bytes: int


def probe(model_dir: str | PathLike[str] | None = None) -> RawHardware:
    """The raw hardware facts of this PC. model_dir is where models will be stored; only its drive matters (for the free-space check)."""
    if sys.platform != "win32":
        raise PlatformNotSupportedError("The hardware probe needs Windows. Pass a probe delegate or use a simulated device.")

    gpus: list[RawGpu] = []
    npus: list[RawNpu] = []
    for d in _present_devices(_DISPLAY_CLASS):
        if _NPU_NAME.search(d.name):
            npus.append(RawNpu(d.name, d.driver, d.provider))
        elif not _STALE_ADAPTER.search(d.name):
            gpus.append(RawGpu(d.name, d.driver, round_half_even(d.vram_bytes / _GIB)))
    for d in _present_devices(_COMPUTE_ACCELERATOR_CLASS):
        if not any(equals_ignore_case(n.name, d.name) for n in npus):
            npus.append(RawNpu(d.name, d.driver, d.provider))
    # Before Windows 11 24H2 an NPU is listed under System devices instead.
    for d in _present_devices(_SYSTEM_CLASS):
        if _NPU_NAME.search(d.name) and not any(equals_ignore_case(n.name, d.name) for n in npus):
            npus.append(RawNpu(d.name, d.driver, d.provider))

    smi = _find_nvidia_smi()
    if smi is not None and any(_needs_vram_from_smi(gpu) for gpu in gpus):
        # nvidia-smi lists NVIDIA GPUs only, so the n-th NVIDIA adapter here is its n-th line.
        totals = _nvidia_smi_totals_gb(smi)
        nth = 0
        for i, gpu in enumerate(gpus):
            if not _is_nvidia(gpu):
                continue
            if gpu.vram_gb <= 0 and nth < len(totals) and totals[nth] is not None:
                gpus[i] = dataclasses.replace(gpu, vram_gb=totals[nth])
            nth += 1

    if model_dir is None:
        model_dir = os.environ.get("LOCALAPPDATA") or os.path.join(os.path.expanduser("~"), "AppData", "Local")
    free, total_disk, drive = _disk(os.fspath(model_dir))
    power_point, power_point_version = _power_point()
    os_version, os_build = _windows_version()
    manufacturer, product, sku = _registry_text(r"HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer", "SystemProductName", "SystemSKU")
    (cpu,) = _registry_text(r"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")
    os_arch, process_arch = _architectures()

    return RawHardware(
        manufacturer=manufacturer,
        product=product,
        sku=sku,
        cpu=cpu,
        logical_cores=os.cpu_count() or 0,
        os_version=os_version,
        os_build=os_build,
        os_arch=os_arch,
        process_arch=process_arch,
        gpus=tuple(gpus),
        npus=tuple(npus),
        installed_ram_gb=_installed_ram_gb(),
        visible_ram_gb=_visible_ram_gb(),
        model_drive=drive,
        free_disk_gb=free,
        total_disk_gb=total_disk,
        nvidia_smi=smi is not None,
        power_point=power_point,
        power_point_version=power_point_version,
    )


def _winreg() -> Any:
    import winreg

    return winreg


@contextlib.contextmanager
def _open_key(root: int, path: str, *, view64: bool = True) -> Iterator[Any]:
    """The key for reading in the 64-bit (or 32-bit) registry view, or None when it cannot be opened."""
    winreg = _winreg()
    try:
        key = winreg.OpenKey(root, path, 0, winreg.KEY_READ | (winreg.KEY_WOW64_64KEY if view64 else winreg.KEY_WOW64_32KEY))
    except OSError:
        yield None
        return
    with key:
        yield key


def _read(key: Any, name: str) -> tuple[Any, int] | None:
    if key is None:
        return None
    try:
        return _winreg().QueryValueEx(key, name)
    except OSError:
        return None


def _signed(value: int, bits: int) -> int:
    return value - (1 << bits) if value >= 1 << (bits - 1) else value


def _text(key: Any, name: str) -> str:
    """RegistryKey.GetValue(name)?.ToString() ?? "": the text of a string or number value, empty for a missing or binary one."""
    item = _read(key, name)
    if item is None:
        return ""
    value, kind = item
    winreg = _winreg()
    if isinstance(value, str):
        return winreg.ExpandEnvironmentStrings(value) if kind == winreg.REG_EXPAND_SZ else value
    if isinstance(value, int):
        return str(_signed(value, 32 if kind == winreg.REG_DWORD else 64))
    return ""


def _registry_text(path: str, *names: str) -> list[str]:
    with _open_key(_winreg().HKEY_LOCAL_MACHINE, path) as key:
        return [clean(_text(key, name)) for name in names]


def _memory_value(item: tuple[Any, int] | None, *, wide: bool) -> int:
    """Video memory in bytes from a DWORD, a QWORD or a binary value; the older MemorySize value only ever holds 32 bits."""
    if item is None:
        return 0
    value, kind = item
    if isinstance(value, int):
        return value if kind == _winreg().REG_DWORD else _signed(value, 64)
    if isinstance(value, (bytes, bytearray)):
        if wide and len(value) >= 8:
            return int.from_bytes(value[:8], "little", signed=True)
        if len(value) >= 4:
            return int.from_bytes(value[:4], "little")
    return 0


def _video_memory(key: Any) -> int:
    memory = _memory_value(_read(key, "HardwareInformation.qwMemorySize"), wide=True)
    return memory if memory != 0 else _memory_value(_read(key, "HardwareInformation.MemorySize"), wide=False)


@lru_cache(maxsize=1)
def _setupapi() -> Any:
    dll = ctypes.WinDLL("setupapi")
    pointer, uint32, void_p = ctypes.POINTER, ctypes.c_uint32, ctypes.c_void_p
    dll.SetupDiGetClassDevsW.argtypes = [pointer(_GUID), ctypes.c_wchar_p, void_p, uint32]
    dll.SetupDiGetClassDevsW.restype = void_p
    dll.SetupDiEnumDeviceInfo.argtypes = [void_p, uint32, pointer(_SP_DEVINFO_DATA)]
    dll.SetupDiEnumDeviceInfo.restype = ctypes.c_int
    dll.SetupDiGetDeviceRegistryPropertyW.argtypes = [void_p, pointer(_SP_DEVINFO_DATA), uint32, pointer(uint32), void_p, uint32, pointer(uint32)]
    dll.SetupDiGetDeviceRegistryPropertyW.restype = ctypes.c_int
    dll.SetupDiDestroyDeviceInfoList.argtypes = [void_p]
    dll.SetupDiDestroyDeviceInfoList.restype = ctypes.c_int
    return dll


@lru_cache(maxsize=1)
def _kernel32() -> Any:
    dll = ctypes.WinDLL("kernel32")
    dll.GetPhysicallyInstalledSystemMemory.argtypes = [ctypes.POINTER(ctypes.c_uint64)]
    dll.GetPhysicallyInstalledSystemMemory.restype = ctypes.c_int
    dll.GlobalMemoryStatusEx.argtypes = [ctypes.POINTER(_MEMORYSTATUSEX)]
    dll.GlobalMemoryStatusEx.restype = ctypes.c_int
    dll.GetCurrentProcess.argtypes = []
    dll.GetCurrentProcess.restype = ctypes.c_void_p
    dll.GetSystemDirectoryW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32]
    dll.GetSystemDirectoryW.restype = ctypes.c_uint32
    return dll


def _property(api: Any, handle: int, data: _SP_DEVINFO_DATA, which: int) -> str:
    needed = ctypes.c_uint32(0)
    api.SetupDiGetDeviceRegistryPropertyW(handle, ctypes.byref(data), which, None, None, 0, ctypes.byref(needed))
    if needed.value == 0 or needed.value > 4096:
        return ""
    buffer = (ctypes.c_ubyte * needed.value)()
    if not api.SetupDiGetDeviceRegistryPropertyW(handle, ctypes.byref(data), which, None, buffer, needed.value, None):
        return ""
    return bytes(buffer).decode("utf-16-le", errors="replace").rstrip("\0")


def _present_devices(class_guid: str) -> list[_Device]:
    """Devices of one class that are present right now, so adapters left behind in the registry by old drivers are ignored."""
    api = _setupapi()
    guid = _GUID.from_buffer_copy(uuid.UUID(class_guid).bytes_le)
    handle = api.SetupDiGetClassDevsW(ctypes.byref(guid), None, None, _DIGCF_PRESENT)
    if handle is None or handle == _INVALID_HANDLE_VALUE:
        return []

    devices: list[_Device] = []
    try:
        index = 0
        while True:
            data = _SP_DEVINFO_DATA()
            data.cbSize = ctypes.sizeof(_SP_DEVINFO_DATA)
            if not api.SetupDiEnumDeviceInfo(handle, index, ctypes.byref(data)):
                break
            index += 1

            driver_key = _property(api, handle, data, _SPDRP_DRIVER)
            name, driver, provider, vram = _property(api, handle, data, _SPDRP_DEVICEDESC), "", "", 0
            if driver_key:
                with _open_key(_winreg().HKEY_LOCAL_MACHINE, _CLASS_KEY + driver_key) as key:
                    if key is not None:
                        name = _text(key, "DriverDesc") or name
                        driver = _text(key, "DriverVersion")
                        provider = _text(key, "ProviderName")
                        vram = _video_memory(key)
            name = clean(name)
            if name:
                devices.append(_Device(name, driver, provider, vram))
    finally:
        api.SetupDiDestroyDeviceInfoList(handle)
    return devices


def _installed_ram_gb() -> float:
    try:
        kilobytes = ctypes.c_uint64(0)
        if _kernel32().GetPhysicallyInstalledSystemMemory(ctypes.byref(kilobytes)):
            return round_half_even(kilobytes.value / 1024.0 / 1024.0)
    except (OSError, AttributeError):
        pass
    return 0.0


def _visible_ram_gb() -> float:
    status = _MEMORYSTATUSEX()
    status.dwLength = ctypes.sizeof(_MEMORYSTATUSEX)
    return round_half_even(status.ullTotalPhys / _GIB) if _kernel32().GlobalMemoryStatusEx(ctypes.byref(status)) else 0.0


def _architectures() -> tuple[str, str]:
    """The names of the machine's and the process's architecture.

    platform.machine() names the machine even inside an x64 Python emulated on ARM64, and IsWow64Process2 reports a process machine only for
    32-bit x86 processes, never for x64 on ARM64. So unless it does report one, the build of this interpreter says what the process is."""
    machine = wow64 = None
    try:
        kernel32 = _kernel32()
        is_wow64_process2 = kernel32.IsWow64Process2  # missing before Windows 10 1709
        is_wow64_process2.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint16), ctypes.POINTER(ctypes.c_uint16)]
        is_wow64_process2.restype = ctypes.c_int
        process, native = ctypes.c_uint16(0), ctypes.c_uint16(0)
        if is_wow64_process2(kernel32.GetCurrentProcess(), ctypes.byref(process), ctypes.byref(native)):
            machine = _MACHINE_NAMES.get(native.value)
            wow64 = _MACHINE_NAMES.get(process.value)  # 0 (IMAGE_FILE_MACHINE_UNKNOWN) when the process is not under WOW64
    except (OSError, AttributeError):
        pass
    if machine is None:
        named = os.environ.get("PROCESSOR_ARCHITEW6432") or platform.machine()
        machine = _ENVIRONMENT_ARCHITECTURES.get(named.upper(), named.lower())
    return machine, wow64 or _PLATFORM_ARCHITECTURES.get(sysconfig.get_platform()) or machine


def _parse_int(text: str) -> int:
    text = trim(text)
    value = int(text) if _INTEGER.fullmatch(text) else 0
    return value if -(1 << 31) <= value < (1 << 31) else 0


def _windows_version() -> tuple[str, int]:
    with _open_key(_winreg().HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Windows NT\CurrentVersion") as key:
        build = _parse_int(_text(key, "CurrentBuildNumber"))
        display = _text(key, "DisplayVersion")
        ubr = _text(key, "UBR")
        # ProductName still says "Windows 10" on Windows 11, so take only the edition from it.
        edition = trim(re.sub(r"^Windows\s+1[01]\s*", "", _text(key, "ProductName")))
    name = "Windows 11" if build >= 22000 else "Windows 10"
    text = trim(f"{name} {edition} {display}".replace("  ", " "))
    return f"{text} (build {build}{'.' + ubr if ubr else ''})", build


def _disk(path: str) -> tuple[float, float, str]:
    """Free and total gigabytes of the drive that holds path, and its name ("C:"); zeros when that is not a lettered drive."""
    try:
        drive = os.path.splitdrive(os.path.abspath(path))[0] if path else ""
        if not re.fullmatch(r"[A-Za-z]:", drive):
            return 0.0, 0.0, ""
        usage = shutil.disk_usage(drive + "\\")
        return round_half_even(usage.free / _GIB), round_half_even(usage.total / _GIB), drive
    except (OSError, ValueError):
        return 0.0, 0.0, ""


def _power_point() -> tuple[bool, str]:
    winreg = _winreg()
    for view64 in (True, False):
        with _open_key(winreg.HKEY_CLASSES_ROOT, r"PowerPoint.Application\CurVer", view64=view64) as key:
            if key is None:
                continue
            with _open_key(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Office\ClickToRun\Configuration") as click_to_run:
                version = _text(click_to_run, "VersionToReport")
            if not version:
                match = re.search(r"\d+$", _text(key, ""))
                version = match.group() if match else ""
            return True, version
    return False, ""


def _system_directory() -> str:
    buffer = ctypes.create_unicode_buffer(260)
    length = _kernel32().GetSystemDirectoryW(buffer, len(buffer))
    return buffer.value if 0 < length < len(buffer) else os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32")


def _find_nvidia_smi() -> str | None:
    for directory in (os.environ.get("PATH") or "").split(";"):
        if directory:
            candidate = os.path.join(trim(directory).strip('"'), "nvidia-smi.exe")
            if os.path.isfile(candidate):
                return candidate
    for candidate in (os.path.join(_system_directory(), "nvidia-smi.exe"), r"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"):
        if os.path.isfile(candidate):
            return candidate
    return None


def _is_nvidia(gpu: RawGpu) -> bool:
    return "nvidia" in gpu.name.lower()


def _needs_vram_from_smi(gpu: RawGpu) -> bool:
    return gpu.vram_gb <= 0 and _is_nvidia(gpu)


def _nvidia_smi_totals_gb(smi: str) -> list[float | None]:
    """Video memory in GB for each NVIDIA GPU, in the order nvidia-smi prints them (None for a line that is not a number)."""
    try:
        done = subprocess.run(
            [smi, "--query-gpu=memory.total", "--format=csv,noheader,nounits"],
            capture_output=True,
            text=True,
            errors="replace",
            stdin=subprocess.DEVNULL,
            timeout=4,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
    except (OSError, subprocess.SubprocessError, ValueError):
        return []
    lines = [line for line in map(trim, done.stdout.split("\n")) if line]
    return [round_half_even(float(line) / 1024.0) if _FLOAT.fullmatch(line) else None for line in lines]
