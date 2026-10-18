"""Export a minimal .wsb and one reviewable request; NEVER starts a session.

M0 manual test helper mirrors Rust SandboxMappings. Real launch/stop ownership
must be verified on Win10/11 before it becomes an application executor.
"""
import argparse
import json
from pathlib import Path
import re
import secrets
import shutil
import xml.etree.ElementTree as ET

ID = re.compile(r"[A-Za-z0-9_-]{1,128}\Z")


def checked(path):
    path = path.absolute()
    for parent in [path, *path.parents]:
        stat = parent.lstat()
        if parent.is_symlink() or getattr(stat, "st_file_attributes", 0) & 0x400:
            raise ValueError("reparse/symlink mapping rejected")
    path = path.resolve(strict=True)
    if not path.is_dir() or str(path).startswith("\\\\"):
        raise ValueError("requires local absolute directory")
    return path


def export(runtime, tasks_root, job_id, networking=False, code=None, inputs=()):
    if not ID.fullmatch(job_id):
        raise ValueError("invalid job id")
    runtime = checked(runtime)
    for name in ["python.exe", "guest_runner.py", "host_entry.py", "winjob.py"]:
        if not (runtime / name).is_file():
            raise ValueError(f"runtime missing {name}")
    tasks_root.mkdir(parents=True, exist_ok=True)
    tasks_root = checked(tasks_root)
    task = tasks_root / job_id
    task.mkdir()  # Must be new; do not overwrite/replay a previous task.
    for name in ["inputs", "requests", "results"]:
        (task / name).mkdir()
    for source in inputs:
        if source.is_symlink() or not source.is_file():
            raise ValueError("input must be a regular file")
        shutil.copy2(source, task / "inputs" / source.name)
    nonce = secrets.token_hex(32)
    config = ET.Element("Configuration")
    for key, value in {"VGpu": "Disable", "Networking": "Enable" if networking else "Disable",
                       "ClipboardRedirection": "Disable", "AudioInput": "Disable", "VideoInput": "Disable",
                       "PrinterRedirection": "Disable", "MemoryInMB": "2048"}.items():
        ET.SubElement(config, key).text = value
    mappings = ET.SubElement(config, "MappedFolders")
    for host, guest, readonly in [(runtime, "Runtime", True), (task / "inputs", "Inputs", True),
                                  (task / "requests", "Requests", True), (task / "results", "Results", False)]:
        if host == runtime and (runtime.is_relative_to(task) or task.is_relative_to(runtime)):
            raise ValueError("runtime and task roots must not overlap")
        mapping = ET.SubElement(mappings, "MappedFolder")
        ET.SubElement(mapping, "HostFolder").text = str(checked(host))
        ET.SubElement(mapping, "SandboxFolder").text = f"C:\\OMS\\{guest}"
        ET.SubElement(mapping, "ReadOnly").text = str(readonly).lower()
    command = f"C:\\OMS\\Runtime\\python.exe -I C:\\OMS\\Runtime\\guest_runner.py --job-id {job_id} --nonce {nonce} --requests C:\\OMS\\Requests --results C:\\OMS\\Results --inputs C:\\OMS\\Inputs"
    ET.SubElement(ET.SubElement(config, "LogonCommand"), "Command").text = command
    ET.indent(config)
    ET.ElementTree(config).write(task / "session.wsb", encoding="utf-8", xml_declaration=True)
    code = code if code is not None else "import sys, numpy, sympy, matplotlib; print('bridge 中文'); print(sys.executable); print(numpy.__version__, sympy.__version__, matplotlib.__version__)"
    request = {"protocol_version": 1, "job_id": job_id, "call_id": "probe", "tool": "python", "content": code,
               "arguments": [], "working_directory": "C:\\OMS\\Results", "timeout_ms": 30000}
    temporary = task / "requests/probe.pending"
    temporary.write_text(json.dumps(request, ensure_ascii=False, indent=2), "utf-8")
    temporary.replace(task / "requests/probe.json")
    (task / "session-host.json").write_text(json.dumps({"job_id": job_id, "session_nonce": nonce,
        "launch_tested": False, "ownership_confirmed": False}, indent=2), "utf-8")
    print(json.dumps({"wsb": str(task / "session.wsb"), "request": str(task / "requests/probe.json"),
                      "launched": False}, ensure_ascii=False))
    return task


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--tasks-root", type=Path, required=True)
    parser.add_argument("--job-id", required=True)
    parser.add_argument("--networking", action="store_true")
    parser.add_argument("--code-file", type=Path)
    parser.add_argument("--input", type=Path, action="append", default=[])
    args = parser.parse_args()
    export(args.runtime, args.tasks_root, args.job_id, args.networking,
           args.code_file.read_text("utf-8") if args.code_file else None, args.input)
