import http.client
import json
import re
import struct
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from content import DEFAULT_ASSUMPTIONS, SCENARIOS, calculate
from local_engine import DEFAULT_MODEL, MODELS, PROVIDER, HarmonyFilter, LocalEngine
from server import ShowcaseServer, read_device

import builder
import demos
import hybrid
import workbench

ROOT = Path(__file__).resolve().parent.parent


class InventoryTests(unittest.TestCase):
    def test_installed_and_visible_memory_remain_distinct(self):
        inventory = {"installed_ram_gb": 128, "installed_ram_bytes": 137438953472,
                     "os_visible_ram_gb": 64, "os_visible_ram_bytes": 68699033600}
        with patch("server.subprocess.run", side_effect=[
            SimpleNamespace(stdout=json.dumps(inventory)),
            SimpleNamespace(stdout="NVIDIA GPU, 64000\nAuxiliary accelerator, [N/A]\n"),
        ]) as run:
            result = read_device()
        self.assertEqual(result["installed_ram_gb"], 128)
        self.assertEqual(result["os_visible_ram_gb"], 64)
        self.assertEqual(result["gpu_memory"][0]["reported_budget_mib"], 64000)
        self.assertIn("Win32_PhysicalMemory", run.call_args_list[0].args[0][-1])
        self.assertNotIn("$cs.TotalPhysicalMemory", run.call_args_list[0].args[0][-1])

    def test_inventory_failure_does_not_guess_memory(self):
        with patch("server.subprocess.run", side_effect=OSError("CIM unavailable")):
            result = read_device()
        self.assertIsNone(result["installed_ram_gb"])
        self.assertIn("CIM unavailable", result["error"])

    def test_gpu_query_failure_preserves_valid_dimm_capacity(self):
        with patch("server.subprocess.run", side_effect=[
            SimpleNamespace(stdout=json.dumps({"installed_ram_gb": 128, "os_visible_ram_gb": 64})),
            OSError("nvidia-smi missing"),
        ]):
            result = read_device()
        self.assertEqual(result["installed_ram_gb"], 128)
        self.assertEqual(result["gpu_memory"], [])
        self.assertIn("nvidia-smi missing", result["gpu_memory_error"])


class EnginePolicyTests(unittest.TestCase):
    def test_larger_models_are_pinned(self):
        self.assertEqual(DEFAULT_MODEL, "qwen2.5-coder-14b")
        self.assertIn("14b", MODELS[DEFAULT_MODEL]["id"])
        self.assertEqual(MODELS["gpt-oss-20b"]["id"], "gpt-oss-20b-cuda-gpu:1")
        self.assertEqual(PROVIDER, "CUDAExecutionProvider")

    def test_reasoning_model_has_larger_output_budget(self):
        engine = LocalEngine()
        self.assertEqual(engine.generation_budget("code"), 1600)
        engine.state["selected_model"] = "gpt-oss-20b"
        self.assertEqual(engine.generation_budget("code"), 4096)
        self.assertEqual(engine.generation_budget("brief"), 3072)

    def test_unsupported_model_rejected(self):
        with self.assertRaises(ValueError):
            LocalEngine().switch("arbitrary-cpu-model")

    def test_cpu_variant_never_loads(self):
        engine = LocalEngine()
        model = SimpleNamespace(info=SimpleNamespace(runtime=SimpleNamespace(execution_provider="CPUExecutionProvider")))
        engine.manager = SimpleNamespace(catalog=SimpleNamespace(get_model_variant=lambda _: model))
        engine.switch(DEFAULT_MODEL)
        self.assertEqual(engine.state["state"], "error")
        self.assertIn("refusing silent fallback", engine.state["error"])
        self.assertIsNone(engine.model)


class HarmonyFilterTests(unittest.TestCase):
    def filter_chunks(self, chunks):
        parser = HarmonyFilter()
        output, hidden = "", 0
        for chunk in [*chunks, ""]:
            text, count = parser.feed(chunk, final=chunk == "")
            output += text
            hidden += count
        return output, hidden

    def test_fragmented_analysis_is_never_visible(self):
        framed = "<|channel|>analysis<|message|>PRIVATE_TEST_TEXT<|end|><|start|>assistant<|channel|>final<|message|>Public answer.<|return|>"
        for size in (1, 2, 7, 31, len(framed)):
            with self.subTest(size=size):
                output, hidden = self.filter_chunks([framed[i:i + size] for i in range(0, len(framed), size)])
                self.assertEqual(output, "Public answer.")
                self.assertEqual(hidden, len("PRIVATE_TEST_TEXT"))
                self.assertNotIn("PRIVATE_TEST_TEXT", output)
                self.assertNotIn("<|", output)

    def test_full_assistant_header_and_plain_answer(self):
        framed = "<|start|>assistant<|channel|>final<|message|>Answer<|return|>"
        self.assertEqual(self.filter_chunks([framed]), ("Answer", 0))
        self.assertEqual(self.filter_chunks(["if x < ", "3:\n    return x"]), ("if x < 3:\n    return x", 0))
        self.assertEqual(self.filter_chunks(["value <"]), ("value <", 0))

    def test_truncated_control_is_explicit_failure(self):
        parser = HarmonyFilter()
        parser.feed("<|channel")
        with self.assertRaises(RuntimeError):
            parser.feed("", final=True)

    def test_literal_marker_text_in_reasoning_stays_hidden(self):
        secret = "SECRET hidden plan: customer budget is tight"
        framed = ("<|channel|>analysis<|message|>The user wrote a <| b and x <|y|> then. " + secret +
                  "<|end|><|start|>assistant<|channel|>final<|message|>Public a <| b answer.<|return|>")
        for size in (1, 2, 3, 5, 13, len(framed)):
            with self.subTest(size=size):
                output, hidden = self.filter_chunks([framed[i:i + size] for i in range(0, len(framed), size)])
                self.assertEqual(output, "Public a <| b answer.")
                self.assertNotIn("SECRET", output)
                self.assertGreater(hidden, len(secret))

    def test_unknown_marker_in_answer_fails_without_echoing_model_text(self):
        framed = "<|channel|>analysis<|message|>SECRET plan<|end|><|channel|>final<|message|>Answer <|call|> SECRET2"
        with self.assertRaises(RuntimeError) as raised:
            self.filter_chunks([framed])
        self.assertNotIn("SECRET", str(raised.exception))
        self.assertNotIn("Answer", str(raised.exception))


class CalculatorTests(unittest.TestCase):
    def test_default_case(self):
        result = calculate(DEFAULT_ASSUMPTIONS)
        self.assertEqual(result["hours"], 2640)
        self.assertEqual(result["capacity"], 158400)
        self.assertEqual(result["investment"], 60000)
        self.assertIn("not cash savings", result["disclaimer"])
        self.assertIn("Cloud subscriptions are unchanged", result["disclaimer"])

    def test_zero_improvement_and_zero_adoption(self):
        for key in ("minutes", "tasks", "adoption"):
            with self.subTest(key=key):
                result = calculate(DEFAULT_ASSUMPTIONS | {key: 0})
                self.assertEqual(result["capacity"], 0)
                self.assertEqual(result["hours"], 0)
                self.assertNotIn("roi", result)

    def test_zero_cost_does_not_invent_roi(self):
        result = calculate(DEFAULT_ASSUMPTIONS | {"premium": 0})
        self.assertEqual(result["investment"], 0)
        self.assertNotIn("roi", result)
        self.assertNotIn("months", result)

    def test_invalid_assumptions(self):
        for value in (-1, 101, float("nan"), float("inf"), True, "60", None):
            with self.subTest(value=value), self.assertRaises(ValueError):
                calculate(DEFAULT_ASSUMPTIONS | {"adoption": value})
        with self.assertRaises(ValueError):
            calculate({})
        with self.assertRaises(ValueError):
            calculate(DEFAULT_ASSUMPTIONS | {"extra": 1})


class PlatformScenarioTests(unittest.TestCase):
    def test_platform_workflows_are_explicit_text_fixtures(self):
        self.assertIn("Snapdragon-fit", SCENARIOS["field"]["label"])
        self.assertIn("Intel-fit", SCENARIOS["meeting"]["label"])
        self.assertIn("No speech recording or image was supplied", SCENARIOS["field"]["source"])
        self.assertIn("not a recording", SCENARIOS["meeting"]["source"])
        for name in ("field", "meeting"):
            with self.subTest(scenario=name):
                scenario = SCENARIOS[name]
                self.assertIn("synthetic", scenario["system"])
                self.assertIn("Human review", scenario["system"])
                self.assertIn("Do not", scenario["prompt"])
                self.assertTrue(scenario["rehearsal"])


class TutorScenarioTests(unittest.TestCase):
    def test_tutor_is_a_display_only_plain_english_fixture(self):
        tutor = SCENARIOS["tutor"]
        self.assertIn("```python", tutor["prompt"])
        self.assertIn("Do not run the code", tutor["prompt"])
        self.assertIn("never executed", tutor["source"])
        self.assertIn("Never claim the code was run", tutor["system"])
        self.assertIn("review and test", tutor["system"])
        self.assertIn("1200", tutor["rehearsal"])
        self.assertLessEqual(len(tutor["prompt"]), 4000)
        for word in ("faster", "savings", "NPU", "guarantee"):
            with self.subTest(word=word):
                self.assertIsNone(re.search(rf"\b{word}\b", tutor["rehearsal"] + tutor["prompt"], re.IGNORECASE))

    def test_tutor_prompt_matches_native_lesson_handoff(self):
        native = (ROOT / "native" / "CodingTrack.cs").read_text(encoding="utf-8")
        match = re.search(r"TutorIntro\s*=\s*((?:\"[^\"]*\"\s*\+?\s*)+);", native)
        self.assertIsNotNone(match)
        intro = "".join(re.findall(r"\"([^\"]*)\"", match.group(1)))
        self.assertTrue(SCENARIOS["tutor"]["prompt"].startswith(intro + "\n\n```python\n"))


class FakeEngine:
    def __init__(self):
        self.state = {"state": "ready", "detail": "Test engine", "model": None,
                      "selected_model": DEFAULT_MODEL, "models": []}
        self.calls = []
        self.fail = False
        self.endpoint = "stopped"

    def switch(self, selected):
        self.state.update(selected_model=selected, state="ready")

    def generation_budget(self, scenario):
        return 650

    def stream(self, prompt, system, max_tokens):
        self.calls.append((prompt, system, max_tokens))
        if self.fail:
            raise RuntimeError("Injected inference failure")
        yield {"type": "token", "text": "<script>not executed</script>"}
        yield {"type": "done", "mode": "live", "ttft_ms": 1, "total_ms": 2}

    def endpoint_info(self):
        foundry = {"runtime": "Foundry Local", "state": self.endpoint, "base_url": "http://127.0.0.1:9799/v1",
                   "port": 9799, "model": "fake-model-cuda-gpu:1", "detail": "fixture"}
        return {"foundry": foundry, "large": None, "active": "foundry"}

    def start_endpoint(self):
        self.endpoint = "running"
        return self.endpoint_info()

    def stop_endpoint(self):
        self.endpoint = "stopped"
        return self.endpoint_info()


class ApiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.engine = FakeEngine()
        cls.server = ShowcaseServer(("127.0.0.1", 0), engine=cls.engine, device={"model": "fixture"})
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()

    def request(self, method, path, body=None, headers=None):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=10)
        request_headers = {"X-Showcase-Token": self.server.token}
        request_headers.update(headers or {})
        connection.request(method, path, body, request_headers)
        response = connection.getresponse()
        data = response.read().decode()
        status, out_headers = response.status, dict(response.getheaders())
        connection.close()
        return status, data, out_headers

    def post(self, path, payload, headers=None):
        return self.request("POST", path, json.dumps(payload), headers)

    def test_assets_and_csp(self):
        code, body, _ = self.request("GET", "/api/status")
        self.assertEqual(code, 200)
        self.assertEqual(json.loads(body)["app_id"], json.loads((ROOT / "edition.json").read_text(encoding="utf-8"))["serviceAppId"])
        for path in ("/", "/app.js", "/style.css", "/seller-guide"):
            with self.subTest(path=path):
                code, body, headers = self.request("GET", path)
                self.assertEqual(code, 200)
                self.assertTrue(body)
                self.assertIn("frame-ancestors 'none'", headers["Content-Security-Policy"])
        self.assertEqual(self.request("GET", "/../server.py")[0], 404)

    def test_browser_icon_has_seven_real_resolutions(self):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=10)
        try:
            connection.request("GET", "/favicon.ico")
            response = connection.getresponse()
            body = response.read()
            self.assertEqual(response.status, 200)
            self.assertEqual(response.getheader("Content-Type"), "image/x-icon")
            self.assertEqual(struct.unpack_from("<HHH", body), (0, 1, 7))
            for index, size in enumerate((16, 24, 32, 48, 64, 128, 0)):
                width, height, _, _, planes, bits, length, offset = struct.unpack_from(
                    "<BBBBHHII", body, 6 + index * 16)
                self.assertEqual((width, height, planes, bits), (size, size, 1, 32))
                self.assertLessEqual(offset + length, len(body))
                self.assertEqual(body[offset:offset + 8], b"\x89PNG\r\n\x1a\n")
            self.assertIn('href="/favicon.ico"', self.request("GET", "/")[1])
        finally:
            connection.close()

    def test_inventory_and_synthetic_sources(self):
        code, data, _ = self.request("GET", "/api/status")
        self.assertEqual(code, 200)
        self.assertEqual(json.loads(data)["device"]["model"], "fixture")
        code, data, _ = self.request("GET", "/api/scenarios")
        self.assertEqual(code, 200)
        self.assertNotIn("rehearsal", json.loads(data)["brief"])

    def test_host_token_and_origin_guards(self):
        self.assertEqual(self.request("GET", "/", headers={"Host": "attacker.example"})[0], 403)
        self.assertEqual(self.post("/api/calculate", DEFAULT_ASSUMPTIONS, {"X-Showcase-Token": "wrong"})[0], 403)
        self.assertEqual(self.post("/api/calculate", DEFAULT_ASSUMPTIONS, {"Origin": "https://attacker.example"})[0], 403)
        self.assertEqual(self.post("/api/calculate", DEFAULT_ASSUMPTIONS, {"X-Showcase-Token": "t\u00f6ken"})[0], 403)

    def test_rejected_posts_reliably_deliver_their_response(self):
        payload = DEFAULT_ASSUMPTIONS | {"padding": "x" * 12000}
        for attempt in range(40):
            with self.subTest(attempt=attempt):
                self.assertEqual(self.post("/api/calculate", payload, {"X-Showcase-Token": "wrong"})[0], 403)

    def test_rehearsal_is_explicit_and_unmeasured(self):
        for scenario, source in SCENARIOS.items():
            with self.subTest(scenario=scenario):
                code, data, _ = self.post("/api/run", {"scenario": scenario, "mode": "rehearsal", "prompt": source["prompt"]})
                events = [json.loads(line) for line in data.splitlines()]
                self.assertEqual(code, 200)
                self.assertEqual(events[0]["text"], source["rehearsal"])
                self.assertEqual(events[-1]["mode"], "rehearsal")
                self.assertIsNone(events[-1]["ttft_ms"])
                self.assertIsNone(events[-1]["total_ms"])
        self.assertEqual(self.post("/api/run", {"scenario": "brief", "mode": "rehearsal", "prompt": "Different"})[0], 400)

    def test_live_uses_model_and_context(self):
        code, data, _ = self.post("/api/run", {"scenario": "code", "mode": "live", "prompt": "Create draft"})
        self.assertEqual(code, 200)
        events = [json.loads(line) for line in data.splitlines()]
        self.assertEqual(events[-1]["mode"], "live")
        self.assertIn(SCENARIOS["code"]["source"], self.engine.calls[-1][0])
        self.assertEqual(events[0]["text"], "<script>not executed</script>")

    def test_unready_is_not_success_or_rehearsal(self):
        self.engine.state["state"] = "error"
        try:
            code, data, _ = self.post("/api/run", {"scenario": "brief", "mode": "live", "prompt": "Hello"})
            self.assertEqual(code, 503)
            self.assertIn("error", json.loads(data))
        finally:
            self.engine.state["state"] = "ready"

    def test_inference_failure_is_explicit_and_releases_lock(self):
        self.engine.fail = True
        try:
            code, data, _ = self.post("/api/run", {"scenario": "brief", "mode": "live", "prompt": "Hello"})
            events = [json.loads(line) for line in data.splitlines()]
            self.assertEqual(code, 200)
            self.assertEqual(events, [{"type": "error", "error": "Injected inference failure"}])
            self.assertFalse(self.server.run_lock.locked())
        finally:
            self.engine.fail = False

    def test_concurrent_run_rejected(self):
        self.server.run_lock.acquire()
        try:
            self.assertEqual(self.post("/api/run", {"scenario": "brief", "mode": "live", "prompt": "Hello"})[0], 409)
        finally:
            self.server.run_lock.release()

    def test_second_instance_cannot_bind_the_same_port(self):
        with self.assertRaises(OSError):
            ShowcaseServer(("127.0.0.1", self.server.server_port), engine=FakeEngine(), device={"model": "fixture"})

    def test_model_selection_is_validated_and_serialized(self):
        self.assertEqual(self.post("/api/model", {"model": "arbitrary-cpu"})[0], 400)
        self.assertEqual(self.post("/api/model", {"model": []})[0], 400)
        self.server.run_lock.acquire()
        try:
            self.assertEqual(self.post("/api/model", {"model": "gpt-oss-20b"})[0], 409)
        finally:
            self.server.run_lock.release()
        code, data, _ = self.post("/api/model", {"model": "gpt-oss-20b"})
        self.assertEqual(code, 202)
        self.assertEqual(json.loads(data)["selected_model"], "gpt-oss-20b")
        for thread in threading.enumerate():
            if thread.name == "model-switch":
                thread.join(timeout=5)
        self.assertEqual(self.engine.state["selected_model"], "gpt-oss-20b")
        self.assertFalse(self.server.run_lock.locked())
        self.engine.switch(DEFAULT_MODEL)

    def test_bad_requests(self):
        for payload in ([], None, {"scenario": []}, {"scenario": "bad"},
                        {"scenario": "brief", "mode": "live", "prompt": 42},
                        {"scenario": "brief", "mode": "live", "prompt": " "},
                        {"scenario": "brief", "mode": "live", "prompt": "x" * 4001}):
            with self.subTest(payload=str(payload)[:100]):
                self.assertEqual(self.post("/api/run", payload)[0], 400)
        self.assertEqual(self.request("POST", "/api/run", "{broken")[0], 400)
        self.assertEqual(self.request("POST", "/api/run", "{}",
                                     {"Content-Length": "bad"})[0], 400)
        self.assertEqual(self.post("/api/calculate", DEFAULT_ASSUMPTIONS | {"minutes": -2})[0], 400)
        self.assertEqual(self.post("/missing", {})[0], 404)

    @staticmethod
    def events(data):
        return [json.loads(line) for line in data.splitlines() if line.strip()]

    def test_hybrid_catalog_and_route_preview(self):
        code, data, _ = self.request("GET", "/api/hybrid")
        self.assertEqual(code, 200)
        catalog = json.loads(data)
        self.assertEqual(len(catalog["presets"]), len(hybrid.PRESETS))
        self.assertTrue(all(p["caveat"] for p in catalog["patterns"]))
        rates = next(p for p in hybrid.PRESETS if p["id"] == "rates")
        calls = len(self.engine.calls)
        code, data, _ = self.post("/api/hybrid/route", {"text": rates["text"]})
        self.assertEqual(code, 200)
        decision = json.loads(data)
        self.assertEqual(decision["lane"], "hybrid")
        self.assertFalse(decision["cloud_payload"]["sent"])
        self.assertEqual(len(self.engine.calls), calls, "a route preview never runs the model")
        for payload in ({}, {"text": ""}, {"text": "x" * 4001}, {"text": "hi", "network": "satellite"},
                        {"text": "hi", "scale": "huge"}):
            with self.subTest(payload=str(payload)[:60]):
                self.assertEqual(self.post("/api/hybrid/route", payload)[0], 400)
                self.assertEqual(self.post("/api/hybrid/run", payload)[0], 400)

    def test_hybrid_run_streams_each_lane_without_sending(self):
        news = next(p for p in hybrid.PRESETS if p["id"] == "news")
        calls = len(self.engine.calls)
        code, data, _ = self.post("/api/hybrid/run", {"text": news["text"]})
        self.assertEqual(code, 200)
        events = self.events(data)
        self.assertEqual([e["type"] for e in events], ["route", "payload", "complete"])
        self.assertFalse(events[1]["sent"])
        self.assertFalse(events[-1]["model_ran"])
        self.assertEqual(len(self.engine.calls), calls, "the cloud lane never runs the model")
        complaint = next(p for p in hybrid.PRESETS if p["id"] == "complaint")
        events = self.events(self.post("/api/hybrid/run", {"text": complaint["text"]})[1])
        self.assertEqual([e["type"] for e in events], ["route", "stage", "token", "model_done", "complete"])
        self.assertTrue(events[-1]["model_ran"])
        self.assertFalse(events[-1]["sent"])
        self.assertFalse(self.server.run_lock.locked())
        self.server.run_lock.acquire()
        try:
            self.assertEqual(self.post("/api/hybrid/run", {"text": complaint["text"]})[0], 409)
            self.assertEqual(self.post("/api/speedtest", {})[0], 409)
            self.assertEqual(self.post("/api/builder/endpoint", {"action": "start"})[0], 409)
        finally:
            self.server.run_lock.release()

    def test_builder_hub_endpoint_and_starters(self):
        code, data, _ = self.request("GET", "/api/builder")
        self.assertEqual(code, 200)
        hub = json.loads(data)
        self.assertEqual({k["id"] for k in hub["kits"]}, set(builder.KITS))
        self.assertEqual(hub["target"]["base_url"], "http://127.0.0.1:9799/v1")
        self.assertTrue(all("fake-model-cuda-gpu:1" in s["code"] for s in hub["snippets"] if s["id"] != "models"))
        self.assertEqual(self.post("/api/builder/endpoint", {"action": "explode"})[0], 400)
        code, data, _ = self.post("/api/builder/endpoint", {"action": "start"})
        self.assertEqual(code, 200)
        self.assertEqual(json.loads(data)["target"]["state"], "running")
        code, data, _ = self.post("/api/builder/endpoint", {"action": "stop"})
        self.assertEqual(json.loads(data)["target"]["state"], "stopped")
        self.assertEqual(self.post("/api/builder/test", {})[0], 409, "a stopped endpoint cannot be tested")
        self.assertEqual(self.post("/api/builder/starter", {"kit": "../evil"})[0], 400)
        code, data, _ = self.post("/api/builder/starter", {"kit": "chat"})
        self.assertEqual(code, 200)
        self.assertEqual(set(json.loads(data)["files"]), {"README.md", "local_ai.py", "chat.py"})
        self.assertFalse(self.server.run_lock.locked())

    def test_speedtest_streams_labelled_results(self):
        code, data, _ = self.post("/api/speedtest", {})
        self.assertEqual(code, 200)
        events = self.events(data)
        self.assertEqual(events[0]["type"], "start")
        complete = events[-1]
        self.assertEqual(complete["type"], "complete")
        self.assertIn("Not a benchmark", complete["note"])
        self.assertTrue(complete["decode_source"] and complete["prefill_source"])
        self.assertFalse(self.server.run_lock.locked())

    def test_endpoint_port_follows_env_and_studio_port(self):
        from edition import ENV_PREFIX
        from server import endpoint_port
        with patch.dict("os.environ", {f"{ENV_PREFIX}ENDPOINT_PORT": ""}):
            self.assertEqual(endpoint_port(8792), 9792)
        with patch.dict("os.environ", {f"{ENV_PREFIX}ENDPOINT_PORT": "9123"}):
            self.assertEqual(endpoint_port(8792), 9123)
        with patch.dict("os.environ", {f"{ENV_PREFIX}ENDPOINT_PORT": "80"}):
            self.assertIsNone(endpoint_port(8792))
        with patch.dict("os.environ", {f"{ENV_PREFIX}ENDPOINT_PORT": "8792"}):
            self.assertIsNone(endpoint_port(8792))


class ScriptedEngine:
    """Replays a fixed reply so router and speedometer logic can be checked without a GPU."""

    def __init__(self, reply="", tokens=None, done=None, delay=0.0):
        self.state = {"state": "ready", "detail": "Scripted", "model": {"name": "Scripted 1B"},
                      "selected_model": DEFAULT_MODEL}
        self.reply, self.tokens, self.done, self.delay = reply, tokens, done or {}, delay
        self.calls = []

    def generation_budget(self, scenario):
        return 650

    def stream(self, prompt, system, max_tokens):
        import time
        self.calls.append((prompt, system, max_tokens))
        words = re.findall(r"\S+\s*", self.reply) or [""]
        for word in words:
            if self.delay:
                time.sleep(self.delay)
            yield {"type": "token", "text": word}
        yield {"type": "done", "mode": "live", "total_ms": 1, "output_tokens": self.tokens or len(words),
               **self.done}


class HybridRouterTests(unittest.TestCase):
    def preset(self, preset_id):
        return next(p for p in hybrid.PRESETS if p["id"] == preset_id)

    def test_presets_route_to_their_expected_lanes(self):
        for preset in hybrid.PRESETS:
            with self.subTest(preset=preset["id"]):
                decision = hybrid.route(preset["text"], preset["network"], preset["scale"])
                self.assertEqual(decision["lane"], preset["expect"])
                self.assertEqual(decision, hybrid.route(preset["text"], preset["network"], preset["scale"]))
                if decision["cloud_payload"]:
                    self.assertFalse(decision["cloud_payload"]["sent"])

    def test_credentials_block_the_cloud_and_are_masked(self):
        decision = hybrid.route(self.preset("secret")["text"])
        self.assertTrue(decision["cloud_blocked"])
        self.assertIsNone(decision["cloud_payload"])
        self.assertNotIn("sk-FAKE", decision["masked_text"])
        self.assertTrue(any(s["where"] == "cloud" and "Blocked" in s["text"] for s in decision["steps"]))

    def test_offline_defers_and_masks_the_held_part(self):
        field = self.preset("field")
        decision = hybrid.route(field["text"], "offline")
        self.assertTrue(decision["deferred"])
        self.assertNotIn("Sam Ortega", decision["cloud_payload"]["text"])
        self.assertEqual(hybrid.route(field["text"], "online")["lane"], "hybrid")

    def test_hybrid_payload_masks_identifiers(self):
        decision = hybrid.route(self.preset("rates")["text"])
        payload = decision["cloud_payload"]["text"]
        for value in ("jonas.becker", "5521", "Jonas Becker"):
            self.assertNotIn(value, payload)
        self.assertIn("exchange rates", payload)

    def test_scale_what_if_escalates_clean_requests_only(self):
        self.assertEqual(hybrid.route("Summarise our approach to onboarding.")["lane"], "local")
        self.assertEqual(hybrid.route("Summarise our approach to onboarding.", scale="beyond_device")["lane"], "cloud")
        complaint = self.preset("complaint")["text"]
        self.assertEqual(hybrid.route(complaint, scale="beyond_device")["lane"], "hybrid")

    def test_structured_identifiers_in_the_demo_email_are_masked(self):
        masked = hybrid.masked_text(demos.EMAIL)
        for value in ("dana.whitfield@example.com", "marcus.lee@example.com", "ACCT-7781-2209", "555-0187",
                      "1847 Lakeview Terrace", "PE4 7QT", "3 March 1984"):
            self.assertNotIn(value, masked)

    def test_leak_check(self):
        rehearsal = demos.DEMOS["redact"]["rehearsal"][0]
        self.assertEqual(hybrid.leaks(demos.EMAIL, rehearsal), [])
        survived = hybrid.leaks(demos.EMAIL, rehearsal.replace("[EMAIL]", "dana.whitfield@example.com", 1))
        self.assertEqual([s["label"] for s in survived], ["Email address"])
        self.assertNotIn("dana.whitfield", json.dumps(survived), "evidence is masked")
        self.assertTrue(hybrid.leaks("hi", "my card is 4111 1111 1111 1111"), "new identifiers are caught too")

    def test_patterns_point_at_real_proofs(self):
        for pattern in hybrid.PATTERNS:
            with self.subTest(pattern=pattern["id"]):
                proof = pattern["proof"]
                if proof["kind"] == "demo":
                    self.assertIn(proof["target"], demos.DEMOS)
                else:
                    self.assertIn(proof["target"], ("hybrid", "build", "showoff"))
                self.assertTrue(pattern["caveat"])
                self.assertEqual({w for w, _ in pattern["flow"]} - {"device", "cloud", "person"}, set())


class WorkbenchTests(unittest.TestCase):
    def test_redaction_that_passes_the_rescan_is_shown_not_sent(self):
        rates = next(p for p in hybrid.PRESETS if p["id"] == "rates")["text"]
        clean = hybrid.masked_text(rates)
        events = list(workbench.hybrid_run(ScriptedEngine(clean), rates))
        types = [e["type"] for e in events]
        self.assertEqual(types[:2], ["route", "stage"])
        check = next(e for e in events if e["type"] == "check")
        payload = next(e for e in events if e["type"] == "payload")
        self.assertTrue(check["passed"])
        self.assertFalse(payload["sent"])
        self.assertFalse(payload["withheld"])
        self.assertEqual(payload["text"], clean.strip())

    def test_redaction_that_leaks_is_withheld(self):
        rates = next(p for p in hybrid.PRESETS if p["id"] == "rates")["text"]
        events = list(workbench.hybrid_run(ScriptedEngine(rates), rates))
        check = next(e for e in events if e["type"] == "check")
        payload = next(e for e in events if e["type"] == "payload")
        self.assertFalse(check["passed"])
        self.assertTrue(payload["withheld"])
        self.assertIsNone(payload["text"])

    def test_model_that_answers_instead_of_redacting_is_withheld(self):
        rates = next(p for p in hybrid.PRESETS if p["id"] == "rates")["text"]
        # Observed from a 14B model given the raw text: it drafted the reply (with an invented rate) instead.
        answered = ("Subject: Exchange Rate for Euro to USD\n\nDear [NAME],\n\nThank you for your inquiry regarding the "
                    "conversion of euros to US dollars. As of today, the exchange rate is approximately 1 EUR = 1.10 USD. "
                    "Therefore, a 12,400 EUR invoice would be roughly equivalent to 13,640 USD.\n\nBest regards")
        self.assertEqual(hybrid.leaks(rates, answered), [], "the identifier re-scan alone would pass this")
        self.assertIn("wording survived", hybrid.drift(rates, answered))
        engine = ScriptedEngine("<text>\n" + answered + "\n</text>")
        events = list(workbench.hybrid_run(engine, rates))
        check = next(e for e in events if e["type"] == "check")
        payload = next(e for e in events if e["type"] == "payload")
        self.assertFalse(check["passed"])
        self.assertTrue(check["drift"])
        self.assertTrue(payload["withheld"])
        self.assertIsNone(payload["text"])
        self.assertIn("<text>", engine.calls[0][0], "the request is fenced as data")
        self.assertTrue(engine.calls[0][0].rstrip().endswith("</text>"))

    def test_faithful_redaction_passes_drift_and_tags_are_stripped(self):
        rates = next(p for p in hybrid.PRESETS if p["id"] == "rates")["text"]
        model_style = hybrid.masked_text(rates).replace("FICTIONAL. ", "")
        self.assertIsNone(hybrid.drift(rates, model_style))
        self.assertIn("much longer", hybrid.drift(rates, model_style + " padding" * 200))
        self.assertIsNone(hybrid.drift("Hi Sam", "Hello there, how can I help?"), "too short to judge")
        events = list(workbench.hybrid_run(ScriptedEngine("<text>\n" + model_style + "\n</text>"), rates))
        payload = next(e for e in events if e["type"] == "payload")
        self.assertFalse(payload["withheld"])
        self.assertNotIn("<text>", payload["text"])
        self.assertEqual(payload["text"], model_style.strip())

    def test_offline_lane_answers_locally_and_queues(self):
        field = next(p for p in hybrid.PRESETS if p["id"] == "field")
        engine = ScriptedEngine("Work order drafted.")
        events = list(workbench.hybrid_run(engine, field["text"], "offline"))
        queued = next(e for e in events if e["type"] == "queued")
        self.assertFalse(queued["sent"])
        self.assertEqual(engine.calls[0][1], workbench.LOCAL_SYSTEM)

    def test_phase_maths_prefers_runtime_timings_and_labels_estimates(self):
        timeline = [(0.5, "token")] + [(0.5 + i * 0.1, "token") for i in range(1, 11)]
        measured = workbench.phase_result("decode", timeline, {"output_tokens": 11, "prompt_tokens": 1000}, 400)
        self.assertEqual(measured["decode_tps"], 10.0)
        self.assertTrue(measured["decode_source"].startswith("Measured"))
        self.assertEqual(measured["prefill_tps"], 2000)
        self.assertTrue(measured["prefill_source"].startswith("Estimate"))
        runtime = workbench.phase_result("decode", timeline, {"output_tokens": 11, "decode_tokens_per_second": 42.5,
                                                              "prompt_tokens_per_second": 1500.2}, 400)
        self.assertEqual((runtime["decode_tps"], runtime["prefill_tps"]), (42.5, 1500.2))
        self.assertIn("llama.cpp", runtime["decode_source"])
        rough = workbench.phase_result("prefill", [(2.0, "token")], {}, 8000)
        self.assertEqual(rough["prefill_tps"], 1000)
        self.assertIn("Rough estimate", rough["prefill_source"])
        self.assertIsNone(workbench.phase_result("decode", [], None, 10)["decode_tps"])

    def test_speedtest_uses_a_fresh_long_prompt_each_run(self):
        engine = ScriptedEngine("one two three four five six", delay=0.01)
        events = list(workbench.speedtest(engine))
        self.assertEqual(events[-1]["type"], "complete")
        self.assertIsNotNone(events[-1]["decode_tps"])
        list(workbench.speedtest(engine))
        prefill_prompts = [call[0] for call in engine.calls if len(call[0]) > 5000]
        self.assertEqual(len(prefill_prompts), 2)
        self.assertNotEqual(prefill_prompts[0][:40], prefill_prompts[1][:40], "the nonce leads, so no prompt cache hit")
        self.assertGreaterEqual(len(prefill_prompts[0]), workbench.PREFILL_TARGET)

    def test_endpoint_test_calls_the_loopback_api_like_a_builder(self):
        from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
        seen = []

        class Stub(BaseHTTPRequestHandler):
            def do_POST(self):
                seen.append((self.path, json.loads(self.rfile.read(int(self.headers["Content-Length"])))))
                body = json.dumps({"choices": [{"message": {"content": "Hello from your Surface."},
                                                "finish_reason": "stop"}],
                                   "usage": {"prompt_tokens": 12, "completion_tokens": 6}}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *args):
                pass

        stub = ThreadingHTTPServer(("127.0.0.1", 0), Stub)
        threading.Thread(target=stub.serve_forever, daemon=True).start()
        try:
            engine = FakeEngine()
            engine.endpoint = "running"
            engine.endpoint_info = lambda: {"large": None, "active": "foundry", "foundry": {
                "runtime": "Foundry Local", "state": "running", "model": "fake-model-cuda-gpu:1",
                "base_url": f"http://127.0.0.1:{stub.server_port}/v1"}}
            result = workbench.endpoint_test(engine)
        finally:
            stub.shutdown()
            stub.server_close()
        self.assertEqual(seen[0][0], "/v1/chat/completions")
        self.assertEqual(seen[0][1]["model"], "fake-model-cuda-gpu:1")
        self.assertEqual(result["reply"], "Hello from your Surface.")
        self.assertEqual(result["usage"]["completion_tokens"], 6)
        stopped = FakeEngine()
        with self.assertRaises(RuntimeError):
            workbench.endpoint_test(stopped)

    def test_endpoint_reply_never_shows_private_reasoning(self):
        framed = "<|channel|>analysis<|message|>PRIVATE plan<|end|><|start|>assistant<|channel|>final<|message|>Hi there.<|return|>"
        self.assertEqual(workbench.visible_reply({"content": framed}, False), ("Hi there.", len("PRIVATE plan")))
        self.assertEqual(workbench.visible_reply({"content": "Hi.", "reasoning_content": "PRIVATE"}, True), ("Hi.", 7))
        self.assertEqual(workbench.visible_reply({"content": " if x < 3: ok "}, False), ("if x < 3: ok", 0))
        reply, withheld = workbench.visible_reply({"content": "<|channel|>analysis<|message|>PRIVATE<|call|>"}, True)
        self.assertEqual(reply, "")
        self.assertGreater(withheld, 0)


class BuilderTests(unittest.TestCase):
    BASE, MODEL = "http://127.0.0.1:9792/v1", "qwen2.5-coder-14b-instruct-cuda-gpu:4"

    def kit(self, kit_id):
        return builder.files(kit_id, self.BASE, self.MODEL)

    def load(self, files, module):
        import importlib.util
        import sys
        import tempfile
        folder = Path(tempfile.mkdtemp(prefix="kit-"))
        self.addCleanup(__import__("shutil").rmtree, folder, True)
        for rel, text in files.items():
            (folder / rel).parent.mkdir(parents=True, exist_ok=True)
            (folder / rel).write_text(text, encoding="utf-8")
        sys.path.insert(0, str(folder))
        self.addCleanup(sys.path.remove, str(folder))
        for name in ("local_ai", "hybrid_kit", module):
            sys.modules.pop(name, None)
        self.addCleanup(sys.modules.pop, "local_ai", None)
        spec = importlib.util.spec_from_file_location(f"kit_{module}", folder / f"{module}.py")
        loaded = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(loaded)
        return loaded, folder

    def test_every_kit_compiles_and_targets_the_endpoint(self):
        for kit_id in builder.KITS:
            with self.subTest(kit=kit_id):
                files = self.kit(kit_id)
                self.assertIn("README.md", files)
                for rel, text in files.items():
                    self.assertNotIn("{{", text, rel)
                    self.assertFalse(rel.startswith(("/", "\\")) or ".." in rel, rel)
                    if rel.endswith(".py"):
                        compile(text, rel, "exec")
                joined = "\n".join(files.values())
                self.assertIn(self.BASE, joined)
                self.assertIn(self.MODEL, joined)
        csharp = self.kit("csharp-chat")
        self.assertIn("<TargetFramework>net10.0</TargetFramework>", csharp["LocalChat.csproj"])
        self.assertNotIn("PackageReference", csharp["LocalChat.csproj"])

    def test_targets_must_be_loopback(self):
        for base, model in (("http://example.com/v1", self.MODEL), ("http://127.0.0.1:9792", self.MODEL),
                            ("https://127.0.0.1:9792/v1", self.MODEL), (self.BASE, "bad model"), (self.BASE, None),
                            (None, self.MODEL)):
            with self.subTest(base=base, model=model):
                with self.assertRaises(ValueError):
                    builder.files("chat", base, model)
                with self.assertRaises(ValueError):
                    builder.snippets(base, model)
        with self.assertRaises(ValueError):
            builder.files("nope", self.BASE, self.MODEL)

    def test_snippets_are_concrete(self):
        snippets = builder.snippets(self.BASE, self.MODEL)
        self.assertGreaterEqual(len(snippets), 6)
        for snippet in snippets:
            self.assertIn(self.BASE.rsplit("/v1", 1)[0], snippet["code"])
        python = next(s for s in snippets if s["id"] == "python-stdlib")["code"]
        compile(python, "snippet", "exec")
        curl = next(s for s in snippets if s["id"] == "curl")["code"]
        payload = re.search(r'-d "(.*)"', curl).group(1).replace('\\"', '"')
        self.assertEqual(json.loads(payload)["model"], self.MODEL)

    def test_redact_kit_ships_the_same_policy_and_routes_its_sample(self):
        files = self.kit("redact-then-cloud")
        self.assertEqual(files["hybrid.py"], (ROOT / "hybrid.py").read_text(encoding="utf-8"))
        self.assertEqual(hybrid.route(files["sample.txt"])["lane"], "hybrid")
        self.assertIn("NOT SENT", files["redact_then_cloud.py"])
        self.assertIn("hybrid.drift(text, redacted)", files["redact_then_cloud.py"], "the kit blocks answers posing as redactions")
        self.assertIn("hybrid.redaction_prompt(text)", files["redact_then_cloud.py"], "the kit uses the studio's prompt")
        compile(files["redact_then_cloud.py"], "redact_then_cloud.py", "exec")

    def test_agent_tools_are_safe_and_useful(self):
        agent, _ = self.load(self.kit("tool-agent"), "agent")
        self.assertEqual(agent.calculator("4*3*60"), "720")
        for hostile in ("__import__('os').system('calc')", "open('x')", "9**99", "[1]*10"):
            with self.assertRaises(Exception):
                agent.calculator(hostile)
        self.assertIn("POL-01", agent.policy_lookup("hotel"))
        self.assertEqual(agent.first_json('Sure: ```json\n{"tool": "today", "input": ""}\n```'),
                         {"tool": "today", "input": ""})
        self.assertIsNone(agent.first_json("no json here"))

    def test_docs_kit_retrieves_the_lost_device_policy(self):
        ask, _ = self.load(self.kit("private-docs"), "ask_docs")
        hits = ask.retrieve(demos.DOCS_QUESTION, ask.load())
        # Keyword scoring is deliberately simple; the model sees the top three and cites the one that answers.
        self.assertIn(demos.DOCS_EXPECTED, [hit[1] for hit in hits])
        self.assertLessEqual(len(hits), 3)
        self.assertEqual(len(ask.load()), len(demos.POLICIES))


class EditionTests(unittest.TestCase):
    """edition.json is the brand manifest; generated files and the service must agree with it."""

    @classmethod
    def setUpClass(cls):
        cls.edition = json.loads((ROOT / "edition.json").read_text(encoding="utf-8"))

    def test_manifest_shape(self):
        e = self.edition
        self.assertEqual(e["schema"], "studio-edition/1")
        self.assertNotIn("rules", e, "rules belong only in editions/<id>.json targets")
        self.assertNotIn("forbiddenTokens", e)
        self.assertRegex(e["codeName"], r"^[A-Z][A-Za-z0-9]{5,40}$")
        self.assertRegex(e["serviceAppId"], r"^[a-z0-9]+(-[a-z0-9]+)+$")
        self.assertRegex(e["envPrefix"], r"^[A-Z][A-Z0-9]*_([A-Z0-9]+_)*$")
        self.assertIn(e["visibility"], ("public", "private"))
        self.assertRegex(e["logo"]["text"], r"^[A-Za-z0-9]{1,4}$")
        self.assertIn(e["logo"].get("style", "monogram"), ("monogram", "device"))
        if "backgroundEnd" in e["logo"]:
            self.assertRegex(e["logo"]["backgroundEnd"], r"^#[0-9A-Fa-f]{6}$")
        for group, keys in (("accent", ("base", "light1", "light2", "light3", "dark1", "dark2", "dark3", "tint")),
                            ("webAccent", ("base", "deep", "focus")), ("logo", ("background", "foreground"))):
            for key in keys:
                self.assertRegex(e[group][key], r"^#[0-9A-Fa-f]{6}$", f"{group}.{key}")

    def test_service_identity_comes_from_manifest(self):
        import edition
        import local_engine
        self.assertEqual(edition.APP_ID, self.edition["serviceAppId"])
        self.assertEqual(local_engine.APP_ID, self.edition["serviceAppId"])
        self.assertEqual(edition.DISPLAY_NAME, self.edition["displayName"])

    def test_generated_brand_source_matches_manifest(self):
        source = (ROOT / "native" / "Brand.cs").read_text(encoding="utf-8")
        constants = dict(re.findall(r'internal const string (\w+) = "([^"]*)";', source))
        e = self.edition
        expected = {"EditionId": e["id"], "DisplayName": e["displayName"], "Device": e["device"],
                    "WindowTitle": f"{e['displayName']} - {e['device']}", "Mark": e["mark"],
                    "MarkSubtitle": e["markSubtitle"], "CodeName": e["codeName"],
                    "AppUserModelId": e["appUserModelId"], "ServiceAppId": e["serviceAppId"],
                    "SchemaPrefix": e["schemaPrefix"], "EnvPrefix": e["envPrefix"],
                    "Accent": e["accent"]["base"].upper(), "AccentDeep": e["accent"]["dark2"].upper(),
                    "AccentTint": e["accent"]["tint"].upper()}
        self.assertEqual(constants, expected, "run tools\\Sync-Edition.ps1")
        self.assertIn(f"namespace {e['codeName']};", source)

    def test_accent_ramps_and_project_follow_manifest(self):
        e = self.edition
        xaml = (ROOT / "native" / "App.xaml").read_text(encoding="utf-8")
        ramp = dict(re.findall(r'<Color x:Key="SystemAccentColor(\w*)">(#[0-9A-Fa-f]{6})</Color>', xaml))
        self.assertEqual(ramp, {"": e["accent"]["base"].upper(), "Light1": e["accent"]["light1"].upper(),
                                "Light2": e["accent"]["light2"].upper(), "Light3": e["accent"]["light3"].upper(),
                                "Dark1": e["accent"]["dark1"].upper(), "Dark2": e["accent"]["dark2"].upper(),
                                "Dark3": e["accent"]["dark3"].upper()})
        self.assertIn(f'x:Class="{e["codeName"]}.ShowcaseApp"', xaml)
        css = (ROOT / "style.css").read_text(encoding="utf-8")
        for variable, key in (("--accent", "base"), ("--accent-deep", "deep"), ("--focus", "focus")):
            self.assertIn(f"{variable}:{e['webAccent'][key].lower()}", css)
        self.assertTrue((ROOT / "native" / f"{e['codeName']}.csproj").is_file())
        svg = (ROOT / "native" / "Assets" / "AppLogo.svg").read_text(encoding="utf-8")
        logo = e["logo"]
        if logo.get("style", "monogram") == "device":
            self.assertIn('data-style="device"', svg)
            self.assertEqual(svg.count("<path "), 2, "laptop-and-spark glyph has two sparks")
            paint = f'stop-color="{logo["background"]}"' if "backgroundEnd" in logo else f'fill="{logo["background"]}"'
            self.assertIn(paint, svg)
        else:
            self.assertIn(f">{logo['text']}</text>", svg)
            self.assertIn(f'fill="{logo["background"]}"', svg)

    def test_generated_icon_and_logo_sizes(self):
        import struct
        png = (ROOT / "native" / "Assets" / "AppLogo.png").read_bytes()
        self.assertEqual(png[:8], b"\x89PNG\r\n\x1a\n")
        self.assertEqual(struct.unpack(">II", png[16:24]), (256, 256))
        ico = (ROOT / "native" / "Assets" / "AppIcon.ico").read_bytes()
        reserved, kind, count = struct.unpack("<HHH", ico[:6])
        self.assertEqual((reserved, kind), (0, 1))
        sizes = [ico[6 + 16 * i] or 256 for i in range(count)]
        self.assertEqual(sizes, [16, 24, 32, 48, 64, 128, 256])
        for i in range(count):
            length, offset = struct.unpack("<II", ico[6 + 16 * i + 8:6 + 16 * i + 16])
            self.assertEqual(ico[offset:offset + 8], b"\x89PNG\r\n\x1a\n")
            self.assertLessEqual(offset + length, len(ico))

    def test_setup_and_release_scripts_are_edition_neutral(self):
        edition = json.loads((ROOT / "edition.json").read_text(encoding="utf-8"))
        identity = [edition[key] for key in ("displayName", "codeName", "appUserModelId", "serviceAppId",
                                             "schemaPrefix", "envPrefix", "shortcutName", "mark")]
        scripts = {name: (ROOT / name).read_bytes() for name in ("Setup.ps1", "Setup.cmd", "tools/New-ReleasePackage.ps1")}
        for name, data in scripts.items():
            text = data.decode("utf-8")
            self.assertNotRegex(text, r"(?<!\r)\n", f"{name} must use CRLF line endings")
            for token in identity:
                self.assertFalse(token.lower() in text.lower(), f"{name} hardcodes {token!r}; read edition.json instead")
        # Windows PowerShell 5.1 reads BOM-less scripts as ANSI, and Setup.cmd runs Setup.ps1 with it.
        self.assertTrue(scripts["Setup.ps1"].isascii())
        self.assertNotIn(b"#Requires -Version 7", scripts["Setup.ps1"])
        self.assertIn(b'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup.ps1"', scripts["Setup.cmd"])
        self.assertIn(b"-Prepare", scripts["Setup.cmd"])
        self.assertIn(b"-c (Join-Path $root 'constraints.txt')", scripts["Setup.ps1"])

    def test_constraints_pin_the_tested_requirements(self):
        def pins(name):
            lines = (ROOT / name).read_text(encoding="utf-8").splitlines()
            entries = [line.strip() for line in lines if line.strip() and not line.startswith("#")]
            for entry in entries:
                self.assertRegex(entry, r"^[a-z0-9][a-z0-9.-]*==[0-9][0-9A-Za-z.]*$", f"{name}: {entry}")
            return dict(entry.split("==") for entry in entries)
        requirements, constraints = pins("requirements.txt"), pins("constraints.txt")
        for package, version in requirements.items():
            self.assertEqual(constraints.get(package), version, f"constraints.txt must pin {package}=={version}")
        self.assertEqual(list(constraints), sorted(constraints))


class PortabilityTests(unittest.TestCase):
    def test_source_has_no_user_profile_paths(self):
        root = Path(__file__).resolve().parent.parent
        skipped = {".runtime", ".venv", ".git", "bin", "obj", "__pycache__"}
        pattern = re.compile(r"[A-Za-z]:\\+Users\\+[^\\\s\"'<>]+", re.IGNORECASE)
        offenders = []
        for path in root.rglob("*"):
            if not path.is_file() or skipped.intersection(path.relative_to(root).parts):
                continue
            if path.suffix.lower() not in {".py", ".ps1", ".psm1", ".cs", ".md", ".js", ".html", ".css",
                                           ".config", ".csproj", ".json", ".xaml", ".manifest", ".txt"}:
                continue
            for number, line in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
                if pattern.search(line):
                    offenders.append(f"{path.relative_to(root)}:{number}")
        self.assertEqual(offenders, [])


if __name__ == "__main__":
    unittest.main()
