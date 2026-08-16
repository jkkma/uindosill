# Phase plan and where this repository actually stands

Exit criteria are evidence, not opinion. What follows is the plan and an honest mark against each
step.

## Phase 0 — spike, no UI — **DONE**

Console harness: load a GGUF through P/Invoke, transcribe a real file, print the JSON.

*Exit:* non-empty, correct text from real weights on real Windows, **before anything else is built**.
Then a benchmark matrix (warmed up) and a WER harness over a corpus that includes real disfluent
accented audio and at least two files over ten minutes. Record RTF, cold load, peak RSS, and
long-file WER at each quantisation against f16.

**Status:** the first half is done. A 30-second clip decoded correctly on Windows x64 against
parakeet.cpp v0.5.0 and `tdt-0.6b-v3-f16`, at RTF 0.10 — the full record is in `docs/UNPROVEN.md`.
That settles the question this phase exists to answer: the engine produces correct text through
these bindings.

The timing and memory half is now done, though not through `bench`. Three real files were measured
end to end — 30 s, 10 min and 2 h 55 m — giving RTF at three durations (0.1005, 0.0829, 0.0790),
peak working set at two (2,379 MB and ~2,950 MB), and a working-set profile across three hours
showing memory peaks mid-run and falls. `scripts/measure-transcribe.ps1` is that harness.

**The WER half is done, 2026-08-16.** `scripts/measure-wer.ps1` is the harness and
`scripts/wer-corpus.json` the corpus — Rev.com's Earnings-22 Subset 10, ten human-transcribed
earnings calls of 58–78 minutes from five countries, 11.12 hours, two transcript styles, every file
pinned by digest — which is real, disfluent, accented and long-form, as this phase asked. Every
catalogue entry was scored on the RTX 5080 desktop on CUDA: **f16 10.21%, q8_0 10.23%, q6_k
10.17%, q5_k 10.17%, q4_k 10.15%** against the verbatim transcripts, 13.34–13.43% against the
non-verbatim ones — a 0.08-point spread with no ordering, so on this material no quantisation in
the catalogue costs measurable accuracy against f16. The method, its normaliser (deliberately not
the leaderboard's, so the figures are not comparable to a published one), the per-file table, the
CPU control and the limits are in `docs/UNPROVEN.md`. `uindosill bench` has still only run against
the canned engine; the timings this phase wanted came from `transcribe` runs instead.

## Phase 1 — core — **DONE**

Contracts, WAVE decoding, formatters, model catalogue and resumable download with SHA-256, the fake
engine.

*Exit:* `dotnet test` green on Linux with no weights present.

**Status:** met. 359 tests, no weights, no display, no network. One of them — the Media Foundation
extension list — is Windows-only and skips itself here, so a Linux run reports 358 passed and
1 skipped.

## Phase 2 — engine — **DONE**

`Parakeet.Engine.ParakeetCpp`: SafeHandles, the marshalling layer, VAD segmentation with a 30-second
cap, batch decode, timestamps from `frame_sec`.

*Exit:* the CLI transcribes a real file to correct SRT.

**Status:** exit criterion **met**, on a 30-second WAV, ten minutes of podcast, and a full
2 h 55 m episode through Media Foundation: 1,488 segments, 29,926 words, no word out of order, none
past the end of the audio, and no duplication or loss at any join. All segment boundaries land on
the 0.03 s analysis-frame grid and all word starts on the 0.08 s model-frame grid relative to their
segment, so the two clocks stay locked across three hours.

The caveat carried through Phases 2–4 is now discharged: **four segments reached the 30-second cap**
on the long file and were cut mid-sentence, and all four joins read through cleanly. Three runs of
that file produced byte-identical output. See `docs/UNPROVEN.md`.

## Phase 3 — CLI — **DONE**

`transcribe`, `models`, `bench`, plus `doctor`, `notice` and — since 2026-08-16 — `wer`, which
scores a transcript against a human reference and is what the Phase 0 harness is built on.

*Exit:* usable on its own; `bench` reproduces Phase 0.

**Status:** usable, tested end to end against the canned engine (34 of the project's 60 CLI
tests drive the real entry point; the other 26 never construct it — 17 parser unit tests, 7 that
check `--vk-disable-bf16` and its opposite `--vk-bf16` against the real command specs through
`CommandLineParser`, and 2 on the resolver that turns the pair into the engine option). `bench` has not yet been pointed at real weights, so the RTF 0.10 figure above came
from a plain `transcribe` run rather than from a warmed-up timed sweep.

One deviation from the plan worth recording: **`bench` does not sweep thread counts.** The founding
plan called for a thread-count × machine matrix, but no entry point in the parakeet.cpp ABI takes a
thread count, so such a sweep would be measuring nothing. It sweeps batch size instead and prints a
line saying why.

## Phase 4 — UI — **DONE**

Avalonia: drop zone, job queue with continue-on-error, streaming transcript, model manager showing
the licence, settings.

*Exit:* a human uses it on Windows to transcribe a real file.

**Status:** exit criterion **met**. Run on Windows 11: a file dropped on the window, decoded with
a live progress bar and streaming transcript, the model list showing the installed weights and the
Licences tab rendering the full CC BY notice.

Two defects that only a real launch could show, both since fixed: the Models tab was read-only —
its download, remove and unverified-opt-in controls existed on the view model and were bound to
nothing, while its own text told the reader the opt-in was "below" — and Start was enabled with an
empty queue, so pressing it did nothing and read as a broken button.

## Phase 5 — ship — **STARTED**

Velopack, signing every PE, SmartScreen reputation, auto-update.

**Status:** started 2026-08-15, with the piece that has no external dependency. What existed before
was the groundwork: publish is self-contained + ReadyToRun and verified to cross-publish from Linux
for `win-x64`, single-file and trimming are off (and documented as deliberately off), and every
native lives under `native/<rid>/<backend>/` where a signing step can enumerate it.

What is new is that **the natives no longer arrive by hand.** `scripts/vendor-natives.ps1`
downloads the pinned parakeet.cpp v0.5.0 archives, refuses to unpack anything whose byte count or
SHA-256 is not the one recorded in `docs/NATIVE-BINARIES.md`, unpacks flat into the layout the
loader searches, and reads the drop back — `parakeet.dll` at the documented size, `LICENSE` beside
it. CI runs it before the `win-x64` publish and then asserts both files, for both backends, in both
apps' output. Verified locally the same way on 2026-08-15: a `win-x64` publish of the CLI and the app
each carried `native/win-x64/{cpu,vulkan}/{parakeet.dll,LICENSE}`, and `uindosill doctor` run from
the published CLI reported `ok — abi 6` for cpu and vulkan from those directories.

The first CI run after that commit did the same on Linux, and the artefact it uploaded was
downloaded and run on Windows: `doctor` from it reported `ok — abi 6` for cpu and vulkan from their
own directories. `docs/UNPROVEN.md` has the run and what it does and does not prove.

Two things that is not. It is not an installer — the artefact is a directory you unzip, and no
transcription has yet been made from a CI-built binary. And it is not signed: the repository
still has no signing identity, and the vendored `parakeet.dll`s are unsigned third-party binaries
— `Get-AuthenticodeSignature` reports `NotSigned` for both the cpu and vulkan builds — which is the
shape Smart App Control blocks.

Remember that signing `Setup.exe` alone is not enough. Smart App Control and WDAC evaluate every
loaded binary, unsigned native DLLs are exactly what gets blocked, and a signed installer dropping
unsigned executables is itself a recognised malware shape.

### Decisions taken 2026-08-16

These close the two questions this phase was waiting on. **Nothing below is built**; this is the
plan, recorded so the next session builds what was decided rather than re-deciding it.

1. **The CUDA tier is a second download flavour.** Two Velopack channels from the same publish: the
   default carries `cpu` and `vulkan`; the second carries `cpu`, `vulkan` and `cuda`. The choice is
   made at download time, so the default download stays clear of the ~700 MB the CUDA archives add
   (`docs/NATIVE-BINARIES.md`). It reuses `scripts/vendor-cuda.ps1`, the `native/**` glob and the
   NVIDIA attribution unchanged, and it keeps the runtime inside a whole-application package rather
   than a download of its own — the shape `docs/LICENSING.md` reads the EULA's stand-alone clause
   against. Cost: a ~1 GB release asset per version, deltas after the first. Not chosen: an in-app
   download of the CUDA archives (best experience, most new code — a user-writable search path for
   the loader, a Backends control, tests) and deferring CUDA past v1.
2. **Signing takes the free route: the SignPath Foundation's open-source programme.** No certificate
   is bought. Its terms, read at signpath.org/terms on 2026-08-16, decide what "signed" can mean
   here, and two of them cut across item 1:
   - *"Sign your own binaries only."* Upstream binaries may ship unsigned inside a signed package,
     but the project may not sign them. So `parakeet.dll` stays unsigned on this route, and Smart
     App Control — which evaluates every loaded binary — is not answered by it. What it does answer
     is SmartScreen's "unknown publisher" on the installer and the app.
   - The project may not contain a *"proprietary, non open-source component"* — which the CUDA
     flavour does, in the three NVIDIA DLLs. On that reading only the default flavour is eligible
     and the CUDA flavour ships unsigned. This has not been put to SignPath.
   - The certificate is issued to SignPath Foundation, so that is the publisher name a user sees;
     every release needs manual approval; the build must be verifiable from source (the CI publish
     is); a code-signing policy and a privacy statement have to be published — and the statement
     cannot be "transfers nothing", because the app downloads models and, per item 4, checks for
     updates.

   Eligibility is not established: nobody has applied. If SignPath declines, the alternative that
   costs nothing is to ship unsigned, and that would be a further decision, not this one.
3. **The installer is the desktop app only.** The CLI stays a zip beside it on the release, as the
   CI artefact is today: a smaller download and one thing to sign and update. Not chosen: both in
   one package with a PATH entry (Velopack has no PATH feature, so that is custom code on install
   and on uninstall), or two installers.
4. **Updates: check on launch, install on a click.** One HTTPS request to GitHub Releases at
   startup, a visible notice when there is a newer version, download and restart only when the user
   asks, and a setting that turns the check off. That request is the one thing the app does on the
   network unprompted, and the documentation will say so. Not chosen: Velopack's silent
   download-and-apply, and manual-only.

Defaults taken without a decision, all cheap to reverse: GitHub Releases is the host and the update
feed; a `v*` tag builds the release; the installer carries no weights — the Models tab downloads
them, as now; winget can follow later. **One thing to verify before anything is built:** Velopack
installs under `%LOCALAPPDATA%\<package id>`, and `%LOCALAPPDATA%\Uindosill\models` already exists
on every machine that has run this product. The package id or the layout has to keep the installer
from touching those files, and uninstall has to leave them.

## The honest summary

| Phase | Planned exit criterion | Met? |
|---|---|---|
| 0 — spike | Correct text from real weights on real Windows | Yes |
| 0 — spike | Timing and memory over real long audio | Yes |
| 0 — spike | A WER harness, so quantisation can be judged | Yes — all five entries within 0.08 points of f16 on 11 h of human-transcribed calls |
| 1 — core | `dotnet test` green on Linux, no weights | Yes |
| 2 — engine | CLI transcribes a real file to correct SRT | Yes, up to 2 h 55 m |
| 3 — CLI | Usable on its own | Yes (against the canned engine) |
| 4 — UI | A human transcribes a real file on Windows | Yes |
| 5 — ship | Signed, updating installer | Started: CI publish carries the natives; no installer, no signing |

### The dictation seam

The brief said push-to-talk dictation must not be built and must not be architected out. It is now
v3, behind the question-answering panel, and nothing about that ordering changes what it will
need. Two
things it would need are recorded rather than assumed: the streaming ASR and end-of-utterance
weights are pinned in the `deferred` array of `models.json` with exact sizes and digests, and the
loader, installer and digest checking reach them unchanged once a licence is established for each.
Neither is installable in this build, and the reason is written down in `docs/MODELS.md`.

**Phase 5 is the only thing between this and a v1 anybody can install.** Everything the product
does, it does; what it cannot do is arrive on a machine that has no .NET SDK and no git clone.

The next actions, in order:

1. **Phase 5 itself** — Velopack, and signing every PE rather than only `Setup.exe`. ~~A build
   that vendors the natives instead of expecting a manual copy~~ — done 2026-08-15, above. ~~What
   signing waits on is a certificate, which is a purchase rather than a commit; what Velopack
   waits on is a decision about how the opt-in CUDA tier arrives, since 700 MB does not belong in
   the default download.~~ Both decided 2026-08-16 — see *Decisions taken* above: the CUDA tier is a
   second download flavour, and signing goes through SignPath Foundation's free programme, which
   waits on an application to SignPath rather than on a purchase, and which by its own terms signs
   this project's binaries and not the upstream natives.
2. ~~**A WER harness**, which gates *recommending* q8_0 or q4_k~~ — **done 2026-08-16**, Phase 0
   above and `docs/UNPROVEN.md`. It was moved behind v1 on 2026-08-15 by making f16 the default,
   and it has now been built and run anyway: every catalogue entry scores within 0.08 points of f16
   against eleven hours of human-transcribed accented English, so the evidence a recommendation
   needed exists.

   **f16 stays the default for now.** `tdt-0.6b-v3-f16` carries `"recommended": true` and
   `tdt-0.6b-v3-q8_0` carries `false`, so `ModelCatalog.Recommended` — which `EngineFactory`
   resolves an unspecified `--model` to — returns f16. That was chosen on 2026-08-15 because f16
   was the one entry whose quality was not an open question, at the cost of a 1.34 GiB download
   instead of 941 MB and a slower CPU decode. The measurement removes the *reason* for that
   choice without making the other one: whether to make q8_0 (or smaller) the default is a
   product decision about download size and CPU speed against one corpus's worth of evidence, and
   it has not been taken. The catalogue's entries no longer say "unmeasured"; they say what was
   measured and where.
3. ~~**Settle the CUDA drop's licensing**~~ — **done 2026-08-15.** The EULA was read against what
   this product actually ships, `Attributions.Components` carries the NVIDIA entry so both
   `uindosill notice` and the Licences tab render it, and two tests hold it up. The reading, and
   the three things about it that remain unverified — no legal review, an EULA revision not
   contemporaneous with the CUDA 12.8 binaries, and an upstream redistribution this project did not
   perform — are in `docs/LICENSING.md` and `docs/UNPROVEN.md`. What is left is Phase 5 packaging
   the result.

### After v1

**v2 is asking questions about a transcript** and **v3 is push-to-talk dictation.** v2 went in front
because it needs none of the Win32 surface below — it reads a transcript this product already
produces — while what it does need is a second native stack and an answer to a harder honesty
problem than v1 ever posed. Its open decisions are recorded in `docs/V2-ASK-THE-TRANSCRIPT.md` and none
of them is settled. Neither version starts before Phase 5 ships.

**Pinning the model digests used to head this list** and is done: all five entries carry the exact
byte size and the SHA-256 read from the repository's LFS listing, `"verified": true`, and no entry
needs `--allow-unverified`. `docs/MODELS.md` has the table. That settles *provenance* and settles
nothing about quantisation quality, which is what item 2 is for.
