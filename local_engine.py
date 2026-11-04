"""Real, serialized Foundry Local inference. No authored fallback."""
import array
import json
import logging
import os
import re
import tempfile
import threading
import time
import warnings
from pathlib import Path

import device_context
import gpu_runtime
import llama_engine
import media
import target_plan
from edition import APP_ID, ENV_PREFIX
from llama_engine import LARGE_MODELS
from surface_devicekit import foundry

DEFAULT_MODEL = "qwen2.5-coder-14b"
PROVIDER = "CUDAExecutionProvider"
LANES_ENV = f"{ENV_PREFIX}ENGINE_LANES"
MARKERS = ("<|channel|>", "<|start|>", "<|message|>", "<|end|>", "<|return|>", "<|endoftext|>", "<|fim_suffix|>")
PENDING_MARKER = re.compile(r"<\|[a-z_]{0,24}\|?")
UNKNOWN_MARKER = re.compile(r"<\|[a-z_]{1,24}\|>")
# The models the demos were tuned on (an NVIDIA RTX GPU). On that hardware they run exactly as listed; on any other PC the engine
# keeps these names as the demos' vocabulary and maps each to the model target_plan chose for it (see LocalEngine.alias_for).
MODELS = {
    DEFAULT_MODEL: {"id": "qwen2.5-coder-14b-instruct-cuda-gpu:4",
                    "label": "Qwen2.5-Coder 14B", "reasoning": False, "params": "14B dense"},
    "gpt-oss-20b": {"id": "gpt-oss-20b-cuda-gpu:1",
                    "label": "GPT-OSS 20B", "reasoning": True, "params": "21B MoE, 3.6B active"},
}


def model_spec(alias):
    """Chat model spec from Foundry Local (MODELS) or the llama.cpp larger-model path (LARGE_MODELS)."""
    return MODELS.get(alias) or LARGE_MODELS[alias]
# Specialist models for the live demos. All CUDA; they load on first use and stay resident beside the chat model.
# Whisper large-v3-turbo is deliberately not used: its catalog package caps output at 20 tokens (max_length).
MEDIA_MODELS = {
    "asr": {"id": "openai-whisper-medium-cuda-gpu:3", "label": "Whisper medium",
            "task": "automatic-speech-recognition"},
    "vision": {"id": "qwen3-vl-4b-instruct-cuda-gpu:2", "label": "Qwen3-VL 4B", "task": "vision-language-chat"},
    "embed": {"id": "qwen3-embedding-0.6b-cuda-gpu:1", "label": "Qwen3-Embedding 0.6B", "task": "embeddings"},
}
RTX_WHERE = "the RTX GPU"


def lane_phrase(lane, device_name=""):
    """The words the demos use for where a model runs. It names only what the lane is, never a speed."""
    if lane == "npu":
        return "the NPU"
    if lane == "cpu":
        return "the CPU"
    return RTX_WHERE if "RTX" in (device_name or "").upper() else "the GPU"


def _reference_models():
    return {alias: {**spec, "slot": target_plan.BY_REFERENCE[alias].key, "lane": "gpu", "device": "GPU", "ep": "cuda",
                    "provider": PROVIDER, "where": RTX_WHERE, "channel": "harmony" if spec["reasoning"] else None}
            for alias, spec in MODELS.items()}


def _reference_media():
    return {role: {**spec, "slot": role, "lane": "gpu", "device": "GPU", "ep": "cuda", "provider": PROVIDER,
                   "where": RTX_WHERE, "blocker": None}
            for role, spec in MEDIA_MODELS.items()}


def forced_lanes():
    """HYBRID_STUDIO_ENGINE_LANES (for example 'cpu') limits which lanes the engine may use. A diagnostic for testing the generic path on
    any PC; the device report and every claim still describe the real hardware."""
    raw = os.environ.get(LANES_ENV, "").strip().lower()
    lanes = {part for part in re.split(r"[\s,;]+", raw) if part}
    unknown = lanes - {"npu", "gpu", "cpu"}
    if unknown:
        raise RuntimeError(f"{LANES_ENV} lists unknown lanes {sorted(unknown)}. Use npu, gpu and cpu.")
    return lanes or None


def storage_dirs():
    """(app data folder, model cache folder): the showcase-wide ones when they exist on this PC, otherwise a private pair under the profile."""
    shared = Path(os.environ["LOCALAPPDATA"]) / "ASUAgenticShowcase"
    if (shared / "models").is_dir():
        return shared, shared / "models"
    app_data = Path.home() / f".{APP_ID}"
    return app_data, app_data / "cache"


class HarmonyFilter:
    """Separate GPT-OSS channel-framed text even when the SDK labels it DEFAULT."""

    def __init__(self):
        self.buffer = ""
        self.mode = "message"
        self.channel = "final"
        self.header = ""

    def feed(self, text, *, final=False):
        self.buffer += text
        visible = []
        reasoning = 0

        def consume(chunk):
            nonlocal reasoning
            if self.mode == "channel":
                self.header += chunk
            elif self.mode == "message":
                if self.channel == "final":
                    visible.append(chunk)
                else:
                    reasoning += len(chunk)

        while self.buffer:
            start = self.buffer.find("<|")
            if start == -1:
                keep = 1 if not final and self.buffer.endswith("<") else 0
                chunk = self.buffer[:-keep] if keep else self.buffer
                consume(chunk)
                self.buffer = self.buffer[-keep:] if keep else ""
                break
            if start:
                consume(self.buffer[:start])
                self.buffer = self.buffer[start:]
            token = next((m for m in MARKERS if self.buffer.startswith(m)), None)
            if token is None:
                if PENDING_MARKER.fullmatch(self.buffer):
                    if not final:
                        break
                    if any(m.startswith(self.buffer) for m in MARKERS):
                        raise RuntimeError("Incomplete or malformed GPT-OSS channel framing; answer not completed.")
                if UNKNOWN_MARKER.match(self.buffer) and self.mode == "message" and self.channel == "final":
                    # Fixed message: exception text reaches the UI and must never echo model output.
                    raise RuntimeError("Unsupported GPT-OSS control marker in the answer; output stopped.")
                # A literal "<|" in model text stays in its channel (hidden unless it is the final answer).
                consume("<|")
                self.buffer = self.buffer[2:]
                continue
            self.buffer = self.buffer[len(token):]
            if token == "<|channel|>":
                self.mode, self.header = "channel", ""
            elif token == "<|start|>":
                self.mode, self.channel = "header", "final"
            elif token == "<|message|>":
                if self.mode == "channel":
                    self.channel = self.header.strip()
                    if not self.channel:
                        raise RuntimeError("GPT-OSS returned an empty channel name.")
                self.mode = "message"
            elif token == "<|end|>":
                self.mode = "header"
            elif token in ("<|return|>", "<|endoftext|>", "<|fim_suffix|>"):
                self.mode = "done"
        return "".join(visible), reasoning


def _partial_suffix(text, tag):
    """Length of the longest end of text that is the start of tag, so a tag split across two chunks is not shown."""
    for size in range(min(len(tag) - 1, len(text)), 0, -1):
        if tag.startswith(text[-size:]):
            return size
    return 0


class ThinkFilter:
    """Hide the <think>...</think> reasoning that DeepSeek-R1, Phi-4 reasoning and Qwen3 write into their answer.

    Some chat templates open the block for the model, so the stream can start inside it and show only the closing tag. Until it sees a tag
    the filter cannot tell that from a plain answer, so it holds the text back: a closing tag means the held text was reasoning, and the
    end of the stream with no tag at all means it was the answer. Like HarmonyFilter, feed returns (visible text, hidden characters)."""

    OPEN, CLOSE = "<think>", "</think>"

    def __init__(self):
        self.buffer = ""
        self.mode = "undecided"
        self.lead = False

    @property
    def pending(self):
        """Characters held back that may still turn out to be reasoning (not yet counted as hidden)."""
        return len(self.buffer) if self.mode == "undecided" else 0

    def reasoning_is_separate(self):
        """The runtime already delivers the reasoning apart from the answer, so everything that follows is the answer."""
        if self.mode == "undecided":
            self.mode = "answer"

    def feed(self, text, *, final=False):
        self.buffer += text
        visible = []
        hidden = 0
        while self.buffer:
            if self.mode == "undecided":
                start, end = self.buffer.find(self.OPEN), self.buffer.find(self.CLOSE)
                if start != -1 and (end == -1 or start < end):
                    if self.buffer[:start].strip():
                        visible.append(self.buffer[:start])
                    self.buffer = self.buffer[start + len(self.OPEN):]
                    self.mode = "think"
                elif end != -1:
                    hidden += end
                    self.buffer = self.buffer[end + len(self.CLOSE):]
                    self.mode, self.lead = "answer", True
                else:
                    break
            elif self.mode == "think":
                end = self.buffer.find(self.CLOSE)
                if end == -1:
                    keep = _partial_suffix(self.buffer, self.CLOSE)
                    hidden += len(self.buffer) - keep
                    self.buffer = self.buffer[len(self.buffer) - keep:] if keep else ""
                    break
                hidden += end
                self.buffer = self.buffer[end + len(self.CLOSE):]
                self.mode, self.lead = "answer", True
            else:
                start = self.buffer.find(self.OPEN)
                if start == -1:
                    keep = _partial_suffix(self.buffer, self.OPEN)
                    visible.append(self.buffer[:len(self.buffer) - keep])
                    self.buffer = self.buffer[len(self.buffer) - keep:] if keep else ""
                    break
                visible.append(self.buffer[:start])
                self.buffer = self.buffer[start + len(self.OPEN):]
                self.mode = "think"
        if final and self.buffer:
            if self.mode == "think":
                hidden += len(self.buffer)
            else:
                visible.append(self.buffer)
            self.buffer = ""
        shown = "".join(visible)
        if self.lead and shown:
            shown = shown.lstrip()
            self.lead = not shown
        return shown, hidden


class LocalEngine:
    def __init__(self, endpoint_port=None):
        self.state = {"state": "starting", "detail": "Starting Foundry Local", "model": None,
                      "selected_model": DEFAULT_MODEL, "models": [], "error": None,
                      "media": {"runtime": None, "models": []}}
        # Re-entrant: a demo may load a model and then stream from it on the same request thread.
        self.lock = threading.RLock()
        self.model = None
        self.guests = {}
        self.media = {}
        self.manager = None
        self.allow_download = False
        self.app_data = None
        self.model_cache = None
        # Loopback port for Foundry Local's OpenAI-compatible web service; it stays off until a builder starts it.
        self.endpoint_port = endpoint_port
        self.endpoint_note = None
        self.large = llama_engine.LargeModelHost(Path(__file__).resolve().parent / ".runtime")
        # What each slot runs on this PC. Until start() resolves them against Foundry Local's live catalog these are the reference models
        # the demos were tuned on, so rehearsal-only mode and unit tests see the names they always did.
        self.models = _reference_models()
        self.media_models = _reference_media()
        self.blockers = {}
        self.slots = {}
        self.provisional = False
        self.report = None
        self.registration = None
        self.source = None
        self.lanes = None
        self.failed = set()
        self.retry_chat = False

    def start(self, allow_download=False, model_ref=DEFAULT_MODEL):
        try:
            self.app_data, self.model_cache = storage_dirs()
            self.allow_download = allow_download
            self.lanes = forced_lanes()
            # Decisions use the real hardware, never a simulated device: a simulation changes what the studio shows, not what runs.
            self.report = device_context.service(self.model_cache).detected
            cuda = target_plan.has_cuda_lane(self.report, self.lanes)
            if cuda:
                # The cuDNN supplement must be loaded before Foundry Local loads the CUDA provider.
                try:
                    if allow_download:
                        self.state.update(detail="Preparing GPU media libraries (speech, vision, embeddings)")
                    runtime = gpu_runtime.ensure(self.app_data, allow_download)
                except Exception as exc:
                    logging.exception("GPU media runtime preparation failed")
                    runtime = {"state": "error", "version": gpu_runtime.TAG, "path": None,
                               "detail": f"GPU media libraries unavailable: {exc}"}
            else:
                runtime = {"state": "not_needed", "version": gpu_runtime.TAG, "path": None,
                           "detail": "The cuDNN supplement is for NVIDIA GPUs only; this PC's models run on another lane."}
            self.state["media"] = {"runtime": runtime, "models": []}
            self._open_manager()
            choices = self._resolve()
            if cuda:
                try:
                    choices += self.large.catalog()
                except Exception:
                    logging.exception("Larger-model discovery failed; Foundry Local models remain available")
            self.state.update(models=choices)
            self._refresh_media_state()
            self.switch(model_ref)
            if self.retry_chat and self.state["state"] == "error":
                # The first build failed to load and another one was chosen for the slot: try that one once, without a click.
                self.retry_chat = False
                again = next((alias for alias, spec in self.models.items() if spec["slot"] == target_plan.CHAT), None)
                if again is not None:
                    self.switch(again)
        except Exception as exc:
            logging.exception("Local model preparation failed")
            self.state.update(state="error", detail="Local inference unavailable", error=str(exc))

    def _open_manager(self):
        """Initialize Foundry Local and register the execution providers this PC's lanes need; also used to reopen it after a larger model."""
        from foundry_local_sdk import Configuration, FoundryLocalManager
        logs = Path(__file__).resolve().parent / ".runtime" / "foundry-logs"
        logs.mkdir(parents=True, exist_ok=True)
        if FoundryLocalManager.instance is None:
            # Configuring the web service URL does not start it; start_endpoint() does, on request.
            web = (Configuration.WebService(urls=f"http://127.0.0.1:{self.endpoint_port}")
                   if self.endpoint_port else None)
            FoundryLocalManager.initialize(Configuration(
                app_name=APP_ID,
                app_data_dir=str(self.app_data),
                model_cache_dir=str(self.model_cache),
                logs_dir=str(logs),
                disable_nonessential_telemetry=True,
                web=web,
            ))
        self.manager = manager = FoundryLocalManager.instance
        self.registration = self._register(manager)
        # Register before the catalog is read: it lists only the models the registered providers can run.
        self.source = foundry.SdkModelSource(manager.catalog)
        return manager

    def _register(self, manager):
        def installed(token):
            if token == "cuda":
                # The CUDA bundle counts as prepared once it is marked active, as before.
                return (self.app_data / "ep" / "cuda-ep" / "active").is_file()
            return foundry.ep_cache_has(self.app_data, token)

        result = foundry.register_eps(manager, self.report, allow_download=self.allow_download, installed=installed,
                                      progress=lambda text: self.state.update(detail=text), log=logging.info, lanes=self.lanes)
        problem = result.lane_problems.get("gpu")
        if problem and target_plan.has_cuda_lane(self.report, self.lanes):
            # An NVIDIA PC never falls back to the CPU quietly: the demos are tuned for its GPU.
            raise RuntimeError("GPU provider is not prepared. Run Start-Showcase.ps1 -Prepare to acquire it."
                               if "not installed" in problem else f"GPU provider registration failed: {problem}")
        return result

    def _chat_spec(self, slot, model):
        reference = MODELS.get(model.alias)
        lane = self.report.profile.lane(model.lane)
        return {"id": model.variant_id, "label": reference["label"] if reference else model.title,
                "reasoning": reference["reasoning"] if reference else model.thinks,
                "params": reference["params"] if reference else f"{model.params_b:g}B",
                "slot": slot.key, "lane": model.lane, "device": model.device, "ep": model.ep, "provider": model.ep_name,
                "where": lane_phrase(model.lane, lane.device if lane else ""),
                "channel": "harmony" if model.alias == "gpt-oss-20b" else "think" if model.thinks else None}

    def _media_spec(self, slot, model):
        reference = MEDIA_MODELS[slot.key]
        lane = self.report.profile.lane(model.lane)
        return {"id": model.variant_id, "label": reference["label"] if model.alias == slot.reference else model.title,
                "task": slot.task, "slot": slot.key, "lane": model.lane, "device": model.device, "ep": model.ep,
                "provider": model.ep_name, "where": lane_phrase(model.lane, lane.device if lane else ""), "blocker": None}

    @staticmethod
    def _better(choice):
        better = choice.better
        return None if better is None else {"alias": better.alias, "label": better.title, "lane": better.lane,
                                            "device": better.device, "size_mb": round(better.file_gb * 1024),
                                            "summary": better.summary}

    def _resolve(self):
        """Choose each slot's model for this PC's real hardware from Foundry Local's catalog. Returns the chat-model rows."""
        return self._apply(target_plan.choose(self.report, device_context.service().catalog, self.source,
                                              usable=self.registration.usable, failed=self.failed,
                                              lane_problems=self.registration.lane_problems,
                                              allow_download=self.allow_download, lanes=self.lanes))

    def preview(self, report=None, *, forced=True):
        """Name each slot's model from a device report and the kit's size estimates alone, before Foundry Local has answered. The demos'
        wording, the device page and a simulated device all read from this; start() replaces it with the catalog's real answer, so nothing
        previewed is ever loaded. forced=False ignores the lane-forcing diagnostic (a simulated device is not this PC's engine).
        Returns self."""
        if self.slots and not self.provisional:
            return self
        try:
            self.lanes = forced_lanes() if forced else None
            self.report = report or device_context.service(storage_dirs()[1]).detected
            self._apply(target_plan.estimate(self.report, device_context.service().catalog, lanes=self.lanes))
        except Exception:
            # The reference wording stays; start() reports the real problem when detection really cannot run.
            logging.exception("Device preview unavailable")
        return self

    def _apply(self, choices):
        """Turn slot choices into the specs the engine runs and the rows the apps list. Returns the chat-model rows."""
        previous, previous_media = self.models, self.media_models
        self.slots = choices
        self.provisional = any(choice.estimated for choice in choices.values())
        self.models, self.media_models, self.blockers, rows = {}, {}, {}, []
        for slot in target_plan.SLOTS:
            choice = choices[slot.key]
            model = choice.model
            note = " ".join(choice.notes) or None
            if slot.kind == "media":
                reference = MEDIA_MODELS[slot.key]
                spec = (self._media_spec(slot, model) if model else
                        {**reference, "slot": slot.key, "lane": None, "device": None, "ep": None, "provider": None,
                         "where": None, "blocker": choice.blocker})
                self.media_models[slot.key] = spec
                continue
            if model is None:
                reference = MODELS[slot.reference]
                self.blockers[slot.key] = choice.blocker
                rows.append({"alias": slot.reference, **reference, "cached": False, "size_mb": None, "provider": None,
                             "runtime": "Foundry Local", "available": False, "blocker": choice.blocker, "exclusive": False,
                             "lane": None, "device": None, "ep": None, "slot": slot.key, "where": None, "note": note,
                             "better": None})
                continue
            spec = self.models[model.alias] = self._chat_spec(slot, model)
            rows.append({"alias": model.alias, "id": spec["id"], "label": spec["label"], "reasoning": spec["reasoning"],
                         "params": spec["params"], "cached": model.cached, "size_mb": round(model.file_gb * 1024),
                         "provider": spec["provider"], "runtime": "Foundry Local", "available": True, "blocker": None,
                         "exclusive": False, "lane": model.lane, "device": model.device, "ep": model.ep_label,
                         "slot": slot.key, "where": spec["where"], "note": note, "better": self._better(choice)})
        # A model that is already loaded keeps the spec it was loaded with, whatever a later re-choice says.
        for alias in (self._chat_alias(), *self.guests):
            if alias in previous and alias not in self.models:
                self.models[alias] = previous[alias]
        for role in self.media:
            self.media_models[role] = previous_media[role]
        return rows

    def _note_failure(self, spec):
        """A build that failed to load is not offered again in this run. Returns the sentence that says what will be used instead."""
        if self.source is None or self.report is None:
            return ""
        self.failed.add(spec["id"])
        large = [row for row in self.state.get("models") or [] if row.get("runtime") == "llama.cpp"]
        self.state["models"] = self._resolve() + large
        self._refresh_media_state()
        replacement = next((s for s in (*self.models.values(), *self.media_models.values())
                            if s["slot"] == spec["slot"] and s["id"] != spec["id"] and not s.get("blocker")), None)
        if replacement is None:
            return f"No other build of {spec['label']} fits this PC."
        self.retry_chat = self.retry_chat or spec["slot"] == target_plan.CHAT
        return f"{replacement['label']} on {replacement['where']} is chosen instead."

    def _load(self, model, spec):
        try:
            model.load()
        except Exception as exc:
            first = (str(exc).splitlines() or [type(exc).__name__])[0][:220]
            raise RuntimeError(f"{spec['label']} did not load on {spec['where']}: {first}. "
                               f"{self._note_failure(spec)}".rstrip()) from exc

    # ------------------------------------------------------------ what each name means on this PC
    def alias_for(self, ref):
        """The model this PC runs for ref: a real alias it chose, a reference name the demos are tuned on (mapped to the model for that
        slot), or a media role. None when ref is unknown or its slot has no model that fits."""
        if ref in self.models:
            return ref
        slot = target_plan.BY_REFERENCE.get(ref)
        if slot is not None:
            return next((alias for alias, spec in self.models.items() if spec["slot"] == slot.key), None)
        if ref in self.media_models:
            return None if self.media_models[ref].get("blocker") else ref
        return None

    def blocker_for(self, ref):
        slot = target_plan.BY_REFERENCE.get(ref) or target_plan.BY_KEY.get(ref)
        if ref in self.models:
            return None
        if slot is None:
            return None
        return (self.media_models[slot.key].get("blocker") if slot.kind == "media" else self.blockers.get(slot.key))

    def require_alias(self, ref):
        alias = self.alias_for(ref)
        if alias is not None:
            return alias
        blocker = self.blocker_for(ref)
        if blocker:
            raise RuntimeError(blocker)
        raise ValueError("Unsupported chat model.")

    def _spec(self, ref):
        """The spec for a chat alias or media role, the larger models included; None when unknown."""
        alias = self.alias_for(ref)
        if alias in self.models:
            return self.models[alias]
        if ref in self.media_models:
            return self.media_models[ref]
        return LARGE_MODELS.get(ref)

    def label(self, ref):
        spec = self._spec(ref)
        if spec is not None:
            return spec["label"]
        return (MODELS.get(ref) or {}).get("label", ref)

    def reasoning(self, ref):
        spec = self._spec(ref)
        return bool(spec and spec.get("reasoning"))

    def _gpu_where(self):
        """The words for the PC's GPU lane: the larger llama.cpp models run there on an NVIDIA PC."""
        lane = self.report.profile.lane("gpu") if self.report is not None else None
        return lane_phrase("gpu", lane.device) if lane else RTX_WHERE

    def where(self, ref=None):
        """Where a model runs on this PC, in words: 'the RTX GPU', 'the GPU', 'the NPU' or 'the CPU'."""
        ref = ref or self.state.get("selected_model")
        if ref in LARGE_MODELS:
            return self._gpu_where()
        spec = self._spec(ref)
        return (spec or {}).get("where") or "this PC"

    def lane(self, ref=None):
        """The lane ('npu', 'gpu' or 'cpu') a model runs on, or None when nothing on this PC runs it."""
        ref = ref or self.state.get("selected_model")
        if ref in LARGE_MODELS:
            return "gpu"
        spec = self._spec(ref)
        return (spec or {}).get("lane")

    def describe(self, ref=None):
        """What this PC runs for ref (None is the selected chat model): its label, where it runs and whether it can run at all."""
        ref = ref or self.state.get("selected_model")
        return {"label": self.label(ref), "where": self.where(ref), "lane": self.lane(ref),
                "available": ref in LARGE_MODELS or self.alias_for(ref) is not None}

    def expected_models(self):
        """Every model the engine could have resident on this PC, as {id, label, lane, where}."""
        specs = [*self.models.values(), *(s for s in self.media_models.values() if not s.get("blocker"))]
        return [{"id": spec["id"], "label": spec["label"], "lane": spec.get("lane"), "where": spec.get("where")}
                for spec in specs]

    def unavailable_models(self):
        """The slots this PC has no model for, as {slot, title, blocker}: nothing can be resident for them."""
        rows = []
        for slot in target_plan.SLOTS:
            blocker = (self.media_models.get(slot.key, {}).get("blocker") if slot.kind == "media"
                       else self.blockers.get(slot.key))
            if blocker:
                rows.append({"slot": slot.key, "title": slot.title, "blocker": blocker})
        return rows

    def target_info(self):
        """What the engine chose for this PC, for the device endpoint. The registration is the real providers' state."""
        registration = self.registration
        slots = []
        for slot in target_plan.SLOTS:
            choice = self.slots.get(slot.key)
            slots.append({"slot": slot.key, "title": slot.title, "reference": slot.reference,
                          "model": choice.model.describe() if choice and choice.model else None,
                          "better": self._better(choice) if choice else None,
                          "blocker": choice.blocker if choice else None,
                          "notes": list(choice.notes) if choice else []})
        return {"resolved": bool(self.slots) and not self.provisional, "lanes": sorted(self.lanes) if self.lanes else None,
                "slots": slots,
                "providers": [{"token": p.token, "name": p.name, "registered": p.registered, "usable": p.usable}
                              for p in (registration.providers if registration else [])],
                "usable_eps": sorted(registration.usable) if registration else [],
                "notes": list(registration.notes) if registration else [],
                "lane_problems": dict(registration.lane_problems) if registration else {},
                "failed": sorted(self.failed)}

    def _foundry(self):
        """The Foundry Local manager, reopened if a larger model closed it."""
        if self.manager is None:
            if self.app_data is None:
                raise RuntimeError("Foundry Local is not initialized")
            detail = self.state.get("detail")
            logging.info("Reopening Foundry Local")
            self._open_manager()
            if self.state.get("state") == "ready":
                self.state["detail"] = detail
        return self.manager

    def _close_foundry(self):
        """Unload every Foundry Local model and close the manager.

        Unloading alone leaves about 7 GiB of CUDA memory held by this process (measured with Qwen2.5-Coder 14B);
        closing the manager returns it, which the largest models need.
        """
        for other in (self.model, *self.guests.values(), *self.media.values()):
            if other is not None:
                other.unload()
        self.model = None
        self.guests.clear()
        self.media.clear()
        if self.manager is not None:
            if self._endpoint_urls():
                self.endpoint_note = ("The Foundry Local endpoint stopped when Foundry Local closed to give a larger "
                                      "model the GPU. Use the llama.cpp endpoint, or select a Foundry Local model "
                                      "and start it again.")
            self.manager.close()
            self.manager = None
        self._refresh_media_state()

    def _variant(self, spec):
        model = self.manager.catalog.get_model_variant(spec["id"])
        if model is None or foundry.norm_ep(model.info.runtime.execution_provider) != spec["ep"]:
            raise RuntimeError(f"Expected {foundry.ep_label(spec['ep'])} variant {spec['id']}; refusing silent fallback.")
        return model

    def _acquire(self, model, label, on_progress=None):
        if not model.is_cached:
            if not self.allow_download:
                raise RuntimeError(f"{label} is not cached. Run Start-Showcase.ps1 -Prepare once to download it.")
            if on_progress:
                on_progress(f"Downloading {label}")
            model.download(progress_callback=lambda p: on_progress and on_progress(f"Downloading {label}: {p:.0f}%"))

    def _chat_alias(self):
        return (self.state.get("model") or {}).get("alias")

    def switch(self, model_ref):
        if model_ref in LARGE_MODELS:
            return self._switch_large(model_ref)
        alias = self.alias_for(model_ref)
        if alias is None:
            raise ValueError(self.blocker_for(model_ref) or "Choose a supported local model on this PC.")
        try:
            spec = self.models[alias]
            with self.lock:
                self.state.update(state="loading", selected_model=alias, error=None,
                                  detail=f"Preparing {spec['label']} on {spec['where']}")
                if self.large.loaded():
                    self.state.update(detail=f"Stopping {self.large.info['name']}; one chat model stays selected")
                    self.large.stop()
                    self.state["model"] = None
                self._foundry()
                keep = self.guests.pop(alias, None)
                if keep is None and self.model is not None and self._chat_alias() == alias:
                    keep = self.model
                release = [m for m in (self.model, *self.guests.values()) if m is not None and m is not keep]
                if release:
                    self.state.update(detail="Releasing other chat models; one chat model stays selected")
                    for other in release:
                        other.unload()
                self.model = None
                self.guests.clear()
                self.state["model"] = None
                if keep is not None:
                    model = keep
                else:
                    model = self._variant(spec)
                    self._acquire(model, spec["label"], lambda d: self.state.update(state="downloading", detail=d))
                    self.state.update(state="loading", detail=self._loading_detail(spec))
                    self._load(model, spec)
                self.model = model
                self.state.update(model=self._chat_info(alias, model))
                self.state["models"] = [{**choice, "cached": True} if choice["alias"] == alias else choice
                                        for choice in self.state["models"]]
            self.state.update(state="warming", detail=f"Warming {spec['label']}")
            self._warm_chat(alias)
            self.state.update(state="ready", detail=f"{spec['label']} ready for live local inference")
        except Exception as exc:
            logging.exception("Model switch failed")
            self.state.update(state="error", detail="Selected model unavailable", error=str(exc))

    @staticmethod
    def _loading_detail(spec):
        text = f"Loading {spec['label']} on {foundry.ep_label(spec['ep'])}" if spec["ep"] == "cuda" else \
            f"Loading {spec['label']} on {spec['where']} ({foundry.ep_label(spec['ep'])})"
        # The first load of a model on an NPU compiles it for that chip; say so instead of looking stuck.
        return text + ". The first load on the NPU can take a while" if spec["lane"] == "npu" else text

    def precheck_large(self, alias):
        """Raise RuntimeError, before anything is unloaded, when a larger model cannot fit. Counts the GPU memory
        this app would release: its own llama-server, plus Foundry Local for models that need the GPU to itself."""
        pids = [self.large.owned_pid()]
        if LARGE_MODELS[alias]["exclusive"]:
            pids.append(os.getpid())
        releasable = 0
        for pid in filter(None, pids):
            split = llama_engine.gpu_process_memory(pid)
            releasable += split[0] if split else 0
        self.large.precheck(alias, releasable)

    def _switch_large(self, alias):
        spec = LARGE_MODELS[alias]
        try:
            with self.lock:
                row = next((m for m in self.state.get("models") or [] if m.get("alias") == alias), None)
                if row is None or not row.get("available"):
                    raise RuntimeError((row or {}).get("blocker") or f"{spec['label']} is not available on this PC.")
                self.state.update(state="loading", selected_model=alias, error=None,
                                  detail=f"Preparing {spec['label']} on {self._gpu_where()}")
                if not self.large.loaded(alias):
                    if spec["exclusive"]:
                        self.state.update(detail="Closing Foundry Local to give the GPU to this model; one chat model stays selected")
                        self._close_foundry()
                    else:
                        release = [m for m in (self.model, *self.guests.values()) if m is not None]
                        if release:
                            self.state.update(detail="Releasing Foundry Local chat models; one chat model stays selected")
                            for other in release:
                                other.unload()
                        self.model = None
                        self.guests.clear()
                    self.state["model"] = None
                    self.large.load(alias, lambda detail: self.state.update(state="loading", detail=detail))
                self.state.update(model=dict(self.large.info))
            self.state.update(state="warming", detail=f"Warming {spec['label']}")
            self._warm_chat(alias)
            info = self.large.info
            self.state.update(state="ready", detail=f"{spec['label']} ready for live local inference "
                                                    f"({info['offload']}, {llama_engine.PROVIDER}"
                                                    + (f"; {info['gpu_memory']}" if info.get("gpu_memory") else "") + ")")
        except Exception as exc:
            logging.exception("Larger model switch failed")
            self.state.update(state="error", detail="Selected model unavailable", error=str(exc))

    def _chat_info(self, alias, model):
        spec = self.models[alias]
        return {"name": spec["label"], "alias": alias, "id": model.id, "provider": spec["provider"],
                "reasoning": spec["reasoning"], "license": model.info.license,
                "lane": spec["lane"], "device": spec["device"], "ep": foundry.ep_label(spec["ep"]), "where": spec["where"]}

    def _channel(self, ref):
        """How the model marks its reasoning: 'harmony' (GPT-OSS), 'think' (<think> tags) or None. The larger llama.cpp models are all Harmony."""
        spec = self._spec(ref) or {}
        return spec["channel"] if "channel" in spec else ("harmony" if spec.get("reasoning") else None)

    def _warm_chat(self, alias):
        channel = self._channel(alias)
        # A reasoning model needs room to think before its first visible word; the others only need to answer.
        budget = 1024 if channel == "harmony" else 160 if channel == "think" else 16
        try:
            for _ in self.stream("Reply with READY only.", "Keep the response brief.", max_tokens=budget, alias=alias):
                pass
        except RuntimeError as exc:
            # A warm-up only has to exercise the model's lane; an empty or truncated reply is not a failure.
            if "no visible answer" not in str(exc):
                raise

    def generation_budget(self, scenario):
        reasoning = self.reasoning(self.state["selected_model"])
        return (4096 if scenario == "code" else 3072) if reasoning else (1600 if scenario == "code" else 650)

    def is_resident(self, ref):
        alias = self.alias_for(ref) or ref
        return (alias in self.guests or (self.model is not None and self._chat_alias() == alias)
                or alias in self.media or self.large.loaded(alias))

    def _exclusive_blocker(self):
        """Message when the selected larger model needs the GPU to itself, else None."""
        if self.large.loaded() and LARGE_MODELS[self.large.alias]["exclusive"]:
            return (f"{self.large.info['name']} is using most of the GPU memory, so this demo's Foundry Local models "
                    "cannot load beside it. Select Qwen2.5-Coder 14B, GPT-OSS 20B or Qwen3-Coder 30B in the "
                    "Live GPU workspace, then run the demo again.")
        return None

    def ensure_chat(self, ref):
        """Keep another supported chat model resident beside the selected one; returns load timings or None."""
        alias = self.require_alias(ref)
        if alias not in self.models:
            raise ValueError("Unsupported chat model.")
        with self.lock:
            if self.is_resident(alias):
                return None
            if blocker := self._exclusive_blocker():
                raise RuntimeError(blocker)
            self._foundry()
            spec = self.models[alias]
            started = time.perf_counter()
            model = self._variant(spec)
            self._acquire(model, spec["label"])
            self._load(model, spec)
            self.guests[alias] = model
            loaded = time.perf_counter()
            self._warm_chat(alias)
            return {"load_ms": round((loaded - started) * 1000), "warm_ms": round((time.perf_counter() - loaded) * 1000)}

    def chat_info(self, alias=None):
        with self.lock:
            if self.large.loaded() and alias in (None, self.large.alias):
                return dict(self.large.info)
            return self._chat(alias)[2]

    def _chat(self, ref=None):
        alias = None if ref is None else self.alias_for(ref) or ref
        if alias is None or (self.model is not None and alias == self._chat_alias()):
            if self.model is None:
                raise RuntimeError("No model loaded")
            return self.model, self.models[self._chat_alias()], self.state["model"]
        if alias not in self.guests:
            raise RuntimeError(f"{self.label(alias)} is not loaded")
        model = self.guests[alias]
        return model, self.models[alias], self._chat_info(alias, model)

    def _refresh_media_state(self):
        if self.manager is None and self.state.get("media", {}).get("models"):
            # Foundry Local is closed while a larger model holds the GPU; catalog facts are unchanged.
            self.state["media"]["models"] = [{**row, "loaded": False} for row in self.state["media"]["models"]]
            return
        rows = []
        for role, spec in self.media_models.items():
            choice = self.slots.get(role)
            row = {"role": role, "label": spec["label"], "id": spec["id"], "provider": spec["provider"],
                   "available": False, "cached": False, "loaded": role in self.media, "size_mb": None,
                   "lane": spec["lane"], "device": spec["device"], "ep": foundry.ep_label(spec["ep"]) if spec["ep"] else None,
                   "where": spec["where"], "blocker": spec.get("blocker"), "better": self._better(choice) if choice else None}
            if not row["blocker"]:
                try:
                    variant = self.manager.catalog.get_model_variant(spec["id"]) if self.manager else None
                    if variant is not None and foundry.norm_ep(variant.info.runtime.execution_provider) == spec["ep"]:
                        row.update(available=True, cached=bool(variant.is_cached), size_mb=variant.info.file_size_mb)
                except Exception:
                    logging.exception("Could not read media model %s", spec["id"])
            rows.append(row)
        self.state["media"] = {**self.state.get("media", {}), "models": rows}

    def media_info(self, role):
        spec = self.media_models[role]
        model = self.media.get(role)
        return {"name": spec["label"], "alias": role, "id": model.id if model else spec["id"],
                "provider": spec["provider"], "reasoning": False, "license": model.info.license if model else None,
                "lane": spec["lane"], "device": spec["device"], "ep": foundry.ep_label(spec["ep"]) if spec["ep"] else None,
                "where": spec["where"]}

    def ensure_media(self, role, warm_audio=None, warm_images=()):
        """Load a speech, vision or embedding model on the lane this PC runs it on and warm it; returns timings or None."""
        spec = self.media_models.get(role)
        if spec is None:
            raise ValueError("Unsupported media model.")
        with self.lock:
            if role in self.media:
                return None
            if spec.get("blocker"):
                raise RuntimeError(spec["blocker"])
            if blocker := self._exclusive_blocker():
                raise RuntimeError(blocker)
            self._foundry()
            if spec["ep"] == "cuda":
                runtime = self.state.get("media", {}).get("runtime") or {}
                if runtime.get("state") not in ("active", "not_needed"):
                    raise RuntimeError(runtime.get("detail") or "GPU media libraries are unavailable.")
            started = time.perf_counter()
            model = self._variant(spec)
            if model.info.task != spec["task"]:
                raise RuntimeError(f"{spec['label']} reports task {model.info.task!r}, expected {spec['task']!r}.")
            self._acquire(model, spec["label"])
            self._load(model, spec)
            loaded = time.perf_counter()
            try:
                self._warm_media(role, model, warm_audio, warm_images)
            except Exception:
                model.unload()
                raise
            self.media[role] = model
            self._refresh_media_state()
            return {"load_ms": round((loaded - started) * 1000), "warm_ms": round((time.perf_counter() - loaded) * 1000)}

    def _warm_media(self, role, model, warm_audio, warm_images):
        # The first call on each input shape can trigger kernel autotuning (seconds on CUDA); warm with the demo's own shapes.
        if role == "asr":
            with tempfile.TemporaryDirectory() as folder:
                path = Path(folder) / "warm.wav"
                if warm_audio and Path(warm_audio).is_file():
                    rate, pcm = media.read_wav(warm_audio)
                    media.write_wav(path, rate, pcm[:rate * 3])
                else:
                    media.write_wav(path, 16000, array.array("h", bytes(16000 * 2)))
                self._transcribe_file(model, path)
        elif role == "vision":
            for image in list(warm_images) or [media.solid_png()]:
                for _ in self._generate(model, self.media_info("vision"), None, "Reply briefly.",
                                        self._vision_content(image, "Reply with OK."), 1, allow_empty=True):
                    pass
        elif role == "embed":
            self._embed_with(model, ["Warm-up sentence for the embedding model."])

    @staticmethod
    def _transcribe_file(model, path):
        from foundry_local_sdk import AudioSession, Request, TextItem, TextItemType
        with AudioSession(model) as session, Request() as request:
            request.add_item(TextItem(json.dumps({"model": model.id, "filename": str(path), "language": "en"}),
                                      TextItemType.OPENAI_JSON))
            with session.process_request(request) as response:
                return json.loads(response.get_item(0).text).get("text", "").strip()

    def transcribe(self, wav_path, workdir):
        """Silence-aware chunked transcription; yields one measured result per chunk (Whisper sees 30 s windows)."""
        rate, pcm = media.read_wav(wav_path)
        chunks = media.plan_chunks(pcm, rate)
        workdir = Path(workdir)
        workdir.mkdir(parents=True, exist_ok=True)
        for index, (start, end) in enumerate(chunks):
            path = workdir / f"chunk-{index}.wav"
            media.write_wav(path, rate, pcm[start:end])
            with self.lock:
                model = self.media.get("asr")
                if model is None:
                    raise RuntimeError("Speech model is not loaded")
                started = time.perf_counter()
                text = self._transcribe_file(model, path)
                elapsed = time.perf_counter() - started
            path.unlink(missing_ok=True)
            yield {"index": index, "count": len(chunks), "start_s": start / rate, "end_s": end / rate,
                   "text": text, "ms": round(elapsed * 1000)}

    @staticmethod
    def _vision_content(image, prompt):
        from foundry_local_sdk import ImageItem, TextItem
        return [ImageItem("png", image), TextItem(prompt)]

    def see(self, image, prompt, system, max_tokens=500):
        with self.lock:
            model = self.media.get("vision")
            if model is None:
                raise RuntimeError("Vision model is not loaded")
            yield from self._generate(model, self.media_info("vision"), False, system,
                                      self._vision_content(image, prompt), max_tokens)

    @staticmethod
    def _embed_with(model, texts):
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", DeprecationWarning)
            response = model.get_embedding_client().generate_embeddings(list(texts))
        return [list(item.embedding) for item in response.data]

    def embed(self, texts):
        with self.lock:
            model = self.media.get("embed")
            if model is None:
                raise RuntimeError("Embedding model is not loaded")
            started = time.perf_counter()
            vectors = self._embed_with(model, texts)
            return vectors, round((time.perf_counter() - started) * 1000)

    def resident(self):
        """Ids Foundry Local reports as loaded. Its catalog list covers chat models only (measured on the CPU lane with
        speech and embedding models loaded), so every model handle this engine holds is asked directly as well."""
        with self.lock:
            large = [self.large.info["id"]] if self.large.loaded() else []
            if self.manager is None:
                return large
            ids = {m.id for m in self.manager.catalog.get_loaded_models()}
            for handle in (self.model, *self.guests.values(), *self.media.values()):
                if handle is not None and handle.is_loaded:
                    ids.add(handle.id)
            return sorted(ids | set(large))

    # ------------------------------------------------------------ OpenAI-compatible endpoints for builders
    def _endpoint_urls(self):
        try:
            return list(self.manager.urls or []) if self.manager is not None else []
        except Exception:
            logging.exception("Could not read the Foundry Local web service state")
            return []

    def endpoint_info(self):
        """Loopback OpenAI-compatible endpoints a builder can call. Never takes the engine lock (a run may hold it)."""
        large = None
        if self.large.loaded() and self.large.info:
            info = self.large.info
            large = {"runtime": "llama.cpp", "state": "running", "base_url": f"http://127.0.0.1:{self.large.port}/v1",
                     "model": self.large.alias, "model_name": info.get("name"), "owner": info.get("endpoint"),
                     "detail": "llama-server's OpenAI-compatible API on loopback. It runs while this larger model "
                               "is loaded."}
        urls = self._endpoint_urls()
        model, alias = self.model, self._chat_alias()
        if urls:
            state, base = "running", urls[0].rstrip("/") + "/v1"
        else:
            state = "stopped" if self.manager is not None else "closed"
            base = f"http://127.0.0.1:{self.endpoint_port}/v1" if self.endpoint_port else None
        detail = {"running": "Foundry Local's OpenAI-compatible web service on loopback. It stops when you stop it "
                             "or close the studio.",
                  "stopped": "Off. Start it to call the loaded Foundry Local model from your own code.",
                  "closed": "Foundry Local is closed while a larger model has the GPU to itself." if large else
                            "Foundry Local is not running yet."}[state]
        foundry_ep = {"runtime": "Foundry Local", "state": state, "base_url": base, "port": self.endpoint_port,
                      "model": model.id if model is not None else None,
                      "model_name": self.models[alias]["label"] if model is not None and alias in self.models else None,
                      "detail": self.endpoint_note if state != "running" and self.endpoint_note else detail}
        return {"foundry": foundry_ep, "large": large, "active": "large" if large else "foundry"}

    def start_endpoint(self):
        if not self.endpoint_port:
            raise RuntimeError("No loopback port is configured for the Foundry Local endpoint.")
        if self.manager is None:
            raise RuntimeError("Foundry Local is closed while a larger model has the GPU to itself. Use its "
                               "llama.cpp endpoint, or select a Foundry Local model first.")
        if not self._endpoint_urls():
            try:
                self.manager.start_web_service()
            except Exception as exc:
                raise RuntimeError(f"Foundry Local could not start its web service on 127.0.0.1:{self.endpoint_port} "
                                   f"({exc}). If another app uses that port, set {ENV_PREFIX}ENDPOINT_PORT and "
                                   "restart the studio.") from None
            self.endpoint_note = None
        return self.endpoint_info()

    def stop_endpoint(self):
        if self._endpoint_urls():
            self.manager.stop_web_service()
        self.endpoint_note = None
        return self.endpoint_info()

    def stream(self, prompt, system, max_tokens=650, alias=None):
        with self.lock:
            if self.large.loaded() and alias in (None, self.large.alias):
                reasoning = LARGE_MODELS[self.large.alias]["reasoning"]
                yield from self.large.stream(system, prompt, max_tokens, HarmonyFilter() if reasoning else None)
                return
            model, spec, info = self._chat(alias)
            yield from self._generate(model, info, spec.get("channel"), system, prompt, max_tokens)

    def _generate(self, model, info, channel, system, content, max_tokens, allow_empty=False):
        """Stream one reply. channel says how the model marks its reasoning ('harmony', 'think' or None); a bool is accepted for callers
        that only know whether the model reasons (True means Harmony). Reasoning is counted, never shown."""
        from foundry_local_sdk import (
            ChatSession, MessageItem, Request, RequestOptions, SearchOptions, TextItem, TextItemType,
        )
        channel = "harmony" if channel is True else channel or None
        started = time.perf_counter()
        first = None
        chars = reasoning_chars = 0
        channel_filter = {"harmony": HarmonyFilter, "think": ThinkFilter}.get(channel, lambda: None)()
        # GPT-OSS is sampled at its recommended 1.0; DeepSeek-R1 style <think> models need about 0.6 to avoid loops; the rest stay deterministic.
        temperature, top_p = {"harmony": (1.0, 1.0), "think": (0.6, 0.95)}.get(channel, (0.0, 1.0))
        with ChatSession(model) as session:
            session.set_options(RequestOptions(search=SearchOptions(
                temperature=temperature, top_p=top_p, max_output_tokens=max_tokens)))
            session.set_streaming(True)
            with Request() as request:
                request.add_item(MessageItem.system(
                    "Reasoning: low\nKeep internal planning brief; do not count words step by step.\n" + system
                    if channel == "harmony" else system))
                request.add_item(MessageItem.user(content))
                with session.process_streaming_request(request) as response:
                    for item in response:
                        if not isinstance(item, TextItem) or not item.text:
                            continue
                        if item.type == TextItemType.REASONING:
                            if isinstance(channel_filter, ThinkFilter):
                                channel_filter.reasoning_is_separate()
                            reasoning_chars += len(item.text)
                            yield {"type": "activity", "reasoning_characters": reasoning_chars}
                            continue
                        if item.type != TextItemType.DEFAULT:
                            continue
                        text = item.text
                        if channel_filter is not None:
                            text, hidden_chars = channel_filter.feed(text)
                            if hidden_chars:
                                reasoning_chars += hidden_chars
                                yield {"type": "activity", "reasoning_characters": reasoning_chars}
                            elif getattr(channel_filter, "pending", 0):
                                # Held back until a tag shows whether it is reasoning; show progress now, count it exactly at the end.
                                yield {"type": "activity", "reasoning_characters": reasoning_chars + channel_filter.pending}
                        if not text:
                            continue
                        if first is None:
                            first = time.perf_counter()
                        chars += len(text)
                        yield {"type": "token", "text": text}
                    if channel_filter is not None:
                        text, hidden_chars = channel_filter.feed("", final=True)
                        if hidden_chars:
                            reasoning_chars += hidden_chars
                            yield {"type": "activity", "reasoning_characters": reasoning_chars}
                        if text:
                            first = first or time.perf_counter()
                            chars += len(text)
                            yield {"type": "token", "text": text}
                    with response.final_response as final:
                        usage = final.get_usage()
                        finish = final.finish_reason.name
                        tokens = usage.completion_tokens if usage else None
                        prompt_tokens = getattr(usage, "prompt_tokens", None) if usage else None
        elapsed = time.perf_counter() - started
        if not chars:
            if allow_empty:
                return
            if reasoning_chars and tokens and tokens >= max_tokens - 8:
                raise RuntimeError(f"{info['name']} used its whole {max_tokens:,}-token output budget reasoning and "
                                   "gave no visible answer. Answers vary run to run; run it again.")
            raise RuntimeError("Model returned no visible answer")
        yield {
            "type": "done", "mode": "live", "model": info,
            "ttft_ms": round((first - started) * 1000),
            "total_ms": round(elapsed * 1000), "characters": chars,
            "reasoning_characters": reasoning_chars,
            "output_tokens": tokens,
            "prompt_tokens": prompt_tokens,
            "tokens_per_second": round(tokens / elapsed, 1) if tokens else None,
            "finish_reason": finish,
        }
