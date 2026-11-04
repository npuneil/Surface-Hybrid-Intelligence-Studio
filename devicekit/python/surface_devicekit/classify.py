"""Turns raw hardware facts into a DeviceProfile. A probed PC and a simulated one take exactly this path, and the steps follow the C# Classifier
one for one, so the two ports can be checked against each other with golden vectors."""

from __future__ import annotations

from .catalog import Catalog, SiliconSpec, compile_ignore_case
from .dotnet import clean, fmt, round_half_away, trim
from .models import CpuInfo, DeviceProfile, GpuInfo, LaneInfo, MemoryInfo, NpuInfo, OsInfo, RawGpu, RawHardware, RawNpu, SoftwareInfo, StorageInfo

_VIRTUAL_ADAPTER = compile_ignore_case(
    r"Basic (Render|Display)|Remote Display|Hyper-V|Virtual|VMware|Parallels|Citrix|Indirect Display|DisplayLink|Mirage"
)
_VENDORS = tuple(
    (vendor, compile_ignore_case(pattern))
    for vendor, pattern in (
        ("nvidia", "nvidia"),
        ("qualcomm", "qualcomm|snapdragon|adreno|hexagon|microsoft sq"),
        ("intel", "intel"),
        ("amd", "amd|ryzen|radeon"),
    )
)


def r(value: float) -> float:
    """Classifier.R: two decimals, half away from zero, computed the way .NET does it (see dotnet.round_half_away)."""
    return round_half_away(value)


def vendor_of(name: str) -> str:
    """nvidia, qualcomm, intel, amd or other, judged from a device name."""
    return next((vendor for vendor, pattern in _VENDORS if pattern.search(name)), "other")


def budget(catalog: Catalog, lane_kind: str, vram_gb: float, visible_ram_gb: float) -> float:
    """Gigabytes a lane can give model weights: min(basis * fraction, basis - reserve)."""
    rule = catalog.budget.rule(lane_kind)
    basis = vram_gb if rule.basis == "vram" else visible_ram_gb
    return r(max(0.0, min(basis * rule.fraction, basis - rule.reserve_gb)))


def friendly_product(product: str, catalog: Catalog) -> str:
    text = clean(product)
    for prefix in catalog.surface.strip_prefixes:
        if text[: len(prefix)].lower() == prefix.lower():
            text = text[len(prefix):]
    for suffix in catalog.surface.strip_suffixes:
        cut = len(text) - len(suffix)
        if cut >= 0 and text[cut:].lower() == suffix.lower():
            text = text[:cut]
    return trim(text)


def classify(
    raw: RawHardware, catalog: Catalog, source: str = "detected", simulated_id: str | None = None, label: str | None = None
) -> DeviceProfile:
    th = catalog.thresholds
    silicon = catalog.match_silicon(raw.cpu)
    emulated = raw.process_arch != "" and raw.os_arch.lower() != raw.process_arch.lower()
    installed = raw.installed_ram_gb if raw.installed_ram_gb > 0 else raw.visible_ram_gb
    visible = raw.visible_ram_gb if raw.visible_ram_gb > 0 else raw.installed_ram_gb
    tier = catalog.ram_tier(installed)

    cpu_name = clean(raw.cpu)
    cpu = CpuInfo(
        name=cpu_name,
        vendor=silicon.vendor if silicon is not None else vendor_of(cpu_name),
        family_id=silicon.id if silicon is not None else "unknown",
        family_label=silicon.label if silicon is not None else cpu_name,
        arch=silicon.arch if silicon is not None else raw.os_arch.lower(),
        logical_cores=raw.logical_cores,
        unified_memory=silicon.unified_memory if silicon is not None else False,
    )

    gpus = tuple(_build_gpu(g, catalog) for g in raw.gpus if trim(g.name) and not _VIRTUAL_ADAPTER.search(g.name))
    npus = tuple(_build_npu(n, silicon, catalog, emulated) for n in raw.npus if trim(n.name))

    primary = _choose_primary_gpu(gpus)
    gpu_lane = _gpu_lane(primary, catalog, visible, emulated)
    npu_lane = _npu_lane(npus, silicon, catalog, visible)
    cpu_lane = _cpu_lane(cpu, catalog, visible, emulated)

    strong_nvidia = primary is not None and primary.vendor == "nvidia" and primary.kind in ("discrete", "unified") and gpu_lane.available
    if strong_nvidia:
        device_class = "gpu-first"
    elif npu_lane.available:
        device_class = "npu-first"
    elif gpu_lane.available:
        device_class = "igpu-first"
    else:
        device_class = "cpu-only"

    copilot_silicon = silicon is not None and silicon.copilot_plus != "no"
    copilot_npu = (silicon is not None and silicon.copilot_plus == "listed") or any(
        n.rated_tops is not None and n.rated_tops >= th.copilot_plus_min_tops for n in npus
    )
    if silicon is not None and silicon.builder_pc == "listed":
        pc_class = "builder"
    elif copilot_silicon and copilot_npu and tier >= th.copilot_plus_min_ram_gb:
        pc_class = "copilot-plus"
    elif strong_nvidia:
        pc_class = "gpu"
    elif any(n.rated_tops is not None and n.rated_tops > 0 for n in npus):
        pc_class = "npu"
    else:
        pc_class = "windows"

    by_id = {"npu": npu_lane, "gpu": gpu_lane, "cpu": cpu_lane}
    order = catalog.lane_order[device_class]
    lanes = tuple(by_id[lane_id] for lane_id in order) + tuple(lane for lane in by_id.values() if lane.id not in order)

    product = friendly_product(raw.product, catalog)
    is_surface = compile_ignore_case(catalog.surface.product_pattern).search(product) is not None
    name = label if label is not None else (product or "Windows PC")
    ram = f"{tier} GB" if tier > 0 else f"{fmt(r(installed), 1, optional=True)} GB"

    return DeviceProfile(
        source=source,
        simulated_id=simulated_id,
        label=name,
        headline=label if label is not None else f"{name} · {cpu.family_label} · {ram}",  # a caller's label (the simulated devices) already names the silicon and memory
        manufacturer=clean(raw.manufacturer),
        product=product,
        sku=clean(raw.sku),
        is_surface=is_surface,
        os=OsInfo(raw.os_version, raw.os_build, raw.os_arch.lower(), (raw.process_arch or raw.os_arch).lower(), emulated),
        cpu=cpu,
        gpus=gpus,
        npus=npus,
        memory=MemoryInfo(r(installed), r(visible), tier),
        storage=StorageInfo(raw.model_drive, r(raw.free_disk_gb), r(raw.total_disk_gb)),
        software=SoftwareInfo(raw.nvidia_smi, raw.power_point, raw.power_point_version),
        pc_class=pc_class,
        pc_class_label=catalog.pc_class_labels[pc_class],
        device_class=device_class,
        device_class_label=catalog.device_class_labels[device_class],
        lanes=lanes,
        raw=raw,
    )


def _build_gpu(gpu: RawGpu, catalog: Catalog) -> GpuInfo:
    family = catalog.match_gpu(gpu.name)
    name = clean(gpu.name)
    return GpuInfo(
        name=name,
        vendor=family.vendor if family is not None else vendor_of(name),
        kind=family.kind if family is not None else "integrated",
        family_id=family.id if family is not None else "unknown",
        vram_gb=r(gpu.vram_gb),
        driver=gpu.driver,
    )


def _build_npu(npu: RawNpu, silicon: SiliconSpec | None, catalog: Catalog, emulated: bool) -> NpuInfo:
    name = clean(npu.name)
    vendor = vendor_of(name)
    spec = silicon.npu if silicon is not None else None
    if spec is not None and vendor == "other":
        vendor = spec.vendor
    if spec is not None and spec.vendor != vendor:
        spec = None

    tops = spec.rated_tops if spec is not None else None
    runtime = spec.runtime if spec is not None else False
    minimum = catalog.thresholds.npu_min_tops_for_llm
    llm = tops is not None and tops >= minimum and runtime and vendor in catalog.eps.npu and not emulated

    if spec is None:
        note = "No rating on file for this NPU."
    elif spec.note:
        note = spec.note
    elif tops is None:
        note = "No rated TOPS on file for this NPU."
    elif emulated:
        note = "An x64 process on an ARM64 PC cannot load the NPU runtime. Use the ARM64 build."
    elif tops < minimum:
        note = (
            f"Rated {fmt(tops)} TOPS (vendor figure): enough for light background AI, "
            f"below the {fmt(minimum)} TOPS this kit needs for language models."
        )
    else:
        note = f"Rated {fmt(tops)} TOPS (vendor figure)."

    return NpuInfo(name, vendor, npu.driver, tops, "none" if tops is None else "vendor-rated", runtime, llm, note)


def _choose_primary_gpu(gpus: tuple[GpuInfo, ...]) -> GpuInfo | None:
    """A real NVIDIA GPU first, then any non-integrated one, then the most video memory; ties keep Windows' order."""
    ranked = sorted(gpus, key=lambda g: (not (g.vendor == "nvidia" and g.kind != "integrated"), g.kind == "integrated", -g.vram_gb))
    return ranked[0] if ranked else None


def _gpu_lane(gpu: GpuInfo | None, catalog: Catalog, visible: float, emulated: bool) -> LaneInfo:
    caps = catalog.model_classes.lane_cap
    if gpu is None:
        return LaneInfo("gpu", "gpu-integrated", False, "", "", (), 0.0, caps["gpu-integrated"], None, "Windows did not report a GPU.")

    kind = "gpu-" + gpu.kind
    lane_budget = budget(catalog, kind, gpu.vram_gb, visible)
    eps = catalog.eps.gpu[gpu.vendor] if gpu.vendor in catalog.eps.gpu else catalog.eps.gpu["other"]
    ok = not emulated and lane_budget >= catalog.thresholds.gpu_min_budget_gb

    if emulated:
        note = "An x64 process on an ARM64 PC cannot load GPU runtimes. Use the ARM64 build."
    elif not ok:
        note = f"Only {fmt(lane_budget, 1)} GB of graphics memory is usable for models."
    elif gpu.kind == "unified":
        note = f"Unified memory: the GPU and CPU share one pool, and the GPU reports {fmt(gpu.vram_gb, 1)} GB of it."
    elif gpu.kind == "discrete":
        note = f"Dedicated video memory: {fmt(gpu.vram_gb, 1)} GB."
    else:
        note = "Integrated graphics use system memory, so the budget is a share of RAM."
    return LaneInfo("gpu", kind, ok, gpu.name, gpu.vendor, eps, lane_budget if ok else 0.0, caps[kind], None, note)


def _npu_lane(npus: tuple[NpuInfo, ...], silicon: SiliconSpec | None, catalog: Catalog, visible: float) -> LaneInfo:
    capable = next((n for n in npus if n.llm_capable), None)
    shown = capable if capable is not None else (npus[0] if npus else None)
    if shown is not None:
        note = shown.note
    elif silicon is not None and silicon.npu is not None and silicon.npu.runtime:
        note = "Windows did not report an NPU for this chip. Install the NPU driver from Windows Update or the Surface driver pack."
    else:
        note = "This PC has no NPU."
    eps = catalog.eps.npu.get(shown.vendor, ()) if shown is not None else ()
    return LaneInfo(
        "npu",
        "npu",
        capable is not None,
        shown.name if shown is not None else "",
        shown.vendor if shown is not None else "",
        eps,
        budget(catalog, "npu", 0.0, visible) if capable is not None else 0.0,
        catalog.model_classes.lane_cap["npu"],
        shown.rated_tops if shown is not None else None,
        note,
    )


def _cpu_lane(cpu: CpuInfo, catalog: Catalog, visible: float, emulated: bool) -> LaneInfo:
    note = "Runs under x64 emulation on ARM64, which adds overhead. The ARM64 build is faster." if emulated else "Runs on any PC; the slowest lane."
    return LaneInfo(
        "cpu", "cpu", True, cpu.name, cpu.vendor, catalog.eps.cpu["any"], budget(catalog, "cpu", 0.0, visible), catalog.model_classes.lane_cap["cpu"], None, note
    )
