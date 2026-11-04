import math
import unittest

from surface_devicekit import classify, planner
from surface_devicekit.models import CpuInfo, LaneInfo, MemoryInfo, RawGpu, RawNpu, UnknownKeyError
from surface_devicekit.service import analyze

from tests.support import N1X_CPU, N1X_GPU, SNAPDRAGON_X_ELITE_CPU, SNAPDRAGON_X_ELITE_NPU, analyze_raw, catalog, catalog_with, device, devices, pc, raw_of, report_of

RTX = "NVIDIA GeForce RTX 4060 Laptop GPU"
TESLA = "NVIDIA Tesla T4"  # NVIDIA, but in none of the catalog's GPU families
RADEON = "AMD Radeon(TM) 780M Graphics"
ARC = "Intel(R) Arc(TM) Graphics"
RYZEN_CPU = "AMD Ryzen 7 7840U w/ Radeon 780M Graphics"
METEOR_CPU = "Intel(R) Core(TM) Ultra 7 155H"
METEOR_NPU = "Intel(R) AI Boost"
HEXAGON = RawNpu(SNAPDRAGON_X_ELITE_NPU)
SMALL = {"installed_ram_gb": 8.0, "visible_ram_gb": 7.6}


def profile_of(raw, cat=None):
    return (analyze_raw(raw) if cat is None else analyze(raw, cat)).profile


def capability(profile, capability_id):
    return next(c for c in profile.capabilities if c.id == capability_id)


def lane_of(lane_id="cpu", budget=1000.0, cap="XL", available=True, note="a note"):
    kind = {"cpu": "cpu", "npu": "npu", "gpu": "gpu-discrete"}[lane_id]
    return LaneInfo(lane_id, kind, available, "a device", "", (), budget, cap, None, note)


class RoundingAndMemoryTests(unittest.TestCase):
    def test_every_figure_is_rounded_to_two_decimals_half_away_from_zero_as_dotnet_does(self):
        # 0.145 and 1.005 are below the tie in binary, so a decimal-of-repr rounding would give 0.15 and 1.01 here
        profile = profile_of(pc(gpus=(RawGpu(RTX, "", 0.145),), installed_ram_gb=15.995, visible_ram_gb=62.995, free_disk_gb=1.005, total_disk_gb=2.675))
        self.assertEqual(
            (profile.gpus[0].vram_gb, profile.memory.installed_gb, profile.memory.visible_gb, profile.storage.free_gb, profile.storage.total_gb),
            (0.14, 16.0, 63.0, 1.0, 2.68),
        )

    def test_each_memory_figure_stands_in_for_a_missing_one(self):
        memory = lambda **changes: profile_of(pc(**changes)).memory
        self.assertEqual(memory(installed_ram_gb=128.0, visible_ram_gb=63.98), MemoryInfo(128.0, 63.98, 128))
        self.assertEqual(memory(installed_ram_gb=0.0, visible_ram_gb=15.6), MemoryInfo(15.6, 15.6, 16))
        self.assertEqual(memory(installed_ram_gb=16.0, visible_ram_gb=0.0), MemoryInfo(16.0, 16.0, 16))

    def test_memory_that_is_not_a_standard_size_is_shown_as_it_is_in_the_headline(self):
        self.assertTrue(profile_of(pc(installed_ram_gb=3.5, visible_ram_gb=3.4)).headline.endswith(" · 3.5 GB"))
        self.assertTrue(profile_of(pc(installed_ram_gb=15.6, visible_ram_gb=15.6)).headline.endswith(" · 16 GB"))


class BudgetTests(unittest.TestCase):
    def test_a_lane_gets_the_smaller_of_a_share_of_its_memory_and_the_memory_less_a_reserve(self):
        cases = [
            ("gpu-discrete", 8.0, 16.0, 7.2),
            ("gpu-discrete", 24.0, 64.0, 23.2),
            ("gpu-discrete", 0.5, 16.0, 0.0),
            ("gpu-unified", 62.5, 64.0, 60.5),
            ("gpu-unified", 2.5, 64.0, 0.5),
            ("gpu-integrated", 0.0, 15.6, 7.02),
            ("gpu-integrated", 0.0, 8.0, 2.0),
            ("gpu-integrated", 0.0, 7.9, 1.9),
            ("gpu-integrated", 0.0, 4.0, 0.0),
            ("cpu", 0.0, 63.98, 28.79),
            ("cpu", 0.0, 3.0, 0.0),
            ("npu", 0.0, 15.6, 7.02),
            ("npu", 0.0, 0.0, 0.0),
        ]
        for kind, vram, ram, expected in cases:
            with self.subTest(kind=kind, vram=vram, ram=ram):
                self.assertEqual(classify.budget(catalog(), kind, vram, ram), expected)

    def test_the_basis_fraction_and_reserve_come_from_the_catalog(self):
        def rule(**values):
            return catalog_with(lambda d: d["budget"].update({"gpu-discrete": values}))

        by_ram = rule(basis="ram", fraction=0.5, reserveGb=1.0)
        self.assertEqual(classify.budget(by_ram, "gpu-discrete", 24.0, 10.0), 5.0)
        by_vram = rule(basis="vram", fraction=0.95, reserveGb=0.8)
        self.assertEqual(classify.budget(by_vram, "gpu-discrete", 8.0, 64.0), 7.2)  # the reserve is the tighter bound
        self.assertEqual(classify.budget(by_vram, "gpu-discrete", 24.0, 64.0), 22.8)  # the share is the tighter bound


class GpuLaneTests(unittest.TestCase):
    def gpu_lane(self, *gpus, **changes):
        return profile_of(pc(gpus=gpus, **changes)).lane("gpu")

    def test_a_discrete_gpu_needs_a_two_gb_budget_which_takes_2_8_gb_of_video_memory(self):
        for vram, available in ((2.8, True), (2.79, False)):
            with self.subTest(vram):
                lane = self.gpu_lane(RawGpu(RTX, "", vram))
                self.assertEqual((lane.available, lane.budget_gb), (available, 2.0 if available else 0.0))

    def test_a_gpu_that_is_too_small_is_not_offered_and_the_note_says_how_much_it_had(self):
        lane = self.gpu_lane(RawGpu(RTX, "", 1.0))
        self.assertEqual((lane.kind, lane.available, lane.budget_gb, lane.device, lane.vendor), ("gpu-discrete", False, 0.0, RTX, "nvidia"))
        self.assertEqual(lane.note, "Only 0.2 GB of graphics memory is usable for models.")

    def test_the_note_names_where_the_memory_of_each_kind_of_gpu_comes_from(self):
        cases = {
            "discrete": (RawGpu(RTX, "", 8.0), "Dedicated video memory: 8.0 GB."),
            "unified": (RawGpu(N1X_GPU, "", 62.5), "Unified memory: the GPU and CPU share one pool, and the GPU reports 62.5 GB of it."),
            "integrated": (RawGpu(RADEON, "", 0.5), "Integrated graphics use system memory, so the budget is a share of RAM."),
        }
        for kind, (gpu, note) in cases.items():
            with self.subTest(kind):
                lane = self.gpu_lane(gpu)
                self.assertEqual((lane.kind, lane.available, lane.note), ("gpu-" + kind, True, note))

    def test_the_lane_without_a_gpu_is_an_unavailable_integrated_lane(self):
        lane = self.gpu_lane()
        self.assertEqual(
            (lane.kind, lane.available, lane.device, lane.vendor, lane.eps, lane.budget_gb, lane.class_cap, lane.note),
            ("gpu-integrated", False, "", "", (), 0.0, "M", "Windows did not report a GPU."),
        )

    def test_a_vendor_without_providers_of_its_own_gets_those_of_other(self):
        cat = catalog_with(lambda d: d["eps"]["gpu"].pop("amd"))
        lane = profile_of(pc(gpus=(RawGpu(RADEON, "", 0.5),)), cat).lane("gpu")
        self.assertEqual((lane.vendor, lane.eps), ("amd", cat.eps.gpu["other"]))

    def test_a_gpu_the_catalog_does_not_know_is_integrated_and_takes_its_vendor_from_its_name(self):
        gpu = profile_of(pc(gpus=(RawGpu("  Contoso   Graphics 3000 ", "31.0", 0.5),))).gpus[0]
        self.assertEqual((gpu.name, gpu.vendor, gpu.kind, gpu.family_id, gpu.driver), ("Contoso Graphics 3000", "other", "integrated", "unknown", "31.0"))
        self.assertEqual(profile_of(pc(gpus=(RawGpu(TESLA, "", 16.0),))).gpus[0].vendor, "nvidia")


class PrimaryGpuTests(unittest.TestCase):
    def primary(self, *gpus, cat=None):
        return profile_of(pc(gpus=gpus), cat).lane("gpu").device

    def test_a_real_nvidia_gpu_comes_before_a_gpu_with_more_memory(self):
        self.assertEqual(self.primary(RawGpu(RADEON, "", 16.0), RawGpu(RTX, "", 4.0)), RTX)
        self.assertEqual(self.primary(RawGpu(TESLA, "", 16.0), RawGpu(RTX, "", 4.0)), RTX)

    def test_any_other_gpu_that_is_not_integrated_comes_next(self):
        cat = catalog_with(lambda d: next(g for g in d["gpuFamilies"] if g["id"] == "amd-radeon").update(kind="discrete"))
        self.assertEqual(self.primary(RawGpu(ARC, "", 16.0), RawGpu(RADEON, "", 4.0), cat=cat), RADEON)

    def test_then_the_gpu_with_the_most_video_memory_and_ties_keep_the_order_windows_gave(self):
        self.assertEqual(self.primary(RawGpu(ARC, "", 1.0), RawGpu(RADEON, "", 2.0)), RADEON)
        self.assertEqual(self.primary(RawGpu("Intel(R) Arc(TM) A", "", 1.0), RawGpu("Intel(R) Arc(TM) B", "", 1.0)), "Intel(R) Arc(TM) A")


class AdapterFilterTests(unittest.TestCase):
    def test_virtual_adapters_and_blank_names_are_not_gpus(self):
        names = [
            "Microsoft Basic Render Driver", "Microsoft Basic Display Adapter", "Microsoft Remote Display Adapter", "Microsoft Hyper-V Video",
            "Virtual Desktop Monitor", "vmware svga 3d", "Parallels Display Adapter (WDDM)", "Citrix Display Driver", "Indirect Display Device",
            "DisplayLink USB Graphics", "Mirage Driver", "", "   ",
        ]
        gpus = tuple(RawGpu(name, "", 1.0) for name in names) + (RawGpu(RTX, "", 8.0),)
        profile = profile_of(pc(gpus=gpus))
        self.assertEqual([gpu.name for gpu in profile.gpus], [RTX])

    def test_a_blank_npu_is_dropped_and_names_are_cleaned_of_extra_white_space(self):
        profile = profile_of(pc(cpu=METEOR_CPU, npus=(RawNpu(""), RawNpu("  Intel(R)   AI \t Boost "), RawNpu("  "))))
        self.assertEqual([npu.name for npu in profile.npus], [METEOR_NPU])


class NpuTests(unittest.TestCase):
    def test_an_npu_takes_its_rating_from_the_chip_and_runs_language_models_from_40_tops(self):
        profile = profile_of(pc(npus=(RawNpu(SNAPDRAGON_X_ELITE_NPU, "1.0"),)))
        npu, lane = profile.npus[0], profile.lane("npu")
        self.assertEqual(
            (npu.vendor, npu.rated_tops, npu.rated_source, npu.runtime_supported, npu.llm_capable, npu.driver),
            ("qualcomm", 45.0, "vendor-rated", True, True, "1.0"),
        )
        self.assertEqual(npu.note, "Rated 45 TOPS (vendor figure).")
        self.assertEqual((lane.available, lane.eps, lane.budget_gb, lane.class_cap, lane.rated_tops), (True, ("qnn",), 7.02, "M", 45.0))

    def test_the_minimum_for_language_models_is_a_threshold_of_the_catalog_and_is_reached_at_the_rating(self):
        for minimum, capable in ((45.0, True), (45.5, False)):
            with self.subTest(minimum):
                cat = catalog_with(lambda d: d["thresholds"].update(npuMinTopsForLlm=minimum))
                self.assertEqual(profile_of(pc(npus=(HEXAGON,)), cat).npus[0].llm_capable, capable)

    def test_a_weak_npu_is_kept_off_language_models_and_the_note_says_so(self):
        npu = profile_of(pc(cpu=METEOR_CPU, npus=(RawNpu(METEOR_NPU),))).npus[0]
        self.assertEqual((npu.vendor, npu.rated_tops, npu.llm_capable), ("intel", 11.0, False))
        self.assertEqual(npu.note, "Rated 11 TOPS (vendor figure): enough for light background AI, below the 40 TOPS this kit needs for language models.")

    def test_emulation_keeps_the_rating_but_takes_the_npu_off_language_models(self):
        npu = profile_of(pc(npus=(HEXAGON,), process_arch="x64")).npus[0]
        self.assertEqual((npu.rated_tops, npu.runtime_supported, npu.llm_capable), (45.0, True, False))
        self.assertEqual(npu.note, "An x64 process on an ARM64 PC cannot load the NPU runtime. Use the ARM64 build.")

    def test_a_name_without_a_vendor_belongs_to_the_vendor_of_the_chip(self):
        npu = profile_of(pc(npus=(RawNpu("Neural Processing Unit"),))).npus[0]
        self.assertEqual((npu.vendor, npu.rated_tops, npu.llm_capable), ("qualcomm", 45.0, True))

    def test_an_npu_is_not_rated_when_its_vendor_is_not_the_chips_or_the_chip_has_no_rating(self):
        for name, cpu in (("AMD IPU Device", SNAPDRAGON_X_ELITE_CPU), (METEOR_NPU, "Contoso Octa 9000"), (METEOR_NPU, "Intel(R) Core(TM) i7-1255U")):
            with self.subTest(name=name, cpu=cpu):
                npu = profile_of(pc(cpu=cpu, npus=(RawNpu(name),))).npus[0]
                self.assertEqual((npu.rated_tops, npu.rated_source, npu.runtime_supported, npu.llm_capable), (None, "none", False, False))
                self.assertEqual(npu.note, "No rating on file for this NPU.")

    def test_the_lane_names_the_first_npu_that_can_run_models_and_otherwise_the_first_one(self):
        npus = (RawNpu("AMD IPU Device"), HEXAGON)
        usable = profile_of(pc(npus=npus)).lane("npu")
        self.assertEqual((usable.available, usable.device), (True, SNAPDRAGON_X_ELITE_NPU))
        unusable = profile_of(pc(npus=npus, process_arch="x64")).lane("npu")
        self.assertEqual(
            (unusable.available, unusable.device, unusable.vendor, unusable.eps, unusable.budget_gb, unusable.rated_tops, unusable.note),
            (False, "AMD IPU Device", "amd", catalog().eps.npu["amd"], 0.0, None, "No rating on file for this NPU."),
        )

    def test_without_an_npu_the_lane_says_whether_the_chip_should_have_one(self):
        expected = {
            SNAPDRAGON_X_ELITE_CPU: "Windows did not report an NPU for this chip. Install the NPU driver from Windows Update or the Surface driver pack.",
            "Intel(R) Core(TM) i7-1255U": "This PC has no NPU.",
            N1X_CPU: "This PC has no NPU.",  # listed, but with no runtime to wait for
        }
        for cpu, note in expected.items():
            with self.subTest(cpu):
                lane = profile_of(pc(cpu=cpu)).lane("npu")
                self.assertEqual((lane.available, lane.device, lane.vendor, lane.eps, lane.budget_gb, lane.rated_tops, lane.note), (False, "", "", (), 0.0, None, note))


class EmulationTests(unittest.TestCase):
    def test_a_process_architecture_that_differs_from_the_os_is_emulation_and_an_empty_one_is_not(self):
        cases = [
            ("arm64", "", False, "arm64"),
            ("arm64", "arm64", False, "arm64"),
            ("ARM64", "arm64", False, "arm64"),
            ("arm64", "x64", True, "x64"),
            ("x64", "X86", True, "x86"),
        ]
        for os_arch, process_arch, emulated, shown in cases:
            with self.subTest(os_arch=os_arch, process_arch=process_arch):
                os = profile_of(pc(os_arch=os_arch, process_arch=process_arch)).os
                self.assertEqual((os.arch, os.process_arch, os.emulated), (os_arch.lower(), shown, emulated))

    def test_emulation_turns_off_the_gpu_and_the_npu_but_not_the_cpu(self):
        profile = profile_of(pc(gpus=(RawGpu(RTX, "", 8.0),), npus=(HEXAGON,), process_arch="x64"))
        gpu, npu, cpu = profile.lane("gpu"), profile.lane("npu"), profile.lane("cpu")
        self.assertEqual((gpu.available, gpu.budget_gb), (False, 0.0))
        self.assertEqual(gpu.note, "An x64 process on an ARM64 PC cannot load GPU runtimes. Use the ARM64 build.")
        self.assertEqual((npu.available, npu.budget_gb), (False, 0.0))
        self.assertEqual((cpu.available, cpu.budget_gb, cpu.class_cap), (True, 7.02, "M"))
        self.assertEqual(cpu.note, "Runs under x64 emulation on ARM64, which adds overhead. The ARM64 build is faster.")
        self.assertEqual(profile.device_class, "cpu-only")

    def test_the_cpu_lane_stays_at_class_m_however_much_memory_there_is(self):
        lane = profile_of(pc(installed_ram_gb=128.0, visible_ram_gb=127.9)).lane("cpu")
        self.assertEqual((lane.class_cap, lane.budget_gb, lane.note), ("M", 57.56, "Runs on any PC; the slowest lane."))


class DeviceClassTests(unittest.TestCase):
    def test_the_device_class_is_the_first_that_applies_and_it_sets_the_order_of_the_lanes(self):
        cases = [
            ("gpu-first", ["gpu", "cpu", "npu"], pc(gpus=(RawGpu(RTX, "", 8.0),), npus=(HEXAGON,))),  # a strong NVIDIA GPU outranks a usable NPU
            ("npu-first", ["npu", "gpu", "cpu"], pc(gpus=(RawGpu(RTX, "", 1.0),), npus=(HEXAGON,))),  # a GPU with too little memory does not
            ("npu-first", ["npu", "gpu", "cpu"], pc(npus=(HEXAGON,))),
            ("igpu-first", ["gpu", "cpu", "npu"], pc(cpu=RYZEN_CPU, gpus=(RawGpu(RADEON, "", 0.5),))),
            ("igpu-first", ["gpu", "cpu", "npu"], pc(gpus=(RawGpu(TESLA, "", 16.0),))),  # NVIDIA, but integrated as far as the catalog knows
            ("cpu-only", ["cpu", "npu", "gpu"], pc()),
            ("cpu-only", ["cpu", "npu", "gpu"], pc(gpus=(RawGpu(RADEON, "", 0.5),), installed_ram_gb=4.0, visible_ram_gb=3.8)),
            ("cpu-only", ["cpu", "npu", "gpu"], pc(gpus=(RawGpu(RTX, "", 8.0),), process_arch="x64")),
        ]
        for index, (device_class, lane_ids, raw) in enumerate(cases):
            with self.subTest(index=index, device_class=device_class):
                profile = profile_of(raw)
                self.assertEqual((profile.device_class, [lane.id for lane in profile.lanes]), (device_class, lane_ids))
                self.assertEqual(profile.device_class_label, catalog().device_class_labels[device_class])

    def test_the_pc_class_is_the_first_that_applies(self):
        meteor_npu = RawNpu(METEOR_NPU)
        cases = [
            ("builder", pc(cpu=N1X_CPU, **SMALL)),  # a chip Microsoft lists for the Builder PC class wins whatever else is there
            ("copilot-plus", pc(npus=(HEXAGON,))),
            ("copilot-plus", pc(gpus=(RawGpu(RTX, "", 8.0),), npus=(HEXAGON,))),  # Copilot+ outranks the GPU class
            ("npu", pc(npus=(HEXAGON,), **SMALL)),  # too little RAM for Copilot+, but the NPU has a rating
            ("gpu", pc(cpu=METEOR_CPU, gpus=(RawGpu(RTX, "", 8.0),), npus=(meteor_npu,))),
            ("npu", pc(cpu=METEOR_CPU, npus=(meteor_npu,))),
            ("windows", pc()),  # a Copilot+ chip, but no NPU was reported
            ("windows", pc(cpu=METEOR_CPU, gpus=(RawGpu(ARC, "", 0.5),))),
            ("gpu", pc(cpu=METEOR_CPU, gpus=(RawGpu(RTX, "", 8.0),))),
        ]
        for index, (pc_class, raw) in enumerate(cases):
            with self.subTest(index=index, pc_class=pc_class):
                profile = profile_of(raw)
                self.assertEqual((profile.pc_class, profile.pc_class_label), (pc_class, catalog().pc_class_labels[pc_class]))

    def test_a_chip_listed_for_copilot_plus_is_copilot_plus_without_an_npu_rating_once_it_is_not_a_builder_pc(self):
        cat = catalog_with(lambda d: next(s for s in d["silicon"] if s["id"] == "nvidia-n1x").pop("builderPc"))
        self.assertEqual(profile_of(raw_of("nvidia-n1x-64"), cat).pc_class, "copilot-plus")
        self.assertEqual(profile_of(raw_of("nvidia-n1x-32"), cat).pc_class, "copilot-plus")
        self.assertEqual(profile_of(pc(cpu=N1X_CPU, **SMALL), cat).pc_class, "windows")  # 8 GB is under the Copilot+ minimum

    def test_a_cpu_the_catalog_does_not_know_is_described_by_its_own_name(self):
        cpu = profile_of(pc(cpu="  Contoso   Octa 9000 ", os_arch="X64")).cpu
        self.assertEqual(cpu, CpuInfo("Contoso Octa 9000", "other", "unknown", "Contoso Octa 9000", "x64", 12, False))
        self.assertEqual(profile_of(pc(cpu="Qualcomm(R) Oryon(TM) CPU")).cpu.vendor, "qualcomm")


class ProductNameTests(unittest.TestCase):
    def test_microsoft_and_for_business_are_taken_off_the_product_name_whatever_their_case(self):
        cases = {
            "Microsoft Surface Laptop Ultra": "Surface Laptop Ultra",
            "microsoft surface pro 11": "surface pro 11",
            "Surface Pro for Business": "Surface Pro",
            "Surface Pro FOR BUSINESS": "Surface Pro",
            "  Microsoft   Surface  Laptop for Business ": "Surface Laptop",
            "Microsoft": "Microsoft",
            "Dell XPS 13": "Dell XPS 13",
            "": "",
        }
        for product, expected in cases.items():
            with self.subTest(product):
                self.assertEqual(profile_of(pc(product=product)).product, expected)

    def test_a_surface_is_recognised_from_the_cleaned_name_and_an_unnamed_pc_is_a_windows_pc(self):
        self.assertTrue(profile_of(pc(product="Microsoft Surface Pro")).is_surface)
        self.assertTrue(profile_of(pc(product="microsoft surface pro")).is_surface)
        self.assertFalse(profile_of(pc(product="Dell XPS 13")).is_surface)
        unnamed = profile_of(pc(product=""))
        self.assertEqual((unnamed.label, unnamed.is_surface), ("Windows PC", False))

    def test_a_label_is_the_headline_as_given(self):
        profile = classify.classify(pc(), catalog(), "simulated", "some-id", "My label")
        self.assertEqual((profile.source, profile.simulated_id, profile.label), ("simulated", "some-id", "My label"))
        self.assertEqual(profile.headline, "My label")

    def test_a_simulated_headline_is_its_label_without_a_repeated_part(self):
        for sim in devices():
            with self.subTest(sim.id):
                headline = report_of(sim.id).profile.headline
                self.assertEqual(headline, sim.label)
                parts = headline.split(" · ")
                self.assertEqual(len(parts), len(set(parts)), headline)

    def test_the_manufacturer_and_sku_are_cleaned_of_extra_white_space(self):
        profile = profile_of(pc(manufacturer=" Microsoft  Corporation ", sku="Surface_Laptop_7 \t 2036"))
        self.assertEqual((profile.manufacturer, profile.sku), ("Microsoft Corporation", "Surface_Laptop_7 2036"))


class CapabilityTests(unittest.TestCase):
    def status(self, raw, capability_id, cat=None):
        found = capability(profile_of(raw, cat), capability_id)
        return found.status, found.lane, found.basis

    def test_storage_is_available_from_12_gb_limited_from_4_gb_and_otherwise_unavailable(self):
        cases = [(200.0, "available"), (12.0, "available"), (11.996, "available"), (11.99, "limited"), (4.0, "limited"), (3.99, "unavailable"), (0.0, "unavailable")]
        for free, status in cases:
            with self.subTest(free):
                self.assertEqual(self.status(pc(free_disk_gb=free), "storage"), (status, "", "detected"))

    def test_storage_names_the_drive_or_the_model_drive(self):
        self.assertEqual(capability(profile_of(pc(free_disk_gb=150.0)), "storage").detail, "150 GB free on C:. This kit wants 12 GB free for model downloads.")
        self.assertEqual(
            capability(profile_of(pc(free_disk_gb=150.0, model_drive="")), "storage").detail,
            "150 GB free on the model drive. This kit wants 12 GB free for model downloads.",
        )

    def test_powerpoint_is_available_when_it_is_installed_and_limited_when_it_is_not(self):
        found = lambda **changes: capability(profile_of(pc(**changes)), "powerpoint")
        self.assertEqual(found(power_point=True, power_point_version="16.0").detail, "PowerPoint 16.0 is installed, so decks can be opened and exported as images.")
        self.assertEqual(found(power_point=True).detail, "PowerPoint is installed, so decks can be opened and exported as images.")
        self.assertEqual((found(power_point=False).status, found(power_point=False).basis), ("limited", "detected"))

    def test_the_native_build_is_available_unless_the_process_is_emulated(self):
        self.assertEqual(capability(profile_of(pc()), "native-build").detail, "arm64 build on arm64 Windows.")
        emulated = capability(profile_of(pc(process_arch="x64")), "native-build")
        self.assertEqual(emulated.status, "limited")
        self.assertEqual(emulated.detail, "This x64 build is running under emulation on arm64 Windows, so NPU and GPU runtimes cannot load. Use the arm64 build.")

    def test_a_usable_npu_is_available_with_its_runtime(self):
        found = capability(profile_of(pc(npus=(HEXAGON,))), "npu-inference")
        self.assertEqual((found.status, found.lane, found.basis), ("available", "npu", "vendor-rated"))
        self.assertEqual(found.detail, f"{SNAPDRAGON_X_ELITE_NPU}. Rated 45 TOPS (vendor figure). Runtime: QNN through Foundry Local.")

    def test_an_npu_that_cannot_run_models_is_limited_when_it_has_a_rating_and_a_runtime_and_otherwise_unavailable(self):
        cases = [
            ("limited", "vendor-rated", pc(cpu=METEOR_CPU, npus=(RawNpu(METEOR_NPU),))),
            ("limited", "vendor-rated", pc(npus=(HEXAGON,), process_arch="x64")),
            ("unavailable", "detected", pc(cpu=N1X_CPU, npus=(RawNpu("NVIDIA NPU"),))),
            ("unavailable", "detected", pc(npus=(RawNpu("AMD IPU Device"),))),
            ("unavailable", "detected", pc()),
        ]
        for index, (status, basis, raw) in enumerate(cases):
            with self.subTest(index=index):
                self.assertEqual(self.status(raw, "npu-inference"), (status, "", basis))

    def test_without_an_npu_the_capability_repeats_the_note_of_the_lane(self):
        profile = profile_of(pc(cpu="Intel(R) Core(TM) i7-1255U"))
        self.assertEqual(capability(profile, "npu-inference").detail, profile.lane("npu").note)

    def test_a_gpu_is_available_when_it_has_its_own_memory_limited_when_it_shares_system_memory_and_unavailable_without_one(self):
        self.assertEqual(self.status(pc(gpus=(RawGpu(RTX, "", 8.0),)), "gpu-inference"), ("available", "gpu", "detected"))
        self.assertEqual(self.status(pc(gpus=(RawGpu(N1X_GPU, "", 62.5),)), "gpu-inference"), ("available", "gpu", "detected"))
        self.assertEqual(self.status(pc(gpus=(RawGpu(RADEON, "", 0.5),)), "gpu-inference"), ("limited", "gpu", "estimated"))
        self.assertEqual(self.status(pc(gpus=(RawGpu(RTX, "", 1.0),)), "gpu-inference"), ("unavailable", "", "detected"))
        self.assertEqual(self.status(pc(), "gpu-inference"), ("unavailable", "", "detected"))
        self.assertEqual(capability(profile_of(pc()), "gpu-inference").detail, "Windows did not report a GPU.")

    def test_the_cpu_capability_is_always_available(self):
        found = capability(profile_of(pc()), "cpu-inference")
        self.assertEqual((found.status, found.lane, found.basis), ("available", "cpu", "estimated"))
        self.assertEqual(found.detail, f"{SNAPDRAGON_X_ELITE_CPU}: 7.0 GB of RAM available for model weights. Runs on any PC; the slowest lane.")

    def test_a_workload_is_limited_when_its_model_is_weaker_than_the_one_that_counts_as_full_capability(self):
        # structured-json counts as full capability from qwen2.5-7b, which needs more than the 7 GB a 16 GB CPU lane has
        self.assertEqual(self.status(pc(), "structured-json"), ("limited", "cpu", "estimated"))
        self.assertEqual(self.status(pc(installed_ram_gb=32.0, visible_ram_gb=31.5), "structured-json"), ("available", "cpu", "estimated"))

    def test_a_workload_nothing_fits_is_unavailable_and_repeats_the_plan_summary(self):
        profile = profile_of(pc(installed_ram_gb=2.0, visible_ram_gb=1.9))
        found = capability(profile, "structured-json")
        self.assertEqual((found.status, found.lane, found.basis, found.detail), ("unavailable", "", "estimated", "Nothing for this workload fits this device."))

    def test_large_models_are_available_where_a_lane_has_room_for_one(self):
        found = capability(profile_of(raw_of("nvidia-n1x-128")), "large-models")
        self.assertEqual((found.status, found.lane, found.basis), ("available", "gpu", "estimated"))
        self.assertRegex(found.detail, r"^.+ fits on the GPU: it needs about [\d.]+ GB and the lane has 60\.5 GB\.$")

    def test_large_models_are_unavailable_when_no_lane_has_room_and_the_note_gives_the_numbers(self):
        found = capability(profile_of(pc()), "large-models")
        self.assertEqual((found.status, found.lane, found.basis), ("unavailable", "", "estimated"))
        self.assertRegex(found.detail, r"^A 12B-class model needs about [\d.]+ GB on a GPU\. The best lane here has 7\.0 GB, so the plan stays with smaller models\.$")

    def test_copilot_plus_is_unavailable_for_a_chip_outside_the_class(self):
        profile = profile_of(pc(cpu=METEOR_CPU, installed_ram_gb=32.0, visible_ram_gb=31.5))
        found = capability(profile, "copilot-plus")
        self.assertEqual((found.status, found.basis), ("unavailable", "catalog"))
        self.assertEqual(found.detail, f"{profile.cpu.family_label} is not a Copilot+ PC class chip.")
        self.assertEqual(capability(profile_of(pc(cpu="Contoso Octa 9000")), "copilot-plus").detail, "Contoso Octa 9000 is not a Copilot+ PC class chip.")

    def test_copilot_plus_is_unavailable_below_16_gb_of_ram_whatever_the_chip_and_npu(self):
        found = capability(profile_of(pc(npus=(HEXAGON,), **SMALL)), "copilot-plus")
        self.assertEqual((found.status, found.basis), ("unavailable", "detected"))
        self.assertEqual(found.detail, "Copilot+ PCs need at least 16 GB of RAM; this one has 8 GB.")

    def test_copilot_plus_for_a_chip_that_depends_on_the_sku_needs_an_npu_of_40_tops(self):
        with_npu = capability(profile_of(pc(npus=(HEXAGON,))), "copilot-plus")
        self.assertEqual((with_npu.status, with_npu.basis), ("available", "vendor-rated"))
        self.assertRegex(with_npu.detail, r" with an NPU rated 45 TOPS and 16 GB of RAM\. Confirm on the exact SKU\.$")
        without_npu = capability(profile_of(pc()), "copilot-plus")
        self.assertEqual((without_npu.status, without_npu.basis), ("unavailable", "vendor-rated"))
        self.assertEqual(without_npu.detail, "Copilot+ PCs need an NPU rated at 40 TOPS or more; none was found here.")

    def test_copilot_plus_for_a_chip_microsoft_lists_does_not_depend_on_an_npu_and_quotes_the_catalog(self):
        found = capability(profile_of(raw_of("nvidia-n1x-64")), "copilot-plus")
        self.assertEqual((found.status, found.basis), ("available", "catalog"))
        self.assertEqual(found.detail, f"{catalog().match_silicon(N1X_CPU).copilot_plus_note} Confirm on the exact SKU.")
        plain = catalog_with(lambda d: next(s for s in d["silicon"] if s["id"] == "nvidia-n1x").pop("copilotPlusNote", None))
        family = profile_of(raw_of("nvidia-n1x-64"), plain).cpu.family_label
        self.assertEqual(capability(profile_of(raw_of("nvidia-n1x-64"), plain), "copilot-plus").detail, f"Microsoft lists {family} devices as Copilot+ PCs. Confirm on the exact SKU.")


class PlannerTests(unittest.TestCase):
    def setUp(self):
        self.catalog = catalog()
        self.workload = self.catalog.workloads["structured-json"]

    def test_a_model_fits_when_its_size_times_the_overhead_plus_the_constant_is_within_the_budget(self):
        def one_model_of_1_gb(d):
            d["models"]["qwen2.5-0.5b"]["sizeMb"] = {"gpu": 1024, "cpu": 1024}
            d["workloads"]["structured-json"]["candidates"] = ["qwen2.5-0.5b"]

        cat = catalog_with(one_model_of_1_gb)
        workload = cat.workloads["structured-json"]
        required = cat.load_cost(1.0)
        self.assertAlmostEqual(required, 1.65)
        fits = planner.plan_lane(lane_of(budget=required), workload, cat)
        misses = planner.plan_lane(lane_of(budget=math.nextafter(required, 0.0)), workload, cat)
        self.assertEqual((fits.choice.alias, fits.choice.size_gb, fits.choice.required_gb), ("qwen2.5-0.5b", 1.0, 1.65))
        self.assertEqual((misses.choice, misses.ladder), (None, ()))

    def test_a_model_above_the_class_cap_of_the_lane_is_left_out_even_when_it_fits(self):
        classes, models = self.catalog.model_classes, self.catalog.models
        open_lane = planner.plan_lane(lane_of(cap="XL"), self.workload, self.catalog)
        capped = planner.plan_lane(lane_of(cap="S"), self.workload, self.catalog)
        self.assertGreater(len(open_lane.ladder), len(capped.ladder))
        self.assertTrue(capped.ladder)
        self.assertEqual(
            [m.alias for m in capped.ladder],
            [m.alias for m in open_lane.ladder if classes.rank(models[m.alias].class_) <= classes.rank("S")],
        )

    def test_the_ladder_follows_the_candidate_order_and_its_first_entry_is_the_choice(self):
        lane = planner.plan_lane(lane_of(), self.workload, self.catalog)
        self.assertEqual([m.alias for m in lane.ladder], list(self.workload.candidates))
        self.assertIs(lane.choice, lane.ladder[0])
        self.assertEqual(lane.reason, "")

    def test_a_lane_that_cannot_run_models_has_no_ladder_and_gives_its_own_reason(self):
        lane = planner.plan_lane(lane_of(available=False, budget=1000.0, note="No GPU."), self.workload, self.catalog)
        self.assertEqual((lane.choice, lane.ladder, lane.reason), (None, (), "No GPU."))

    def test_an_available_lane_with_too_little_memory_says_what_it_has(self):
        lane = planner.plan_lane(lane_of(budget=0.5), self.workload, self.catalog)
        self.assertEqual((lane.choice, lane.ladder, lane.reason), (None, (), "Nothing in the list fits 0.5 GB on this lane."))

    def test_the_lane_plan_keeps_the_lane_it_was_made_for(self):
        lane = planner.plan_lane(lane_of("gpu", budget=24.0), self.workload, self.catalog)
        self.assertEqual((lane.lane, lane.kind, lane.budget_gb), ("gpu", "gpu-discrete", 24.0))

    def test_only_models_planned_for_an_npu_are_flagged_as_size_estimates(self):
        estimated = {
            lane_id: {m.size_estimated for m in planner.plan_lane(lane_of(lane_id), self.workload, self.catalog).ladder} for lane_id in ("npu", "gpu", "cpu")
        }
        self.assertEqual(estimated, {"npu": {True}, "gpu": {False}, "cpu": {False}})

    def test_the_primary_lane_is_the_first_lane_with_a_model_and_the_others_are_fallbacks(self):
        plan = analyze_raw(raw_of("nvidia-n1x-128")).plans["structured-json"]
        self.assertEqual((plan.primary_lane, plan.primary.alias), ("gpu", "qwen2.5-coder-14b"))
        self.assertEqual([lane.lane for lane in plan.lanes], ["gpu", "cpu", "npu"])
        fallback = next(lane for lane in plan.lanes if lane.lane == "cpu").choice
        self.assertEqual(
            plan.summary,
            f"{plan.primary.title} on the GPU ({plan.primary.size_gb:.1f} GB model file). Fallback: {fallback.title} on the CPU.",
        )

    def test_a_plan_with_no_model_anywhere_has_no_primary(self):
        plan = analyze_raw(pc(installed_ram_gb=2.0, visible_ram_gb=1.9)).plans["structured-json"]
        self.assertEqual((plan.primary_lane, plan.primary, plan.summary), (None, None, "Nothing for this workload fits this device."))

    def test_an_estimated_size_is_named_in_the_summary(self):
        plan = analyze_raw(pc(npus=(HEXAGON,))).plans["structured-json"]
        self.assertEqual(plan.primary_lane, "npu")
        self.assertIn(", size estimated)", plan.summary)

    def test_an_unknown_workload_is_named_with_the_known_ones(self):
        profile = profile_of(pc())
        with self.assertRaises(UnknownKeyError) as refused:
            planner.plan(profile, self.catalog, "nope")
        self.assertEqual(str(refused.exception), "Unknown workload 'nope'. Known: structured-json, chat, code, reasoning, speech-to-text, embeddings, vision")

    def test_a_candidate_that_is_not_a_model_is_skipped(self):
        cat = catalog_with(lambda d: d["workloads"]["chat"]["candidates"].insert(0, "ghost-model"))
        plan = planner.plan(classify.classify(raw_of("nvidia-n1x-128"), cat), cat, "chat")
        aliases = {m.alias for lane in plan.lanes for m in lane.ladder}
        self.assertTrue(aliases)
        self.assertNotIn("ghost-model", aliases)

    def test_a_candidate_that_is_not_a_model_does_not_stop_the_capability_matrix(self):
        cat = catalog_with(lambda d: d["workloads"]["chat"]["candidates"].insert(0, "ghost-model"))
        for device_id, large_models in (("nvidia-n1x-128", "available"), ("snapdragon-x-elite-16", "unavailable")):
            with self.subTest(device_id):
                sim = device(device_id)
                report = analyze(sim.raw, cat, "simulated", sim.id, sim.label)
                found = {c.id: c.status for c in report.profile.capabilities}
                self.assertEqual(found["large-models"], large_models)
