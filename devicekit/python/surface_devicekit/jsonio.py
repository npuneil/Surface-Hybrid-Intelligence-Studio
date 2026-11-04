"""JSON the way the C# kit reads and writes it (System.Text.Json): comments are skipped on read, property names match case-insensitively,
and output is indented camelCase with the same number and string formatting."""

from __future__ import annotations

import dataclasses
import json
import re
from collections.abc import Mapping
from os import PathLike
from pathlib import Path
from typing import Any

from .dotnet import escape_string, format_double
from .models import RawGpu, RawHardware, RawNpu

_STRING_OR_COMMENT = re.compile(r'"(?:[^"\\]++|\\.)*+"|//[^\n]*|/\*.*?\*/', re.DOTALL)
_BYTE_ORDER_MARKS = (
    (b"\xff\xfe\x00\x00", "utf-32"),
    (b"\x00\x00\xfe\xff", "utf-32"),
    (b"\xef\xbb\xbf", "utf-8-sig"),
    (b"\xff\xfe", "utf-16"),
    (b"\xfe\xff", "utf-16"),
)


def read_text(path: str | PathLike[str]) -> str:
    """File.ReadAllText: UTF-8 unless a byte-order mark says UTF-16 or UTF-32 (Windows PowerShell 5 writes UTF-16 with '>')."""
    data = Path(path).read_bytes()
    for mark, encoding in _BYTE_ORDER_MARKS:
        if data.startswith(mark):
            return data.decode(encoding, errors="replace")
    return data.decode("utf-8", errors="replace")


class RawNumber(float):
    """A number that remembers the text it was read from, as a JsonNode does, so a difference report can quote it exactly as written."""

    __slots__ = ("raw",)
    raw: str

    def __new__(cls, text: str) -> RawNumber:
        value = super().__new__(cls, text)
        value.raw = text
        return value


def _reject_constant(name: str) -> Any:
    raise ValueError(f"'{name}' is not a valid JSON value.")


def loads(text: str, *, comments: bool = True, keep_numbers: bool = False) -> Any:
    """json.loads that refuses NaN and Infinity and, unless comments is False, skips // and /* */ comments as ReadCommentHandling.Skip does.

    With keep_numbers every number comes back as a RawNumber."""
    text = text.removeprefix("\ufeff")
    if comments and "/" in text:
        text = _STRING_OR_COMMENT.sub(lambda m: m.group() if m.group().startswith('"') else " ", text)
    if keep_numbers:
        return json.loads(text, parse_float=RawNumber, parse_int=RawNumber, parse_constant=_reject_constant)
    return json.loads(text, parse_constant=_reject_constant)


class JsonObject:
    """An object bound the way System.Text.Json binds properties: names match case-insensitively, a missing or null value keeps the default,
    and a value of the wrong type is an error."""

    __slots__ = ("path", "_items", "_written")

    def __init__(self, value: Any, path: str = "$") -> None:
        if not isinstance(value, dict):
            raise ValueError(f"{path}: expected a JSON object.")
        self.path = path
        self._written = value
        self._items = {key.lower(): item for key, item in value.items()}

    def _bad(self, name: str, wanted: str) -> ValueError:
        return ValueError(f"{self.path}.{name}: expected {wanted}.")

    def has(self, name: str) -> bool:
        return self._items.get(name.lower()) is not None

    def text(self, name: str, default: str = "") -> str:
        value = self._items.get(name.lower())
        if value is None:
            return default
        if not isinstance(value, str):
            raise self._bad(name, "a string")
        return value

    def optional_text(self, name: str) -> str | None:
        return self.text(name) if self.has(name) else None

    def number(self, name: str, default: float = 0.0) -> float:
        value = self._items.get(name.lower())
        if value is None:
            return default
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise self._bad(name, "a number")
        return float(value)

    def optional_number(self, name: str) -> float | None:
        return self.number(name) if self.has(name) else None

    def integer(self, name: str, default: int = 0) -> int:
        value = self._items.get(name.lower())
        if value is None:
            return default
        if isinstance(value, bool) or not isinstance(value, int):
            raise self._bad(name, "an integer")
        return value

    def boolean(self, name: str, default: bool = False) -> bool:
        value = self._items.get(name.lower())
        if value is None:
            return default
        if not isinstance(value, bool):
            raise self._bad(name, "true or false")
        return value

    def child(self, name: str) -> JsonObject | None:
        value = self._items.get(name.lower())
        return None if value is None else JsonObject(value, f"{self.path}.{name}")

    def objects(self, name: str) -> list[JsonObject]:
        value = self._items.get(name.lower())
        if value is None:
            return []
        if not isinstance(value, list):
            raise self._bad(name, "an array")
        return [JsonObject(item, f"{self.path}.{name}[{i}]") for i, item in enumerate(value)]

    def mapping(self, name: str) -> dict[str, JsonObject]:
        """A dictionary of objects; keys are kept exactly as written."""
        value = self._items.get(name.lower())
        if value is None:
            return {}
        if not isinstance(value, dict):
            raise self._bad(name, "an object")
        return {key: JsonObject(item, f"{self.path}.{name}.{key}") for key, item in value.items()}

    def strings(self, name: str) -> list[str]:
        value = self._items.get(name.lower())
        if value is None:
            return []
        if not isinstance(value, list) or not all(isinstance(item, str) for item in value):
            raise self._bad(name, "an array of strings")
        return list(value)

    def integers(self, name: str) -> list[int]:
        value = self._items.get(name.lower())
        if value is None:
            return []
        if not isinstance(value, list) or not all(isinstance(item, int) and not isinstance(item, bool) for item in value):
            raise self._bad(name, "an array of integers")
        return list(value)

    def string_map(self, name: str) -> dict[str, str]:
        value = self._items.get(name.lower())
        if value is None:
            return {}
        if not isinstance(value, dict) or not all(isinstance(item, str) for item in value.values()):
            raise self._bad(name, "an object of strings")
        return dict(value)

    def string_lists(self, name: str) -> dict[str, list[str]]:
        value = self._items.get(name.lower())
        if value is None:
            return {}
        if not isinstance(value, dict):
            raise self._bad(name, "an object of string arrays")
        out: dict[str, list[str]] = {}
        for key, item in value.items():
            if not isinstance(item, list) or not all(isinstance(entry, str) for entry in item):
                raise self._bad(f"{name}.{key}", "an array of strings")
            out[key] = list(item)
        return out

    def rest(self, *known: str) -> dict[str, Any]:
        """Every property that is not one of the known names (JsonExtensionData), keys as written."""
        skip = {name.lower() for name in known}
        return {key: value for key, value in self._written.items() if key.lower() not in skip}


def raw_from_dict(data: Any) -> RawHardware:
    o = JsonObject(data)
    return RawHardware(
        manufacturer=o.text("manufacturer"),
        product=o.text("product"),
        sku=o.text("sku"),
        cpu=o.text("cpu"),
        logical_cores=o.integer("logicalCores"),
        os_version=o.text("osVersion"),
        os_build=o.integer("osBuild"),
        os_arch=o.text("osArch", "x64"),
        process_arch=o.text("processArch"),
        gpus=tuple(RawGpu(g.text("name"), g.text("driver"), g.number("vramGb")) for g in o.objects("gpus")),
        npus=tuple(RawNpu(n.text("name"), n.text("driver"), n.text("provider")) for n in o.objects("npus")),
        installed_ram_gb=o.number("installedRamGb"),
        visible_ram_gb=o.number("visibleRamGb"),
        model_drive=o.text("modelDrive"),
        free_disk_gb=o.number("freeDiskGb"),
        total_disk_gb=o.number("totalDiskGb"),
        nvidia_smi=o.boolean("nvidiaSmi"),
        power_point=o.boolean("powerPoint"),
        power_point_version=o.text("powerPointVersion"),
    )


def parse_raw(text: str) -> RawHardware:
    """The output of --raw, from another PC."""
    data = loads(text)
    if data is None:
        raise ValueError("Hardware JSON is empty.")
    return raw_from_dict(data)


def _camel(name: str) -> str:
    head, *tail = name.rstrip("_").split("_")
    return head + "".join(part[:1].upper() + part[1:] for part in tail)


def to_dict(value: Any) -> Any:
    """The JSON-ready form of a record: camelCase keys in field order, tuples as lists, nested records expanded.

    Fields declared with metadata json=False (the raw hardware on a profile) are left out, as the C# serializer's [JsonIgnore] does."""
    if dataclasses.is_dataclass(value) and not isinstance(value, type):
        return {_camel(f.name): to_dict(getattr(value, f.name)) for f in dataclasses.fields(value) if f.metadata.get("json", True)}
    if isinstance(value, Mapping):
        return {key: to_dict(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [to_dict(item) for item in value]
    return value


def dumps(value: Any, *, indented: bool = True) -> str:
    """Text identical to System.Text.Json output for the same data: two-space indent, ": " after keys, whole doubles without ".0".

    Lines break with \\n; print() turns that into the CRLF a Windows C# build writes."""
    out: list[str] = []
    _write(value, out, 0, indented)
    return "".join(out)


def to_json(value: Any, *, indented: bool = True) -> str:
    """DeviceJson.Serialize: a record (or a dict of records) straight to text."""
    return dumps(to_dict(value), indented=indented)


def _write(value: Any, out: list[str], depth: int, indented: bool) -> None:
    if value is None:
        out.append("null")
    elif value is True:
        out.append("true")
    elif value is False:
        out.append("false")
    elif isinstance(value, str):
        out.append(escape_string(value))
    elif isinstance(value, RawNumber):
        out.append(value.raw)
    elif isinstance(value, int):
        out.append(str(value))
    elif isinstance(value, float):
        out.append(format_double(value))
    elif isinstance(value, Mapping):
        _write_items(out, "{}", [(key, item) for key, item in value.items()], depth, indented)
    elif isinstance(value, (list, tuple)):
        _write_items(out, "[]", [(None, item) for item in value], depth, indented)
    else:
        raise TypeError(f"Cannot write {type(value).__name__} as JSON.")


def _write_items(out: list[str], brackets: str, items: list[tuple[str | None, Any]], depth: int, indented: bool) -> None:
    if not items:
        out.append(brackets)
        return
    out.append(brackets[0])
    inner = "\n" + "  " * (depth + 1) if indented else ""
    for i, (key, item) in enumerate(items):
        out.append(("," if i else "") + inner)
        if key is not None:
            out.append(escape_string(key) + (": " if indented else ":"))
        _write(item, out, depth + 1, indented)
    out.append(("\n" + "  " * depth if indented else "") + brackets[1])
