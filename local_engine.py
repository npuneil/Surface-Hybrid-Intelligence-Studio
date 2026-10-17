"""Real, serialized Foundry Local inference. No authored fallback."""
import os
import re
import threading
import time
from pathlib import Path

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
                      "selected_model": DEFAULT_MODEL, "models": [], "error": None}
        self.lock = threading.Lock()
        self.model = None
        self.manager = None
        self.allow_download = False

    def start(self, allow_download=False, model_ref=DEFAULT_MODEL):
        try:
            from foundry_local_sdk import Configuration, FoundryLocalManager
            shared = Path(os.environ["LOCALAPPDATA"]) / "ASUAgenticShowcase"
            reuse = (shared / "models").is_dir()
            app_data = shared if reuse else Path.home() / f".{APP_ID}"
            model_cache = shared / "models" if reuse else app_data / "cache"
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
            self.allow_download = allow_download
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
            self.switch(model_ref)
        except Exception as exc:
            import logging
            logging.exception("Local model preparation failed")
            self.state.update(state="error", detail="Local inference unavailable", error=str(exc))

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
                if self.model is not None:
                    self.state.update(detail="Releasing the previous model; one model resident at a time")
                    self.model.unload()
                    self.model = None
                    self.state["model"] = None
                model = self.manager.catalog.get_model_variant(spec["id"])
                if model is None or model.info.runtime.execution_provider != PROVIDER:
                    raise RuntimeError(f"Expected CUDA variant {spec['id']}; refusing silent fallback.")
                if not model.is_cached:
                    if not self.allow_download:
                        raise RuntimeError("Model is not cached. Restart with -Prepare to permit model acquisition.")
                    self.state.update(state="downloading", detail=f"Downloading {spec['label']}")
                    model.download(progress_callback=lambda p: self.state.update(detail=f"Downloading {spec['label']}: {p:.0f}%"))
                self.state.update(state="loading", detail=f"Loading {spec['label']} on CUDA")
                model.load()
                self.model = model
                self.state.update(model={"name": spec["label"], "alias": model_ref, "id": model.id,
                                         "provider": PROVIDER, "reasoning": spec["reasoning"],
                                         "license": model.info.license})
                self.state["models"] = [{**choice, "cached": True} if choice["alias"] == model_ref else choice
                                        for choice in self.state["models"]]
            self.state.update(state="warming", detail=f"Warming {spec['label']}")
            for _ in self.stream("Reply with READY only.", "Keep the response brief.",
                                 max_tokens=1024 if spec["reasoning"] else 16):
                pass
            self.state.update(state="ready", detail=f"{spec['label']} ready for live local inference")
        except Exception as exc:
            import logging
            logging.exception("Model switch failed")
            self.state.update(state="error", detail="Selected model unavailable", error=str(exc))

    def generation_budget(self, scenario):
        reasoning = MODELS[self.state["selected_model"]]["reasoning"]
        return (4096 if scenario == "code" else 3072) if reasoning else (1600 if scenario == "code" else 650)

    def stream(self, prompt, system, max_tokens=650):
        from foundry_local_sdk import (
            ChatSession, MessageItem, Request, RequestOptions, SearchOptions, TextItem, TextItemType,
        )
        with self.lock:
            if self.model is None:
                raise RuntimeError("No model loaded")
            started = time.perf_counter()
            first = None
            chars = reasoning_chars = 0
            reasoning_model = self.state["model"]["reasoning"]
            channel_filter = HarmonyFilter() if reasoning_model else None
            with ChatSession(self.model) as session:
                session.set_options(RequestOptions(search=SearchOptions(
                    temperature=1.0 if reasoning_model else 0.0, top_p=1.0, max_output_tokens=max_tokens)))
                session.set_streaming(True)
                with Request() as request:
                    request.add_item(MessageItem.system(
                        "Reasoning: low\nKeep internal planning brief; do not count words step by step.\n" + system
                        if reasoning_model else system))
                    request.add_item(MessageItem.user(prompt))
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
                raise RuntimeError("Model returned no visible answer")
            yield {
                "type": "done", "mode": "live", "model": self.state["model"],
                "ttft_ms": round((first - started) * 1000),
                "total_ms": round(elapsed * 1000), "characters": chars,
                "reasoning_characters": reasoning_chars,
                "output_tokens": tokens,
                "tokens_per_second": round(tokens / elapsed, 1) if tokens else None,
                "finish_reason": finish,
            }
