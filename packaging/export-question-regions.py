import hashlib
import json
import os
import shutil
import urllib.request
from pathlib import Path

os.environ['YOLO_AUTOINSTALL'] = 'false'
os.environ['YOLO_CONFIG_DIR'] = str(Path(__file__).resolve().parent.parent / 'output' / 'question-regions-export' / 'config')

from ultralytics import YOLO

root = Path(__file__).resolve().parent.parent
destination = root / 'output' / 'migration' / 'toolchain' / 'question-regions'
destination.mkdir(parents=True, exist_ok=True)
revision = '30466102640b08833cd5356440989820b87871b7'
url = f'https://huggingface.co/erayyapagci/yolo26n-question-segmentation-v2/resolve/{revision}/best.pt'
expected = '2cdd0d47695bc69da469446376defde0b8e96ee307044e724c56ed5a5f29f964'
checkpoint = destination / 'best.pt'
if not checkpoint.exists():
    urllib.request.urlretrieve(url, checkpoint)
if hashlib.sha256(checkpoint.read_bytes()).hexdigest() != expected:
    raise ValueError('Question detector checkpoint checksum mismatch.')
model = YOLO(str(checkpoint), task='detect')
model.info = lambda *args, **kwargs: None
exported = Path(model.export(format='onnx', imgsz=1280, dynamic=False, nms=False, simplify=False, opset=17, device='cpu', batch=1))
output = destination / 'base.onnx'
shutil.copyfile(exported, output)
(destination / 'model.json').write_text(json.dumps({
    'repository': 'erayyapagci/yolo26n-question-segmentation-v2',
    'revision': revision, 'checkpoint_sha256': expected,
    'onnx_sha256': hashlib.sha256(output.read_bytes()).hexdigest(),
    'input': [1, 3, 1280, 1280], 'output': '[1,N,6] xyxy,confidence,class',
    'license': 'CC-BY-4.0', 'chinese_fine_tuned': False,
}, indent=2) + '\n', encoding='utf-8')
shutil.copyfile(root / 'packaging' / 'question-regions-NOTICE.md', destination / 'NOTICE.md')
print(output)
