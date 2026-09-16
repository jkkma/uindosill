"""Start the engines with only the package roots explicitly chosen by the host.

The host runs this with -I: inherited Python paths, user site packages and the
working directory cannot supply modules. Embedded CPython also ignores
PYTHONPATH through its ._pth file, so install the resolved roots directly.
Arguments are paths, never Python source; the optional CUDA overlay comes first.
"""

import runpy
import sys

sys.path[:0] = sys.argv[1:]
sys.argv = sys.argv[:1]
runpy.run_module("uindosill_engines", run_name="__main__", alter_sys=True)
