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
import media
from edition import APP_ID

DEFAULT_MODEL = "qwen2.5-coder-14b"
PROVIDER = "CUDAExecutionProvider"
MARKERS = ("<|channel|>", "<|start|>", "<|message|>", "<|end|>", "<|return|>", "<|endoftext|>", "<|fim_suffix|>")
PENDING_MARKER = re.compile(r"<\|[a-z_]{0,24}\|?")
UNKNOWN_MARKER = re.compile(r"<\|[a-z_]{1,24}\|>")
MODELS = {
    DEFAULT_MODEL: {"id": "qwen2.5-coder-14b-instruct-cuda-gpu:4",
                    "label": "Qwen2.5-Coder 14B", "reasoning": False},
    "gpt-oss-20b": {"id": "gpt-oss-20b-cuda-gpu:1",
                    "label": "GPT-OSS 20B", "reasoning": True},
}
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
    def __init__(self):
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

    def start(self, allow_download=False, model_ref=DEFAULT_MODEL):
        try:
            shared = Path(os.environ["LOCALAPPDATA"]) / "ASUAgenticShowcase"
            reuse = (shared / "models").is_dir()
            app_data = shared if reuse else Path.home() / f".{APP_ID}"
            model_cache = shared / "models" if reuse else app_data / "cache"
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
            from foundry_local_sdk import Configuration, FoundryLocalManager
            logs = Path(__file__).resolve().parent / ".runtime" / "foundry-logs"
            logs.mkdir(parents=True, exist_ok=True)
            if FoundryLocalManager.instance is None:
                FoundryLocalManager.initialize(Configuration(
                    app_name=APP_ID,
                    app_data_dir=str(app_data),
                    model_cache_dir=str(model_cache),
                    logs_dir=str(logs),
                    disable_nonessential_telemetry=True,
                ))
            self.manager = manager = FoundryLocalManager.instance
            providers = manager.discover_eps()
            if not any(ep.is_registered and ep.name == PROVIDER for ep in providers):
                if not allow_download and not (app_data / "ep" / "cuda-ep" / "active").is_file():
                    raise RuntimeError("GPU provider is not prepared. Run Start-Showcase.ps1 -Prepare to acquire it.")
                self.state.update(detail="Registering the CUDA GPU provider from the SDK cache")
                result = manager.download_and_register_eps(names=[PROVIDER])
                if not result.success:
                    raise RuntimeError(f"GPU provider registration failed: {result.status}")
                if not any(ep.name == PROVIDER and ep.is_registered for ep in manager.discover_eps()):
                    raise RuntimeError("CUDA provider did not register; refusing silent fallback.")
            choices = []
            for alias, spec in MODELS.items():
                variant = manager.catalog.get_model_variant(spec["id"])
                if variant is None or variant.info.runtime.execution_provider != PROVIDER:
                    raise RuntimeError(f"Required CUDA variant is absent: {spec['id']}")
                choices.append({"alias": alias, **spec, "cached": variant.is_cached,
                                "size_mb": variant.info.file_size_mb, "provider": PROVIDER})
            self.state.update(models=choices)
            self._refresh_media_state()
            self.switch(model_ref)
        except Exception as exc:
            logging.exception("Local model preparation failed")
            self.state.update(state="error", detail="Local inference unavailable", error=str(exc))

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
        if model_ref not in MODELS:
            raise ValueError("Choose a supported larger GPU model.")
        try:
            spec = MODELS[model_ref]
            with self.lock:
                if self.manager is None:
                    raise RuntimeError("Foundry Local is not initialized")
                self.state.update(state="loading", selected_model=model_ref, error=None,
                                  detail=f"Preparing {spec['label']} on the RTX GPU")
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

    @staticmethod
    def _chat_info(alias, model):
        spec = MODELS[alias]
        return {"name": spec["label"], "alias": alias, "id": model.id, "provider": PROVIDER,
                "reasoning": spec["reasoning"], "license": model.info.license}

    def _warm_chat(self, alias):
        try:
            for _ in self.stream("Reply with READY only.", "Keep the response brief.",
                                 max_tokens=1024 if MODELS[alias]["reasoning"] else 16, alias=alias):
                pass
        except RuntimeError as exc:
            # A warm-up only has to exercise the GPU path; an empty or truncated reply is not a failure.
            if "no visible answer" not in str(exc) and "GPT-OSS" not in str(exc):
                raise

    def generation_budget(self, scenario):
        reasoning = MODELS[self.state["selected_model"]]["reasoning"]
        return (4096 if scenario == "code" else 3072) if reasoning else (1600 if scenario == "code" else 650)

    def is_resident(self, alias):
        return (alias in self.guests or (self.model is not None and self._chat_alias() == alias)
                or alias in self.media)

    def ensure_chat(self, alias):
        """Keep another supported chat model resident beside the selected one; returns load timings or None."""
        if alias not in MODELS:
            raise ValueError("Unsupported chat model.")
        with self.lock:
            if self.manager is None:
                raise RuntimeError("Foundry Local is not initialized")
            if self.is_resident(alias):
                return None
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
            if self.manager is None:
                raise RuntimeError("Foundry Local is not initialized")
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
            if self.manager is None:
                return []
            return sorted(m.id for m in self.manager.catalog.get_loaded_models())

    def stream(self, prompt, system, max_tokens=650, alias=None):
        with self.lock:
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
            "tokens_per_second": round(tokens / elapsed, 1) if tokens else None,
            "finish_reason": finish,
        }
