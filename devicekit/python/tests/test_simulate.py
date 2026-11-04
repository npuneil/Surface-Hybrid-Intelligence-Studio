import json
import unittest

from surface_devicekit import simulate
from surface_devicekit.models import RawGpu, RawNpu

from tests.support import PROFILES_PATH, device, devices

# defaults key -> RawHardware attribute
DEFAULTED = {
    "manufacturer": "manufacturer",
    "osVersion": "os_version",
    "osBuild": "os_build",
    "modelDrive": "model_drive",
    "freeDiskGb": "free_disk_gb",
    "totalDiskGb": "total_disk_gb",
    "nvidiaSmi": "nvidia_smi",
    "powerPoint": "power_point",
    "powerPointVersion": "power_point_version",
}

SAMPLE = """
{
  // comments are skipped
  "defaults": {"manufacturer": "Contoso", "modelDrive": "D:", "freeDiskGb": 50, "nvidiaSmi": true, "powerPointVersion": "16.0"},
  "profiles": [
    {"id": "Alpha", "group": "g", "label": "Label A", "note": "Note A", "raw": {"cpu": "x", "freeDiskGb": 7.5, "nvidiaSmi": false}},
    {"id": "beta", "raw": {"cpu": "y", "gpus": [{"name": "g", "vramGb": 4}], "npus": [{"name": "n", "provider": "p"}]}}
  ]
}
"""


def shipped_file() -> dict:
    return json.loads(PROFILES_PATH.read_text(encoding="utf-8-sig"))


class DefaultsMergeTests(unittest.TestCase):
    def test_a_profile_inherits_the_defaults_it_does_not_set(self):
        alpha, beta = simulate.load(SAMPLE)
        self.assertEqual((alpha.raw.manufacturer, alpha.raw.model_drive, alpha.raw.power_point_version), ("Contoso", "D:", "16.0"))
        self.assertEqual((beta.raw.manufacturer, beta.raw.model_drive, beta.raw.free_disk_gb, beta.raw.nvidia_smi), ("Contoso", "D:", 50.0, True))

    def test_a_profile_value_wins_over_the_default_even_when_it_is_false(self):
        alpha = simulate.load(SAMPLE)[0]
        self.assertEqual(alpha.raw.free_disk_gb, 7.5)
        self.assertIs(alpha.raw.nvidia_smi, False)

    def test_without_defaults_the_record_defaults_apply(self):
        (only,) = simulate.load('{"profiles": [{"id": "a", "raw": {"cpu": "x"}}]}')
        self.assertEqual((only.raw.manufacturer, only.raw.model_drive, only.raw.free_disk_gb, only.raw.os_arch), ("", "", 0.0, "x64"))

    def test_property_names_are_bound_without_regard_to_case(self):
        (only,) = simulate.load('{"defaults": {"FREEDISKGB": 9}, "profiles": [{"id": "a", "raw": {"CPU": "x", "installedramgb": 8}}]}')
        self.assertEqual((only.raw.cpu, only.raw.installed_ram_gb, only.raw.free_disk_gb), ("x", 8.0, 9.0))

    def test_group_label_and_note_are_optional(self):
        alpha, beta = simulate.load(SAMPLE)
        self.assertEqual((alpha.group, alpha.label, alpha.note), ("g", "Label A", "Note A"))
        self.assertEqual((beta.group, beta.label, beta.note), ("", "", ""))

    def test_gpus_and_npus_become_records(self):
        beta = simulate.load(SAMPLE)[1]
        self.assertEqual(beta.raw.gpus, (RawGpu("g", "", 4.0),))
        self.assertEqual(beta.raw.npus, (RawNpu("n", "", "p"),))

    def test_every_shipped_profile_is_its_raw_laid_over_the_shipped_defaults(self):
        data = shipped_file()
        self.assertTrue(data["defaults"])
        for profile in data["profiles"]:
            merged = {**data["defaults"], **profile["raw"]}
            raw = device(profile["id"]).raw
            for key, attribute in DEFAULTED.items():
                with self.subTest(profile["id"], key=key):
                    self.assertEqual(getattr(raw, attribute), merged[key])

    def test_a_shipped_profile_that_sets_a_default_key_keeps_its_own_value(self):
        self.assertIs(shipped_file()["defaults"]["nvidiaSmi"], False)
        self.assertIs(device("nvidia-dgpu-16").raw.nvidia_smi, True)
        self.assertIs(device("snapdragon-x-elite-16").raw.nvidia_smi, False)


class LoadingTests(unittest.TestCase):
    def test_devices_keep_the_order_of_the_file_and_are_read_once(self):
        self.assertEqual([d.id for d in devices()], [p["id"] for p in shipped_file()["profiles"]])
        self.assertIs(simulate.all_devices(), simulate.all_devices())

    def test_an_id_is_found_without_regard_to_case(self):
        self.assertIs(simulate.find("NVIDIA-N1X-128"), device("nvidia-n1x-128"))
        self.assertEqual(simulate.find("ALPHA", simulate.load(SAMPLE)).id, "Alpha")
        self.assertIsNone(simulate.find("no-such-device"))

    def test_case_is_folded_one_character_at_a_time_as_in_dotnet(self):
        sims = simulate.load('{"profiles": [{"id": "stra\\u00dfe", "raw": {}}]}')
        self.assertIsNotNone(simulate.find("STRA\u00dfE", sims))
        self.assertIsNone(simulate.find("strasse", sims))

    def test_files_that_are_not_profile_files_are_errors(self):
        bad = (
            "",
            "[]",
            '{"profiles": {}}',
            '{"profiles": [1]}',
            '{"profiles": [{"raw": {}}]}',
            '{"profiles": [{"id": "a"}]}',
            '{"profiles": [{"id": "a", "group": 1, "raw": {}}]}',
            '{"defaults": [], "profiles": []}',
            '{"profiles": [{"id": "a", "raw": {"logicalCores": "x"}}]}',
            '{"profiles": [{"id": "a", "raw": {"logicalCores": 1.5}}]}',
            '{"profiles": [{"id": "a", "raw": {"nvidiaSmi": "yes"}}]}',
        )
        for text in bad:
            with self.subTest(text), self.assertRaises(ValueError):
                simulate.load(text)

    def test_an_empty_document_is_reported_as_empty(self):
        with self.assertRaisesRegex(ValueError, "file is empty"):
            simulate.load("null")

    def test_a_missing_file_is_an_os_error(self):
        with self.assertRaises(OSError):
            simulate.load_file(PROFILES_PATH.with_name("no-such-profiles.json"))

    def test_a_file_without_profiles_has_no_devices(self):
        self.assertEqual(simulate.load("{}"), ())
