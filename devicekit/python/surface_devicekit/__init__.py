"""Surface device kit for Python: what this PC can run, and which local models to use on it.

    from surface_devicekit import DeviceDetectionService, to_dict

    service = DeviceDetectionService()
    report = service.effective       # the simulated device if one is set, otherwise this PC
    payload = to_dict(report)        # JSON-ready, shaped like the C# DeviceJson output

The classification, planning and capability rules are a line-for-line port of the C# SurfaceDeviceKit and read the same device-catalog.json.
The golden vectors in vectors/golden-profiles.json pin both ports to the same answers."""

from __future__ import annotations

from . import capabilities, catalog, classify, foundry, golden, models, planner, probe, report, selftest, service, simulate
from .catalog import Catalog
from .jsonio import parse_raw, to_dict, to_json
from .models import DeviceProfile, DeviceReport, RawHardware, SimulatedDevice, UnknownKeyError, WorkloadPlan
from .report import render
from .service import DeviceDetectionService, analyze, get_service

__all__ = [
    "Catalog",
    "DeviceDetectionService",
    "DeviceProfile",
    "DeviceReport",
    "RawHardware",
    "SimulatedDevice",
    "UnknownKeyError",
    "WorkloadPlan",
    "analyze",
    "capabilities",
    "catalog",
    "classify",
    "foundry",
    "get_service",
    "golden",
    "models",
    "parse_raw",
    "planner",
    "probe",
    "render",
    "report",
    "selftest",
    "service",
    "simulate",
    "to_dict",
    "to_json",
]
