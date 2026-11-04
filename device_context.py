"""The studio's one view of the PC it runs on: the device kit's detection service, shared by the engine, the server and the demos.

The kit lives in devicekit/python beside this file (HYBRID_STUDIO_DEVICEKIT can point at another copy of that folder). Importing this module
puts it on sys.path, so `from surface_devicekit import ...` works anywhere in the studio."""
import os
import sys
import threading
from pathlib import Path

from edition import ENV_PREFIX

ROOT = Path(__file__).resolve().parent
KIT_ENV = f"{ENV_PREFIX}DEVICEKIT"


def _kit_dir():
    override = os.environ.get(KIT_ENV, "").strip().strip('"')
    if override:
        folder = Path(override)
        if not (folder / "surface_devicekit").is_dir():
            raise RuntimeError(f"{KIT_ENV} points at a folder without surface_devicekit: {folder}")
        return folder
    return ROOT / "devicekit" / "python"


_kit = _kit_dir()
if str(_kit) not in sys.path:
    sys.path.insert(0, str(_kit))

from surface_devicekit import DeviceDetectionService  # noqa: E402  (needs the path above)

_service = None
_lock = threading.Lock()


def service(model_dir=None):
    """The shared service, created on first use. model_dir says where models are stored, so free disk space is read from that drive."""
    global _service
    with _lock:
        if _service is None:
            _service = DeviceDetectionService(model_dir=model_dir)
        return _service


def install(replacement):
    """Use another service (tests pass one built on a fake probe); returns the one it replaced."""
    global _service
    with _lock:
        previous, _service = _service, replacement
        return previous
