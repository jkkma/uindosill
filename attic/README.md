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
