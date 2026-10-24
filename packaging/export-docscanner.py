import argparse
import sys

import numpy as np
import onnx
import onnxruntime as ort
import torch
from torch import nn


parser = argparse.ArgumentParser()
parser.add_argument("--source", required=True)
parser.add_argument("--segmentation", required=True)
parser.add_argument("--rectifier", required=True)
parser.add_argument("--output", required=True)
args = parser.parse_args()
sys.path.insert(0, args.source)
from model import DocScanner
from seg import U2NETP

torch.set_num_threads(4)


class Combined(nn.Module):
    def __init__(self):
        super().__init__()
        self.msk = U2NETP(3, 1)
        self.bm = DocScanner()
        segmentation = torch.load(args.segmentation, map_location="cpu", weights_only=True)
        rectifier = torch.load(args.rectifier, map_location="cpu", weights_only=True)
        self.msk.load_state_dict({key[6:]: value for key, value in segmentation.items()}, strict=True)
        self.bm.load_state_dict(rectifier, strict=True)

    def forward(self, image):
        mask = self.msk(image)[0]
        backward = self.bm(image * (mask > 0.5).float(), iters=12, test_mode=True)
        return (2 * (backward / 286.8) - 1) * 0.99


model = Combined().eval()
torch.manual_seed(0)
tensor = torch.rand(1, 3, 288, 288)
with torch.no_grad():
    expected = model(tensor).numpy()
    torch.onnx.export(model, tensor, args.output, input_names=["image"], output_names=["backward_grid"], opset_version=17, dynamo=False, external_data=False)
onnx.checker.check_model(args.output)
options = ort.SessionOptions()
options.intra_op_num_threads = 4
options.inter_op_num_threads = 1
session = ort.InferenceSession(args.output, sess_options=options, providers=["CPUExecutionProvider"])
actual = session.run(None, {"image": tensor.numpy()})[0]
if actual.shape != (1, 2, 288, 288) or not np.isfinite(actual).all() or np.max(np.abs(actual - expected)) >= .002:
    raise RuntimeError("The exported DocScanner backward grid failed PyTorch parity verification")
print("DocScanner export verified; maximum normalized-grid error=" + str(np.max(np.abs(actual - expected))))
