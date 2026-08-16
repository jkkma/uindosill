# Why parakeet.cpp

Three candidates were considered. The decision turns on one fact.

## sherpa-onnx — the easy path

`org.k2fsa.sherpa.onnx` has good .NET bindings and would have been the shortest route. Against it:

- Its prebuilt NuGet package is **CPU-only**. There is no GPU execution provider in it, and CUDA
  requires a C++ source rebuild.
- It exposes **no confidence scores at all** in C#. Per-word confidence is what lets a transcript
  flag its own suspect passages, and there is no way to add it from the outside.
- There is an open issue ([#3767](https://github.com/k2-fsa/sherpa-onnx/issues/3767)) reporting
  Parakeet TDT decoding to *empty output* on Windows — the exact model and platform this product
  targets.

## Raw ONNX Runtime plus a hand-written TDT decoder

This works. [`obra/winpepper`](https://github.com/obra/winpepper) has an Apache-2.0 C# reference at
roughly 450 lines, of which the decoder proper is about 97. The cost is that you own a decoder
nothing verifies: no upstream test suite, no parity fixture, and every future model change is yours
to chase.

Worth keeping in mind as the escape hatch if ggml ever becomes untenable.

## parakeet.cpp — chosen

It wins on one fact that neither alternative offers in any form:

**parakeet.cpp's own `docs/parity.md` records every published Parakeet checkpoint validated byte-for-byte against NeMo
2.7.3 at WER 0.0**, including `parakeet-tdt-0.6b-v3` (1024 d_model, 24 layers, 128 mel, 8192 vocab).

No other candidate offers any proof of decode correctness at all. Read `docs/UNPROVEN.md` before
leaning on that result, though — it is one 7.4-second fixture, CPU, batch 1, greedy, and it proves
numerical faithfulness rather than accuracy at quantisation or on long audio.

Everything else is supporting evidence:

- **MIT**, from the LocalAI team, and consumed by LocalAI through the same flat C ABI — so the FFI
  surface is exercised rather than theoretical.
- **The ABI is designed for this.** `extern "C"`, no C++ exception crosses the boundary, an explicit
  ABI version function, and ownership documented per return type.
- **`frame_sec` comes back in the JSON** (0.08 for the 0.6B models), so the frames-to-seconds
  conversion is supplied and nothing here derives or verifies a subsampling factor.
- **Per-word timestamps with confidence** (ABI v4), which SRT/VTT and suspect-segment flagging both
  need.
- **Internal resampling** when the sample rate is not 16 kHz, keeping a resampler off the critical
  path and out of this codebase.
- **Cache-aware streaming with EOU/EOB events** (ABI v5) — the v3 dictation path, already there.
- **CTC log-probs** (ABI v6) if an external LM stack is ever wanted.
- **CUDA ships its own cudart archive** (the llama.cpp convention), so the end user needs no CUDA
  Toolkit. That is the single biggest packaging advantage over ONNX Runtime's CUDA execution
  provider.

## What it costs

- **No Windows CI upstream.** `ci.yml` runs `ubuntu-latest` only; Windows binaries exist only at
  release tags. Hence pinning and vendoring rather than tracking — see `docs/NATIVE-BINARIES.md`.
- **No thread control.** No entry point takes a thread count, so the recommended eight-thread cap
  cannot be applied from here at all.
- **No cancellation.** No abort hook, so a decode in flight runs to completion.
- **No synchronisation.** The C layer has no mutex of any kind, so one context means one decode at a
  time and that is the caller's job to enforce.

The last three were verified by reading `include/parakeet_capi.h`, `include/parakeet.h` and
`src/parakeet_capi.cpp`, not assumed. Two of them are visible in `EngineCapabilities` —
`SupportsThreadCount` and `SupportsDecodeCancellation`. The missing synchronisation is not: it has
no member there at all, and is handled instead by `ParakeetCppEngine` serialising calls on a
semaphore. A caller reading only the capability surface cannot learn that one context means one
decode at a time.
