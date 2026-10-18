"""Developer-only lock refresh from already downloaded, version-selected wheels.

Not used by installation or execution. Review resulting diff/provenance before use.
"""
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile
from email.parser import BytesParser

root = Path(__file__).resolve().parent
cache = root / "wheelhouse"
python_name = "python-3.13.2-embed-amd64.zip"
python_url = f"https://www.python.org/ftp/python/3.13.2/{python_name}"
if not (cache / python_name).exists():
    urllib.request.urlretrieve(python_url, cache / python_name)
lock = {"schema_version": 1, "platform": "windows-x86_64", "cpython": {
    "version": "3.13.2", "filename": python_name, "url": python_url,
    "sha256": hashlib.sha256((cache / python_name).read_bytes()).hexdigest()}, "wheels": []}
for wheel in sorted(cache.glob("*.whl")):
    with zipfile.ZipFile(wheel) as source:
        metadata_name = next(x for x in source.namelist() if x.endswith(".dist-info/METADATA"))
        metadata = BytesParser().parsebytes(source.read(metadata_name))
    with urllib.request.urlopen(f"https://pypi.org/pypi/{metadata['Name']}/{metadata['Version']}/json") as response:
        release = json.load(response)
    published = next(x for x in release["urls"] if x["filename"] == wheel.name)
    sha256 = hashlib.sha256(wheel.read_bytes()).hexdigest()
    if sha256 != published["digests"]["sha256"]:
        raise ValueError(f"PyPI digest mismatch: {wheel.name}")
    lock["wheels"].append({"name": metadata["Name"], "version": metadata["Version"],
        "filename": wheel.name, "url": published["url"], "sha256": sha256})
(root / "runtime-lock.json").write_text(json.dumps(lock, indent=2) + "\n", "utf-8")
print(f"Locked CPython + {len(lock['wheels'])} wheels")
