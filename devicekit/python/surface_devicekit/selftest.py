"""Checks the catalog's internal consistency and a set of known-answer cases, so a change to the catalog or the rules that moves an answer is noticed."""

from __future__ import annotations

from collections.abc import Iterable
from dataclasses import dataclass

from .capabilities import LIMITED, UNAVAILABLE
from .catalog import Catalog
from .dotnet import format_double
from .models import DeviceReport, LaneInfo, SimulatedDevice, UnknownKeyError
from .service import analyze
from .simulate import all_devices

_KINDS = ("gpu-discrete", "gpu-unified", "gpu-integrated", "npu", "cpu")

# (device id, device class, PC class, lane, model alias) for the structured-json workload
_KNOWN_ANSWERS = (
    ("nvidia-n1x-128", "gpu-first", "builder", "gpu", "qwen2.5-coder-14b"),
    ("nvidia-n1x-64", "gpu-first", "builder", "gpu", "qwen2.5-coder-14b"),
    ("nvidia-n1x-32", "gpu-first", "builder", "gpu", "qwen2.5-coder-14b"),
    ("nvidia-dgpu-64", "gpu-first", "gpu", "gpu", "qwen2.5-7b"),
    ("nvidia-dgpu-32", "gpu-first", "gpu", "gpu", "qwen2.5-7b"),
    ("nvidia-dgpu-24", "gpu-first", "gpu", "gpu", "qwen2.5-7b"),
    ("nvidia-dgpu-16", "gpu-first", "gpu", "gpu", "phi-4-mini"),
    ("snapdragon-x-elite-64", "npu-first", "copilot-plus", "npu", "qwen2.5-7b"),
    ("snapdragon-x-elite-16", "npu-first", "copilot-plus", "npu", "qwen2.5-7b"),
    ("snapdragon-x2-plus-8", "npu-first", "npu", "npu", "qwen2.5-0.5b"),
    ("snapdragon-x-elite-16-emulated", "cpu-only", "copilot-plus", "cpu", "phi-4-mini"),
    ("snapdragon-sq3-16", "igpu-first", "windows", "gpu", "qwen2.5-7b"),
    ("intel-lunar-lake-16", "npu-first", "copilot-plus", "npu", "qwen2.5-7b"),
    ("intel-meteor-lake-16", "igpu-first", "npu", "gpu", "qwen2.5-7b"),
    ("intel-12th-gen-16", "igpu-first", "windows", "gpu", "qwen2.5-7b"),
)


@dataclass(frozen=True, slots=True)
class SelfTestResult:
    name: str
    passed: bool
    detail: str = ""


def run(catalog: Catalog | None = None, devices: Iterable[SimulatedDevice] | None = None) -> list[SelfTestResult]:
    if catalog is None:
        catalog = Catalog.default()
    pool = tuple(all_devices() if devices is None else devices)
    results: list[SelfTestResult] = []

    def check(name: str, ok: bool, detail: str = "") -> None:
        results.append(SelfTestResult(name, ok, detail))

    # Catalog consistency
    missing = [f"{w.id}:{alias}" for w in catalog.workloads.values() for alias in w.candidates if alias not in catalog.models]
    check("catalog: every workload candidate is a known model", not missing, ", ".join(missing))
    bad_from = [w.id for w in catalog.workloads.values() if w.available_from not in w.candidates]
    check("catalog: availableFrom is one of the candidates", not bad_from, ", ".join(bad_from))
    bad_order = list(dict.fromkeys(lane for order in catalog.lane_order.values() for lane in order if lane not in catalog.lanes))
    check("catalog: lane orders only name known lanes", not bad_order, ", ".join(bad_order))
    no_cap = [k for k in _KINDS if k not in catalog.model_classes.lane_cap or catalog.budget.rule(k).fraction <= 0]
    check("catalog: every lane kind has a class cap and a budget rule", not no_cap, ", ".join(no_cap))
    bad_class = [m.alias for m in catalog.models.values() if m.class_ not in catalog.model_classes.order]
    check("catalog: model classes are known", not bad_class, ", ".join(bad_class))
    check("simulated profile ids are unique", len({d.id for d in pool}) == len(pool))

    reports = {d.id: analyze(d.raw, catalog, "simulated", d.id, d.label) for d in pool}

    # Every device gets an answer, and every ladder obeys the rules
    no_json = [device_id for device_id, r in reports.items() if r.plans["structured-json"].primary is None]
    check("every simulated device has a structured-json model", not no_json, ", ".join(no_json))
    breaks: list[str] = []
    for device_id, r in reports.items():
        for workload_id, plan in r.plans.items():
            candidates = catalog.workloads[workload_id].candidates
            for lane in plan.lanes:
                cap = catalog.model_classes.rank(_lane(r, lane.lane).class_cap)
                last = -1
                for m in lane.ladder:
                    at = candidates.index(m.alias) if m.alias in candidates else -1
                    if m.required_gb > lane.budget_gb + 0.001 or catalog.model_classes.rank(m.class_) > cap or at <= last:
                        breaks.append(f"{device_id}/{workload_id}/{lane.lane}/{m.alias}")
                    last = at
    check("ladders are ordered, within the class cap and within budget", not breaks, ", ".join(breaks[:5]))

    # Known answers
    for device_id, device_class, pc_class, lane, alias in _KNOWN_ANSWERS:
        r = _report(reports, device_id)
        plan = r.plans["structured-json"]
        got_alias = plan.primary.alias if plan.primary is not None else None
        ok = r.profile.device_class == device_class and r.profile.pc_class == pc_class and plan.primary_lane == lane and got_alias == alias
        check(
            f"{device_id}: {device_class} / {pc_class} / {lane}:{alias}",
            ok,
            "" if ok else f"got {r.profile.device_class} / {r.profile.pc_class} / {plan.primary_lane or ''}:{got_alias or ''}",
        )

    n1x = _report(reports, "nvidia-n1x-128")
    npu, gpu, cpu = _lane(n1x, "npu"), _lane(n1x, "gpu"), _lane(n1x, "cpu")
    check("n1x: the NPU is listed but not used", len(n1x.profile.npus) == 1 and not n1x.profile.npus[0].llm_capable and not npu.available)
    check(
        "n1x: graphics memory comes from the GPU, not the visible RAM",
        gpu.budget_gb > 55 and cpu.budget_gb < 35,
        f"gpu {format_double(gpu.budget_gb)}, cpu {format_double(cpu.budget_gb)}",
    )
    check("n1x: the RAM tier uses installed memory", n1x.profile.memory.tier == 128)
    meteor = _report(reports, "intel-meteor-lake-16")
    check("meteor lake: a 11 TOPS NPU is not used for language models", not _lane(meteor, "npu").available)
    emulated = _report(reports, "snapdragon-x-elite-16-emulated")
    check("emulated build reports limited native-build", _capability(emulated, "native-build") == LIMITED)
    x2 = _report(reports, "snapdragon-x2-plus-8")
    check("x2 plus with 8 GB is not Copilot+ class", _capability(x2, "copilot-plus") == UNAVAILABLE)
    cpu_chat = [m.alias for m in next(p for p in emulated.plans["chat"].lanes if p.lane == "cpu").ladder]
    check("16 GB CPU lane budget: phi-4-mini fits, qwen2.5-7b does not", "phi-4-mini" in cpu_chat and "qwen2.5-7b" not in cpu_chat)
    return results


def _report(reports: dict[str, DeviceReport], device_id: str) -> DeviceReport:
    if device_id not in reports:
        raise UnknownKeyError(f"The given key '{device_id}' was not present in the dictionary.")
    return reports[device_id]


def _lane(report: DeviceReport, lane_id: str) -> LaneInfo:
    lane = report.profile.lane(lane_id)
    if lane is None:
        raise UnknownKeyError(f"The profile has no '{lane_id}' lane.")
    return lane


def _capability(report: DeviceReport, capability_id: str) -> str:
    return next(c.status for c in report.profile.capabilities if c.id == capability_id)
