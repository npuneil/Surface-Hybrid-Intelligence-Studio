"""Runs the live demos as NDJSON event streams: synthetic media, model residency, GPU inference and checks.

Event types: plan, step (running/done/failed/skipped), text, activity, check, done. Live checks compare model
output with the ground truth written into demos.py; rehearsal replays authored output and never runs checks.
"""
import hashlib
import json
import logging
import os
import re
import subprocess
import tempfile
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

import demos
import gpu_runtime
import media
from local_engine import LARGE_MODELS, MEDIA_MODELS, MODELS

ROOT = Path(__file__).resolve().parent
MEDIA_DIR = ROOT / ".runtime" / "demo-media"
RENDERER = ROOT / "Render-DemoMedia.ps1"
MAX_PROMPT = 4000
FAILED = object()


class MediaStore:
    """Synthetic speech and document images, rendered once on this laptop by Windows and cached by spec hash."""

    def __init__(self, folder=MEDIA_DIR, spec=None):
        self.folder = Path(folder)
        self.spec = spec or demos.MEDIA_SPEC
        self.names = tuple(item["name"] for item in self.spec["files"])
        self.digest = hashlib.sha256(json.dumps(self.spec, sort_keys=True).encode()).hexdigest()
        self.lock = threading.Lock()
        self.state = {"state": "pending", "detail": "Demo media has not been rendered yet.", "voices": []}
        current = self._current()
        if current is not None:
            self.state = self._ready(current)

    def _current(self):
        try:
            data = json.loads((self.folder / "manifest.json").read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return None
        if data.get("spec_sha256") != self.digest or not all((self.folder / n).is_file() for n in self.names):
            return None
        return data

    @staticmethod
    def _ready(data):
        return {"state": "ready", "detail": "Synthetic speech and documents rendered on this laptop.",
                "voices": data.get("voices", []), "rendered_at": data.get("rendered_at")}

    def ensure(self):
        with self.lock:
            data = self._current()
            if data is None:
                self.state = {"state": "rendering", "voices": [],
                              "detail": "Rendering synthetic speech and documents with Windows."}
                try:
                    data = self._render()
                except Exception as exc:
                    logging.exception("Demo media rendering failed")
                    self.state = {"state": "error", "voices": [], "detail": f"Demo media could not be rendered: {exc}"}
                    return dict(self.state)
            self.state = self._ready(data)
            return dict(self.state)

    def _render(self):
        self.folder.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory() as folder:
            spec = Path(folder) / "spec.json"
            spec.write_text(json.dumps(self.spec), encoding="utf-8")
            # Windows PowerShell 5.1 ships System.Speech and System.Drawing on every Windows 11 device.
            result = subprocess.run(
                ["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                 "-File", str(RENDERER), "-Spec", str(spec), "-OutDir", str(self.folder)],
                capture_output=True, text=True, timeout=180)
        if result.returncode != 0:
            message = (result.stderr or result.stdout or "").strip().splitlines()
            raise RuntimeError(message[-1][:300] if message else f"renderer exited with {result.returncode}")
        report = json.loads(result.stdout.strip().splitlines()[-1])
        files = report.get("files") or []
        if isinstance(files, dict):
            files = [files]
        voices = sorted({voice for item in files for voice in (item.get("voices") or [])})
        manifest = {"spec_sha256": self.digest, "rendered_at": datetime.now(timezone.utc).isoformat(),
                    "files": files, "voices": voices}
        (self.folder / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        return manifest

    def path(self, name):
        if name not in self.names or self.state.get("state") != "ready":
            return None
        file = self.folder / name
        return file if file.is_file() else None

    def require(self, name):
        if self.state.get("state") != "ready":
            self.ensure()
        file = self.path(name)
        if file is None:
            raise RuntimeError(self.state.get("detail") or "Demo media is unavailable.")
        return file


def seconds(ms):
    return "n/a" if ms is None else f"{ms / 1000:.1f} s"


def gib(value):
    return "Unavailable" if value is None else f"{value / 2 ** 30:.1f} GiB"


def metric(label, value):
    return {"label": label, "value": value}


MIN_SPEED_TOKENS = 20


def llm_metrics(done):
    first_label = ("First visible token (after hidden reasoning)" if done.get("reasoning_characters")
                   else "Time to first token")
    tokens = done.get("output_tokens")
    if not done.get("tokens_per_second"):
        speed = "n/a"
    elif tokens and tokens < MIN_SPEED_TOKENS:
        speed = f"Too short to measure ({tokens} tokens)"
    else:
        speed = f"{done['tokens_per_second']:.1f} tokens/s"
    rows = [metric(first_label, seconds(done.get("ttft_ms"))),
            metric("Speed", speed),
            metric("Output tokens", str(done["output_tokens"]) if done.get("output_tokens") else "n/a"),
            metric("Total time", seconds(done.get("total_ms")))]
    if done.get("reasoning_characters"):
        rows.append(metric("Hidden reasoning", f"{done['reasoning_characters']:,} characters"))
    if done.get("finish_reason") and done["finish_reason"] != "STOP":
        rows.append(metric("Finish", str(done["finish_reason"]).replace("_", " ").title()))
    return rows


def load_metric(timing):
    if not timing:
        return []
    return [metric("Load and warm-up (first use)", seconds(timing["load_ms"] + timing["warm_ms"]))]


class DemoRunner:
    def __init__(self, engine, media_store, device=None):
        self.engine = engine
        self.media = media_store
        self.device = device or {}

    # ------------------------------------------------------------ request validation
    def validate(self, body):
        demo_id = body.get("demo")
        if not isinstance(demo_id, str) or demo_id not in demos.DEMOS:
            raise ValueError("Select a valid demo.")
        demo = demos.DEMOS[demo_id]
        mode = body.get("mode")
        if mode not in ("live", "rehearsal"):
            raise ValueError("Select live or rehearsal mode.")
        option = body.get("option")
        choices = demos.option_ids(demo)
        if choices:
            option = demos.default_option(demo) if option is None else option
            if option not in choices:
                raise ValueError("Select a valid option for this demo.")
        elif option is not None:
            raise ValueError("This demo has no options.")
        default = demos.prompt_for(demo_id, option)
        prompt = body.get("prompt")
        if prompt is None:
            prompt = default
        if not isinstance(prompt, str):
            raise ValueError("Prompt must be text.")
        if not demos.is_editable(demo_id):
            if prompt.strip() != default.strip():
                raise ValueError("This demo uses a fixed measurement and has no editable prompt.")
        elif not 1 <= len(prompt.strip()) <= MAX_PROMPT:
            raise ValueError(f"Prompt must contain 1 to {MAX_PROMPT} characters.")
        if mode == "rehearsal":
            if not demos.has_rehearsal(demo_id, option):
                raise ValueError("This demo only runs live; it has no authored rehearsal.")
            if prompt.strip() != default.strip():
                raise ValueError("Rehearsal uses the fixed authored sample prompt.")
        return demo_id, mode, prompt.strip(), option, prompt.strip() != default.strip()

    # ------------------------------------------------------------ orchestration
    def role_label(self, role):
        if role in MEDIA_MODELS:
            return MEDIA_MODELS[role]["label"]
        if role in MODELS:
            return MODELS[role]["label"]
        if role == "chat":
            return (self.engine.state.get("model") or {}).get("name") or "Selected chat model"
        return "Foundry Local and Windows counters"

    def run(self, demo_id, mode, prompt, option=None, edited=False):
        started = time.perf_counter()
        demo = demos.DEMOS[demo_id]
        yield {"type": "plan", "demo": demo_id, "mode": mode, "option": option, "edited": edited,
               "steps": [{"title": step["title"], "model": self.role_label(step["role"])} for step in demo["steps"]]}
        passed = total = failed = 0
        source = (self._rehearse(demo_id, option) if mode == "rehearsal"
                  else getattr(self, "_live_" + demo_id.replace("-", "_"))(prompt, option, edited))
        try:
            for event in source:
                if event["type"] == "check":
                    total += 1
                    passed += bool(event["pass"])
                elif event["type"] == "step" and event["state"] == "failed":
                    failed += 1
                yield event
        finally:
            source.close()
        note = None
        if mode == "rehearsal":
            note = "Rehearsal replays authored sample output. No model ran and no checks were scored."
        elif edited:
            note = "The prompt was edited, so checks tied to the default prompt were skipped."
        yield {"type": "done", "mode": mode, "total_ms": round((time.perf_counter() - started) * 1000),
               "checks_passed": passed, "checks_total": total, "steps_failed": failed, "note": note}

    def _rehearse(self, demo_id, option):
        for index, text in enumerate(demos.rehearsal_for(demo_id, option)):
            yield {"type": "step", "index": index, "state": "running", "detail": "Replaying authored sample"}
            yield {"type": "text", "index": index, "text": text}
            yield {"type": "step", "index": index, "state": "done", "model": "Authored sample",
                   "metrics": [metric("Mode", "Rehearsal: no model ran")]}

    def _attempt(self, index, step):
        """Run one step; a failure is reported on that step and FAILED is returned instead of raising."""
        try:
            return (yield from step)
        except Exception as exc:
            logging.exception("Demo step %s failed", index)
            yield {"type": "step", "index": index, "state": "failed", "error": str(exc) or type(exc).__name__}
            return FAILED

    @staticmethod
    def _skip(*indexes):
        for index in indexes:
            yield {"type": "step", "index": index, "state": "skipped",
                   "detail": "Skipped because an earlier step failed."}

    # ------------------------------------------------------------ step primitives
    def _chat(self, index, alias, prompt, system, max_tokens):
        metrics = []
        if alias is not None and not self.engine.is_resident(alias):
            yield {"type": "step", "index": index, "state": "running", "model": MODELS[alias]["label"],
                   "detail": f"Loading {MODELS[alias]['label']} on the RTX GPU (first use only)"}
            metrics += load_metric(self.engine.ensure_chat(alias))
        info = self.engine.chat_info(alias)
        if info.get("reasoning"):
            max_tokens = max(max_tokens, demos.REASONING_BUDGET)
        yield {"type": "step", "index": index, "state": "running", "model": info["name"],
               "detail": f"Generating with {info['name']} on the RTX GPU"}
        parts, done = [], None
        for event in self.engine.stream(prompt, system, max_tokens=max_tokens, alias=alias):
            if event["type"] == "token":
                parts.append(event["text"])
                yield {"type": "text", "index": index, "text": event["text"]}
            elif event["type"] == "activity":
                yield {"type": "activity", "index": index, "reasoning_characters": event["reasoning_characters"]}
            elif event["type"] == "done":
                done = event
        yield {"type": "step", "index": index, "state": "done", "model": info["name"],
               "metrics": metrics + llm_metrics(done or {})}
        return "".join(parts)

    def _ensure_media(self, index, role, **warm):
        label = MEDIA_MODELS[role]["label"]
        if not self.engine.is_resident(role):
            yield {"type": "step", "index": index, "state": "running", "model": label,
                   "detail": f"Loading {label} on the RTX GPU and warming it up (first use only)"}
        return load_metric(self.engine.ensure_media(role, **warm))

    def _transcribe(self, index, name):
        wav = self.media.require(name)
        metrics = yield from self._ensure_media(index, "asr", warm_audio=wav)
        rate, pcm = media.read_wav(wav)
        duration = len(pcm) / rate
        label = MEDIA_MODELS["asr"]["label"]
        yield {"type": "step", "index": index, "state": "running", "model": label,
               "detail": f"Transcribing {duration:.0f} seconds of audio on the RTX GPU"}
        parts, spent, count = [], 0, 0
        with tempfile.TemporaryDirectory() as folder:
            for chunk in self.engine.transcribe(wav, folder):
                count = chunk["count"]
                spent += chunk["ms"]
                if chunk["text"]:
                    yield {"type": "text", "index": index, "text": (" " if parts else "") + chunk["text"]}
                    parts.append(chunk["text"])
                yield {"type": "step", "index": index, "state": "running", "model": label,
                       "detail": f"Transcribed part {chunk['index'] + 1} of {count}"}
        speed = f"{duration / (spent / 1000):.0f}x real time" if spent else "n/a"
        yield {"type": "step", "index": index, "state": "done", "model": label, "metrics": metrics + [
            metric("Audio length", f"{duration:.1f} s"), metric("Parts (30-second windows)", str(count)),
            metric("Transcription time", seconds(spent)), metric("Speed", speed)]}
        return " ".join(parts)

    def _see(self, index, name, prompt, system, max_tokens, warm_names):
        image = self.media.require(name).read_bytes()
        warm_images = [self.media.require(other).read_bytes() for other in warm_names]
        metrics = yield from self._ensure_media(index, "vision", warm_images=warm_images)
        label = MEDIA_MODELS["vision"]["label"]
        yield {"type": "step", "index": index, "state": "running", "model": label,
               "detail": f"Reading the image with {label} on the RTX GPU"}
        parts, done = [], None
        for event in self.engine.see(image, prompt, system, max_tokens):
            if event["type"] == "token":
                parts.append(event["text"])
                yield {"type": "text", "index": index, "text": event["text"]}
            elif event["type"] == "done":
                done = event
        yield {"type": "step", "index": index, "state": "done", "model": label,
               "metrics": metrics + llm_metrics(done or {})}
        return "".join(parts)

    def _search(self, index, question, top=3):
        metrics = yield from self._ensure_media(index, "embed")
        label = MEDIA_MODELS["embed"]["label"]
        yield {"type": "step", "index": index, "state": "running", "model": label,
               "detail": "Embedding the question and twelve policies on the RTX GPU"}
        passages = [f"{title}: {text}" for _, title, text in demos.POLICIES]
        vectors, elapsed = self.engine.embed(passages + [demos.QUERY_INSTRUCTION + question])
        query = vectors[-1]
        ranked = sorted(((media.cosine(query, vector), policy) for vector, policy in zip(vectors, demos.POLICIES)),
                        key=lambda pair: pair[0], reverse=True)[:top]
        lines = [f"{rank}. {pid} {title}  (similarity {score:.3f})"
                 for rank, (score, (pid, title, _)) in enumerate(ranked, 1)]
        yield {"type": "text", "index": index, "text": "\n".join(lines)}
        yield {"type": "step", "index": index, "state": "done", "model": label, "metrics": metrics + [
            metric("Passages searched", str(len(passages))), metric("Vector size", str(len(query))),
            metric("Embedding time", seconds(elapsed) if elapsed >= 1000 else f"{elapsed} ms")]}
        return [policy for _, policy in ranked]

    # ------------------------------------------------------------ pipelines
    def _live_meeting(self, prompt, option, edited):
        demo = demos.DEMOS["meeting"]
        transcript = yield from self._attempt(0, self._transcribe(0, "meeting.wav"))
        if transcript is FAILED:
            yield from self._skip(1)
            return
        yield demos.transcript_check(demos.MEETING_SEGMENTS, transcript)
        minutes = yield from self._attempt(1, self._chat(1, None, f"{prompt}\n\nTranscript:\n{transcript}",
                                                          demo["system"], demo["max_tokens"]))
        if minutes is not FAILED and not edited:
            flagged = "not assigned" in minutes.lower()
            yield demos.check("Unowned action marked 'Not assigned'", "Yes" if flagged else "No", flagged,
                              "Nobody took the security review; the minutes must not invent an owner.")

    def _live_voice_memo(self, prompt, option, edited):
        demo = demos.DEMOS["voice-memo"]
        transcript = yield from self._attempt(0, self._transcribe(0, "voice-memo.wav"))
        if transcript is FAILED:
            yield from self._skip(1)
            return
        yield demos.transcript_check(demos.MEMO_SEGMENTS, transcript)
        order = yield from self._attempt(1, self._chat(1, None, f"{prompt}\n\nTranscript:\n{transcript}",
                                                        demo["system"], demo["max_tokens"]))
        if order is not FAILED and not edited:
            yield from demos.fields_check("Work order fields match the memo", demo["check_fields"], order)

    def _live_vision(self, prompt, option, edited):
        demo = demos.DEMOS["vision"]
        document = demo["documents"][option]
        # Warm on both documents so switching documents later does not pay first-shape GPU tuning.
        output = yield from self._attempt(0, self._see(0, document["media"], prompt, demo["system"], demo["max_tokens"],
                                                       [doc["media"] for doc in demo["documents"].values()]))
        if output is FAILED or edited:
            return
        label = "Receipt fields match the document" if option == "receipt" else "Form fields match the document"
        yield from demos.fields_check(label, document["check"], output)
        if option == "receipt":
            yield demos.receipt_sum_check(output)

    def _live_ask_docs(self, prompt, option, edited):
        demo = demos.DEMOS["ask-docs"]
        found = yield from self._attempt(0, self._search(0, prompt))
        if found is FAILED:
            yield from self._skip(1)
            return
        ids = [pid for pid, _, _ in found]
        if prompt == demos.DOCS_QUESTION:
            rank = ids.index(demos.DOCS_EXPECTED) + 1 if demos.DOCS_EXPECTED in ids else None
            yield demos.check("Lost-device policy retrieved", f"Rank {rank}" if rank else "Not in top 3",
                              rank is not None, "POL-03 is the policy written to answer this question.")
        excerpts = "\n".join(f"[{pid}] {title}: {text}" for pid, title, text in found)
        answer = yield from self._attempt(1, self._chat(1, None, f"Question: {prompt}\n\nPolicy excerpts:\n{excerpts}",
                                                         demo["system"], demo["max_tokens"]))
        if answer is FAILED:
            return
        cited = sorted(set(re.findall(r"POL-\d{2}", answer)))
        grounded = bool(cited) and all(pid in ids for pid in cited)
        yield demos.check("Answer cites only retrieved policies", ", ".join(cited) or "No citation", grounded,
                          "Every cited policy ID must be one of the three retrieved excerpts.")

    def _text_demo(self, demo_id, prompt):
        demo = demos.DEMOS[demo_id]
        return (yield from self._attempt(0, self._chat(0, demo.get("model"), f"{prompt}\n\n{demo['source']}"
                                                       if demo["source"] else prompt, demo["system"],
                                                       demo["max_tokens"])))

    def _live_translate(self, prompt, option, edited):
        output = yield from self._text_demo("translate", prompt)
        if output is not FAILED:
            yield demos.protected_terms_check(output)

    def _live_brief(self, prompt, option, edited):
        output = yield from self._text_demo("brief", prompt)
        if output is not FAILED and not edited:
            yield from demos.brief_checks(output)

    def _live_redact(self, prompt, option, edited):
        output = yield from self._text_demo("redact", prompt)
        if output is FAILED:
            return
        yield demos.leak_check(output)
        kept = "Northstar service desk" in output
        yield demos.check("Business wording kept", "Yes" if kept else "No", kept,
                          "The company name and request must survive; only personal data is replaced.")

    def _live_triage(self, prompt, option, edited):
        output = yield from self._text_demo("triage", prompt)
        if output is not FAILED and not edited:
            yield from demos.triage_checks(output)

    def _live_code(self, prompt, option, edited):
        output = yield from self._text_demo("code", prompt)
        if output is not FAILED and not edited:
            yield from demos.code_checks(output)

    def _live_reasoning(self, prompt, option, edited):
        output = yield from self._text_demo("reasoning", prompt)
        if output is not FAILED and not edited:
            yield demos.schedule_check(output)

    def _live_faceoff(self, prompt, option, edited):
        demo = demos.DEMOS["faceoff"]
        for index, step in enumerate(demo["steps"]):
            alias = step["role"]
            output = yield from self._attempt(index, self._chat(index, alias, prompt, demo["system"],
                                                                 demo["max_tokens"] if MODELS[alias]["reasoning"] else 900))
            if output is not FAILED and not edited:
                result = demos.schedule_check(output)
                result["label"] = f"{MODELS[alias]['label']}: {result['label'][0].lower()}{result['label'][1:]}"
                yield result

    def _live_team(self, prompt, option, edited):
        tasks = (
            ("qwen2.5-coder-14b", "Write a Python expression that reverses the string s. Reply with the expression only.",
             "[::-1]", "Reversal expression contains [::-1]"),
            ("gpt-oss-20b", "What is 17 multiplied by 23? Reply with the number only.", "391", "Answer is 391"),
        )
        for index, (alias, question, expected, label) in enumerate(tasks):
            answer = yield from self._attempt(index, self._chat(index, alias, question, "Answer briefly.",
                                                                 1024 if MODELS[alias]["reasoning"] else 48))
            if answer is not FAILED:
                yield demos.check(f"{MODELS[alias]['label']}: {label}", "Yes" if expected in answer else "No",
                                  expected in answer, "Ground truth is fixed in the demo.")
        transcript = yield from self._attempt(2, self._transcribe(2, "voice-memo.wav"))
        if transcript is not FAILED:
            yield demos.transcript_check(demos.MEMO_SEGMENTS, transcript)
        total = yield from self._attempt(3, self._see(3, "receipt.png", "What is the TOTAL on this receipt? Reply with "
                                                      "the amount only.", "Answer briefly.", 24,
                                                      [doc["media"] for doc in demos.DEMOS["vision"]["documents"].values()]))
        if total is not FAILED:
            yield demos.check(f"{MEDIA_MODELS['vision']['label']}: receipt total is 93.53",
                              "Yes" if "93.53" in total else "No", "93.53" in total, "Printed on the synthetic receipt.")
        found = yield from self._attempt(4, self._search(4, demos.DOCS_QUESTION, top=1))
        if found is not FAILED:
            hit = found[0][0] == demos.DOCS_EXPECTED
            yield demos.check(f"{MEDIA_MODELS['embed']['label']}: top match is POL-03", found[0][0], hit,
                              "The lost-device policy should rank first.")
        yield from self._attempt(5, self._measure(5))

    def _measure(self, index):
        yield {"type": "step", "index": index, "state": "running", "model": self.role_label("measure"),
               "detail": "Reading loaded models from Foundry Local and GPU memory from Windows counters"}
        loaded = set(self.engine.resident())
        expected = [(spec["label"], spec["id"]) for spec in (*MODELS.values(), *MEDIA_MODELS.values())]
        present = [label for label, model_id in expected if model_id in loaded]
        usage = gpu_runtime.gpu_memory_usage(os.getpid())
        private = gpu_runtime.process_private_bytes()
        budget = sum(item.get("reported_budget_mib", 0) for item in self.device.get("gpu_memory") or []) or None
        lines = [f"{'Loaded' if model_id in loaded else 'Not loaded'}: {label} ({model_id})"
                 for label, model_id in expected]
        large = (self.engine.state.get("model") or {}) if self.engine.state.get("selected_model") in LARGE_MODELS else {}
        if large.get("id") in loaded:
            lines.append(f"Also loaded: {large['name']} ({large['id']}, {large.get('provider')}, {large.get('offload')}"
                         + (f"; at load: {large['gpu_memory']}" if large.get("gpu_memory") else "") + ")")
        if usage:
            lines.append("Whole-GPU figures include every app using the GPU on this PC. Shared usage is system "
                         "memory the GPU borrows when dedicated memory is full.")
        yield {"type": "text", "index": index, "text": "\n".join(lines)}
        yield {"type": "step", "index": index, "state": "done", "model": self.role_label("measure"), "metrics": [
            metric("Models resident at once", f"{len(present)} of {len(expected)}"),
            metric("GPU dedicated memory, this service", gib(usage and usage["process_dedicated_bytes"])),
            metric("GPU shared memory, this service", gib(usage and usage["process_shared_bytes"])),
            metric("GPU dedicated memory, whole GPU", gib(usage and usage["adapter_dedicated_bytes"])),
            metric("GPU dedicated budget (driver)", gib(budget and budget * 2 ** 20)),
            metric("Private memory, this service", gib(private))]}
        yield demos.check("Models resident at the same time", f"{len(present)} of {len(expected)}",
                          len(present) == len(expected),
                          "Read from Foundry Local's loaded-model list. GPU memory is from Windows GPU memory "
                          "counters" + ("." if usage else " (unavailable on this device)."))
