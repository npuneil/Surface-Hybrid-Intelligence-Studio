"""Shared test doubles. The detection service is built on a device from the kit's catalog, so no test probes the PC it runs on."""
import json

import device_context
from local_engine import MEDIA_MODELS, MODELS, LocalEngine
from surface_devicekit import DeviceDetectionService, simulate
from surface_devicekit.foundry import CatalogVariant, norm_ep

# The reference PC with enough memory for the full resident lineup.
REFERENCE_DEVICE = "nvidia-n1x-128"
CATALOG_FIXTURE = device_context.ROOT / "devicekit" / "python" / "tests" / "data" / "foundry-catalog-arm64-cuda.json"


def service_for(device_id=REFERENCE_DEVICE):
    """A detection service whose "this PC" is the catalog device, so its report never depends on the machine running the tests."""
    return DeviceDetectionService(probe=lambda: simulate.find(device_id).raw)


def report_for(device_id):
    return service_for(device_id).detected


class ReadyEngine(LocalEngine):
    """An engine that never started but reports itself ready, so routes that need a ready engine can be exercised. Nothing runs."""

    def __init__(self):
        super().__init__()
        self.state.update(state="ready", detail="Test engine")

    def switch(self, model_ref):
        self.state.update(selected_model=model_ref, state="ready")


def reference_builds():
    """The ids of the models the demos were tuned on, which is what a PC that has run the Studio before has cached."""
    return {spec["id"] for spec in (*MODELS.values(), *MEDIA_MODELS.values())}


class FakeSource:
    """Foundry Local's catalog as the resolver reads it: variants by alias."""

    def __init__(self, builds):
        self.rows = {}
        for build in builds:
            self.rows.setdefault(build.alias, []).append(build)

    def variants(self, alias):
        return self.rows.get(alias)


def real_catalog(cached=()):
    """The builds a live Arm64 PC with only the CUDA provider registered lists (captured from Foundry Local); nothing is cached unless named."""
    doc = json.loads(CATALOG_FIXTURE.read_text(encoding="utf-8"))
    return FakeSource(
        CatalogVariant(b["id"], b["alias"], b["device"], norm_ep(b["ep"]), b["ep"], b["mb"], b["version"], b["id"] in cached,
                       b["task"] or "", b["contextLength"])
        for b in doc["builds"]
    )
