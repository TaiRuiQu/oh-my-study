"""Assemble a private runtime using only SHA256-locked archives (no pip at runtime).

Bootstrap with a developer Python. The produced python.exe and vendored packages
do not use that interpreter or its site-packages. Never overwrite an existing build.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import urllib.request
import zipfile


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def extract(archive, destination):
    with zipfile.ZipFile(archive) as source:
        for item in source.infolist():
            target = (destination / item.filename).resolve()
            if not target.is_relative_to(destination.resolve()) or "\\" in item.filename:
                raise ValueError("archive path escaped destination")
        source.extractall(destination)


def build(lock_path, cache, destination, offline=False):
    lock = json.loads(lock_path.read_text("utf-8"))
    if destination.exists():
        raise ValueError("destination must be new; preserve prior build")
    cache.mkdir(parents=True, exist_ok=True)
    archives = []
    for item in [lock["cpython"], *lock["wheels"]]:
        if Path(item["filename"]).name != item["filename"]:
            raise ValueError("invalid archive filename")
        path = cache / item["filename"]
        if not path.exists():
            if offline:
                raise ValueError(f"offline archive missing: {path.name}")
            if not item["url"].startswith(("https://www.python.org/", "https://files.pythonhosted.org/")):
                raise ValueError("unexpected archive origin")
            pending = path.with_suffix(".pending")
            urllib.request.urlretrieve(item["url"], pending)
            if digest(pending) != item["sha256"]:
                raise ValueError(f"archive hash mismatch: {path.name}")
            pending.replace(path)
        if digest(path) != item["sha256"]:
            raise ValueError(f"archive hash mismatch: {path.name}")
        archives.append(path)
    destination.mkdir(parents=True)
    extract(archives[0], destination)
    packages = destination / "Lib" / "site-packages"
    packages.mkdir(parents=True)
    for archive in archives[1:]:
        extract(archive, packages)
    (destination / "python313._pth").write_text("python313.zip\n.\nLib\\site-packages\nimport site\n", "utf-8")
    for name in ["guest_runner.py", "host_entry.py", "winjob.py", "runtime_smoke.py"]:
        shutil.copy2(lock_path.parent / name, destination / name)
    shutil.copy2(lock_path, destination / "runtime-lock.json")
    print(json.dumps({"runtime": str(destination.resolve()), "archives_verified": len(archives)}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--lock", type=Path, default=Path(__file__).with_name("runtime-lock.json"))
    parser.add_argument("--cache", type=Path, required=True)
    parser.add_argument("--destination", type=Path, required=True)
    parser.add_argument("--offline", action="store_true")
    args = parser.parse_args()
    build(args.lock.resolve(), args.cache.resolve(), args.destination.resolve(), args.offline)
