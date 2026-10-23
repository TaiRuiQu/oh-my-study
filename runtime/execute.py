"""Private computation entry point. The C# host owns policy, cancellation and the process tree."""
import json
import os
import sys
import time
import traceback
from pathlib import Path


def main():
    request_path = Path(sys.argv[1])
    if request_path.stat().st_size > 2 * 1024 * 1024:
        raise ValueError("request exceeds limit")
    request = json.loads(request_path.read_text(encoding="utf-8"))
    if request["protocol_version"] != 1 or request["tool"] != "python":
        raise ValueError("invalid computation request")
    sys.stdout.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
    sys.stderr.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
    sys.argv = ["<approved-call>", *request["arguments"]]
    exec(compile(request["content"], "<approved-call>", "exec"), {"__name__": "__main__"})


def session(requests, results, working_directory, job_id):
    namespace = {"__name__": "__main__"}
    completed = set()
    stdout, stderr = sys.stdout, sys.stderr
    stdout.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
    stderr.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
    while True:
        request_file = next((path for path in requests.glob("*.json") if path.stem not in completed), None)
        if request_file is None:
            time.sleep(0.02)
            continue
        if request_file.stat().st_size > 2 * 1024 * 1024:
            raise ValueError("request exceeds limit")
        request = json.loads(request_file.read_text(encoding="utf-8"))
        call_id = request["call_id"]
        if (request["protocol_version"] != 1 or request["tool"] != "python"
                or request["job_id"] != job_id or call_id != request_file.stem or not call_id
                or any(not (c.isascii() and (c.isalnum() or c in "-_")) for c in call_id)):
            raise ValueError("invalid computation request")
        completed.add(call_id)
        output = results / call_id
        saved = os.dup(1), os.dup(2)
        status, code = "completed", 0
        try:
            with (output / "stdout.txt").open("wb") as out, (output / "stderr.txt").open("wb") as err:
                os.dup2(out.fileno(), 1)
                os.dup2(err.fileno(), 2)
                os.chdir(working_directory)
                sys.argv = ["<approved-call>", *request["arguments"]]
                try:
                    exec(compile(request["content"], "<approved-call>", "exec"), namespace)
                except BaseException as error:
                    code = error.code if isinstance(error, SystemExit) and isinstance(error.code, int) else 1
                    code &= 0xffffffff
                    status = "completed" if code == 0 else "failed"
                    if code != 0:
                        traceback.print_exc(file=stderr)
                finally:
                    sys.stdout, sys.stderr = stdout, stderr
                    stdout.flush()
                    stderr.flush()
        finally:
            for target, source in zip((1, 2), saved):
                os.dup2(source, target)
                os.close(source)
        temporary = output / "response.pending"
        temporary.write_text(json.dumps({"status": status, "exit_code": code}), encoding="utf-8")
        os.replace(temporary, output / "response.json")


if __name__ == "__main__":
    if len(sys.argv) == 6 and sys.argv[1] == "--session":
        session(Path(sys.argv[2]), Path(sys.argv[3]), Path(sys.argv[4]), sys.argv[5])
    else:
        main()
