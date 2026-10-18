"""M0 file bridge. Its output is untrusted; never grants host permissions.

Only the trusted read-only Requests mapping controls calls. Guest arbitrary code
can modify this process: host validation is mandatory regardless of runner checks.
"""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

# -I deliberately omits the script directory; only this read-only runtime path is added.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from winjob import run_owned

ID = re.compile(r"[A-Za-z0-9_-]{1,128}\Z")


def atomic_json(path, value):
    temporary = path.with_suffix(".pending")
    with temporary.open("x", encoding="utf-8") as output:
        json.dump(value, output, ensure_ascii=False)
        output.flush()
        os.fsync(output.fileno())
    temporary.replace(path)


def validate(request, job):
    keys = {"protocol_version", "job_id", "call_id", "tool", "content", "arguments", "working_directory", "timeout_ms"}
    if set(request) != keys or request["protocol_version"] != 1 or request["job_id"] != job:
        raise ValueError("protocol or job mismatch")
    if not isinstance(request["call_id"], str) or not ID.fullmatch(request["call_id"]):
        raise ValueError("invalid call id")
    if request["tool"] not in {"python", "shell"} or not isinstance(request["content"], str):
        raise ValueError("invalid code/tool")
    if len(request["content"].encode()) > 1048576 or not isinstance(request["arguments"], list) or not all(isinstance(x, str) for x in request["arguments"]):
        raise ValueError("invalid arguments/code size")
    if type(request["timeout_ms"]) is not int or not 1 <= request["timeout_ms"] <= 3600000:
        raise ValueError("invalid timeout")


def execute(request, requests, results, inputs):
    call_id = request["call_id"]
    # Guest working directories stay in the current task's known mapped roots.
    cwd = Path(request["working_directory"]).resolve()
    if not (cwd == inputs.resolve() or cwd == results.resolve() or cwd.is_relative_to(results.resolve())):
        raise ValueError("guest working directory outside task")
    command = [sys.executable, "-I", str(Path(__file__).with_name("host_entry.py")), str(requests / f"{call_id}.json"), str(results)]
    env = {key: value for key, value in os.environ.items() if key.lower() in {"systemroot", "windir", "temp", "tmp"}}
    env.update(MPLBACKEND="Agg", MPLCONFIGDIR=str(Path(os.environ.get("TEMP", str(results))) / "oms-matplotlib"), OMP_NUM_THREADS="2", OPENBLAS_NUM_THREADS="2")
    status, code = run_owned(command, cwd, env, request["timeout_ms"],
        lambda: (requests / f"{call_id}.cancel").is_file(),
        [results / f"{call_id}.stdout.log", results / f"{call_id}.stderr.log"])
    # Names are host pre-registered; only current call-prefixed PNG is offered.
    artifacts = [p.name for p in results.glob(f"{call_id}-*.png") if p.is_file() and not p.is_symlink()]
    atomic_json(results / f"{call_id}.response.json", {"protocol_version": 1, "job_id": request["job_id"], "call_id": call_id, "status": status, "exit_code": code, "artifacts": artifacts})


def serve(job, nonce, requests, results, inputs):
    if not ID.fullmatch(job) or not ID.fullmatch(nonce):
        raise ValueError("invalid identity")
    atomic_json(results / "handshake.json", {"protocol_version": 1, "job_id": job, "session_nonce": nonce})
    seen = set()
    while not (requests / "stop.marker").exists():
        for path in sorted(requests.glob("*.json")):
            if path.name in seen:
                continue
            seen.add(path.name)
            try:
                if path.stat().st_size > 2 * 1024 * 1024 or path.is_symlink():
                    raise ValueError("invalid request file")
                request = json.loads(path.read_text("utf-8"))
                validate(request, job)
                if path.name != f"{request['call_id']}.json":
                    raise ValueError("filename/call mismatch")
                execute(request, requests, results, inputs)
            except Exception as exc:
                # Error evidence only, no permission or host command fields.
                atomic_json(results / f"{path.stem}.error.json", {"error": str(exc)[:2048]})
        time.sleep(0.25)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--job-id", required=True)
    parser.add_argument("--nonce", required=True)
    for name in ["requests", "results", "inputs"]:
        parser.add_argument(f"--{name}", type=Path, required=True)
    options = parser.parse_args()
    serve(options.job_id, options.nonce, options.requests, options.results, options.inputs)
