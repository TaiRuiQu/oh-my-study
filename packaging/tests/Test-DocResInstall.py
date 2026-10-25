import importlib.util
import json
import tempfile
from pathlib import Path


root = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("docres_install", root / "packaging/docres/install.py")
install = importlib.util.module_from_spec(spec)
spec.loader.exec_module(install)
manifest = json.loads((root / "packaging/docres/models.json").read_text(encoding="utf-8"))
archives = [asset["filename"] for asset in manifest["assets"] if asset["filename"].endswith(".zip")]
kept = [asset["filename"] for asset in manifest["assets"] if not asset["filename"].endswith(".zip")]
assert archives == ["python-3.13.2-embed-amd64.zip", "docres-source.zip"]
assert kept == ["docres.pkl", "mbd.pkl"]

with tempfile.TemporaryDirectory() as directory:
    destination = Path(directory)
    for name in archives + kept + ["pip-25.3-py3-none-any.whl", "verified.json"]:
        (destination / name).write_bytes(b"payload")
    install.discard_extracted_archives(destination, archives)
    assert all(not (destination / name).exists() for name in archives)
    assert all((destination / name).read_bytes() == b"payload" for name in kept + ["pip-25.3-py3-none-any.whl", "verified.json"])
    install.discard_extracted_archives(destination, archives)

print("DocRes install removes extracted zip archives and keeps weights.")
