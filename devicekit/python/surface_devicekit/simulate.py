"""Representative devices for demos and tests: they go through the same classifier as a real probe, but nothing here was measured on that hardware
(except the one profile whose note says so)."""

from __future__ import annotations

import threading
from collections.abc import Iterable
from os import PathLike
from pathlib import Path
from typing import Any

from .catalog import DATA_ROOT
from .dotnet import equals_ignore_case
from .jsonio import loads, raw_from_dict, read_text
from .models import SimulatedDevice

DEFAULT_PROFILES_PATH = DATA_ROOT / "vectors" / "simulated-profiles.json"

_cache: dict[str, tuple[SimulatedDevice, ...]] = {}
_lock = threading.Lock()


def _optional_text(profile: dict[str, Any], key: str, where: str) -> str:
    value = profile.get(key)
    if value is None:
        return ""
    if not isinstance(value, str):
        raise ValueError(f"{where}.{key}: expected a string.")
    return value


def _object(value: Any, where: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ValueError(f"{where}: expected a JSON object.")
    return value


def load(text: str) -> tuple[SimulatedDevice, ...]:
    """Parse a simulated-profiles file. Its "defaults" object fills in anything a profile's "raw" leaves out; names here are case-sensitive,
    and the merged result is bound to the raw hardware record case-insensitively, as the C# kit does."""
    data = loads(text)
    if data is None:
        raise ValueError("The simulated profiles file is empty.")
    root = _object(data, "$")
    defaults = _object(root.get("defaults", {}), "$.defaults")
    profiles = root.get("profiles", [])
    if not isinstance(profiles, list):
        raise ValueError("$.profiles: expected an array.")

    devices = []
    for i, node in enumerate(profiles):
        where = f"$.profiles[{i}]"
        profile = _object(node, where)
        device_id = profile.get("id")
        if not isinstance(device_id, str):
            raise ValueError(f"{where}.id: expected a string.")
        raw = _object(profile.get("raw"), f"{where}.raw")
        devices.append(
            SimulatedDevice(
                device_id,
                _optional_text(profile, "group", where),
                _optional_text(profile, "label", where),
                _optional_text(profile, "note", where),
                raw_from_dict({**defaults, **raw}),
            )
        )
    return tuple(devices)


def load_file(path: str | PathLike[str]) -> tuple[SimulatedDevice, ...]:
    return load(read_text(path))


def all_devices(path: str | PathLike[str] | None = None) -> tuple[SimulatedDevice, ...]:
    """The devices in file order; read once per file and then cached."""
    key = str(Path(path if path is not None else DEFAULT_PROFILES_PATH).resolve())
    with _lock:
        if key not in _cache:
            _cache[key] = load_file(key)
        return _cache[key]


def find(device_id: str, devices: Iterable[SimulatedDevice] | None = None) -> SimulatedDevice | None:
    """The device with this id, ignoring case."""
    pool = all_devices() if devices is None else devices
    return next((d for d in pool if equals_ignore_case(d.id, device_id)), None)
