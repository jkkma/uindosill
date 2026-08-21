"""The engines the .NET host drives out of process.

Two of the product's three models run on ONNX Runtime, and both live here rather than in C#: the
diariser, whose numerical core is NVIDIA's own `SortformerModules` imported and called rather than
reimplemented, and the translator, which gets its beam search and tokenizer from `transformers`.

This is a sidecar, not a library anybody imports. `python -m uindosill_engines` speaks the line
protocol in :mod:`.protocol` over stdin and stdout, and the host owns every decision; see
:mod:`.serve`.
"""

__all__ = ["__version__"]

#: Independent of the product version on purpose — the host checks the protocol number, not this.
__version__ = "0.1.0"
