import argparse
import ctypes
import json
import sys
from pathlib import Path

import cv2
import numpy as np
import torch
import torch.nn.functional as functional


def cuda_device(root, ordinal):
    driver = ctypes.WinDLL("nvcuda.dll")
    if driver.cuInit(0) != 0:
        raise RuntimeError("The NVIDIA CUDA driver could not initialize")
    device = ctypes.c_int()
    if driver.cuDeviceGet(ctypes.byref(device), ordinal) != 0:
        raise RuntimeError("The selected NVIDIA device is unavailable")
    integrated = ctypes.c_int()
    memory = ctypes.c_size_t()
    if driver.cuDeviceGetAttribute(ctypes.byref(integrated), 18, device) != 0 or driver.cuDeviceTotalMem_v2(ctypes.byref(memory), device) != 0:
        raise RuntimeError("Could not inspect the selected NVIDIA device")
    if integrated.value != 0 or memory.value <= 12 * 1024 ** 3:
        raise RuntimeError("DocRes requires one discrete NVIDIA device with strictly more than 12 GiB dedicated VRAM")
    driver_bus = ctypes.create_string_buffer(32)
    if driver.cuDeviceGetPCIBusId(driver_bus, 32, device) != 0:
        raise RuntimeError("Could not identify the selected physical NVIDIA device")
    libraries = list((root / "python/Lib/site-packages/torch/lib").glob("cudart64_*.dll"))
    if not libraries:
        raise RuntimeError("The isolated CUDA runtime is missing")
    runtime = ctypes.WinDLL(str(libraries[0]))
    for index in range(torch.cuda.device_count()):
        runtime_bus = ctypes.create_string_buffer(32)
        if runtime.cudaDeviceGetPCIBusId(runtime_bus, 32, index) == 0 and runtime_bus.value.lower() == driver_bus.value.lower():
            if torch.cuda.get_device_properties(index).total_memory <= 12 * 1024 ** 3:
                raise RuntimeError("CUDA reports insufficient dedicated device memory")
            torch.cuda.set_device(index)
            torch.ones(1, device=torch.device("cuda", index)).add_(1)
            torch.cuda.synchronize(index)
            return torch.device("cuda", index)
    raise RuntimeError("No usable installed CUDA runtime matches the selected physical NVIDIA device")


def load_models(root, device):
    source = root / "source/DocRes-d3e3a18c7c7ad10e4615ca2eb6a10f83128d8560"
    sys.path.insert(0, str(source))
    sys.path.insert(0, str(source / "data/MBD"))
    from models.restormer_arch import Restormer
    from model.deep_lab_model.deeplab import DeepLab
    model = Restormer(inp_channels=6, out_channels=3, dim=48, num_blocks=[2, 3, 3, 4], num_refinement_blocks=4, heads=[1, 2, 4, 8], ffn_expansion_factor=2.66, bias=False, LayerNorm_type="WithBias", dual_pixel_task=True)
    state = torch.load(root / "docres.pkl", map_location="cpu", weights_only=True)["model_state"]
    model.load_state_dict({key.removeprefix("module."): value for key, value in state.items()}, strict=True)
    mask = DeepLab(num_classes=1, backbone="resnet", output_stride=16, sync_bn=None, freeze_bn=False)
    state = torch.load(root / "mbd.pkl", map_location="cpu", weights_only=True)["model_state"]
    mask.load_state_dict({key.removeprefix("module."): value for key, value in state.items()}, strict=True)
    return model.eval().to(device), mask.eval().to(device)


def mask_prompt(image, model, device):
    height, width = image.shape[:2]
    small = cv2.GaussianBlur(cv2.resize(image, (448, 448)), (15, 15), 0)
    small = cv2.cvtColor(small, cv2.COLOR_BGR2RGB).astype(np.float32) / 255
    tensor = torch.from_numpy(small.transpose(2, 0, 1).copy()).unsqueeze(0).to(device)
    with torch.inference_mode():
        prediction = model(tensor)[:, :1]
        prediction = functional.interpolate(prediction, (height, width))
    mask = (prediction[0, 0].cpu().numpy() * 255).astype(np.uint8)
    kernel = np.ones((3, 3), np.uint8)
    mask = cv2.erode(cv2.dilate(mask, kernel, iterations=3), kernel, iterations=3)
    mask[mask > 100] = 255
    mask[mask < 100] = 0
    masked = image.copy()
    masked[mask == 0] = 0
    x, y = np.meshgrid(np.arange(256), np.arange(256))
    base = np.stack((x, y), -1).astype(np.float32) / 256
    prompt = np.concatenate((base, (cv2.resize(mask, (256, 256)) / 255)[..., None]), -1)
    return masked, prompt.astype(np.float32), base


def dewarp(image, model, mask, device, pre_cropped=False):
    height, width = image.shape[:2]
    if pre_cropped:
        border = max(1, round(max(width, height) * 0.10))
        image = cv2.copyMakeBorder(image, border, border, border, border, cv2.BORDER_CONSTANT, value=(0, 0, 0))
    masked, prompt, base = mask_prompt(image, mask, device)
    small = cv2.resize(masked, (256, 256)).astype(np.float32) / 255
    tensor = torch.from_numpy(np.concatenate((small, prompt), -1).transpose(2, 0, 1).copy()).unsqueeze(0).to(device)
    with torch.inference_mode():
        prediction = model.float()(tensor)[0, :2].permute(1, 2, 0).cpu().numpy() + base
    if not np.isfinite(prediction).all():
        raise RuntimeError("DocRes returned a nonfinite dewarping grid")
    for _ in range(15):
        prediction = cv2.blur(prediction, (3, 3), borderType=cv2.BORDER_REPLICATE)
    source_height, source_width = image.shape[:2]
    grid = (cv2.resize(prediction, (width, height)) * (source_width, source_height)).astype(np.float32)
    return cv2.remap(image, grid[:, :, 0], grid[:, :, 1], cv2.INTER_LINEAR)


def background_prompt(image, appearance):
    height, width = image.shape[:2]
    small = cv2.resize(image, (1024, 1024))
    planes = []
    for plane in cv2.split(small):
        background = cv2.medianBlur(cv2.dilate(plane, np.ones((7, 7), np.uint8)), 21)
        if appearance:
            background = cv2.normalize(255 - cv2.absdiff(plane, background), None, 0, 255, cv2.NORM_MINMAX, dtype=cv2.CV_8UC1)
        planes.append(background)
    return cv2.resize(cv2.merge(planes), (width, height))


def padded(image):
    height, width = image.shape[:2]
    top, left = (-height) % 8, (-width) % 8
    return cv2.copyMakeBorder(image, top, 0, left, 0, cv2.BORDER_REPLICATE), top, left


def predict(image, prompt, model, device):
    combined = np.concatenate((image, prompt), -1).astype(np.float32) / 255
    tensor = torch.from_numpy(combined.transpose(2, 0, 1).copy()).unsqueeze(0).half().to(device)
    with torch.inference_mode():
        result = model.half()(tensor).clamp(0, 1)[0].permute(1, 2, 0).float().cpu().numpy()
    if not np.isfinite(result).all():
        raise RuntimeError("DocRes returned nonfinite pixels")
    return (result * 255).astype(np.uint8)


def photometric(image, model, device, appearance):
    height, width = image.shape[:2]
    prompt = background_prompt(image, appearance)
    if max(width, height) < 1600:
        combined, top, left = padded(np.concatenate((image, prompt), -1))
        return predict(combined[:, :, :3], combined[:, :, 3:], model, device)[top:, left:]
    small = cv2.resize(np.concatenate((image, prompt), -1), (1600, 1600))
    prediction = predict(small[:, :, :3], small[:, :, 3:], model, device)
    prediction[prediction == 0] = 1
    ratio = cv2.resize(image, (1600, 1600)).astype(np.float32) / prediction.astype(np.float32)
    ratio = cv2.resize(ratio, (width, height))
    return np.clip(image.astype(np.float32) / np.maximum(ratio, .00001), 0, 255).astype(np.uint8)


def deblur_prompt(image):
    x = cv2.convertScaleAbs(cv2.Sobel(image, cv2.CV_16S, 1, 0))
    y = cv2.convertScaleAbs(cv2.Sobel(image, cv2.CV_16S, 0, 1))
    return cv2.cvtColor(cv2.cvtColor(cv2.addWeighted(x, .5, y, .5, 0), cv2.COLOR_BGR2GRAY), cv2.COLOR_GRAY2BGR)


def positions(length, tile, overlap):
    points = list(range(0, max(1, length - tile + 1), tile - overlap))
    if points[-1] != max(0, length - tile):
        points.append(max(0, length - tile))
    return points


def deblur(image, model, device):
    height, width = image.shape[:2]
    tile, overlap = 768, 96
    result = np.zeros(image.shape, np.float32)
    weights = np.zeros((height, width, 1), np.float32)
    for top in positions(height, tile, overlap):
        for left in positions(width, tile, overlap):
            crop = image[top:top + tile, left:left + tile]
            block, pad_y, pad_x = padded(crop)
            prediction = predict(block, deblur_prompt(block), model, device)[pad_y:, pad_x:]
            h, w = crop.shape[:2]
            vertical, horizontal = np.ones(h, np.float32), np.ones(w, np.float32)
            if top > 0:
                vertical[:min(overlap, h)] = np.linspace(.01, 1, min(overlap, h))
            if top + h < height:
                vertical[-min(overlap, h):] = np.linspace(1, .01, min(overlap, h))
            if left > 0:
                horizontal[:min(overlap, w)] = np.linspace(.01, 1, min(overlap, w))
            if left + w < width:
                horizontal[-min(overlap, w):] = np.linspace(1, .01, min(overlap, w))
            weight = (vertical[:, None] * horizontal[None, :])[..., None]
            result[top:top + h, left:left + w] += prediction * weight
            weights[top:top + h, left:left + w] += weight
    return np.clip(result / np.maximum(weights, .00001), 0, 255).astype(np.uint8)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--request", type=Path)
    parser.add_argument("--self-test", type=int)
    args = parser.parse_args()
    request = json.loads(args.request.read_text(encoding="utf-8-sig")) if args.request else None
    device = cuda_device(args.root, request["deviceIndex"] if request else args.self_test)
    model, mask = load_models(args.root, device)
    if request is None:
        with torch.inference_mode():
            predictions = [model(torch.zeros((1, 6, 32, 32), device=device)), mask(torch.zeros((1, 3, 448, 448), device=device)), model.half()(torch.zeros((1, 6, 32, 32), device=device, dtype=torch.float16))]
            if any(not torch.isfinite(prediction).all().item() for prediction in predictions):
                raise RuntimeError("DocRes CUDA FP32/FP16 self-test returned nonfinite values")
        torch.cuda.synchronize(device)
        print(json.dumps({"device": str(device), "selfTest": "passed"}), flush=True)
        return
    width, height = request["width"], request["height"]
    if width < 2 or height < 2 or width * height > 40000000:
        raise ValueError("Invalid restoration image dimensions")
    image = np.fromfile(request["input"], np.uint8).reshape(height, width, 4)[:, :, :3].copy()
    tasks = request["tasks"]
    if not tasks or len(set(tasks)) != len(tasks) or any(task not in ["dewarping", "deshadowing", "deblurring", "appearance"] for task in tasks):
        raise ValueError("Invalid DocRes restoration tasks")
    for task in ["dewarping", "deshadowing", "deblurring", "appearance"]:
        if task not in tasks:
            continue
        print(task, flush=True)
        if task == "dewarping":
            image = dewarp(image, model, mask, device, request.get("preCropped", False))
        elif task == "deblurring":
            image = deblur(image, model, device)
        else:
            image = photometric(image, model, device, task == "appearance")
    output = cv2.cvtColor(image, cv2.COLOR_BGR2BGRA)
    output.tofile(request["output"])


if __name__ == "__main__":
    main()
