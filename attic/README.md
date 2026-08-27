# The attic

Two engines that this product shipped and no longer runs. They are kept because they are the record
of a gate being passed, not because anything intends to maintain them. **They will rot**: nothing
builds them, nothing tests them, and the first framework or dependency change that would have broken
them will break them silently.

Retired 2026-08-21, when the diariser and the translator moved into a bundled Python sidecar
(`python/uindosill_engines/`, driven by `src/Parakeet.Engine.Python/`). The reason was not that this
code was bad. It was that between them these four projects are about 7,400 lines reimplementing what
NVIDIA and HuggingFace already ship — an arrival-order speaker cache, a mel featurizer, a
SentencePiece processor, a Marian tokenizer, a beam search and two ONNX decoder loops — and every one
of those is a second place for a measured number to drift from the thing that produced it. The C#
diariser also ran **12% slower** than the Python spike it was ported from.

**The last commit where these built as part of `Uindosill.slnx` is `a472502`.** There are no tags in
this repository, so no tag names one; the SHA is the reference. `git show a472502:Uindosill.slnx`
shows the solution that included them.

## What is here

| | what it was | what it carried |
|---|---|---|
| `Parakeet.Engine.Sortformer/` | NVIDIA Streaming Sortformer 4spk v2.1 over ONNX Runtime, in C#: mel featurizer, chunk plan, arrival-order speaker cache, post-processing | **AMI test DER 16.3368%** at collar 0 with overlap, against the Python reference's 16.3324% — 0.0044 points apart, speaker error 0.06. Both gate criteria held |
| `Parakeet.Engine.Marian/` | Helsinki-NLP `opus-mt-tc-bible-big-mul-deu_eng_nld` over ONNX Runtime, in C#: SentencePiece, Marian tokenizer, beam search, merged-KV decoder | The 2026-08-20 translation gate: 8,149 sentences across 24 languages, beam 6 |
| `Parakeet.Engine.Sortformer.Tests/` | 46 tests, no weights | |
| `Parakeet.Engine.Marian.Tests/` | 31 tests, of which 7 need real weights and skip without them | Includes nine hermetic beam-search tests driven by a scripted decoder |

## Why they are unbuilt rather than deleted

The maintainer asked to save them, and there are two things here that a deleted directory would take
with it. The first is the DER figure above: `16.3368%` is the number a C# reimplementation of this
model actually reached, and it is the only evidence this project has for how faithfully that kind of
port can be done. The second is the beam search, which was checked against `transformers`' own
`_get_logits_processor` on 2026-08-20 and is a written-down account of exactly which logits
processors this checkpoint's decode uses — two, and no more. `python/uindosill_engines/translator/`
now relies on `transformers` to apply them, and this is where the reading of what it applies lives.

Nothing outside this directory refers to any of it. They are not in `Uindosill.slnx`, no project
references them, and no script drives them, so a `dotnet build` of the solution neither builds nor
notices them.

## Reading them

`attic/*/**.cs` is ordinary source. To build one, add it back to `Uindosill.slnx` and expect to fix
whatever has moved since — `Resampler` left `Parakeet.Engine.Sortformer` for `Parakeet.Audio` before
the move, and `SortformerPostProcessing` now names a different type in `Parakeet.Engine.Python`.

The two test projects' fixture paths point into `tests/fixtures/`, which is still there and still
used: `tests/fixtures/translation/marian-tokenizer.json` is the source of the six sentences in the
sidecar's own translation parity fixture, and is held against it by
`Parakeet.Engine.Python.Tests.ParityFixtureTests`.

## `diarizen/` — the second diariser, shelved 2026-08-27

Not a C# project like the two above, and shelved for a different reason: nothing was wrong with it.
It was **displaced by a version conflict it could not survive**.

DiariZen runs on a fork of `pyannote-audio` 3.1.1 — which adds `VBxClustering`, among other
changes — and pyannote's own release line is at 4.x. (A "3,996 changed lines across 45 of
upstream's 82 files" figure travelled with this note from `requirements-bundle.txt` and is not
reproduced here: nothing in this session diffed the fork against upstream 3.1.1, and the number is
not needed to make the point.) The two cannot share an
interpreter: `pyannote.audio` 4.0.7 floors `pyannote.core>=6.0.1`, `pyannote.database>=6.1.1`,
`pyannote.metrics>=4.0.0` and `pyannote.pipeline>=4.0.0`, against the 5.0.0, 5.1.3, 3.2.1 and 3.0.1
the fork needs. Five shared import names, five incompatible floors. So the second diariser could be
DiariZen or it could be pyannote, and it became pyannote — which had by then upstreamed the VBx
clustering BUT Speech@FIT contributed, so the capability that mattered came along.

The move also removed this product's only non-commercial licence: the DiariZen checkpoint is
CC BY-NC 4.0 and `pyannote/speaker-diarization-community-1` is CC BY 4.0.

### What is here

| | what it was |
|---|---|
| `uindosill_engines/diariser/diarizen.py` | The engine: pipeline construction, the three torch-2.13 compatibility shims, the progress hooks that wrapped two bound methods because upstream's `__call__` passed none |
| `uindosill_engines/diariser/embedding_onnx.py` | The ONNX speaker embedder, exported from the vendored wespeaker ResNet34, and its `GRAPH_VERSION` cache marker |
| `uindosill_engines/diariser/embedding_parity.py` + `embedding-parity-reference.npy` | Its parity fixture — ONNX against torch at 1e-4, passing at 1.9e-07 |
| `uindosill_engines/_vendor/diarizen/` | DiariZen's own source, MIT (c) 2024 BUT Speech@FIT, with `clustering/VBx.py` Apache-2.0 under its own header |
| `uindosill_engines/_vendor/pyannote/` | The 3.1.1 fork, MIT (c) 2020 CNRS. It is DiariZen's fork rather than upstream's release, and it was carried unedited — which is why every incompatibility was repaired from `diarizen.py` instead. "Unedited" means this project changed nothing in it; it is emphatically **not** identical to upstream 3.1.1, which is the whole reason it had to be vendored |

**It will rot exactly as the two above will**, and faster in one respect: the vendored fork is
pinned to a torch and numpy the bundle will move off. The last commit where it ran as part of the
product is the one before this line was written.

**What went with it, and is not here.** The `SidecarEngineTests` case asserting that its ONNX
embedder was parity-checked even when it reported the CPU — the new engine has no ONNX route, so it
has no fixture and the sidecar refuses the `parity` op for it by name. Its measurements stay in
`docs/UNPROVEN.md`, which is where the record of a thing that was measured belongs whether or not
the thing still ships.
