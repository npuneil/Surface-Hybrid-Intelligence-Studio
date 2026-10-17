"""Loopback-only seller showcase; standard library HTTP, Foundry Local SDK inference."""
import argparse
import json
import logging
import secrets
import socket
import subprocess
import threading
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from content import DEFAULT_ASSUMPTIONS, SCENARIOS, calculate
from edition import APP_ID, DISPLAY_NAME
from local_engine import DEFAULT_MODEL, MODELS, LocalEngine

ROOT = Path(__file__).resolve().parent
MAX_BODY = 24000


def read_device():
    command = r"""
    $ErrorActionPreference = 'Stop'
    $cs = Get-CimInstance Win32_ComputerSystem
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $gpu = @(Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'GPU|RTX' } | Select-Object -ExpandProperty Name)
    $os = Get-CimInstance Win32_OperatingSystem
    $dimms = @(Get-CimInstance Win32_PhysicalMemory)
    $capacity = ($dimms | Measure-Object -Property Capacity -Sum).Sum
    if (-not $capacity) { throw 'No installed physical-memory capacity was returned by firmware.' }
    @{
      manufacturer=$cs.Manufacturer; model=$cs.Model
      installed_ram_bytes=[long]$capacity
      installed_ram_gb=[math]::Round($capacity / 1GB,1)
      os_visible_ram_bytes=[long]$os.TotalVisibleMemorySize * 1KB
      os_visible_ram_gb=[math]::Round(($os.TotalVisibleMemorySize * 1KB) / 1GB,1)
      memory_source='Installed: Win32_PhysicalMemory.Capacity sum; OS-visible: Win32_OperatingSystem.TotalVisibleMemorySize'
      cpu=$cpu.Name; cores=$cpu.NumberOfCores; accelerators=$gpu
      os=$os.Caption; os_version=$os.Version
    } | ConvertTo-Json -Compress
    """
    try:
        result = subprocess.run(["powershell.exe", "-NoProfile", "-Command", command],
                                capture_output=True, text=True, check=True, timeout=30)
        data = json.loads(result.stdout)
        data["gpu_memory"] = []
        data["gpu_memory_error"] = None
        try:
            gpu = subprocess.run(["nvidia-smi", "--query-gpu=name,memory.total", "--format=csv,noheader,nounits"],
                                 capture_output=True, text=True, check=True, timeout=15)
            for line in gpu.stdout.splitlines():
                name, memory = line.rsplit(",", 1)
                if memory.strip().isdigit():
                    data["gpu_memory"].append({"name": name.strip(), "reported_budget_mib": int(memory)})
        except (OSError, subprocess.SubprocessError, ValueError) as exc:
            logging.warning("GPU memory inventory unavailable: %s", exc)
            data["gpu_memory_error"] = str(exc)
        data["observed_at"] = datetime.now(timezone.utc).isoformat()
        data["error"] = None
        return data
    except (OSError, subprocess.SubprocessError, ValueError) as exc:
        logging.exception("Hardware inventory failed")
        return {"error": f"Hardware inventory unavailable: {exc}", "model": "Device not verified",
                "installed_ram_gb": None, "os_visible_ram_gb": None, "cores": None, "accelerators": []}


class ShowcaseServer(ThreadingHTTPServer):
    daemon_threads = True
    # Windows SO_REUSEADDR lets a second instance bind a port that is already listening.
    allow_reuse_address = False

    def __init__(self, address, *, engine=None, device=None, rehearsal_only=False):
        # Inventory before binding so a listening socket can always answer status probes.
        self.device = read_device() if device is None else device
        super().__init__(address, Handler)
        self.engine = engine or LocalEngine()
        self.token = secrets.token_urlsafe(32)
        self.run_lock = threading.Lock()
        if rehearsal_only:
            self.engine.state.update(state="disabled", detail="Rehearsal only. No model loaded.")

    def shutdown_request(self, request):
        # Lingering close: send FIN, then briefly drain unread request bytes so Windows
        # does not turn the close into a reset that discards an early error response.
        try:
            request.shutdown(socket.SHUT_WR)
            request.settimeout(0.5)
            drained = 0
            while drained < 65536:
                chunk = request.recv(4096)
                if not chunk:
                    break
                drained += len(chunk)
        except OSError:
            pass
        self.close_request(request)


class Handler(BaseHTTPRequestHandler):
    server_version = "SurfaceSellerShowcase/1.0"

    def headers_out(self, status, content_type, length=None):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        if length is not None:
            self.send_header("Content-Length", str(length))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("Content-Security-Policy",
                         "default-src 'self'; script-src 'self'; style-src 'self'; "
                         "img-src 'self' data:; connect-src 'self'; object-src 'none'; "
                         "base-uri 'none'; frame-ancestors 'none'; form-action 'none'")
        self.end_headers()

    def json_out(self, status, data):
        body = json.dumps(data, allow_nan=False).encode()
        self.headers_out(status, "application/json; charset=utf-8", len(body))
        self.wfile.write(body)

    def valid_host(self):
        return self.headers.get("Host", "").lower() in {
            f"127.0.0.1:{self.server.server_port}", f"localhost:{self.server.server_port}",
        }

    def do_GET(self):
        if not self.valid_host():
            return self.json_out(403, {"error": "Only loopback hosts are accepted."})
        path = self.path.split("?")[0]
        if path == "/api/status":
            return self.json_out(200, {"app_id": APP_ID,
                                       "engine": dict(self.server.engine.state), "device": self.server.device,
                                       "token": self.server.token, "assumptions": DEFAULT_ASSUMPTIONS})
        if path == "/api/scenarios":
            return self.json_out(200, {k: {n: v[n] for n in ("title", "label", "prompt", "source")}
                                      for k, v in SCENARIOS.items()})
        files = {"/": "index.html", "/app.js": "app.js", "/style.css": "style.css",
                 "/seller-guide": "SELLER-GUIDE.md", "/favicon.ico": r"native\Assets\AppIcon.ico"}
        if path not in files:
            return self.json_out(404, {"error": "Not found"})
        file = ROOT / files[path]
        types = {".html": "text/html", ".js": "text/javascript", ".css": "text/css",
                 ".md": "text/plain", ".ico": "image/x-icon"}
        body = file.read_bytes()
        content_type = types[file.suffix]
        if content_type.startswith("text/"):
            content_type += "; charset=utf-8"
        self.headers_out(200, content_type, len(body))
        self.wfile.write(body)

    def do_POST(self):
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            length = -1
        # Consume the bounded body before any rejection: closing a socket with unread
        # request data makes Windows reset the connection and the client loses the response.
        raw = self.rfile.read(length) if 0 < length <= MAX_BODY else None
        token =  (self.headers.get("X-Showcase-Token") or "").encode("utf-8", "surrogateescape")
        if not self.valid_host() or not secrets.compare_digest(token, self.server.token.encode()):
            return self.json_out(403, {"error": "Invalid local request token or host."})
        origin = self.headers.get("Origin")
        if origin and origin not in {f"http://127.0.0.1:{self.server.server_port}",
                                     f"http://localhost:{self.server.server_port}"}:
            return self.json_out(403, {"error": "Cross-origin requests are not accepted."})
        try:
            if raw is None:
                raise ValueError("Request must be between 1 and 24000 bytes.")
            body = json.loads(raw)
            if not isinstance(body, dict):
                raise ValueError("Expected a JSON object.")
        except (ValueError, UnicodeError) as exc:
            return self.json_out(400, {"error": str(exc)})
        if self.path == "/api/calculate":
            try:
                return self.json_out(200, calculate(body))
            except ValueError as exc:
                return self.json_out(400, {"error": str(exc)})
        if self.path == "/api/model":
            selected = body.get("model")
            if not isinstance(selected, str) or selected not in MODELS:
                return self.json_out(400, {"error": "Select Qwen2.5-Coder 14B or GPT-OSS 20B."})
            if self.server.engine.state["state"] not in ("ready", "error"):
                return self.json_out(503, {"error": self.server.engine.state["detail"]})
            if not self.server.run_lock.acquire(blocking=False):
                return self.json_out(409, {"error": "A generation or model switch is active."})
            self.server.engine.state.update(state="loading", selected_model=selected, error=None,
                                            detail=f"Preparing {MODELS[selected]['label']}")

            def switch():
                try:
                    self.server.engine.switch(selected)
                finally:
                    self.server.run_lock.release()

            threading.Thread(target=switch, name="model-switch", daemon=True).start()
            return self.json_out(202, {"selected_model": selected, "state": "loading"})
        if self.path != "/api/run":
            return self.json_out(404, {"error": "Not found"})
        scenario = SCENARIOS.get(body.get("scenario")) if isinstance(body.get("scenario"), str) else None
        mode = body.get("mode")
        prompt = body.get("prompt")
        if scenario is None or mode not in ("live", "rehearsal"):
            return self.json_out(400, {"error": "Select a valid scenario and mode."})
        if not isinstance(prompt, str) or not 1 <= len(prompt.strip()) <= 4000:
            return self.json_out(400, {"error": "Prompt must contain 1 to 4000 characters."})
        if mode == "rehearsal" and prompt != scenario["prompt"]:
            return self.json_out(400, {"error": "Rehearsal uses the fixed authored sample prompt."})
        if mode == "live" and self.server.engine.state["state"] != "ready":
            return self.json_out(503, {"error": self.server.engine.state["detail"]})
        if not self.server.run_lock.acquire(blocking=False):
            return self.json_out(409, {"error": "A run is already active. Wait for it to finish."})
        events = None
        try:
            self.headers_out(200, "application/x-ndjson; charset=utf-8")
            if mode == "rehearsal":
                events = iter([{"type": "token", "text": scenario["rehearsal"]},
                               {"type": "done", "mode": "rehearsal", "characters": len(scenario["rehearsal"]),
                                "model": None, "ttft_ms": None, "total_ms": None,
                                "output_tokens": None, "tokens_per_second": None, "finish_reason": "AUTHORED"}])
            else:
                events = self.server.engine.stream(
                    prompt.strip() + "\n\n" + scenario["source"], scenario["system"],
                    max_tokens=self.server.engine.generation_budget(body["scenario"]),
                )
            for event in events:
                self.wfile.write((json.dumps(event) + "\n").encode())
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            logging.info("Viewer disconnected; closing generation stream")
        except Exception as exc:
            logging.exception("Generation failed")
            try:
                self.wfile.write((json.dumps({"type": "error", "error": str(exc)}) + "\n").encode())
                self.wfile.flush()
            except OSError:
                logging.info("Could not deliver error to disconnected viewer")
        finally:
            if events is not None and hasattr(events, "close"):
                events.close()
            self.server.run_lock.release()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8790)
    parser.add_argument("--rehearsal-only", action="store_true")
    parser.add_argument("--prepare", action="store_true")
    parser.add_argument("--model", choices=MODELS, default=DEFAULT_MODEL)
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    server = ShowcaseServer(("127.0.0.1", args.port), rehearsal_only=args.rehearsal_only)
    if not args.rehearsal_only:
        threading.Thread(target=server.engine.start,
                         kwargs={"allow_download": args.prepare, "model_ref": args.model}, daemon=True).start()
    logging.info("%s: http://127.0.0.1:%s", DISPLAY_NAME, args.port)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        logging.info("Showcase stopped")
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
