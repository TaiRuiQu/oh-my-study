import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import threading
import time

MAX_OUTPUT = 1_048_576


def atomic_json(path, value):
    temporary = path.with_suffix(".pending")
    with temporary.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, ensure_ascii=False, separators=(",", ":"))
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def run_computation(request, runtime, inputs, results):
    call = request["call"]
    work = results / "work"
    shutil.copytree(inputs, work)
    temporary = results / "temp"
    temporary.mkdir()
    system = Path(os.environ.get("SystemRoot", r"C:\Windows"))
    environment = {
        "SystemRoot": str(system), "WINDIR": str(system),
        "TEMP": str(temporary), "TMP": str(temporary),
        "PATH": str(runtime) + ";" + str(system / "System32"),
        "MPLBACKEND": "Agg", "MPLCONFIGDIR": str(temporary / "matplotlib"),
        "OMP_NUM_THREADS": "2", "OPENBLAS_NUM_THREADS": "2",
        "PYTHONIOENCODING": "utf-8",
    }
    for name in ("USERPROFILE", "LOCALAPPDATA", "APPDATA"):
        if name in os.environ:
            environment[name] = os.environ[name]
    computation_request = results / "approved-call.json"
    atomic_json(computation_request, call)
    if call["tool"] == "python":
        command = [str(runtime / "python.exe"), "-I", "-X", "utf8",
                   str(runtime / "execute.py"), str(computation_request)]
    elif call["tool"] == "shell":
        import base64
        literal = "'" + str(computation_request).replace("'", "''") + "'"
        wrapper = ("$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);"
                   "$r=([IO.File]::ReadAllText(" + literal + ")|ConvertFrom-Json);"
                   "$a=@($r.arguments|ForEach-Object{[string]$_});"
                   "& ([scriptblock]::Create([string]$r.content)) @a;if(-not $?){exit 1}")
        command = [str(system / "System32/WindowsPowerShell/v1.0/powershell.exe"),
                   "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                   base64.b64encode(wrapper.encode("utf-16-le")).decode("ascii")]
    else:
        raise ValueError("unsupported computation tool")
    lock = threading.Lock()
    captured = [bytearray(), bytearray()]
    total = 0
    exceeded = threading.Event()

    def capture(stream, index):
        nonlocal total
        while True:
            data = os.read(stream.fileno(), 4096)
            if not data:
                return
            with lock:
                keep = max(0, min(len(data), MAX_OUTPUT - total))
                captured[index].extend(data[:keep])
                total += len(data)
                if total > MAX_OUTPUT:
                    exceeded.set()

    process = subprocess.Popen(command, cwd=work, env=environment, stdin=subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               creationflags=0x08000000 if os.name == "nt" else 0)
    readers = [threading.Thread(target=capture, args=(process.stdout, 0), daemon=True),
               threading.Thread(target=capture, args=(process.stderr, 1), daemon=True)]
    for thread in readers:
        thread.start()
    deadline = time.monotonic() + call["timeout_ms"] / 1000
    status = "completed"
    while process.poll() is None:
        if exceeded.is_set():
            status = "output_limit"
            break
        if time.monotonic() >= deadline:
            status = "timed_out"
            break
        time.sleep(0.02)
    if process.poll() is None:
        process.kill()
    code = process.wait(timeout=5)
    for thread in readers:
        thread.join(timeout=0.25)
    with lock:
        if status == "completed" and exceeded.is_set():
            status = "output_limit"
        if status == "completed" and code != 0:
            status = "failed"
        output = [bytes(value).decode("utf-8", errors="ignore") for value in captured]
    return status, code & 0xffffffff, output


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--job-id", required=True)
    parser.add_argument("--nonce", required=True)
    parser.add_argument("--runtime", required=True, type=Path)
    parser.add_argument("--inputs", required=True, type=Path)
    parser.add_argument("--requests", required=True, type=Path)
    parser.add_argument("--results", required=True, type=Path)
    options = parser.parse_args()
    for value in (options.job_id, options.nonce):
        if not 0 < len(value) <= 128 or any(not (c.isascii() and (c.isalnum() or c in "-_")) for c in value):
            raise ValueError("invalid session identity")
    for path in (options.runtime, options.inputs, options.requests, options.results):
        if not path.is_absolute() or not path.is_dir():
            raise ValueError("invalid sandbox directory")
    request_file = options.requests / "request.json"
    if request_file.stat().st_size > 2_097_152:
        raise ValueError("request exceeds limit")
    request = json.loads(request_file.read_text(encoding="utf-8"))
    call = request["call"]
    if (request["protocol_version"] != 1 or call["protocol_version"] != 1
            or request["job_id"] != options.job_id or call["job_id"] != options.job_id
            or request["nonce"] != options.nonce or request["call_id"] != call["call_id"]
            or not 0 < call["timeout_ms"] <= 3_600_000):
        raise ValueError("invalid computation request")
    result = {key: request[key] for key in ("protocol_version", "job_id", "call_id", "nonce", "digest")}
    result.update(status="ready", exit_code=None, stdout="", stderr="")
    atomic_json(options.results / "handshake.json", result)
    try:
        status, code, output = run_computation(request, options.runtime, options.inputs, options.results)
        result.update(status=status, exit_code=code, stdout=output[0], stderr=output[1])
    except Exception:
        result.update(status="failed", exit_code=None, stdout="", stderr="运行未完成，请检查代码和输入文件。")
    atomic_json(options.results / "response.json", result)


if __name__ == "__main__":
    main()
