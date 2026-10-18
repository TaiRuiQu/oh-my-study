"""Run with private python.exe -I runtime/plot_probe.py --output <Chinese path>.

Font must be supplied explicitly; release fonts are owned by the print pipeline.
System fonts may be used only for this developer probe, never copied into a bundle.
"""
import argparse
import json
from pathlib import Path
import os
import sys
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--font", type=Path, required=True)
    parser.add_argument("--hold-seconds", type=float, default=0)
    options = parser.parse_args()
    options.output.mkdir(parents=True, exist_ok=True)
    os.environ["MPLCONFIGDIR"] = str(options.output / "mpl-cache")
    os.environ["MPLBACKEND"] = "Agg"
    os.environ["OMP_NUM_THREADS"] = "2"
    os.environ["OPENBLAS_NUM_THREADS"] = "2"
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.font_manager import FontProperties
    import numpy as np
    import sympy
    from PIL import Image
    font = FontProperties(fname=str(options.font.resolve()))
    x = np.linspace(-2, 2, 200)
    fig, axes = plt.subplots(1, 2, figsize=(8, 3), layout="constrained")
    axes[0].plot(x, x * x, label=r"$y=x^2$")
    axes[0].set_title("函数图：二次函数", fontproperties=font)
    axes[0].legend()
    axes[1].plot([0, 1, .5, 0], [0, 0, 3 ** .5 / 2, 0])
    axes[1].set_aspect("equal")
    axes[1].set_title("几何图：等边三角形", fontproperties=font)
    axes[1].text(.5, .3, r"$A=\frac{\sqrt{3}}{4}a^2$", ha="center")
    for extension in ["png", "svg"]:
        fig.savefig(options.output / f"中文数学验证.{extension}", dpi=160)
    plt.close(fig)
    with Image.open(options.output / "中文数学验证.png") as image:
        image.verify()
    result = {"executable": sys.executable, "python": sys.version, "isolated": sys.flags.isolated,
              "numpy": np.__version__, "sympy": sympy.__version__, "matplotlib": matplotlib.__version__,
              "pillow": Image.__version__, "symbolic_result": str(sympy.integrate(sympy.Symbol("x") ** 2, sympy.Symbol("x"))),
              "font": str(options.font.resolve()), "output": str(options.output.resolve())}
    (options.output / "runtime-smoke.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), "utf-8")
    print(json.dumps(result, ensure_ascii=False), flush=True)
    time.sleep(max(0, min(options.hold_seconds, 300)))


if __name__ == "__main__":
    main()
