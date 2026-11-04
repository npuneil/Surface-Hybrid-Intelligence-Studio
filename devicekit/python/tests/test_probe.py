import contextlib
import os
import subprocess
import sys
import sysconfig
import unittest
from types import SimpleNamespace
from unittest.mock import MagicMock, patch

from surface_devicekit import probe as probe_module
from surface_devicekit.jsonio import parse_raw, to_json
from surface_devicekit.models import RawGpu, RawNpu
from surface_devicekit.probe import PlatformNotSupportedError, probe
from surface_devicekit.service import analyze

from tests.support import PACKAGE_ROOT, catalog

GIB = 1024**3
ARM64, X64, X86 = 0xAA64, 0x8664, 0x014C

# Hides everything Windows-specific, imports every module of the package, then checks that the probe refuses to run.
IMPORT_WITHOUT_WINDOWS = """
import ctypes, importlib, pkgutil, sys
sys.modules["winreg"] = None
for name in ("WinDLL", "windll", "OleDLL", "oledll", "WinError", "FormatError", "GetLastError", "HRESULT"):
    if hasattr(ctypes, name):
        delattr(ctypes, name)
import surface_devicekit
for module in pkgutil.iter_modules(surface_devicekit.__path__):
    importlib.import_module("surface_devicekit." + module.name)
from surface_devicekit.probe import PlatformNotSupportedError, probe
sys.platform = "linux"
try:
    probe()
except PlatformNotSupportedError as error:
    print("refused:", error)
"""


def device(name: str, driver: str = "", provider: str = "", vram_bytes: int = 0) -> probe_module._Device:
    return probe_module._Device(name, driver, provider, vram_bytes)


@unittest.skipUnless(sys.platform == "win32", "the hardware probe reads Windows")
class ProbeSmokeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.system_drive = os.environ.get("SystemDrive", "C:")
        cls.raw = probe(model_dir=cls.system_drive + "\\")

    def test_the_operating_system_and_processor_are_plausible(self):
        raw = self.raw
        self.assertRegex(raw.os_version, r"^Windows 1[01]\b.*\(build \d+(\.\d+)?\)$")
        self.assertGreaterEqual(raw.os_build, 10240)
        self.assertIn(raw.os_arch, {"arm64", "x64", "x86", "arm"})
        self.assertGreaterEqual(raw.logical_cores, 1)
        self.assertTrue(raw.cpu)
        self.assertEqual(raw.cpu, " ".join(raw.cpu.split()))

    def test_the_process_architecture_is_that_of_this_interpreter(self):
        built_as = {"win-arm64": "arm64", "win-amd64": "x64", "win32": "x86"}.get(sysconfig.get_platform())
        if built_as is None:
            self.skipTest("an interpreter build this probe has no name for")
        self.assertEqual(self.raw.process_arch, built_as)

    def test_memory_and_disk_figures_are_plausible(self):
        raw = self.raw
        self.assertGreater(raw.visible_ram_gb, 0)
        if raw.installed_ram_gb:
            self.assertLessEqual(raw.visible_ram_gb, raw.installed_ram_gb)
        self.assertEqual(raw.model_drive.upper(), self.system_drive.upper())
        self.assertGreater(raw.total_disk_gb, 0)
        self.assertTrue(0 <= raw.free_disk_gb <= raw.total_disk_gb)

    def test_adapters_are_present_devices_with_clean_names(self):
        for gpu in self.raw.gpus:
            self.assertEqual(gpu.name, " ".join(gpu.name.split()))
            self.assertIsNone(probe_module._STALE_ADAPTER.search(gpu.name), gpu.name)
            self.assertGreaterEqual(gpu.vram_gb, 0)
        for npu in self.raw.npus:
            self.assertTrue(npu.name)

    def test_the_raw_hardware_survives_json_and_can_be_analyzed(self):
        self.assertEqual(parse_raw(to_json(self.raw)), self.raw)
        report = analyze(self.raw, catalog())
        self.assertEqual(report.profile.source, "detected")
        self.assertEqual(list(report.plans), list(catalog().workloads))


class DiskTests(unittest.TestCase):
    @unittest.skipUnless(sys.platform == "win32", "drive letters belong to Windows")
    def test_the_free_and_total_space_are_the_drive_rounded_to_two_decimals(self):
        usage = SimpleNamespace(total=int(476.944 * GIB), used=0, free=int(7.5 * GIB))
        with patch.object(probe_module.shutil, "disk_usage", return_value=usage) as disk_usage:
            self.assertEqual(probe_module._disk("D:\\models\\llama"), (7.5, 476.94, "D:"))
        disk_usage.assert_called_once_with("D:\\")

    @unittest.skipUnless(sys.platform == "win32", "drive letters belong to Windows")
    def test_a_drive_that_cannot_be_read_has_no_disk_figures(self):
        with patch.object(probe_module.shutil, "disk_usage", side_effect=OSError("not ready")):
            self.assertEqual(probe_module._disk("D:\\models"), (0.0, 0.0, ""))

    def test_a_path_that_is_not_on_a_lettered_drive_has_no_disk_figures(self):
        for path in ("", "\\\\server\\share\\models"):
            with self.subTest(path):
                self.assertEqual(probe_module._disk(path), (0.0, 0.0, ""))


class ProbeGuardTests(unittest.TestCase):
    def test_the_probe_refuses_to_run_anywhere_but_windows(self):
        with patch.object(sys, "platform", "linux"), self.assertRaises(PlatformNotSupportedError) as refused:
            probe()
        self.assertIn("needs Windows", str(refused.exception))

    def test_the_package_imports_on_a_host_without_the_windows_modules(self):
        done = subprocess.run([sys.executable, "-c", IMPORT_WITHOUT_WINDOWS], cwd=PACKAGE_ROOT, capture_output=True, text=True, timeout=60)
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(done.stdout.strip(), "refused: The hardware probe needs Windows. Pass a probe delegate or use a simulated device.")


class ArchitectureTests(unittest.TestCase):
    """An x64 Python on an ARM64 PC is emulated, yet platform.machine() still says ARM64: the build of the interpreter is the process."""

    def architectures(self, kernel, *, interpreter: str = "win-arm64", machine: str = "ARM64", environment: str | None = None):
        with (
            patch.dict(os.environ),
            patch.object(probe_module, "_kernel32", return_value=kernel),
            patch.object(probe_module.sysconfig, "get_platform", return_value=interpreter),
            patch.object(probe_module.platform, "machine", return_value=machine),
        ):
            os.environ.pop("PROCESSOR_ARCHITEW6432", None)
            if environment:
                os.environ["PROCESSOR_ARCHITEW6432"] = environment
            return probe_module._architectures()

    @staticmethod
    def wow64(process: int, native: int, succeeds: bool = True):
        """A kernel32 whose IsWow64Process2 reports these machine codes; 0 for the process means it is not under WOW64."""

        def is_wow64_process2(handle, process_ref, native_ref):
            process_ref._obj.value, native_ref._obj.value = process, native
            return int(succeeds)

        return SimpleNamespace(IsWow64Process2=MagicMock(side_effect=is_wow64_process2), GetCurrentProcess=lambda: 1)

    def test_a_native_process_has_the_architecture_of_the_machine(self):
        self.assertEqual(self.architectures(self.wow64(0, ARM64)), ("arm64", "arm64"))
        self.assertEqual(self.architectures(self.wow64(0, X64), interpreter="win-amd64", machine="AMD64"), ("x64", "x64"))

    def test_an_x64_python_on_arm64_is_an_x64_process_on_an_arm64_machine(self):
        self.assertEqual(self.architectures(self.wow64(0, ARM64), interpreter="win-amd64"), ("arm64", "x64"))

    def test_a_32_bit_process_on_a_64_bit_machine_is_the_wow64_machine(self):
        self.assertEqual(self.architectures(self.wow64(X86, X64), interpreter="win32", machine="AMD64"), ("x64", "x86"))

    def test_without_the_api_the_environment_and_then_platform_name_the_machine(self):
        failing = self.wow64(0, 0, succeeds=False)
        self.assertEqual(self.architectures(failing, interpreter="win-amd64", environment="ARM64"), ("arm64", "x64"))
        self.assertEqual(self.architectures(failing, interpreter="win-amd64", machine="AMD64"), ("x64", "x64"))
        absent = SimpleNamespace(GetCurrentProcess=lambda: 1)
        self.assertEqual(self.architectures(absent), ("arm64", "arm64"))

    def test_an_interpreter_build_that_has_no_name_takes_the_machine(self):
        self.assertEqual(self.architectures(self.wow64(0, X64), interpreter="mingw", machine="AMD64"), ("x64", "x64"))


class VideoMemoryTests(unittest.TestCase):
    REG_DWORD, REG_BINARY, REG_QWORD = 4, 3, 11
    WIDE, LEGACY = "HardwareInformation.qwMemorySize", "HardwareInformation.MemorySize"

    def video_memory(self, values: dict[str, tuple[object, int]]) -> int:
        registry = SimpleNamespace(REG_DWORD=self.REG_DWORD, REG_QWORD=self.REG_QWORD)
        with patch.object(probe_module, "_winreg", return_value=registry), patch.object(probe_module, "_read", side_effect=lambda key, name: values.get(name)):
            return probe_module._video_memory(object())

    def test_the_64_bit_value_holds_more_than_4_gb(self):
        self.assertEqual(self.video_memory({self.WIDE: (67108864000, self.REG_QWORD)}), 67108864000)

    def test_the_older_32_bit_value_is_used_only_when_the_64_bit_one_is_missing_or_zero(self):
        legacy = (2147483648, self.REG_DWORD)
        self.assertEqual(self.video_memory({self.LEGACY: legacy}), 2147483648)
        self.assertEqual(self.video_memory({self.WIDE: (0, self.REG_QWORD), self.LEGACY: legacy}), 2147483648)
        self.assertEqual(self.video_memory({self.WIDE: (1 << 33, self.REG_QWORD), self.LEGACY: legacy}), 1 << 33)

    def test_binary_values_are_read_little_endian_and_the_older_one_only_for_its_first_32_bits(self):
        eight = (1 << 33).to_bytes(8, "little")
        self.assertEqual(self.video_memory({self.WIDE: (eight, self.REG_BINARY)}), 1 << 33)
        self.assertEqual(self.video_memory({self.WIDE: ((3 << 29).to_bytes(4, "little"), self.REG_BINARY)}), 3 << 29)
        self.assertEqual(self.video_memory({self.LEGACY: (eight + b"\x01", self.REG_BINARY)}), 0)

    def test_a_driver_that_reports_nothing_gives_zero(self):
        self.assertEqual(self.video_memory({}), 0)


class ProbeAssemblyTests(unittest.TestCase):
    """probe() with the Windows calls replaced, so that sorting devices into GPUs and NPUs is checked on any host."""

    N1X = device("NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU)", "32.0.16.1742", "NVIDIA", 67108864000)

    def run_probe(self, display=(), compute=(), system=(), smi=None, smi_totals=(), **arguments):
        by_class = {probe_module._DISPLAY_CLASS: display, probe_module._COMPUTE_ACCELERATOR_CLASS: compute, probe_module._SYSTEM_CLASS: system}
        replacements = {
            "_present_devices": dict(side_effect=lambda guid: list(by_class[guid])),
            "_find_nvidia_smi": dict(return_value=smi),
            "_nvidia_smi_totals_gb": dict(return_value=list(smi_totals)),
            "_disk": dict(return_value=(150.0, 476.9, "D:")),
            "_power_point": dict(return_value=(True, "16")),
            "_windows_version": dict(return_value=("Windows 11 Pro 25H2 (build 26200.1)", 26200)),
            "_registry_text": dict(side_effect=lambda path, *names: list(names)),
            "_architectures": dict(return_value=("arm64", "arm64")),
            "_installed_ram_gb": dict(return_value=128.0),
            "_visible_ram_gb": dict(return_value=63.98),
        }
        with contextlib.ExitStack() as stack:
            stack.enter_context(patch.object(sys, "platform", "win32"))
            mocks = {name: stack.enter_context(patch.object(probe_module, name, **options)) for name, options in replacements.items()}
            return probe(**arguments), mocks

    def test_display_devices_become_gpus_with_their_video_memory(self):
        raw, _ = self.run_probe(display=[self.N1X])
        self.assertEqual(raw.gpus, (RawGpu(self.N1X.name, "32.0.16.1742", 62.5),))
        self.assertEqual(raw.npus, ())

    def test_virtual_and_stale_adapters_are_not_gpus(self):
        names = ("Microsoft Basic Render Driver", "Microsoft Basic Display Adapter", "Microsoft Remote Display Adapter", "Microsoft Hyper-V Video", "Virtual Display", "Indirect Display Device")
        raw, _ = self.run_probe(display=[device(name) for name in names] + [self.N1X])
        self.assertEqual([gpu.name for gpu in raw.gpus], [self.N1X.name])

    def test_npus_come_from_the_display_compute_and_system_classes_without_duplicates(self):
        display = [device("Snapdragon(R) X Elite - X1E80100 - Qualcomm(R) Hexagon(TM) NPU", "1.0", "Qualcomm")]
        compute = [device("SNAPDRAGON(R) X ELITE - X1E80100 - QUALCOMM(R) HEXAGON(TM) NPU"), device("Intel(R) AI Boost", "2.0", "Intel")]
        system = [
            device("Intel(R) NPU Accelerator", "3.0", "Intel"),
            device("intel(r) ai boost"),
            device("Intel(R) GNA Scoring Accelerator"),
            device("Neural Compute Stick"),
        ]
        raw, _ = self.run_probe(display=display, compute=compute, system=system)
        self.assertEqual(raw.gpus, ())
        self.assertEqual(
            raw.npus,
            (
                RawNpu("Snapdragon(R) X Elite - X1E80100 - Qualcomm(R) Hexagon(TM) NPU", "1.0", "Qualcomm"),
                RawNpu("Intel(R) AI Boost", "2.0", "Intel"),
                RawNpu("Intel(R) NPU Accelerator", "3.0", "Intel"),
            ),
        )

    def test_nvidia_smi_fills_in_only_the_nvidia_gpus_that_the_registry_gave_no_memory(self):
        silent = [device("NVIDIA GeForce RTX 4060 Laptop GPU", "31.0", "NVIDIA"), device("NVIDIA GeForce GTX 1650", "31.0", "NVIDIA")]
        intel = device("Intel(R) Arc(TM) Graphics", "32.0", "Intel")
        # nvidia-smi prints one line per NVIDIA GPU, so the n-th NVIDIA adapter takes the n-th line
        raw, mocks = self.run_probe(display=[self.N1X, *silent, intel], smi="C:\\Windows\\System32\\nvidia-smi.exe", smi_totals=[62.5, 8.0, 4.0])
        self.assertEqual([gpu.vram_gb for gpu in raw.gpus], [62.5, 8.0, 4.0, 0.0])
        self.assertTrue(raw.nvidia_smi)
        mocks["_nvidia_smi_totals_gb"].assert_called_once_with("C:\\Windows\\System32\\nvidia-smi.exe")

    def test_a_gpu_without_a_line_from_nvidia_smi_stays_at_zero(self):
        silent = [device("NVIDIA GeForce RTX 4060 Laptop GPU"), device("NVIDIA GeForce GTX 1650")]
        raw, _ = self.run_probe(display=silent, smi="nvidia-smi.exe", smi_totals=[8.0, None])
        self.assertEqual([gpu.vram_gb for gpu in raw.gpus], [8.0, 0.0])
        raw, _ = self.run_probe(display=silent, smi="nvidia-smi.exe", smi_totals=[8.0])
        self.assertEqual([gpu.vram_gb for gpu in raw.gpus], [8.0, 0.0])

    def test_nvidia_smi_is_not_run_for_gpus_that_already_have_memory(self):
        raw, mocks = self.run_probe(display=[self.N1X], smi="nvidia-smi.exe", smi_totals=[8.0])
        mocks["_nvidia_smi_totals_gb"].assert_not_called()
        self.assertEqual(raw.gpus[0].vram_gb, 62.5)

    def test_without_nvidia_smi_the_memory_stays_zero(self):
        raw, mocks = self.run_probe(display=[device("NVIDIA GeForce RTX 4060 Laptop GPU")])
        mocks["_nvidia_smi_totals_gb"].assert_not_called()
        self.assertEqual((raw.gpus[0].vram_gb, raw.nvidia_smi), (0.0, False))

    def test_the_other_facts_are_carried_over(self):
        raw, mocks = self.run_probe(display=[self.N1X], model_dir="E:\\models")
        self.assertEqual((raw.manufacturer, raw.product, raw.sku, raw.cpu), ("SystemManufacturer", "SystemProductName", "SystemSKU", "ProcessorNameString"))
        self.assertEqual((raw.os_version, raw.os_build, raw.os_arch, raw.process_arch), ("Windows 11 Pro 25H2 (build 26200.1)", 26200, "arm64", "arm64"))
        self.assertEqual((raw.installed_ram_gb, raw.visible_ram_gb), (128.0, 63.98))
        self.assertEqual((raw.model_drive, raw.free_disk_gb, raw.total_disk_gb), ("D:", 150.0, 476.9))
        self.assertEqual((raw.power_point, raw.power_point_version), (True, "16"))
        self.assertEqual(raw.logical_cores, os.cpu_count())
        mocks["_disk"].assert_called_once_with("E:\\models")

    def test_models_default_to_the_local_application_data_folder(self):
        with patch.dict(os.environ, {"LOCALAPPDATA": "F:\\Profile\\AppData\\Local"}):
            _, mocks = self.run_probe()
        mocks["_disk"].assert_called_once_with("F:\\Profile\\AppData\\Local")
        with patch.dict(os.environ):
            os.environ.pop("LOCALAPPDATA", None)
            _, mocks = self.run_probe()
        mocks["_disk"].assert_called_once_with(os.path.join(os.path.expanduser("~"), "AppData", "Local"))


class WindowsVersionTests(unittest.TestCase):
    def version(self, **values: str) -> tuple[str, int]:
        @contextlib.contextmanager
        def open_key(root, path, *, view64=True):
            yield object()

        with (
            patch.object(probe_module, "_winreg", return_value=SimpleNamespace(HKEY_LOCAL_MACHINE=0)),
            patch.object(probe_module, "_open_key", open_key),
            patch.object(probe_module, "_text", side_effect=lambda key, name: values.get(name, "")),
        ):
            return probe_module._windows_version()

    def test_windows_11_still_calls_itself_windows_10_in_the_registry(self):
        values = dict(ProductName="Windows 10 Pro", CurrentBuildNumber="26200", DisplayVersion="25H2", UBR="1234")
        self.assertEqual(self.version(**values), ("Windows 11 Pro 25H2 (build 26200.1234)", 26200))

    def test_the_build_number_decides_between_windows_10_and_11(self):
        self.assertEqual(self.version(ProductName="Windows 10 Home", CurrentBuildNumber="19045", DisplayVersion="22H2"), ("Windows 10 Home 22H2 (build 19045)", 19045))
        self.assertEqual(self.version(ProductName="Windows 10 Home", CurrentBuildNumber="22000")[1], 22000)
        self.assertTrue(self.version(ProductName="Windows 10 Home", CurrentBuildNumber="22000")[0].startswith("Windows 11 Home "))
        self.assertTrue(self.version(ProductName="Windows 11 Home", CurrentBuildNumber="21999")[0].startswith("Windows 10 Home "))

    def test_missing_values_leave_a_bare_name_and_build_zero(self):
        self.assertEqual(self.version(), ("Windows 10 (build 0)", 0))
        self.assertEqual(self.version(CurrentBuildNumber="not a number")[1], 0)


class NvidiaSmiTests(unittest.TestCase):
    def totals(self, stdout: str | None = None, error: Exception | None = None) -> list[float | None]:
        completed = SimpleNamespace(stdout=stdout)
        with (
            patch.object(probe_module.subprocess, "CREATE_NO_WINDOW", 0, create=True),
            patch.object(probe_module.subprocess, "run", side_effect=error, return_value=completed),
        ):
            return probe_module._nvidia_smi_totals_gb("nvidia-smi.exe")

    def test_each_line_is_megabytes_and_becomes_gigabytes(self):
        self.assertEqual(self.totals("64000\n"), [62.5])
        self.assertEqual(self.totals("\r\n 24576 \r\n8192\r\n"), [24.0, 8.0])

    def test_a_line_that_is_not_a_number_is_no_answer_for_that_gpu(self):
        for text in ("", "\n"):
            with self.subTest(text):
                self.assertEqual(self.totals(text), [])
        for text in ("N/A\n", "[N/A]", "12 GB"):
            with self.subTest(text):
                self.assertEqual(self.totals(text), [None])
        self.assertEqual(self.totals("[N/A]\n8192\n"), [None, 8.0])

    def test_a_tool_that_cannot_run_or_does_not_finish_is_no_answer(self):
        self.assertEqual(self.totals(error=OSError("not found")), [])
        self.assertEqual(self.totals(error=subprocess.TimeoutExpired("nvidia-smi", 4)), [])

    def test_nvidia_smi_is_found_on_the_path_first_then_in_the_known_folders(self):
        def isfile(found: set[str]):
            return patch.object(probe_module.os.path, "isfile", side_effect=lambda path: path in found)

        on_path = os.path.join("D:\\tools", "nvidia-smi.exe")
        with patch.dict(os.environ, {"PATH": ';"D:\\tools" ;C:\\other'}), isfile({on_path}):
            self.assertEqual(probe_module._find_nvidia_smi(), on_path)
        installed = r"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
        with patch.dict(os.environ, {"PATH": "C:\\other"}), patch.object(probe_module, "_system_directory", return_value="C:\\Windows\\System32"), isfile({installed}):
            self.assertEqual(probe_module._find_nvidia_smi(), installed)
        with patch.dict(os.environ, {"PATH": ""}), patch.object(probe_module, "_system_directory", return_value="C:\\Windows\\System32"), isfile(set()):
            self.assertIsNone(probe_module._find_nvidia_smi())
