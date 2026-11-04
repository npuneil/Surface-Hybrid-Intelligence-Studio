"""A plain-text view of a DeviceReport for consoles and log panes."""

from __future__ import annotations

from .capabilities import AVAILABLE, LIMITED
from .catalog import Catalog
from .dotnet import fmt
from .models import DeviceReport

_MARKS = {AVAILABLE: "[ok]     ", LIMITED: "[limited]"}
_NO_MARK = "[no]     "


def render(report: DeviceReport, catalog: Catalog | None = None, capabilities: bool = True, plans: bool = True) -> str:
    """Lines end in \\n, which print() turns into the platform's line ending."""
    if catalog is None:
        catalog = Catalog.default()
    p = report.profile
    emulated = f" (this {p.os.process_arch} build is emulated)" if p.os.emulated else f" (native {p.os.process_arch} build)"
    lines = [
        f"{p.headline}{'  [SIMULATED: display and planning only]' if p.source == 'simulated' else ''}",
        f"  Class      {p.pc_class_label} · {p.device_class_label}",
        f"  OS         {p.os.version} · {p.os.arch}{emulated}",
        f"  CPU        {p.cpu.name} · {p.cpu.logical_cores} logical cores",
    ]
    for g in p.gpus:
        lines.append(f"  GPU        {g.name} · {g.kind}{f' · {fmt(g.vram_gb, 1)} GB' if g.vram_gb >= 0.5 else ''}")
    if not p.gpus:
        lines.append("  GPU        none reported")
    for n in p.npus:
        rating = f"rated {fmt(n.rated_tops)} TOPS (vendor figure)" if n.rated_tops is not None else "no rating"
        lines.append(f"  NPU        {n.name} · {rating}{'' if n.llm_capable else ' · not used for language models'}")
    if not p.npus:
        lines.append("  NPU        none reported")
    lines.append(f"  Memory     {fmt(p.memory.installed_gb, 1, optional=True)} GB installed · {fmt(p.memory.visible_gb, 1, optional=True)} GB visible to Windows")
    lines.append(f"  Storage    {fmt(p.storage.free_gb)} GB free of {fmt(p.storage.total_gb)} GB on {p.storage.drive}")

    lines += ["", "Lanes, preferred first"]
    for lane in p.lanes:
        state = (
            f"budget {fmt(lane.budget_gb, 1):>5} GB · up to {lane.class_cap}-class · EP {', '.join(lane.eps)}"
            if lane.available
            else "not available"
        )
        lines.append(f"  {catalog.lanes[lane.id].label:<4} {state}")
        lines.append(f"       {lane.device} — {lane.note}")

    if capabilities:
        lines += ["", "Capabilities"]
        lines += [f"  {_MARKS.get(c.status, _NO_MARK)} {c.title:<32} {c.detail}  [{c.basis}]" for c in p.capabilities]

    if plans:
        lines += ["", "Plans"]
        lines += [f"  {workload_id:<16} {plan.summary}" for workload_id, plan in report.plans.items()]
    return "\n".join(lines) + "\n"
