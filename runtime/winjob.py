"""Owned Windows process tree, no global process-name kills or PID reacquisition.

The root starts suspended, joins a kill-on-close Job, then resumes. Job lifetime
also covers descendants after normal root completion. No breakaway is enabled.
"""
import ctypes as C
from ctypes import wintypes as W
import os
from pathlib import Path
import subprocess
import time


class BasicLimits(C.Structure):
    _fields_ = [("process_time", C.c_int64), ("job_time", C.c_int64), ("flags", W.DWORD),
                ("minimum", C.c_size_t), ("maximum", C.c_size_t), ("active", W.DWORD),
                ("affinity", C.c_size_t), ("priority", W.DWORD), ("scheduling", W.DWORD)]


class ExtendedLimits(C.Structure):
    _fields_ = [("basic", BasicLimits), ("io", C.c_uint64 * 6),
                ("process_memory", C.c_size_t), ("job_memory", C.c_size_t),
                ("peak_process", C.c_size_t), ("peak_job", C.c_size_t)]


class Startup(C.Structure):
    _fields_ = [("cb", W.DWORD), ("reserved", W.LPWSTR), ("desktop", W.LPWSTR),
                ("title", W.LPWSTR), ("x", W.DWORD), ("y", W.DWORD), ("xsize", W.DWORD),
                ("ysize", W.DWORD), ("xchars", W.DWORD), ("ychars", W.DWORD),
                ("fill", W.DWORD), ("flags", W.DWORD), ("show", W.WORD),
                ("reserved_size", W.WORD), ("reserved2", C.POINTER(C.c_byte)),
                ("stdin", W.HANDLE), ("stdout", W.HANDLE), ("stderr", W.HANDLE)]


class Process(C.Structure):
    _fields_ = [("process", W.HANDLE), ("thread", W.HANDLE), ("pid", W.DWORD), ("tid", W.DWORD)]


def run_owned(command, cwd, env, timeout_ms, cancelled, log_paths, output_limit=4 * 1024 * 1024):
    if os.name != "nt":
        raise RuntimeError("WinJob requires Windows")
    kernel = C.WinDLL("kernel32", use_last_error=True)
    signatures = {
        "CreateJobObjectW": (W.HANDLE, [C.c_void_p, W.LPCWSTR]),
        "SetInformationJobObject": (W.BOOL, [W.HANDLE, C.c_int, C.c_void_p, W.DWORD]),
        "CreateProcessW": (W.BOOL, [W.LPCWSTR, W.LPWSTR, C.c_void_p, C.c_void_p, W.BOOL, W.DWORD, C.c_void_p, W.LPCWSTR, C.POINTER(Startup), C.POINTER(Process)]),
        "AssignProcessToJobObject": (W.BOOL, [W.HANDLE, W.HANDLE]),
        "ResumeThread": (W.DWORD, [W.HANDLE]), "WaitForSingleObject": (W.DWORD, [W.HANDLE, W.DWORD]),
        "GetExitCodeProcess": (W.BOOL, [W.HANDLE, C.POINTER(W.DWORD)]),
        "TerminateProcess": (W.BOOL, [W.HANDLE, W.UINT]), "CloseHandle": (W.BOOL, [W.HANDLE]),
    }
    for name, (result, args) in signatures.items():
        getattr(kernel, name).restype = result
        getattr(kernel, name).argtypes = args
    def checked(result):
        if not result:
            raise C.WinError(C.get_last_error())
        return result
    job = checked(kernel.CreateJobObjectW(None, None))
    process = Process()
    try:
        limits = ExtendedLimits()
        limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        checked(kernel.SetInformationJobObject(job, 9, C.byref(limits), C.sizeof(limits)))
        startup = Startup()
        startup.cb = C.sizeof(startup)
        if any("\0" in x for x in command) or any("\0" in k + v for k, v in env.items()):
            raise ValueError("NUL in process data")
        environment = C.create_unicode_buffer("\0".join(f"{k}={v}" for k, v in sorted(env.items(), key=lambda x: x[0].lower())) + "\0\0")
        checked(kernel.CreateProcessW(command[0], C.create_unicode_buffer(subprocess.list2cmdline(command)),
            None, None, False, 0x08000000 | 4 | 0x400, environment, str(cwd), C.byref(startup), C.byref(process)))
        if not kernel.AssignProcessToJobObject(job, process.process):
            error = C.WinError(C.get_last_error())
            kernel.TerminateProcess(process.process, 1)
            kernel.WaitForSingleObject(process.process, 5000)
            raise error
        if cancelled():
            return "cancelled", None
        if kernel.ResumeThread(process.thread) == 0xFFFFFFFF:
            raise C.WinError(C.get_last_error())
        kernel.CloseHandle(process.thread)
        process.thread = None
        started = time.monotonic()
        status = "completed"
        while True:
            if cancelled():
                status = "cancelled"
                break
            if (time.monotonic() - started) * 1000 >= timeout_ms:
                status = "timed_out"
                break
            if sum(p.stat().st_size for p in log_paths if p.exists()) > output_limit:
                status = "failed"
                break
            wait = kernel.WaitForSingleObject(process.process, 50)
            if wait == 0:
                break
            if wait != 258:
                raise C.WinError(C.get_last_error())
        kernel.CloseHandle(job)
        job = None
        if kernel.WaitForSingleObject(process.process, 5000) != 0:
            raise RuntimeError("owned tree did not terminate")
        code = W.DWORD()
        checked(kernel.GetExitCodeProcess(process.process, C.byref(code)))
        if status == "completed" and (code.value or sum(p.stat().st_size for p in log_paths if p.exists()) > output_limit):
            status = "failed"
        return status, code.value
    finally:
        if job:
            kernel.CloseHandle(job)
        if process.thread:
            kernel.CloseHandle(process.thread)
        if process.process:
            kernel.CloseHandle(process.process)
