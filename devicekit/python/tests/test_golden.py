import json
import unittest

from surface_devicekit import golden
from surface_devicekit.jsonio import read_text
from surface_devicekit.models import DeviceReport

from tests.support import GOLDEN_PATH, catalog, devices, report_of


def golden_data() -> dict:
    return json.loads(GOLDEN_PATH.read_text(encoding="utf-8-sig"))


def layout(node):
    """Every object's keys in document order and every array's length, without the values."""
    if isinstance(node, dict):
        return [(key, layout(value)) for key, value in node.items()]
    if isinstance(node, list):
        return [layout(value) for value in node]
    return None


class GoldenParityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.expected = golden_data()

    def assert_matches(self, want: dict, report: DeviceReport) -> None:
        p, tolerance = report.profile, golden.TOLERANCE
        self.assertEqual(p.device_class, want["deviceClass"])
        self.assertEqual(p.pc_class, want["pcClass"])
        self.assertEqual(p.cpu.family_id, want["familyId"])
        self.assertEqual(p.os.emulated, want["emulated"])
        self.assertEqual(p.memory.tier, want["ramTier"])
        self.assertAlmostEqual(p.memory.installed_gb, want["installedGb"], delta=tolerance)
        self.assertAlmostEqual(p.memory.visible_gb, want["visibleGb"], delta=tolerance)

        self.assertEqual(len(p.gpus), len(want["gpus"]))
        for got, exp in zip(p.gpus, want["gpus"]):
            self.assertEqual((got.vendor, got.kind), (exp["vendor"], exp["kind"]))
            self.assertAlmostEqual(got.vram_gb, exp["vramGb"], delta=tolerance)

        self.assertEqual(len(p.npus), len(want["npus"]))
        for got, exp in zip(p.npus, want["npus"]):
            self.assertEqual(got.llm_capable, exp["llmCapable"])
            if exp["ratedTops"] is None:
                self.assertIsNone(got.rated_tops)
            else:
                self.assertAlmostEqual(got.rated_tops, exp["ratedTops"], delta=tolerance)

        self.assertEqual([lane.id for lane in p.lanes], [lane["id"] for lane in want["lanes"]])
        for got, exp in zip(p.lanes, want["lanes"]):
            self.assertEqual((got.available, got.class_cap, list(got.eps)), (exp["available"], exp["classCap"], exp["eps"]))
            self.assertAlmostEqual(got.budget_gb, exp["budgetGb"], delta=tolerance)

        self.assertEqual(
            [(c.id, c.status, c.lane) for c in p.capabilities],
            [(c["id"], c["status"], c["lane"]) for c in want["capabilities"]],
        )

        self.assertEqual(list(report.plans), list(want["plans"]))
        for workload, exp in want["plans"].items():
            plan = report.plans[workload]
            self.assertEqual(plan.primary_lane, exp["primaryLane"], workload)
            self.assertEqual(plan.primary.alias if plan.primary else None, exp["primary"], workload)
            self.assertEqual([lane.lane for lane in plan.lanes], list(exp["ladders"]), workload)
            self.assertEqual({lane.lane: [m.alias for m in lane.ladder] for lane in plan.lanes}, exp["ladders"], workload)

    def test_the_golden_file_has_one_entry_per_simulated_device_in_order(self):
        self.assertEqual(list(self.expected["devices"]), [d.id for d in devices()])
        self.assertEqual(self.expected["schema"], 1)
        self.assertEqual(self.expected["catalogVersion"], catalog().catalog_version)

    def test_every_simulated_profile_matches_its_golden_entry(self):
        for sim in devices():
            with self.subTest(device=sim.id):
                self.assert_matches(self.expected["devices"][sim.id], report_of(sim.id))

    def test_the_built_vectors_match_the_golden_file(self):
        self.assertEqual(golden.compare(read_text(GOLDEN_PATH), golden.build(catalog())), [])

    def test_the_built_vectors_use_the_golden_files_keys_in_the_same_order(self):
        built = json.loads(golden.build(catalog()))
        self.assertEqual(layout(built), layout(self.expected))
        self.assertEqual(built["note"], self.expected["note"])

    def test_vectors_are_built_for_the_devices_given(self):
        subset = devices()[:2]
        built = json.loads(golden.build(catalog(), subset))
        self.assertEqual(list(built["devices"]), [d.id for d in subset])

    def test_a_changed_answer_is_reported_with_its_path(self):
        changed = json.loads(golden.build(catalog()))
        first = next(iter(changed["devices"]))
        changed["devices"][first]["lanes"][0]["budgetGb"] += 1
        changed["devices"][first]["plans"]["chat"]["primary"] = "not-a-model"
        diffs = golden.compare(read_text(GOLDEN_PATH), json.dumps(changed))
        self.assertEqual(len(diffs), 2)
        self.assertTrue(diffs[0].startswith(f"$.devices.{first}.lanes[0].budgetGb: expected "))
        self.assertTrue(diffs[1].startswith(f"$.devices.{first}.plans.chat.primary: expected "))
        self.assertTrue(diffs[1].endswith('got "not-a-model"'))


class GoldenCompareTests(unittest.TestCase):
    def test_numbers_match_within_the_tolerance(self):
        self.assertEqual(golden.compare('{"gb": 7.02}', '{"gb": 7.03}'), [])
        self.assertEqual(golden.compare('{"gb": 7.02}', '{"gb": 7.01}'), [])
        self.assertEqual(golden.compare('{"gb": 7.02}', '{"gb": 7.04}'), ["$.gb: expected 7.02, got 7.04"])
        self.assertEqual(golden.compare('{"gb": 8}', '{"gb": 8.0}'), [])

    def test_numbers_are_quoted_as_they_were_written(self):
        self.assertEqual(golden.compare('{"gb": 8.0}', '{"gb": 9}'), ["$.gb: expected 8.0, got 9"])
        self.assertEqual(golden.compare('{"gb": 7.020}', '{"gb": 7.5}'), ["$.gb: expected 7.020, got 7.5"])

    def test_everything_but_numbers_must_be_equal(self):
        self.assertEqual(golden.compare('{"lane": "gpu"}', '{"lane": "npu"}'), ['$.lane: expected "gpu", got "npu"'])
        self.assertEqual(golden.compare('{"ok": true}', '{"ok": false}'), ["$.ok: expected true, got false"])
        self.assertEqual(golden.compare('{"ok": true}', '{"ok": 1}'), ["$.ok: expected true, got 1"])
        self.assertEqual(golden.compare('{"n": 1}', '{"n": "1"}'), ['$.n: expected 1, got "1"'])
        self.assertEqual(golden.compare('{"lane": "g\\u0070u"}', '{"lane": "gpu"}'), [])

    def test_strings_are_quoted_the_way_the_c_sharp_serializer_writes_them(self):
        self.assertEqual(golden.compare('{"s": "a\\"b"}', '{"s": "c<d"}'), ['$.s: expected "a\\u0022b", got "c\\u003Cd"'])
        self.assertEqual(golden.compare('{"s": "caf\\u00e9"}', '{"s": "x"}'), ['$.s: expected "caf\\u00E9", got "x"'])

    def test_null_only_equals_null(self):
        self.assertEqual(golden.compare('{"top": null}', '{"top": null}'), [])
        self.assertEqual(golden.compare('{"top": null}', '{"top": 45}'), ["$.top: expected null, got 45"])
        self.assertEqual(golden.compare('{"top": 45}', '{"top": null}'), ["$.top: expected 45, got null"])
        self.assertEqual(golden.compare('{"x": null}', '{"x": []}'), ["$.x: expected null, got []"])

    def test_missing_keys_come_before_unexpected_ones_whatever_the_key_order(self):
        self.assertEqual(golden.compare('{"a": 1, "b": 2}', '{"a": 1, "c": 3}'), ["$.b: missing", "$.c: unexpected"])
        self.assertEqual(golden.compare('{"a": 1, "b": 2}', '{"c": 3, "a": 1}'), ["$.b: missing", "$.c: unexpected"])

    def test_arrays_are_compared_by_position(self):
        self.assertEqual(golden.compare('{"x": [1, 2, 3]}', '{"x": [1, 2]}'), ["$.x: expected 3 items, got 2"])
        self.assertEqual(golden.compare('{"x": [1]}', '{"x": [1, 2, 3]}'), ["$.x: expected 1 items, got 3"])
        self.assertEqual(golden.compare('{"x": [1, 2]}', '{"x": [1, 5]}'), ["$.x[1]: expected 2, got 5"])
        self.assertEqual(
            golden.compare('{"x": ["gpu", "cpu"]}', '{"x": ["cpu", "gpu"]}'),
            ['$.x[0]: expected "gpu", got "cpu"', '$.x[1]: expected "cpu", got "gpu"'],
        )

    def test_a_different_kind_of_node_is_a_shape_difference(self):
        self.assertEqual(golden.compare('{"a": {"b": 1}}', '{"a": [1]}'), ["$.a: the shape differs"])
        self.assertEqual(golden.compare('{"a": {"b": 1}}', '{"a": 1}'), ["$.a: the shape differs"])
        self.assertEqual(golden.compare('{"a": [1]}', '{"a": "x"}'), ["$.a: the shape differs"])
        self.assertEqual(golden.compare("[1]", '{"a": 1}'), ["$: the shape differs"])

    def test_paths_run_through_nested_objects_and_arrays(self):
        self.assertEqual(
            golden.compare('{"a": {"b": {"c": [{"d": 1}]}}}', '{"a": {"b": {"c": [{"d": 2}]}}}'), ["$.a.b.c[0].d: expected 1, got 2"]
        )

    def test_text_that_is_not_plain_json_is_an_error(self):
        for text in ('{"a": 1 /* comment */}', '{"a": NaN}', ""):
            with self.subTest(text=text), self.assertRaises(ValueError):
                golden.compare(text, "{}")
