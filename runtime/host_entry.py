"""Trusted host wrapper. Rust places this process in an owned Windows Job first.

Rust clears environment before startup. This file never accesses application
configuration, credentials, databases, or provider APIs.
"""
import json
import base64
import os
from pathlib import Path
import subprocess
import sys
import traceback


def run(request_file, results):
    request = json.loads(Path(request_file).read_text("utf-8"))
    results = Path(results).resolve()
    call_id = request["call_id"]
    with (results / f"{call_id}.stdout.log").open("wb", buffering=0) as out, (results / f"{call_id}.stderr.log").open("wb", buffering=0) as err:
        os.dup2(out.fileno(), 1)
        os.dup2(err.fileno(), 2)
        # CREATE_NO_WINDOW may leave Python's initial _WindowsConsoleIO handles
        # invalid. Replace those wrappers rather than merely changing encoding.
        sys.stdout = open(os.dup(out.fileno()), "w", encoding="utf-8", errors="replace", buffering=1)
        sys.stderr = open(os.dup(err.fileno()), "w", encoding="utf-8", errors="replace", buffering=1)
        if os.name == "nt":
            import ctypes
            import msvcrt
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel.SetStdHandle.argtypes = [ctypes.c_uint32, ctypes.c_void_p]
            kernel.SetStdHandle(0xFFFFFFF5, msvcrt.get_osfhandle(out.fileno()))
            kernel.SetStdHandle(0xFFFFFFF4, msvcrt.get_osfhandle(err.fileno()))
        os.chdir(request["working_directory"])
        os.environ["MPLBACKEND"] = "Agg"
        os.environ["MPLCONFIGDIR"] = str(results / "matplotlib-cache")
        try:
            if request["tool"] == "python":
                sys.argv = ["<approved-call>", *request["arguments"]]
                exec(compile(request["content"], "<approved-call>", "exec"), {"__name__": "__main__"})
            elif request["tool"] == "shell":
                # Execute the approved command through an encoded fixed wrapper.
                # ScriptBlock handles the reviewed content; array splatting preserves
                # arguments as data. No execution-policy switch or .ps1 bypass flag.
                request_literal = "'" + str(Path(request_file).resolve()).replace("'", "''") + "'"
                wrapper = (
                    "$ErrorActionPreference='Stop'; "
                    "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); "
                    f"$callRequest=([IO.File]::ReadAllText({request_literal}) | ConvertFrom-Json); "
                    "$callArguments=@($callRequest.arguments | ForEach-Object { [string]$_ }); "
                    "& ([scriptblock]::Create([string]$callRequest.content)) @callArguments; "
                    "if (-not $?) { exit 1 }"
                )
                encoded = base64.b64encode(wrapper.encode("utf-16-le")).decode("ascii")
                shell = Path(os.environ["SystemRoot"]) / "System32/WindowsPowerShell/v1.0/powershell.exe"
                return subprocess.call([str(shell), "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
                    stdin=subprocess.DEVNULL, stdout=sys.stdout, stderr=sys.stderr, creationflags=0x08000000)
            else:
                raise ValueError("unsupported tool")
        except SystemExit as exc:
            if exc.code is None:
                return 0
            if isinstance(exc.code, int):
                return exc.code
            print(exc.code, file=sys.stderr)
            return 1
        except BaseException:
            traceback.print_exc()
            return 1
        finally:
            sys.stdout.flush()
            sys.stderr.flush()
        return 0


if __name__ == "__main__":
    raise SystemExit(run(sys.argv[1], sys.argv[2]))
