"""What the Studio shows for the PC it runs on: each demo's fit, the device page's payload, simulation, and the routes that carry them."""
import http.client
import json
import threading
import time
import unittest
from unittest.mock import patch

import demos
import hybrid
from content import SCENARIOS
from device_view import NO_DEPTH, DeviceView, hybrid_wording, models, placement
from fakes import ReadyEngine, report_for, service_for
from local_engine import LocalEngine
from server import ShowcaseServer

RTX = "the RTX GPU"
INDEPENDENT = {"faceoff", "team"}


def view_for(device_id):
    """The Studio on that device: an unstarted engine previewing the device's lineup, as it does before Foundry Local has answered."""
    service = service_for(device_id)
    return DeviceView(LocalEngine().preview(service.detected, forced=False), service)


def npu_share(row):
    return sum(1 for step in row["steps"] if step["lane"] == "npu") / len(row["steps"])


class FitTests(unittest.TestCase):
    def test_the_reference_pc_runs_every_demo_as_authored(self):
        rows, order = view_for("nvidia-n1x-64").fits()
        self.assertEqual(order, list(demos.ORDER))
        for demo_id, row in rows.items():
            with self.subTest(demo=demo_id):
                self.assertEqual((row["state"], row["lanes"], row["where"], row["note"]), ("ready", ["gpu"], RTX, None))
                self.assertEqual(row["summary"], f"Runs on {RTX}.")

    def test_an_npu_first_pc_runs_every_demo_on_the_npu_and_names_the_stand_in_models(self):
        rows, _ = view_for("snapdragon-x2-elite-24").fits()
        for demo_id, row in rows.items():
            with self.subTest(demo=demo_id):
                self.assertEqual((row["state"], row["lanes"], row["summary"]), ("ready", ["npu"], "Runs on the NPU."))
        self.assertEqual(rows["brief"]["note"], "Uses Qwen2.5 7B in place of Qwen2.5-Coder 14B, the model this demo was tuned on; "
                                                 "checks are scored the same way.")
        self.assertIsNone(rows["vision"]["note"], "Qwen3-VL 4B is the model the vision demo was tuned on")

    def test_demos_that_run_mostly_on_the_npu_come_first_and_unavailable_ones_last(self):
        view = view_for("snapdragon-x2-plus-8")
        rows, order = view.fits()
        states = [rows[demo_id]["state"] for demo_id in order]
        self.assertEqual(states, sorted(states, key=lambda state: state == "unavailable"))
        usable = [demo_id for demo_id in order if rows[demo_id]["state"] != "unavailable"]
        shares = [npu_share(rows[demo_id]) for demo_id in usable]
        self.assertEqual(shares, sorted(shares, reverse=True))
        self.assertGreater(shares[0], shares[-1], "this PC runs some demos on the CPU, so the order has to tell them apart")
        self.assertEqual([rows[demo_id]["rank"] for demo_id in order], list(range(len(order))))

    def test_a_pc_with_no_room_for_a_reasoning_model_limits_the_demos_that_can_run_without_one(self):
        rows, order = view_for("intel-pentium-8").fits()
        for demo_id in INDEPENDENT:
            self.assertEqual(rows[demo_id]["state"], "limited", demo_id)
            self.assertEqual(rows[demo_id]["summary"], "Runs on the CPU; the reasoning model does not fit this PC.")
        self.assertEqual(rows["reasoning"]["state"], "unavailable")
        self.assertTrue(rows["reasoning"]["summary"].startswith("Not available on this PC: "))
        self.assertIn("CPU", rows["reasoning"]["summary"])
        self.assertEqual(order[-1], "reasoning")
        self.assertEqual([step["available"] for step in rows["reasoning"]["steps"]], [False])
        self.assertIsNone(rows["reasoning"]["steps"][0]["label"])

    def test_every_demo_has_a_fit_on_every_simulated_device(self):
        for device in service_for().simulated_devices:
            with self.subTest(device=device.id):
                rows, order = view_for(device.id).fits()
                self.assertEqual(sorted(order), sorted(demos.ORDER))
                self.assertTrue(all(row["state"] in ("ready", "limited", "unavailable") for row in rows.values()))
                self.assertTrue(any(row["state"] != "unavailable" for row in rows.values()), "every device runs something")


class LineupTests(unittest.TestCase):
    def test_the_lineup_says_which_model_runs_where(self):
        view = view_for("snapdragon-x2-elite-24")
        rows = models(view.shown())
        self.assertEqual([row["slot"] for row in rows], ["chat", "reasoner", "asr", "vision", "embed"])
        self.assertEqual({(row["lane"], row["where"], row["available"]) for row in rows}, {("npu", "the NPU", True)})
        self.assertEqual(rows[0]["label"], "Qwen2.5 7B")
        self.assertEqual(rows[0]["reference"], "qwen2.5-coder-14b")
        self.assertTrue(all(row["estimated"] for row in rows), "a preview is sized from estimates")
        self.assertEqual(placement(rows), "5 of 5 models run here: 5 on the NPU.")

    def test_a_slot_with_no_model_has_a_reason_and_is_left_out_of_the_placement(self):
        rows = models(view_for("intel-pentium-8").shown())
        reasoner = next(row for row in rows if row["slot"] == "reasoner")
        self.assertFalse(reasoner["available"])
        self.assertIsNone(reasoner["label"])
        self.assertIn("CPU", reasoner["blocker"])
        self.assertEqual(placement(rows), "4 of 5 models run here: 4 on the CPU.")

    def test_a_lineup_split_across_lanes_is_counted_per_lane(self):
        rows = models(view_for("snapdragon-x2-plus-8").shown())
        text = placement(rows)
        self.assertRegex(text, r"^\d of 5 models run here: \d on the NPU, \d on the CPU\.$")

    def test_nothing_runs_is_said_plainly(self):
        self.assertEqual(placement([{"available": False, "where": None}]), "No model in the Studio's lineup fits this PC.")


class HybridWordingTests(unittest.TestCase):
    def test_the_reference_pc_keeps_the_authored_wording(self):
        wording = hybrid_wording(view_for("nvidia-n1x-64").shown())
        self.assertEqual(wording, {"where": RTX, "routine": "14B-20B for routine work", "depth": NO_DEPTH})
        self.assertEqual(hybrid_wording(LocalEngine())["routine"], hybrid.REFERENCE_WORDING["routine"])

    def test_larger_local_models_are_named_only_when_this_pc_lists_them(self):
        engine = view_for("nvidia-n1x-64").shown()
        engine.state["models"] = [
            {"runtime": "llama.cpp", "available": True, "params": "30B"},
            {"runtime": "llama.cpp", "available": True, "params": "120B"},
            {"runtime": "llama.cpp", "available": False, "params": "400B"},
            {"runtime": "Foundry Local", "available": True, "params": "14B"},
        ]
        self.assertEqual(hybrid_wording(engine)["depth"], f"30B-120B on {RTX} for depth")

    def test_a_model_whose_name_has_no_size_is_sized_by_its_parameter_count(self):
        engine = view_for("nvidia-dgpu-16").shown()
        self.assertEqual([spec["label"] for spec in engine.models.values() if spec["slot"] in ("chat", "reasoner")],
                         ["Phi-4 mini", "Phi-4 mini reasoning"])
        self.assertEqual(hybrid_wording(engine)["routine"], "3.8B for routine work")

    def test_a_small_pc_says_what_it_runs_and_that_depth_needs_memory(self):
        wording = hybrid_wording(view_for("intel-pentium-8").shown())
        self.assertEqual(wording, {"where": "the CPU", "routine": "0.5B for routine work", "depth": NO_DEPTH})
        npu = hybrid_wording(view_for("snapdragon-x2-elite-24").shown())
        self.assertEqual((npu["where"], npu["routine"]), ("the NPU", "7B for routine work"))


class ViewTests(unittest.TestCase):
    def test_a_simulation_shows_another_device_and_back(self):
        view = view_for("nvidia-n1x-64")
        detected = view.service.detected.profile.label
        self.assertIs(view.shown(), view.engine)
        payload = view.simulate("snapdragon-x2-elite-24")
        self.assertTrue(payload["simulated"])
        self.assertEqual(payload["simulation"]["id"], "snapdragon-x2-elite-24")
        self.assertEqual(payload["detected"]["label"], detected, "the real PC stays visible while another is shown")
        self.assertIsNot(view.shown(), view.engine)
        self.assertEqual(view.shown().describe(None)["where"], "the NPU")
        self.assertIs(view.shown(), view.shown(), "a simulated device is previewed once")
        self.assertTrue(view.target()["simulated"])
        self.assertEqual(view.engine.describe(None)["where"], RTX, "the real engine is untouched")
        payload = view.simulate(None)
        self.assertFalse(payload["simulated"])
        self.assertIs(view.shown(), view.engine)
        self.assertFalse(view.target()["simulated"])

    def test_unknown_devices_are_refused_and_a_device_that_cannot_be_previewed_is_not_kept(self):
        view = view_for("nvidia-n1x-64")
        with self.assertRaises(ValueError):
            view.simulate("not-a-device")
        self.assertFalse(view.simulated)
        with patch.object(LocalEngine, "preview", lambda self, report=None, *, forced=True: self):
            with self.assertRaisesRegex(RuntimeError, "could not be previewed"):
                view.simulate("snapdragon-x2-elite-24")
        self.assertFalse(view.simulated)

    def test_the_payload_carries_the_report_the_lineup_and_the_demos_in_this_pcs_order(self):
        view = view_for("snapdragon-x2-plus-8")
        payload = view.payload()
        json.dumps(payload, allow_nan=False)
        self.assertEqual(len(payload["models"]), 5)
        self.assertEqual(payload["placement"], placement(payload["models"]))
        self.assertEqual([row["rank"] for row in payload["demos"]], list(range(len(demos.ORDER))))
        self.assertEqual(sorted(row["id"] for row in payload["demos"]), sorted(demos.ORDER))
        ids = [device["id"] for device in payload["simulation"]["devices"]]
        self.assertIn("snapdragon-x2-elite-24", ids)
        self.assertEqual(len(ids), len(view.service.simulated_devices))
        self.assertIn("report", payload)
        self.assertEqual(payload["engine"]["lanes"], None)
        self.assertEqual(payload["detected"]["deviceClass"], view.target()["deviceClass"])

    def test_the_status_poll_description_is_small_and_names_the_lanes(self):
        target = view_for("nvidia-n1x-64").target()
        self.assertEqual((target["simulated"], target["deviceClass"]), (False, "gpu-first"))
        self.assertEqual({lane["id"] for lane in target["lanes"]}, {"npu", "gpu", "cpu"})
        self.assertFalse(next(lane for lane in target["lanes"] if lane["id"] == "npu")["available"])

    def test_the_status_poll_says_where_the_models_run_and_follows_a_simulation(self):
        view = view_for("nvidia-n1x-64")
        self.assertEqual(view.target()["where"], RTX)
        view.simulate("snapdragon-x2-elite-24")
        self.assertEqual(view.target()["where"], "the NPU")
        with patch.object(view, "shown", side_effect=RuntimeError("no preview")), self.assertLogs(level="ERROR"):
            target = view.target()
        self.assertIsNone(target["where"], "a description that cannot be made must not break the status poll")
        self.assertIsNone(target["lane"])

    def test_the_status_poll_names_the_lane_and_its_vendor_for_the_platform_page(self):
        view = view_for("nvidia-n1x-64")
        vendors = lambda target: {lane["id"]: lane["vendor"] for lane in target["lanes"] if lane["available"]}
        self.assertEqual((view.target()["lane"], vendors(view.target())["gpu"]), ("gpu", "nvidia"))
        for device_id, vendor in (("snapdragon-x2-elite-24", "qualcomm"), ("intel-lunar-lake-16", "intel")):
            view.simulate(device_id)
            target = view.target()
            self.assertEqual((target["lane"], vendors(target)["npu"]), ("npu", vendor), device_id)


class DeviceApiTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.engine = ReadyEngine()
        cls.server = ShowcaseServer(("127.0.0.1", 0), engine=cls.engine, device={"model": "fixture"},
                                    device_service=service_for("nvidia-n1x-64"))
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()

    def setUp(self):
        self.server.view.simulate(None)
        self.addCleanup(self.server.view.simulate, None)

    def request(self, method, path, body=None, headers=None):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=10)
        request_headers = {"X-Showcase-Token": self.server.token}
        request_headers.update(headers or {})
        connection.request(method, path, body, request_headers)
        response = connection.getresponse()
        data = response.read().decode()
        connection.close()
        return response.status, data

    def get(self, path):
        code, data = self.request("GET", path)
        return code, json.loads(data)

    def post(self, path, payload, headers=None):
        code, data = self.request("POST", path, json.dumps(payload), headers)
        return code, data

    def simulate(self, device_id):
        code, data = self.post("/api/device/simulate", {"id": device_id})
        self.assertEqual(code, 200, data)
        return json.loads(data)

    def test_status_names_the_pc_the_studio_is_showing(self):
        code, status = self.get("/api/status")
        self.assertEqual(code, 200)
        self.assertEqual((status["target"]["simulated"], status["target"]["deviceClass"]), (False, "gpu-first"))
        self.simulate("snapdragon-x2-elite-24")
        status = self.get("/api/status")[1]
        self.assertEqual((status["target"]["simulated"], status["target"]["deviceClass"]), (True, "npu-first"))

    def test_the_device_endpoint_reports_the_pc_its_lineup_and_the_demos(self):
        code, payload = self.get("/api/device")
        self.assertEqual(code, 200)
        self.assertEqual([row["id"] for row in payload["demos"]], list(demos.ORDER))
        self.assertEqual(len(payload["models"]), 5)
        self.assertGreaterEqual(len(payload["simulation"]["devices"]), 20)
        self.assertEqual(self.request("GET", "/api/device", headers={"Host": "evil.example"})[0], 403)

    def test_simulating_is_protected_validated_and_serialized(self):
        self.assertEqual(self.post("/api/device/simulate", {"id": "intel-pentium-8"}, {"X-Showcase-Token": "bad"})[0], 403)
        self.assertEqual(self.post("/api/device/simulate", {"id": "not-a-device"})[0], 400)
        for bad in (7, [], {"a": 1}):
            self.assertEqual(self.post("/api/device/simulate", {"id": bad})[0], 400, bad)
        self.assertFalse(self.server.view.simulated)
        self.server.run_lock.acquire()
        try:
            self.assertEqual(self.post("/api/device/simulate", {"id": "intel-pentium-8"})[0], 409)
        finally:
            self.server.run_lock.release()
        self.assertFalse(self.server.view.simulated)

    def test_a_simulated_device_changes_what_the_demos_and_the_router_say(self):
        before = self.get("/api/demos")[1]["demos"]
        self.assertEqual([row["id"] for row in before], list(demos.ORDER))
        self.assertEqual({row["fit"]["state"] for row in before}, {"ready"})
        self.simulate("intel-pentium-8")
        after = self.get("/api/demos")[1]["demos"]
        self.assertEqual(after[-1]["id"], "reasoning")
        self.assertEqual(after[-1]["fit"]["state"], "unavailable")
        self.assertEqual(next(row for row in after if row["id"] == "team")["fit"]["state"], "limited")
        self.assertNotIn(RTX, json.dumps(after))
        patterns = self.get("/api/hybrid")[1]["patterns"]
        self.assertIn("the CPU", json.dumps(patterns))
        self.assertNotIn(RTX, json.dumps(patterns))
        secret = next(preset for preset in hybrid.PRESETS if preset["id"] == "secret")
        code, data = self.post("/api/hybrid/route", {"text": secret["text"]})
        self.assertEqual(code, 200, "a route preview never runs a model")
        self.assertEqual(json.loads(data)["steps"][0]["text"], "Answer on the CPU; nothing leaves the device.")

    def test_live_runs_pause_while_a_device_is_simulated_but_rehearsals_and_the_cloud_lane_do_not(self):
        self.simulate("snapdragon-x2-elite-24")
        live = [("/api/demo/run", {"demo": "brief", "mode": "live"}),
                ("/api/run", {"scenario": "brief", "mode": "live", "prompt": SCENARIOS["brief"]["prompt"]}),
                ("/api/speedtest", {}),
                ("/api/model", {"model": "qwen2.5-coder-14b"}),
                ("/api/hybrid/run", {"text": next(p for p in hybrid.PRESETS if p["id"] == "secret")["text"]})]
        for path, payload in live:
            with self.subTest(path=path):
                code, data = self.post(path, payload)
                self.assertEqual(code, 409, data)
                self.assertIn("Simulation is on", json.loads(data)["error"])
                self.assertIn("paused", json.loads(data)["error"])
        self.assertFalse(self.server.run_lock.locked())
        code, data = self.post("/api/demo/run", {"demo": "voice-memo", "mode": "rehearsal"})
        self.assertEqual(code, 200, data)
        self.assertIn("Qwen2.5 7B", data, "the rehearsal's plan names the models of the simulated device")
        self.assertNotIn("Qwen2.5-Coder 14B", data)
        news = next(p for p in hybrid.PRESETS if p["id"] == "news")
        self.assertEqual(self.post("/api/hybrid/run", {"text": news["text"]})[0], 200)
        self.assertFalse(self.server.run_lock.locked())

    def test_turning_the_simulation_off_lets_live_runs_through_to_the_engine_again(self):
        self.simulate("snapdragon-x2-elite-24")
        self.engine.state["state"] = "loading"
        self.addCleanup(self.engine.state.update, state="ready")
        self.assertEqual(self.post("/api/demo/run", {"demo": "brief", "mode": "live"})[0], 409)
        self.simulate(None)
        self.assertEqual(self.post("/api/demo/run", {"demo": "brief", "mode": "live"})[0], 503, "the engine, not the simulation, is what stops it")


class ModelSelectionTests(unittest.TestCase):
    """/api/model on an engine that runs this PC's own lineup: the demos' reference names mean whatever this PC runs in that role."""

    @classmethod
    def setUpClass(cls):
        cls.engine = ReadyEngine().preview(report_for("intel-pentium-8"), forced=False)
        cls.server = ShowcaseServer(("127.0.0.1", 0), engine=cls.engine, device={"model": "fixture"},
                                    device_service=service_for("intel-pentium-8"))
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()

    def post(self, payload):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=10)
        connection.request("POST", "/api/model", json.dumps(payload), {"X-Showcase-Token": self.server.token})
        response = connection.getresponse()
        data = json.loads(response.read().decode())
        connection.close()
        return response.status, data

    def wait_for_switch(self):
        deadline = time.monotonic() + 5
        while self.server.run_lock.locked() and time.monotonic() < deadline:
            time.sleep(0.01)
        self.assertFalse(self.server.run_lock.locked())

    def test_a_slot_with_no_model_is_refused_with_the_reason(self):
        code, data = self.post({"model": "gpt-oss-20b"})
        self.assertEqual(code, 400)
        self.assertIn("CPU", data["error"])
        self.assertEqual(self.post({"model": "whisper-medium"})[0], 400)

    def test_a_demo_reference_name_selects_the_model_this_pc_runs_in_that_role(self):
        code, data = self.post({"model": "qwen2.5-coder-14b"})
        self.assertEqual((code, data["selected_model"]), (202, "qwen2.5-0.5b"))
        self.wait_for_switch()
        self.assertEqual(self.engine.state["selected_model"], "qwen2.5-0.5b")
        code, data = self.post({"model": "qwen2.5-0.5b"})
        self.assertEqual((code, data["selected_model"]), (202, "qwen2.5-0.5b"))
        self.wait_for_switch()


if __name__ == "__main__":
    unittest.main()
