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

import gpu_runtime
import llama_engine
import media
from edition import APP_ID, ENV_PREFIX
from llama_engine import LARGE_MODELS

DEFAULT_MODEL = "qwen2.5-coder-14b"
PROVIDER = "CUDAExecutionProvider"
MARKERS = ("<|channel|>", "<|start|>", "<|message|>", "<|end|>", "<|return|>", "<|endoftext|>", "<|fim_suffix|>")
PENDING_MARKER = re.compile(r"<\|[a-z_]{0,24}\|?")
UNKNOWN_MARKER = re.compile(r"<\|[a-z_]{1,24}\|>")
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

    def start(self, allow_download=False, model_ref=DEFAULT_MODEL):
        try:
            shared = Path(os.environ["LOCALAPPDATA"]) / "ASUAgenticShowcase"
            reuse = (shared / "models").is_dir()
            app_data = shared if reuse else Path.home() / f".{APP_ID}"
            self.model_cache = shared / "models" if reuse else app_data / "cache"
            self.app_data = app_data
            self.allow_download = allow_download
            # The cuDNN supplement must be loaded before Foundry Local loads the CUDA provider.
            try:
                if allow_download:
                    self.state.update(detail="Preparing GPU media libraries (speech, vision, embeddings)")
                runtime = gpu_runtime.ensure(app_data, allow_download)
            except Exception as exc:
                logging.exception("GPU media runtime preparation failed")
                runtime = {"state": "error", "version": gpu_runtime.TAG, "path": None,
                           "detail": f"GPU media libraries unavailable: {exc}"}
            self.state["media"] = {"runtime": runtime, "models": []}
            manager = self._open_manager()
            choices = []
            for alias, spec in MODELS.items():
                variant = manager.catalog.get_model_variant(spec["id"])
                if variant is None or variant.info.runtime.execution_provider != PROVIDER:
                    raise RuntimeError(f"Required CUDA variant is absent: {spec['id']}")
                choices.append({"alias": alias, **spec, "cached": variant.is_cached,
                                "size_mb": variant.info.file_size_mb, "provider": PROVIDER,
                                "runtime": "Foundry Local", "available": True, "blocker": None, "exclusive": False})
            try:
                choices += self.large.catalog()
            except Exception:
                logging.exception("Larger-model discovery failed; Foundry Local models remain available")
            self.state.update(models=choices)
            self._refresh_media_state()
            self.switch(model_ref)
        except Exception as exc:
            logging.exception("Local model preparation failed")
            self.state.update(state="error", detail="Local inference unavailable", error=str(exc))

    def _open_manager(self):
        """Initialize Foundry Local and register the CUDA provider; also used to reopen it after a larger model."""
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
        providers = manager.discover_eps()
        if not any(ep.is_registered and ep.name == PROVIDER for ep in providers):
            if not self.allow_download and not (self.app_data / "ep" / "cuda-ep" / "active").is_file():
                raise RuntimeError("GPU provider is not prepared. Run Start-Showcase.ps1 -Prepare to acquire it.")
            self.state.update(detail="Registering the CUDA GPU provider from the SDK cache")
            result = manager.download_and_register_eps(names=[PROVIDER])
            if not result.success:
                raise RuntimeError(f"GPU provider registration failed: {result.status}")
            if not any(ep.name == PROVIDER and ep.is_registered for ep in manager.discover_eps()):
                raise RuntimeError("CUDA provider did not register; refusing silent fallback.")
        return manager

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

    def _variant(self, variant_id):
        model = self.manager.catalog.get_model_variant(variant_id)
        if model is None or model.info.runtime.execution_provider != PROVIDER:
            raise RuntimeError(f"Expected CUDA variant {variant_id}; refusing silent fallback.")
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
        if model_ref not in MODELS:
            raise ValueError("Choose a supported GPU model.")
        try:
            spec = MODELS[model_ref]
            with self.lock:
                self.state.update(state="loading", selected_model=model_ref, error=None,
                                  detail=f"Preparing {spec['label']} on the RTX GPU")
                if self.large.loaded():
                    self.state.update(detail=f"Stopping {self.large.info['name']}; one chat model stays selected")
                    self.large.stop()
                    self.state["model"] = None
                self._foundry()
                keep = self.guests.pop(model_ref, None)
                if keep is None and self.model is not None and self._chat_alias() == model_ref:
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
                    model = self._variant(spec["id"])
                    self._acquire(model, spec["label"], lambda d: self.state.update(state="downloading", detail=d))
                    self.state.update(state="loading", detail=f"Loading {spec['label']} on CUDA")
                    model.load()
                self.model = model
                self.state.update(model=self._chat_info(model_ref, model))
                self.state["models"] = [{**choice, "cached": True} if choice["alias"] == model_ref else choice
                                        for choice in self.state["models"]]
            self.state.update(state="warming", detail=f"Warming {spec['label']}")
            self._warm_chat(model_ref)
            self.state.update(state="ready", detail=f"{spec['label']} ready for live local inference")
        except Exception as exc:
            logging.exception("Model switch failed")
            self.state.update(state="error", detail="Selected model unavailable", error=str(exc))

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
                                  detail=f"Preparing {spec['label']} on the RTX GPU")
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

    @staticmethod
    def _chat_info(alias, model):
        spec = MODELS[alias]
        return {"name": spec["label"], "alias": alias, "id": model.id, "provider": PROVIDER,
                "reasoning": spec["reasoning"], "license": model.info.license}

    def _warm_chat(self, alias):
        try:
            for _ in self.stream("Reply with READY only.", "Keep the response brief.",
                                 max_tokens=1024 if model_spec(alias)["reasoning"] else 16, alias=alias):
                pass
        except RuntimeError as exc:
            # A warm-up only has to exercise the GPU path; an empty or truncated reply is not a failure.
            if "no visible answer" not in str(exc) and "GPT-OSS" not in str(exc):
                raise

    def generation_budget(self, scenario):
        reasoning = model_spec(self.state["selected_model"])["reasoning"]
        return (4096 if scenario == "code" else 3072) if reasoning else (1600 if scenario == "code" else 650)

    def is_resident(self, alias):
        return (alias in self.guests or (self.model is not None and self._chat_alias() == alias)
                or alias in self.media or self.large.loaded(alias))

    def _exclusive_blocker(self):
        """Message when the selected larger model needs the GPU to itself, else None."""
        if self.large.loaded() and LARGE_MODELS[self.large.alias]["exclusive"]:
            return (f"{self.large.info['name']} is using most of the GPU memory, so this demo's Foundry Local models "
                    "cannot load beside it. Select Qwen2.5-Coder 14B, GPT-OSS 20B or Qwen3-Coder 30B in the "
                    "Live GPU workspace, then run the demo again.")
        return None

    def ensure_chat(self, alias):
        """Keep another supported chat model resident beside the selected one; returns load timings or None."""
        if alias not in MODELS:
            raise ValueError("Unsupported chat model.")
        with self.lock:
            if self.is_resident(alias):
                return None
            if blocker := self._exclusive_blocker():
                raise RuntimeError(blocker)
            self._foundry()
            spec = MODELS[alias]
            started = time.perf_counter()
            model = self._variant(spec["id"])
            self._acquire(model, spec["label"])
            model.load()
            self.guests[alias] = model
            loaded = time.perf_counter()
            self._warm_chat(alias)
            return {"load_ms": round((loaded - started) * 1000), "warm_ms": round((time.perf_counter() - loaded) * 1000)}

    def chat_info(self, alias=None):
        with self.lock:
            if self.large.loaded() and alias in (None, self.large.alias):
                return dict(self.large.info)
            return self._chat(alias)[2]

    def _chat(self, alias=None):
        if alias is None or (self.model is not None and alias == self._chat_alias()):
            if self.model is None:
                raise RuntimeError("No model loaded")
            return self.model, MODELS[self._chat_alias()], self.state["model"]
        if alias not in self.guests:
            raise RuntimeError(f"{MODELS[alias]['label']} is not loaded")
        model = self.guests[alias]
        return model, MODELS[alias], self._chat_info(alias, model)

    def _refresh_media_state(self):
        if self.manager is None and self.state.get("media", {}).get("models"):
            # Foundry Local is closed while a larger model holds the GPU; catalog facts are unchanged.
            self.state["media"]["models"] = [{**row, "loaded": False} for row in self.state["media"]["models"]]
            return
        rows = []
        for role, spec in MEDIA_MODELS.items():
            row = {"role": role, "label": spec["label"], "id": spec["id"], "provider": PROVIDER,
                   "available": False, "cached": False, "loaded": role in self.media, "size_mb": None}
            try:
                variant = self.manager.catalog.get_model_variant(spec["id"]) if self.manager else None
                if variant is not None and variant.info.runtime.execution_provider == PROVIDER:
                    row.update(available=True, cached=bool(variant.is_cached), size_mb=variant.info.file_size_mb)
            except Exception:
                logging.exception("Could not read media model %s", spec["id"])
            rows.append(row)
        self.state["media"] = {**self.state.get("media", {}), "models": rows}

    def media_info(self, role):
        spec = MEDIA_MODELS[role]
        model = self.media.get(role)
        return {"name": spec["label"], "alias": role, "id": model.id if model else spec["id"],
                "provider": PROVIDER, "reasoning": False, "license": model.info.license if model else None}

    def ensure_media(self, role, warm_audio=None, warm_images=()):
        """Load a speech, vision or embedding model on CUDA and warm its kernels; returns timings or None."""
        spec = MEDIA_MODELS[role]
        with self.lock:
            if role in self.media:
                return None
            if blocker := self._exclusive_blocker():
                raise RuntimeError(blocker)
            self._foundry()
            runtime = self.state.get("media", {}).get("runtime") or {}
            if runtime.get("state") not in ("active", "not_needed"):
                raise RuntimeError(runtime.get("detail") or "GPU media libraries are unavailable.")
            started = time.perf_counter()
            model = self._variant(spec["id"])
            if model.info.task != spec["task"]:
                raise RuntimeError(f"{spec['label']} reports task {model.info.task!r}, expected {spec['task']!r}.")
            self._acquire(model, spec["label"])
            model.load()
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
        # The first call on each input shape triggers cuDNN autotuning (seconds); warm with the demo's own shapes.
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
                for _ in self._generate(model, self.media_info("vision"), False, "Reply briefly.",
                                        self._vision_content(image, "Reply with OK."), 1, allow_empty=True):
                    pass
        elif role == "embed":
            self._embed_with(model, ["Warm-up sentence for the GPU embedding model."])

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
        with self.lock:
            large = [self.large.info["id"]] if self.large.loaded() else []
            if self.manager is None:
                return large
            return sorted([m.id for m in self.manager.catalog.get_loaded_models()] + large)

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
        foundry = {"runtime": "Foundry Local", "state": state, "base_url": base, "port": self.endpoint_port,
                   "model": model.id if model is not None else None,
                   "model_name": MODELS[alias]["label"] if model is not None and alias in MODELS else None,
                   "detail": self.endpoint_note if state != "running" and self.endpoint_note else detail}
        return {"foundry": foundry, "large": large, "active": "large" if large else "foundry"}

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
            yield from self._generate(model, info, spec["reasoning"], system, prompt, max_tokens)

    def _generate(self, model, info, reasoning_model, system, content, max_tokens, allow_empty=False):
        from foundry_local_sdk import (
            ChatSession, MessageItem, Request, RequestOptions, SearchOptions, TextItem, TextItemType,
        )
        started = time.perf_counter()
        first = None
        chars = reasoning_chars = 0
        channel_filter = HarmonyFilter() if reasoning_model else None
        with ChatSession(model) as session:
            session.set_options(RequestOptions(search=SearchOptions(
                temperature=1.0 if reasoning_model else 0.0, top_p=1.0, max_output_tokens=max_tokens)))
            session.set_streaming(True)
            with Request() as request:
                request.add_item(MessageItem.system(
                    "Reasoning: low\nKeep internal planning brief; do not count words step by step.\n" + system
                    if reasoning_model else system))
                request.add_item(MessageItem.user(content))
                with session.process_streaming_request(request) as response:
                    for item in response:
                        if not isinstance(item, TextItem) or not item.text:
                            continue
                        if item.type == TextItemType.REASONING:
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
