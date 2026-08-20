# Translation fixtures

One file, one purpose. It was committed on 2026-08-20 with nothing reading it, deliberately; the C#
tokenizer arrived later the same day and `MarianTokenizerFixtureTests` is what reads it now.

## `marian-tokenizer.json` — what HuggingFace's `MarianTokenizer` actually emits

Token ids, token strings and the round-tripped decode for six fixed sentences through
`MarianTokenizer` at checkpoint `Helsinki-NLP/opus-mt-tc-bible-big-mul-deu_eng_nld`, revision
`bb1ef830d540449c89c7ee5b9ea5b1fc666db3d5`. Written by
`scripts/export-translation-onnx.py --tokenizer-fixture`, which also records the toolchain that
produced it inside the file, so the fixture names its own provenance rather than relying on this
page to stay accurate about it.

**Why it existed before anything read it.** `docs/UNPROVEN.md` § *Translating into English* carried
"whether a C# `SentencePieceTokenizer` reproduces HuggingFace's `MarianTokenizer` is still
unestablished" from the day the route was chosen, and that cannot be established against a
description — only against ids. Committing them first meant the tokenizer was written against a
fixed target instead of against whatever its own first output happened to be. It reproduced all six
cases, ids and round-tripped text, on the first run.

**Reading it needs the checkpoint, so the test that reads it is skipped where there is none.** The
ids cannot be recomputed without `source.spm`, `target.spm` and `vocab.json` — 3.06 MB of a 1.34 GiB
artefact this repository does not carry, and whose redistribution is a licence question rather than
a size one. Everything about the tokenizer that does not need them — the protobuf reader, the
double-array character map, the Unigram search, byte fallback, the language-code rule — is tested
hermetically in `SentencePieceTests` against models those tests write byte by byte, so what the skip
costs is the check against HuggingFace's real ids and nothing else.

**Six sentences is a start and not a proof**, which is why the same tokenizer is also held to the
8,149 sources the translation gate run tokenised — see `scripts/measure-translation-agreement.ps1`.
A fixture proves the shape; a corpus proves the tail.

**Three things in it are load-bearing and would be easy to get wrong.**

* Every source is recorded twice: as the sentence, and as `markedSource` with `>>eng<<` on the
  front. The marked form is the only string the product ever tokenises — without the prefix this
  checkpoint returns fluent German rather than an error — so the ids are for the marked form.
* `>>eng<<` is **one token**, id 693, not a punctuation sequence the tokenizer takes apart. A C#
  tokenizer that splits it will produce plausible-looking ids and silently lose the target.
* `modelMaxLength` is 512 while `config.json` says `max_position_embeddings` 1024. 512 is the
  number to design against; the discrepancy is recorded in `docs/UNPROVEN.md` rather than resolved.

The six sentences are the same set the export script's smoke test uses: four are real ASR output
from this project's own pipeline (Spanish and German), one carries a German number written as a
word, which is where the ASR and the translator interact worst, and one is English, which this
model passes through byte-identical.

Regenerate with the export script's own venv, made outside the working tree as its header says:

```
%USERPROFILE%\marian-onnx-venv\Scripts\python scripts\export-translation-onnx.py --skip-export --variants fp32 --tokenizer-fixture tests\fixtures\translation\marian-tokenizer.json
```

Do not edit it by hand. If the ids change, either the checkpoint revision moved or the tokenizer
library did, and which one is the thing worth finding out.
