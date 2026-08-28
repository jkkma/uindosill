# The attic

Engines this product shipped and no longer runs. They are kept because they are the record of a gate
being passed, not because anything intends to maintain them. **They will rot**: nothing builds them,
nothing tests them, and the first framework or dependency change that would have broken them will
break them silently.

Six directories, retired on two days. The four C# projects below went on 2026-08-21; `diarizen/` and
`sortformer/` went on 2026-08-27, a few hours apart and for unrelated reasons, and between them they
are every diariser this product has ever had except the one it ships.

The C# pair were retired 2026-08-21, when the diariser and the translator moved into a bundled Python sidecar
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

The two test projects' fixture paths point into `tests/fixtures/`, and **one of the two trees they
point at has since moved in here.** `Parakeet.Engine.Marian.Tests` still resolves:
`tests/fixtures/translation/marian-tokenizer.json` is the source of the six sentences in the
sidecar's own translation parity fixture, and is held against it by
`Parakeet.Engine.Python.Tests.ParityFixtureTests`. `Parakeet.Engine.Sortformer.Tests` no longer
does — its blobs went to `attic/sortformer/fixtures/sortformer/` on 2026-08-27, when nothing live
was left to read them, so reviving that project means repointing `Fixtures.cs`'s directory walk as
well as everything else.

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
has no fixture. The sidecar refused the `parity` op for that engine by name for a few hours; once
`sortformer/` followed the same afternoon there was no arm left that had a fixture, and the refusal
moved to cover the diariser outright. Its measurements stay in `docs/UNPROVEN.md`, which is where the
record of a thing that was measured belongs whether or not the thing still ships.

## `sortformer/` — the first diariser, shelved 2026-08-27

The Python engine, its vendored NeMo, its fixtures and its licence. Shelved the same day as
`diarizen/` and for the opposite reason: nothing displaced it, and nothing was wrong with it. It was
**the only diariser this project ever measured**, and it left anyway.

That is worth stating plainly rather than softening, because the shape of this directory hides it.
`diarizen/` above lost a version conflict. This one was retired by decision while passing every
check it had — a gate criterion cleared, a parity fixture green on every provider that shipped, and
a DER within 0.0044 points of the Python reference its C# port had been held to.

### What is here

| | what it was |
|---|---|
| `uindosill_engines/diariser/engine.py` | `SortformerEngine`: the streaming driver, its 340/40/188-frame chunk geometry, the provider table, the graph-optimisation and thread-pool settings |
| `uindosill_engines/diariser/feats.py` + `mel-filterbank.npy` | The NeMo-faithful mel featurizer, validated bit-exact against `FilterbankFeatures`, and the committed 128 × 257 Slaney filterbank that replaced the `librosa.filters.mel` call on 2026-08-26 |
| `uindosill_engines/diariser/postproc.py` | `ts_vad_post_processing`'s knobs over the `[F, 4]` probabilities: binarize, pad, merge, drop, fill |
| `uindosill_engines/diariser/parity.py` + `parity-reference.npy` | The fixture: 6,096 synthetic mel frames at seed 20260821, 762 × 4 reference probabilities taken on the CPU, tolerance 1e-4 |
| `uindosill_engines/_vendor/nemo/` | NVIDIA's `SortformerModules` — the arrival-order speaker cache — copied verbatim under Apache-2.0, plus the thirteen stubs and package markers that made it importable — fifteen files, of which two are NVIDIA's |
| `fixtures/sortformer/` | The eight `.f32` blobs and `expected.json` the C# suite replayed, moved here from `tests/fixtures/diarisation/sortformer/` |
| `scripts/make-diariser-fixtures.py` | What generated them |
| `NVIDIA-Open-Model-License-2025-10-24.txt` | The Agreement copy §3.1 required, moved from `licences/` |

The C# reimplementation of this model has been in `Parakeet.Engine.Sortformer/` since 2026-08-21;
the two are now the same story in two languages, one directory apart.

### What it carried

Every one of these was measured on this engine and **describes nothing that ships now**. They are
listed here rather than only in `docs/UNPROVEN.md` because a reader who finds this directory is
exactly the reader who needs to know the numbers did not transfer.

| | |
|---|---|
| AMI test DER, CPU | **16.3324%** at collar 0 with overlap, 16 meetings — the published figure |
| The same on WebGPU | 16.3319%, 0.0005 points away, which is why `auto` reached for it |
| The same on CUDA | 16.1021% — *better*, and excluded from `auto` anyway, because a provider whose answer is its own means the published figure describes only whoever measured it |
| The same on DirectML | **53.15%** at ONNX Runtime's default optimisation, at 13× the speed, emitting speaker turns that read as perfectly ordinary |
| Parity, CUDA | 8.143e-04 against a 1e-4 tolerance — a fail, and the reason CUDA was named rather than chosen |
| Speaker slots | 4, architectural: a fifth voice was merged into one of the four rather than reported |
| Established length | 50 minutes, measured 2026-08-20 by growing a window from a fixed onset; wrong past an hour |
| Graph | 453 MiB in memory, 474 MB installed, under the NVIDIA Open Model License |

### What went with it, and is not here

The **parity check itself**, on both sides. The sidecar's `parity` and `placement` ops now refuse the
diariser by name — one sentence for both, where the two arms previously needed their own — and the
host's `SidecarSpeakerLabeller` sends neither. Five sentences in `Parakeet.Core` went with them:
`DescribeBackend`, `DescribeEmbeddingBackend`, two `DescribeParityFailure` overloads and
`DescribeParityNotRun`.

**`--speaker-backend-unverified` and `diarise --backend-unverified`.** They existed to unlock
DirectML for measurement against the fixture above. With no ONNX diariser there is no provider to
unlock and no check to override, so they were withdrawn rather than left as flags that do nothing.

**The catalogue entry, and with it this product's last NVIDIA-licensed component.** `models.json` has
one diarisation entry now. The Open Model License's §2.1 revocability note left
`Attributions.WeightUsageRestrictions` with the grant it described — but **the biometric caution
stayed**, restated as this project's own: it arrived as NVIDIA's §2.3 and was never really about the
licence, since separating people by their voices is voice biometrics whichever model does it.
`OpenModelLicenceAttribution` is kept unused, as `CcByNcAttribution` is, because it is a reading of a
licence family that took work to establish.

**Protocol 4 → 5.** `load` for the diariser no longer carries `kind`, and a version-4 host would send
`kind: "sortformer"` with an ONNX path and be handed a torch pipeline under a different licence. The
number is what turns that into a refusal at `hello`.

**Ten tests**, leaving 1404. Six exercised the parity path, two the retired licence's notice and
Agreement copy, and two the four-speaker cap. `DeclaredLimitsTests` survived by changing what it
polices: the constants it holds the host against are `None` now, and a null is a claim held to the
same way a number was.
