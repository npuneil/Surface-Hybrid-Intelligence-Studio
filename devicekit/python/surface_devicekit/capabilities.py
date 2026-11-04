"""What this device can do, in plain words. Every entry says where it comes from (see Capability), so a demo never has to claim more than was
detected, estimated or vendor-rated."""

from __future__ import annotations

from collections.abc import Mapping, Sequence

from .catalog import Catalog, WorkloadSpec
from .dotnet import fmt
from .models import Capability, DeviceProfile, LaneInfo, WorkloadPlan

AVAILABLE = "available"
LIMITED = "limited"
UNAVAILABLE = "unavailable"


def build(profile: DeviceProfile, plans: Mapping[str, WorkloadPlan], catalog: Catalog) -> tuple[Capability, ...]:
    npu, gpu, cpu = profile.lane("npu"), profile.lane("gpu"), profile.lane("cpu")
    items = [
        _npu_capability(profile, npu),
        _gpu_capability(gpu),
        Capability(
            "cpu-inference", "lane", "CPU language models", AVAILABLE, "cpu",
            f"{cpu.device}: {fmt(cpu.budget_gb, 1)} GB of RAM available for model weights. {cpu.note}", "estimated",
        ),
    ]
    items.extend(_workload_capability(plans[workload_id], workload) for workload_id, workload in catalog.workloads.items())
    items.append(_large_models(plans["chat"], profile, catalog))
    items.extend((_copilot_plus(profile, catalog), _storage(profile, catalog), _native_build(profile), _powerpoint(profile)))
    return tuple(items)


def _index(items: Sequence[str], item: str) -> int:
    return items.index(item) if item in items else -1


def _npu_capability(p: DeviceProfile, npu: LaneInfo) -> Capability:
    shown = next((n for n in p.npus if n.llm_capable), None)
    if shown is None and p.npus:
        shown = p.npus[0]
    title = "NPU language models"
    if npu.available and shown is not None:
        runtime = " or ".join(npu.eps).upper()
        return Capability("npu-inference", "lane", title, AVAILABLE, "npu", f"{shown.name}. {shown.note} Runtime: {runtime} through Foundry Local.", "vendor-rated")
    if shown is None:
        return Capability("npu-inference", "lane", title, UNAVAILABLE, "", npu.note, "detected")
    status = LIMITED if shown.runtime_supported and shown.rated_tops is not None else UNAVAILABLE
    return Capability("npu-inference", "lane", title, status, "", f"{shown.name}. {shown.note}", "detected" if shown.rated_tops is None else "vendor-rated")


def _gpu_capability(gpu: LaneInfo) -> Capability:
    title = "GPU language models"
    if not gpu.available:
        return Capability("gpu-inference", "lane", title, UNAVAILABLE, "", gpu.note, "detected")
    strong = gpu.kind != "gpu-integrated"
    return Capability(
        "gpu-inference", "lane", title, AVAILABLE if strong else LIMITED, "gpu",
        f"{gpu.device}: {fmt(gpu.budget_gb, 1)} GB for model weights, up to {gpu.class_cap}-class models. {gpu.note}",
        "detected" if strong else "estimated",
    )


def _workload_capability(plan: WorkloadPlan, workload: WorkloadSpec) -> Capability:
    pick = plan.primary
    if pick is None or plan.primary_lane is None:
        return Capability(workload.id, "workload", workload.title, UNAVAILABLE, "", plan.summary, "estimated")
    have = _index(workload.candidates, pick.alias)
    need = _index(workload.candidates, workload.available_from)
    status = AVAILABLE if need < 0 or have <= need else LIMITED
    return Capability(workload.id, "workload", workload.title, status, plan.primary_lane, plan.summary, "estimated")


def _large_models(chat: WorkloadPlan, p: DeviceProfile, catalog: Catalog) -> Capability:
    title = "Large models (12B and up)"
    classes = catalog.model_classes
    large = classes.rank("L")
    for lane_plan in chat.lanes:
        if lane_plan.choice is None:
            continue
        pick = next((m for m in lane_plan.ladder if classes.rank(m.class_) >= large), None)
        if pick is not None:
            return Capability(
                "large-models", "workload", title, AVAILABLE, lane_plan.lane,
                f"{pick.title} fits on the {catalog.lanes[lane_plan.lane].label}: "
                f"it needs about {fmt(pick.required_gb, 1)} GB and the lane has {fmt(lane_plan.budget_gb, 1)} GB.",
                "estimated",
            )

    costs: list[float] = []
    for alias in catalog.workloads["chat"].candidates:
        model = catalog.models.get(alias)
        if model is None:
            continue
        size = model.size_for("gpu")
        if classes.rank(model.class_) >= large and size is not None:
            costs.append(catalog.load_cost(size[0]))
    need = min(costs, default=0.0)
    best = max((lane.budget_gb for lane in p.lanes if lane.available), default=0.0)
    return Capability(
        "large-models", "workload", title, UNAVAILABLE, "",
        f"A 12B-class model needs about {fmt(need, 1)} GB on a GPU. The best lane here has {fmt(best, 1)} GB, so the plan stays with smaller models.",
        "estimated",
    )


def _copilot_plus(p: DeviceProfile, catalog: Catalog) -> Capability:
    th = catalog.thresholds
    title = "Copilot+ PC class"
    silicon = next((s for s in catalog.silicon if s.id == p.cpu.family_id), None)
    best = max((n.rated_tops if n.rated_tops is not None else 0.0 for n in p.npus), default=0.0)

    if silicon is None or silicon.copilot_plus == "no":
        return Capability("copilot-plus", "platform", title, UNAVAILABLE, "", f"{p.cpu.family_label} is not a Copilot+ PC class chip.", "catalog")
    if p.memory.tier < th.copilot_plus_min_ram_gb:
        return Capability(
            "copilot-plus", "platform", title, UNAVAILABLE, "",
            f"Copilot+ PCs need at least {fmt(th.copilot_plus_min_ram_gb)} GB of RAM; this one has {p.memory.tier} GB.", "detected",
        )
    if silicon.copilot_plus == "listed":
        source = silicon.copilot_plus_note if silicon.copilot_plus_note is not None else f"Microsoft lists {p.cpu.family_label} devices as Copilot+ PCs."
        return Capability("copilot-plus", "platform", title, AVAILABLE, "", f"{source} Confirm on the exact SKU.", "catalog")
    if best < th.copilot_plus_min_tops:
        return Capability(
            "copilot-plus", "platform", title, UNAVAILABLE, "",
            f"Copilot+ PCs need an NPU rated at {fmt(th.copilot_plus_min_tops)} TOPS or more; none was found here.", "vendor-rated",
        )
    return Capability(
        "copilot-plus", "platform", title, AVAILABLE, "",
        f"{p.cpu.family_label} with an NPU rated {fmt(best)} TOPS and {p.memory.tier} GB of RAM. Confirm on the exact SKU.", "vendor-rated",
    )


def _storage(p: DeviceProfile, catalog: Catalog) -> Capability:
    need = catalog.thresholds.model_drive_min_free_gb
    free = p.storage.free_gb
    status = AVAILABLE if free >= need else LIMITED if free >= 4 else UNAVAILABLE
    drive = p.storage.drive or "the model drive"
    return Capability(
        "storage", "platform", "Room for models", status, "",
        f"{fmt(free)} GB free on {drive}. This kit wants {fmt(need)} GB free for model downloads.", "detected",
    )


def _native_build(p: DeviceProfile) -> Capability:
    title = "Native build"
    if p.os.emulated:
        return Capability(
            "native-build", "platform", title, LIMITED, "",
            f"This {p.os.process_arch} build is running under emulation on {p.os.arch} Windows, "
            f"so NPU and GPU runtimes cannot load. Use the {p.os.arch} build.",
            "detected",
        )
    return Capability("native-build", "platform", title, AVAILABLE, "", f"{p.os.process_arch} build on {p.os.arch} Windows.", "detected")


def _powerpoint(p: DeviceProfile) -> Capability:
    title = "PowerPoint on this PC"
    name = f"PowerPoint {p.software.power_point_version}" if p.software.power_point_version else "PowerPoint"
    if p.software.power_point:
        return Capability("powerpoint", "platform", title, AVAILABLE, "", f"{name} is installed, so decks can be opened and exported as images.", "detected")
    return Capability(
        "powerpoint", "platform", title, LIMITED, "",
        "PowerPoint was not found. Previews use the built-in renderer and decks open in any PPTX viewer.", "detected",
    )
