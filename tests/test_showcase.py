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
        self.assertIn(f">{e['logo']['text']}</text>", svg)
        self.assertIn(f'fill="{e["logo"]["background"]}"', svg)


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
