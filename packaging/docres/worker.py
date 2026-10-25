import argparse
import ast
import ctypes
import json
import sys
import types
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


def official_functions(root, device, mask):
    source = root / "source/DocRes-d3e3a18c7c7ad10e4615ca2eb6a10f83128d8560"
    namespace = {"cv2": cv2, "np": np, "torch": torch, "DEVICE": device}
    modules = [
        (source / "data/preprocess/crop_merge_image.py", {"stride_integral"}),
        (source / "utils.py", {"getBasecoord"}),
        (source / "inference.py", {"deshadow_prompt", "deblur_prompt", "appearance_prompt", "dewarping", "deshadowing", "appearance", "deblurring"}),
    ]
    for path, names in modules:
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
        definitions = [node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name in names]
        if {node.name for node in definitions} != names:
            raise RuntimeError("The pinned official DocRes inference functions are incomplete")
        exec(compile(ast.Module(body=definitions, type_ignores=[]), str(path), "exec"), namespace)
    namespace["utils"] = types.SimpleNamespace(getBasecoord=namespace["getBasecoord"])
    namespace["dewarp_prompt"] = lambda image: mask_prompt(image, mask, device)[:2]
    return namespace


def restore_official(original, output, tasks, model, functions, progress=print):
    current = Path(original)
    result = None
    for task in ["dewarping", "deshadowing", "appearance", "deblurring"]:
        if task not in tasks:
            continue
        progress(task, flush=True)
        _, _, _, result = functions[task](model, str(current))
        current = Path(output).parent / ("stage-" + task + ".png")
        if not cv2.imwrite(str(current), result):
            raise RuntimeError("Could not save the official DocRes task result")
    return result


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
    tasks = request["tasks"]
    if not tasks or len(set(tasks)) != len(tasks) or any(task not in ["dewarping", "deshadowing", "deblurring", "appearance"] for task in tasks):
        raise ValueError("Invalid DocRes restoration tasks")
    encoded = request.get("encoded", False)
    original = Path(request["input"])
    if encoded:
        image = cv2.imread(str(original))
        if image is None:
            raise ValueError("Could not decode the original photo")
        height, width = image.shape[:2]
    else:
        width, height = request["width"], request["height"]
    if width < 2 or height < 2 or width * height > 40000000:
        raise ValueError("Invalid restoration image dimensions")
    if not encoded:
        image = np.fromfile(original, np.uint8).reshape(height, width, 4)[:, :, :3].copy()
        original = Path(request["output"]).parent / "original.png"
        if not cv2.imwrite(str(original), image):
            raise RuntimeError("Could not encode the restoration input")
    functions = official_functions(args.root, device, mask)
    result = restore_official(original, request["output"], tasks, model, functions)
    if encoded:
        if not cv2.imwrite(request["output"], result):
            raise RuntimeError("Could not encode the official DocRes result")
    else:
        cv2.cvtColor(result, cv2.COLOR_BGR2BGRA).tofile(request["output"])


if __name__ == "__main__":
    main()
