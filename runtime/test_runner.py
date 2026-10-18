"""Run with the assembled private Python; never starts Windows Sandbox."""
import importlib.util
import json
import os
import subprocess
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest

RUNTIME = Path(sys.executable).resolve().parent
sys.path.insert(0, str(RUNTIME))
import guest_runner
from winjob import run_owned


class BridgeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="执行验证-", dir=Path(__file__).resolve().parents[1] / "tmp")
        self.root = Path(self.temp.name)
        for name in ["requests", "results", "inputs"]:
            (self.root / name).mkdir()

    def tearDown(self):
        self.temp.cleanup()

    def request(self, content, timeout=5000, call="call", tool="python"):
        return {"protocol_version": 1, "job_id": "job", "call_id": call, "tool": tool,
                "content": content, "arguments": [], "working_directory": str(self.root / "results"), "timeout_ms": timeout}

    def execute(self, request):
        (self.root / "requests" / f"{request['call_id']}.json").write_text(json.dumps(request), "utf-8")
        guest_runner.validate(request, "job")
        guest_runner.execute(request, self.root / "requests", self.root / "results", self.root / "inputs")
        return json.loads((self.root / "results" / f"{request['call_id']}.response.json").read_text("utf-8"))

    def test_private_runtime_and_utf8(self):
        self.assertTrue((RUNTIME / "python313._pth").is_file())
        self.assertEqual(sys.flags.isolated, 1)
        for name in ["numpy", "sympy", "PIL", "matplotlib"]:
            self.assertTrue(Path(importlib.util.find_spec(name).origin).absolute().is_relative_to(RUNTIME))
        response = self.execute(self.request("import os; print('中文数学 ✓'); print(os.environ.get('OMS_TEST_PROVIDER_KEY', 'absent'))"))
        self.assertEqual(response["status"], "completed", (self.root / "results/call.stderr.log").read_text("utf-8"))
        log = (self.root / "results/call.stdout.log").read_text("utf-8")
        self.assertIn("中文数学 ✓", log)
        self.assertIn("absent", log)

    def test_exception_is_utf8_and_failed(self):
        response = self.execute(self.request("raise RuntimeError('中文异常')"))
        self.assertEqual(response["status"], "failed")
        self.assertIn("中文异常", (self.root / "results/call.stderr.log").read_text("utf-8"))

    def test_system_exit_default_and_message(self):
        self.assertEqual(self.execute(self.request("import sys; sys.exit()", call="exit_default"))["status"], "completed")
        self.assertEqual(self.execute(self.request("import sys; sys.exit('退出原因')", call="exit_message"))["status"], "failed")
        self.assertIn("退出原因", (self.root / "results/exit_message.stderr.log").read_text("utf-8"))

    def test_timeout_kills_owned_call(self):
        started = time.monotonic()
        response = self.execute(self.request("import time; time.sleep(60)", timeout=250))
        self.assertEqual(response["status"], "timed_out")
        self.assertLess(time.monotonic() - started, 8)

    def test_cancel_marker(self):
        (self.root / "requests/call.cancel").touch()
        response = self.execute(self.request("raise RuntimeError('must not run')"))
        self.assertEqual(response["status"], "cancelled")
        self.assertFalse((self.root / "results/call.stderr.log").exists())

    def test_reject_cross_job_and_extra_permission(self):
        request = self.request("pass")
        with self.assertRaises(ValueError):
            guest_runner.validate(request, "other-job")
        request["allow_host"] = True
        with self.assertRaises(ValueError):
            guest_runner.validate(request, "job")

    def test_shell_preserves_arguments_as_data_and_utf8(self):
        code = "param([string]$one,[string]$two) Write-Output '中文 shell'; Write-Output $one; Write-Output $two"
        request = self.request(code, tool="shell")
        request["arguments"] = ["a b", "$(throw 'must remain data'); x"]
        response = self.execute(request)
        self.assertEqual(response["status"], "completed", (self.root / "results/call.stderr.log").read_text("utf-8"))
        log = (self.root / "results/call.stdout.log").read_text("utf-8")
        self.assertIn("中文 shell", log)
        self.assertIn("a b", log)
        self.assertIn("$(throw 'must remain data'); x", log)

    def test_native_job_reaps_descendant_on_normal_exit(self):
        marker = self.root / "late-write.txt"
        child_code = f"import time; from pathlib import Path; time.sleep(1); Path({str(marker)!r}).write_text('leaked')"
        code = f"import subprocess,sys; subprocess.Popen([sys.executable,'-I','-c',{child_code!r}]); print('root done')"
        self.assertEqual(self.execute(self.request(code))["status"], "completed", (self.root / "results/call.stderr.log").read_text("utf-8"))
        time.sleep(1.3)
        self.assertFalse(marker.exists(), "descendant survived root completion")

    def test_file_bridge_handshake_atomic_publish_and_stop(self):
        requests, results, inputs = (self.root / name for name in ["requests", "results", "inputs"])
        pending = requests / "call.pending"
        pending.write_text(json.dumps(self.request("print('atomic bridge')")), "utf-8")
        command = [sys.executable, "-I", str(RUNTIME / "guest_runner.py"), "--job-id", "job", "--nonce", "host_nonce",
                   "--requests", str(requests), "--results", str(results), "--inputs", str(inputs)]
        server = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=0x08000000)
        def wait_for(path):
            deadline = time.monotonic() + 5
            while not path.exists() and time.monotonic() < deadline:
                self.assertIsNone(server.poll(), "runner exited before response")
                time.sleep(.05)
            self.assertTrue(path.exists(), f"missing bridge file: {path.name}")
        try:
            wait_for(results / "handshake.json")
            handshake = json.loads((results / "handshake.json").read_text("utf-8"))
            self.assertEqual(handshake, {"protocol_version": 1, "job_id": "job", "session_nonce": "host_nonce"})
            self.assertFalse((results / "call.response.json").exists())
            pending.replace(requests / "call.json")
            wait_for(results / "call.response.json")
            self.assertEqual(json.loads((results / "call.response.json").read_text("utf-8"))["status"], "completed")
        finally:
            (requests / "stop.marker").touch()
            server.wait(timeout=8)
        self.assertEqual(server.returncode, 0)


if __name__ == "__main__":
    os.environ["OMS_TEST_PROVIDER_KEY"] = "synthetic-secret-must-not-inherit"
    unittest.main(verbosity=2)
