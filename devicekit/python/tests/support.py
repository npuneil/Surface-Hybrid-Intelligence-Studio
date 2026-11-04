import contextlib
import dataclasses
import io
import json
import os
import unittest
from functools import lru_cache
from typing import Any
from unittest.mock import patch

from surface_devicekit import simulate
from surface_devicekit.__main__ import main
from surface_devicekit.catalog import CATALOG_ENV, DATA_ROOT, DEFAULT_CATALOG_PATH, Catalog
from surface_devicekit.models import DeviceReport, RawGpu, RawHardware, RawNpu, SimulatedDevice
from surface_devicekit.service import SIMULATE_ENV, analyze

GOLDEN_PATH = DATA_ROOT / "vectors" / "golden-profiles.json"
PROFILES_PATH = simulate.DEFAULT_PROFILES_PATH
PACKAGE_ROOT = DATA_ROOT / "python"

SNAPDRAGON_X_ELITE_CPU = "Snapdragon(R) X Elite - X1E80100 - Qualcomm(R) Oryon(TM) CPU"
SNAPDRAGON_X_ELITE_NPU = "Snapdragon(R) X Elite - X1E80100 - Qualcomm(R) Hexagon(TM) NPU"
N1X_CPU = "NVIDIA RTX Spark N1X (6144-core GPU, 20-core CPU)"
N1X_GPU = "NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU)"


@lru_cache(maxsize=None)
def catalog() -> Catalog:
    return Catalog.load(DEFAULT_CATALOG_PATH)


def catalog_data() -> dict[str, Any]:
    return json.loads(DEFAULT_CATALOG_PATH.read_text(encoding="utf-8-sig"))


def catalog_with(edit) -> Catalog:
    """The shipped catalog after edit(data) has changed its JSON in place."""
    data = catalog_data()
    edit(data)
    return Catalog.parse(json.dumps(data))


def devices() -> tuple[SimulatedDevice, ...]:
    return simulate.all_devices()


def device(device_id: str) -> SimulatedDevice:
    found = simulate.find(device_id)
    if found is None:
        raise KeyError(device_id)
    return found


def raw_of(device_id: str, **changes: Any) -> RawHardware:
    return dataclasses.replace(device(device_id).raw, **changes)


def report_of(device_id: str) -> DeviceReport:
    sim = device(device_id)
    return analyze(sim.raw, catalog(), "simulated", sim.id, sim.label)


def analyze_raw(raw: RawHardware) -> DeviceReport:
    return analyze(raw, catalog())


def pc(cpu: str = SNAPDRAGON_X_ELITE_CPU, gpus: tuple[RawGpu, ...] = (), npus: tuple[RawNpu, ...] = (), **changes: Any) -> RawHardware:
    """A small 16 GB arm64 PC for tests that change one thing at a time."""
    base = RawHardware(
        manufacturer="Microsoft Corporation",
        product="Surface Test",
        cpu=cpu,
        logical_cores=12,
        os_version="Windows 11 25H2 (build 26200)",
        os_build=26200,
        os_arch="arm64",
        gpus=gpus,
        npus=npus,
        installed_ram_gb=16.0,
        visible_ram_gb=15.6,
        model_drive="C:",
        free_disk_gb=200.0,
        total_disk_gb=512.0,
    )
    return dataclasses.replace(base, **changes)


def run_cli(*args: str) -> tuple[int, str, str]:
    """The exit code, standard output and standard error of python -m surface_devicekit, run in this process."""
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = main(list(args))
    return code, out.getvalue(), err.getvalue()


class FakeProbe:
    """Stands in for the hardware probe: returns, or raises, each outcome in turn (the last one repeats) and counts its calls."""

    def __init__(self, *outcomes: RawHardware | Exception) -> None:
        self.outcomes = outcomes
        self.calls = 0

    def __call__(self) -> RawHardware:
        outcome = self.outcomes[min(self.calls, len(self.outcomes) - 1)]
        self.calls += 1
        if isinstance(outcome, Exception):
            raise outcome
        return outcome


class IsolatedEnvironment(unittest.TestCase):
    """Tests that read the environment start without the kit's own variables and leave the real environment as they found it."""

    def setUp(self) -> None:
        patcher = patch.dict(os.environ)
        patcher.start()
        self.addCleanup(patcher.stop)
        os.environ.pop(SIMULATE_ENV, None)
        os.environ.pop(CATALOG_ENV, None)
