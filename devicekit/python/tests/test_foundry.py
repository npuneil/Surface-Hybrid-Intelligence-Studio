import json
import unittest
from enum import StrEnum
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from surface_devicekit import foundry
from surface_devicekit.foundry import CatalogVariant, SdkModelSource, ep_label, norm_ep, register_eps, resolve, resolve_model
from surface_devicekit.models import UnknownKeyError

from tests.support import catalog, report_of

FIXTURE = Path(__file__).parent / "data" / "foundry-catalog-arm64-cuda.json"


def variant(alias, device, ep, mb, *, version=1, cached=False, task="chat-completion", id=None, context=None):
    token = norm_ep(ep)
    return CatalogVariant(
        id or f"{alias}-{token}-{device.lower()}:{version}", alias, device, token, ep, mb, version, cached, task, context
    )


class FakeSource:
    def __init__(self, builds):
        self.rows = {}
        for build in builds:
            self.rows.setdefault(build.alias, []).append(build)

    def variants(self, alias):
        return self.rows.get(alias)


def real_catalog(cached=()):
    """The 96 builds a live Arm64 PC with only the CUDA provider registered lists; nothing is cached unless named."""
    doc = json.loads(FIXTURE.read_text(encoding="utf-8"))
    return FakeSource(
        CatalogVariant(
            b["id"], b["alias"], b["device"], norm_ep(b["ep"]), b["ep"], b["mb"], b["version"], b["id"] in cached, b["task"] or "", b["contextLength"]
        )
        for b in doc["builds"]
    )


def plan(device_id, workload, source, usable, **options):
    return resolve(report_of(device_id), catalog(), workload, source, usable=usable, **options)


class NamesTests(unittest.TestCase):
    def test_providers_are_normalised_whatever_the_spelling(self):
        self.assertEqual(norm_ep("CUDAExecutionProvider"), "cuda")
        self.assertEqual(norm_ep("NvTensorRTRTXExecutionProvider"), "nvtensorrtrtx")
        self.assertEqual(norm_ep("Vitis-AI_ExecutionProvider"), "vitisai")
        self.assertEqual(norm_ep(None), "")

    def test_labels_name_the_provider_a_person_knows(self):
        self.assertEqual(ep_label("QNNExecutionProvider"), "QNN")
        self.assertEqual(ep_label("NvTensorRTRTXExecutionProvider"), "TensorRT for RTX")
        self.assertEqual(ep_label("DmlExecutionProvider"), "DirectML")
        self.assertEqual(ep_label(""), "unknown")
        self.assertEqual(ep_label("SomethingNewExecutionProvider"), "somethingnew")


class BigNvidiaPcTests(unittest.TestCase):
    """The 128 GB Surface Laptop Ultra class: the app's tuned models fit on the GPU lane and come out as the builds they were tuned on."""

    usable = {"cpu", "cuda"}

    def test_the_tuned_models_resolve_to_the_exact_builds_they_were_tuned_on(self):
        source = real_catalog()
        expected = {
            "structured-json": ("qwen2.5-coder-14b", "qwen2.5-coder-14b-instruct-cuda-gpu:4"),
            "code": ("qwen2.5-coder-14b", "qwen2.5-coder-14b-instruct-cuda-gpu:4"),
            "reasoning": ("gpt-oss-20b", "gpt-oss-20b-cuda-gpu:1"),
            "speech-to-text": ("whisper-medium", "openai-whisper-medium-cuda-gpu:3"),
            "embeddings": ("qwen3-embedding-0.6b", "qwen3-embedding-0.6b-cuda-gpu:1"),
        }
        for workload, (alias, variant_id) in expected.items():
            with self.subTest(workload):
                primary = plan("nvidia-n1x-128", workload, source, self.usable).primary
                self.assertEqual((primary.alias, primary.variant_id), (alias, variant_id))
                self.assertEqual((primary.lane, primary.ep_label), ("gpu", "CUDA"))

    def test_a_preferred_model_goes_first_and_the_catalog_order_decides_without_it(self):
        source = real_catalog()
        self.assertEqual(plan("nvidia-n1x-128", "vision", source, self.usable).primary.alias, "qwen3-vl-8b-instruct")
        tuned = plan("nvidia-n1x-128", "vision", source, self.usable, prefer=("qwen3-vl-4b-instruct",))
        self.assertEqual(tuned.primary.variant_id, "qwen3-vl-4b-instruct-cuda-gpu:2")
        self.assertEqual([o.alias for o in tuned.options if o.lane == "gpu"].count("qwen3-vl-4b-instruct"), 1)

    def test_the_cpu_lane_follows_the_gpu_lane_as_the_fallback(self):
        options = plan("nvidia-n1x-128", "structured-json", real_catalog(), self.usable).options
        lanes = [o.lane for o in options]
        self.assertEqual(lanes, sorted(lanes, key=["gpu", "cpu"].index))
        self.assertIn("cpu", lanes)

    def test_the_class_cap_keeps_large_models_off_the_cpu_lane(self):
        cap = catalog().model_classes.rank("M")
        cpu = [o for o in plan("nvidia-n1x-128", "reasoning", real_catalog(), self.usable).options if o.lane == "cpu"]
        self.assertTrue(cpu)
        self.assertTrue(all(catalog().model_classes.rank(o.class_) <= cap for o in cpu))

    def test_every_option_fits_the_lane_it_runs_on(self):
        report = report_of("nvidia-n1x-128")
        for workload in catalog().workloads:
            for option in plan("nvidia-n1x-128", workload, real_catalog(), self.usable).options:
                self.assertLessEqual(option.required_gb, report.profile.lane(option.lane).budget_gb, (workload, option.alias))

    def test_a_cached_build_is_reported_and_preferred_over_an_uncached_one_of_the_same_rank(self):
        source = FakeSource([variant("qwen2.5-7b", "GPU", "CUDAExecutionProvider", 4843, version=3), variant("qwen2.5-7b", "GPU", "CUDAExecutionProvider", 4843, version=2, cached=True)])
        primary = plan("nvidia-n1x-128", "chat", source, self.usable).primary
        self.assertEqual((primary.version, primary.cached), (2, True))
        self.assertTrue(primary.summary.endswith("on this PC"))

    def test_a_newer_version_wins_a_tie(self):
        source = FakeSource([variant("qwen2.5-7b", "GPU", "CUDAExecutionProvider", 4843, version=2), variant("qwen2.5-7b", "GPU", "CUDAExecutionProvider", 4843, version=4)])
        self.assertEqual(plan("nvidia-n1x-128", "chat", source, self.usable).primary.version, 4)

    def test_lanes_can_be_limited_for_a_diagnostic_run(self):
        options = plan("nvidia-n1x-128", "structured-json", real_catalog(), self.usable, lanes={"cpu"}).options
        self.assertTrue(options)
        self.assertEqual({o.lane for o in options}, {"cpu"})

    def test_a_variant_that_failed_to_start_is_left_out_of_later_plans(self):
        source = real_catalog()
        first = plan("nvidia-n1x-128", "structured-json", source, self.usable).primary
        after = plan("nvidia-n1x-128", "structured-json", source, self.usable, failed={first.variant_id}).primary
        self.assertNotEqual(after.variant_id, first.variant_id)
        self.assertEqual(after.alias, "qwen2.5-14b")

    def test_an_unknown_workload_is_named_with_the_known_ones(self):
        with self.assertRaisesRegex(UnknownKeyError, "Unknown workload 'poetry'. Known: structured-json"):
            plan("nvidia-n1x-128", "poetry", real_catalog(), self.usable)


class SmallNvidiaPcTests(unittest.TestCase):
    usable = {"cpu", "cuda"}

    def test_a_6_gb_graphics_card_gets_a_model_that_fits_and_not_the_14b(self):
        report = report_of("nvidia-dgpu-16")
        reasoning = plan("nvidia-dgpu-16", "reasoning", real_catalog(), self.usable)
        chat = plan("nvidia-dgpu-16", "structured-json", real_catalog(), self.usable)
        for result in (reasoning, chat):
            gpu = [o for o in result.options if o.lane == "gpu"]
            self.assertTrue(gpu)
            self.assertTrue(all(o.required_gb <= report.profile.lane("gpu").budget_gb for o in gpu))
        self.assertNotEqual(chat.primary.alias, "qwen2.5-coder-14b")
        self.assertNotEqual(reasoning.primary.alias, "gpt-oss-20b")

    def test_a_model_that_does_not_fit_is_explained_when_nothing_else_does(self):
        source = FakeSource([variant("qwen3-embedding-0.6b", "GPU", "CUDAExecutionProvider", 30000), variant("qwen3-embedding-8b", "GPU", "CUDAExecutionProvider", 30000)])
        result = plan("nvidia-dgpu-16", "embeddings", source, self.usable, lanes={"gpu"})
        self.assertIsNone(result.primary)
        self.assertRegex(
            result.notes[0],
            r"^GPU: nothing in the list for this workload has a build that runs here\. Qwen3 Embedding 0\.6B needs \d+\.\d GB on the GPU, which has \d+\.\d GB\.",
        )


class NpuTests(unittest.TestCase):
    """Snapdragon and Intel Core Ultra put the NPU first. Nothing here ran on an NPU: the catalog is a fake shaped like Foundry Local's."""

    def builds(self):
        return FakeSource(
            [
                variant("qwen2.5-7b", "NPU", "QNNExecutionProvider", 4400),
                variant("qwen2.5-7b", "CPU", "CPUExecutionProvider", 6307),
                variant("phi-4-mini", "NPU", "QNNExecutionProvider", 3000),
                variant("phi-4-mini", "CPU", "CPUExecutionProvider", 4915),
                variant("qwen2.5-14b", "NPU", "QNNExecutionProvider", 8800),
                variant("qwen2.5-0.5b", "CPU", "CPUExecutionProvider", 822),
            ]
        )

    def test_the_npu_lane_comes_first_on_a_snapdragon_pc(self):
        result = plan("snapdragon-x2-plus-16", "chat", self.builds(), {"cpu", "qnn"})
        primary = result.primary
        self.assertEqual((primary.lane, primary.device, primary.ep_label, primary.alias), ("npu", "NPU", "QNN", "qwen2.5-7b"))
        self.assertEqual(result.options[0].summary, "Qwen2.5 7B on the NPU (QNN), 4.3 GB, needs a download")
        self.assertIn("cpu", [o.lane for o in result.options])

    def test_a_model_above_the_npu_class_cap_is_never_offered_there(self):
        options = plan("snapdragon-x2-plus-16", "chat", self.builds(), {"cpu", "qnn"}).options
        self.assertNotIn("qwen2.5-14b", [o.alias for o in options])

    def test_without_a_registered_provider_the_pc_falls_back_and_says_why(self):
        problems = {"npu": "QNN is not installed on this PC yet. Allow downloads to add it."}
        result = plan("snapdragon-x2-plus-16", "chat", self.builds(), {"cpu"}, lane_problems=problems)
        self.assertEqual(result.primary.lane, "cpu")
        self.assertIn("NPU: QNN is not installed on this PC yet. Allow downloads to add it.", result.notes)

    def test_a_pc_with_no_npu_never_lists_one(self):
        result = plan("intel-12th-gen-16", "chat", self.builds(), {"cpu", "qnn"})
        self.assertNotIn("npu", [o.lane for o in result.options])

    def test_intel_core_ultra_reads_the_openvino_builds_for_its_npu(self):
        source = FakeSource([variant("phi-4-mini", "NPU", "OpenVINOExecutionProvider", 2600), variant("phi-4-mini", "GPU", "OpenVINOExecutionProvider", 2600)])
        primary = plan("intel-lunar-lake-16", "chat", source, {"cpu", "openvino"}).primary
        self.assertEqual((primary.lane, primary.ep_label), ("npu", "OpenVINO"))

    def test_a_build_for_the_wrong_device_or_provider_is_not_used(self):
        source = FakeSource([variant("qwen2.5-7b", "GPU", "QNNExecutionProvider", 4400), variant("qwen2.5-7b", "NPU", "CUDAExecutionProvider", 4400)])
        result = plan("snapdragon-x2-plus-16", "chat", source, {"cpu", "qnn", "cuda"}, lanes={"npu"})
        self.assertIsNone(result.primary)

    def test_a_build_whose_provider_is_not_registered_is_explained(self):
        source = FakeSource([variant("qwen2.5-7b", "NPU", "QNNExecutionProvider", 4400)])
        result = plan("snapdragon-x2-plus-16", "chat", source, {"cpu"}, lanes={"npu"})
        self.assertIsNone(result.primary)
        self.assertIn("Qwen2.5 7B needs the QNN provider, which is not registered.", result.notes[0])

    def test_a_missing_file_size_falls_back_to_the_catalog_estimate(self):
        source = FakeSource([variant("qwen2.5-7b", "NPU", "QNNExecutionProvider", None)])
        primary = plan("snapdragon-x2-plus-16", "chat", source, {"cpu", "qnn"}).primary
        self.assertAlmostEqual(primary.file_gb, 4843 / 1024.0)

    def test_a_context_window_is_kept_only_when_the_catalog_gives_one(self):
        with_window = FakeSource([variant("qwen2.5-7b", "NPU", "QNNExecutionProvider", 4400, context=8192)])
        without = FakeSource([variant("qwen2.5-7b", "NPU", "QNNExecutionProvider", 4400, context=0)])
        self.assertEqual(plan("snapdragon-x2-plus-16", "chat", with_window, {"cpu", "qnn"}).primary.context_tokens, 8192)
        self.assertIsNone(plan("snapdragon-x2-plus-16", "chat", without, {"cpu", "qnn"}).primary.context_tokens)

    def test_an_alias_missing_from_the_catalog_is_reported_not_raised(self):
        result = plan("snapdragon-x2-plus-16", "chat", FakeSource([]), {"cpu", "qnn"}, lanes={"cpu"})
        self.assertIsNone(result.primary)
        self.assertIn("is not in this PC's model catalog.", result.notes[0])

    def test_the_resolved_model_describes_itself_for_json(self):
        described = plan("snapdragon-x2-plus-16", "chat", self.builds(), {"cpu", "qnn"}).primary.describe()
        self.assertEqual(
            (described["lane"], described["ep"], described["variant"], described["cached"], described["fileGb"]),
            ("npu", "QNN", "qwen2.5-7b-qnn-npu:1", False, 4.3),
        )


class ManualPickTests(unittest.TestCase):
    def test_a_hand_picked_model_ignores_the_class_cap_but_not_memory(self):
        source = FakeSource([variant("qwen2.5-14b", "CPU", "CPUExecutionProvider", 11325)])
        report = report_of("nvidia-n1x-128")
        model, why = resolve_model(report, catalog(), "qwen2.5-14b", "cpu", source, usable={"cpu"})
        self.assertIsNotNone(model, why)
        self.assertEqual(model.lane, "cpu")

        small = report_of("intel-pentium-8")
        model, why = resolve_model(small, catalog(), "qwen2.5-14b", "cpu", source, usable={"cpu"})
        self.assertIsNone(model)
        self.assertRegex(why, r"needs \d+\.\d GB on the CPU")

    def test_a_lane_the_pc_does_not_have_is_explained(self):
        model, why = resolve_model(report_of("intel-12th-gen-16"), catalog(), "qwen2.5-7b", "npu", FakeSource([]), usable={"cpu"})
        self.assertIsNone(model)
        self.assertTrue(why)

    def test_an_unknown_lane_raises(self):
        with self.assertRaises(UnknownKeyError):
            resolve_model(report_of("intel-12th-gen-16"), catalog(), "qwen2.5-7b", "tpu", FakeSource([]), usable={"cpu"})


class FakeManager:
    def __init__(self, eps, outcomes=None):
        self.eps = [SimpleNamespace(name=n, is_registered=r) for n, r in eps]
        self.outcomes = outcomes or {}
        self.downloads = []

    def discover_eps(self):
        return list(self.eps)

    def download_and_register_eps(self, names=None, progress_callback=None):
        self.downloads.append(names)
        name = names[0]
        outcome = self.outcomes.get(name, True)
        if isinstance(outcome, Exception):
            raise outcome
        if progress_callback:
            progress_callback(name, 50.0)
        if outcome:
            for ep in self.eps:
                if ep.name == name:
                    ep.is_registered = True
        return SimpleNamespace(success=bool(outcome), status="Completed" if outcome else "Failed", registered_eps=[name] if outcome else [], failed_eps=[] if outcome else [name])


def register(device_id, manager, *, allow_download=False, installed=lambda token: False, **options):
    return register_eps(manager, report_of(device_id), allow_download=allow_download, installed=installed, **options)


class RegisterEpsTests(unittest.TestCase):
    def test_a_provider_the_runtime_already_has_costs_nothing(self):
        manager = FakeManager([("QNNExecutionProvider", True)])
        result = register("snapdragon-x2-plus-16", manager)
        self.assertEqual(result.usable, {"cpu", "qnn"})
        self.assertEqual(manager.downloads, [])
        self.assertIn("QNN ready.", result.notes)
        self.assertEqual(result.lane_problems.get("npu"), None)

    def test_a_missing_provider_is_not_downloaded_unless_downloads_are_allowed(self):
        manager = FakeManager([("QNNExecutionProvider", False)])
        result = register("snapdragon-x2-plus-16", manager)
        self.assertEqual(manager.downloads, [])
        self.assertNotIn("qnn", result.usable)
        self.assertEqual(result.lane_problems["npu"], "QNN is not installed on this PC yet. Allow downloads to add it.")

    def test_a_provider_that_is_installed_registers_without_a_download_permission(self):
        manager = FakeManager([("QNNExecutionProvider", False)])
        result = register("snapdragon-x2-plus-16", manager, installed=lambda token: token == "qnn")
        self.assertEqual(manager.downloads, [["QNNExecutionProvider"]])
        self.assertIn("qnn", result.usable)
        self.assertNotIn("npu", result.lane_problems)

    def test_with_downloads_allowed_the_provider_is_fetched_and_progress_is_reported(self):
        manager = FakeManager([("QNNExecutionProvider", False)])
        seen = []
        result = register("snapdragon-x2-plus-16", manager, allow_download=True, progress=seen.append)
        self.assertIn("qnn", result.usable)
        self.assertIn("QNN registered.", result.notes)
        self.assertEqual(seen, ["Preparing QNN...", "Preparing QNN: 50%"])

    def test_only_the_first_working_provider_of_a_lane_is_registered_in_the_lane_order(self):
        manager = FakeManager([("WebGpuExecutionProvider", False), ("DmlExecutionProvider", False), ("NvTensorRTRTXExecutionProvider", False)])
        result = register("nvidia-dgpu-32", manager, allow_download=True)
        self.assertEqual(manager.downloads, [["NvTensorRTRTXExecutionProvider"]])
        self.assertEqual(result.usable, {"cpu", "nvtensorrtrtx"})

    def test_register_all_registers_every_provider_a_lane_lists(self):
        manager = FakeManager([("WebGpuExecutionProvider", False), ("DmlExecutionProvider", False)])
        result = register("amd-ryzen-16", manager, allow_download=True, register_all=True)
        self.assertEqual({"webgpu", "dml"}, result.usable - {"cpu"})

    def test_a_failed_registration_is_a_lane_problem_and_the_next_provider_is_tried(self):
        manager = FakeManager([("WebGpuExecutionProvider", False), ("DmlExecutionProvider", False)], {"WebGpuExecutionProvider": False})
        result = register("amd-ryzen-16", manager, allow_download=True)
        self.assertEqual(manager.downloads, [["WebGpuExecutionProvider"], ["DmlExecutionProvider"]])
        self.assertIn("dml", result.usable)
        self.assertNotIn("gpu", result.lane_problems)
        self.assertIn("WebGPU registration reported 'Failed'.", result.notes)

    def test_an_exception_is_reported_and_does_not_stop_the_other_lanes(self):
        manager = FakeManager([("QNNExecutionProvider", False), ("WebGpuExecutionProvider", False)], {"QNNExecutionProvider": RuntimeError("no network\nsecond line")})
        logged = []
        result = register("snapdragon-x2-plus-16", manager, allow_download=True, log=logged.append)
        self.assertEqual(result.lane_problems["npu"], "QNN could not register: no network")
        self.assertIn("webgpu", result.usable)
        self.assertTrue(any("Register QNNExecutionProvider failed" in line for line in logged))

    def test_a_lane_whose_provider_the_runtime_does_not_offer_says_so(self):
        result = register("snapdragon-x2-plus-16", FakeManager([]))
        self.assertTrue(result.lane_problems["npu"].startswith("Foundry Local does not offer a QNN provider for the "))
        self.assertTrue(result.lane_problems["gpu"].startswith("Foundry Local does not offer a WebGPU or DirectML provider for the "))

    def test_a_pc_with_only_a_cpu_lane_registers_nothing(self):
        manager = FakeManager([("QNNExecutionProvider", False)])
        result = register("intel-pentium-8", manager, allow_download=True)
        self.assertEqual(manager.downloads, [])
        self.assertEqual(result.usable, {"cpu"})
        self.assertEqual(result.notes, ["No GPU or NPU lane is available on this PC, so local models use the CPU."])

    def test_an_x64_build_under_emulation_says_why_it_uses_the_cpu(self):
        result = register("snapdragon-x-elite-16-emulated", FakeManager([]))
        self.assertTrue(result.notes[0].startswith("This app runs under x64 emulation here"))

    def test_limiting_registration_to_a_lane_skips_the_others(self):
        manager = FakeManager([("QNNExecutionProvider", False), ("WebGpuExecutionProvider", False)])
        result = register("snapdragon-x2-plus-16", manager, allow_download=True, lanes={"gpu"})
        self.assertEqual(manager.downloads, [["WebGpuExecutionProvider"]])
        self.assertEqual(result.usable, {"cpu", "webgpu"})
        self.assertNotIn("npu", result.lane_problems)

    def test_limiting_registration_to_the_cpu_says_it_is_a_diagnostic(self):
        manager = FakeManager([("QNNExecutionProvider", False)])
        result = register("snapdragon-x2-plus-16", manager, allow_download=True, lanes={"cpu"})
        self.assertEqual(manager.downloads, [])
        self.assertEqual(result.usable, {"cpu"})
        self.assertEqual(result.notes, ["Limited to the CPU for diagnostics, so no GPU or NPU provider is registered."])

    def test_the_provider_list_after_registering_says_what_is_usable(self):
        manager = FakeManager([("QNNExecutionProvider", False), ("OtherExecutionProvider", False)])
        result = register("snapdragon-x2-plus-16", manager, allow_download=True)
        by_token = {p.token: p for p in result.providers}
        self.assertTrue(by_token["qnn"].registered and by_token["qnn"].usable)
        self.assertFalse(by_token["other"].usable)

    def test_registered_providers_are_found_by_the_registration_helper(self):
        manager = FakeManager([("CUDAExecutionProvider", False)])
        result = register("nvidia-n1x-128", manager)
        self.assertTrue(foundry.missing_registrable(report_of("nvidia-n1x-128"), result, manager.discover_eps()))


class SdkAdapterTests(unittest.TestCase):
    class Device(StrEnum):
        NPU = "NPU"
        GPU = "GPU"

    def sdk_variant(self, id, device, ep, mb=None, version=1, cached=False, task="chat-completion", context=None):
        runtime = None if device is None else SimpleNamespace(device_type=device, execution_provider=ep)
        info = SimpleNamespace(runtime=runtime, file_size_mb=mb, version=version, task=task, context_length=context)
        return SimpleNamespace(id=id, info=info, is_cached=cached)

    def test_sdk_objects_become_plain_records(self):
        model = SimpleNamespace(alias="a", variants=[self.sdk_variant("a-npu:2", self.Device.NPU, "QNNExecutionProvider", 4400, 2, True, context=4096)])
        source = SdkModelSource(SimpleNamespace(get_model=lambda alias: model if alias == "a" else None))
        (row,) = source.variants("a")
        self.assertEqual(
            (row.id, row.device, row.ep, row.ep_name, row.file_mb, row.version, row.cached, row.task, row.context_tokens),
            ("a-npu:2", "NPU", "qnn", "QNNExecutionProvider", 4400, 2, True, "chat-completion", 4096),
        )
        self.assertIsNone(source.variants("missing"))

    def test_a_variant_without_a_runtime_matches_no_lane(self):
        model = SimpleNamespace(alias="a", variants=[self.sdk_variant("a:1", None, None)])
        (row,) = SdkModelSource(SimpleNamespace(get_model=lambda alias: model)).variants("a")
        self.assertEqual((row.device, row.ep), ("", ""))

    def test_one_alias_failing_does_not_break_the_rest_and_answers_are_read_once(self):
        calls = []

        def get_model(alias):
            calls.append(alias)
            if alias == "bad":
                raise RuntimeError("catalog error")
            return SimpleNamespace(alias=alias, variants=[])

        source = SdkModelSource(SimpleNamespace(get_model=get_model))
        self.assertIsNone(source.variants("bad"))
        self.assertEqual(source.variants("good"), ())
        source.variants("good")
        self.assertEqual(calls, ["bad", "good"])

    def test_the_whole_catalog_can_be_listed_for_diagnostics(self):
        models = [SimpleNamespace(alias="a", variants=[self.sdk_variant("a-gpu:1", self.Device.GPU, "CUDAExecutionProvider", 100)]), SimpleNamespace(alias="b", variants=[])]
        rows = SdkModelSource(SimpleNamespace(list_models=lambda: models)).all_variants()
        self.assertEqual([r.id for r in rows], ["a-gpu:1"])


class EpCacheTests(unittest.TestCase):
    def test_a_provider_folder_in_the_app_data_counts_as_installed(self):
        import tempfile

        with tempfile.TemporaryDirectory() as folder, patch.object(foundry, "winml_ep_installed", return_value=False):
            (Path(folder) / "ep" / "cuda-ep").mkdir(parents=True)
            (Path(folder) / "ep" / "QNN_EP").mkdir(parents=True)
            self.assertTrue(foundry.ep_cache_has(folder, "cuda"))
            self.assertTrue(foundry.ep_cache_has(folder, "qnn"))
            self.assertFalse(foundry.ep_cache_has(folder, "openvino"))
            self.assertFalse(foundry.ep_cache_has(None, "cuda"))
            self.assertFalse(foundry.ep_cache_has(Path(folder) / "nowhere", "cuda"))

    def test_a_provider_installed_system_wide_by_windows_ml_counts_too(self):
        with patch.object(foundry, "_winml_packages", ["microsoftcorporationiiwinmlqualcommqnnep1"]):
            self.assertTrue(foundry.winml_ep_installed("qnn"))
            self.assertFalse(foundry.winml_ep_installed("openvino"))
            self.assertFalse(foundry.winml_ep_installed("cuda"))


if __name__ == "__main__":
    unittest.main()
