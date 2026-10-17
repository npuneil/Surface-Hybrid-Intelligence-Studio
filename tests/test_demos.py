import array
import http.client
import json
import re
import tempfile
import threading
import unittest
from pathlib import Path

import demos
import media
from demo_runner import FAILED, DemoRunner, MediaStore
from local_engine import DEFAULT_MODEL, MEDIA_MODELS, MODELS
from server import ShowcaseServer

ROOT = Path(__file__).resolve().parent.parent


def tone(seconds, rate=16000, level=3000):
    return array.array("h", [level if (i // 20) % 2 else -level for i in range(int(seconds * rate))])


def silence(seconds, rate=16000):
    return array.array("h", bytes(int(seconds * rate) * 2))


class MediaHelperTests(unittest.TestCase):
    def test_words_normalize_numbers_symbols_and_case(self):
        self.assertEqual(media.normalize_words("Battery 40% & H-17"),
                         ["battery", "forty", "percent", "and", "h", "seventeen"])
        self.assertEqual(media.normalize_words("2,500"), ["two", "five", "hundred"])
        self.assertEqual(media.number_words(48217), ["forty", "eight", "thousand", "two", "hundred", "seventeen"])

    def test_word_errors_counts_edits(self):
        self.assertEqual(media.word_errors("dock three is down", "Dock 3 is down")["errors"], 0)
        score = media.word_errors("one two three four", "one too three")
        self.assertEqual((score["errors"], score["reference_words"]), (2, 4))
        self.assertAlmostEqual(score["accuracy"], 0.5)
        self.assertEqual(media.word_errors("", "anything")["accuracy"], 0.0)

    def test_chunks_cut_inside_silence_and_cover_all_audio(self):
        pcm = tone(20) + silence(1) + tone(15) + silence(0.8) + tone(10)
        chunks = media.plan_chunks(pcm, 16000)
        self.assertEqual(chunks[0][0], 0)
        self.assertEqual(chunks[-1][1], len(pcm))
        for (_, end), (start, _) in zip(chunks, chunks[1:]):
            self.assertEqual(end, start)
        for start, end in chunks:
            self.assertLessEqual((end - start) / 16000, 28.0)
        first_cut = chunks[0][1] / 16000
        self.assertTrue(20.0 < first_cut < 21.0, first_cut)
        self.assertEqual(media.plan_chunks(tone(5), 16000), [(0, 80000)])
        self.assertEqual(media.plan_chunks(array.array("h"), 16000), [])

    def test_chunks_fall_back_to_hard_cut_without_silence(self):
        chunks = media.plan_chunks(tone(70), 16000)
        self.assertEqual(chunks[-1][1], 70 * 16000)
        self.assertTrue(all((end - start) <= 28 * 16000 for start, end in chunks))

    def test_wav_round_trip(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "a.wav"
            media.write_wav(path, 16000, tone(0.5))
            rate, pcm = media.read_wav(path)
            self.assertEqual((rate, len(pcm)), (16000, 8000))

    def test_json_extraction_tolerates_fences_and_prose(self):
        self.assertEqual(media.extract_json('Sure:\n```json\n{"a": [1, 2]}\n```'), {"a": [1, 2]})
        self.assertEqual(media.extract_json("x [ {\"id\": 1} ] y"), [{"id": 1}])
        self.assertEqual(media.extract_json("[draft] {\"b\": 2}"), {"b": 2})
        with self.assertRaises(ValueError):
            media.extract_json("no json here")

    def test_field_matching(self):
        self.assertTrue(media.field_matches("86.40", "$86.40"))
        self.assertTrue(media.field_matches("40", 40))
        self.assertTrue(media.field_matches("H17", "Handheld scanner H-17"))
        self.assertTrue(media.field_matches("2", "P2"))
        self.assertTrue(media.field_matches("dock 3", "Dock three, Building B"))
        self.assertFalse(media.field_matches("86.40", "86.41"))
        self.assertFalse(media.field_matches("Okafor", None))
        self.assertFalse(media.field_matches("40", True))
        self.assertFalse(media.field_matches("H17", "H-71"))

    def test_cosine_and_png(self):
        self.assertAlmostEqual(media.cosine([1, 0], [1, 0]), 1.0)
        self.assertAlmostEqual(media.cosine([1, 0], [0, 1]), 0.0)
        self.assertEqual(media.cosine([0, 0], [1, 1]), 0.0)
        self.assertTrue(media.solid_png().startswith(b"\x89PNG\r\n\x1a\n"))


class RegistryTests(unittest.TestCase):
    def test_puzzle_has_exactly_one_answer_and_rehearsal_states_it(self):
        self.assertEqual(demos.valid_schedules(), [("Birch", "Atlas", "Delta", "Cedar", "Harbor")])
        self.assertEqual(demos.parse_order(demos.rehearsal_for("reasoning")[0]),
                         ["Birch", "Atlas", "Delta", "Cedar", "Harbor"])
        self.assertIn("(2) Harbor immediately after Cedar",
                      demos.schedule_violations(["Birch", "Cedar", "Delta", "Atlas", "Harbor"]))
        self.assertEqual(demos.schedule_violations(["Birch"]), ["Not a schedule of the five sites, each exactly once"])
        self.assertIsNone(demos.parse_order("No final line"))
        self.assertEqual(demos.parse_order("**ORDER:** birch, atlas, delta, cedar, harbor"),
                         ["Birch", "Atlas", "Delta", "Cedar", "Harbor"])

    def test_receipt_fixture_is_arithmetically_true(self):
        subtotal = sum(float(amount) for _, amount in demos.RECEIPT_ITEMS)
        self.assertAlmostEqual(subtotal, float(demos.RECEIPT["subtotal"]))
        self.assertEqual(f"{subtotal * 0.0825:.2f}", demos.RECEIPT["tax"])
        self.assertEqual(f"{subtotal + float(demos.RECEIPT['tax']):.2f}", demos.RECEIPT["total"])

    def test_every_demo_is_complete_and_public_view_is_safe(self):
        rows = demos.public()
        self.assertEqual([row["id"] for row in rows], list(demos.ORDER))
        self.assertEqual(len(rows), 12)
        workloads = {"transcription", "field", "translation", "writing", "documents", "vision", "coding",
                     "reasoning", "prototyping"}
        self.assertEqual({row["workload"] for row in rows}, workloads)
        for row in rows:
            with self.subTest(demo=row["id"]):
                self.assertTrue(row["title"] and row["pitch"] and row["proof"] and row["steps"])
                self.assertIsInstance(row["rehearsal"], bool)
                self.assertNotIn("rehearsal_options", row)
                self.assertNotIn("check_fields", row)
                if row["rehearsal"]:
                    self.assertEqual(len(demos.rehearsal_for(row["id"])), len(row["steps"]))
                for option in row["options"]:
                    self.assertTrue(option["prompt"])
        self.assertFalse(demos.has_rehearsal("team"))
        self.assertFalse(demos.has_rehearsal("faceoff"))
        self.assertFalse(demos.has_rehearsal("translate", "ja"))
        self.assertTrue(demos.has_rehearsal("vision", "receipt"))
        self.assertIn("Japanese", demos.prompt_for("translate", "ja"))
        self.assertEqual(demos.media_for("vision", "receipt"), "receipt.png")

    def test_demo_text_is_synthetic_and_edition_neutral(self):
        text = json.dumps([demos.DEMOS, demos.MEDIA_SPEC]).lower()
        mark = json.loads((ROOT / "edition.json").read_text(encoding="utf-8"))["mark"].lower()
        for banned in {mark, "npu", "microsoft", "surface"}:
            self.assertIsNone(re.search(rf"\b{banned}\b", text), banned)
        for item in demos.MEDIA_SPEC["files"]:
            self.assertRegex(item["name"], r"^[a-z0-9-]+\.(wav|png)$")
        self.assertIn("fictional", demos.NOTICE.lower())
        self.assertIn("fictional", demos.EMAIL.lower())

    def test_checks_pass_authored_ground_truth(self):
        self.assertTrue(demos.transcript_check(demos.MEMO_SEGMENTS, demos.script_text(demos.MEMO_SEGMENTS))["pass"])
        voice = demos.DEMOS["voice-memo"]
        self.assertTrue(all(c["pass"] for c in demos.fields_check("f", voice["check_fields"], voice["rehearsal"][1])))
        for option in ("work-order", "receipt"):
            document = demos.DEMOS["vision"]["documents"][option]
            output = demos.rehearsal_for("vision", option)[0]
            self.assertTrue(all(c["pass"] for c in demos.fields_check("f", document["check"], output)), option)
        self.assertTrue(demos.receipt_sum_check(demos.rehearsal_for("vision", "receipt")[0])["pass"])
        self.assertTrue(demos.leak_check(demos.rehearsal_for("redact")[0])["pass"])
        self.assertTrue(all(c["pass"] for c in demos.triage_checks(demos.rehearsal_for("triage")[0])))
        self.assertTrue(demos.protected_terms_check(demos.rehearsal_for("translate")[0])["pass"])
        self.assertTrue(all(c["pass"] for c in demos.brief_checks(demos.rehearsal_for("brief")[0])))
        self.assertTrue(all(c["pass"] for c in demos.code_checks(demos.rehearsal_for("code")[0])))

    def test_checks_catch_bad_output(self):
        self.assertEqual(demos.leak_check(demos.EMAIL)["value"], "9 of 9")
        self.assertFalse(demos.leak_check("Contact DANA at [EMAIL]")["pass"])
        self.assertFalse(demos.transcript_check(demos.MEMO_SEGMENTS, "Something else entirely.")["pass"])
        self.assertFalse(demos.fields_check("f", {"a": "1"}, "not json")[0]["pass"])
        self.assertFalse(demos.fields_check("f", {"a": "1"}, '{"a": 2}')[1]["pass"])
        self.assertFalse(demos.triage_checks('[{"id": "E1", "category": "urgent"}]')[0]["pass"])
        self.assertFalse(demos.schedule_check("ORDER: Atlas, Birch, Cedar, Harbor, Delta")["pass"])
        self.assertFalse(demos.schedule_check("I could not decide.")["pass"])
        self.assertFalse(demos.receipt_sum_check('{"line_items": [{"amount": "1.00"}], "subtotal": "2.00"}')["pass"])
        self.assertFalse(demos.receipt_sum_check('{"subtotal": "2.00"}')["pass"])
        self.assertFalse(demos.protected_terms_check("Northstar, HD 204, 2 pm")["pass"])
        self.assertFalse(demos.code_checks("def other(): pass")[0]["pass"])

    def test_native_app_demo_links_match_the_registry(self):
        native = (ROOT / "native" / "Demos.cs").read_text(encoding="utf-8")
        known = re.search(r"KnownIds\s*=\s*\[(.*?)\];", native, re.S)
        self.assertIsNotNone(known)
        self.assertEqual(re.findall(r'"([^"]+)"', known.group(1)), list(demos.ORDER))
        sales = (ROOT / "native" / "SalesSizing.cs").read_text(encoding="utf-8")
        links = re.findall(r'new\("([a-z]+)",\s*"[^"]+",\s*"[^"]+",\s*"[^"]+",\s*TShirtSize\.\w+,\s*"[^"]+",\s*"([a-z-]+)"', sales)
        self.assertEqual(len(links), 9, links)
        for workload, demo_id in links:
            with self.subTest(workload=workload):
                self.assertIn(demo_id, demos.DEMOS)
                self.assertEqual(demos.DEMOS[demo_id]["workload"], workload)


def make_store(folder):
    folder = Path(folder)
    folder.mkdir(parents=True, exist_ok=True)
    for name in demos.MEDIA_NAMES:
        if name.endswith(".wav"):
            media.write_wav(folder / name, 16000, tone(2))
        else:
            (folder / name).write_bytes(media.solid_png())
    digest = MediaStore(folder).digest
    (folder / "manifest.json").write_text(json.dumps({"spec_sha256": digest, "voices": ["Test voice"]}),
                                          encoding="utf-8")
    return MediaStore(folder)


class FakeDemoEngine:
    """Deterministic stand-in: replies come from queues so every pipeline can be exercised without a GPU."""

    def __init__(self):
        self.state = {"state": "ready", "detail": "Test engine", "selected_model": DEFAULT_MODEL,
                      "model": {"name": MODELS[DEFAULT_MODEL]["label"], "alias": DEFAULT_MODEL, "reasoning": False}}
        self.loaded = {DEFAULT_MODEL}
        self.replies, self.views = [], []
        self.fail = set()
        self.prompts = []

    def is_resident(self, alias):
        return alias in self.loaded

    def _load(self, alias):
        if alias in self.fail:
            raise RuntimeError(f"Injected {alias} failure")
        if alias in self.loaded:
            return None
        self.loaded.add(alias)
        return {"load_ms": 1200, "warm_ms": 300}

    def ensure_chat(self, alias):
        return self._load(alias)

    def ensure_media(self, role, warm_audio=None, warm_images=()):
        return self._load(role)

    def chat_info(self, alias=None):
        alias = alias or DEFAULT_MODEL
        return {"name": MODELS[alias]["label"], "alias": alias, "reasoning": MODELS[alias]["reasoning"]}

    @staticmethod
    def _done(text):
        return {"type": "done", "mode": "live", "ttft_ms": 120, "total_ms": 900, "output_tokens": len(text) // 4,
                "tokens_per_second": 31.5, "finish_reason": "STOP", "reasoning_characters": 0}

    def stream(self, prompt, system, max_tokens=650, alias=None):
        self.prompts.append((alias, prompt, max_tokens))
        text = self.replies.pop(0)
        yield {"type": "token", "text": text}
        yield self._done(text)

    def transcribe(self, wav, workdir):
        if "transcribe" in self.fail:
            raise RuntimeError("Injected transcription failure")
        segments = demos.MEETING_SEGMENTS if Path(wav).name == "meeting.wav" else demos.MEMO_SEGMENTS
        yield {"index": 0, "count": 1, "start_s": 0, "end_s": 2, "text": demos.script_text(segments), "ms": 400}

    def see(self, image, prompt, system, max_tokens):
        text = self.views.pop(0)
        yield {"type": "token", "text": text}
        yield self._done(text)

    def embed(self, texts):
        words = ("lost", "stolen", "laptop", "device")
        return [[0.1] + [text.lower().count(w) for w in words] for text in texts], 35

    def resident(self):
        ids = {**{a: s["id"] for a, s in MODELS.items()}, **{r: s["id"] for r, s in MEDIA_MODELS.items()}}
        return sorted(ids[a] for a in self.loaded)


def chat_replies(demo_id):
    outputs = demos.rehearsal_for(demo_id)
    return [outputs[-1]]


class RunnerTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.store = make_store(self.folder.name)
        self.engine = FakeDemoEngine()
        self.runner = DemoRunner(self.engine, self.store, {"gpu_memory": [{"reported_budget_mib": 64000}]})

    def tearDown(self):
        self.folder.cleanup()

    def run_demo(self, demo_id, mode="live", option=None, prompt=None):
        body = {"demo": demo_id, "mode": mode, "option": option, "prompt": prompt}
        return list(self.runner.run(*self.runner.validate({k: v for k, v in body.items() if v is not None})))

    def assert_complete(self, events, demo_id):
        plan, done = events[0], events[-1]
        self.assertEqual(plan["type"], "plan")
        self.assertEqual(len(plan["steps"]), len(demos.DEMOS[demo_id]["steps"]))
        self.assertEqual(done["type"], "done")
        finished = {e["index"] for e in events if e["type"] == "step" and e["state"] == "done"}
        self.assertEqual(finished, set(range(len(plan["steps"]))))
        checks = [e for e in events if e["type"] == "check"]
        self.assertEqual((done["checks_passed"], done["checks_total"]), (sum(c["pass"] for c in checks), len(checks)))
        json.dumps(events, allow_nan=False)
        return checks

    def test_every_live_pipeline_passes_on_ground_truth_output(self):
        for demo_id in demos.ORDER:
            with self.subTest(demo=demo_id):
                if demo_id == "faceoff":
                    self.engine.replies = chat_replies("reasoning") * 2
                elif demo_id == "team":
                    self.engine.replies = ["s[::-1]", "391"]
                    self.engine.views = ["TOTAL 93.53"]
                elif demo_id == "vision":
                    self.engine.views = list(demos.rehearsal_for("vision"))
                else:
                    self.engine.replies = chat_replies(demo_id)
                checks = self.assert_complete(self.run_demo(demo_id), demo_id)
                self.assertTrue(checks, "every live demo reports at least one check")
                failing = [c["label"] for c in checks if not c["pass"]]
                self.assertEqual(failing, [])
                self.assertEqual((self.engine.replies, self.engine.views), ([], []))

    def test_live_steps_report_model_and_first_use_load(self):
        events = self.run_demo("meeting")
        done = [e for e in events if e["type"] == "step" and e["state"] == "done"]
        self.assertEqual(done[0]["model"], MEDIA_MODELS["asr"]["label"])
        labels = [m["label"] for m in done[0]["metrics"]]
        self.assertIn("Load and warm-up (first use)", labels)
        self.assertIn("Speed", labels)
        self.assertIn("Transcript:\n" + demos.script_text(demos.MEETING_SEGMENTS), self.engine.prompts[-1][1])
        again = self.run_demo("voice-memo")
        first = [e for e in again if e["type"] == "step" and e["state"] == "done"][0]
        self.assertNotIn("Load and warm-up (first use)", [m["label"] for m in first["metrics"]])

    def test_reasoning_demo_uses_gpt_oss_with_reasoning_budget(self):
        self.engine.replies = chat_replies("reasoning")
        self.run_demo("reasoning")
        alias, prompt, budget = self.engine.prompts[-1]
        self.assertEqual(alias, "gpt-oss-20b")
        self.assertEqual(prompt, demos.PUZZLE)
        self.assertGreaterEqual(budget, demos.REASONING_BUDGET)
        self.engine.replies = chat_replies("reasoning") * 2
        self.run_demo("faceoff")
        budgets = {alias: budget for alias, _, budget in self.engine.prompts[-2:]}
        self.assertEqual(budgets, {"qwen2.5-coder-14b": 900, "gpt-oss-20b": demos.REASONING_BUDGET})

    def test_wrong_answers_fail_their_checks(self):
        self.engine.replies = ["ORDER: Atlas, Birch, Cedar, Harbor, Delta"]
        checks = self.assert_complete(self.run_demo("reasoning"), "reasoning")
        self.assertFalse(checks[0]["pass"])
        self.engine.replies = [demos.EMAIL]
        checks = self.assert_complete(self.run_demo("redact"), "redact")
        self.assertEqual(checks[0]["value"], "9 of 9")

    def test_step_failure_skips_dependants_and_is_reported(self):
        self.engine.fail.add("transcribe")
        events = self.run_demo("meeting")
        states = [(e["index"], e["state"]) for e in events if e["type"] == "step" and e["state"] != "running"]
        self.assertEqual(states, [(0, "failed"), (1, "skipped")])
        self.assertIn("Injected transcription failure", [e for e in events if e.get("state") == "failed"][0]["error"])
        self.assertEqual(events[-1]["steps_failed"], 1)
        self.assertEqual(events[-1]["checks_total"], 0)

    def test_team_steps_fail_independently(self):
        self.engine.fail.add("vision")
        self.engine.replies = ["s[::-1]", "391"]
        events = self.run_demo("team")
        terminal = {e["index"]: e["state"] for e in events if e["type"] == "step" and e["state"] != "running"}
        self.assertEqual(terminal, {0: "done", 1: "done", 2: "done", 3: "failed", 4: "done", 5: "done"})
        residency = [e for e in events if e["type"] == "check"][-1]
        self.assertEqual(residency["value"], "4 of 5")
        self.assertFalse(residency["pass"])
        measure = [e for e in events if e["type"] == "step" and e["index"] == 5 and e["state"] == "done"][0]
        self.assertIn({"label": "GPU dedicated budget (driver)", "value": "62.5 GiB"}, measure["metrics"])
        labels = [m["label"] for m in measure["metrics"]]
        self.assertIn("GPU shared memory, this service", labels)
        self.assertIn("GPU dedicated memory, whole GPU", labels)

    def test_llm_metrics_label_reasoning_and_short_outputs_honestly(self):
        from demo_runner import llm_metrics
        rows = {m["label"]: m["value"] for m in llm_metrics(
            {"ttft_ms": 25500, "tokens_per_second": 71.2, "output_tokens": 2031, "total_ms": 28500,
             "reasoning_characters": 7153, "finish_reason": "STOP"})}
        self.assertEqual(rows["First visible token (after hidden reasoning)"], "25.5 s")
        self.assertNotIn("Time to first token", rows)
        self.assertEqual(rows["Speed"], "71.2 tokens/s")
        rows = {m["label"]: m["value"] for m in llm_metrics(
            {"ttft_ms": 300, "tokens_per_second": 8.1, "output_tokens": 4, "total_ms": 500, "finish_reason": "STOP"})}
        self.assertEqual(rows["Time to first token"], "0.3 s")
        self.assertEqual(rows["Speed"], "Too short to measure (4 tokens)")

    def test_rehearsal_replays_authored_output_without_checks(self):
        for demo_id in demos.ORDER:
            if not demos.has_rehearsal(demo_id):
                with self.assertRaises(ValueError):
                    self.run_demo(demo_id, "rehearsal")
                continue
            with self.subTest(demo=demo_id):
                events = self.run_demo(demo_id, "rehearsal")
                self.assertEqual([e["text"] for e in events if e["type"] == "text"], demos.rehearsal_for(demo_id))
                self.assertEqual(events[-1]["checks_total"], 0)
                self.assertIn("No model ran", events[-1]["note"])
        self.assertEqual(self.engine.prompts, [])

    def test_edited_prompt_skips_prompt_bound_checks(self):
        self.engine.replies = ["ORDER: Birch, Atlas, Delta, Cedar, Harbor"]
        events = self.run_demo("reasoning", prompt="Solve it differently.")
        self.assertEqual(events[-1]["checks_total"], 0)
        self.assertIn("edited", events[-1]["note"])
        self.engine.replies = [demos.EMAIL]
        events = self.run_demo("redact", prompt="Remove names only.")
        self.assertEqual(events[-1]["checks_total"], 2, "privacy checks always run")

    def test_validation(self):
        for body in ({}, {"demo": "nope", "mode": "live"}, {"demo": "brief", "mode": "fast"},
                     {"demo": "brief", "mode": "live", "option": "es"},
                     {"demo": "translate", "mode": "live", "option": "xx"},
                     {"demo": "brief", "mode": "live", "prompt": " "},
                     {"demo": "brief", "mode": "live", "prompt": "x" * 4001},
                     {"demo": "brief", "mode": "live", "prompt": 7},
                     {"demo": "team", "mode": "live", "prompt": "Load a CPU model"},
                     {"demo": "team", "mode": "rehearsal"},
                     {"demo": "translate", "mode": "rehearsal", "option": "ja"},
                     {"demo": "brief", "mode": "rehearsal", "prompt": "Changed"}):
            with self.subTest(body=body):
                with self.assertRaises(ValueError):
                    self.runner.validate(body)
        self.assertEqual(self.runner.validate({"demo": "translate", "mode": "live"})[3], "es")
        self.assertFalse(self.runner.validate({"demo": "team", "mode": "live"})[4])

    def test_attempt_returns_sentinel(self):
        def broken():
            yield {"type": "step", "index": 0, "state": "running"}
            raise RuntimeError("boom")

        events = []
        generator = self.runner._attempt(0, broken())
        try:
            while True:
                events.append(next(generator))
        except StopIteration as stop:
            self.assertIs(stop.value, FAILED)
        self.assertEqual(events[-1]["error"], "boom")


class MediaStoreTests(unittest.TestCase):
    def test_store_is_ready_only_for_matching_spec(self):
        with tempfile.TemporaryDirectory() as folder:
            store = make_store(folder)
            self.assertEqual(store.state["state"], "ready")
            self.assertEqual(store.path("meeting.wav"), Path(folder) / "meeting.wav")
            self.assertIsNone(store.path("../server.py"))
            self.assertIsNone(store.path("manifest.json"))
            changed = MediaStore(folder, spec={"files": [{"name": "other.wav"}]})
            self.assertEqual(changed.state["state"], "pending")
            self.assertIsNone(changed.path("other.wav"))


class DemoApiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.folder = tempfile.TemporaryDirectory()
        cls.engine = FakeDemoEngine()
        cls.server = ShowcaseServer(("127.0.0.1", 0), engine=cls.engine, device={"model": "fixture"},
                                    media_store=make_store(cls.folder.name))
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()
        cls.folder.cleanup()

    def request(self, method, path, body=None, headers=None):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=10)
        request_headers = {"X-Showcase-Token": self.server.token}
        request_headers.update(headers or {})
        connection.request(method, path, body, request_headers)
        response = connection.getresponse()
        data = response.read()
        result = response.status, data, dict(response.getheaders())
        connection.close()
        return result

    def post(self, payload, headers=None):
        return self.request("POST", "/api/demo/run", json.dumps(payload), headers)

    def test_registry_and_media_endpoints(self):
        code, data, _ = self.request("GET", "/api/demos")
        self.assertEqual(code, 200)
        body = json.loads(data)
        self.assertEqual(len(body["demos"]), 12)
        self.assertEqual(body["media"]["state"], "ready")
        code, data, headers = self.request("GET", "/api/demo-media/meeting.wav")
        self.assertEqual((code, headers["Content-Type"], data[:4]), (200, "audio/wav", b"RIFF"))
        code, data, headers = self.request("GET", "/api/demo-media/receipt.png")
        self.assertEqual((code, headers["Content-Type"]), (200, "image/png"))
        for path in ("/api/demo-media/manifest.json", "/api/demo-media/..%2Fserver.py", "/api/demo-media/"):
            self.assertEqual(self.request("GET", path)[0], 404, path)
        self.assertEqual(self.request("GET", "/api/demos", headers={"Host": "evil.example"})[0], 403)

    def test_rehearsal_stream_and_guards(self):
        code, data, _ = self.post({"demo": "voice-memo", "mode": "rehearsal"})
        events = [json.loads(line) for line in data.decode().splitlines()]
        self.assertEqual(code, 200)
        self.assertEqual(events[0]["type"], "plan")
        self.assertEqual(events[-1]["mode"], "rehearsal")
        self.assertEqual(self.post({"demo": "team", "mode": "rehearsal"})[0], 400)
        self.assertEqual(self.post({"demo": "voice-memo", "mode": "live"}, {"X-Showcase-Token": "bad"})[0], 403)
        self.server.run_lock.acquire()
        try:
            self.assertEqual(self.post({"demo": "brief", "mode": "rehearsal"})[0], 409)
        finally:
            self.server.run_lock.release()
        self.engine.state["state"] = "loading"
        try:
            self.assertEqual(self.post({"demo": "brief", "mode": "live"})[0], 503)
        finally:
            self.engine.state["state"] = "ready"

    def test_live_stream_reports_checks_and_releases_lock(self):
        self.engine.replies = chat_replies("triage")
        code, data, _ = self.post({"demo": "triage", "mode": "live"})
        events = [json.loads(line) for line in data.decode().splitlines()]
        self.assertEqual(code, 200)
        self.assertEqual((events[-1]["checks_passed"], events[-1]["checks_total"]), (2, 2))
        self.assertFalse(self.server.run_lock.locked())


class RendererTests(unittest.TestCase):
    def test_renderer_validates_names_and_never_reads_outside_spec(self):
        script = (ROOT / "Render-DemoMedia.ps1").read_text(encoding="utf-8")
        self.assertIn("^[a-z0-9-]+\\.(wav|png)$", script)
        self.assertNotRegex(script, re.compile(r"Invoke-WebRequest|Start-Process|Invoke-Expression", re.I))
        self.assertTrue((ROOT / ".gitignore").read_text(encoding="utf-8").count(".runtime"))


if __name__ == "__main__":
    unittest.main()
