import unittest

from surface_devicekit.report import render

from tests.support import SNAPDRAGON_X_ELITE_CPU, analyze_raw, catalog, devices, pc, raw_of, report_of

N1X_128_REPORT = """\
Surface Laptop Ultra · RTX Spark N1X · 128 GB (measured)  [SIMULATED: display and planning only]
  Class      Builder PC · GPU-first
  OS         Windows 11 25H2 (build 26200) · arm64 (native arm64 build)
  CPU        NVIDIA RTX Spark N1X (6144-core GPU, 20-core CPU) · 20 logical cores
  GPU        NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU) · unified · 62.5 GB
  NPU        NVIDIA NPU · no rating · not used for language models
  Memory     128 GB installed · 64 GB visible to Windows
  Storage    200 GB free of 512 GB on C:

Lanes, preferred first
  GPU  budget  60.5 GB · up to XL-class · EP cuda, nvtensorrtrtx, webgpu, dml
       NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU) — Unified memory: the GPU and CPU share one pool, and the GPU reports 62.5 GB of it.
  CPU  budget  28.8 GB · up to M-class · EP cpu
       NVIDIA RTX Spark N1X (6144-core GPU, 20-core CPU) — Runs on any PC; the slowest lane.
  NPU  not available
       NVIDIA NPU — An NVIDIA NPU device is present, but Foundry Local has no execution provider for it, so language models do not use it.

Capabilities
  [no]      NPU language models              NVIDIA NPU. An NVIDIA NPU device is present, but Foundry Local has no execution provider for it, so language models do not use it.  [detected]
  [ok]      GPU language models              NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU): 60.5 GB for model weights, up to XL-class models. Unified memory: the GPU and CPU share one pool, and the GPU reports 62.5 GB of it.  [detected]
  [ok]      CPU language models              NVIDIA RTX Spark N1X (6144-core GPU, 20-core CPU): 28.8 GB of RAM available for model weights. Runs on any PC; the slowest lane.  [estimated]
  [ok]      Structured JSON generation       Qwen2.5-Coder 14B on the GPU (8.8 GB model file). Fallback: Qwen2.5 7B on the CPU.  [estimated]
  [ok]      General chat and writing         Qwen2.5 14B on the GPU (8.8 GB model file). Fallback: Qwen2.5 7B on the CPU.  [estimated]
  [ok]      Code and structured output       Qwen2.5-Coder 14B on the GPU (8.8 GB model file). Fallback: Qwen2.5-Coder 7B on the CPU.  [estimated]
  [ok]      Multi-step reasoning             gpt-oss 20B on the GPU (11.1 GB model file). Fallback: DeepSeek-R1 Distill Qwen 7B on the CPU.  [estimated]
  [ok]      Speech to text                   Whisper medium on the GPU (0.6 GB model file). Fallback: Whisper medium on the CPU.  [estimated]
  [ok]      Embeddings for search and RAG    Qwen3 Embedding 0.6B on the GPU (0.5 GB model file). Fallback: Qwen3 Embedding 0.6B on the CPU.  [estimated]
  [ok]      Vision and image understanding   Qwen3-VL 8B on the GPU (6.0 GB model file). Fallback: Qwen3-VL 8B on the CPU.  [estimated]
  [ok]      Large models (12B and up)        Qwen2.5 14B fits on the GPU: it needs about 10.6 GB and the lane has 60.5 GB.  [estimated]
  [ok]      Copilot+ PC class                Microsoft's Surface Laptop Ultra page answers yes to "Is Surface Laptop Ultra a Copilot+ PC?" and its Hybrid Intelligence post lists the device as a Builder PC. Confirm on the exact SKU.  [catalog]
  [ok]      Room for models                  200 GB free on C:. This kit wants 12 GB free for model downloads.  [detected]
  [ok]      Native build                     arm64 build on arm64 Windows.  [detected]
  [ok]      PowerPoint on this PC            PowerPoint 16.0 is installed, so decks can be opened and exported as images.  [detected]

Plans
  structured-json  Qwen2.5-Coder 14B on the GPU (8.8 GB model file). Fallback: Qwen2.5 7B on the CPU.
  chat             Qwen2.5 14B on the GPU (8.8 GB model file). Fallback: Qwen2.5 7B on the CPU.
  code             Qwen2.5-Coder 14B on the GPU (8.8 GB model file). Fallback: Qwen2.5-Coder 7B on the CPU.
  reasoning        gpt-oss 20B on the GPU (11.1 GB model file). Fallback: DeepSeek-R1 Distill Qwen 7B on the CPU.
  speech-to-text   Whisper medium on the GPU (0.6 GB model file). Fallback: Whisper medium on the CPU.
  embeddings       Qwen3 Embedding 0.6B on the GPU (0.5 GB model file). Fallback: Qwen3 Embedding 0.6B on the CPU.
  vision           Qwen3-VL 8B on the GPU (6.0 GB model file). Fallback: Qwen3-VL 8B on the CPU.
"""

EMULATED_REPORT = """\
Snapdragon X Elite · 16 GB, x64 build under emulation  [SIMULATED: display and planning only]
  Class      Copilot+ PC class · CPU-only
  OS         Windows 11 25H2 (build 26200) · arm64 (this x64 build is emulated)
  CPU        Snapdragon(R) X Elite - X1E80100 - Qualcomm(R) Oryon(TM) CPU · 12 logical cores
  GPU        Qualcomm(R) Adreno(TM) X1-85 GPU · integrated
  NPU        Snapdragon(R) X Elite - X1E80100 - Qualcomm(R) Hexagon(TM) NPU · rated 45 TOPS (vendor figure) · not used for language models
  Memory     16 GB installed · 15.6 GB visible to Windows
  Storage    200 GB free of 512 GB on C:

Lanes, preferred first
  CPU  budget   7.0 GB · up to M-class · EP cpu
       Snapdragon(R) X Elite - X1E80100 - Qualcomm(R) Oryon(TM) CPU — Runs under x64 emulation on ARM64, which adds overhead. The ARM64 build is faster.
  NPU  not available
       Snapdragon(R) X Elite - X1E80100 - Qualcomm(R) Hexagon(TM) NPU — An x64 process on an ARM64 PC cannot load the NPU runtime. Use the ARM64 build.
  GPU  not available
       Qualcomm(R) Adreno(TM) X1-85 GPU — An x64 process on an ARM64 PC cannot load GPU runtimes. Use the ARM64 build.

Capabilities
  [limited] NPU language models              Snapdragon(R) X Elite - X1E80100 - Qualcomm(R) Hexagon(TM) NPU. An x64 process on an ARM64 PC cannot load the NPU runtime. Use the ARM64 build.  [vendor-rated]
  [no]      GPU language models              An x64 process on an ARM64 PC cannot load GPU runtimes. Use the ARM64 build.  [detected]
  [ok]      CPU language models              Snapdragon(R) X Elite - X1E80100 - Qualcomm(R) Oryon(TM) CPU: 7.0 GB of RAM available for model weights. Runs under x64 emulation on ARM64, which adds overhead. The ARM64 build is faster.  [estimated]
  [limited] Structured JSON generation       Phi-4 mini on the CPU (4.8 GB model file)  [estimated]
  [limited] General chat and writing         Phi-4 mini on the CPU (4.8 GB model file)  [estimated]
  [limited] Code and structured output       Qwen2.5-Coder 1.5B on the CPU (1.8 GB model file)  [estimated]
  [limited] Multi-step reasoning             Phi-4 mini reasoning on the CPU (4.5 GB model file)  [estimated]
  [ok]      Speech to text                   Whisper medium on the CPU (0.9 GB model file)  [estimated]
  [ok]      Embeddings for search and RAG    Qwen3 Embedding 0.6B on the CPU (0.5 GB model file)  [estimated]
  [ok]      Vision and image understanding   Qwen3-VL 8B on the CPU (5.0 GB model file)  [estimated]
  [no]      Large models (12B and up)        A 12B-class model needs about 10.1 GB on a GPU. The best lane here has 7.0 GB, so the plan stays with smaller models.  [estimated]
  [ok]      Copilot+ PC class                Snapdragon X with an NPU rated 45 TOPS and 16 GB of RAM. Confirm on the exact SKU.  [vendor-rated]
  [ok]      Room for models                  200 GB free on C:. This kit wants 12 GB free for model downloads.  [detected]
  [limited] Native build                     This x64 build is running under emulation on arm64 Windows, so NPU and GPU runtimes cannot load. Use the arm64 build.  [detected]
  [ok]      PowerPoint on this PC            PowerPoint 16.0 is installed, so decks can be opened and exported as images.  [detected]

Plans
  structured-json  Phi-4 mini on the CPU (4.8 GB model file)
  chat             Phi-4 mini on the CPU (4.8 GB model file)
  code             Qwen2.5-Coder 1.5B on the CPU (1.8 GB model file)
  reasoning        Phi-4 mini reasoning on the CPU (4.5 GB model file)
  speech-to-text   Whisper medium on the CPU (0.9 GB model file)
  embeddings       Qwen3 Embedding 0.6B on the CPU (0.5 GB model file)
  vision           Qwen3-VL 8B on the CPU (5.0 GB model file)
"""


class RenderEveryDeviceTests(unittest.TestCase):
    def test_the_report_renders_for_every_simulated_device(self):
        for sim in devices():
            with self.subTest(sim.id):
                report = report_of(sim.id)
                text = render(report, catalog())
                lines = text.split("\n")
                self.assertEqual(lines[0], f"{report.profile.headline}  [SIMULATED: display and planning only]")
                self.assertTrue(text.endswith("\n"))
                self.assertNotIn("\r", text)
                self.assertNotIn("None", text)
                for heading in ("Lanes, preferred first", "Capabilities", "Plans"):
                    self.assertIn(heading, lines)
                for capability in report.profile.capabilities:
                    self.assertIn(f"{capability.title:<32} {capability.detail}  [{capability.basis}]", text)
                for workload_id, plan in report.plans.items():
                    self.assertIn(f"  {workload_id:<16} {plan.summary}", lines)

    def test_lanes_are_listed_in_the_order_the_device_prefers(self):
        for sim in devices():
            with self.subTest(sim.id):
                report = report_of(sim.id)
                lines = render(report, catalog()).split("\n")
                start = lines.index("Lanes, preferred first") + 1
                block = lines[start : lines.index("", start)]
                labels = [line[2:5] for line in block if not line.startswith("       ")]
                self.assertEqual(labels, [catalog().lanes[lane.id].label for lane in report.profile.lanes])

    def test_the_catalog_is_optional(self):
        report = report_of("nvidia-n1x-128")
        self.assertEqual(render(report), render(report, catalog()))


class RenderedTextTests(unittest.TestCase):
    def test_a_measured_gpu_first_builder_pc_reads_as_the_c_sharp_report_does(self):
        self.assertMultiLineEqual(render(report_of("nvidia-n1x-128"), catalog()), N1X_128_REPORT)

    def test_an_emulated_build_shows_limited_and_unavailable_marks(self):
        self.assertMultiLineEqual(render(report_of("snapdragon-x-elite-16-emulated"), catalog()), EMULATED_REPORT)

    def test_a_detected_report_is_not_marked_as_simulated(self):
        text = render(analyze_raw(raw_of("nvidia-n1x-128")), catalog())
        self.assertNotIn("SIMULATED", text)
        self.assertTrue(text.startswith("Surface Laptop Ultra · NVIDIA RTX Spark (N1X) · 128 GB\n"))

    def test_a_pc_without_a_gpu_or_an_npu_says_so(self):
        text = render(analyze_raw(pc(SNAPDRAGON_X_ELITE_CPU)), catalog())
        self.assertIn("\n  GPU        none reported\n", text)
        self.assertIn("\n  NPU        none reported\n", text)
        self.assertIn("  GPU  not available\n        — Windows did not report a GPU.\n", text)

    def test_capabilities_and_plans_can_be_left_out(self):
        report = report_of("nvidia-n1x-128")
        without_capabilities = render(report, catalog(), capabilities=False)
        self.assertNotIn("Capabilities", without_capabilities)
        self.assertIn("Plans", without_capabilities)
        without_plans = render(report, catalog(), plans=False)
        self.assertIn("Capabilities", without_plans)
        self.assertNotIn("Plans", without_plans)
        lanes_only = render(report, catalog(), capabilities=False, plans=False)
        self.assertTrue(lanes_only.endswith("so language models do not use it.\n"))
