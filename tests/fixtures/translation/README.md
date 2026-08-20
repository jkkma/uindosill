# Translation fixtures

One file, one purpose, and nothing reads it yet — which is deliberate rather than an oversight.

## `marian-tokenizer.json` — what HuggingFace's `MarianTokenizer` actually emits

Token ids, token strings and the round-tripped decode for six fixed sentences through
`MarianTokenizer` at checkpoint `Helsinki-NLP/opus-mt-tc-bible-big-mul-deu_eng_nld`, revision
`bb1ef830d540449c89c7ee5b9ea5b1fc666db3d5`. Written by
`scripts/export-translation-onnx.py --tokenizer-fixture`, which also records the toolchain that
produced it inside the file, so the fixture names its own provenance rather than relying on this
page to stay accurate about it.

**Why it exists before anything reads it.** `docs/UNPROVEN.md` § *Translating into English* has
carried "whether a C# `SentencePieceTokenizer` reproduces HuggingFace's `MarianTokenizer` is still
unestablished" since the route was chosen, and that cannot be established against a description —
only against ids. The decode loop is the step that will have a C# tokenizer to hold up to this, the
way `tests/fixtures/diarisation/sortformer/` holds the diariser's featurizer to a reference it did
not compute. Committing the ids now means the loop is written against a fixed target instead of
whatever the first C# implementation happens to produce.

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
