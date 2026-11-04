"""The data-only knowledge file (catalog/device-catalog.json): silicon families, lane budgets, EP preferences and model preferences per workload.

The same file is read by the C# port, so nothing here is a measured result."""

from __future__ import annotations

import os
import re
from dataclasses import dataclass, field
from functools import lru_cache
from pathlib import Path
from typing import Any

from .jsonio import JsonObject, loads, read_text

CATALOG_ENV = "SURFACE_DEVICEKIT_CATALOG"
DATA_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_CATALOG_PATH = DATA_ROOT / "catalog" / "device-catalog.json"


@lru_cache(maxsize=None)
def compile_ignore_case(pattern: str) -> re.Pattern[str]:
    return re.compile(pattern, re.IGNORECASE)


def _checked(pattern: str, where: str) -> str:
    try:
        compile_ignore_case(pattern)
    except re.error as error:
        raise ValueError(f"{where}: invalid regular expression ({error}).") from error
    return pattern


@dataclass(frozen=True, slots=True)
class Thresholds:
    npu_min_tops_for_llm: float = 40.0
    copilot_plus_min_ram_gb: float = 16.0
    copilot_plus_min_tops: float = 40.0
    gpu_min_budget_gb: float = 2.0
    model_drive_min_free_gb: float = 12.0


@dataclass(frozen=True, slots=True)
class BudgetOverhead:
    multiplier: float = 1.15
    constant_gb: float = 0.5


@dataclass(frozen=True, slots=True)
class BudgetRule:
    basis: str = "ram"  # vram or ram
    fraction: float = 1.0
    reserve_gb: float = 0.0


@dataclass(frozen=True, slots=True)
class BudgetSection:
    note: str = ""
    overhead: BudgetOverhead = BudgetOverhead()
    rules: dict[str, Any] = field(default_factory=dict)  # every key except note and overhead, read on demand like JsonExtensionData

    def rule(self, kind: str) -> BudgetRule:
        value = self.rules.get(kind)
        if value is None:
            return BudgetRule()
        o = JsonObject(value, f"$.budget.{kind}")
        return BudgetRule(o.text("basis", "ram"), o.number("fraction", 1.0), o.number("reserveGb"))


@dataclass(frozen=True, slots=True)
class ModelClassSection:
    order: tuple[str, ...] = ()
    describe: dict[str, str] = field(default_factory=dict)
    lane_cap: dict[str, str] = field(default_factory=dict)

    def rank(self, model_class: str) -> int:
        return self.order.index(model_class) if model_class in self.order else 0


@dataclass(frozen=True, slots=True)
class LaneSpec:
    label: str = ""
    device_type: str = ""


@dataclass(frozen=True, slots=True)
class EpSection:
    note: str = ""
    npu: dict[str, tuple[str, ...]] = field(default_factory=dict)
    gpu: dict[str, tuple[str, ...]] = field(default_factory=dict)
    cpu: dict[str, tuple[str, ...]] = field(default_factory=dict)


@dataclass(frozen=True, slots=True)
class SurfaceSection:
    product_pattern: str = "^Surface"
    strip_prefixes: tuple[str, ...] = ()
    strip_suffixes: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class SiliconNpu:
    vendor: str = ""
    rated_tops: float | None = None
    runtime: bool = False
    note: str | None = None


@dataclass(frozen=True, slots=True)
class SiliconSpec:
    id: str = ""
    label: str = ""
    vendor: str = ""
    arch: str = ""
    cpu_match: str = ""
    unified_memory: bool = False
    copilot_plus: str = "no"  # yes, config (depends on the SKU), listed (Microsoft lists it) or no
    copilot_plus_note: str | None = None  # where the "listed" claim comes from
    builder_pc: str | None = None  # "listed" when Microsoft names this silicon in its Builder PC class; the kit does not infer it from specs
    npu: SiliconNpu | None = None

    def matches(self, cpu_name: str) -> bool:
        return compile_ignore_case(self.cpu_match).search(cpu_name) is not None


@dataclass(frozen=True, slots=True)
class GpuFamilySpec:
    id: str = ""
    match: str = ""
    vendor: str = ""
    kind: str = "integrated"

    def matches(self, name: str) -> bool:
        return compile_ignore_case(self.match).search(name) is not None


@dataclass(frozen=True, slots=True)
class ModelSizes:
    gpu: float | None = None
    cpu: float | None = None


@dataclass(frozen=True, slots=True)
class ModelSpec:
    alias: str = ""
    title: str = ""
    params: float = 0.0
    class_: str = "S"
    task: str = "chat"
    thinks: bool = False
    tools: bool = False
    size_mb: ModelSizes = ModelSizes()

    def size_for(self, lane_id: str) -> tuple[float, bool] | None:
        """Weight file size in GB for a lane. NPU packages are not in the static catalog, so the GPU size stands in and is flagged as an estimate."""
        gpu, cpu = self.size_mb.gpu, self.size_mb.cpu
        if lane_id == "cpu":
            mb = cpu if cpu is not None else gpu
        else:
            mb = gpu if gpu is not None else cpu
        return None if mb is None else (mb / 1024.0, lane_id == "npu")


@dataclass(frozen=True, slots=True)
class WorkloadSpec:
    id: str = ""
    title: str = ""
    task: str = "chat"
    why: str = ""
    available_from: str = ""  # the weakest candidate that still counts as full capability; anything after it is reported as limited
    candidates: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class Catalog:
    schema: int = 0
    catalog_version: str = ""
    about: str = ""
    ram_tiers: tuple[int, ...] = ()
    tier_tolerance: float = 0.08
    thresholds: Thresholds = Thresholds()
    budget: BudgetSection = BudgetSection()
    model_classes: ModelClassSection = ModelClassSection()
    lanes: dict[str, LaneSpec] = field(default_factory=dict)
    lane_order: dict[str, tuple[str, ...]] = field(default_factory=dict)
    device_class_labels: dict[str, str] = field(default_factory=dict)
    pc_class_labels: dict[str, str] = field(default_factory=dict)
    eps: EpSection = EpSection()
    surface: SurfaceSection = SurfaceSection()
    silicon: tuple[SiliconSpec, ...] = ()
    gpu_families: tuple[GpuFamilySpec, ...] = ()
    models: dict[str, ModelSpec] = field(default_factory=dict)
    workloads: dict[str, WorkloadSpec] = field(default_factory=dict)

    @classmethod
    def parse(cls, text: str) -> Catalog:
        data = loads(text)
        if data is None:
            raise ValueError("The device catalog is empty.")
        o = JsonObject(data)
        return cls(
            schema=o.integer("schema"),
            catalog_version=o.text("catalogVersion"),
            about=o.text("about"),
            ram_tiers=tuple(o.integers("ramTiers")),
            tier_tolerance=o.number("tierTolerance", 0.08),
            thresholds=_read_thresholds(o.child("thresholds")),
            budget=_read_budget(o.child("budget")),
            model_classes=_read_model_classes(o.child("modelClasses")),
            lanes={key: LaneSpec(lane.text("label"), lane.text("deviceType")) for key, lane in o.mapping("lanes").items()},
            lane_order={key: tuple(ids) for key, ids in o.string_lists("laneOrder").items()},
            device_class_labels=o.string_map("deviceClassLabels"),
            pc_class_labels=o.string_map("pcClassLabels"),
            eps=_read_eps(o.child("eps")),
            surface=_read_surface(o.child("surface")),
            silicon=tuple(_read_silicon(s) for s in o.objects("silicon")),
            gpu_families=tuple(_read_gpu_family(g) for g in o.objects("gpuFamilies")),
            models={alias: _read_model(alias, m) for alias, m in o.mapping("models").items()},
            workloads={wid: _read_workload(wid, w) for wid, w in o.mapping("workloads").items()},
        )

    @classmethod
    def load(cls, path: str | os.PathLike[str]) -> Catalog:
        return cls.parse(read_text(path))

    @classmethod
    def default(cls) -> Catalog:
        """The catalog that ships beside this package, or the file named by SURFACE_DEVICEKIT_CATALOG.

        A variable that points at a missing file is an error, as in the C# kit, so a typo does not quietly fall back to the shipped catalog."""
        override = os.environ.get(CATALOG_ENV, "")
        if override.strip() and not Path(override).is_file():
            raise FileNotFoundError(f"{CATALOG_ENV} points at a file that does not exist: {override}")
        path = Path(override) if override.strip() else DEFAULT_CATALOG_PATH
        return _load_cached(str(path.resolve()))

    def match_silicon(self, cpu_name: str) -> SiliconSpec | None:
        return next((s for s in self.silicon if s.matches(cpu_name)), None)

    def match_gpu(self, name: str) -> GpuFamilySpec | None:
        return next((g for g in self.gpu_families if g.matches(name)), None)

    def ram_tier(self, gb: float) -> int:
        """The largest standard RAM size the installed amount reaches, within the rounding tolerance."""
        tier = 0
        for t in sorted(self.ram_tiers):
            if t <= gb * (1 + self.tier_tolerance):
                tier = t
        return tier

    def load_cost(self, size_gb: float) -> float:
        return size_gb * self.budget.overhead.multiplier + self.budget.overhead.constant_gb


@lru_cache(maxsize=None)
def _load_cached(path: str) -> Catalog:
    return Catalog.load(path)


def _read_thresholds(o: JsonObject | None) -> Thresholds:
    d = Thresholds()
    if o is None:
        return d
    return Thresholds(
        o.number("npuMinTopsForLlm", d.npu_min_tops_for_llm),
        o.number("copilotPlusMinRamGb", d.copilot_plus_min_ram_gb),
        o.number("copilotPlusMinTops", d.copilot_plus_min_tops),
        o.number("gpuMinBudgetGb", d.gpu_min_budget_gb),
        o.number("modelDriveMinFreeGb", d.model_drive_min_free_gb),
    )


def _read_budget(o: JsonObject | None) -> BudgetSection:
    if o is None:
        return BudgetSection()
    d = BudgetOverhead()
    overhead = o.child("overhead")
    if overhead is not None:
        d = BudgetOverhead(overhead.number("multiplier", d.multiplier), overhead.number("constantGb", d.constant_gb))
    return BudgetSection(o.text("note"), d, o.rest("note", "overhead"))


def _read_model_classes(o: JsonObject | None) -> ModelClassSection:
    if o is None:
        return ModelClassSection()
    return ModelClassSection(tuple(o.strings("order")), o.string_map("describe"), o.string_map("laneCap"))


def _read_eps(o: JsonObject | None) -> EpSection:
    if o is None:
        return EpSection()
    lists = {name: {key: tuple(ids) for key, ids in o.string_lists(name).items()} for name in ("npu", "gpu", "cpu")}
    return EpSection(o.text("note"), lists["npu"], lists["gpu"], lists["cpu"])


def _read_surface(o: JsonObject | None) -> SurfaceSection:
    if o is None:
        return SurfaceSection()
    return SurfaceSection(o.text("productPattern", "^Surface"), tuple(o.strings("stripPrefixes")), tuple(o.strings("stripSuffixes")))


def _read_silicon(o: JsonObject) -> SiliconSpec:
    npu = o.child("npu")
    return SiliconSpec(
        id=o.text("id"),
        label=o.text("label"),
        vendor=o.text("vendor"),
        arch=o.text("arch"),
        cpu_match=_checked(o.text("cpuMatch"), f"{o.path}.cpuMatch"),
        unified_memory=o.boolean("unifiedMemory"),
        copilot_plus=o.text("copilotPlus", "no"),
        copilot_plus_note=o.optional_text("copilotPlusNote"),
        builder_pc=o.optional_text("builderPc"),
        npu=None if npu is None else SiliconNpu(npu.text("vendor"), npu.optional_number("ratedTops"), npu.boolean("runtime"), npu.optional_text("note")),
    )


def _read_gpu_family(o: JsonObject) -> GpuFamilySpec:
    return GpuFamilySpec(o.text("id"), _checked(o.text("match"), f"{o.path}.match"), o.text("vendor"), o.text("kind", "integrated"))


def _read_model(alias: str, o: JsonObject) -> ModelSpec:
    sizes = o.child("sizeMb")
    return ModelSpec(
        alias=alias,
        title=o.text("title"),
        params=o.number("params"),
        class_=o.text("class", "S"),
        task=o.text("task", "chat"),
        thinks=o.boolean("thinks"),
        tools=o.boolean("tools"),
        size_mb=ModelSizes() if sizes is None else ModelSizes(sizes.optional_number("gpu"), sizes.optional_number("cpu")),
    )


def _read_workload(workload_id: str, o: JsonObject) -> WorkloadSpec:
    return WorkloadSpec(
        id=workload_id,
        title=o.text("title"),
        task=o.text("task", "chat"),
        why=o.text("why"),
        available_from=o.text("availableFrom"),
        candidates=tuple(o.strings("candidates")),
    )
