import dataclasses
import os
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import patch

from surface_devicekit import service as service_module
from surface_devicekit.catalog import CATALOG_ENV, Catalog
from surface_devicekit.models import SoftwareInfo, StorageInfo, UnknownKeyError
from surface_devicekit.service import SIMULATE_ENV, DeviceDetectionService, analyze, get_service

from tests.support import GOLDEN_PATH, FakeProbe, IsolatedEnvironment, catalog, device, devices, pc

LOCAL_FACTS = ("model_drive", "free_disk_gb", "total_disk_gb", "nvidia_smi", "power_point", "power_point_version")

# This PC, as the fake probe reports it: all six local facts differ from those of the simulated devices.
LOCAL = pc(model_drive="D:", free_disk_gb=7.5, total_disk_gb=476.9, nvidia_smi=True, power_point=False, power_point_version="")
SIMULATED_ID = "snapdragon-x-elite-16"


def capability(report, capability_id):
    return next(c for c in report.profile.capabilities if c.id == capability_id)


class ServiceTestCase(IsolatedEnvironment):
    def make(self, *outcomes) -> tuple[DeviceDetectionService, FakeProbe]:
        probe = FakeProbe(*(outcomes or (LOCAL,)))
        return DeviceDetectionService(catalog(), probe), probe


class OverlayTests(ServiceTestCase):
    def test_simulate_overlays_the_six_local_facts_and_nothing_else(self):
        service, _ = self.make()
        sim = device(SIMULATED_ID).raw
        for attribute in LOCAL_FACTS:
            self.assertNotEqual(getattr(LOCAL, attribute), getattr(sim, attribute), attribute)
        raw = service.simulate(SIMULATED_ID).profile.raw
        for attribute in LOCAL_FACTS:
            self.assertEqual(getattr(raw, attribute), getattr(LOCAL, attribute), attribute)
        self.assertEqual(dataclasses.replace(raw, **{a: getattr(sim, a) for a in LOCAL_FACTS}), sim)

    def test_the_overlay_reaches_the_report(self):
        service, _ = self.make()
        report = service.simulate(SIMULATED_ID)
        self.assertEqual(report.profile.storage, StorageInfo("D:", 7.5, 476.9))
        self.assertEqual(report.profile.software, SoftwareInfo(True, False, ""))
        self.assertEqual(capability(report, "storage").status, "limited")
        self.assertEqual(capability(report, "powerpoint").status, "limited")

    def test_the_overlay_leaves_the_hardware_answers_alone(self):
        service, _ = self.make()
        overlaid = service.simulate(SIMULATED_ID)
        sim = device(SIMULATED_ID)
        pure = analyze(sim.raw, catalog(), "simulated", sim.id, sim.label)
        self.assertEqual((overlaid.profile.lanes, overlaid.profile.gpus, overlaid.profile.npus), (pure.profile.lanes, pure.profile.gpus, pure.profile.npus))
        self.assertEqual(overlaid.profile.device_class, pure.profile.device_class)
        self.assertEqual(dict(overlaid.plans), dict(pure.plans))

    def test_without_local_facts_the_simulated_raw_is_used_as_it_is_and_nothing_is_probed(self):
        service, probe = self.make()
        report = service.simulate(SIMULATED_ID, keep_local_facts=False)
        self.assertEqual(report.profile.raw, device(SIMULATED_ID).raw)
        self.assertEqual(report.profile.storage, StorageInfo("C:", 200.0, 512.0))
        self.assertEqual(probe.calls, 0)

    def test_analyze_adds_nothing_from_this_pc(self):
        sim = device(SIMULATED_ID)
        with patch.object(service_module, "probe_this_pc", side_effect=AssertionError("the probe must not run")):
            report = analyze(sim.raw, catalog(), "simulated", sim.id, sim.label)
        self.assertEqual(report.profile.raw, sim.raw)
        self.assertEqual(capability(report, "storage").status, "available")
        self.assertEqual((report.profile.source, report.profile.simulated_id, report.profile.label), ("simulated", sim.id, sim.label))

    def test_analyze_ignores_a_simulation_that_is_active_elsewhere(self):
        service, _ = self.make()
        service.simulate(SIMULATED_ID)
        sim = device(SIMULATED_ID)
        self.assertEqual(analyze(sim.raw, catalog()).profile.storage, StorageInfo("C:", 200.0, 512.0))

    def test_analyze_defaults_to_a_detected_report_without_a_label_override(self):
        report = analyze(device(SIMULATED_ID).raw)
        self.assertEqual((report.profile.source, report.profile.simulated_id), ("detected", None))
        self.assertEqual(report.profile.label, "Surface Pro, 11th Edition")
        self.assertEqual(report.catalog_version, Catalog.default().catalog_version)


class DetectionTests(ServiceTestCase):
    def test_the_probe_runs_once_and_its_report_is_cached(self):
        service, probe = self.make()
        first = service.detected
        self.assertIs(service.detected, first)
        self.assertEqual(probe.calls, 1)
        self.assertEqual((first.profile.source, first.profile.simulated_id), ("detected", None))

    def test_nothing_is_probed_until_asked(self):
        _, probe = self.make()
        self.assertEqual(probe.calls, 0)

    def test_a_failed_probe_is_not_cached(self):
        service, probe = self.make(OSError("no registry"), LOCAL)
        with self.assertRaisesRegex(OSError, "no registry"):
            service.detected
        self.assertEqual(service.detected.profile.storage.drive, "D:")
        self.assertEqual(probe.calls, 2)

    def test_the_effective_report_is_the_detected_one_until_a_simulation_is_set(self):
        service, _ = self.make()
        self.assertFalse(service.is_simulated)
        self.assertIs(service.effective, service.detected)

    def test_simulate_makes_the_simulated_device_the_effective_one(self):
        service, _ = self.make()
        report = service.simulate("NVIDIA-N1X-128")
        sim = device("nvidia-n1x-128")
        self.assertTrue(service.is_simulated)
        self.assertIs(service.effective, report)
        self.assertEqual((report.profile.source, report.profile.simulated_id, report.profile.label), ("simulated", sim.id, sim.label))
        self.assertNotEqual(service.detected.profile.source, "simulated")

    def test_clear_simulation_goes_back_to_the_detected_report(self):
        service, _ = self.make()
        service.simulate(SIMULATED_ID)
        service.clear_simulation()
        self.assertFalse(service.is_simulated)
        self.assertIs(service.effective, service.detected)

    def test_plan_follows_the_effective_report(self):
        service, _ = self.make()
        self.assertIs(service.plan("chat"), service.detected.plans["chat"])
        service.simulate("nvidia-n1x-128")
        plan = service.plan("structured-json")
        self.assertEqual((plan.primary_lane, plan.primary.alias), ("gpu", "qwen2.5-coder-14b"))

    def test_an_unknown_device_or_workload_is_an_error_that_says_so(self):
        service, _ = self.make()
        with self.assertRaises(UnknownKeyError) as device_error:
            service.simulate("no-such-device")
        self.assertEqual(str(device_error.exception), "No simulated device 'no-such-device'.")
        with self.assertRaises(UnknownKeyError) as workload_error:
            service.plan("no-such-workload")
        self.assertTrue(str(workload_error.exception).startswith("Unknown workload 'no-such-workload'. Known: structured-json, chat,"))
        self.assertFalse(service.is_simulated)

    def test_a_pc_that_cannot_be_probed_can_still_be_simulated_without_its_local_facts(self):
        service, _ = self.make(OSError("no registry"))
        report = service.simulate(SIMULATED_ID)
        self.assertEqual(report.profile.raw, device(SIMULATED_ID).raw)
        with self.assertRaises(OSError):
            service.detected

    def test_the_default_probe_is_told_where_models_will_be_stored(self):
        with patch.object(service_module, "probe_this_pc", return_value=LOCAL) as probe:
            DeviceDetectionService(catalog(), model_dir="D:\\Models").detected
            DeviceDetectionService(catalog()).detected
        self.assertEqual([call.args for call in probe.call_args_list], [("D:\\Models",), (None,)])

    def test_a_custom_device_list_replaces_the_shipped_one(self):
        only = device("intel-pentium-8")
        service = DeviceDetectionService(catalog(), FakeProbe(LOCAL), simulated_devices=[only])
        self.assertEqual(service.simulated_devices, (only,))
        self.assertEqual(service.simulate("INTEL-PENTIUM-8").profile.simulated_id, "intel-pentium-8")
        with self.assertRaises(UnknownKeyError):
            service.simulate(SIMULATED_ID)

    def test_the_shipped_devices_are_the_default_list(self):
        service, _ = self.make()
        self.assertEqual(service.simulated_devices, devices())


class RefreshAndListenerTests(ServiceTestCase):
    def test_refresh_probes_again_and_returns_the_new_report(self):
        service, probe = self.make(LOCAL, dataclasses.replace(LOCAL, free_disk_gb=99.0))
        before = service.detected
        after = service.refresh()
        self.assertEqual((before.profile.storage.free_gb, after.profile.storage.free_gb), (7.5, 99.0))
        self.assertIs(service.detected, after)
        self.assertEqual(probe.calls, 2)

    def test_refresh_gives_an_active_simulation_the_new_local_facts(self):
        service, _ = self.make(LOCAL, dataclasses.replace(LOCAL, free_disk_gb=99.0))
        service.simulate(SIMULATED_ID)
        self.assertEqual(service.effective.profile.storage.free_gb, 7.5)
        service.refresh()
        self.assertEqual(service.effective.profile.simulated_id, SIMULATED_ID)
        self.assertEqual(service.effective.profile.storage.free_gb, 99.0)
        self.assertEqual(service.detected.profile.storage.free_gb, 99.0)

    def test_refresh_keeps_a_simulation_that_was_started_without_local_facts_as_it_was(self):
        service, _ = self.make(LOCAL, dataclasses.replace(LOCAL, free_disk_gb=99.0))
        service.simulate(SIMULATED_ID, keep_local_facts=False)
        service.refresh()
        self.assertEqual(service.effective.profile.raw, device(SIMULATED_ID).raw)

    def test_refresh_after_the_simulation_was_cleared_leaves_the_detected_report_in_charge(self):
        service, _ = self.make(LOCAL, dataclasses.replace(LOCAL, free_disk_gb=99.0))
        service.simulate(SIMULATED_ID)
        service.clear_simulation()
        service.refresh()
        self.assertFalse(service.is_simulated)
        self.assertIs(service.effective, service.detected)

    def test_listeners_hear_about_every_change_and_refresh(self):
        service, _ = self.make()
        heard = []
        service.subscribe(heard.append)
        service.simulate(SIMULATED_ID)
        service.clear_simulation()
        service.refresh()
        self.assertEqual(len(heard), 3)
        self.assertTrue(all(item is service for item in heard))

    def test_a_listener_can_unsubscribe(self):
        service, _ = self.make()
        heard = []
        unsubscribe = service.subscribe(heard.append)
        service.simulate(SIMULATED_ID)
        unsubscribe()
        unsubscribe()
        service.clear_simulation()
        self.assertEqual(len(heard), 1)

    def test_a_listener_may_read_the_service_it_is_told_about(self):
        service, _ = self.make()
        seen = []
        service.subscribe(lambda s: seen.append(s.effective.profile.source))
        service.simulate(SIMULATED_ID)
        service.clear_simulation()
        self.assertEqual(seen, ["simulated", "detected"])


class EnvironmentTests(ServiceTestCase):
    def test_the_simulate_variable_starts_the_service_in_simulation(self):
        os.environ[SIMULATE_ENV] = "nvidia-n1x-128"
        service, probe = self.make()
        self.assertTrue(service.is_simulated)
        self.assertEqual(service.effective.profile.simulated_id, "nvidia-n1x-128")
        self.assertEqual(service.effective.profile.storage.drive, "D:")
        self.assertEqual(probe.calls, 1)

    def test_the_value_is_trimmed_and_matched_without_regard_to_case(self):
        os.environ[SIMULATE_ENV] = "  Snapdragon-X-Elite-16 \t"
        service, _ = self.make()
        self.assertEqual(service.effective.profile.simulated_id, SIMULATED_ID)

    def test_an_unknown_or_blank_value_is_ignored(self):
        for value in ("no-such-device", "   ", ""):
            with self.subTest(value):
                os.environ[SIMULATE_ENV] = value
                service, _ = self.make()
                self.assertFalse(service.is_simulated)

    def test_the_catalog_variable_names_the_file_to_read_instead_of_the_shipped_one(self):
        os.environ[CATALOG_ENV] = str(GOLDEN_PATH)
        replacement = Catalog.parse('{"catalogVersion": "test"}')
        with patch("surface_devicekit.catalog._load_cached", return_value=replacement) as load:
            service = DeviceDetectionService(probe=FakeProbe(LOCAL))
        load.assert_called_once_with(str(GOLDEN_PATH.resolve()))
        self.assertIs(service.catalog, replacement)

    def test_a_catalog_variable_that_names_a_missing_file_is_an_error(self):
        os.environ[CATALOG_ENV] = str(GOLDEN_PATH.with_name("no-such-catalog.json"))
        with self.assertRaises(FileNotFoundError):
            DeviceDetectionService(probe=FakeProbe(LOCAL))


class SharedServiceTests(IsolatedEnvironment):
    def test_get_service_makes_one_service_and_hands_it_out_again(self):
        with patch.object(service_module, "_shared", None):
            first = get_service()
            self.assertIs(get_service(), first)
            self.assertIsInstance(first, DeviceDetectionService)


class ThreadSafetyTests(ServiceTestCase):
    def test_threads_asking_at_once_probe_only_once(self):
        service, probe = self.make()
        with ThreadPoolExecutor(max_workers=8) as pool:
            reports = list(pool.map(lambda _: service.detected, range(40)))
        self.assertEqual(probe.calls, 1)
        self.assertTrue(all(report is reports[0] for report in reports))

    def test_simulating_and_clearing_from_several_threads_always_leaves_whole_reports(self):
        service, _ = self.make()
        ids = [d.id for d in devices()[:6]]

        def work(worker: int) -> list[str]:
            problems = []
            for n in range(25):
                if n % 5 == 4:
                    service.clear_simulation()
                else:
                    service.simulate(ids[(worker + n) % len(ids)])
                profile = service.effective.profile
                if profile.source == "simulated" and (profile.simulated_id not in ids or profile.label != device(profile.simulated_id).label):
                    problems.append(f"{profile.simulated_id}: {profile.label}")
                elif profile.source == "detected" and profile.simulated_id is not None:
                    problems.append("detected report with a simulated id")
            return problems

        with ThreadPoolExecutor(max_workers=8) as pool:
            self.assertEqual([p for problems in pool.map(work, range(8)) for p in problems], [])
