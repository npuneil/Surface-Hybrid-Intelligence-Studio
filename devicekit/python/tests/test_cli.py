import json
import os
import subprocess
import sys
import unittest
from unittest.mock import patch

from surface_devicekit import golden
from surface_devicekit.__main__ import USAGE
from surface_devicekit.jsonio import parse_raw, to_json
from surface_devicekit.probe import PlatformNotSupportedError
from surface_devicekit.report import render
from surface_devicekit.service import SIMULATE_ENV, analyze

from tests.support import (
    GOLDEN_PATH,
    PACKAGE_ROOT,
    IsolatedEnvironment,
    catalog,
    catalog_data,
    devices,
    raw_of,
    report_of,
    run_cli,
)

N1X = "nvidia-n1x-128"
SNAPDRAGON = "snapdragon-x-elite-16"
EMULATED = "snapdragon-x-elite-16-emulated"
PROBE = "surface_devicekit.service.probe_this_pc"
OPTIONS = ("--json", "--raw", "--raw-file", "--list", "--simulate", "--plan", "--all-simulated", "--golden", "--check-golden", "--self-test", "--catalog")


def serving(files: dict[str, str]):
    """Replaces the tool's file reader with one that serves the given path -> text files and fails for any other path."""

    def read(path: str) -> str:
        if path not in files:
            raise FileNotFoundError(2, "No such file or directory", path)
        return files[path]

    return patch("surface_devicekit.__main__.read_text", side_effect=read)


def catalog_text(edit) -> str:
    data = catalog_data()
    edit(data)
    return json.dumps(data)


def golden_text(edit) -> str:
    data = json.loads(GOLDEN_PATH.read_text(encoding="utf-8-sig"))
    edit(data)
    return json.dumps(data)


def run_module(*args: str, encoding: str = "utf-8") -> subprocess.CompletedProcess:
    """python -m surface_devicekit in a fresh interpreter started in the package's folder."""
    env = {key: value for key, value in os.environ.items() if not key.startswith("SURFACE_DEVICEKIT_")}
    env["PYTHONIOENCODING"] = encoding
    return subprocess.run(
        [sys.executable, "-B", "-m", "surface_devicekit", *args],
        cwd=PACKAGE_ROOT,
        env=env,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=120,
    )


class ReportTests(unittest.TestCase):
    def test_a_simulated_device_prints_its_text_report(self):
        code, out, err = run_cli("--simulate", N1X)
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(out, render(report_of(N1X), catalog()) + "\n")

    def test_json_prints_the_whole_report(self):
        code, out, err = run_cli("--simulate", N1X, "--json")
        self.assertEqual((code, err), (0, ""))
        shown = json.loads(out)
        self.assertEqual(list(shown), ["profile", "plans", "catalogVersion"])
        profile = shown["profile"]
        self.assertEqual((profile["source"], profile["simulatedId"], profile["deviceClass"], profile["pcClass"]), ("simulated", N1X, "gpu-first", "builder"))
        self.assertEqual(shown["plans"]["structured-json"]["primary"]["alias"], "qwen2.5-coder-14b")
        self.assertEqual(out, to_json(report_of(N1X)) + "\n")

    def test_plan_prints_the_title_the_summary_and_a_line_per_lane(self):
        code, out, err = run_cli("--simulate", N1X, "--plan", "structured-json")
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(
            out.splitlines(),
            [
                "Structured JSON generation",
                "  Qwen2.5-Coder 14B on the GPU (8.8 GB model file). Fallback: Qwen2.5 7B on the CPU.",
                "  gpu budget  60.5 GB  ladder: qwen2.5-coder-14b > qwen2.5-14b > phi-4 > qwen2.5-7b > qwen2.5-coder-7b > phi-4-mini > qwen2.5-1.5b > qwen2.5-coder-1.5b > qwen2.5-0.5b",
                "  cpu budget  28.8 GB  ladder: qwen2.5-7b > qwen2.5-coder-7b > phi-4-mini > qwen2.5-1.5b > qwen2.5-coder-1.5b > qwen2.5-0.5b",
                "  npu budget   0.0 GB  ladder: An NVIDIA NPU device is present, but Foundry Local has no execution provider for it, so language models do not use it.",
            ],
        )

    def test_plan_with_json_prints_only_that_plan(self):
        code, out, err = run_cli("--simulate", N1X, "--plan", "chat", "--json")
        self.assertEqual((code, err), (0, ""))
        plan = json.loads(out)
        self.assertEqual(list(plan), ["workload", "title", "why", "primaryLane", "primary", "lanes", "summary"])
        self.assertEqual(plan["workload"], "chat")

    def test_an_unknown_workload_names_the_known_ones(self):
        known = ", ".join(report_of(N1X).plans)
        self.assertEqual(run_cli("--simulate", N1X, "--plan", "nope"), (2, "", f"Unknown workload 'nope'. Known: {known}\n"))

    def test_options_may_come_in_any_order(self):
        self.assertEqual(run_cli("--plan", "chat", "--json", "--simulate", N1X), run_cli("--simulate", N1X, "--plan", "chat", "--json"))


class ListTests(unittest.TestCase):
    def test_list_groups_the_devices_in_file_order(self):
        code, out, err = run_cli("--list")
        self.assertEqual((code, err), (0, ""))
        expected = []
        for group in dict.fromkeys(d.group for d in devices()):
            expected.append(group)
            expected.extend(f"  {d.id:<34} {d.label}" for d in devices() if d.group == group)
        lines = out.splitlines()
        self.assertEqual(lines, expected)
        self.assertEqual(lines[:2], ["snapdragon", "  snapdragon-x-plus-16               Surface Laptop 7 · Snapdragon X Plus · 16 GB"])

    def test_all_simulated_prints_every_device_report_under_its_id(self):
        code, out, err = run_cli("--all-simulated")
        self.assertEqual((code, err), (0, ""))
        everything = json.loads(out)
        self.assertEqual(list(everything), [d.id for d in devices()])
        for device_id, shown in everything.items():
            with self.subTest(device_id):
                self.assertEqual(shown, json.loads(to_json(report_of(device_id))))
                self.assertEqual(shown["profile"]["simulatedId"], device_id)

    def test_when_several_modes_are_given_the_earliest_in_the_tools_order_wins(self):
        modes = (
            ("--self-test",),
            ("--golden",),
            ("--check-golden", str(GOLDEN_PATH)),
            ("--list",),
            ("--all-simulated",),
            ("--simulate", N1X),
        )
        for first, second in zip(modes, modes[1:]):
            with self.subTest(first=first[0], second=second[0]):
                self.assertEqual(run_cli(*second, *first), run_cli(*first))


class GoldenCommandTests(unittest.TestCase):
    def test_golden_prints_the_vectors_the_repo_holds(self):
        code, out, err = run_cli("--golden")
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(out, golden.build(catalog()) + "\n")
        self.assertEqual(json.loads(out), json.loads(GOLDEN_PATH.read_text(encoding="utf-8-sig")))

    def test_the_repos_golden_file_matches(self):
        self.assertEqual(run_cli("--check-golden", str(GOLDEN_PATH)), (0, "Golden vectors match.\n", ""))

    def test_what_golden_prints_is_accepted_by_check_golden(self):
        _, printed, _ = run_cli("--golden")
        with serving({"mine.json": printed}):
            self.assertEqual(run_cli("--check-golden", "mine.json"), (0, "Golden vectors match.\n", ""))

    def test_a_difference_is_listed_with_its_path_and_the_exit_code_is_1(self):
        def change(data):
            data["devices"][N1X]["pcClass"] = "windows"
            data["devices"][N1X]["lanes"][0]["budgetGb"] += 0.02

        with serving({"edited.json": golden_text(change)}):
            code, out, err = run_cli("--check-golden", "edited.json")
        self.assertEqual((code, err), (1, ""))
        self.assertEqual(
            sorted(out.splitlines()),
            sorted(
                [
                    f"$.devices.{N1X}.lanes[0].budgetGb: expected 60.52, got 60.5",
                    f'$.devices.{N1X}.pcClass: expected "windows", got "builder"',
                    "2 difference(s) from edited.json. Regenerate with --golden if the change was intended.",
                ]
            ),
        )

    def test_only_the_first_forty_differences_are_printed_but_all_are_counted(self):
        def change(data):
            for entry in data["devices"].values():
                entry["pcClass"] = entry["deviceClass"] = "x"

        with serving({"edited.json": golden_text(change)}):
            code, out, err = run_cli("--check-golden", "edited.json")
        lines = out.splitlines()
        self.assertEqual((code, err, len(lines)), (1, "", 41))
        self.assertEqual(lines[-1], f"{2 * len(devices())} difference(s) from edited.json. Regenerate with --golden if the change was intended.")

    def test_check_golden_builds_the_vectors_from_the_catalog_given(self):
        files = {"golden.json": GOLDEN_PATH.read_text(encoding="utf-8-sig"), "other.json": catalog_text(lambda d: d.update(catalogVersion="9.9.9"))}
        with serving(files):
            code, out, err = run_cli("--check-golden", "golden.json", "--catalog", "other.json")
        self.assertEqual((code, err), (1, ""))
        self.assertEqual(out.splitlines()[0], '$.catalogVersion: expected "2026.11.0", got "9.9.9"')


class SelfTestCommandTests(unittest.TestCase):
    def test_self_test_prints_a_line_per_check_and_a_summary(self):
        code, out, err = run_cli("--self-test")
        self.assertEqual((code, err), (0, ""))
        lines = out.splitlines()
        self.assertEqual(len(lines), 32)
        self.assertEqual(lines[0], "PASS  catalog: every workload candidate is a known model")
        self.assertTrue(all(line.startswith("PASS  ") for line in lines[:-2]))
        self.assertEqual(lines[-2:], ["", "Device kit self-test: 30/30 checks passed"])

    def test_a_failing_check_shows_its_detail_and_the_exit_code_is_1(self):
        broken = catalog_text(lambda d: d["workloads"]["vision"]["candidates"].append("no-such-model"))
        with serving({"broken.json": broken}):
            code, out, err = run_cli("--self-test", "--catalog", "broken.json")
        lines = out.splitlines()
        self.assertEqual((code, err), (1, ""))
        self.assertEqual(lines[0], "FAIL  catalog: every workload candidate is a known model  <- vision:no-such-model")
        self.assertEqual(sum(line.startswith("FAIL") for line in lines), 1)
        self.assertEqual(lines[-1], "Device kit self-test: 29/30 checks passed")


class CatalogOptionTests(unittest.TestCase):
    def test_every_mode_that_reads_the_catalog_uses_the_one_given(self):
        files = {"other.json": catalog_text(lambda d: d.update(catalogVersion="9.9.9")), "raw.json": to_json(raw_of(N1X))}
        versions = {
            ("--simulate", N1X, "--json"): lambda shown: shown["catalogVersion"],
            ("--raw-file", "raw.json", "--json"): lambda shown: shown["catalogVersion"],
            ("--all-simulated",): lambda shown: shown[N1X]["catalogVersion"],
            ("--golden",): lambda shown: shown["catalogVersion"],
        }
        with serving(files):
            for args, version_of in versions.items():
                with self.subTest(args):
                    code, out, err = run_cli(*args, "--catalog", "other.json")
                    self.assertEqual((code, err), (0, ""))
                    self.assertEqual(version_of(json.loads(out)), "9.9.9")

    def test_a_catalog_that_cannot_be_read_or_parsed_is_an_error_message(self):
        with serving({"bad.json": "{ nope", "list.json": "[]"}):
            for name in ("bad.json", "list.json", "missing.json"):
                with self.subTest(name):
                    code, out, err = run_cli("--simulate", N1X, "--catalog", name)
                    self.assertEqual((code, out), (2, ""))
                    self.assertTrue(err.endswith("\n") and len(err) > 1, err)

    def test_a_missing_dictionary_key_is_worded_the_way_dotnet_words_it(self):
        broken = catalog_text(lambda d: d["workloads"].pop("chat"))
        with serving({"broken.json": broken}):
            result = run_cli("--simulate", N1X, "--catalog", "broken.json")
        self.assertEqual(result, (2, "", "The given key 'chat' was not present in the dictionary.\n"))

    def test_a_workload_that_lists_an_unknown_model_is_planned_without_it(self):
        broken = catalog_text(lambda d: d["workloads"]["chat"].update(candidates=["no-such-model"], availableFrom="no-such-model"))
        with serving({"broken.json": broken}):
            code, out, err = run_cli("--simulate", N1X, "--catalog", "broken.json")
        self.assertEqual((code, err), (0, ""))
        self.assertIn("Large models", out)


class RawFileTests(unittest.TestCase):
    def test_hardware_captured_elsewhere_is_reported_as_a_detected_pc(self):
        raw = raw_of(N1X)
        with serving({"surface.json": to_json(raw)}):
            code, out, err = run_cli("--raw-file", "surface.json")
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(out, render(analyze(raw, catalog()), catalog()) + "\n")

    def test_the_report_of_a_raw_file_is_detected_not_simulated(self):
        with serving({"surface.json": to_json(raw_of(N1X))}):
            _, out, _ = run_cli("--raw-file", "surface.json", "--json")
        profile = json.loads(out)["profile"]
        self.assertEqual((profile["source"], profile["simulatedId"]), ("detected", None))

    def test_a_raw_file_wins_over_simulate(self):
        with serving({"x.json": to_json(raw_of(SNAPDRAGON))}):
            _, out, _ = run_cli("--raw-file", "x.json", "--simulate", N1X, "--json")
        profile = json.loads(out)["profile"]
        self.assertEqual((profile["source"], profile["deviceClass"]), ("detected", "npu-first"))

    def test_a_raw_file_can_be_planned_like_any_report(self):
        with serving({"x.json": to_json(raw_of(EMULATED))}):
            code, out, err = run_cli("--raw-file", "x.json", "--plan", "structured-json", "--json")
        plan = json.loads(out)
        self.assertEqual((code, err, plan["primaryLane"], plan["primary"]["alias"]), (0, "", "cpu", "phi-4-mini"))

    def test_a_file_that_is_not_hardware_json_is_an_error_message(self):
        with serving({"bad.json": "{ nope", "list.json": "[]"}):
            for name in ("bad.json", "list.json", "missing.json"):
                with self.subTest(name):
                    code, out, err = run_cli("--raw-file", name)
                    self.assertEqual((code, out), (2, ""))
                    self.assertTrue(err.endswith("\n") and len(err) > 1, err)


class ThisPcTests(IsolatedEnvironment):
    def test_without_options_the_probed_pc_is_reported_as_detected(self):
        raw = raw_of(N1X)
        with patch(PROBE, return_value=raw):
            code, out, err = run_cli()
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(out, render(analyze(raw, catalog()), catalog()) + "\n")

    def test_raw_prints_the_hardware_in_a_form_raw_file_reads_back(self):
        raw = raw_of(N1X)
        with patch(PROBE, return_value=raw):
            code, printed, err = run_cli("--raw")
            _, direct, _ = run_cli("--json")
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(parse_raw(printed), raw)
        with serving({"again.json": printed}):
            self.assertEqual(run_cli("--raw-file", "again.json", "--json"), (0, direct, ""))

    def test_the_simulate_variable_does_not_change_what_the_tool_prints(self):
        os.environ[SIMULATE_ENV] = SNAPDRAGON
        with patch(PROBE, return_value=raw_of(N1X)):
            code, out, err = run_cli("--json")
        self.assertEqual((code, err), (0, ""))
        self.assertEqual(json.loads(out)["profile"]["source"], "detected")
        self.assertEqual(json.loads(out)["profile"]["deviceClass"], "gpu-first")

    def test_a_pc_that_cannot_be_probed_is_an_error_message(self):
        with patch(PROBE, side_effect=PlatformNotSupportedError("Device probing needs Windows.")):
            self.assertEqual(run_cli(), (2, "", "Device probing needs Windows.\n"))

    def test_the_modes_that_do_not_describe_this_pc_never_probe_it(self):
        with serving({"raw.json": to_json(raw_of(N1X))}), patch(PROBE, side_effect=AssertionError("probed")) as probe:
            for args in (("--list",), ("--simulate", N1X), ("--all-simulated",), ("--golden",), ("--self-test",), ("--raw-file", "raw.json")):
                with self.subTest(args):
                    self.assertEqual(run_cli(*args)[0], 0)
        probe.assert_not_called()


@unittest.skipUnless(sys.platform == "win32", "the hardware probe reads Windows")
class ThisPcOnWindowsTests(IsolatedEnvironment):
    def test_json_and_raw_describe_the_same_pc(self):
        raw_code, raw_out, raw_err = run_cli("--raw")
        json_code, json_out, json_err = run_cli("--json")
        self.assertEqual((raw_code, raw_err, json_code, json_err), (0, "", 0, ""))
        raw, profile = json.loads(raw_out), json.loads(json_out)["profile"]
        self.assertEqual((profile["source"], profile["simulatedId"]), ("detected", None))
        self.assertEqual(profile["manufacturer"], raw["manufacturer"])
        self.assertIn(profile["product"], raw["product"])
        self.assertEqual(profile["deviceClass"], analyze(parse_raw(raw_out), catalog()).profile.device_class)


class ArgumentTests(unittest.TestCase):
    def test_help_prints_the_usage_whatever_else_is_given(self):
        for flag in ("--help", "-h", "-?"):
            with self.subTest(flag):
                code, out, err = run_cli("--bogus", flag)
                self.assertEqual((code, err), (0, ""))
                self.assertEqual(out, USAGE + "\n")
                for option in OPTIONS:
                    self.assertIn(option, out)

    def test_an_unknown_option_is_refused_with_the_usage(self):
        self.assertEqual(run_cli("--bogus"), (2, "", f"Unknown option '--bogus'.\n\n{USAGE}\n"))

    def test_an_option_given_twice_leaves_the_second_unknown(self):
        self.assertEqual(run_cli("--json", "--json", "--simulate", N1X)[2:], (f"Unknown option '--json'.\n\n{USAGE}\n",))

    def test_each_option_that_takes_a_value_says_so_when_it_has_none(self):
        for option in ("--check-golden", "--raw-file", "--simulate", "--plan", "--catalog"):
            with self.subTest(option):
                self.assertEqual(run_cli(option), (2, "", f"{option} needs a value.\n"))

    def test_an_option_that_takes_a_value_takes_the_next_word_unless_that_word_is_a_flag(self):
        self.assertEqual(run_cli("--simulate", "--bogus"), (2, "", "No simulated device '--bogus'. Try --list.\n"))
        self.assertEqual(run_cli("--simulate", "--json"), (2, "", "--simulate needs a value.\n"))

    def test_an_unknown_device_points_to_the_list(self):
        self.assertEqual(run_cli("--simulate", "nope"), (2, "", "No simulated device 'nope'. Try --list.\n"))

    def test_files_that_do_not_exist_are_error_messages_not_tracebacks(self):
        missing = str(PACKAGE_ROOT / "no-such-folder" / "file.json")
        for option in ("--raw-file", "--catalog", "--check-golden"):
            with self.subTest(option):
                code, out, err = run_cli(option, missing)
                self.assertEqual((code, out), (2, ""))
                self.assertIn("no-such-folder", err)


class ModuleEntryPointTests(unittest.TestCase):
    def test_the_self_test_runs_from_the_package_folder(self):
        done = run_module("--self-test")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(done.stdout.splitlines()[-1], "Device kit self-test: 30/30 checks passed")

    def test_the_repos_golden_file_is_matched_by_the_module(self):
        done = run_module("--check-golden", str(GOLDEN_PATH))
        self.assertEqual((done.returncode, done.stdout, done.stderr), (0, "Golden vectors match.\n", ""))

    def test_errors_go_to_standard_error_and_the_exit_code_is_2(self):
        done = run_module("--simulate", "nope")
        self.assertEqual((done.returncode, done.stdout, done.stderr), (2, "", "No simulated device 'nope'. Try --list.\n"))

    def test_a_console_that_cannot_show_the_middle_dot_gets_a_question_mark(self):
        done = run_module("--simulate", N1X, encoding="ascii")
        expected = render(report_of(N1X), catalog()).encode("ascii", "replace").decode("ascii")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIn("?", expected)
        self.assertEqual(done.stdout, expected + "\n")
