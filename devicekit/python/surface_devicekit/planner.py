"""Chooses models. For each lane the catalog's candidates are walked strongest to weakest, and a model stays on the ladder only if its class
is allowed on that lane and its weights (times the load overhead) fit the lane's memory budget."""

from __future__ import annotations

from collections.abc import Mapping
from types import MappingProxyType

from .catalog import Catalog, WorkloadSpec
from .classify import r
from .dotnet import fmt
from .models import DeviceProfile, LaneInfo, LanePlan, ModelChoice, UnknownKeyError, WorkloadPlan


def plan_all(profile: DeviceProfile, catalog: Catalog) -> Mapping[str, WorkloadPlan]:
    return MappingProxyType({workload_id: plan(profile, catalog, workload_id) for workload_id in catalog.workloads})


def plan(profile: DeviceProfile, catalog: Catalog, workload_id: str) -> WorkloadPlan:
    workload = catalog.workloads.get(workload_id)
    if workload is None:
        raise UnknownKeyError(f"Unknown workload '{workload_id}'. Known: {', '.join(catalog.workloads)}")

    lane_plans = tuple(plan_lane(lane, workload, catalog) for lane in profile.lanes)
    primary = next((p for p in lane_plans if p.choice is not None), None)
    others = [p for p in lane_plans if p is not primary and p.choice is not None]
    return WorkloadPlan(
        workload.id,
        workload.title,
        workload.why,
        primary.lane if primary is not None else None,
        primary.choice if primary is not None else None,
        lane_plans,
        _summarize(catalog, primary, others),
    )


def plan_lane(lane: LaneInfo, workload: WorkloadSpec, catalog: Catalog) -> LanePlan:
    ladder: list[ModelChoice] = []
    if lane.available:
        cap = catalog.model_classes.rank(lane.class_cap)
        for alias in workload.candidates:
            model = catalog.models.get(alias)
            if model is None or catalog.model_classes.rank(model.class_) > cap:
                continue
            size = model.size_for(lane.id)
            if size is None:
                continue
            size_gb, estimated = size
            required = catalog.load_cost(size_gb)
            if required > lane.budget_gb:
                continue
            ladder.append(ModelChoice(alias, model.title, model.class_, model.params, r(size_gb), estimated, r(required)))

    if ladder:
        reason = ""
    elif lane.available:
        reason = f"Nothing in the list fits {fmt(lane.budget_gb, 1)} GB on this lane."
    else:
        reason = lane.note
    return LanePlan(lane.id, lane.kind, lane.budget_gb, ladder[0] if ladder else None, tuple(ladder), reason)


def _summarize(catalog: Catalog, primary: LanePlan | None, others: list[LanePlan]) -> str:
    pick = primary.choice if primary is not None else None
    if primary is None or pick is None:
        return "Nothing for this workload fits this device."
    text = f"{pick.title} on the {catalog.lanes[primary.lane].label} ({fmt(pick.size_gb, 1)} GB model file{', size estimated' if pick.size_estimated else ''})"
    if others:
        fallbacks = ", then ".join(f"{p.choice.title} on the {catalog.lanes[p.lane].label}" for p in others if p.choice is not None)
        text += ". Fallback: " + fallbacks + "."
    return text
