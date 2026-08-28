# Models

Weights live under `%LOCALAPPDATA%\Uindosill\models` on Windows, and the platform equivalent
elsewhere — resolved through `Environment.SpecialFolder.LocalApplicationData`, never hardcoded, so
folder redirection and roaming profiles keep working. `UINDOSILL_MODELS_DIR` overrides it.

Never the install directory: a **downloaded** model there is destroyed by every update, which turns
each patch into a 670 MB re-download.

**One of the four ships inside the installer instead**, and it is a different thing from a download:
speech detection (2.2 MiB, MIT) sits in `models/` beside the executable, so that opt-in is live on a
fresh install rather than dead until somebody visits a tab. It is replaced with the application on
every update and removed with it on uninstall, which is right — it belongs to the build, not to the
user.

**Speaker labelling was the second, from 2026-08-23 until 2026-08-26**, at 452.6 MiB under the NVIDIA
Open Model License. It left the installer that day because two entries then did the job and bundling
one would have chosen for the user; the entry itself was retired on 2026-08-27. What replaced it
could not be bundled in any case: `pyannote/speaker-diarization-community-1` is a gated repository,
so **speaker labelling is dead on a fresh install until the user fetches it**, which needs a free
Hugging Face account and an accepted user agreement.
Speech recognition and English translation are 1.34 GiB each and stay downloads: a GitHub release
asset must be under 2 GiB, and either one puts the CUDA installer past it.

A copy you downloaded always wins over the copy the installer carried. That is what keeps the Models
tab meaningful — the entry it installs, updates and removes is the one the application then loads —
and `scripts/package-windows.ps1` verifies each bundled file against the same SHA-256 in
`models.json` that a download is checked against, so the two copies are the same bytes either way.

**Uninstalling the application does not touch this folder.** Nothing Uindosill does unattended
deletes anything on your disk: an uninstaller cannot ask you about files you put somewhere
yourself, and uninstall-then-reinstall is a repair people try — it should not cost gigabytes. So
the weights survive an update, a reinstall and an uninstall alike, and clearing them out is a thing
you do on the Models tab, which shows what is there and what it costs, or by deleting the folder.

An uninstall hook that removed the folder shipped for one night in `v1.0.0-rc.3` and was withdrawn;
`docs/GOTCHAS.md` gotcha 8 says why, and `docs/PHASES.md` has the full account.

```bash
uindosill models list
uindosill models path
uindosill models download tdt-0.6b-v3-f16
uindosill models verify   tdt-0.6b-v3-f16
uindosill models remove   tdt-0.6b-v3-f16
```

## The ask model is a file you supply, not a catalogue entry — deliberately, for now

The Ask panel's language model (since 2026-08-24) is whatever `.gguf` file sits in this same
models folder: the largest wins where several are present, the answer's model line names it, and
nothing else is consulted. There is no catalogue entry, no download button and no
recommendation, because `docs/V2-ASK-THE-TRANSCRIPT.md` decision 2 forbids recommending a model
until its CSB384 measurements have been run — a recommendation is a claim, and the register's
rule is that claims are measured. When those measurements exist, entries join this catalogue
under the same discipline as everything above: exact byte size, SHA-256, licence and attribution,
verified on download and again before an installer would carry one. Until then, a user-supplied
file keeps the capability available without this application asserting anything about any model
it has not measured.

## The catalogue is data

`src/Parakeet.Core/Models/models.json` is an embedded resource, not code, so pinning a digest is a
reviewable data change rather than a code change.

**Every entry is pinned.** File name, byte size and SHA-256 were read from the
`mudler/parakeet-cpp-gguf` file listing, where the LFS `oid` *is* the SHA-256 of the blob.
`ModelInstaller` compares both the digest and the exact byte count, and moves nothing into place
that disagrees. The file names — which had been conventional guesses — are confirmed by the same
listing.

**`pyannote-speaker-diarization-community-1` was the exception until 2026-08-27, and what ended it
is worth recording.** Its repository is gated, and an *unauthenticated* read serves the file listing
with real byte sizes but **masks every LFS `oid`** — so the entry shipped pinning sizes only, marked
`"verified": false`, and `ModelTests.EveryShippedEntryIsPinned` exempted it by exact id so that a
*second* unpinned entry would still fail.

Accepting the agreement turns out to lift more than the download. **With an accepted token the hub
serves the object ids like any other repository's**, so the digests did not have to rest on one
machine's download: four of the five files' SHA-256 are upstream's own `lfs.oid`, and they match the
bytes that arrived. The fifth, `config.yaml`, is 444 bytes and is stored as a plain git blob rather
than an LFS object, so it has no `oid` to compare — it was corroborated instead against the **git
blob id** the same listing publishes, `4022db43960736338378fdb6b5a85cfdae198910`, recomputed from
the installed bytes. The entry is therefore pinned on the same footing as every other one, is
marked `"verified": true`, needs no unverified opt-in, and the test's exemption is gone.

What is *not* claimed by that: the digests say these five files are the ones upstream published at
commit `3533c8cf`, not that the pipeline they describe scores anything in particular. That is a
separate question and `docs/UNPROVEN.md` holds it.

| Entry | Bytes | SHA-256 |
|---|---|---|
| `tdt-0.6b-v3-f16` | 1,441,046,400 | `8ba47343…fc5abb22` |
| `opus-mt-tc-bible-big-mul-en-fp32` | nine files | per file; see `models.json` |
| `silero-vad-v5.1.2` | 2,327,524 | `2623a295…5bdd788f` |
| `pyannote-speaker-diarization-community-1` | five files, 32,821,421 | per file; see `models.json` — four from upstream LFS `oid`s, `config.yaml` from its git blob id (see above) |

**Five entries, and all five are unquantised.** The catalogue offered f16 plus four quantisations
of it until 2026-08-20, when the four were withdrawn — a product decision recorded in
`docs/PHASES.md`, not a quality finding. Their digests are kept here because a pin that was once
shipped is worth not re-deriving, and because the measurements below are about these exact files:

| Withdrawn 2026-08-20 | Bytes | SHA-256 |
|---|---|---|
| `tdt-0.6b-v3-q8_0` | 940,663,680 | `4d69a4a6…cd07d757` |
| `tdt-0.6b-v3-q6_k` | 812,700,512 | `5fe7e463…d2d717aa` |
| `tdt-0.6b-v3-q5_k` | 741,867,360 | `5ebd1d55…0646c2e4` |
| `tdt-0.6b-v3-q4_k` | 675,200,864 | `993d73fe…8b1d5ee8` |

Do not add one back without reversing the decision in `docs/PHASES.md` first.

The f16 digest has independent corroboration the rest do not: a copy installed from that URL and
used to transcribe 30 seconds, ten minutes and 2 h 55 m of audio — three bit-identical runs —
hashes to exactly the value the repository publishes. For the withdrawn four, the pin meant the
download matched what upstream served, which is what a digest is for and is not a claim about
accuracy. **Pinning is not measurement**: a digest says the bytes are the ones upstream serves, and
says nothing whatever about what they transcribe. q8_0 through q4_k were *diffed* against f16 on
nearly three hours of real speech — see the section below and `docs/UNPROVEN.md` — but that is
divergence, not accuracy.

### The diarisation entry is a different upstream, and it used to be a different licence

`pyannote-speaker-diarization-community-1` carries `"task": "diarisation"`, which is what keeps it
out of every ASR path — `transcribe` refuses it by name, it is never the recommended entry, and the
window's Load button does not offer it. That discriminator shipped in an earlier release **before**
any such entry existed, deliberately, so that no build could ever list one as a transcription model.

**It is CC BY 4.0, like the transcription weights**, and it is the only diarisation entry. There were
two until 2026-08-27, and the other was the interesting one: `sortformer-4spk-v2.1`, 474,630,246
bytes, `cc5d606a…52c0062a`, under the **NVIDIA Open Model License** — the one entry in this catalogue
that was not CC BY 4.0. That licence was revocable where CC BY is not, wanted a copy of its Agreement
rather than a link, and carried a use restriction about biometric processing. It was retired to
`attic/sortformer/` with its weights, the Agreement copy went with it, and **this catalogue now has
no entry under any licence but CC BY 4.0, Apache-2.0 and MIT**. `docs/LICENSING.md` keeps the reading
of the retired one, and `attic/README.md` says what left with it.

**Its `quantisation` says `fp32` and nobody has opened its files to check.** The paragraph that stood
here belonged to the Sortformer export and went to `attic/sortformer/` with it: on that entry the
Hub's `base_model:quantized:…` tag was wrong, and this document said so on evidence — 473 of the
file's 474 MB were float32 parameters, 118.3 M of them, counted by opening the graph. **No equivalent
count exists for this entry.** It is 31.3 MiB of torch checkpoints and npz matrices, and its `fp32`
is upstream's word rather than a reading of its bytes.

**It is a gated repository and a multi-file entry**, which is the pair of properties that makes it
unlike every row above. Hugging Face masks the LFS object ids of gated repositories, so four of its
five files are pinned by size and upstream `oid` and `config.yaml` by its git blob id; a download
needs an accepted user agreement and a token. **Nothing about its accuracy has been measured here** —
see `docs/UNPROVEN.md`, which is also where the figure the retired entry carried is kept.

### The speech-detection entry is MIT, from GitHub, and pinned against the bytes it serves

`silero-vad-v5.1.2` is the fourth row above and the first that is neither from Hugging Face nor
under a licence with a notice package of its own design. It carries `"task": "voice-activity"`, the
fourth task word, which keeps it out of every ASR path the way the other two discriminators do —
and, like them, the word shipped in the same commit as the code that reads it, so no build can list
this entry as anything but what it is.

**It is 2,327,524 bytes, and it is downloaded, not embedded.** Two megabytes would fit in the
assembly; it is a catalogue entry anyway because every weight this product ships is — pinned by
digest, installed under the user's data root, removable from the Models tab, attributed through the
same `Attribution.ById` every other entry goes through. One mechanism, not one mechanism and a
special case.

**The URL pins a commit, not a tag and not `main`.**
`raw.githubusercontent.com/snakers4/silero-vad/`
`6478567951ae5c9979ad7b234185b5515f4be7a1/src/silero_vad/data/silero_vad.onnx` — the commit tag
v5.1.2 resolves to, read from the GitHub API on 2026-08-23. A tag can be moved and `main` will move;
a commit serves the same bytes or nothing.

**`"verified": true` here means something slightly different from the Hugging Face rows.** Those
were checked against an LFS listing, where the `oid` is the file's SHA-256. GitHub publishes no
digest for a file in a repository, so this pin was taken the only way it could be: the file was
fetched from that URL on 2026-08-23, hashed (`2623a295…5bdd788f`), and the entry was then installed
*through `ModelInstaller` itself* from that URL, which fetched the same bytes and accepted them
against the pin. That is a check against what the URL serves rather than against a listing — which
is what a pin is for — and it is one fetch on one day.

**`quantisation` says `fp32`, counted rather than assumed.** The graph's weights are 36 float32
tensors — 545,601 parameters — held as constants inside its subgraphs; the 309 int64 tensors beside
them are shapes. Counted by walking every subgraph with the `onnx` package on 2026-08-23.

**The licence is MIT**, `Copyright (c) 2020-present Silero Team`; the permission text ships as
`licences/silero-vad-LICENSE.txt` and `docs/LICENSING.md` has the reading. **What the model is for,
what it costs and what it has been measured on** are in `docs/PHASES.md` and `docs/UNPROVEN.md`
under 2026-08-23; the short form is that it cuts a recording at pauses the energy gate cannot hear
under music, at a cost of a few percent of the pass, and that it was measured on one documentary and
one podcast. Installed, it is the default in the app and on the command line alike; `--vad energy`
asks for the gate, and a transcript's JSON names what cut it (`speechDetector`).

### Pins recorded for v3, deliberately unreachable

The same repository carries `tdt`, `rnnt` and `ctc` at 0.6b and 1.1b, a `tdt-0.6b-v2`, and two
families that matter for the push-to-talk dictation the founding brief deferred, now v3. Both are
pinned in the `deferred` array of `models.json`, all five quantisations each, with exact sizes and
digests read from the same listing:

| Family | What it is for | f16 size |
|---|---|---|
| `nemotron-3.5-asr-streaming-0.6b` | Streaming ASR — partial results while a key is held, which this build cannot produce because it decodes whole segments | 1,484,324,992 |
| `realtime_eou_120m-v1` | End-of-utterance detection, *not* transcription — deciding when a speaker has finished, which the energy-based segmenter here does not do | 266,517,952 |

**They are pins, not catalogue entries, and the reason is licensing rather than caution about
quality.** Every entry in `models` carries a licence and an attribution id that must resolve
against `Attributions.ById`, because the application shows that notice and CC BY is not satisfied by
attribution alone. Neither of these families is parakeet-tdt: Nemotron models commonly ship under
the NVIDIA Open Model License rather than CC BY 4.0, and `realtime_eou` has no stated provenance in
the file listing at all. Naming a licence for either would be inventing one and would put a false
notice in front of a user.

So `DeferredModelPin` records only what was read — name, exact size, SHA-256, and what a later
version would use it for. **The type has no licence or attribution property at all**, so a pin
cannot assert one by being filled in carelessly, and a test asserts that structurally rather than
by inspecting the data. `TryGet` and `Get` do not search them, an id appearing in both arrays is a
parse error, and `models list` and the Models tab never show them.

To promote one: establish the licence, register the attribution in `Attributions.ById`, move the
entry into `models`. Nothing about it is installable before that.

## An entry is one file, or several

Every ASR entry and the speech detector are a single file, and that shape is unchanged: `fileName`,
`url`, `sizeBytes` and `sha256` on the entry itself, installed into the store root under that name.
(**The diariser was on that list until 2026-08-27**, when the single-`.onnx` engine was retired; the
pipeline that replaced it is a directory.)

An entry may instead list several. **Two do** — the ONNX translation route, which is nine files
since 2026-08-20: two graphs, two configs and a five-file tokenizer; and the diarisation entry, which
is five files in three subdirectories since 2026-08-27 and is the reason `directory` exists beside
`fileName`. The shape shipped in
2026-08-20's earlier work with nothing using it, which is the same order the task discriminator and
the diarisation entry arrived in and for the same reason: the code that has to understand a shape
ships before the entry that has it, so no build can meet one it does not know. The real entry looks
like this, abbreviated:

```json
{
  "id": "opus-mt-tc-bible-big-mul-en-fp32", "task": "translation", "…": "…",
  "directory": "opus-mt-tc-bible-big-mul-en",
  "files": [
    { "fileName": "config.json", "url": "https://…", "sizeBytes": 1056, "sha256": "…" },
    { "fileName": "decoder_model_merged.onnx", "url": "https://…", "sizeBytes": 886620027, "sha256": "…" },
    { "fileName": "encoder_model.onnx", "url": "https://…", "sizeBytes": 545922847, "sha256": "…" },
    { "…": "and generation_config.json, source.spm, special_tokens_map.json, target.spm, tokenizer_config.json, vocab.json" }
  ]
}
```

**That entry is verified, and it is the only one attested twice.** Its nine digests were first taken
off the bytes the translation gate was scored against and re-hashed from disk — stronger evidence
than an LFS listing, which is what every other entry rests on. The files were then published on
2026-08-20, and every one of the nine LFS `oid`s Hugging Face publishes matched the scored digest,
so the pin is now confirmed by the repository as well as by the machine that built it. Those two
checks are independent, and agreeing is what `"verified": true` means here.

**Its URLs pin a commit sha rather than `main`**, the way the diariser's entry does and for the same
reason: a URL and a digest are pinned together, so a URL that can serve different bytes later would
break every already-installed copy with a digest mismatch as its only symptom.

**All nine files are on LFS, including the four under a kilobyte**, because that is what makes them
checkable. A file stored as an ordinary git blob carries a git SHA-1 and publishes no SHA-256, so
those four would have had nothing to pin against and only five of nine could have been verified — and
an entry is as verified as its least-checked member. The `.gitattributes` uploaded beside them is
what forces it.

**`directory` is required for a multi-file entry, and it is about names rather than tidiness.** The
translation route ships `config.json` and `vocab.json`; neither is a name one model can own in a
folder shared with every other entry. The parser refuses a `files` array without one, refuses a
`directory` that is not a bare name — `../..` would have `models remove` delete outside the store —
and refuses an entry that carries both shapes at once rather than guessing which the author meant.
Two entries that would occupy the same name in the store, directory or file, are also refused.

**Pins are per file, and an entry is verified only when every file is.** Eight pinned digests out of
nine is an unverified entry: a set is as checked as its least-checked member, and `models list` says
so with the count. `models verify` hashes every file and reports every one, including after the
first mismatch, because "which of the nine is wrong" is the question somebody is actually asking.

**A multi-file entry installs all or nothing.** The installer assembles it in a `<directory>.part`
staging folder beside the target and renames that into place only once every file has been fetched,
sized and hashed. Interrupt it anywhere and what is on disk is a staging folder, which `IsInstalled`
does not look at and `models list` does not report — so there is an incomplete *download*, which
resumes, and never an incomplete *model*, which an engine would try to load. Resume is per file: a
file already staged with the right digest is skipped rather than refetched, because throwing away
eight good files because the ninth was interrupted is over a gigabyte of somebody's bandwidth.

`models remove` takes the whole directory, including anything in it the manifest does not list — a
file a later revision of the entry added — because leaving it would make the next install look
partial and the reported disk usage a lie.

## Pinning an entry properly

1. Fetch the repository's file listing and read the LFS `oid` — for Hugging Face repositories that
   value *is* the SHA-256 of the file:

   ```bash
   curl -s "https://huggingface.co/api/models/mudler/parakeet-cpp-gguf?blobs=true" \
     | jq -r '.siblings[] | "\(.rfilename)  \(.size)  \(.lfs.oid // "no-lfs")"'
   ```

2. Copy the real file name, `size` and `oid` into the matching entry in `models.json`, and set
   `"verified": true`.

3. Confirm end to end:

   ```bash
   uindosill models download <id>     # now verified rather than trusted
   uindosill models verify   <id>     # recomputes and compares
   ```

If you cannot reach the API, the other direction works: download once with `--allow-unverified`. The
installer prints the SHA-256 of exactly what arrived, and that is what you pin. That is weaker — it
records what one machine received rather than what the repository publishes — so prefer the listing
and use this only to corroborate.

**If the two disagree, that is a finding.** A local copy whose digest differs from the published
`oid` means the file changed upstream or the download was interfered with. Do not quietly replace
one value with the other.

## Downloading

- **Resumable.** A dropped connection leaves a `.part` file and a `.part.json` recording the URL; the
  next attempt sends a `Range` header and continues. A `.part` left over from a *different* URL is
  discarded rather than spliced onto the new download.
- **Verified before it is installed.** The digest is computed over the finished `.part`, compared to
  the pinned value, and only then is the file moved into place. A half-written 670 MB blob is never
  mistaken for a model.
- **Idempotent.** An already-installed file is hashed and left alone. A file that does not match its
  pinned digest is replaced, because a corrupt or tampered model is not something to keep. For a
  multi-file entry the unit replaced is the whole directory rather than the file that failed:
  keeping the ones that happened to verify would leave a mixed set nobody has ever tested together.

## Which quantisation

`parakeet-tdt-0.6b-v3` is the target: multilingual, 25 European languages. **No Chinese, Japanese,
Korean, Arabic, Hindi or Thai** — do not promise them. A test asserts no catalogue entry claims those
tags.

| File | Size as the repository displays it |
|---|---|
| `tdt-0.6b-v3-f16.gguf` | 1.44 GB |
| `tdt-0.6b-v3-q8_0.gguf` | 941 MB |
| `tdt-0.6b-v3-q6_k.gguf` | 813 MB |
| `tdt-0.6b-v3-q5_k.gguf` | 742 MB |
| `tdt-0.6b-v3-q4_k.gguf` | 675 MB |

Those figures are the repository's **rounded display values**, and they are recorded here for
orientation only. They must **not** be copied into `models.json` as `sizeBytes`: the installer
compares the pinned size to the downloaded byte count exactly and refuses a mismatch, so a rounded
number would reject a perfectly good download. Pin `sizeBytes` only from an exact byte count — the
API listing, or the file's own page on the hub, which also shows the SHA-256 you need for `sha256`.

The catalogue recommends **f16**. Until 2026-08-16 it was the one entry that required no guess
about its accuracy, and every quantisation below it carried this warning:

> Quantisation quality on this engine is unmeasured. The analogous ONNX INT8 export was measured at
> 24.8% long-audio WER against 7.8% for fp32, and it collapsed *silently*, producing fluent wrong
> text rather than obvious garbage.

The rule that went with it — measure against f16 before recommending anything, on real disfluent
accented speech, including at least two files over ten minutes; synthetic text-to-speech will not
catch a decoder regression, because clean TTS decodes identically under conditions that break real
speech — has now been followed, and the entries say what was found instead of the warning.

**Measured, 2026-08-16.** First, divergence: all five entries run against a 2 h 55 m two-host
podcast and diffed against f16 by word-level edit distance — q8_0 0.42%, q6_k 0.87%, q5_k 1.69%,
q4_k 2.69% of normalised tokens, over a CPU-versus-CUDA noise floor of 0.11%, monotonic, no
collapse. Then, the thing divergence cannot say: **word error rate against human transcripts**,
`scripts/measure-wer.ps1` over eleven hours of accented English earnings calls with two transcript
styles (`scripts/wer-corpus.json`), every entry on CUDA on the same machine — f16 10.21%, q8_0
10.23%, q6_k 10.17%, q5_k 10.17%, q4_k 10.15% against the verbatim transcripts, 13.34–13.43%
against the non-verbatim ones. A 0.08-point spread with no ordering: on that corpus no
quantisation costs measurable accuracy, and the divergence turns out to be *which* words differ,
not how many are wrong. The method, the normaliser (this project's own, so the absolute figures
are not comparable to a published one), the per-file table and the limits — one English corpus of
one genre, one machine — are in `docs/UNPROVEN.md`.

f16 stays the default because the choice was made before the measurement and nothing has been
decided since; the measurement removes the reason for it (f16 being the only entry with no open
question) without making the other decision, which is about download size and CPU speed against
one corpus's worth of evidence. Every entry's `notes` now carries its own figure and the same
caveat.

## Producing conversions yourself

`scripts/convert_parakeet_to_gguf.py` in the parakeet.cpp repository converts the CC-BY-4.0 NVIDIA
checkpoints, which removes key-person risk on the artifacts. Worth exercising once even if you never
need it.

Note that conversion and quantisation are **modifications** under CC BY 4.0 §3(a), and the notice
package has to say so. It already does — see `NOTICE.md`.
