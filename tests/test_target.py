"""Which model each slot runs on a given PC, and the filter that keeps a reasoning model's thinking out of what is shown."""
import os
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import device_context
import target_plan
from fakes import REFERENCE_DEVICE, FakeSource, real_catalog, reference_builds, report_for, service_for
from local_engine import LANES_ENV, LocalEngine, ThinkFilter, forced_lanes, lane_phrase
from surface_devicekit import foundry

RTX = "the RTX GPU"


def resolving_engine(device_id=REFERENCE_DEVICE, cached=None, usable=("cpu", "cuda"), **settings):
    """An engine that has been given what start() would find (the PC's report, Foundry Local's catalog and the providers it registered)."""
    engine = LocalEngine()
    engine.report = report_for(device_id)
    engine.source = real_catalog(reference_builds() if cached is None else cached)
    engine.registration = foundry.EpRegistration(usable=set(usable))
    for key, value in settings.items():
        setattr(engine, key, value)
    return engine


class ThinkFilterTests(unittest.TestCase):
    def run_chunks(self, chunks):
        parser = ThinkFilter()
        shown, hidden = "", 0
        for chunk in [*chunks, ""]:
            text, count = parser.feed(chunk, final=chunk == "")
            shown += text
            hidden += count
        return shown, hidden

    def test_reasoning_stays_hidden_however_the_stream_is_cut(self):
        framed = "<think>Weigh the options first.</think>\n\nThe answer is 42."
        for size in (1, 2, 3, 7, len(framed)):
            with self.subTest(size=size):
                chunks = [framed[i:i + size] for i in range(0, len(framed), size)]
                self.assertEqual(self.run_chunks(chunks), ("The answer is 42.", len("Weigh the options first.")))

    def test_a_stream_that_starts_inside_the_block_is_recognised_at_the_closing_tag(self):
        self.assertEqual(self.run_chunks(["Let me think about it.", "</think>Final."]),
                         ("Final.", len("Let me think about it.")))

    def test_text_with_no_tags_is_held_until_the_stream_ends(self):
        parser = ThinkFilter()
        self.assertEqual(parser.feed("Plain "), ("", 0))
        self.assertEqual(parser.pending, len("Plain "))
        self.assertEqual(parser.feed("answer.", final=True), ("Plain answer.", 0))
        self.assertEqual(parser.pending, 0)

    def test_an_unfinished_block_counts_as_hidden(self):
        self.assertEqual(self.run_chunks(["<think>never finished"]), ("", len("never finished")))

    def test_separate_reasoning_ends_the_hold(self):
        parser = ThinkFilter()
        self.assertEqual(parser.feed("Hel"), ("", 0))
        parser.reasoning_is_separate()
        self.assertEqual(parser.feed("lo"), ("Hello", 0))

    def test_angle_brackets_in_the_answer_survive(self):
        self.assertEqual(self.run_chunks(["<think>x</think>if a <", " b: ok"]), ("if a < b: ok", 1))


class LaneTests(unittest.TestCase):
    def test_lane_phrases_name_the_lane_and_never_a_speed(self):
        self.assertEqual(lane_phrase("npu"), "the NPU")
        self.assertEqual(lane_phrase("cpu"), "the CPU")
        self.assertEqual(lane_phrase("gpu", "NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU)"), RTX)
        self.assertEqual(lane_phrase("gpu", "Intel Arc 140V"), "the GPU")
        self.assertEqual(lane_phrase("gpu"), "the GPU")

    def test_forced_lanes_parse_the_diagnostic_variable(self):
        for raw, expected in (("", None), ("  ", None), ("cpu", {"cpu"}), ("NPU, gpu", {"npu", "gpu"}),
                              ("cpu;npu cpu", {"cpu", "npu"})):
            with self.subTest(raw=raw), patch.dict(os.environ, {LANES_ENV: raw}):
                self.assertEqual(forced_lanes(), expected)
        with patch.dict(os.environ, {LANES_ENV: "cpu, tpu"}), self.assertRaisesRegex(RuntimeError, "tpu"):
            forced_lanes()


class EngineTargetTests(unittest.TestCase):
    def setUp(self):
        previous = device_context.install(service_for())
        self.addCleanup(device_context.install, previous)
        patcher = patch.dict(os.environ, {LANES_ENV: ""})
        patcher.start()
        self.addCleanup(patcher.stop)

    def preview(self, device_id, **options):
        return LocalEngine().preview(report_for(device_id), forced=False, **options)

    def test_an_engine_that_has_not_chosen_yet_runs_the_models_the_demos_were_tuned_on(self):
        engine = LocalEngine()
        self.assertEqual(engine.describe(None), {"label": "Qwen2.5-Coder 14B", "where": RTX, "lane": "gpu", "available": True})
        self.assertEqual(engine.describe("gpt-oss-20b")["label"], "GPT-OSS 20B")
        self.assertEqual([item["where"] for item in engine.expected_models()], [RTX] * 5)
        self.assertEqual(engine.unavailable_models(), [])
        self.assertFalse(engine.target_info()["resolved"])

    def test_a_large_nvidia_pc_keeps_the_tuned_models(self):
        engine = self.preview("nvidia-n1x-64")
        self.assertEqual(engine.alias_for("qwen2.5-coder-14b"), "qwen2.5-coder-14b")
        self.assertEqual(engine.alias_for("gpt-oss-20b"), "gpt-oss-20b")
        self.assertEqual({item["where"] for item in engine.expected_models()}, {RTX})
        self.assertEqual([slot["model"] is not None for slot in engine.target_info()["slots"]], [True] * 5)

    def test_a_snapdragon_pc_runs_everything_on_the_npu_and_maps_the_tuned_names_to_its_models(self):
        engine = self.preview("snapdragon-x2-elite-24")
        self.assertEqual(engine.alias_for("qwen2.5-coder-14b"), "qwen2.5-7b")
        self.assertEqual(engine.alias_for("gpt-oss-20b"), "deepseek-r1-7b")
        self.assertEqual(engine.describe(None), {"label": "Qwen2.5 7B", "where": "the NPU", "lane": "npu", "available": True})
        self.assertEqual({(item["lane"], item["where"]) for item in engine.expected_models()}, {("npu", "the NPU")})
        self.assertTrue(engine.reasoning("gpt-oss-20b"), "the reasoning slot's model thinks before it answers")
        self.assertEqual(engine._channel("gpt-oss-20b"), "think")

    def test_a_pc_with_no_room_for_a_reasoning_model_says_why(self):
        engine = self.preview("intel-pentium-8")
        self.assertIsNone(engine.alias_for("gpt-oss-20b"))
        self.assertIn("CPU", engine.blocker_for("gpt-oss-20b"))
        with self.assertRaisesRegex(RuntimeError, "CPU"):
            engine.require_alias("gpt-oss-20b")
        self.assertEqual(engine.describe("gpt-oss-20b")["available"], False)
        self.assertEqual([row["slot"] for row in engine.unavailable_models()], ["reasoner"])
        self.assertEqual(len(engine.expected_models()), 4)
        self.assertEqual(engine.alias_for("qwen2.5-coder-14b"), "qwen2.5-0.5b")

    def test_a_preview_is_provisional_until_the_catalog_answers(self):
        engine = self.preview("nvidia-n1x-64")
        self.assertTrue(engine.provisional)
        self.assertFalse(engine.target_info()["resolved"])
        self.assertIs(engine.preview(report_for("snapdragon-x2-elite-24"), forced=False), engine)
        self.assertEqual(engine.where(), "the NPU", "a provisional engine is previewed again for another device")
        resolving = resolving_engine()
        resolving._resolve()
        self.assertFalse(resolving.provisional)
        self.assertTrue(resolving.target_info()["resolved"])
        self.assertIs(resolving.preview(report_for("snapdragon-x2-elite-24"), forced=False), resolving)
        self.assertEqual(resolving.where(), RTX, "a resolved engine is never replaced by an estimate")

    def test_forced_lanes_apply_only_when_asked(self):
        with patch.dict(os.environ, {LANES_ENV: "cpu"}):
            forced = LocalEngine().preview(report_for("nvidia-n1x-64"))
            free = LocalEngine().preview(report_for("nvidia-n1x-64"), forced=False)
        self.assertEqual(forced.lanes, {"cpu"})
        self.assertEqual(forced.describe(None), {"label": "Qwen2.5 7B", "where": "the CPU", "lane": "cpu", "available": True})
        self.assertEqual(forced.target_info()["lanes"], ["cpu"])
        self.assertIsNone(free.lanes)
        self.assertEqual(free.where(), RTX)

    def test_residency_asks_each_loaded_model_because_the_catalog_list_leaves_out_speech_and_embedding(self):
        def handle(model_id, loaded=True):
            return SimpleNamespace(id=model_id, is_loaded=loaded)

        engine = LocalEngine()
        engine.manager = SimpleNamespace(catalog=SimpleNamespace(get_loaded_models=lambda: [handle("chat:4")]))
        engine.model = handle("chat:4")
        engine.guests = {"reasoner": handle("think:4")}
        engine.media = {"asr": handle("asr:4"), "embed": handle("embed:1", loaded=False)}
        self.assertEqual(engine.resident(), ["asr:4", "chat:4", "think:4"])
        engine.manager = None
        self.assertEqual(engine.resident(), [])

    def test_the_catalog_decides_the_builds_on_this_pc(self):
        engine = resolving_engine()
        rows = engine._resolve()
        self.assertEqual([(row["alias"], row["cached"], row["where"]) for row in rows],
                         [("qwen2.5-coder-14b", True, RTX), ("gpt-oss-20b", True, RTX)])
        self.assertEqual(engine.models["qwen2.5-coder-14b"]["id"], "qwen2.5-coder-14b-instruct-cuda-gpu:4")
        self.assertEqual(engine.models["gpt-oss-20b"]["id"], "gpt-oss-20b-cuda-gpu:1")
        self.assertEqual(engine._channel("gpt-oss-20b"), "harmony")
        self.assertEqual({role: spec["where"] for role, spec in engine.media_models.items()},
                         {"asr": RTX, "vision": RTX, "embed": RTX})
        self.assertEqual(engine.media_models["asr"]["id"], "openai-whisper-medium-cuda-gpu:3")

    def test_a_model_that_is_not_cached_is_named_but_not_reported_as_cached(self):
        rows = resolving_engine(cached=())._resolve()
        self.assertEqual([(row["alias"], row["cached"]) for row in rows], [("qwen2.5-coder-14b", False), ("gpt-oss-20b", False)])

    def test_a_build_that_fails_to_load_is_replaced_and_not_offered_again(self):
        engine = resolving_engine()
        engine._resolve()
        spec = engine.models["qwen2.5-coder-14b"]
        said = engine._note_failure(spec)
        self.assertIn(spec["id"], engine.target_info()["failed"])
        self.assertTrue(engine.retry_chat)
        self.assertTrue(said.endswith("is chosen instead."), said)
        replacement = next(iter(engine.models.values()))
        self.assertNotEqual(replacement["id"], spec["id"])
        self.assertIn(replacement["label"], said)

    def test_a_load_failure_names_the_model_the_lane_and_the_reason(self):
        engine = resolving_engine()
        engine._resolve()
        spec = engine.models["qwen2.5-coder-14b"]

        class Broken:
            @staticmethod
            def load():
                raise OSError("the driver refused\nsecond line")

        with self.assertRaises(RuntimeError) as raised:
            engine._load(Broken(), spec)
        message = str(raised.exception)
        self.assertIn("Qwen2.5-Coder 14B did not load on the RTX GPU: the driver refused.", message)
        self.assertNotIn("second line", message)
        self.assertIn("is chosen instead.", message)

    def test_a_failure_with_nothing_to_fall_back_to_says_so(self):
        only_build = foundry.CatalogVariant("qwen2.5-0.5b-instruct-generic-cpu:4", "qwen2.5-0.5b", "CPU", "cpu",
                                            "CPUExecutionProvider", 822, 4, True, "chat-completion", None)
        engine = resolving_engine("intel-pentium-8", usable=("cpu",), source=FakeSource([only_build]))
        engine._resolve()
        spec = engine.models["qwen2.5-0.5b"]
        self.assertEqual(engine._note_failure(spec), "No other build of Qwen2.5 0.5B fits this PC.")
        self.assertFalse(engine.retry_chat)
        self.assertIsNone(engine.alias_for("qwen2.5-coder-14b"))


class TargetPlanTests(unittest.TestCase):
    def setUp(self):
        previous = device_context.install(service_for())
        self.addCleanup(device_context.install, previous)

    def choose(self, cached, **options):
        report = report_for(REFERENCE_DEVICE)
        return target_plan.choose(report, device_context.service().catalog, real_catalog(cached), usable={"cpu", "cuda"}, **options)

    def test_with_downloads_allowed_the_strongest_build_wins(self):
        choices = self.choose(cached=(), allow_download=True)
        self.assertEqual([choices[slot.key].model.alias for slot in target_plan.SLOTS],
                         ["qwen2.5-coder-14b", "gpt-oss-20b", "whisper-medium", "qwen3-vl-4b-instruct", "qwen3-embedding-0.6b"])
        self.assertTrue(all(choice.better is None and not choice.estimated for choice in choices.values()))

    def test_without_downloads_a_cached_build_wins_and_the_stronger_one_is_reported(self):
        cached = {"qwen2.5-14b-instruct-cuda-gpu:4"}
        choice = self.choose(cached=cached)["chat"]
        self.assertEqual(choice.model.alias, "qwen2.5-14b")
        self.assertTrue(choice.model.cached)
        self.assertEqual(choice.better.alias, "qwen2.5-coder-14b")

    def test_an_estimate_names_every_slot_from_the_kit_sizes_alone(self):
        catalog = device_context.service().catalog
        choices = target_plan.estimate(report_for("intel-pentium-8"), catalog)
        self.assertTrue(all(choice.estimated for choice in choices.values()))
        self.assertIsNone(choices["reasoner"].model)
        self.assertIn("CPU", choices["reasoner"].blocker)
        self.assertEqual(choices["chat"].model.lane, "cpu")
        forced = target_plan.estimate(report_for("nvidia-n1x-64"), catalog, lanes={"cpu"})
        self.assertEqual({choice.model.lane for choice in forced.values()}, {"cpu"})

    def test_only_a_gpu_lane_with_cuda_needs_the_cuda_supplement(self):
        self.assertTrue(target_plan.has_cuda_lane(report_for("nvidia-n1x-64")))
        self.assertTrue(target_plan.has_cuda_lane(report_for("nvidia-dgpu-16")))
        self.assertFalse(target_plan.has_cuda_lane(report_for("snapdragon-x2-elite-24")))
        self.assertFalse(target_plan.has_cuda_lane(report_for("nvidia-n1x-64"), lanes={"cpu"}))


if __name__ == "__main__":
    unittest.main()
