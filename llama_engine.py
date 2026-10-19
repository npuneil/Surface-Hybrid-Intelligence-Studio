"""Optional larger models: GGUF weights on llama.cpp's CUDA backend with every layer on the RTX GPU.

Foundry Local's catalog tops out at GPT-OSS 20B, so 30B+ open-weight models come from the checksum-verified
Local-AI-Bench collection and run through its pinned llama.cpp build. There is no CPU fallback: memory is
checked before loading, and a server that cannot confirm every layer on the GPU is refused.

Only dedicated GPU memory counts. The driver also exposes shared GPU memory carved from Windows RAM, and CUDA
will spill into it, but that is much slower and every byte is charged to Windows commit, so a server whose
measured shared usage exceeds SHARED_LIMIT_GIB is refused.
"""
import atexit
import ctypes
import json
import logging
import os
import re
import socket
import subprocess
import time
import urllib.error
import urllib.request
from pathlib import Path

PROVIDER = "llama.cpp CUDA"
GIB = 1024 ** 3
CONTEXT = 8192
LOAD_TIMEOUT_S = 900
COMMIT_RESERVE_GIB = 2.0
GPU_RESERVE_GIB = 0.5
# Pinned host buffers alone are a few hundred MiB; more than this means weights spilled into shared GPU memory.
SHARED_LIMIT_GIB = 2.0
# A resident full-offload server's private bytes exceed its dedicated + shared GPU usage by under 1 GiB (CPU-side
# buffers). Anything beyond that is GPU memory Windows paged out (after sleep) and will page back in on next use.
PAGED_SLACK_GIB = 1.0
MAI_SOURCE = "https://commandline.microsoft.com/local-models-sandboxed-tools-github-windows/"

# overhead_gib: estimated KV cache (8K context, one slot) plus compute buffers on top of the weight file.
# exclusive: too large to share the GPU with the Foundry Local chat and media models.
LARGE_MODELS = {
    "qwen3-coder-30b": {"label": "Qwen3-Coder 30B-A3B", "params": "30B MoE, 3B active", "reasoning": False,
                        "overhead_gib": 2.0, "exclusive": False},
    "llama3.3-70b": {"label": "Llama 3.3 70B", "params": "70B dense", "reasoning": False,
                     "overhead_gib": 4.0, "exclusive": True},
    "gpt-oss-120b": {"label": "GPT-OSS 120B", "params": "117B MoE, 5.1B active", "reasoning": True,
                     "overhead_gib": 2.5, "exclusive": True},
    "mai-code-1.1-flash": {
        "label": "MAI Code 1.1 Flash", "params": "137B MoE, 6.8B active", "reasoning": False,
        "overhead_gib": 6.0, "exclusive": True,
        "unavailable": "Not installed on this PC. Microsoft ships the local build through GitHub Copilot's "
                       "Windows ML provider; there is no standalone download for this app to load yet.",
        "published": "Microsoft reports a 53 GB quantized local build with 75.5 GB peak memory at 256K context "
                     "on Surface Laptop Ultra (October 2026). Not measured by this app.",
        "published_source": MAI_SOURCE,
    },
}


def home():
    return Path(os.environ.get("HYBRID_STUDIO_LLAMA_HOME") or Path.home() / "Local-AI-Bench")


def server_exe():
    return home() / "tools" / "llama" / "llama-server.exe"


def _manifest():
    try:
        data = json.loads((home() / "models" / "installed.json").read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def locate(alias, manifest=None):
    """(first GGUF path, total bytes, license, checksum-verified) or None when the weights are not on disk."""
    folder = home() / "models" / alias
    entry = (manifest if manifest is not None else _manifest()).get(alias)
    if isinstance(entry, dict) and entry.get("files"):
        files = sorted(entry["files"], key=lambda f: f.get("filename", ""))
        paths = [folder / f["filename"] for f in files]
        # installed.json lists only complete, SHA256-verified artifacts; a size check catches later damage.
        if all(p.is_file() and p.stat().st_size == f.get("size_bytes") for p, f in zip(paths, files)):
            return paths[0], sum(f["size_bytes"] for f in files), entry.get("license"), True
        return None
    ggufs = sorted(folder.glob("*.gguf")) if folder.is_dir() else []
    if ggufs:
        return ggufs[0], sum(p.stat().st_size for p in ggufs), None, False
    return None


def gpu_memory_mib():
    """(total, used) MiB for the first NVIDIA GPU, or None."""
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=memory.total,memory.used", "--format=csv,noheader,nounits"],
                             capture_output=True, text=True, check=True, timeout=15).stdout
        total, used = (int(v) for v in out.splitlines()[0].split(","))
        return total, used
    except (OSError, subprocess.SubprocessError, ValueError, IndexError):
        logging.warning("nvidia-smi memory query failed", exc_info=True)
        return None


class _MemoryStatus(ctypes.Structure):
    _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]


def commit_bytes():
    """(limit, available) Windows commit charge. GPU allocations on this unified-memory PC are charged here too."""
    if os.name != "nt":
        return None
    status = _MemoryStatus()
    status.dwLength = ctypes.sizeof(status)
    if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
        return None
    return int(status.ullTotalPageFile), int(status.ullAvailPageFile)


def gpu_process_memory(pid):
    """(dedicated, shared) bytes the GPU driver reports for one process, or None. Windows performance counters."""
    if os.name != "nt" or not pid:
        return None
    command = (f"$s = (Get-Counter '\\GPU Process Memory(pid_{int(pid)}_*)\\Dedicated Usage',"
               f"'\\GPU Process Memory(pid_{int(pid)}_*)\\Shared Usage' -ErrorAction Stop).CounterSamples; "
               "'{0} {1}' -f [long](($s | Where-Object Path -like '*dedicated usage' | Measure-Object CookedValue -Sum).Sum),"
               "[long](($s | Where-Object Path -like '*shared usage' | Measure-Object CookedValue -Sum).Sum)")
    try:
        out = subprocess.run(["powershell.exe", "-NoProfile", "-Command", command], capture_output=True, text=True,
                             check=True, timeout=20, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).stdout
        dedicated, shared = (int(v) for v in out.split())
        return dedicated, shared
    except (OSError, subprocess.SubprocessError, ValueError):
        logging.warning("GPU process memory counters unavailable for PID %s", pid, exc_info=True)
        return None


def placement(log_text):
    """Layer offload and CUDA buffer sizes from a llama-server log written with -lv 4, or None."""
    offload = re.search(r"offloaded (\d+)/(\d+) layers to GPU", log_text or "")
    if not offload:
        return None
    cuda_mib = sum(float(m) for m in re.findall(r"CUDA\d+ (?:model|KV|compute) buffer size =\s+([\d.]+) MiB", log_text))
    return {"offloaded": int(offload.group(1)), "layers": int(offload.group(2)), "cuda_mib": cuda_mib or None}


def _split_text(split):
    return f"{split[0] / GIB:.1f} GiB dedicated, {split[1] / GIB:.1f} GiB shared GPU memory (measured)"


def paged_out_bytes(server, split=None):
    """GPU memory another llama-server holds but Windows has paged out of the GPU (it does this when the PC
    sleeps). It moves back into dedicated memory on that server's next request, so it still counts as in use."""
    if not server.get("private_bytes") or server.get("gpu_layers") == "0":
        return 0
    split = split if split is not None else gpu_process_memory(server.get("pid"))
    if split is None:
        return 0
    return max(0, int(server["private_bytes"] - split[0] - split[1] - PAGED_SLACK_GIB * GIB))


def _arg(command, *names):
    for name in names:
        match = re.search(rf'(?:^|\s){re.escape(name)}\s+("[^"]+"|\S+)', command or "")
        if match:
            return match.group(1).strip('"')
    return None


def running_servers():
    """Every llama-server.exe on this PC: pid, port, alias, model path, GPU layers flag, private bytes."""
    command = ("Get-CimInstance Win32_Process -Filter \"Name='llama-server.exe'\" | ForEach-Object { "
               "$p = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue; "
               "@{pid=$_.ProcessId; cmd=$_.CommandLine; private=[long]$p.PrivateMemorySize64} } | ConvertTo-Json -Compress")
    try:
        out = subprocess.run(["powershell.exe", "-NoProfile", "-Command", command],
                             capture_output=True, text=True, check=True, timeout=20).stdout.strip()
        rows = json.loads(out) if out else []
    except (OSError, subprocess.SubprocessError, ValueError):
        logging.warning("Could not list llama-server processes", exc_info=True)
        return []
    servers = []
    for row in rows if isinstance(rows, list) else [rows]:
        cmd = row.get("cmd") or ""
        port = _arg(cmd, "--port")
        servers.append({"pid": row.get("pid"), "port": int(port) if port and port.isdigit() else 8080,
                        "alias": _arg(cmd, "--alias", "-a"), "model": _arg(cmd, "-m", "--model"),
                        "gpu_layers": _arg(cmd, "-ngl", "--n-gpu-layers", "--gpu-layers"),
                        "private_bytes": row.get("private") or 0})
    return servers


def _full_offload_flag(value):
    return value == "all" or (value is not None and value.isdigit() and int(value) >= 999)


def _same_file(a, b):
    try:
        return a is not None and b is not None and os.path.samefile(a, b)
    except OSError:
        return False


def _http_json(port, path, timeout=3, body=None):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(f"http://127.0.0.1:{port}{path}", data=data,
                                     headers={"Content-Type": "application/json"} if data else {})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read())


def _free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


class LargeModelHost:
    """One llama-server at a time: either attached (already serving the same GGUF) or started and owned here."""

    def __init__(self, runtime_dir):
        self.runtime_dir = Path(runtime_dir)
        self.process = None
        self.port = None
        self.alias = None
        self.info = None
        self.build = None
        atexit.register(self.stop)

    # ------------------------------------------------------------ discovery
    def catalog(self):
        manifest = _manifest()
        exe_ok = server_exe().is_file()
        rows = []
        for alias, spec in LARGE_MODELS.items():
            found = locate(alias, manifest)
            blocker = None
            if found is None:
                blocker = spec.get("unavailable") or (f"Weights not found in {home() / 'models' / alias}. "
                                                      "Install them with Local-AI-Bench (bench.ps1 models install).")
            elif not exe_ok:
                blocker = f"llama.cpp server not found at {server_exe()}. Run Local-AI-Bench scripts\\setup.ps1."
            rows.append({"alias": alias, "id": found[0].name if found else alias, "label": spec["label"],
                         "reasoning": spec["reasoning"], "cached": found is not None,
                         "size_mb": round(found[1] / 1e6) if found else None, "provider": PROVIDER,
                         "runtime": "llama.cpp", "params": spec["params"], "available": blocker is None,
                         "exclusive": spec["exclusive"],
                         "need_gib": round(found[1] / GIB + spec["overhead_gib"], 1) if found else None,
                         "blocker": blocker, "license": found[2] if found else None,
                         "verified": bool(found and found[3]), "published": spec.get("published"),
                         "published_source": spec.get("published_source")})
        return rows

    def _version(self):
        if self.build is None:
            try:
                out = subprocess.run([str(server_exe()), "--version"], capture_output=True, text=True, timeout=20,
                                     creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
                match = re.search(r"build (\d+), commit (\w+)", out.stdout + out.stderr)
                self.build = f"b{match.group(1)}-{match.group(2)}" if match else "unknown build"
            except (OSError, subprocess.SubprocessError):
                self.build = "unknown build"
        return self.build

    # ------------------------------------------------------------ lifecycle
    def loaded(self, alias=None):
        return self.info is not None and (alias is None or alias == self.alias)

    def stop(self):
        process, self.process = self.process, None
        self.port = self.alias = self.info = None
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                process.kill()

    def load(self, alias, on_progress=lambda detail: None):
        spec = LARGE_MODELS[alias]
        found = locate(alias)
        if found is None:
            raise RuntimeError(spec.get("unavailable") or f"{spec['label']} weights are not installed.")
        path, size = found[0], found[1]
        if self.loaded(alias):
            return None
        self.stop()
        started = time.perf_counter()
        servers = running_servers()
        for server in servers:
            if _same_file(server["model"], path) and _full_offload_flag(server["gpu_layers"]):
                try:
                    props = _http_json(server["port"], "/props")
                    if _same_file(props.get("model_path"), path) and _http_json(server["port"], "/health").get("status") == "ok":
                        split = gpu_process_memory(server["pid"])
                        if split is not None and split[1] / GIB > SHARED_LIMIT_GIB:
                            logging.info("Not attaching to PID %s: %s", server["pid"], _split_text(split))
                            continue
                        if split is not None and paged_out_bytes(server, split):
                            on_progress(f"Moving the running {spec['label']} server back into GPU memory "
                                        "(Windows paged it out while the PC slept)")
                            _http_json(server["port"], "/completion", timeout=300, body={"prompt": "OK", "n_predict": 1})
                            split = gpu_process_memory(server["pid"])
                            if split is not None and (split[1] / GIB > SHARED_LIMIT_GIB or paged_out_bytes(server, split)):
                                logging.info("Not attaching to PID %s after warm-up: %s", server["pid"], _split_text(split))
                                continue
                        self._ready(alias, server["port"], f"attached to llama-server PID {server['pid']} "
                                    f"on port {server['port']} (already running; not started by this app)",
                                    props.get("build_info") or self._version(), "all layers (-ngl all)", found,
                                    split=split)
                        on_progress(f"Using the {spec['label']} server that is already running")
                        return {"load_ms": round((time.perf_counter() - started) * 1000), "attached": True}
                except (OSError, ValueError, urllib.error.URLError):
                    logging.info("llama-server on port %s did not answer; not attaching", server["port"])
        if not server_exe().is_file():
            raise RuntimeError(f"llama.cpp server not found at {server_exe()}.")
        self._preflight(spec, size, servers)
        port = _free_port()
        self.runtime_dir.mkdir(parents=True, exist_ok=True)
        log_path = self.runtime_dir / f"llama-{alias}.log"
        # -lv 4: this llama.cpp build writes the "offloaded N/N layers" and buffer lines only at debug verbosity.
        args = [str(server_exe()), "-m", str(path), "--alias", alias, "--host", "127.0.0.1", "--port", str(port),
                "-ngl", "all", "-fit", "off", "-c", str(CONTEXT), "-np", "1", "-fa", "on", "-cram", "0",
                "--jinja", "--no-webui", "--metrics", "-lv", "4"]
        with open(log_path, "wb") as log:
            self.process = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.port = port
        while True:
            elapsed = time.perf_counter() - started
            if self.process.poll() is not None:
                reason = self._log_reason(log_path)
                self.stop()
                raise RuntimeError(f"llama.cpp stopped while loading {spec['label']}: {reason}")
            if elapsed > LOAD_TIMEOUT_S:
                self.stop()
                raise RuntimeError(f"{spec['label']} did not finish loading within {LOAD_TIMEOUT_S // 60} minutes.")
            try:
                if _http_json(port, "/health", timeout=2).get("status") == "ok":
                    break
            except (OSError, ValueError, urllib.error.URLError):
                pass
            on_progress(f"Loading {spec['label']} ({size / GIB:.1f} GiB) into GPU memory: {elapsed:.0f} s")
            time.sleep(1)
        log_text = log_path.read_text(encoding="utf-8", errors="replace")
        placed = placement(log_text)
        if not placed or placed["offloaded"] != placed["layers"]:
            self.stop()
            found_text = f"offloaded {placed['offloaded']}/{placed['layers']} layers" if placed else "offload not reported"
            raise RuntimeError(f"{spec['label']} did not place every layer on the GPU ({found_text}); "
                               "refusing CPU fallback.")
        split = gpu_process_memory(self.process.pid)
        if split is not None and split[1] / GIB > SHARED_LIMIT_GIB:
            self.stop()
            raise RuntimeError(f"{spec['label']} did not fit in dedicated GPU memory: {_split_text(split)}. Shared GPU "
                               "memory is Windows RAM; it is much slower and is not used for demos. Close other GPU "
                               "apps or local model servers and load again.")
        self._ready(alias, port, "started by this app (stops when the app closes or another model is selected)",
                    self._version(), f"{placed['offloaded']}/{placed['layers']} layers on GPU", found,
                    placed["cuda_mib"], split)
        return {"load_ms": round((time.perf_counter() - started) * 1000), "attached": False}

    def _ready(self, alias, port, endpoint, build, offload, found, gpu_mib=None, split=None):
        spec = LARGE_MODELS[alias]
        path, size, license_name, verified = found
        self.alias, self.port = alias, port
        self.info = {"name": spec["label"], "alias": alias, "id": path.name, "provider": PROVIDER,
                     "reasoning": spec["reasoning"], "license": license_name, "runtime": f"llama.cpp {build}",
                     "offload": offload, "endpoint": endpoint, "weights_gib": round(size / GIB, 1),
                     "checksum_verified": verified, "gpu_buffers_mib": round(gpu_mib) if gpu_mib else None,
                     "gpu_memory": _split_text(split) if split else None}

    def precheck(self, alias, releasable_bytes=0):
        """Refuse early, before anything is unloaded, when the model cannot fit even after this app releases
        `releasable_bytes` of its own GPU memory. load() repeats the full check once memory is released."""
        if self.loaded(alias):
            return
        spec = LARGE_MODELS[alias]
        found = locate(alias)
        if found is None:
            raise RuntimeError(spec.get("unavailable") or f"{spec['label']} weights are not installed.")
        servers = running_servers()
        if any(_same_file(s["model"], found[0]) and _full_offload_flag(s["gpu_layers"]) for s in servers):
            return
        self._preflight(spec, found[1], servers, releasable_bytes)

    def owned_pid(self):
        return self.process.pid if self.process is not None and self.process.poll() is None else None

    def _preflight(self, spec, size, servers, releasable_bytes=0):
        need = size / GIB + spec["overhead_gib"]
        released = releasable_bytes / GIB
        others = [s for s in servers if s["pid"] and s["private_bytes"] and s["pid"] != self.owned_pid()]
        holders = "; ".join(f"llama-server PID {s['pid']} ({s['alias'] or Path(s['model'] or '?').stem}, port "
                            f"{s['port']}) holding {s['private_bytes'] / GIB:.1f} GiB" for s in others)
        hint = (f" Other local model servers on this PC: {holders}. This app never stops another app's server; "
                "close it from the app that started it and load again." if holders
                else " Close other GPU-heavy apps and load again.")
        mine = f" (including {released:.1f} GiB this app would release first)" if released >= 0.1 else ""
        gpu = gpu_memory_mib()
        if gpu is not None:
            total, used = gpu
            paged = [(s, b) for s in others if (b := paged_out_bytes(s))]
            paged_gib = sum(b for _, b in paged) / GIB
            paged_text = "".join(f" llama-server PID {s['pid']} also has {b / GIB:.1f} GiB paged out of GPU memory "
                                 "(Windows does this when the PC sleeps); it moves back on that server's next "
                                 "request, so it is counted as in use." for s, b in paged)
            free = (total - used) / 1024 + released - paged_gib
            if need > free - GPU_RESERVE_GIB:
                raise RuntimeError(f"{spec['label']} needs about {need:.1f} GiB of dedicated GPU memory ({size / GIB:.1f} GiB "
                                   f"weights plus an estimated {spec['overhead_gib']:.1f} GiB for context and buffers); "
                                   f"{free:.1f} GiB of the GPU's {total / 1024:.1f} GiB would be free{mine}."
                                   f"{paged_text}{hint}")
        commit = commit_bytes()
        if commit is not None:
            limit, available = commit
            available_gib = available / GIB + released
            if need > available_gib - COMMIT_RESERVE_GIB:
                raise RuntimeError(f"{spec['label']} needs about {need:.1f} GiB of Windows commit (GPU memory is "
                                   f"charged there on this PC); {available_gib:.1f} GiB of {limit / GIB:.1f} GiB would "
                                   f"be available{mine}.{hint}")

    @staticmethod
    def _log_reason(log_path):
        try:
            lines = [l.strip() for l in log_path.read_text(encoding="utf-8", errors="replace").splitlines() if l.strip()]
        except OSError:
            return "no log was written"
        for line in reversed(lines):
            if re.search(r"error|failed|out of memory|unable", line, re.IGNORECASE):
                return line[-300:]
        return lines[-1][-300:] if lines else "no log output"

    # ------------------------------------------------------------ inference
    def stream(self, system, prompt, max_tokens, channel_filter=None):
        if self.info is None:
            raise RuntimeError("No larger model is loaded")
        info, reasoning = dict(self.info), self.info["reasoning"]
        body = {"model": self.alias, "max_tokens": max_tokens, "stream": True,
                "stream_options": {"include_usage": True},
                "temperature": 1.0 if reasoning else 0.0, "top_p": 1.0, "repeat_penalty": 1.0,
                "messages": [{"role": "system", "content": system}, {"role": "user", "content": prompt}]}
        if reasoning:
            body["chat_template_kwargs"] = {"reasoning_effort": "low"}
        request = urllib.request.Request(f"http://127.0.0.1:{self.port}/v1/chat/completions",
                                         data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
        started = time.perf_counter()
        first = None
        chars = reasoning_chars = 0
        finish, usage, timings = None, None, None
        try:
            response = urllib.request.urlopen(request, timeout=600)
        except urllib.error.HTTPError as exc:
            raise RuntimeError(f"llama.cpp rejected the request (HTTP {exc.code}).") from None
        except (OSError, urllib.error.URLError) as exc:
            raise RuntimeError(f"Could not reach the llama.cpp server for {info['name']}: {exc}") from None
        with response:
            for raw in response:
                line = raw.decode("utf-8", errors="replace").strip()
                if not line.startswith("data:"):
                    continue
                payload = line[5:].strip()
                if payload == "[DONE]":
                    break
                chunk = json.loads(payload)
                if "error" in chunk:
                    raise RuntimeError("llama.cpp reported an error while generating.")
                usage = chunk.get("usage") or usage
                timings = chunk.get("timings") or timings
                for choice in chunk.get("choices") or []:
                    finish = choice.get("finish_reason") or finish
                    delta = choice.get("delta") or {}
                    if delta.get("reasoning_content"):
                        reasoning_chars += len(delta["reasoning_content"])
                        yield {"type": "activity", "reasoning_characters": reasoning_chars}
                    text = delta.get("content") or ""
                    if text and channel_filter is not None:
                        text, hidden = channel_filter.feed(text)
                        if hidden:
                            reasoning_chars += hidden
                            yield {"type": "activity", "reasoning_characters": reasoning_chars}
                    if text:
                        first = first or time.perf_counter()
                        chars += len(text)
                        yield {"type": "token", "text": text}
        if channel_filter is not None:
            text, hidden = channel_filter.feed("", final=True)
            reasoning_chars += hidden
            if text:
                first = first or time.perf_counter()
                chars += len(text)
                yield {"type": "token", "text": text}
        elapsed = time.perf_counter() - started
        tokens = (usage or {}).get("completion_tokens") or (timings or {}).get("predicted_n")
        if not chars:
            if reasoning_chars and tokens and tokens >= max_tokens - 8:
                raise RuntimeError(f"{info['name']} used its whole {max_tokens:,}-token output budget reasoning and "
                                   "gave no visible answer. Answers vary run to run; run it again.")
            raise RuntimeError("Model returned no visible answer")
        decode = (timings or {}).get("predicted_per_second")
        prefill = (timings or {}).get("prompt_per_second")
        yield {"type": "done", "mode": "live", "model": info,
               "ttft_ms": round((first - started) * 1000), "total_ms": round(elapsed * 1000),
               "characters": chars, "reasoning_characters": reasoning_chars, "output_tokens": tokens,
               "tokens_per_second": round(tokens / elapsed, 1) if tokens else None,
               "decode_tokens_per_second": round(decode, 1) if decode else None,
               "prompt_tokens_per_second": round(prefill, 1) if prefill else None,
               "finish_reason": (finish or "unknown").upper()}
