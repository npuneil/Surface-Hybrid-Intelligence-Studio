"""Which model each of the Studio's slots runs on this PC, chosen from the device kit's resolver and Foundry Local's live catalog.

The demos were tuned on a Surface with a large NVIDIA GPU. Each slot names the model they were tuned on (the reference). On a PC where the
reference fits its lane it is chosen, so that PC behaves exactly as it always did; anywhere else the slot gets the strongest model the
catalog offers that fits, on the PC's best lane (NPU, then GPU, then CPU on an NPU-first PC).

Nothing here touches Foundry Local or the hardware: it works on plain records, so the same rules run in the engine, in tests and for a
simulated device."""
from dataclasses import dataclass

import device_context  # noqa: F401  (puts the device kit on sys.path)
from surface_devicekit import foundry
from surface_devicekit.catalog import Catalog
from surface_devicekit.models import DeviceReport


@dataclass(frozen=True)
class Slot:
    key: str
    workload: str
    reference: str
    kind: str
    task: str | None
    title: str
    reasoning: bool = False


CHAT, REASONER, ASR, VISION, EMBED = "chat", "reasoner", "asr", "vision", "embed"
SLOTS = (
    Slot(CHAT, "structured-json", "qwen2.5-coder-14b", "chat", None, "Chat model"),
    Slot(REASONER, "reasoning", "gpt-oss-20b", "chat", None, "Reasoning model", reasoning=True),
    Slot(ASR, "speech-to-text", "whisper-medium", "media", "automatic-speech-recognition", "Speech model"),
    Slot(VISION, "vision", "qwen3-vl-4b-instruct", "media", "vision-language-chat", "Vision model"),
    Slot(EMBED, "embeddings", "qwen3-embedding-0.6b", "media", "embeddings", "Embedding model"),
)
BY_KEY = {slot.key: slot for slot in SLOTS}
BY_REFERENCE = {slot.reference: slot for slot in SLOTS if slot.kind == "chat"}
MEDIA_KEYS = tuple(slot.key for slot in SLOTS if slot.kind == "media")


@dataclass(frozen=True)
class SlotChoice:
    """model runs on this PC now (None when nothing fits); better is a stronger option that first needs a download."""

    slot: Slot
    model: foundry.ResolvedModel | None
    better: foundry.ResolvedModel | None
    notes: tuple[str, ...]
    blocker: str | None
    estimated: bool = False


def choose(
    report: DeviceReport,
    catalog: Catalog,
    source: foundry.ModelSource,
    *,
    usable,
    failed=(),
    lane_problems=None,
    allow_download: bool = False,
    lanes=None,
    estimated: bool = False,
) -> dict[str, SlotChoice]:
    """With downloads allowed the strongest option wins (it is fetched when needed); without, the strongest one already on this PC wins
    and a stronger one that needs a download is reported as 'better'."""
    chosen: dict[str, SlotChoice] = {}
    for slot in SLOTS:
        plan = foundry.resolve(report, catalog, slot.workload, source, usable=usable, failed=failed, lane_problems=lane_problems,
                               prefer=(slot.reference,), lanes=lanes)
        if slot.task:
            plan = _only_task(plan, slot.task)
        strongest = plan.primary
        model = strongest if allow_download or estimated else (plan.primary_on_this_pc or strongest)
        better = strongest if strongest is not None and model is not strongest else None
        blocker = None
        if model is None:
            blocker = " ".join(plan.notes) or f"No {slot.title.lower()} in this PC's catalog fits it."
        chosen[slot.key] = SlotChoice(slot, model, better, plan.notes, blocker, estimated)
    return chosen


def _only_task(plan: foundry.ResolvedPlan, task: str) -> foundry.ResolvedPlan:
    """A speech, vision or embedding slot must get a model of that task; the catalog's task string is the source of truth."""
    options = tuple(option for option in plan.options if not option.task or option.task == task)
    return foundry.ResolvedPlan(plan.workload, plan.title, options, plan.notes)


class EstimatedSource:
    """What a device would be offered if every candidate had a build for each lane, sized from the kit's catalog. It lets a simulated device
    (or an engine that has not started) show the plan the same rules would make; sizes are estimates, so nothing here is 'on this PC'."""

    _TASKS = {"chat": "chat-completion", "stt": "automatic-speech-recognition", "vision": "vision-language-chat", "embed": "embeddings"}

    def __init__(self, catalog: Catalog, report: DeviceReport) -> None:
        self._catalog = catalog
        self._lanes = [lane for lane in report.profile.lanes if lane.available]

    def variants(self, alias: str):
        spec = self._catalog.models.get(alias)
        if spec is None:
            return None
        task = self._TASKS.get(spec.task, spec.task)
        rows = []
        for lane in self._lanes:
            size = spec.size_for(lane.id)
            if size is None:
                continue
            device = self._catalog.lanes[lane.id].device_type.upper()
            for ep in lane.eps:
                rows.append(foundry.CatalogVariant(f"{alias}:{lane.id}:{ep}", alias, device, ep, ep, size[0] * 1024.0, 1, False, task))
        return rows


def estimate(report: DeviceReport, catalog: Catalog, lanes=None) -> dict[str, SlotChoice]:
    """The slot choices for any report, from the kit's size estimates alone; every provider counts as available."""
    usable = {ep for lane in report.profile.lanes for ep in lane.eps}
    return choose(report, catalog, EstimatedSource(catalog, report), usable=usable, estimated=True, lanes=lanes)


def has_cuda_lane(report: DeviceReport, lanes=None) -> bool:
    """True when this PC has an NVIDIA GPU lane that can run CUDA models (the only lane that needs the cuDNN supplement)."""
    return any(lane.id == "gpu" and lane.available and "cuda" in lane.eps and (lanes is None or "gpu" in lanes)
               for lane in report.profile.lanes)
