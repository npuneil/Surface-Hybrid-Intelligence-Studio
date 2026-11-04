import json
import unittest
from functools import reduce
from pathlib import Path
from unittest.mock import patch

from surface_devicekit.jsonio import RawNumber, dumps, loads, parse_raw, read_text, to_dict, to_json
from surface_devicekit.models import RawGpu, RawHardware

from tests.support import analyze_raw, devices, raw_of, report_of

# The property order of DeviceJson.Serialize(report) in the C# kit, level by level; a list is entered through its first element.
REPORT_KEYS = {
    (): ["profile", "plans", "catalogVersion"],
    ("profile",): [
        "source", "simulatedId", "label", "headline", "manufacturer", "product", "sku", "isSurface", "os", "cpu", "gpus", "npus",
        "memory", "storage", "software", "pcClass", "pcClassLabel", "deviceClass", "deviceClassLabel", "lanes", "capabilities",
    ],
    ("profile", "os"): ["version", "build", "arch", "processArch", "emulated"],
    ("profile", "cpu"): ["name", "vendor", "familyId", "familyLabel", "arch", "logicalCores", "unifiedMemory"],
    ("profile", "gpus", 0): ["name", "vendor", "kind", "familyId", "vramGb", "driver"],
    ("profile", "npus", 0): ["name", "vendor", "driver", "ratedTops", "ratedSource", "runtimeSupported", "llmCapable", "note"],
    ("profile", "memory"): ["installedGb", "visibleGb", "tier"],
    ("profile", "storage"): ["drive", "freeGb", "totalGb"],
    ("profile", "software"): ["nvidiaSmi", "powerPoint", "powerPointVersion"],
    ("profile", "lanes", 0): ["id", "kind", "available", "device", "vendor", "eps", "budgetGb", "classCap", "ratedTops", "note"],
    ("profile", "capabilities", 0): ["id", "group", "title", "status", "lane", "detail", "basis"],
    ("plans", "structured-json"): ["workload", "title", "why", "primaryLane", "primary", "lanes", "summary"],
    ("plans", "structured-json", "lanes", 0): ["lane", "kind", "budgetGb", "choice", "ladder", "reason"],
    ("plans", "structured-json", "lanes", 0, "choice"): ["alias", "title", "class", "paramsB", "sizeGb", "sizeEstimated", "requiredGb"],
    ("plans", "structured-json", "lanes", 0, "ladder", 0): ["alias", "title", "class", "paramsB", "sizeGb", "sizeEstimated", "requiredGb"],
}

RAW_KEYS = [
    "manufacturer", "product", "sku", "cpu", "logicalCores", "osVersion", "osBuild", "osArch", "processArch", "gpus", "npus", "installedRamGb",
    "visibleRamGb", "modelDrive", "freeDiskGb", "totalDiskGb", "nvidiaSmi", "powerPoint", "powerPointVersion",
]


class ReportJsonTests(unittest.TestCase):
    def test_the_report_has_the_properties_and_order_of_the_c_sharp_json(self):
        data = to_dict(report_of("nvidia-n1x-128"))
        for path, keys in REPORT_KEYS.items():
            with self.subTest(".".join(map(str, path)) or "report"):
                self.assertEqual(list(reduce(lambda node, step: node[step], path, data)), keys)

    def test_the_raw_hardware_is_not_part_of_a_profile(self):
        self.assertNotIn("raw", to_dict(report_of("nvidia-n1x-128").profile))

    def test_a_detected_report_has_no_simulated_id_and_is_labelled_with_the_friendly_product_name(self):
        report = analyze_raw(raw_of("nvidia-n1x-128"))
        profile = to_dict(report)["profile"]
        self.assertEqual((profile["source"], profile["simulatedId"], profile["label"]), ("detected", None, "Surface Laptop Ultra"))
        self.assertIn('\n    "simulatedId": null,\n', to_json(report))

    def test_the_text_is_valid_json_that_reads_back_as_the_same_data(self):
        for sim in devices():
            with self.subTest(sim.id):
                report = report_of(sim.id)
                self.assertEqual(json.loads(to_json(report)), to_dict(report))


class WriterTests(unittest.TestCase):
    DATA = {"a": [1, 2.0, {"b": None}], "c": {}, "d": [], "e": "x<y", "f": True, "g": (0.5,)}

    def test_text_is_indented_by_two_spaces_with_a_space_after_each_colon(self):
        expected = (
            '{\n  "a": [\n    1,\n    2,\n    {\n      "b": null\n    }\n  ],\n  "c": {},\n  "d": [],\n  "e": "x\\u003Cy",\n  "f": true,\n  "g": [\n    0.5\n  ]\n}'
        )
        self.assertEqual(dumps(self.DATA), expected)

    def test_compact_text_has_no_white_space(self):
        self.assertEqual(dumps(self.DATA, indented=False), '{"a":[1,2,{"b":null}],"c":{},"d":[],"e":"x\\u003Cy","f":true,"g":[0.5]}')

    def test_a_number_read_with_keep_numbers_is_written_as_it_was_read(self):
        data = loads('{"a": 1.50, "b": 2, "c": [0.145, 1e3]}', keep_numbers=True)
        self.assertEqual(dumps(data, indented=False), '{"a":1.50,"b":2,"c":[0.145,1e3]}')
        self.assertIsInstance(data["a"], RawNumber)
        self.assertEqual(data["a"], 1.5)

    def test_a_value_that_is_not_json_is_refused(self):
        with self.assertRaises(TypeError):
            dumps({"a": {1, 2}})


class ReaderTests(unittest.TestCase):
    def test_comments_are_skipped_but_not_inside_strings(self):
        text = '{ // note\n  "url": "http://example.com/*x*/", /* a block\n comment */ "n": 1 }'
        self.assertEqual(loads(text), {"url": "http://example.com/*x*/", "n": 1})
        with self.assertRaises(ValueError):
            loads(text, comments=False)

    def test_nan_and_infinity_are_refused(self):
        for text in ("NaN", "[Infinity]", '{"a": -Infinity}'):
            with self.subTest(text), self.assertRaisesRegex(ValueError, "is not a valid JSON value"):
                loads(text)

    def test_trailing_commas_are_refused_as_the_c_sharp_reader_does(self):
        with self.assertRaises(ValueError):
            loads("[1, 2,]")

    def test_a_leading_byte_order_mark_is_ignored(self):
        self.assertEqual(loads("\ufeff[1]"), [1])


class ReadTextTests(unittest.TestCase):
    TEXT = '{"name": "Ré · 😀"}'

    def read(self, data: bytes) -> str:
        with patch.object(Path, "read_bytes", return_value=data):
            return read_text("any.json")

    def test_the_byte_order_mark_picks_the_encoding_and_utf_8_is_the_default(self):
        encoded = {
            "utf-8": self.TEXT.encode("utf-8"),
            "utf-8 with a mark": b"\xef\xbb\xbf" + self.TEXT.encode("utf-8"),
            "utf-16 little endian": b"\xff\xfe" + self.TEXT.encode("utf-16-le"),
            "utf-16 big endian": b"\xfe\xff" + self.TEXT.encode("utf-16-be"),
            "utf-32 little endian": b"\xff\xfe\x00\x00" + self.TEXT.encode("utf-32-le"),
            "utf-32 big endian": b"\x00\x00\xfe\xff" + self.TEXT.encode("utf-32-be"),
        }
        for name, data in encoded.items():
            with self.subTest(name):
                self.assertEqual(self.read(data), self.TEXT)

    def test_bytes_that_are_not_valid_text_are_replaced_instead_of_failing(self):
        self.assertEqual(self.read(b"a\xffb"), "a\ufffdb")


class RawHardwareTests(unittest.TestCase):
    def test_every_simulated_device_survives_its_own_json(self):
        for sim in devices():
            with self.subTest(sim.id):
                self.assertEqual(parse_raw(to_json(sim.raw)), sim.raw)

    def test_the_json_lists_the_properties_in_the_order_the_c_sharp_kit_writes_them(self):
        data = json.loads(to_json(raw_of("nvidia-n1x-128")))
        self.assertEqual(list(data), RAW_KEYS)
        self.assertEqual(list(data["gpus"][0]), ["name", "driver", "vramGb"])
        self.assertEqual(list(data["npus"][0]), ["name", "driver", "provider"])

    def test_property_names_match_without_regard_to_case(self):
        raw = parse_raw('{"PRODUCT": "Surface Pro", "osarch": "arm64", "GPUs": [{"NAME": "g", "VramGb": 4}], "logicalCORES": 12}')
        self.assertEqual((raw.product, raw.os_arch, raw.logical_cores, raw.gpus), ("Surface Pro", "arm64", 12, (RawGpu("g", "", 4.0),)))

    def test_a_missing_or_null_property_keeps_its_default(self):
        self.assertEqual(parse_raw('{"product": null, "cpu": "x", "gpus": null}'), RawHardware(cpu="x"))
        self.assertEqual(parse_raw("{}").os_arch, "x64")

    def test_comments_are_allowed_in_a_file_from_another_pc(self):
        self.assertEqual(parse_raw('{ // pasted from --raw\n "product": "Surface Pro" }').product, "Surface Pro")

    def test_a_value_of_the_wrong_type_is_named_by_its_path(self):
        cases = {
            "null": "Hardware JSON is empty.",
            "[]": "$: expected a JSON object.",
            '{"logicalCores": 1.5}': "$.logicalCores: expected an integer.",
            '{"osBuild": "x"}': "$.osBuild: expected an integer.",
            '{"nvidiaSmi": "yes"}': "$.nvidiaSmi: expected true or false.",
            '{"freeDiskGb": "lots"}': "$.freeDiskGb: expected a number.",
            '{"installedRamGb": true}': "$.installedRamGb: expected a number.",
            '{"gpus": {}}': "$.gpus: expected an array.",
            '{"gpus": [1]}': "$.gpus[0]: expected a JSON object.",
            '{"gpus": [{"name": 3}]}': "$.gpus[0].name: expected a string.",
        }
        for text, message in cases.items():
            with self.subTest(text), self.assertRaises(ValueError) as refused:
                parse_raw(text)
            self.assertEqual(str(refused.exception), message)
