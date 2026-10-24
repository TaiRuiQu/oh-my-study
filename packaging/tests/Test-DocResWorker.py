import ast
import importlib.util
from pathlib import Path

import numpy as np
import torch


root = Path(__file__).resolve().parents[2]
for script in ["packaging/docres/worker.py", "packaging/docres/install.py", "packaging/export-docscanner.py"]:
    ast.parse((root / script).read_text(encoding="utf-8"))
spec = importlib.util.spec_from_file_location("docres_worker", root / "packaging/docres/worker.py")
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)
for height, width in [(2, 2), (17, 1025), (1025, 17), (801, 1003)]:
    image = np.random.default_rng(4).integers(0, 256, (height, width, 3), np.uint8)
    padded, top, left = worker.padded(image)
    assert padded.shape[0] % 8 == 0 and padded.shape[1] % 8 == 0
    assert np.array_equal(padded[top:, left:], image)
    worker.predict = lambda source, prompt, model, device: source.copy()
    result = worker.deblur(image, None, None)
    assert result.shape == image.shape
    assert np.max(np.abs(result.astype(np.int16) - image.astype(np.int16))) <= 1
    assert np.isfinite(result).all()
    for appearance in [False, True]:
        assert worker.background_prompt(image, appearance).shape == image.shape


class SelectedRegionGrid(torch.nn.Module):
    def forward(self, tensor):
        return torch.from_numpy((selected_grid - base).transpose(2, 0, 1).copy()).unsqueeze(0)


original_blur = worker.cv2.blur
worker.cv2.blur = lambda source, *args, **kwargs: source
for height, width in [(2, 2), (17, 81), (103, 19), (40, 30)]:
    image = np.random.default_rng(9).integers(1, 256, (height, width, 3), np.uint8)
    for pre_cropped in [False, True]:
        border = max(1, round(max(width, height) * 0.10)) if pre_cropped else 0
        source_height, source_width = height + 2 * border, width + 2 * border
        x, y = np.meshgrid(np.arange(256), np.arange(256))
        base = np.stack((x, y), -1).astype(np.float32) / 256
        selected_grid = np.stack(((border + (x + .5) * width / 256 - .5) / source_width, (border + (y + .5) * height / 256 - .5) / source_height), -1).astype(np.float32)

        def selected_prompt(source, model, device):
            assert source.shape == (source_height, source_width, 3)
            if pre_cropped:
                assert not source[:border].any() and not source[-border:].any()
                assert not source[:, :border].any() and not source[:, -border:].any()
                assert np.array_equal(source[border:-border, border:-border], image)
            else:
                assert np.array_equal(source, image)
            return source, np.zeros((256, 256, 3), np.float32), base

        worker.mask_prompt = selected_prompt
        result = worker.dewarp(image, SelectedRegionGrid(), None, torch.device("cpu"), True) if pre_cropped else worker.dewarp(image, SelectedRegionGrid(), None, torch.device("cpu"))
        assert result.shape == image.shape
        assert np.max(np.abs(result.astype(np.int16) - image.astype(np.int16))) <= 1
worker.cv2.blur = original_blur


class InvalidGrid(torch.nn.Module):
    def forward(self, tensor):
        return torch.full((1, 3, 256, 256), float("nan"))


image = np.full((40, 30, 3), 200, np.uint8)
worker.mask_prompt = lambda source, model, device: (source, np.zeros((256, 256, 3), np.float32), np.zeros((256, 256, 2), np.float32))
try:
    worker.dewarp(image, InvalidGrid(), None, torch.device("cpu"))
    raise AssertionError("A nonfinite DocRes backward grid was accepted")
except RuntimeError as error:
    assert "nonfinite" in str(error)
print("DocRes adapter syntax, padding, tile coverage, identity joins, cropped dewarp context and coordinates, prompts and invalid-grid checks passed.")
