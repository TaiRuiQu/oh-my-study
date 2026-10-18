"""Private computation entry point. The C# host owns policy, cancellation and the process tree."""
import json
import sys
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


if __name__ == "__main__":
    main()
