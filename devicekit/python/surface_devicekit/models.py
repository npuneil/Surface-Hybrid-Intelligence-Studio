"""The records passed between the stages: raw hardware facts in, a profile and plans out. Field order is the JSON key order."""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field


class UnknownKeyError(KeyError):
    """A KeyError that prints its message as written instead of its repr, for lookups the caller can fix (an unknown workload or device id)."""

    def __str__(self) -> str:
        return str(self.args[0]) if self.args else ""


@dataclass(frozen=True, slots=True)
class RawGpu:
    name: str = ""
    driver: str = ""
    vram_gb: float = 0.0


@dataclass(frozen=True, slots=True)
class RawNpu:
    name: str = ""
    driver: str = ""
    provider: str = ""


@dataclass(frozen=True, slots=True)
class RawHardware:
    """What the probe collected. The classifier turns this, and nothing else, into a DeviceProfile, so a real PC and a simulated one share all code."""

    manufacturer: str = ""
    product: str = ""
    sku: str = ""
    cpu: str = ""
    logical_cores: int = 0
    os_version: str = ""
    os_build: int = 0
    os_arch: str = "x64"  # the machine, not the process: arm64, x64 or x86
    process_arch: str = ""  # empty means the same as os_arch; a different value means emulation
    gpus: tuple[RawGpu, ...] = ()
    npus: tuple[RawNpu, ...] = ()
    installed_ram_gb: float = 0.0  # 0 when Windows would not say
    visible_ram_gb: float = 0.0
    model_drive: str = ""
    free_disk_gb: float = 0.0
    total_disk_gb: float = 0.0
    nvidia_smi: bool = False
    power_point: bool = False
    power_point_version: str = ""


@dataclass(frozen=True, slots=True)
class OsInfo:
    version: str
    build: int
    arch: str
    process_arch: str
    emulated: bool


@dataclass(frozen=True, slots=True)
class CpuInfo:
    name: str
    vendor: str
    family_id: str
    family_label: str
    arch: str
    logical_cores: int
    unified_memory: bool


@dataclass(frozen=True, slots=True)
class GpuInfo:
    """kind is discrete, unified (one memory pool shared with the CPU, reported as dedicated) or integrated."""

    name: str
    vendor: str
    kind: str
    family_id: str
    vram_gb: float
    driver: str


@dataclass(frozen=True, slots=True)
class NpuInfo:
    name: str
    vendor: str
    driver: str
    rated_tops: float | None
    rated_source: str
    runtime_supported: bool
    llm_capable: bool
    note: str


@dataclass(frozen=True, slots=True)
class MemoryInfo:
    installed_gb: float
    visible_gb: float
    tier: int


@dataclass(frozen=True, slots=True)
class StorageInfo:
    drive: str
    free_gb: float
    total_gb: float


@dataclass(frozen=True, slots=True)
class SoftwareInfo:
    nvidia_smi: bool
    power_point: bool
    power_point_version: str


@dataclass(frozen=True, slots=True)
class LaneInfo:
    """One place a model can run: npu, gpu or cpu."""

    id: str
    kind: str
    available: bool
    device: str
    vendor: str
    eps: tuple[str, ...]
    budget_gb: float
    class_cap: str
    rated_tops: float | None
    note: str


@dataclass(frozen=True, slots=True)
class Capability:
    """group is lane, workload or platform; status is available, limited or unavailable.

    basis says where the claim comes from: detected (read from this PC), estimated (detected memory against catalog model sizes),
    vendor-rated (a vendor figure nothing here measures) or catalog (a lookup)."""

    id: str
    group: str
    title: str
    status: str
    lane: str
    detail: str
    basis: str


@dataclass(frozen=True, slots=True)
class ModelChoice:
    alias: str
    title: str
    class_: str
    params_b: float
    size_gb: float
    size_estimated: bool
    required_gb: float


@dataclass(frozen=True, slots=True)
class LanePlan:
    lane: str
    kind: str
    budget_gb: float
    choice: ModelChoice | None
    ladder: tuple[ModelChoice, ...]
    reason: str


@dataclass(frozen=True, slots=True)
class WorkloadPlan:
    workload: str
    title: str
    why: str
    primary_lane: str | None
    primary: ModelChoice | None
    lanes: tuple[LanePlan, ...]
    summary: str


@dataclass(frozen=True, slots=True)
class DeviceProfile:
    source: str = "detected"  # detected or simulated
    simulated_id: str | None = None
    label: str = ""
    headline: str = ""
    manufacturer: str = ""
    product: str = ""
    sku: str = ""
    is_surface: bool = False
    os: OsInfo = OsInfo("", 0, "", "", False)
    cpu: CpuInfo = CpuInfo("", "", "", "", "", 0, False)
    gpus: tuple[GpuInfo, ...] = ()
    npus: tuple[NpuInfo, ...] = ()
    memory: MemoryInfo = MemoryInfo(0.0, 0.0, 0)
    storage: StorageInfo = StorageInfo("", 0.0, 0.0)
    software: SoftwareInfo = SoftwareInfo(False, False, "")
    pc_class: str = "windows"
    pc_class_label: str = ""
    device_class: str = "cpu-only"
    device_class_label: str = ""
    lanes: tuple[LaneInfo, ...] = ()
    capabilities: tuple[Capability, ...] = ()
    raw: RawHardware | None = field(default=None, metadata={"json": False})

    @property
    def usable_lanes(self) -> tuple[LaneInfo, ...]:
        """Lanes that can run a model, in the order this device prefers."""
        return tuple(lane for lane in self.lanes if lane.available)

    def lane(self, lane_id: str) -> LaneInfo | None:
        return next((lane for lane in self.lanes if lane.id == lane_id), None)


@dataclass(frozen=True, slots=True)
class DeviceReport:
    """The whole answer for one device: who it is, what it can do, and what to run for each workload."""

    profile: DeviceProfile
    plans: Mapping[str, WorkloadPlan]
    catalog_version: str


@dataclass(frozen=True, slots=True)
class SimulatedDevice:
    id: str
    group: str
    label: str
    note: str
    raw: RawHardware
