"""What the Studio shows for the PC it runs on: the device kit's report, the model each slot runs, and how well each demo fits.

A simulated device (a preview for sellers and for testing) is described by a throwaway engine that only previews its models. The real
engine keeps running this PC's real hardware, so a simulation changes what the Studio shows, never what runs."""
import logging
import re
import threading

import demos
import device_context
import hybrid
import target_plan
from local_engine import LARGE_MODELS, LocalEngine
from surface_devicekit import to_dict

LANE_ORDER = ("npu", "gpu", "cpu")
PARAMS = re.compile(r"^(\d+(?:\.\d+)?)B")
NAMED_SIZE = re.compile(r"(?<![\d.])(\d+(?:\.\d+)?)B\b")
NO_DEPTH = "A larger local model for depth, where this PC has the memory"


def _join(items):
    items = list(items)
    return items[0] if len(items) == 1 else ", ".join(items[:-1]) + " and " + items[-1]


def _unique(items):
    return list(dict.fromkeys(items))


def fit(demo_id, engine, context):
    """How one demo runs on this PC: each model step's lane, label and blocker, and whether the demo is ready, limited or unavailable.

    Demos whose steps fail on their own (the face-off and the five-model team) are limited when only some of their models are missing;
    every other demo is a pipeline that needs all of its models."""
    demo = demos.DEMOS[demo_id]
    steps = []
    for step, title in zip(demo["steps"], demos.render(demo_id, context)["steps"]):
        role = step["role"]
        if role == "measure":
            continue
        info = context["roles"][role]
        usable = info["available"]
        blocker = None if usable else engine.blocker_for(role) or f"{demos.GENERIC[role]} is not available on this PC."
        steps.append({"title": title, "role": role, "available": usable, "label": info["label"] if usable else None,
                      "where": info["where"] if usable else None, "lane": info["lane"] if usable else None, "blocker": blocker})
    missing = [step for step in steps if not step["available"]]
    state = ("ready" if not missing else "limited" if demo.get("independent") and len(missing) < len(steps) else "unavailable")
    where = _join(_unique(step["where"] for step in steps if step["available"])) if len(missing) < len(steps) else None
    if state == "ready":
        summary = f"Runs on {where}."
    elif state == "limited":
        nouns = [demos.GENERIC[step["role"]].lower() for step in missing]
        summary = f"Runs on {where}; {_join(nouns)} {'does' if len(nouns) == 1 else 'do'} not fit this PC."
    else:
        summary = f"Not available on this PC: {missing[0]['blocker']}"
    uses = [f"{step['label']} in place of {demos.REFERENCE['roles'][step['role']]['label']}" for step in steps
            if step["available"] and step["label"] != demos.REFERENCE["roles"][step["role"]]["label"]]
    note = (f"Uses {_join(_unique(uses))}, the model{'s' if len(set(uses)) > 1 else ''} this demo was tuned on; "
            "checks are scored the same way." if uses else None)
    return {"state": state, "lanes": [lane for lane in LANE_ORDER if any(step["lane"] == lane for step in steps)],
            "where": where, "summary": summary, "steps": steps, "note": note}


def ordered(fits, device_class):
    """Demo ids in the order this PC is best at them: unavailable demos last, and on an NPU-first PC the demos that run mostly on the NPU
    first (a demo's share of model steps on the NPU; ties keep the registry order)."""
    position = {demo_id: index for index, demo_id in enumerate(demos.ORDER)}

    def key(demo_id):
        row = fits[demo_id]
        share = sum(1 for step in row["steps"] if step["lane"] == "npu") / len(row["steps"]) if device_class == "npu-first" else 0
        return row["state"] == "unavailable", -share, position[demo_id]

    return sorted(demos.ORDER, key=key)


def models(engine):
    """One row per slot of the Studio's lineup: what this PC runs for it, where, and what blocks it when nothing fits."""
    rows = []
    for entry in engine.target_info()["slots"]:
        slot = target_plan.BY_KEY[entry["slot"]]
        info = engine.describe(slot.reference if slot.kind == "chat" else slot.key)
        model, usable = entry["model"], info["available"]
        choice = engine.slots.get(slot.key)
        rows.append({"slot": slot.key, "title": slot.title, "reference": slot.reference, "available": usable,
                     "label": info["label"] if usable else None, "where": info["where"] if usable else None,
                     "lane": info["lane"] if usable else None, "device": model and model["device"],
                     "ep": model and model["ep"], "sizeGb": model and model["fileGb"], "cached": model and model["cached"],
                     "estimated": bool(choice and choice.estimated), "better": entry["better"],
                     "blocker": None if usable else entry["blocker"] or f"No {slot.title.lower()} fits this PC.",
                     "notes": entry["notes"]})
    return rows


def placement(rows):
    """Where the lineup runs, as a sentence: '5 of 5 models run here: 3 on the NPU, 2 on the CPU'."""
    runnable = [row for row in rows if row["available"]]
    if not runnable:
        return "No model in the Studio's lineup fits this PC."
    places = _unique(row["where"] for row in runnable)
    parts = [f"{sum(1 for row in runnable if row['where'] == place)} on {place}" for place in places]
    return f"{len(runnable)} of {len(rows)} models run here: {', '.join(parts)}."


def _span(sizes):
    low, high = min(sizes), max(sizes)
    return f"{low:g}B" if low == high else f"{low:g}B-{high:g}B"


def _size(spec):
    """A chat model's size as its name states it (Qwen2.5 7B is 7B), else its parameter count (Phi-4 mini is 3.8B)."""
    found = NAMED_SIZE.search(spec["label"]) or PARAMS.match(spec["params"])
    return float(found.group(1)) if found else None


def hybrid_wording(engine):
    """The words hybrid.public() fills in for this PC: where private work runs, what runs there routinely and what runs for depth."""
    chosen = [spec for key in (target_plan.CHAT, target_plan.REASONER)
              if (spec := next((spec for spec in engine.models.values() if spec["slot"] == key), None)) is not None]
    sizes = [size for spec in chosen if (size := _size(spec)) is not None]
    if not engine.slots:
        routine = hybrid.REFERENCE_WORDING["routine"]
    else:
        routine = f"{_span(sizes)} for routine work" if sizes else "Small models for routine work"
    large = [float(found.group(1)) for row in engine.state.get("models") or []
             if row.get("runtime") == "llama.cpp" and row.get("available") and (found := PARAMS.match(row.get("params") or ""))]
    depth = f"{_span(large)} on {engine.where(next(iter(LARGE_MODELS)))} for depth" if large else NO_DEPTH
    return {"where": engine.where(), "routine": routine, "depth": depth}


class DeviceView:
    """The Studio's device state: the shared detection service, the real engine, and a preview engine while a device is simulated."""

    def __init__(self, engine, service=None):
        self.engine = engine
        self._service = service
        self._lock = threading.Lock()
        self._preview = (None, None)

    @property
    def service(self):
        return self._service if self._service is not None else device_context.service()

    @property
    def simulated(self):
        return self.service.is_simulated

    def shown(self):
        """The engine whose models the Studio describes: the real one, or a throwaway preview of the simulated device."""
        service = self.service
        if not service.is_simulated:
            return self.engine
        report = service.effective
        with self._lock:
            if self._preview[0] is not report:
                preview = LocalEngine().preview(report, forced=False)
                if not preview.slots:
                    raise RuntimeError(f"{report.profile.label} could not be previewed.")
                self._preview = (report, preview)
            return self._preview[1]

    def target(self):
        """A short description of the PC being shown, cheap enough for the status poll."""
        profile = self.service.effective.profile
        try:
            engine = self.shown()
            where, primary = engine.where(), engine.lane()
        except Exception:
            logging.exception("Could not describe where the models run")
            where, primary = None, None
        return {"simulated": profile.source == "simulated", "label": profile.label, "headline": profile.headline,
                "deviceClass": profile.device_class, "deviceClassLabel": profile.device_class_label,
                "pcClassLabel": profile.pc_class_label, "where": where, "lane": primary,
                "lanes": [{"id": lane.id, "available": lane.available, "device": lane.device, "vendor": lane.vendor}
                          for lane in profile.lanes]}

    def fits(self, engine=None):
        """(fit rows by demo id with their rank, demo ids in this PC's order) for the PC being shown."""
        engine = engine or self.shown()
        context = demos.wording(engine)
        rows = {demo_id: fit(demo_id, engine, context) for demo_id in demos.ORDER}
        order = ordered(rows, self.service.effective.profile.device_class)
        for rank, demo_id in enumerate(order):
            rows[demo_id]["rank"] = rank
        return rows, order

    def payload(self):
        service = self.service
        engine = self.shown()
        report = service.effective
        detected = service.detected.profile
        rows, order = self.fits(engine)
        lineup = models(engine)
        return {"simulated": report.profile.source == "simulated",
                "detected": {"label": detected.label, "headline": detected.headline, "deviceClass": detected.device_class,
                             "deviceClassLabel": detected.device_class_label},
                "report": to_dict(report),
                "simulation": {"id": report.profile.simulated_id,
                               "devices": [{"id": item.id, "group": item.group, "label": item.label, "note": item.note}
                                           for item in service.simulated_devices]},
                "models": lineup, "placement": placement(lineup), "engine": engine.target_info(),
                "demos": [{"id": demo_id, **rows[demo_id]} for demo_id in order]}

    def simulate(self, device_id):
        """Show another device (None goes back to this PC) and return the new payload. A device that cannot be previewed is not kept."""
        service = self.service
        if device_id is None:
            service.clear_simulation()
        else:
            if not any(item.id == device_id for item in service.simulated_devices):
                raise ValueError("Choose one of the listed devices.")
            service.simulate(device_id)
            try:
                self.shown()
            except Exception:
                service.clear_simulation()
                raise
        return self.payload()
