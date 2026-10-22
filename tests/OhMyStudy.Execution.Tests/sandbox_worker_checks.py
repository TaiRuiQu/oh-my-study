import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile

worker_path = Path(sys.argv[1]).resolve()
runtime = Path(sys.argv[2]).resolve()
spec = importlib.util.spec_from_file_location("sandbox_worker", worker_path)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)

results = []
for name in ("USERPROFILE", "LOCALAPPDATA", "APPDATA"):
    os.environ[name] = "synthetic-" + name
os.environ["OMS_SYNTHETIC_SECRET"] = "must-not-inherit"
with tempfile.TemporaryDirectory(prefix="oms-sandbox-worker-") as temporary:
    root = Path(temporary)
    inputs = root / "inputs"
    inputs.mkdir()
    (inputs / "资料.txt").write_text("合成输入", encoding="utf-8")
    cases = [
        ("python-profile-environment", "python", "import os;assert all(os.environ.get(k) == 'synthetic-' + k for k in ('USERPROFILE','LOCALAPPDATA','APPDATA'));assert 'OMS_SYNTHETIC_SECRET' not in os.environ", 5000, "completed", []),
        ("python-unicode-input-and-arguments", "python", "from pathlib import Path; import sys; print(Path('资料.txt').read_text(encoding='utf-8'));print('|'.join(sys.argv[1:]))", 5000, "completed", ["合成输入", "中文|a b|尾\\"]),
        ("python-nonzero", "python", "raise SystemExit(7)", 5000, "failed", []),
        ("python-timeout", "python", "import time;time.sleep(30)", 150, "timed_out", []),
        ("python-output-limit", "python", "import sys;sys.stdout.write('x'*2000000);sys.stdout.flush()", 5000, "output_limit", []),
        # This checks argument preservation, not PowerShell cold-start performance. Hosted
        # Windows runners can spend >15 s in first-launch security scanning. The separate
        # 150 ms python-timeout case above continues to verify enforced termination.
        ("powershell-arguments", "shell", "param($a,$b,$c);[Console]::WriteLine(($a,$b,$c -join '|'))", 60000, "completed", ["中文|a b|尾\\"]),
    ]
    for name, tool, content, timeout, expected, fragments in cases:
        result_dir = root / name
        result_dir.mkdir()
        call = {"protocol_version": 1, "job_id": "worker-test", "call_id": name,
                "tool": tool, "content": content, "arguments": ["中文", "a b", "尾\\"],
                "working_directory": str(inputs), "timeout_ms": timeout}
        status, code, output = worker.run_computation({"call": call}, runtime, inputs, result_dir)
        assert status == expected, (name, status, output)
        assert len((output[0] + output[1]).encode("utf-8")) <= worker.MAX_OUTPUT
        for fragment in fragments:
            assert fragment in output[0], (name, output)
        if name == "python-nonzero":
            assert code == 7
        assert (inputs / "资料.txt").read_text(encoding="utf-8") == "合成输入"
        results.append({"name": name, "passed": True})
print(json.dumps({"scope": "host computation; AppContainer isolation tested separately", "results": results}, ensure_ascii=False))
