"""Matches a device's plan with what Foundry Local really offers on it, and registers the execution providers its lanes need.

The static plans in planner.py say what should run where from the hardware alone. This module checks that against the live Foundry Local
catalog: a model counts only if the catalog has a build for the lane's device on a provider that is registered, and its real file size
(times the load overhead) fits the lane's memory budget. It is the Python twin of SurfaceDeviceKit.FoundryLocal's resolver.

The logic works on plain records (CatalogVariant), so it needs neither the Foundry Local SDK nor a PC. SdkModelSource and register_eps read
the objects the foundry_local_sdk package returns, by attribute, and import nothing."""

from __future__ import annotations

import re
import subprocess
import threading
from collections.abc import Callable, Collection, Iterable, Mapping, Sequence
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Protocol

from .catalog import Catalog
from .dotnet import fmt
from .models import DeviceReport, LaneInfo, UnknownKeyError

__all__ = [
    "CatalogVariant",
    "EpRegistration",
    "EpState",
    "ModelSource",
    "ResolvedModel",
    "ResolvedPlan",
    "SdkModelSource",
    "ep_cache_has",
    "ep_label",
    "norm_ep",
    "register_eps",
    "resolve",
    "resolve_model",
]

_EP_LABELS = {
    "cuda": "CUDA",
    "nvtensorrtrtx": "TensorRT for RTX",
    "qnn": "QNN",
    "openvino": "OpenVINO",
    "vitisai": "Vitis AI",
    "webgpu": "WebGPU",
    "dml": "DirectML",
    "cpu": "CPU",
}


def norm_ep(ep: str | None) -> str:
    """"CUDAExecutionProvider" and "cuda" both become "cuda"."""
    return (ep or "").lower().replace("executionprovider", "").replace("-", "").replace("_", "").strip()


def ep_label(ep: str | None) -> str:
    token = norm_ep(ep)
    return _EP_LABELS.get(token, token or "unknown")


@dataclass(frozen=True, slots=True)
class CatalogVariant:
    """One build of a model as Foundry Local lists it. device is NPU, GPU or CPU (empty when the catalog does not say); ep is normalised."""

    id: str
    alias: str
    device: str
    ep: str
    ep_name: str = ""
    file_mb: float | None = None
    version: int = 0
    cached: bool = False
    task: str = ""
    context_tokens: int | None = None


@dataclass(frozen=True, slots=True)
class ResolvedModel:
    """A model build that exists in this PC's live Foundry Local catalog, runs on one lane, and fits that lane's memory budget."""

    workload: str
    alias: str
    title: str
    class_: str
    params_b: float
    lane: str
    device: str
    ep: str
    ep_label: str
    ep_name: str
    variant_id: str
    file_gb: float
    required_gb: float
    cached: bool
    task: str
    context_tokens: int | None
    version: int
    thinks: bool

    @property
    def is_chat(self) -> bool:
        return not self.task or "chat" in self.task.lower()

    @property
    def summary(self) -> str:
        return f"{self.title} on the {self.device} ({self.ep_label}), {_gb(self.file_gb)}{', on this PC' if self.cached else ', needs a download'}"

    def describe(self) -> dict[str, Any]:
        return {
            "workload": self.workload,
            "alias": self.alias,
            "title": self.title,
            "class": self.class_,
            "lane": self.lane,
            "device": self.device,
            "ep": self.ep_label,
            "variant": self.variant_id,
            "fileGb": round(self.file_gb, 2),
            "requiredGb": round(self.required_gb, 2),
            "cached": self.cached,
            "task": self.task,
            "contextTokens": self.context_tokens,
        }


@dataclass(frozen=True, slots=True)
class ResolvedPlan:
    """Every model that can run on this PC for a workload, strongest first, plus why anything was left out."""

    workload: str
    title: str
    options: tuple[ResolvedModel, ...]
    notes: tuple[str, ...]

    @property
    def primary(self) -> ResolvedModel | None:
        return self.options[0] if self.options else None

    @property
    def primary_on_this_pc(self) -> ResolvedModel | None:
        """The strongest option that is already on this PC, so it can start without a download."""
        return next((o for o in self.options if o.cached), None)

    def describe(self) -> dict[str, Any]:
        return {"workload": self.workload, "title": self.title, "options": [o.describe() for o in self.options], "notes": list(self.notes)}


@dataclass(frozen=True, slots=True)
class EpState:
    """An execution provider the runtime offers on this PC."""

    token: str
    name: str
    registered: bool
    usable: bool
    installed: bool


@dataclass(slots=True)
class EpRegistration:
    """usable holds provider tokens (cpu is always there); lane_problems says, per lane, why none of its providers could be used."""

    usable: set[str] = field(default_factory=lambda: {"cpu"})
    notes: list[str] = field(default_factory=list)
    lane_problems: dict[str, str] = field(default_factory=dict)
    providers: list[EpState] = field(default_factory=list)


class ModelSource(Protocol):
    def variants(self, alias: str) -> Sequence[CatalogVariant] | None:
        """Every build of the model, or None when the alias is not in the catalog."""


def _gb(gb: float) -> str:
    return f"{gb:.0f} GB" if gb >= 10 else f"{fmt(gb, 1)} GB"


def resolve(
    report: DeviceReport,
    catalog: Catalog,
    workload: str,
    source: ModelSource,
    *,
    usable: Collection[str],
    failed: Collection[str] = (),
    lane_problems: Mapping[str, str] | None = None,
    prefer: Sequence[str] = (),
    lanes: Collection[str] | None = None,
) -> ResolvedPlan:
    """Lanes in this device's order, and inside a lane the candidates in order. prefer names models an app has tuned for: they are tried
    before the catalog's own candidates. lanes limits the lanes that may be used (a diagnostic, not a simulation)."""
    spec = catalog.workloads.get(workload)
    if spec is None:
        raise UnknownKeyError(f"Unknown workload '{workload}'. Known: {', '.join(catalog.workloads)}")
    candidates = [*prefer, *(c for c in spec.candidates if c not in prefer)]
    options: list[ResolvedModel] = []
    notes: list[str] = []
    for lane in report.profile.lanes:
        label = catalog.lanes[lane.id].label
        if lanes is not None and lane.id not in lanes:
            continue
        if not lane.available:
            if lane.id != "cpu" and lane.note:
                notes.append(f"{label}: {lane.note}")
            continue
        reasons: list[str] = []
        found = 0
        for alias in candidates:
            model, why = _resolve_on_lane(spec.id, alias, lane, catalog, source, usable, failed, enforce_class_cap=True)
            if model is not None:
                options.append(model)
                found += 1
            elif why is not None:
                reasons.append(why)
        if found:
            continue
        problem = (lane_problems or {}).get(lane.id)
        if problem:
            notes.append(f"{label}: {problem}")
        else:
            distinct = list(dict.fromkeys(reasons))[:2]
            notes.append(f"{label}: nothing in the list for this workload has a build that runs here. {' '.join(distinct)}".rstrip())
    return ResolvedPlan(spec.id, spec.title, tuple(options), tuple(notes))


def resolve_model(
    report: DeviceReport,
    catalog: Catalog,
    alias: str,
    lane_id: str,
    source: ModelSource,
    *,
    usable: Collection[str],
    failed: Collection[str] = (),
) -> tuple[ResolvedModel | None, str]:
    """One specific model on one lane: a model the user picked by hand, or a test that forces a class. The class cap is not enforced."""
    lane = report.profile.lane(lane_id)
    if lane is None:
        raise UnknownKeyError(f"No lane '{lane_id}'. Known: {', '.join(item.id for item in report.profile.lanes)}")
    if not lane.available:
        return None, lane.note or f"The {lane_id} lane is not available on this PC."
    model, why = _resolve_on_lane("manual", alias, lane, catalog, source, usable, failed, enforce_class_cap=False)
    return model, "" if model is not None else (why or f"{alias} cannot run on the {lane_id} lane.")


def _resolve_on_lane(
    workload: str,
    alias: str,
    lane: LaneInfo,
    catalog: Catalog,
    source: ModelSource,
    usable: Collection[str],
    failed: Collection[str],
    *,
    enforce_class_cap: bool,
) -> tuple[ResolvedModel | None, str | None]:
    spec = catalog.models.get(alias)
    title = spec.title if spec is not None and spec.title else alias
    if enforce_class_cap and spec is not None and catalog.model_classes.rank(spec.class_) > catalog.model_classes.rank(lane.class_cap):
        return None, None

    variants = source.variants(alias)
    if variants is None:
        return None, f"{title} is not in this PC's model catalog."

    device = catalog.lanes[lane.id].device_type
    lane_label = device.upper()
    best: CatalogVariant | None = None
    best_gb = 0.0
    best_score = -1
    why: str | None = None
    for variant in variants:
        if variant.device.upper() != device.upper():
            continue
        ep = norm_ep(variant.ep)
        if ep not in lane.eps:
            continue
        rank = lane.eps.index(ep)
        if ep not in usable:
            why = why or f"{title} needs the {ep_label(ep)} provider, which is not registered."
            continue
        if variant.id in failed:
            why = why or f"{title} did not start on the {lane_label} earlier in this run."
            continue

        gb = (variant.file_mb or 0) / 1024.0
        if gb <= 0 and spec is not None and (estimate := spec.size_for(lane.id)) is not None:
            gb = estimate[0]
        required = catalog.load_cost(gb)
        if required > lane.budget_gb:
            why = why or f"{title} needs {fmt(required, 1)} GB on the {lane_label}, which has {fmt(lane.budget_gb, 1)} GB."
            continue

        score = rank * 2 + (0 if variant.cached else 1)
        if best is None or score < best_score or (score == best_score and variant.version > best.version):
            best, best_score, best_gb = variant, score, gb
    if best is None:
        return None, why or f"{title} has no {lane_label} build on this PC."

    best_ep = norm_ep(best.ep)
    return (
        ResolvedModel(
            workload=workload,
            alias=alias,
            title=title,
            class_=spec.class_ if spec is not None else "?",
            params_b=spec.params if spec is not None else 0.0,
            lane=lane.id,
            device=lane_label,
            ep=best_ep,
            ep_label=ep_label(best_ep),
            ep_name=best.ep_name or best_ep,
            variant_id=best.id,
            file_gb=best_gb,
            required_gb=catalog.load_cost(best_gb),
            cached=best.cached,
            task=best.task,
            context_tokens=best.context_tokens if best.context_tokens and best.context_tokens > 0 else None,
            version=best.version,
            thinks=spec.thinks if spec is not None else False,
        ),
        None,
    )


class SdkModelSource:
    """Reads variants from a Foundry Local SDK catalog (foundry_local_sdk's Catalog), once per alias. A closed manager means a new instance."""

    def __init__(self, sdk_catalog: Any) -> None:
        self._catalog = sdk_catalog
        self._known: dict[str, tuple[CatalogVariant, ...] | None] = {}
        self._lock = threading.Lock()

    def variants(self, alias: str) -> tuple[CatalogVariant, ...] | None:
        with self._lock:
            if alias in self._known:
                return self._known[alias]
        rows = self._read(alias)
        with self._lock:
            return self._known.setdefault(alias, rows)

    def _read(self, alias: str) -> tuple[CatalogVariant, ...] | None:
        try:
            model = self._catalog.get_model(alias)
        except Exception:  # a catalog that cannot answer for one alias still answers for the others
            return None
        return None if model is None else tuple(variant_of(v, alias) for v in model.variants)

    def all_variants(self) -> list[CatalogVariant]:
        """Every build in the live catalog, for diagnostics."""
        rows: list[CatalogVariant] = []
        for model in self._catalog.list_models():
            rows.extend(variant_of(v, model.alias) for v in model.variants)
        return rows


def variant_of(sdk_variant: Any, alias: str) -> CatalogVariant:
    info = sdk_variant.info
    runtime = info.runtime
    device_type = getattr(runtime, "device_type", None) if runtime is not None else None
    device = str(getattr(device_type, "value", device_type) or "").upper()
    ep_name = getattr(runtime, "execution_provider", "") if runtime is not None else ""
    context = info.context_length
    return CatalogVariant(
        id=sdk_variant.id,
        alias=alias,
        device=device,
        ep=norm_ep(ep_name),
        ep_name=ep_name or "",
        file_mb=info.file_size_mb,
        version=int(info.version or 0),
        cached=bool(sdk_variant.is_cached),
        task=info.task or "",
        context_tokens=int(context) if context else None,
    )


# ----------------------------------------------------------------------------- execution providers

# Windows ML installs some providers system-wide (for example "MicrosoftCorporationII.WinML.Qualcomm.QNN.EP.1"); those register offline.
_WINML_PARTS = {"nvtensorrtrtx": "trtrtx", "openvino": "openvino", "qnn": "qnn", "vitisai": "vitisai"}
_winml_packages: list[str] | None = None
_winml_lock = threading.Lock()


def _list_winml_packages() -> list[str]:
    script = "Get-AppxPackage -Name 'MicrosoftCorporationII.WinML.*' | Select-Object -ExpandProperty Name"
    try:
        done = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.SubprocessError):
        return []
    return [re.sub(r"[^a-z0-9]", "", line.lower()) for line in done.stdout.splitlines() if line.strip()]


def winml_ep_installed(token: str) -> bool:
    part = _WINML_PARTS.get(token)
    if part is None:
        return False
    global _winml_packages
    with _winml_lock:
        if _winml_packages is None:
            _winml_packages = _list_winml_packages()
        packages = _winml_packages
    return any(part in name for name in packages)


def ep_cache_has(app_data: str | Path | None, token: str) -> bool:
    """True when the provider is already on this PC: installed system-wide by Windows ML, or downloaded into the app data folder's ep directory."""
    if winml_ep_installed(token):
        return True
    if app_data is None:
        return False
    root = Path(app_data) / "ep"
    if not root.is_dir():
        return False
    for directory in root.iterdir():
        if not directory.is_dir():
            continue
        name = re.sub(r"[^a-z0-9]", "", directory.name.lower())
        if name.endswith("ep"):
            name = name[:-2]
        if name == token:
            return True
    return False


def register_eps(
    manager: Any,
    report: DeviceReport,
    *,
    allow_download: bool,
    installed: Callable[[str], bool],
    progress: Callable[[str], None] | None = None,
    log: Callable[[str], None] | None = None,
    register_all: bool = False,
    lanes: Collection[str] | None = None,
) -> EpRegistration:
    """Registers, for each usable GPU or NPU lane of the REAL hardware, the first provider on its list that this PC can register (all of them
    when register_all is set). Providers the runtime already has cost nothing. One that is not there is downloaded only when allow_download
    is true; otherwise the lane gets a problem note that says so. Register before the catalog is read: the catalog lists only models the
    registered providers can run. lanes limits registration to those lanes (a diagnostic that forces a lane, never a simulation)."""
    result = EpRegistration()
    profile = report.profile
    active = [lane for lane in profile.usable_lanes if lane.id != "cpu" and (lanes is None or lane.id in lanes)]
    if not active:
        result.notes.append(
            "Limited to the CPU for diagnostics, so no GPU or NPU provider is registered."
            if lanes is not None and any(lane.id != "cpu" for lane in profile.usable_lanes)
            else "This app runs under x64 emulation here, so local models use the CPU. The native Arm64 build uses the GPU and NPU."
            if profile.os.emulated
            else "No GPU or NPU lane is available on this PC, so local models use the CPU."
        )
        return result

    say = progress or (lambda _text: None)
    note = log or (lambda _text: None)
    discovered = list(manager.discover_eps())
    wanted = {e for lane in active for e in lane.eps if e != "cpu"}

    for info in discovered:
        token = norm_ep(info.name)
        if info.is_registered and token in wanted and token not in result.usable:
            result.usable.add(token)
            result.notes.append(f"{ep_label(token)} ready.")

    for lane in active:
        tokens = [e for e in lane.eps if e != "cpu"]
        for token in tokens:
            if token in result.usable:
                if register_all:
                    continue
                break
            info = next((e for e in discovered if norm_ep(e.name) == token), None)
            if info is None:
                continue
            label = ep_label(token)
            if not allow_download and not installed(token):
                result.lane_problems.setdefault(lane.id, f"{label} is not installed on this PC yet. Allow downloads to add it.")
                continue
            say(f"Preparing {label}...")
            try:
                outcome = manager.download_and_register_eps(
                    names=[info.name], progress_callback=lambda name, percent: say(f"Preparing {ep_label(name)}: {percent:.0f}%")
                )
                registered = {n.lower() for n in (outcome.registered_eps or [])}
                if outcome.success or info.name.lower() in registered:
                    result.usable.add(token)
                    result.notes.append(f"{label} registered.")
                    result.lane_problems.pop(lane.id, None)
                    if not register_all:
                        break
                else:
                    result.notes.append(f"{label} registration reported '{outcome.status}'.")
                    result.lane_problems.setdefault(lane.id, f"{label} could not register ({outcome.status}).")
            except Exception as exc:  # one provider failing must not stop the others
                note(f"Register {info.name} failed: {exc}")
                first = str(exc).splitlines()[0][:220] if str(exc) else type(exc).__name__
                result.notes.append(f"{label} could not register: {first}")
                result.lane_problems.setdefault(lane.id, f"{label} could not register: {first}")
        if tokens and not any(t in result.usable for t in tokens) and lane.id not in result.lane_problems:
            labels = " or ".join(ep_label(t) for t in tokens)
            device = (profile.lane(lane.id) or lane).device or lane.id
            result.lane_problems[lane.id] = f"Foundry Local does not offer a {labels} provider for the {device}."

    try:
        discovered = list(manager.discover_eps())
    except Exception as exc:
        note(f"Discovering providers after registering failed: {exc}")
    result.providers = [
        EpState(norm_ep(e.name), e.name, bool(e.is_registered), norm_ep(e.name) in result.usable, installed(norm_ep(e.name)) if not e.is_registered else True)
        for e in discovered
    ]
    for line in result.notes:
        note("EP: " + line)
    return result


def missing_registrable(report: DeviceReport, registration: EpRegistration, discovered: Iterable[Any]) -> bool:
    """True when a provider a lane lists is offered by the runtime but not registered yet, so turning downloads on would add models."""
    names = {norm_ep(e.name) for e in discovered}
    wanted = {e for lane in report.profile.usable_lanes if lane.id != "cpu" for e in lane.eps if e != "cpu"}
    return any(token not in registration.usable and token in names for token in wanted)
