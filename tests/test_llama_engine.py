import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import llama_engine
from llama_engine import GIB, LARGE_MODELS, LargeModelHost, placement

# Lines captured from this build (b11573) loading Llama 3.3 70B with -lv 4.
LOG_70B = """\
0.00.484.239 I srv    load_model: loading model 'Llama-3.3-70B-Instruct-Q4_K_M.gguf'
0.36.100.001 I load_tensors: offloading output layer to GPU
0.36.100.002 I load_tensors: offloading 79 repeating layers to GPU
0.36.100.003 I load_tensors: offloaded 81/81 layers to GPU
0.36.100.004 I load_tensors:        CUDA0 model buffer size = 39979.48 MiB
0.36.100.005 I load_tensors:    CUDA_Host model buffer size =   563.62 MiB
0.36.200.000 I llama_kv_cache:      CUDA0 KV buffer size =  2560.00 MiB
0.36.300.000 I sched_reserve:      CUDA0 compute buffer size =   240.01 MiB
0.36.300.001 I sched_reserve:  CUDA_Host compute buffer size =    72.02 MiB
"""


class PlacementTests(unittest.TestCase):
    def test_full_offload_and_cuda_buffers_are_read_from_the_log(self):
        placed = placement(LOG_70B)
        self.assertEqual((placed["offloaded"], placed["layers"]), (81, 81))
        self.assertAlmostEqual(placed["cuda_mib"], 39979.48 + 2560.00 + 240.01, places=2)

    def test_partial_offload_is_reported(self):
        placed = placement(LOG_70B.replace("offloaded 81/81", "offloaded 60/81"))
        self.assertNotEqual(placed["offloaded"], placed["layers"])

    def test_info_level_log_without_offload_line_is_not_trusted(self):
        self.assertIsNone(placement("I srv  llama_server: model loaded\nI srv  llama_server: listening"))


class CatalogTests(unittest.TestCase):
    def test_mai_is_listed_unavailable_with_published_figures_labelled(self):
        with tempfile.TemporaryDirectory() as tmp, patch.dict("os.environ", {"HYBRID_STUDIO_LLAMA_HOME": tmp}):
            rows = {row["alias"]: row for row in LargeModelHost(Path(tmp)).catalog()}
        mai = rows["mai-code-1.1-flash"]
        self.assertFalse(mai["available"])
        self.assertIn("Windows ML", mai["blocker"])
        self.assertIn("Not measured by this app", mai["published"])
        self.assertIsNone(mai["need_gib"])

    def test_installed_weights_report_memory_need(self):
        with tempfile.TemporaryDirectory() as tmp, patch.dict("os.environ", {"HYBRID_STUDIO_LLAMA_HOME": tmp}):
            folder = Path(tmp) / "models" / "llama3.3-70b"
            folder.mkdir(parents=True)
            (folder / "model.gguf").write_bytes(b"\0" * 1024)
            (Path(tmp) / "tools" / "llama").mkdir(parents=True)
            (Path(tmp) / "tools" / "llama" / "llama-server.exe").write_bytes(b"")
            row = next(r for r in LargeModelHost(Path(tmp)).catalog() if r["alias"] == "llama3.3-70b")
        self.assertTrue(row["available"])
        self.assertFalse(row["verified"])
        self.assertAlmostEqual(row["need_gib"], LARGE_MODELS["llama3.3-70b"]["overhead_gib"], places=1)


class PreflightTests(unittest.TestCase):
    def setUp(self):
        self.host = LargeModelHost(Path(tempfile.gettempdir()))
        self.spec = LARGE_MODELS["llama3.3-70b"]
        self.size = int(39.6 * GIB)

    def test_refuses_when_dedicated_memory_is_short_and_names_other_servers(self):
        server = {"pid": 4242, "port": 8090, "alias": "qwen3-coder-30b", "model": None, "private_bytes": int(30.6 * GIB)}
        with patch.object(llama_engine, "gpu_memory_mib", return_value=(64000, 37000)), \
                patch.object(llama_engine, "gpu_process_memory", return_value=(int(29.6 * GIB), int(0.4 * GIB))), \
                patch.object(llama_engine, "commit_bytes", return_value=(int(108 * GIB), int(60 * GIB))):
            with self.assertRaises(RuntimeError) as caught:
                self.host._preflight(self.spec, self.size, [server])
        message = str(caught.exception)
        self.assertIn("43.6 GiB", message)
        self.assertIn("PID 4242", message)
        self.assertIn("never stops another app's server", message)
        self.assertNotIn("paged out", message)

    def test_server_paged_out_after_sleep_still_counts_as_using_the_gpu(self):
        # Measured after resume: nvidia-smi shows 3.6 GiB used while the 30B server's 29.6 GiB sits paged out.
        server = {"pid": 4242, "port": 8090, "alias": "qwen3-coder-30b", "model": None, "gpu_layers": "all",
                  "private_bytes": int(30.7 * GIB)}
        with patch.object(llama_engine, "gpu_memory_mib", return_value=(64000, 3621)), \
                patch.object(llama_engine, "gpu_process_memory", return_value=(0, int(0.36 * GIB))), \
                patch.object(llama_engine, "commit_bytes", return_value=(int(108 * GIB), int(75 * GIB))):
            with self.assertRaises(RuntimeError) as caught:
                self.host._preflight(self.spec, self.size, [server])
        self.assertIn("29.3 GiB paged out of GPU memory", str(caught.exception))

    def test_refuses_when_windows_commit_is_short(self):
        with patch.object(llama_engine, "gpu_memory_mib", return_value=(64000, 7000)), \
                patch.object(llama_engine, "commit_bytes", return_value=(int(108 * GIB), int(40 * GIB))):
            with self.assertRaisesRegex(RuntimeError, "Windows commit"):
                self.host._preflight(self.spec, self.size, [])

    def test_passes_when_both_budgets_fit(self):
        with patch.object(llama_engine, "gpu_memory_mib", return_value=(64000, 7000)), \
                patch.object(llama_engine, "commit_bytes", return_value=(int(108 * GIB), int(75 * GIB))):
            self.host._preflight(self.spec, self.size, [])

    def test_memory_this_app_would_release_counts_toward_the_fit(self):
        budgets = {"gpu_memory_mib": (64000, 24000), "commit_bytes": (int(108 * GIB), int(35 * GIB))}
        with patch.object(llama_engine, "gpu_memory_mib", return_value=budgets["gpu_memory_mib"]), \
                patch.object(llama_engine, "commit_bytes", return_value=budgets["commit_bytes"]):
            with self.assertRaisesRegex(RuntimeError, "dedicated GPU memory"):
                self.host._preflight(self.spec, self.size, [])
            self.host._preflight(self.spec, self.size, [], releasable_bytes=int(17 * GIB))


class PrecheckTests(unittest.TestCase):
    def test_refuses_before_unloading_and_counts_foundry_for_exclusive_models(self):
        from local_engine import LocalEngine
        engine = LocalEngine.__new__(LocalEngine)
        engine.large = LargeModelHost(Path(tempfile.gettempdir()))
        with patch.object(llama_engine, "gpu_process_memory", return_value=(int(17 * GIB), int(0.3 * GIB))) as counters, \
                patch.object(engine.large, "precheck") as precheck:
            engine.precheck_large("llama3.3-70b")
            precheck.assert_called_once_with("llama3.3-70b", int(17 * GIB))
            precheck.reset_mock()
            engine.precheck_large("qwen3-coder-30b")
            precheck.assert_called_once_with("qwen3-coder-30b", 0)
        self.assertEqual(counters.call_count, 1)

    def test_attachable_server_skips_the_memory_check(self):
        host = LargeModelHost(Path(tempfile.gettempdir()))
        gguf = Path(tempfile.gettempdir()) / "attach.gguf"
        gguf.write_bytes(b"\0")
        try:
            server = {"pid": 7, "port": 8090, "alias": "qwen3-coder-30b", "model": str(gguf), "gpu_layers": "all",
                      "private_bytes": int(30 * GIB)}
            with patch.object(llama_engine, "locate", return_value=(gguf, int(17.3 * GIB), None, True)), \
                    patch.object(llama_engine, "running_servers", return_value=[server]), \
                    patch.object(host, "_preflight") as preflight:
                host.precheck("qwen3-coder-30b")
            preflight.assert_not_called()
        finally:
            gguf.unlink()

    def test_attach_moves_a_paged_out_server_back_onto_the_gpu_before_measuring(self):
        host = LargeModelHost(Path(tempfile.gettempdir()))
        gguf = Path(tempfile.gettempdir()) / "attach-paged.gguf"
        gguf.write_bytes(b"\0")
        calls = []

        def fake_http(port, path, timeout=3, body=None):
            calls.append(path)
            return {"/props": {"model_path": str(gguf), "build_info": "b1"}, "/health": {"status": "ok"}}.get(path, {})

        try:
            server = {"pid": 7, "port": 8090, "alias": "qwen3-coder-30b", "model": str(gguf), "gpu_layers": "all",
                      "private_bytes": int(30.7 * GIB)}
            splits = [(0, int(0.36 * GIB)), (int(29.6 * GIB), int(0.38 * GIB))]
            with patch.object(llama_engine, "locate", return_value=(gguf, int(17.3 * GIB), None, True)), \
                    patch.object(llama_engine, "running_servers", return_value=[server]), \
                    patch.object(llama_engine, "gpu_process_memory", side_effect=splits), \
                    patch.object(llama_engine, "_http_json", side_effect=fake_http):
                result = host.load("qwen3-coder-30b")
            self.assertTrue(result["attached"])
            self.assertIn("/completion", calls)
            self.assertIn("29.6 GiB dedicated", host.info["gpu_memory"])
        finally:
            host.info = None
            gguf.unlink()


class PagedOutTests(unittest.TestCase):
    server = {"pid": 7, "gpu_layers": "all", "private_bytes": int(30.7 * GIB)}

    def test_resident_server_has_nothing_paged_out(self):
        self.assertEqual(llama_engine.paged_out_bytes(self.server, (int(29.6 * GIB), int(0.38 * GIB))), 0)

    def test_evicted_server_reports_its_gpu_memory(self):
        paged = llama_engine.paged_out_bytes(self.server, (0, int(0.36 * GIB)))
        self.assertAlmostEqual(paged / GIB, 29.34, places=1)

    def test_cpu_only_server_is_ignored(self):
        self.assertEqual(llama_engine.paged_out_bytes(dict(self.server, gpu_layers="0"), (0, 0)), 0)


if __name__ == "__main__":
    unittest.main()
