import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import urllib.request
import zipfile
from pathlib import Path


def download(url, target, digest):
    if target.exists():
        with target.open("rb") as existing:
            if hashlib.file_digest(existing, "sha256").hexdigest() == digest:
                size = target.stat().st_size
                print(f"OHMS {size} {size} {target.name}", flush=True)
                return target
    partial = target.with_suffix(target.suffix + ".partial")
    checksum = hashlib.sha256()
    last_percent = -1
    last_bytes = -1

    def emit(done, total):
        nonlocal last_percent, last_bytes
        percent = done * 100 // total if total else 0
        if last_percent < 0 or done == total or percent > last_percent or done - last_bytes >= 8 * 1024 * 1024:
            print(f"OHMS {done} {total} {target.name}", flush=True)
            last_percent = percent
            last_bytes = done

    with urllib.request.urlopen(url, timeout=60) as response, partial.open("wb") as output:
        total = int(response.headers.get("Content-Length") or 0)
        done = 0
        emit(done, total)
        while data := response.read(1024 * 1024):
            output.write(data)
            checksum.update(data)
            done += len(data)
            emit(done, total)
    if checksum.hexdigest() != digest:
        partial.unlink(missing_ok=True)
        raise RuntimeError("Download checksum mismatch: " + target.name)
    partial.replace(target)
    return target


def run(arguments):
    subprocess.run([str(argument) for argument in arguments], check=True)


def discard_extracted_archives(root, filenames):
    for name in filenames:
        (root / name).unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--destination", required=True, type=Path)
    parser.add_argument("--device", required=True, type=int)
    args = parser.parse_args()
    root = args.destination
    root.mkdir(parents=True, exist_ok=True)
    (root / "verified.json").unlink(missing_ok=True)
    manifest = json.loads((Path(__file__).parent / "models.json").read_text(encoding="utf-8"))
    for asset in manifest["assets"]:
        download(asset["url"], root / asset["filename"], asset["sha256"])
    runtime = root / "python"
    runtime.mkdir(exist_ok=True)
    with zipfile.ZipFile(root / "python-3.13.2-embed-amd64.zip") as archive:
        archive.extractall(runtime)
    sites = runtime / "Lib/site-packages"
    sites.mkdir(parents=True, exist_ok=True)
    (runtime / "python313._pth").write_text(chr(10).join(["python313.zip", ".", "Lib/site-packages", "import site", ""]), encoding="utf-8")
    with urllib.request.urlopen("https://pypi.org/pypi/pip/25.3/json", timeout=60) as response:
        package = json.load(response)
    wheel = next(file for file in package["urls"] if file["filename"].endswith(".whl"))
    path = download(wheel["url"], root / wheel["filename"], wheel["digests"]["sha256"])
    with zipfile.ZipFile(path) as archive:
        archive.extractall(sites)
    python = runtime / "python.exe"
    print("Installing isolated CUDA runtime and PyTorch; this can download several GB.", flush=True)
    run([python, "-I", "-B", "-m", "pip", "install", "--progress-bar", "raw", "--disable-pip-version-check", "--only-binary=:all:", "torch==2.9.1", "torchvision==0.24.1", "--index-url", "https://download.pytorch.org/whl/cu128", "--report", root / "torch-install.json"])
    run([python, "-I", "-B", "-m", "pip", "install", "--progress-bar", "raw", "--disable-pip-version-check", "--only-binary=:all:", "numpy==2.2.6", "opencv-python-headless==4.12.0.88", "einops==0.8.1", "pillow==11.3.0", "--report", root / "image-install.json"])
    source = root / "source"
    source.mkdir(exist_ok=True)
    with zipfile.ZipFile(root / "docres-source.zip") as archive:
        archive.extractall(source)
    shutil.copyfile(source / "DocRes-d3e3a18c7c7ad10e4615ca2eb6a10f83128d8560/LICENSE", root / "DocRes-LICENSE.txt")
    print("Checking weights and running CUDA inference on the selected physical device.", flush=True)
    run([python, "-I", "-B", Path(__file__).parent / "worker.py", "--root", root, "--self-test", args.device])
    discard_extracted_archives(root, [asset["filename"] for asset in manifest["assets"] if asset["filename"].endswith(".zip")])
    (root / "verified.json").write_text(json.dumps({"schema": 1, "source": manifest["source"], "torch": "2.9.1+cu128", "device": args.device}), encoding="utf-8")
    print("DocRes installation verified. Enable it explicitly to use it.", flush=True)


if __name__ == "__main__":
    main()
