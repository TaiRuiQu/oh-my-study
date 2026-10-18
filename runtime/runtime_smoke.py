"""Private runtime imports, no developer site-packages or package manager."""
import json
from pathlib import Path
import sys
import numpy
import sympy
import PIL
import matplotlib

if __name__ == "__main__":
    runtime = Path(sys.executable).absolute().parent
    for module in [numpy, sympy, PIL, matplotlib]:
        if not Path(module.__file__).absolute().is_relative_to(runtime):
            raise RuntimeError(f"module escaped private runtime: {module.__name__}")
    print(json.dumps({"executable": sys.executable, "isolated": sys.flags.isolated,
        "numpy": numpy.__version__, "sympy": sympy.__version__, "pillow": PIL.__version__, "matplotlib": matplotlib.__version__}))
