import unittest

from surface_devicekit import selftest
from surface_devicekit.capabilities import LIMITED, UNAVAILABLE

from tests.support import catalog, catalog_with, devices, report_of

# (device id, device class, PC class, lane, model alias) for the structured-json workload, as in the C# DeviceKitSelfTest
KNOWN_ANSWERS = (
    ("nvidia-n1x-128", "gpu-first", "builder", "gpu", "qwen2.5-coder-14b"),
    ("nvidia-n1x-64", "gpu-first", "builder", "gpu", "qwen2.5-coder-14b"),
    ("nvidia-n1x-32", "gpu-first", "builder", "gpu", "qwen2.5-coder-14b"),
    ("nvidia-dgpu-64", "gpu-first", "gpu", "gpu", "qwen2.5-7b"),
    ("nvidia-dgpu-32", "gpu-first", "gpu", "gpu", "qwen2.5-7b"),
    ("nvidia-dgpu-24", "gpu-first", "gpu", "gpu", "qwen2.5-7b"),
    ("nvidia-dgpu-16", "gpu-first", "gpu", "gpu", "phi-4-mini"),
    ("snapdragon-x-elite-64", "npu-first", "copilot-plus", "npu", "qwen2.5-7b"),
    ("snapdragon-x-elite-16", "npu-first", "copilot-plus", "npu", "qwen2.5-7b"),
    ("snapdragon-x2-plus-8", "npu-first", "npu", "npu", "qwen2.5-0.5b"),
    ("snapdragon-x-elite-16-emulated", "cpu-only", "copilot-plus", "cpu", "phi-4-mini"),
    ("snapdragon-sq3-16", "igpu-first", "windows", "gpu", "qwen2.5-7b"),
    ("intel-lunar-lake-16", "npu-first", "copilot-plus", "npu", "qwen2.5-7b"),
    ("intel-meteor-lake-16", "igpu-first", "npu", "gpu", "qwen2.5-7b"),
    ("intel-12th-gen-16", "igpu-first", "windows", "gpu", "qwen2.5-7b"),
)

CHECK_NAMES = (
    "catalog: every workload candidate is a known model",
    "catalog: availableFrom is one of the candidates",
    "catalog: lane orders only name known lanes",
    "catalog: every lane kind has a class cap and a budget rule",
    "catalog: model classes are known",
    "simulated profile ids are unique",
    "every simulated device has a structured-json model",
    "ladders are ordered, within the class cap and within budget",
    *(f"{device_id}: {device_class} / {pc_class} / {lane}:{alias}" for device_id, device_class, pc_class, lane, alias in KNOWN_ANSWERS),
    "n1x: the NPU is listed but not used",
    "n1x: graphics memory comes from the GPU, not the visible RAM",
    "n1x: the RAM tier uses installed memory",
    "meteor lake: a 11 TOPS NPU is not used for language models",
    "emulated build reports limited native-build",
    "x2 plus with 8 GB is not Copilot+ class",
    "16 GB CPU lane budget: phi-4-mini fits, qwen2.5-7b does not",
)


def capability_status(device_id: str, capability_id: str) -> str:
    return next(c.status for c in report_of(device_id).profile.capabilities if c.id == capability_id)


class CatalogConsistencyTests(unittest.TestCase):
    def test_every_workload_candidate_is_a_known_model(self):
        for workload in catalog().workloads.values():
            for alias in workload.candidates:
                self.assertIn(alias, catalog().models, workload.id)

    def test_available_from_is_one_of_the_candidates(self):
        for workload in catalog().workloads.values():
            self.assertIn(workload.available_from, workload.candidates, workload.id)

    def test_lane_orders_only_name_known_lanes(self):
        for device_class, order in catalog().lane_order.items():
            for lane in order:
                self.assertIn(lane, catalog().lanes, device_class)

    def test_every_lane_kind_has_a_class_cap_and_a_budget_rule(self):
        for kind in ("gpu-discrete", "gpu-unified", "gpu-integrated", "npu", "cpu"):
            self.assertIn(kind, catalog().model_classes.lane_cap)
            self.assertGreater(catalog().budget.rule(kind).fraction, 0, kind)

    def test_model_classes_are_known(self):
        for model in catalog().models.values():
            self.assertIn(model.class_, catalog().model_classes.order, model.alias)

    def test_simulated_profile_ids_are_unique(self):
        ids = [d.id for d in devices()]
        self.assertEqual(len(ids), len(set(ids)))


class SimulatedDeviceChecks(unittest.TestCase):
    def test_every_simulated_device_has_a_structured_json_model(self):
        for sim in devices():
            self.assertIsNotNone(report_of(sim.id).plans["structured-json"].primary, sim.id)

    def test_ladders_are_ordered_within_the_class_cap_and_within_the_budget(self):
        classes = catalog().model_classes
        for sim in devices():
            report = report_of(sim.id)
            for workload_id, plan in report.plans.items():
                candidates = catalog().workloads[workload_id].candidates
                for lane in plan.lanes:
                    with self.subTest(device=sim.id, workload=workload_id, lane=lane.lane):
                        cap = classes.rank(report.profile.lane(lane.lane).class_cap)
                        positions = [candidates.index(m.alias) for m in lane.ladder]
                        self.assertEqual(positions, sorted(set(positions)))
                        for m in lane.ladder:
                            self.assertLessEqual(m.required_gb, lane.budget_gb + 0.001, m.alias)
                            self.assertLessEqual(classes.rank(m.class_), cap, m.alias)

    def test_known_answers_for_structured_json(self):
        for device_id, device_class, pc_class, lane, alias in KNOWN_ANSWERS:
            with self.subTest(device_id):
                report = report_of(device_id)
                plan = report.plans["structured-json"]
                self.assertEqual(report.profile.device_class, device_class)
                self.assertEqual(report.profile.pc_class, pc_class)
                self.assertEqual(plan.primary_lane, lane)
                self.assertEqual(plan.primary.alias, alias)

    def test_n1x_npu_is_listed_but_not_used(self):
        n1x = report_of("nvidia-n1x-128").profile
        self.assertEqual(len(n1x.npus), 1)
        self.assertFalse(n1x.npus[0].llm_capable)
        self.assertFalse(n1x.lane("npu").available)

    def test_n1x_graphics_memory_comes_from_the_gpu_not_the_visible_ram(self):
        n1x = report_of("nvidia-n1x-128").profile
        self.assertGreater(n1x.lane("gpu").budget_gb, 55)
        self.assertLess(n1x.lane("cpu").budget_gb, 35)

    def test_n1x_ram_tier_uses_installed_memory(self):
        self.assertEqual(report_of("nvidia-n1x-128").profile.memory.tier, 128)

    def test_meteor_lake_npu_is_too_slow_for_language_models(self):
        self.assertFalse(report_of("intel-meteor-lake-16").profile.lane("npu").available)

    def test_emulated_build_reports_a_limited_native_build(self):
        self.assertEqual(capability_status("snapdragon-x-elite-16-emulated", "native-build"), LIMITED)

    def test_x2_plus_with_8_gb_is_not_copilot_plus_class(self):
        self.assertEqual(capability_status("snapdragon-x2-plus-8", "copilot-plus"), UNAVAILABLE)

    def test_a_16_gb_cpu_lane_fits_phi_4_mini_but_not_qwen_7b(self):
        cpu = next(lane for lane in report_of("snapdragon-x-elite-16-emulated").plans["chat"].lanes if lane.lane == "cpu")
        aliases = [m.alias for m in cpu.ladder]
        self.assertIn("phi-4-mini", aliases)
        self.assertNotIn("qwen2.5-7b", aliases)


def failures(results) -> dict[str, str]:
    return {r.name: r.detail for r in results if not r.passed}


class SelfTestRunnerTests(unittest.TestCase):
    def test_all_thirty_checks_run_in_order_and_pass(self):
        results = selftest.run(catalog())
        self.assertEqual(len(CHECK_NAMES), 30)
        self.assertEqual([r.name for r in results], list(CHECK_NAMES))
        self.assertEqual(failures(results), {})

    def test_the_shipped_catalog_is_used_when_none_is_given(self):
        self.assertEqual(failures(selftest.run()), {})

    def test_an_unknown_candidate_is_named(self):
        broken = catalog_with(lambda d: d["workloads"]["vision"]["candidates"].append("no-such-model"))
        self.assertEqual(failures(selftest.run(broken)), {CHECK_NAMES[0]: "vision:no-such-model"})

    def test_an_available_from_outside_the_candidates_is_named(self):
        broken = catalog_with(lambda d: d["workloads"]["code"].update(availableFrom="nope"))
        self.assertEqual(failures(selftest.run(broken)), {CHECK_NAMES[1]: "code"})

    def test_a_lane_order_with_an_unknown_lane_is_named(self):
        broken = catalog_with(lambda d: d["laneOrder"].update({"quantum-first": ["qpu"]}))
        self.assertEqual(failures(selftest.run(broken)), {CHECK_NAMES[2]: "qpu"})

    def test_a_lane_kind_without_a_budget_is_named(self):
        broken = catalog_with(lambda d: d["budget"]["npu"].update(fraction=0))
        self.assertEqual(failures(selftest.run(broken))[CHECK_NAMES[3]], "npu")

    def test_an_unknown_model_class_is_named(self):
        broken = catalog_with(lambda d: d["models"]["qwen2.5-0.5b"].update({"class": "XXL"}))
        self.assertEqual(failures(selftest.run(broken)), {CHECK_NAMES[4]: "qwen2.5-0.5b"})

    def test_duplicate_simulated_ids_fail_the_uniqueness_check(self):
        sims = devices()
        self.assertEqual(failures(selftest.run(catalog(), (*sims, sims[0]))), {CHECK_NAMES[5]: ""})

    def test_devices_without_a_structured_json_model_are_listed(self):
        broken = catalog_with(lambda d: d["workloads"]["structured-json"].update(candidates=["qwen2.5-coder-14b"]))
        self.assertIn("intel-pentium-8", failures(selftest.run(broken))[CHECK_NAMES[6]])

    def test_a_ladder_that_repeats_a_model_is_reported_with_at_most_five_entries(self):
        def repeat_first(data):
            candidates = data["workloads"]["vision"]["candidates"]
            candidates.insert(1, candidates[0])

        detail = failures(selftest.run(catalog_with(repeat_first)))[CHECK_NAMES[7]]
        entries = detail.split(", ")
        self.assertEqual(len(entries), 5)
        for entry in entries:
            self.assertRegex(entry, r"^[a-z0-9-]+/vision/(npu|gpu|cpu)/qwen3-vl-8b-instruct$")

    def test_a_changed_answer_shows_what_was_found_instead(self):
        broken = catalog_with(lambda d: d["modelClasses"]["laneCap"].update({"gpu-unified": "M"}))
        name = "nvidia-n1x-128: gpu-first / builder / gpu:qwen2.5-coder-14b"
        detail = failures(selftest.run(broken))[name]
        self.assertTrue(detail.startswith("got gpu-first / builder / gpu:"), detail)
        self.assertNotIn("coder-14b", detail)

    def test_a_device_set_without_a_device_a_check_needs_is_an_error(self):
        with self.assertRaisesRegex(KeyError, "nvidia-n1x-128"):
            selftest.run(catalog(), [d for d in devices() if d.id != "nvidia-n1x-128"])
