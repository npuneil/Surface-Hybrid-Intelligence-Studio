"""The one object a demo talks to. It probes this PC once, can pretend to be any simulated device for display and planning, and turns either
into a DeviceReport: profile, capability matrix and per-workload model plans.

Set SURFACE_DEVICEKIT_SIMULATE to a profile id to start in simulation, and SURFACE_DEVICEKIT_CATALOG to a catalog file to replace the shipped one.
Every method is safe to call from several threads."""

from __future__ import annotations

import dataclasses
import os
import threading
from collections.abc import Callable, Iterable
from os import PathLike

from . import capabilities, planner, simulate
from .catalog import Catalog
from .classify import classify
from .dotnet import trim
from .jsonio import to_dict, to_json
from .models import DeviceReport, RawHardware, SimulatedDevice, UnknownKeyError, WorkloadPlan
from .probe import probe as probe_this_pc

__all__ = ["SIMULATE_ENV", "DeviceDetectionService", "analyze", "get_service", "to_dict", "to_json"]

SIMULATE_ENV = "SURFACE_DEVICEKIT_SIMULATE"

Listener = Callable[["DeviceDetectionService"], None]

_shared: DeviceDetectionService | None = None
_shared_lock = threading.Lock()


def analyze(
    raw: RawHardware, catalog: Catalog | None = None, source: str = "detected", simulated_id: str | None = None, label: str | None = None
) -> DeviceReport:
    """Classify raw hardware, then build the capability matrix and the plan for every workload. Pure: nothing from this PC is added."""
    if catalog is None:
        catalog = Catalog.default()
    profile = classify(raw, catalog, source, simulated_id, label)
    plans = planner.plan_all(profile, catalog)
    profile = dataclasses.replace(profile, capabilities=capabilities.build(profile, plans, catalog))
    return DeviceReport(profile, plans, catalog.catalog_version)


class DeviceDetectionService:
    def __init__(
        self,
        catalog: Catalog | None = None,
        probe: Callable[[], RawHardware] | None = None,
        model_dir: str | PathLike[str] | None = None,
        simulated_devices: Iterable[SimulatedDevice] | None = None,
    ) -> None:
        """probe replaces the probe of this PC, which is what tests and non-Windows hosts pass; model_dir is where models will be stored."""
        self.catalog = catalog if catalog is not None else Catalog.default()
        self._probe = probe if probe is not None else (lambda: probe_this_pc(model_dir))
        self._devices = tuple(simulated_devices) if simulated_devices is not None else None
        self._probe_lock = threading.RLock()
        self._state_lock = threading.RLock()
        self._detected: DeviceReport | None = None
        self._simulated: DeviceReport | None = None
        self._simulation: tuple[str, bool] | None = None  # the arguments of the active simulation, so a refresh can rebuild it
        self._listeners: list[Listener] = []

        requested = trim(os.environ.get(SIMULATE_ENV) or "")
        if requested and simulate.find(requested, self.simulated_devices) is not None:
            self.simulate(requested)

    @property
    def simulated_devices(self) -> tuple[SimulatedDevice, ...]:
        return self._devices if self._devices is not None else simulate.all_devices()

    @property
    def detected(self) -> DeviceReport:
        """What this PC really is. Probed on first use and then cached; a failed probe is not cached."""
        with self._probe_lock:
            if self._detected is None:
                self._detected = analyze(self._probe(), self.catalog)
            return self._detected

    @property
    def is_simulated(self) -> bool:
        with self._state_lock:
            return self._simulated is not None

    @property
    def effective(self) -> DeviceReport:
        """The simulated device if one is set, otherwise the detected one. Use this to decide what to show and which models to plan."""
        with self._state_lock:
            simulated = self._simulated
        return simulated if simulated is not None else self.detected

    def refresh(self) -> DeviceReport:
        """Probe again. An active simulation takes the new local facts (disk space, PowerPoint, nvidia-smi) too."""
        with self._probe_lock:
            self._detected = None
            report = self.detected
        with self._state_lock:
            active = self._simulation
        if active is not None:
            rebuilt = self._build_simulation(*active)
            with self._state_lock:
                if self._simulation == active:  # not changed by another thread while the report was being built
                    self._simulated = rebuilt
        self._notify()
        return report

    def simulate(self, device_id: str, keep_local_facts: bool = True) -> DeviceReport:
        """Pretend to be a simulated device.

        keep_local_facts: simulation covers the AI hardware only. Free disk space, PowerPoint and nvidia-smi still come from this PC,
        because that is what will really happen."""
        report = self._build_simulation(device_id, keep_local_facts)
        with self._state_lock:
            self._simulated = report
            self._simulation = (device_id, keep_local_facts)
        self._notify()
        return report

    def _build_simulation(self, device_id: str, keep_local_facts: bool) -> DeviceReport:
        sim = simulate.find(device_id, self.simulated_devices)
        if sim is None:
            raise UnknownKeyError(f"No simulated device '{device_id}'.")
        raw = sim.raw
        local = self._local_raw() if keep_local_facts else None
        if local is not None:
            raw = dataclasses.replace(
                raw,
                model_drive=local.model_drive,
                free_disk_gb=local.free_disk_gb,
                total_disk_gb=local.total_disk_gb,
                nvidia_smi=local.nvidia_smi,
                power_point=local.power_point,
                power_point_version=local.power_point_version,
            )
        return analyze(raw, self.catalog, "simulated", sim.id, sim.label)

    def clear_simulation(self) -> None:
        with self._state_lock:
            self._simulated = None
            self._simulation = None
        self._notify()

    def plan(self, workload: str) -> WorkloadPlan:
        plans = self.effective.plans
        if workload not in plans:
            raise UnknownKeyError(f"Unknown workload '{workload}'. Known: {', '.join(plans)}")
        return plans[workload]

    def subscribe(self, listener: Listener) -> Callable[[], None]:
        """Call listener(service) after the simulated device changes or the probe is refreshed. Returns the function that unsubscribes it."""
        with self._state_lock:
            self._listeners.append(listener)

        def unsubscribe() -> None:
            with self._state_lock:
                if listener in self._listeners:
                    self._listeners.remove(listener)

        return unsubscribe

    def _notify(self) -> None:
        with self._state_lock:
            listeners = tuple(self._listeners)
        for listener in listeners:
            listener(self)

    def _local_raw(self) -> RawHardware | None:
        try:
            return self.detected.profile.raw
        except Exception:  # a PC that cannot be probed still simulates, just without its local facts
            return None


def get_service() -> DeviceDetectionService:
    """A process-wide service, created on first use."""
    global _shared
    with _shared_lock:
        if _shared is None:
            _shared = DeviceDetectionService()
        return _shared
