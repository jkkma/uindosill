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

**Status:** met. 1633 tests, no weights, no display, no network — **1624 passed and 9 skipped**, and
that pair is the same on every machine, which took a correction to make true. One skip is the Media
Foundation extension list, which is platform-specific. The other reads a FLEURS snapshot and is
asked for by name, for the reason below.

**They are opt-in rather than discovered, and the reason is this line.** The tests that read an
artefact first looked for it and ran when it happened to be there — so they passed on a measuring
machine and skipped on CI, and no single number could be written here that was true in both places.
A count that depends on what is installed is not a count this document can carry, so the suite gives
one answer everywhere and such a test is asked for by name:

```
UINDOSILL_FLEURS_DIR=<a google/fleurs snapshot's data/ directory> dotnet test Uindosill.slnx -c Release
```

**Seven of the nine skips left on 2026-08-21** with `Parakeet.Engine.Marian`, which went to
`attic/` when the translator moved into the bundled Python: they were the tokenizer's check against
the ids HuggingFace really emitted and the translator's against the English it really produces, and
neither has anything left in this solution to exercise. **Nothing replaces them in the suite**, and
that is a real loss rather than a tidy-up — what stands in for them is a six-sentence parity fixture
the sidecar runs at load on a real machine, which CI cannot reach. See
`### Decided 2026-08-21` below.

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

`transcribe`, `models`, `bench`, plus `doctor`, `notice`, `wer` — since 2026-08-16 — which
scores a transcript against a human reference and is what the Phase 0 harness is built on, and —
since 2026-08-17 — `der` and `rttm`, the diarisation error rate and the Audacity-labels-to-RTTM
converter the speaker measurement is scored with.

*Exit:* usable on its own; `bench` reproduces Phase 0.

**Status:** usable, tested end to end against the canned engine (114 of the project's 186 CLI
tests drive the real entry point; the other 58 never construct it — 18 on the backend default and
the resolver that turns `--vk-disable-bf16` and its opposite `--vk-bf16` into an engine option,
17 parser unit tests, 9 checking those two flags against the real command specs through
`CommandLineParser`, 7 holding the fallback line's timing and wording against a stub engine, 6 driving
`RunOneAsync`, `Report` and the translate verb's file loop directly with a labeller or translator
made to fail or refuse and a batch made to cancel, and 1 on the anomaly report, which is computed
before the translation pass — because no invocation can reach what they check). `bench` has not yet been pointed at real weights, so the RTF 0.10 figure above came
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

**What Start means was decided 2026-08-19, not merely fixed: it runs what has not been run.** It
used to hand the whole queue to the runner and reset every row on the way, so adding a fourth file
to a queue of three re-decoded the three — minutes a file, and `name (2).txt` beside every original,
neither asked for. Nothing in this repository settled it: the CLI's `--overwrite` and
`--skip-existing` are about output files on a one-shot invocation where the user names the inputs
each time, and they say nothing about a queue that persists and remembers which rows are done. It
was decided against the cost of being wrong in each direction — re-running what is finished costs
decode time and files nobody wanted, while skipping something a user wanted redone costs a click —
and against this window's own convention that a finished row is not silently un-finished, which is
why `JobViewModel.Apply` already refuses to let a late progress report resurrect one.

So a completed row keeps its transcript, its outputs and its "Done"; failed and cancelled rows are
retried, because pressing Start after a failure is how a person retries one; the status line names
what it left alone rather than reporting two out of a queue of three with no explanation; and Start
switches off once nothing is left to run — the same rule as the empty-queue defect above, which is
why a **Run again** button now carries the other intention. Changing the formats or turning the
speaker opt-in on and wanting the same files back is a real thing to want, and it is a press of its
own rather than a guess made from a press of Start.

## Phase 5 — ship — **STARTED**

Velopack, signing every PE, SmartScreen reputation, auto-update.

**Status: the installer exists.** Built 2026-08-19 and exercised end to end on the RTX 5080
desktop — installed, updated one version to the next, and uninstalled, with the 4.295 GiB of
downloaded weights on that machine hashed before and after every step and identical each time. The
section *Built 2026-08-19* below says what exists; `docs/UNPROVEN.md` says what that run does and
does not establish, and what nobody has done yet. What is **not** here is signing: v1.0 ships
unsigned by the decision recorded below, so every user meets SmartScreen's unknown publisher.

**Before that, from 2026-08-15,** the piece that had no external dependency. What existed before
was the groundwork: publish is self-contained + ReadyToRun and verified to cross-publish from Linux
for `win-x64`, single-file is on since 2026-08-23 and trimming stays off (both documented in
`Directory.Build.targets`, with the measurement that reversed the first), and every
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

Two things that was not. It was not an installer — the artefact is a directory you unzip, and no
transcription has yet been made from a CI-built binary; the first half of that is what the section
below closes. And it is not signed: the repository
still has no signing identity, and the vendored `parakeet.dll`s are unsigned third-party binaries
— `Get-AuthenticodeSignature` reports `NotSigned` for both the cpu and vulkan builds — which is the
shape Smart App Control blocks.

Remember that signing `Setup.exe` alone is not enough. Smart App Control and WDAC evaluate every
loaded binary, unsigned native DLLs are exactly what gets blocked, and a signed installer dropping
unsigned executables is itself a recognised malware shape.

### Decisions taken 2026-08-16

These close the two questions this phase was waiting on. **Nothing below was built when it was
written**; it was the plan, recorded so the next session would build what was decided rather than
re-decide it. All four are now code — *Built 2026-08-19* below is what came of them, and the two
sections are kept apart on purpose, so the decision and its execution can each be read for what
they are.

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

   That further decision arrived the same day, taken rather than forced: **v1.0 ships unsigned.**
   The maintainer decided it on 2026-08-16, independent of SignPath — no application gates v1.0,
   and the cost accepted is the known one, SmartScreen's "unknown publisher" on the installer and
   the app for every v1.0 user. The reading above stays for whenever signing is taken up, because
   nothing about the programme's terms changed.
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

### Built 2026-08-19 — the installer, the two channels, and the update check

Everything in *Decisions taken* above is now code. Velopack **1.2.0**, pinned twice: the
`Velopack` package in `Directory.Packages.props` and the `vpk` CLI in `.config/dotnet-tools.json`.
The two build opposite halves of one artefact — the Setup stub, and the runtime that talks to it —
so `scripts/package-windows.ps1` refuses to run when they disagree. Velopack itself only *logs* a
mismatch; the `throw` in its `CompatUtil.VerifyVelopackVersion` is commented out in 1.2.0, so the
check that stops a mismatched installer being built is this repository's.

**The collision the previous section said to verify first is settled, and the answer changed a
name.** Velopack installs under `%LOCALAPPDATA%\<package id>`, so the package id is
**`UindosillDesktop`** — deliberately not `Uindosill`, which is the directory holding the user's
weights. Nothing user-facing carries that string: the window title, the shortcuts and the
Add/Remove Programs entry all read *Uindosill*, from a separate `VelopackPackageTitle`. Both
properties live in `src/Parakeet.App/Parakeet.App.csproj` and there is exactly one copy of each —
the packaging script reads them with `dotnet msbuild -getProperty:`, and the csproj emits the id as
assembly metadata so `PackagingIdentity` and its tests read the value the installer was actually
built with rather than a second literal.

Three things hold that id down, because getting it wrong destroys every weight a user has
downloaded — 453 MiB for the smallest catalogue entry alone, 3.121 GiB for all three, and 4.295 GiB
on the machine this was checked on, which then had the whole quantisation ladder installed — and
nothing else in the build would say so:

- **Five tests** in `tests/Parakeet.App.Tests/PackagingTests.cs`. They assert the id is not the data
  directory's name, that it survives the case-folding and punctuation-stripping an installer might
  apply, and that the install root and the data root are disjoint — and then one of them rebuilds
  both directories under a temporary root using the real path arithmetic, writes a model file, and
  runs the recursive delete uninstall performs. Setting the id to `Uindosill` fails four of the
  five; that was checked by doing it.
- **The packaging script refuses to build**, with the same normalisations, before it publishes
  anything.
- **The observation below**, which is the only one of the three that is evidence rather than
  argument.

**Two channels, from one publish, and the difference is asserted rather than intended.** The
default channel `win` carries the cpu and vulkan natives; `win-cuda` carries those plus the opt-in
CUDA drop. The build copies whatever is in `native/` into its output, so a machine that has vendored
CUDA produces a CUDA-carrying publish for *both* channels — the script therefore deletes the backend
directories a channel does not promise, on disk before packing where they can be listed, and then
**opens each finished `.nupkg` and checks its native payload against what that channel claims**,
`LICENSE` beside every `parakeet.dll` included. Measured on the desktop: the default package
contains `cpu, vulkan` and no NVIDIA file at all, at 81.9 MB of `Setup.exe`; the CUDA package
contains `cpu, cuda, vulkan` at 818.6 MB. That is decision 1 working — the download almost everybody
wants stays clear of 730 MB of runtime they will not use.

An installed copy stays on its own flavour without being told. Velopack records the channel in
`current\sq.version` at install time (`<channel>win</channel>`, read off the installed tree) and
`UpdateOptions.ExplicitChannel` is documented as "should usually be left null … users automatically
receive updates from the same channel they installed from". `VelopackUpdater` therefore sets it to
nothing at all, and says why: setting it would silently move a CUDA user onto the default flavour
and take the runtime away.

**The release.** `.github/workflows/release.yml`, on a `v*` tag, on `windows-latest`. That runner is
a choice rather than a limit, and there are three reasons for it. Vendoring the CUDA drop reads a PE
import table, which is Windows. `vpk` compresses deltas with zstd — bundled on Windows, wanted on
`PATH` elsewhere, and *missing* it does not fail the build but silently falls back to bsdiff, which
in the 1.2.0 line produces patches `Update.exe` cannot apply (velopack/velopack#1008). And packing
Windows releases on Linux does work — `vpk`'s `[win]` directive, which the script passes
unconditionally — so nothing is being worked around. The Linux `build-and-test` and
`publish-windows` jobs in `ci.yml` still run on every push, so the cross-publish constraint this
repository is built on is still proved continuously; the release job is the one place that opts out
of it, for reasons written down rather than assumed.

The release carries both `Setup.exe`s, both full packages, the delta packages, both
`releases.<channel>.json` feeds, and the CLI zip — decision 3, the installer is the desktop app
only. Deltas are uploaded because without them every update is a full download: rc.1 to rc.2 was
**74 KB as a delta against 77 MB as a full package**. `vpk` builds one by diffing against packages
already in its output directory, and a fresh runner has none — so the job downloads the previous
release of each channel first, and tolerates there not being one. Without that step the delta glob
would have matched nothing on every release rather than only the first, which is the kind of thing
that is invisible until somebody measures a download.

The whole workflow was rehearsed twice on 2026-08-19 through a `workflow_dispatch` draft, and it
went green both times — `docs/UNPROVEN.md` has what those runs established and the one step they
could not reach, which is that same delta seeding: `vpk download github` does not see a draft
release, so the first delta will not be built until the second real release. Only `win-x64` gets an installer, because
upstream ships no `win-arm64` native and an installer that cannot transcribe is worse than none; the
arm64 publish stays a CI artefact, as it was.

**The app side.** `VelopackApp.Build().SetAutoApplyOnStartup(false).Run()` is the first statement in
`Program.Main`, before Avalonia — Velopack re-runs the same executable for install, update and
uninstall steps, and anything above that line runs in every one of those short-lived invocations.
`SetAutoApplyOnStartup(false)` is not the default: Velopack's own default is on, which applies an
already-downloaded update during startup, and decision 4's shape is that nothing installs itself.
`vpk pack` statically decompiles the main executable looking for that call and refuses to build
without it; it reports `Verified VelopackApp.Run() in 'System.Int32 Parakeet.App.Program::Main(System.String)'` (vpk's own rendering, quoted as printed — the method takes `string[]`)
on every pack here.

The rest is decision 4 exactly: one HTTPS request to GitHub Releases when the window opens, not
awaited so it never sits between a person and the window they opened; a banner above the tabs when
there is something newer, hidden the rest of the time; **download and restart only on a click**; and
an Updates tab carrying the version, the button and the setting that switches the check off. The
setting is written through on every change to `%LOCALAPPDATA%\Uindosill\settings.json` — beside the
weights, not in the install directory, because a settings file under the install root is destroyed
by every update and the check a user switched off would switch itself back on. Switched off, no
request is made at all, rather than one whose answer is discarded.

One thing that is easy to miss and would bring back a fixed bug: applying an update replaces the
process without a `Closing` event, so the update path calls the window's own `ShutdownAsync` first —
stop the batch, unload the model, release the native backend while the driver is still alive. Without
it, a CUDA user clicking *Download and restart* would reach the native static teardown with a backend
resident and hit gotcha 19's `0xC0000409` abort from a direction the close handler does not cover. A
test asserts the ordering.

`Velopack` is MIT and now has its entry in `Attributions.Components`, with its copyright line, so
`uindosill notice` and the About window render it; the licence was read off the restored package's
own `.nuspec` rather than the repository, because a package and its repository can disagree.

**What this cost the network surface: nothing.** The documentation commits to disclosing exactly one
unprompted network call, and that claim was checked rather than assumed. `Setup.exe` and `Update.exe`
are Rust binaries linking exactly one HTTP client, reachable from exactly two call sites, both behind
a runtime-dependency list that is empty unless `vpk pack --framework` is passed — which this project
must never pass, and does not. `Update.exe` has no update-check verb at all. A `strings` sweep of the
shipped 1.2.0 binaries in the NuGet cache found the Microsoft prerequisite hosts and **zero**
occurrences of `api.velopack.io`, no telemetry, no analytics. `vpk` itself does phone nuget.org on
every invocation to check for a newer `vpk`; that is build-time only and the script passes
`--skip-updates`. `docs/UNPROVEN.md` records both the finding and its limits.

### Designed 2026-08-19 — the interface, before the release rather than after it

The window this repository ships is stock Avalonia `FluentTheme` over a `TabControl`, which is what
a window looks like when nobody has decided anything about it. That was decided on 2026-08-19, and
**none of it is built**: three artboards — the app window, the unbuilt v2 Ask tab, and the token
sheet that specifies both. The sources and the full argument are in the dated folder
`ui-mockup-2026-08-19`, beside the other research on the maintainer's Drive, by the standing
convention that keeps research out of a public repository until v1.0 ships. What binds this
repository is here.

**One accent family per product generation, over a pure white ground.** Matcha for everything v1
does, taro for everything v2 adds. The two ramps are the same six points in oklch — lightness and
chroma identical at every step, to within 0.001 — rotated in hue only, matcha at ~128° and taro at
~304°. That matching is the whole mechanism: a taro panel can sit beside a matcha one without
either shouting, because nothing about them differs except hue. The rotation is *roughly* 175–180°
rather than one exact figure, because rounding each colour to 8-bit sRGB moves its hue a little;
the sheet says so rather than claiming a precision the hex values do not have.

The rule this buys is worth more than the colours: **a surface's colour says which generation it
belongs to.** Which is why speaker labelling — a v1 feature — cannot keep the purple `SPEAKERS`
badge it has today (`#6B4E9C`, in taro's neighbourhood); it would read as a v2 feature the moment
an Ask tab exists — as one now does. It becomes an outlined matcha badge, which still separates it from the solid
`LOADED` badge beside it.

**Type.** Instrument Sans throughout, monospace only for text you copy — paths, extensions, hex,
licence notices. Numbers you *read* — timestamps, durations, percentages — are the sans with
tabular figures, so they still align down a column without looking like code. The monospace is
Chivo Mono, and that was settled by inspection rather than taste: the zero glyph was read out of
twenty monospace families on Google Fonts, and only Chivo Mono and Azeret Mono draw it with no dot
and no slash through it. IBM Plex Mono, the previous choice, carries a dotted zero as its *default*
glyph with no `zero` or `ss01` alternate anywhere in its GSUB, so no stylesheet could have switched
it off. Both faces are OFL and ship inside the installer.

**Chrome.** No OS title bar: a 46px headerbar with the application name at the left, a pill
view-switcher centred, and circular window buttons at the right. Tab order is **Transcribe ·
Models · Updates · Licences** — Licences last, because it is the one tab nobody opens twice.

That order has since changed twice, and the reason Licences was last is the reason it is now gone:
Ask joined on 2026-08-22, and on 2026-08-23 Export and Settings were split off the Transcribe tab
while Licences retired into an About window opened from Settings. The order is now
**Transcribe · Ask · Export · Settings · Models · Updates**, six pills — which is also what set
`MinWidth`: six of them measure 464px and the headerbar's two fixed 210 columns plus its 14px
padding and the window's own 2px edge take 436 more, so 900, and 920 is that with room.

**Corners are square, and the exceptions are the argument.** A 12px-rounded version was built first
and put beside a square one on the same day; the square one won. Every rectangle is square — the
window, the panels, the buttons, fields, checkboxes, list rows, badges, progress bars. Four things
keep their shape, and each because the shape is doing work rather than decorating: the switcher
pills and the Ask panel's suggestion chips, which read as tappable *because* they are pills; the
speaker labels; the chat blobs, 12px with 4px on the speaking corner, which is what says who is
talking; and the things that are genuinely circles rather than rounded rectangles — the window
buttons, the transport's play button, and its seek handle. The rule that came out of it is shorter
than the list: **rectangles are square, circles stay circles, and a corner survives only where it
carries meaning.**

**That list was discharged on 2026-08-23**, when the seek handle — its last unbuilt item — was
drawn.

**The window buttons left that list on 2026-08-20**, at the maintainer's direction and after seeing
them on a real screen. They are a bare glyph now — a dash, the two overlapping squares of the
restore mark, and a cross — with no ground at all until the pointer is over them, which is how the
editors this application sits beside draw theirs. The circles were the one exception on the list
whose shape was doing the least work: a pill says *tappable*, a chat blob's flattened corner says
*who is talking*, and a filled circle in the corner of a headerbar said only that somebody had
decided to put a circle there. So the rule loses an exception and reads the better for it.

The buttons got **larger** in the process, which is worth saying because it sounds backwards. The
24px circle was both the mark and the whole clickable area; a bare 11px glyph sized to itself would
be a smaller target than what it replaced. The target is 40x32 with only the strokes visible, so
what shrank is the ink rather than the button. Close still takes the error red on hover with a
white glyph — the one place in this window where colour lands on a control instead of in a message,
and it keeps the exception because closing is the only irreversible thing in the bar.

**The word-by-word view is the design's one genuinely new idea, and it belongs to v1's data.** A
lane per speaker, two lines deep. Words appear as they are said and fill the lane left to right
with no motion at all; a word that would need a third line clears the last line and carries on
there; a speaker who goes quiet long enough loses the lane entirely. Nothing is ever drawn ahead of
the moment being played — words not yet said are absent, not dimmed. The word being said carries a
pastel yellow, which is the one place a third hue is admitted and is pinned to that single job.

Two speakers at once needs no special treatment under this scheme: it is two lanes lit at the same
time. An earlier version had the words scroll leftward out of a playhead and fade through a mask,
with a time ruler above it, and the ruler died with the motion — a ruler claims that horizontal
position means time, and once lanes fill and clear independently, two lanes at the same offset are
at completely different moments. Do not reintroduce one without reintroducing a shared time axis.

None of this needs a language model. Word timings are v1 data — `vtt-words` is already written —
and `docs/V2-ASK-THE-TRANSCRIPT.md` argues playback should land before any model does. So the whole
view is matcha, and it is the groundwork that document asks for rather than a v2 feature.

**Speaker labels are editable in place and swappable.** The diariser numbers speakers in the order
it first hears them, which is not the order anyone means, and the chip set is closed at four
because four is the architectural ceiling rather than a setting.

**Three colour corrections this found in shipping code, all three made the same day.** They were
defects in `src/Parakeet.App/Views/MainWindow.axaml`, not opinions about the new design:

- `#D9A441`, the warning colour on the no-speech hint and the provenance line, is **2.25:1 on
  white** and fails AA outright. `#966C13` is the same hue at 4.72:1.
- `#D9534F`, the error colour on a failed job, is **3.96:1** — under the line by a little.
  `#B84E45` is 4.98:1.
- A *verified* provenance line read "Verified against the repository, digest pinned" in that same
  warning amber, so a confirmation was painted as a problem. Amber belongs on the unverified case
  only.

**How they closed.** The two contrast failures were four literals across the file; they are now two
brushes in `Window.Resources`, so the next colour decision has one place to happen rather than
four. The provenance line follows a new `ModelViewModel.ProvenanceIsVerified`, which is true only
for the fully checked case — `Verified` *and* a pinned digest — bound to a style class, so amber
now says only what it means and the reassuring case reads as reassurance. The flag is asserted in
both directions by `OnlyAFullyCheckedEntryCountsAsVerifiedProvenance`, because an unpinned entry
saying "cannot be verified" in green would be the same defect pointing the other way. A second test
asserts on the *rendered* brush rather than the flag: this window has shipped a control bound to
nothing before, and a flag the view never reads would have looked exactly like a fix. Suite green
at 549.

Every figure above was computed rather than estimated, and every layout claim in the token sheet
came out of a headless browser with the webfonts confirmed loaded. The one thing in the design that
is **not** checked — how the window's corner behaves on Windows 11 — is in `docs/UNPROVEN.md` with what
would settle it.

### Decided 2026-08-19 — translating the transcript into English

**A v1 opt-in that produces an English version of the transcript beside it, decided
2026-08-19 and not yet built.** The study is in the dated folder `translate-to-english-2026-08-19`,
beside the other research on the maintainer's Drive, per `CLAUDE.md`.

**Four decisions were taken the day the study landed, and the first one overrides its
recommendation.** *Translation is aboard v1.0*, not v1.1 — the same call the diariser got, and for
the same reason: a release that transcribes 25 languages and can only hand back 25 languages is a
narrower product than the one intended, and shipping the narrower one first makes the wider one a
follow-up nobody schedules. *The gate is two criteria and both must hold*, ratified before any score
exists, in the shape the speakers gate was. *The pass is CPU-only in v1*, which keeps the pinned
`Microsoft.ML.OnnxRuntime` 1.29.0 and leaves the diariser's measured DER untouched; DirectML is the
Windows GPU path when a GPU path is wanted, and taking it moves the diariser too. **All three
clauses were overtaken on 2026-08-21** — the pass ships on WebGPU, no project in the solution
references that pin, and DirectML was measured wrong on both components. See *Decided 2026-08-21*. And *the decode
loop is built against the recommended checkpoint itself* rather than the cheap sibling the study
suggested, because the spike showed the mandatory target token is the invariant most likely to fail
silently, and a loop written against a model that needs no token would not exercise it.

**The direction was chosen rather than defaulted to.** Into English is the best-resourced direction
in every open translation family, which makes it the one whose quality claims are cheapest to
support, and this project ships no claim it cannot measure. English → other targets is out of scope,
and the naming the study proposes keeps it that way: `--translate` against a `TranslateToEnglish`
setting, rather than a `translate` that would later have to grow a target.

**One checkbox and no language picker, which is a finding rather than a preference.** This pipeline
cannot detect what it has just transcribed — the JSON `language` field records the request rather
than a detection, and `--language` is inert on this checkpoint (`docs/UNPROVEN.md` § *The language
hint*) — so any design that needs the source language has to ask the user for it. The recommended
family is many-to-one: the source is never declared, only the target, and the constraint never
binds. If measurement forces the bilingual-pair fallback the picker comes back, per file, labelled
as the user's assertion rather than a detection, and never offering "Auto". The cost of not having
one is that there is no per-language control either, which is what decides the gating question
below.

**The pass runs last — decode, label speakers, translate — and that order belongs to the code rather
than to taste.** `SpeakerAssignment` assigns a speaker per *word* and cuts segments where the
speaker changes; handed a segment with no words it falls back to whichever speaker talks most across
the span. Translated text carries no words, so translating before labelling would quietly coarsen
every label rather than fail where anyone could see it.

**Word timings do not survive translation, and nothing pretends otherwise.** `vtt-words` is refused
under the option rather than degraded in silence; SRT and VTT fall to the proportional-cue path
`SubtitleCueBuilder` already takes for word-less segments, which `WordTimedVttFormatter`'s own
comment calls "a reasonable guess about when to show a line and a worthless one about when a word is
spoken"; and the word-by-word view always draws what was spoken. Every artefact that *can* carry the
marker in-band does — and SRT cannot, having no comment syntax, so it is covered by its name
instead: translated files take an `.en` infix, which under `--overwrite` is also what stops a
translated run destroying the transcription run's output.

**Recommended route, pending measurement:** `Helsinki-NLP/opus-mt-tc-bible-big-mul-deu_eng_nld` —
apache-2.0 on its card, and all 25 source languages on a card that disclaims its own coverage list —
exported to ONNX in-house onto the ONNX Runtime dependency the diariser already ships, so no second
native stack and no `llama-server` in v1. Rejected: NLLB and TowerInstruct on non-commercial terms;
Gemma 3 and Llama 3.2 because gated weights break an unattended pinned-URL installer; MADLAD-400
because `llama-server` has no encoder-decoder path *today*, which is a dated exclusion rather than a
permanent one; and Whisper's translate task, which translates audio and would replace the ASR engine
rather than add to it.

**Four things were measured before any of this was built, and three of them change its shape.** The
target token is mandatory: the same Spanish segments without `>>eng<<` return fluent German, so the
prefix is an invariant the translator enforces rather than a convention a caller follows. Greedy
decoding is not safe — over 44 real segments it dropped content that beam-6 kept — so the decode
loop needs beam search and pays 2.1× to 2.3× for it. An int8 export was projected at 227 MiB or
404 MiB depending on whether the embeddings quantise, which is a download decision rather than a
rounding one — and the export on 2026-08-20 measured 345.9 MiB and 694.3 MiB instead, for the reason
*Built 2026-08-20* below gives. And English input passes through byte-identical, so the drift recorded below costs the pass
nothing to carry. `docs/UNPROVEN.md` § *Translating into English* has the numbers and what stays
open, including the encoder position limit, which turned out to be 1024 rather than the 512 the
study assumed and is no longer the feature's largest risk.

**Seam before model, the way the diarisation discriminator shipped before any diarisation entry
existed.** `ModelTask.Translation`, the manifest word, the badge and a fake translator can all land
with no entry in `models.json` at all — and that is as far as it can go, because every catalogue
entry today is one file and the ONNX route is more than one, which is a schema change with a defined
meaning for a partial install behind it. **That step is built as of 2026-08-19** — see *Built
2026-08-19 — the translation seam* below. How many files the route actually has was an assumption
until the export existed; it is nine, counted 2026-08-20 in *Built 2026-08-20 — the ONNX export*.

**The gate, ratified 2026-08-19, before a single score exists.** Two criteria, both of which must
hold. **One:** chrF++ into English clears the **per-language source-copy floor** — the score a
hypothesis earns by echoing its own source untranslated — by a margin fixed per language, because
that floor is a property of the language pair and a single number across 25 would be a different
bar in each. **Two:** a **human adequacy check on the Spanish → English driving case**, rated for
adequacy and flagged for output that is not English at all. Neither criterion is borrowed: no
published chrF++ or BLEU for any candidate on FLEURS X→en at a stated signature was found, so unlike
the DER gate this one cannot be pinned to somebody else's number and is anchored from inside the
measurement instead. The summary below carries it as a row. The premise the
whole feature rests on — that this checkpoint writes each of its 25 languages *in* that language —
is itself settled as of 2026-08-19: it was trained on an ASR subset alone, parakeet.cpp's own
benchmark returns Italian for Italian audio where the English-only checkpoint returns English, and
Spanish and German recordings transcribed here came back in Spanish and German. It holds as a
default rather than as a guarantee, and what is still unmeasured — from the English-drift rate on
spontaneous non-English speech to the other 22 languages — is in `docs/UNPROVEN.md`
§ *Translating into English*.

### Built 2026-08-19 — the translation seam, with no model behind it

**Step 1 of the feature above is code: the contract, the canned translator, the catalogue
discriminator and the CLI flag, in one commit that changes nothing an existing run does.** The same
order the diarisation discriminator shipped in, and for the same reason — the parts that do not
involve a model can be settled, tested and reviewed while the model is still a schema change and a
decode loop away.

`Parakeet.Core.Translation` sits beside `Transcription` and `Diarisation`, and the shape of its
contract is the feature's two facts rather than the other two contracts' symmetry.
`ITranscriptTranslator` takes `IReadOnlyList<TranscriptSegment>` and never audio, because
translation reads what the ASR wrote and a translator that opened the file would be a second
speech model. It returns `IAsyncEnumerable<TranscriptSegment>` rather than an annotation something
else applies, because unlike a speaker turn a translated segment *is* the displayable artefact.
`TranslatorCapabilities` carries `RequiresSourceLanguage`, `PreservesWordTimings`,
`SupportsCancellation`, `MaxSourceTokens` and the source and target language lists, on the terms
`SpeakerLabellerCapabilities` established: what a translator will not honour is said out loud
rather than dropped. `TranslationOptions` has one property — `ContextSegments`, defaulting to zero
because nothing has measured what context buys.

**The three things the spike settled are invariants in code, not comments.** The `>>eng<<` target
token is applied by `TranslationRequest.Build`, which is the only way a source string is built and
which refuses a blank token, because a forgotten prefix returns fluent German rather than an error
and nothing downstream would catch it. Word timings are dropped: a translator declaring
`PreservesWordTimings` false and returning words is refused by the driver, `-f vtt-words` is refused
under `--translate` from the capability rather than from a hardcoded rule, and the message says
which. And the order is enforced by where the pass sits — decode, label, translate — with every
yielded segment's start, end, source index and speaker checked against the segment it replaced, so
a translator cannot quietly recut the timeline the speakers were attributed on. A source past
`MaxSourceTokens` raises `SegmentTooLongException` and is never truncated.

`FakeTranscriptTranslator` is mandatory rather than convenient, for the reason the fake engine and
the fake labeller are: the whole suite runs with no weights on disk. It marks its sources through
the real builder, assembles the real context, drops the real word timings and refuses a real
over-long segment, so what CI exercises is the seam rather than a stand-in for it.

**`ModelTask.Translation` and its manifest word ship before any entry uses them**, which is the
second time that ordering has been needed: a build that did not know the word would list such an
entry as an ASR model. Adding the member compiles clean — nothing switches on the enum
exhaustively, because every site asks whether an entry *is* the task it wants — so the sites were
enumerated by hand rather than by the compiler. Two of them were wrong for a third task and are
fixed: the Models tab's badge said SPEAKERS for anything that was not an ASR model, and the window
subscribed every non-ASR entry's install state to the speaker checkbox's availability. The rest
were already right by shape, and `ModelTests` now holds all three tasks against the ASR lists.

On the command line, `--translate` runs the pass against the canned translator and refuses without
it, naming the missing translator rather than the ASR weights and stopping before any file is
decoded. Its help says what separates it from `--language`, which is a hint to the speech model
about the audio and reaches no translator, and passing both says so at runtime. Output takes an
`.en` infix — `call.en.srt` — which is how SubRip carries the marker at all and what stops a
translated run overwriting a plain one under `--overwrite`; JSON, Markdown and WebVTT say it
in-band as well, and an untranslated document's output is byte-identical to what it always was.
`--context-segments` is the one knob, and it means nothing without `--translate`.

One thing already in `transcribe` had to move rather than be added to: the anomaly report — script
disagreements and low-confidence words — now reads the transcript the engine wrote rather than the
document that comes back from the pass, because translation destroys both signals it rests on. A
translated segment carries no word confidences, and a stretch the model emitted in Cyrillic comes
back as English prose, so reading the report off the translation would have stopped reporting either
without saying so. No invocation can reach that difference — the canned engine writes Latin at
confidences well above the threshold, and the threshold is not a flag — so the two documents are
handed to the report directly in a test instead.

**Nothing is measured and no model has run** — on the day this was written. There is no entry in
`models.json`, no engine project, no decode loop, no checkbox and no harness; the gate above still
has no score against it. `docs/UNPROVEN.md` § *Translating into English* carries what the spike
settled and what it did not. **All of that except the checkbox changed on 2026-08-20**: the export,
the harness, the scores, and then the decode loop and the catalogue entry — see the four sections
below, of which § *Built 2026-08-20 — the decode loop* is the one that closes this paragraph.

### Built 2026-08-20 — the ONNX export, and what it turned out to be

**Step 2 of the feature above is an artefact and a script that reproduces it.** The step before the
catalogue schema, because the schema pins `fileName`, `sizeBytes` and `sha256` per file and there
was nothing to name, size or hash — and because "five files" was an assumption nobody had counted.
`scripts/export-translation-onnx.py` is committed for the reason `validate-der.py` is: this
repository ships an artefact no upstream publishes, so the thing that produces it belongs beside the
code that loads it. The graphs themselves go nowhere near the working tree, and the run's own report
— the manifest of names, sizes and digests, the verbatim smoke diffs, and the timings — is in the
dated folder `translation-onnx-export-2026-08-20` beside the other research on the maintainer's
Drive, per `CLAUDE.md`.

**The recorded export failure was a Python version, not a library pair.** `optimum` 2.1.0 against
`transformers` 4.57.6 failed inside optimum's own config normaliser, which is where the traceback
pointed and why it looked like optimum's fault. It is CPython 3.14: `functools.partial` gained the
descriptor protocol, optimum stores every `NORMALIZED_CONFIG_CLASS` as a class-attribute partial,
and reading one through `self.` now binds the instance into the first positional slot. Twelve lines
at the caller re-wrap those partials in `staticmethod` and the export runs unmodified — no pinning,
no patched install, and no hand-written `torch.onnx.export`, which the maintainer had agreed to as
the fallback and which is not needed. The shim is applied only when `functools.partial` is a
descriptor, so the script is correct on 3.13 too and becomes a no-op when optimum stops handing 3.14
a bare partial.

**The route is nine files, not five, and the tokenizer is five of them on its own.** optimum offers
two layouts and they are a different file count, so both were built rather than assumed: merged
keeps one decoder behind a `use_cache_branch` input, split keeps a with-past decoder and a
without-past one over nearly the same weights. **Merged is the one to ship** — it produced
byte-identical translations to split at every precision tested and stores the decoder once, which
is about 800 MiB of fp32 the split layout spends on nothing measurable. Past-key-values are exposed
in both, so beam search is not foreclosed.

**Both ends of the download decision are measured and the script picks neither.** The int8 route is
345.9 MiB with ONNX Runtime's default dynamic operator set and 694.3 MiB with `Gather` dropped from
it, against 1369.1 MiB at fp32 — merged layout, whole directory, tokenizer included. Those replace
the 227.3-and-404.4 spread, which counted each parameter once; the export unties what the checkpoint
ties, so the vocabulary matrix is stored three times. Which one ships was the maintainer's call and
was taken on 2026-08-20 — see *Decided 2026-08-20 — int8 is dropped* below — but it is taken there
and not here, and the script still produces both, because what an export tool records is what the
options were.

**The export reproduces fp32 PyTorch exactly; int8 does not, and one segment in 44 collapses.** At
fp32 every one of the 44 real segments the 2026-08-19 spike recorded at beam-6 came back
string-identical, which is the check the ASR's silent int8 collapse is the reason for. At int8 most
segments changed — mostly paraphrase, and one German segment came back with a degenerate repetition
loop under both int8 variants. That is not a quality measurement and does not touch the gate; it is
the reason the harness is the next thing worth building before the precision is chosen. **int8 is
also about 9% slower** than the fp32 export on this laptop, while the export itself is about 2.4×
faster than PyTorch — so what int8 buys is download size and nothing else that has been measured.

**Hosting was checked before a tag was proposed.** Velopack 1.2.0's `GithubSource` takes an
injectable downloader, so it was driven against a canned releases list: a release with no
`releases.{channel}.json` asset is skipped rather than fatal, and an all-weights list yields an
empty feed rather than an exception. The constraint it did surface is that the source asks for ten
releases and does not paginate, so fewer than ten feed-less releases may sit above the newest
installer release — a prerelease flag hides such a release from the candidate list but does not buy
back a page slot. `docs/UNPROVEN.md` § *The update check has never found an update* carries it.
**No tag has been pushed, no asset uploaded, and `models.json` is untouched** — the catalogue cannot
name a URL until an asset exists under an agreed tag. The tag proposed and awaiting the maintainer
is `weights/translation-mul-en-2026-08-20`, marked prerelease: outside the `v*` pattern that
triggers the 1.8 GB installer build, and dated because it has to be immutable — `models.json` pins
a URL and a digest together, so replacing assets under a standing tag would break already-installed
copies with a digest mismatch as the only symptom.

### Built 2026-08-20 — the catalogue learns to hold more than one file

**Step 3: an entry may now be a set of files in a directory of its own, and the meaning of a half
install is defined rather than discovered.** The step the ONNX export was blocking, because a schema
that pins `fileName`, `sizeBytes` and `sha256` per file cannot be designed against a file count
nobody has produced. It is nine.

`ModelFile` carries the four pinned things and `ModelDescriptor` carries a list of them.
**`FileName`, `Url`, `SizeBytes` and `Sha256` were deleted from the descriptor rather than kept as
shortcuts onto the first file**, which is the whole reason the change is safe: leaving them would
have let two dozen call sites keep compiling while silently meaning "the first of nine". The
compiler named every one instead. What the display sites actually wanted were `TotalSizeBytes` and
`IsFullyPinned`, and the second is an AND across the set — eight pinned digests out of nine is an
unverified entry, and the CLI and the Models tab both say which.

**A multi-file entry installs all or nothing.** It is assembled in a `<directory>.part` staging
folder and renamed into place in one move, only once every file has been fetched, sized and hashed.
Interrupt it anywhere and the disk holds a staging folder that `IsInstalled` does not look at and
`models list` does not report: there is an incomplete download, which resumes, and never an
incomplete model, which an engine would try to load. **Resume is per file** — a file already staged
with the right digest is skipped, because discarding eight good files because the ninth was
interrupted is over a gigabyte of somebody's bandwidth. The one window left is between deleting an
old directory and moving the new one in; a crash there costs a re-hash and no bytes, which is the
right price for not carrying a rollback journal.

The manifest refuses what it cannot mean: a `files` array with no `directory`, a `directory` that is
not a bare name — `../..` would have `models remove` delete outside the store — an entry carrying
both the inline and the array shape, a file listed twice, and two entries that would occupy one name
in the store. Each is a message naming the fix rather than "invalid manifest".

**No entry uses any of it, and that ordering is now the third time.** `ModelTask.Translation` shipped
before any translation entry and the diarisation discriminator before any diariser, for the same
reason: a build that meets a shape it does not understand mis-files it. `models.json` gains no
entry here — the export exists but no asset has been uploaded under any tag, so there is no URL to
pin. Twenty-four tests hold the schema, the store and the installer up instead, including one that
asserts every shipped entry is still a single file and tells whoever breaks it which documents to
check.

### Built 2026-08-20 — the translation harness, and the floors it computed

**Step 4: `scripts/measure-translation.py`, and criterion one of the gate now has a bar in every
language.** It scores chrF++ into English against the per-language source-copy floor on FLEURS,
pinning each `test.tsv` by SHA-256, checking the n-way alignment as far as sentence ids allow and
refusing to score a language whose overlap with English is too small to mean anything. It fetches no
audio: FLEURS transcripts go in, so **this measures the translation model alone and not the
cascade**, which is the right thing for a gate about translation and a lower bound on what a user's
own transcript would score.

**The floors are computed and they justify the shape the gate was ratified in.** All 24 source
languages, 2026-08-20: 2.00 to 23.10, driven by script rather than family — Ukrainian, Russian,
Bulgarian and Greek between 2.00 and 2.37, French, Italian, Dutch, Portuguese and Spanish between
21.40 and 23.10, the Latin-script Slavic, Baltic and Finno-Ugric languages between 14.54 and 16.84.
An 11.5× spread. The decision on 2026-08-19 to refuse a single number across 25 languages was taken
before any of this existed and is now measured rather than argued.

**The model half is not run, and that is a machine decision rather than a pause.** At 2.16 s per
sentence on the laptop's CPU it is about five hours per precision, and there are two precisions
worth scoring. It belongs on the desktop. One thing the shakedown did settle: **batching is six
times slower than not batching here** — 12.75 s per sentence at batch 16 against 2.16 s at batch 1,
because a padded beam-search batch decodes until its longest member finishes — so the harness
defaults to batch 1.

Scored on 2026-08-20 and the margin ratified the same day — see *Ratified 2026-08-20* below.
The human adequacy sheet has 60 Spanish rows, no ratings, and nobody scheduled to write any:
the maintainer declined it on 2026-08-20. **The gate is therefore not passed** — one criterion
clears in 23 of 24 languages, one clears, and one is unperformed. `docs/UNPROVEN.md` § *Translating into English* carries the floors and everything still open.

### Built 2026-08-20 — the backend the application starts on

**Two defects with one shape: the fastest tier a user had was never the one they got.** The window
defaulted to `ComputeBackend.Vulkan` unconditionally and persisted nothing, so the CUDA channel —
818 MB against the default channel's 82 MB, chosen deliberately — started on Vulkan every time, and
a user who noticed had to change the dropdown on every launch. Against the desktop's measured tiers
that is RTF 0.0110 where 0.0064 was available, given away twice over.

**The default now comes from what is on disk, which is the honest signal.** `cuda` is a directory
the default channel does not ship, so its presence means somebody went and got it —
`ParakeetNativeLibrary.BackendsPresentOnDisk` answers that as the file-system question it is,
sharing the loader's own root list so the two cannot disagree about where a backend lives. CUDA
outranks Vulkan outranks CPU, and nothing on disk still means Vulkan, which is what a build from
source with no vendored natives should say. Reading Velopack's channel name was the alternative and
answers a different question — how the application was packaged, rather than what it can reach.

**The choice is remembered, and a stored choice always wins**, including when it is the slower one:
somebody who picked CPU because the GPU path misbehaves has said something, and reinstating the GPU
under them would be the setting mattering least exactly when it matters most. Reading a default is
not choosing one, so construction writes no file — otherwise the pick would harden into a stored
choice and overrule a later release that picks better.

Two things that fell out of it. `AppSettingsStore.Update` exists because the moment the file held
more than one setting, `Save(new AppSettings { OneField = value })` silently reset the others — both
call sites did exactly that while it was harmless, and now neither can. And the loader's rule that a
CUDA request falls back to CPU rather than Vulkan was written when asking for CUDA was always
deliberate; now that it can be a default, a machine with the CUDA drop and no working driver lands
on CPU for one launch. The Models tab already names that fallback in a warning, and the choice made
instead is kept, so it is visible and it is once.

**The CLI took the same default, and had to grow a notice first.** A bare `--backend` now resolves
the same way, through the same `ParakeetNativeLibrary.PreferredBackend`, so one install cannot
answer the question two ways depending on which front end asked. That changes scriptable behaviour,
which is why it came with the thing the CLI did not have: `transcribe` never reported the loaded
backend at all. Survivable while the default was always Vulkan — the user had typed nothing to be
contradicted — and not survivable once a machine can resolve to CUDA it cannot run, fail, and land
on CPU twelve times slower with no line of output saying why. The notice goes to stderr with the
other warnings, names both backends, and points at `--backend vulkan` when CUDA falls through,
because the loader's chain skips Vulkan on purpose. `--backend vulkan` pins the old behaviour
exactly.

### Measured 2026-08-20 — the GPU priced against the CPU, and neither component takes it

**Two of the three model components run on ONNX Runtime and neither had ever been run on a GPU.**
Both now have been, in Python outside the working tree, with one thing changed per arm — the
`InferenceSession` provider list — and with the diariser's two arms sharing a single
`onnxruntime-gpu` install so that even the binary is held constant. **Nothing shipped moved:**
`Microsoft.ML.OnnxRuntime` 1.29.0 is still pinned, `Directory.Packages.props` is untouched, and no
figure here came out of code the product carries. The study is in the dated folder
`execution-providers-2026-08-20` on the maintainer's Drive, per `CLAUDE.md`.

**The instinct behind the ask was that the GPU should win everywhere and the CPU should be the
fallback. It was tested rather than implemented, and it does not survive either component — for
opposite reasons.**

**The diariser is 21.8x faster on CUDA and gives a different answer.** Over 9.062 h of AMI test
audio: 76.6x realtime on the CPU execution provider against **1230.9x** on CUDA for the whole pass,
78.1x against **1705.7x** for the graph alone, the gap between those two being the mel featurizer,
which stays on the CPU in both arms. The GPU demonstrably ran — 5,601 of 5,604 nodes on
`CUDAExecutionProvider`, counted out of ORT's own profile rather than read off the requested list —
and sm_120 cost 0.72 s of session build against the CPU's 0.63 s. **But zero of sixteen meetings
produced identical probabilities**, the largest difference being 0.964, and pooled DER at collar 0
moves from 16.3324% to 15.7062%. That apparent gain is **one meeting**: TS3003c swings 11.14 points,
and over the other fifteen CUDA is 0.0647 points *worse*. The cause is already in the record — the
arrival-order speaker cache is stateful and `torch.topk` leaves ties among equals undefined, so a
different set of floating-point reductions hands a cache slot to a different speaker for the rest of
a recording. For scale, the C#-against-Python port difference is 0.0044 points; the provider is 142
times that. CUDA is bit-exactly reproducible against itself across two full runs, so the difference
is fixed rather than noisy: a GPU DER can be measured once and trusted, and cannot be inherited.

**The translator is the mirror image — identical output, and almost no speed.** `fp32-merged` on
CUDA returned **240 of 240 sentences string-identical** to the CPU across Latin, Cyrillic and Greek
script, so here a GPU-produced result *could* stand in for a CPU one. What it buys is **1.2x to
1.5x** in the only configuration that survives every input; with IO binding on it is 3.7x and
crashes input-dependently inside optimum on a stale pre-allocated KV buffer.

**So v1 stays on the pinned CPU build, and the decision of 2026-08-19 is now measured rather than
argued.** It was taken then on the strength of a package version —
`Microsoft.ML.OnnxRuntime.DirectML` 1.24.4 lagging 1.29.0 — and it holds for a better reason: the
component with a real speed-up is the one whose accuracy claim the provider moves, and the component
whose output survives the move is the one with nothing to gain. **What would revisit it** is
re-running the diariser's 18-meeting dev grid on CUDA probabilities and scoring test once after; at
26 seconds a pass that costs an afternoon of somebody's attention rather than a machine's, and it is
the right first item *after* v1.0 rather than before. **Re-running the AMI test is not the expensive
part**, which corrects an assumption rather than a measurement: 8.2 minutes through the product on
CPU, 7.1 through the Python driver, 26 seconds on CUDA.

**And no real-time factor from this project gets quoted without its execution provider beside it.**
There are now two providers for one graph with an order of magnitude between them, and a bare "65x"
no longer identifies a measurement.

### Ratified 2026-08-20 — the per-language margins, and the language they fail

**The margins are set: `margin_L = 45 − floor_L`, and Slovak fails.** The gate reserved the margin
for the maintainer and it was taken the day the first scores existed, on the proposal below, which
was written from quantities that do not depend on the scores. The arithmetic is in
`execution-providers-2026-08-20` on the Drive; what is here is the shape, the consequence, and the
one thing the choice actually decided.

**A third criterion was added at the same time: zero degenerate collapses.** The gate is now three
criteria and all three must hold. The trailing-punctuation rate was considered for the same
treatment and **deliberately left out** — it stays a number reported beside the score, because it
costs no meaning and no rate for it has been argued for.

**The floor is cleared everywhere by 32 to 83 standard deviations, so anchoring the margin on
corpus noise binds on nothing.** Bootstrapping the sentence pairs 200 times per language — the
*difference* between score and floor, since the two are computed on the same sentences and are
correlated — puts the corpus's resolution at **0.6 to 1.0 chrF++ points** against margins of 28 to
60. The gate as literally written is therefore a **liveness check**: it asks whether the model beat
echoing its own source, and the answer is yes everywhere, decisively. A quality bar has to come from
somewhere else.

**A table of 24 margins is one decision wearing 24 hats.** chrF++ into English is
script-independent — 44.26 to 68.52 — while the floors are almost entirely script — 2.00 to 23.10.
So `margin = score − floor` is a near-constant minus a floor that varies 11.5x, and the proposal is
to say so: **margin_L = B − floor_L for a single absolute bar B**, plus a 3-standard-deviation sanity
floor that currently binds on nothing. That keeps the gate's letter — the margin is fixed per
language and differs in each — while making the thing under discussion a number a person can hold.

**And choosing B is very nearly choosing whether Slovak ships.** Slovak scores **44.26** against a
next-worst 53.47, a nine-point gap, so **every B between 44.26 and 53.47 fails exactly one
language**. The record predicted it: `docs/UNPROVEN.md` already noted Slovak has no row on the
sibling card's Tatoeba table, consistent with its absence from that card's source list. What makes
it awkward is the **one-checkbox design** — this pipeline cannot detect the source language, and the
translator is many-to-one, so there is no control to withhold one language with. Failing Slovak
cannot mean "do not translate Slovak"; it can only mean "do not ship the feature" or "ship it and
say so in the text".

**B = 45, ratified, and it fails Slovak by 0.74.** A bar should be one you would notice falling
below: 40 certifies whatever it is shown, and 54 describes this checkpoint rather than setting a
standard. 45 sits just above the observed worst, which makes it a bar this model **fails in one
language of 24** — and a gate nothing can fail has told you nothing. B = 44 was the alternative on
which everything passes and was rejected as a bar chosen to be cleared.

**What failing Slovak means here, since it cannot mean withholding Slovak.** The pipeline cannot
detect the source language and the translator is many-to-one, so there is no control to switch off.
So the ratified position is: **Slovak fails criterion one, the feature ships anyway, and the failure
is carried in the text** — the same place `docs/UNPROVEN.md` already says the honesty has to live
for a one-checkbox design. What that costs is a language whose English output is nine points below
every other, offered to users with no marker in the product itself saying so; **writing that marker
is not done, and it is the open item this ratification creates.**

**And the bar is now the harness's business rather than a reader's.** `measure-translation.py`
applies `45 − floor_L` and the zero-collapse ceiling itself and prints PASS or FAIL per language, so
a future run says whether it passed instead of leaving somebody to compare two tables. A number
nobody checks is a number that goes stale, which is the same reason `check-test-counts.py` exists.

### Decided 2026-08-20 — int8 is dropped, and fp32-merged is what ships

**The precision question closed the day it stopped being close.** `int8-merged` was the 345.9 MiB
end of a download-size-against-quality call that the export deliberately left open. Three
measurements taken on 2026-08-20 closed it against int8, none of them a quality score:

- **It is slower, not faster, and increasingly so.** On the desktop's CPU, beam-6, batch 1:
  `fp32-merged` 0.602 s per sentence against `int8-merged` 1.279 on 32 Spanish sentences — **2.1x**,
  where the laptop had measured 9%. Read off at matched indices of the same length-sorted Bulgarian
  run, the ratio is **4.2x at 100 sentences and 8.3x at 239**, because dynamic quantisation
  re-quantises activations on every decoder step and beam-6 takes a set of steps per output token.
  The saving int8 exists for gets worse the more work there is to do.
- **It is silently wrong on a GPU.** Through the CUDA execution provider it returned 0 of 32
  sentences matching the CPU, none of them empty, with no exception and no warning — `These couples
  may choose to plan the adoption of a baby` came back as `The so for`. `fp32-merged` on the same
  provider is string-identical to the CPU on 240 of 240.
- **It already had a collapse on the record.** The export smoke found one German segment in 44
  returning a degenerate repetition loop under both int8 variants and none under fp32.

**What it costs is 1023.2 MiB on every download**, and that is the whole of the case for the thing
that was dropped. **What was not done is score it**: the quality run that would have priced int8
against the floors was stopped in its second language when the decision was taken, so **int8 has no
chrF++ in this repository and no claim that it scores worse against the gate.** The harness's
default `--variant` now points at `fp32-merged` so that omitting the flag scores what ships, and
`scripts/export-translation-onnx.py` still produces every variant, because what an export tool
records is what the options were.

**The sibling variant goes with it.** `int8-merged-fp32-embeddings`, the 694.3 MiB middle, produced
the same degenerate German segment as the default operator set, so nothing measured distinguishes it
on quality and it has never been timed on this machine at all.

### Built 2026-08-20 — the decode loop, and the oracle it was held to

**`--translate` reads real weights.** The seam shipped on 2026-08-19 with a canned translator behind
it and the entry above records what the export turned out to be; this is the part in between —
a SentencePiece tokenizer and a beam search, in C#, in a new `Parakeet.Engine.Marian` beside
`Parakeet.Engine.Sortformer`, on the same terms: one project owns one model's interop, `Parakeet.Core`
keeps its contract and knows nothing about ONNX Runtime, and a build target in Core fails if a NuGet
reference ever appears there. `TranslatorFactory` came off the fake, `models.json` gained its first
multi-file entry, and the claim `docs/UNPROVEN.md` had carried since the route was chosen — *nothing
this product ships has translated a word* — closed.

**The thing that made this different from ordinary porting: the graphs are pinned and the search
over them is not.** Every chrF++ figure this project publishes was produced by HuggingFace's beam
search driving those two ONNX files. The files are fixed by digest. The search is a set of choices,
and each of them changes the English while leaving it looking entirely correct — whether a finished
hypothesis is scored by its total or its mean log probability, whether the loop stops when the beams
are full or when they can no longer improve, how equal-scoring candidates are ordered, whether a beam
that has just emitted the end token can still be continued, how `bad_words_ids` and
`forced_eos_token_id` are applied. The diariser had already shown what that costs here: one numerical
tie-break moved a meeting by 11 DER points. So this is a port of **one implementation** —
transformers 4.57.6's `GenerationMixin._beam_search`, the vectorised rewrite rather than the older
`BeamSearchScorer`, which is a different algorithm with the same name — read out of the installed
source rather than recalled, and its shape is not the textbook one: it keeps `2 x beams` candidates
per step so a step in which every top beam ends the sentence still leaves live continuations, and
only a candidate from the step's top `beams` may enter the finished set at all.

**So there is an oracle, it already existed, and it was not built for this.** The gate run of
2026-08-20 recorded every hypothesis it produced — **8,149 sentences across 24 languages**, source
and output — in `hypotheses/*.jsonl`. That is the acceptance test for the port, and it is a
different question from "does the output look reasonable": *does it reproduce these strings.*
`scripts/measure-translation-agreement.ps1` asks it, per language, as exact ordinal string equality,
and writes any disagreeing pair out verbatim. ****8,148 of the 8,149 reproduce the recorded hypothesis character for character — 99.99%, with 23 of the 24 languages at exactly 100%.** The one disagreement is Hungarian, on a sentence the gate run had already flagged as degenerate: both implementations write the same English for 427 characters and then differ only in how long a run of trailing ` .` goes on, 171 against 248. Rescored at the gate's own signature it moves Hungarian's chrF++ from 56.75 to 56.79 — +0.04, in the port's favour, against a required margin of 29.55 — so the verdict is unchanged and the other 23 languages are unchanged by construction. **The chrF++ table therefore describes what ships**, which is the sentence this whole step exists to be able to write.**

**Three traps in the config, all of which produce plausible output when got wrong, and all three are
now code with a comment on them.** `generation_config.json` says `num_beams: 4`; nothing this project
has measured used four, and the loop takes six from `MarianDecodeSettings` and treats the file's
number as something to notice a change in rather than obey — a test asserts the two differ.
`decoder_start_token_id` is **58433**, which is also `pad_token_id` and is also in `bad_words_ids`:
three roles for one id, and each has a different consequence if confused, so the type that carries it
says which is which. And `>>eng<<` is **one token**, id 693, cut off the front of the string before
SentencePiece sees anything — a tokenizer that hands it to the Unigram search gets plausible ids back
and has silently lost the target, and the same segments without it return fluent German.

**What CI can check, and what it cannot.** The fixture that pins the tokenizer's ids cannot be
recomputed without `source.spm` and `vocab.json`, which are 3.06 MB of an artefact no clone carries,
so those seven tests skip themselves wherever the checkpoint is absent — the repository's standing
rule is that a test needing weights is not a test CI will run. Everything that does not need them is
hermetic and does run: the protobuf reader against a `ModelProto` the test writes byte by byte, the
darts-clone double-array trie against a one-key trie written out in its own bit encoding, the Unigram
search against two score tables that segment the same input two different ways, byte fallback, and
the whole beam search against scripted logits — where a banned token can be made the most likely
continuation and the length penalty can be made to decide between a short hypothesis and a long one,
neither of which a real model will produce on demand. Of the Marian project's 31, twenty-four run
everywhere and seven need the checkpoint.

**One new command, and it is the diariser's argument again.** `uindosill translate` takes a text file
and writes the English one, line for line, with no audio and no ASR — the same translator behind the
same seam as `transcribe --translate`, without the decode that costs orders of magnitude more and
contributes nothing to a translation. `uindosill diarise` exists for that reason and this exists for
a sharper version of it: the corpus the decode loop is held to is written sentences with no audio at
all, so there is no path to it through `transcribe`. It has deliberately no beam, context or length
option — those are the degrees of freedom above, and a flag would make it easy to produce a number
that describes nothing.

**`--context-segments` is reported as ignored rather than silently doing nothing.** The option has
shipped since the seam landed and no translator this product can ship reads it: the checkpoint is a
sentence-level model with no way to mark which part of its input is context, so folding preceding
segments in would translate them too and leave the caller splitting one English paragraph back into
its parts by guess. `TranslatorCapabilities` gained `HonoursContext` and the CLI says the value was
ignored — the same shape as the diariser being told a speaker count it cannot use. A lever that
silently does nothing is worse than no lever.

**What this does not close.** The gate is still **not passed**, and for the same reason as before:
its second criterion is a human adequacy check that was declined on 2026-08-20 and is queued to
nobody. Slovak still fails criterion one by 0.74. No multi-file entry has ever been installed from a
real URL, because no release asset has been uploaded — the entry pins nine digests and is marked
unverified, and everything measured here loaded the checkpoint from a directory instead. No
real-time factor for a translation pass over real audio has been measured. And the window's
checkbox and pane switcher are still not built.

### Decided 2026-08-20 — the speaker cap warns rather than refuses, and what a forced count would mean

**`--speaker-count 7` used to be told the truth in a way that read as harmless.** The message was
*"the value is ignored"*, which is accurate — the diariser estimates the count and cannot be told
one — and which a reader takes to mean nothing was lost. Nothing said that seven was never on offer.
The only thing that ever mentioned the four-speaker cap in a `transcribe` run was
`SpeakerAssignment.DescribeLimit`, **after** the pass, and what it says there is *"4 speakers were
labelled"* — a sentence about the recording, which is exactly the wrong thing for somebody who does
not know the tool has a ceiling.

**Warn loudly, up front, and continue.** Fired before a byte of audio is read, in both
`uindosill diarise` and `transcribe --speakers`, naming the user's own number and the model that
cannot reach it. It does **not** refuse: somebody with six speakers who knows they will get four
still has a good transcript — the words are untouched and only the labels are capped — and blocking
that run would cost them something real to protect them from something they have just been told. It
is also the house pattern, where a count that cannot be honoured is reported as ignored rather than
applied.

**CLI only.** The desktop application has no speaker-count input at all, so there is nothing there
to warn about and nothing in `Parakeet.App` is touched.

**The `diarise` command's standing cap note is suppressed when the specific warning fires**, because
saying the same thing twice in weaker words dilutes the first telling. It still prints on a run that
asked for no count, which is where it earns its place.

**And one decision taken here that is not built, so it is not re-argued later.** If **channel
merging** is ever built — it is not being built now, and nothing about it is designed — and a
user's `--speaker-count` conflicts with what the channels say, **the user's count wins and the
transcript's provenance records that it was forced.** The reasoning is the one above: the person in
the room knows how many people were in it, a channel count is an artefact of how the recording was
made, and a transcript that silently overrode the user would be a transcript whose speaker labels
nobody can account for. Recording that it was forced is what keeps the override honest.

### Measured 2026-08-20 — the cascade penalty, recorded and deliberately not gated

**Nothing had priced what ASR error costs the translation, and the whole of the evidence was one
sentence.** `scripts/measure-cascade.py` fixes that using the one property of FLEURS that makes it
nearly free: it is n-way parallel, so the same sentence ids exist as Spanish audio, as Spanish text
and as English reference text. Both arms run in one process over one id set — transcripts in, and
audio through the recogniser and then in — and the gap between them is the recogniser.

| | sentences | text-in chrF++ | cascade chrF++ | penalty | ASR WER |
|---|---:|---:|---:|---:|---:|
| es (`es_419`) | 348 | 56.17 | 53.22 | **−2.95** | 6.12% |
| de (`de_de`) | 347 | 63.64 | 59.30 | **−4.34** | 9.93% |

**The text-in arm reproduced the gate's published 56.17 and 63.64 exactly**, which is the check that
this harness is measuring the gate's object: it is recomputed rather than quoted, so the subtraction
is between two things that differ only by the ASR.

**The penalty decomposes the reassuring way.** German has 1.62× Spanish's word error rate and 1.47×
its penalty, so the loss scales roughly with how wrong the input is — the translator is not
disproportionately brittle to slightly-off text, which was the alternative this was built to
distinguish. Neither language's verdict moves: Spanish clears its bar by +31.82 after the cascade
against +23.60 required, German by +38.52 against +24.22.

**Two of the twenty-four source languages now have a word error rate**, at 6.12% and 9.93%. Every
WER this project had until today was English.

**Recorded, not gated, and that was decided before the number existed.** A bar argued for after
seeing the figure is not a bar, and the gate already carries one criterion nobody has performed. The
audio halves of both FLEURS configs are pinned by the digest the repository publishes, the way the
TSVs already were.

### Measured 2026-08-20 — the diariser on whole podcasts, and the limit that is not the cap

**The cap warning above was built for one risk and the measurement found a bigger one.** The four
episodes went through `uindosill diarise` on the CPU — 2, 3, 5 and 7 speakers, confirmed by the
maintainer that day — and **all four returned four labels**. Above the cap that is the merge the
model advertises. Below it, it is the opposite: two hosts produced three substantial clusters, three
speakers produced four. On this material the number four says nothing, and a user cannot tell which
of the two things happened to them.

**It is duration.** Same audio, same onset, window grown: correct at 10, 30, 40 and 50 minutes, and
wrong from an hour. A second episode gives the same shape one rung later. **AMI meetings average
about half an hour** — inside the range where it is right — so the gate this model passed could not
have exercised it. AMI dev re-scored the same day is 8.62% DER at collar 0.25 with 0.94% confusion
and 4-of-4 speaker agreement on all eighteen meetings: this is not a model that confuses speakers in
general, it is a model whose speaker identities do not survive a long recording.

**Two diagnostics rule out the easy explanations.** The spurious cluster is spread across the whole
window rather than appearing after long exposure, and the stretch a failing window contains that a
passing one does not is *correct in isolation*. What is left is over-segmentation of one host into
two labels. **Nothing was re-tuned to chase it** — the post-processing is still the one fixed on the
18 AMI dev meetings and applied unchanged, because changing it would invalidate the gate — and no
root cause was established.

**So a second warning was added, in the same shape as the first.**
`SpeakerLabellerCapabilities.ReliableUpTo` is **fifty minutes** for this model — the longest length
at which every window tested was right, rather than the rounder hour at which one of four failed —
and it is a different kind of limit from `MaxSpeakers`: the cap is architectural, in the model's geometry, knowable without
running anything; this is empirical, and it is where the evidence stops. Past it the labels are not
known to be wrong so much as not known to be right, which is the distinction the sentence has to
carry. It warns and continues, `diarise` and `transcribe --speakers`, before a sample is decoded.

**And then it was fixed, by repairing the output rather than re-running it.** The cause is in the
geometry: the speaker cache is 188 encoder frames — 15.0 s in total, **3.52 s per speaker** — and the
ONNX graph takes it as an input and never updates it, so a speaker's identity is 3.5 seconds of
recent audio with no long-term anchor. That is the streaming design, and this project's port of the
eviction is fixture-validated against NVIDIA's own function, so the repair had to go around it.

**Windowing was tried first and is rejected on a number.** Eight-minute windows with two minutes of
overlap, linked by matching labels on the overlap where the same audio is labelled twice: 26 of 30
windows are internally correct, and scored on AMI dev the stitched result is **DER 23.53% against
8.62%**. Missed speech and false alarm barely move; **confusion goes from 0.94% to 15.76%**, because
one bad junction relabels everything after it.

**`SpeakerTurns.FoldDownTo` ships instead.** The failure is always over-segmentation, which is the
repairable direction — two labels merge, one label cannot be split into two people — so the labels
are folded down to the count the user asked for by repeatedly merging the pair that talk over each
other least. It is **a no-op on all 18 AMI dev meetings**, which is what lets it ship against a
passed gate and is exactly what windowing could not offer.

**It never fires unasked, and that is measured.** An automatic version would merge two *genuinely
different* AMI speakers in `IS1008a`, whose least-colliding pair overlaps by 0.0 s across the whole
meeting — one in eighteen. So the fold requires an explicit `--speaker-count`, and **that flag stops
being ignored**: it becomes the cap the transcript is folded to, which is the rule already written
down here for channel merging — the user's count wins, and it is recorded that it was forced. Each
merge prints the seconds the pair overlapped *and how far behind the next-closest pair was*, because
on a three-hour recording the raw seconds mislead and the margin is the evidence.

**`docs/UNPROVEN.md` has the ladder, the shares and everything this does not establish** — no DER on
any podcast, one show, the counts on the maintainer's word, and no root cause.

### Settled 2026-08-20 — the repair reaches the window, and past the bound it is required

**Both halves of the above were on the command line and neither was in the application**, which is
the worse half of the gap: `--speaker-count` drove the fold and `DescribeDurationRisk` fired before
`diarise` read a sample, while the window passed `SpeakerLabellingOptions.Default` — count null, fold
never reached — and reported only `DescribeLimit` afterwards. A two-hour recording labelled there
came back with speaker names, no warning that they were past where the evidence stops, and no
control that would have repaired them.

**The window now carries the count, and the warning arrives when the file is queued rather than when
the batch ends.** Opening a container reads its header and not its audio, so the length is known at
the moment a file is dropped — which is what makes the warning something a person can still act on
rather than a note beside a finished transcript. The merges the fold makes are reported on the row
with their margins, exactly as the command line prints them.

**Past the bound a blank count stops the batch.** Not a default of two, and not a silent estimate:
the fold merges whichever pair collides least whether or not the evidence supports it, so a guessed
count forces an answer rather than estimating one, and puts two people under one name with no margin
behind the merge. The window asks instead, and both ways out are decisions — give the number, or turn
labelling off and take the transcript without names. Inside the bound nothing changes, because that
is the range where estimating is measured to work.

**`--speakers` keeps warning and running, and that asymmetry is deliberate.** A window has somebody
in front of it who can answer a question; a command line is scripted, and a refusal there breaks a
pipeline that has been running for months.

**What this does not touch.** No measurement moved. The count is still a count rather than a DER, a
count given past the bound is still unpriced, and the four-speaker cap is still architectural — above
it the fold has nothing to fold, which is why a count over the cap is reported as unreachable rather
than accepted.

### Settled 2026-08-22 — the window requires the count whenever labelling is on

**The past-the-bound rule above is now the rule everywhere in the window: `Label speakers` does not
run without `How many speakers`.** Inside the bound the estimate is still measured correct, and that
is not what decided this. What decided it is what the estimate's failure looks like from the outside:
a drifted host arrives as a plausible extra speaker, the transcript carries four names, and nothing in
the output — not the JSON, not the RTTM — says which of "four people" and "one person heard twice"
happened. A transcript made with a count and one made without are indistinguishable afterwards, and
the person pressing Start trivially knew the number. So the window asks every time, the field's
placeholder says *required* rather than *estimate*, and a file past the bound still gets the sentence
that names it, because "this recording is where the estimate is measured to go wrong" is more
actionable than the rule alone.

**Still blank rather than defaulting to two.** The number has to come from the user for the fold to
mean anything — a guessed default would merge two genuinely different speakers in `IS1008a` and stamp
the merge with a margin. Required and defaulted are different things, and only the first is honest.

**The command line keeps the estimate, and the asymmetry above widens rather than closes.** `--speakers`
without `--speaker-count` still warns and runs: it is scripted, it is what every diarisation
measurement in `docs/UNPROVEN.md` is taken through, and a refusal there would break both. The window
is the surface with somebody in front of it.

**What this does not touch.** No measurement moved and no engine changed. The estimate is as good or
as bad as it was; the window simply stops accepting it in place of an answer it can ask for.

### Settled 2026-08-28 — the count is optional again, because its evidence retired with the model

**`Label speakers` runs without `How many speakers`, and the field's placeholder says *optional*.**
The rule above was argued from a bound — fifty minutes, past which the estimate drifted and a host
arrived as a plausible extra speaker — and that figure was measured on Sortformer. It left for
`attic/sortformer/` with the graph that produced it, and the labeller that ships declares
`ReliableUpTo` and `MaxSpeakers` null: no bound, and no cap. What outlived the model was a refusal
resting on evidence this repository no longer holds, under a hint that said in one breath that the
count was required and that the model works the number out for itself.

**The sign was backwards, which is the part worth keeping.** The count never reaches the clustering —
the pipeline reports `SupportsFixedSpeakerCount` false, exactly as Sortformer did — so it is a fold
applied to finished labels, merging whichever pair collide least. Above the true count the fold finds
nothing to merge; below it, it puts two people under one name with no margin behind the merge.
Requiring a number did not protect anybody from a silent estimate, then: it made the one destructive
direction the path of least resistance, on a clusterer with no cap forcing it.

**Blank is a decision, and still not a default of two.** Blank takes the model's own estimate, a
number still folds, and the sentence beside the field now says what each of them does. The reasoning
that kept the field from defaulting to two is untouched — a guessed default merges two genuinely
different speakers in `IS1008a`.

**The asymmetry named on 2026-08-22 closes from the other side.** `--speaker-count` was always an
optional flag that warns and runs, against the same capabilities object; the window was the half that
differed, and it is the half that moved.

**What this does not touch.** No measurement moved and no engine changed. The bound itself is not
retired: `SpeakerDurationWarning` still draws beside the queue for any labeller that declares a
`ReliableUpTo`, and the suite still drives it through fake ones — dormant rather than gone, because
nothing has been measured for the pipeline that ships. A count above a cap is still reported as
unreachable wherever a cap exists.

### Decided 2026-08-22 — the sidecar is handed float, because PCM16 moved the answer

**The WAV the host writes for the diariser sidecar is 32-bit float, not 16-bit PCM.** The sidecar's
whole claim is that its output is the Python reference's, and the one place the two paths differed
was that file: the host resamples in float and was writing `int16`, the reference reads float.
Measured 2026-08-22 on the CPU, same recording, same implementation, the PCM16 arm scored 2.50% DER
against the float arm on a 48 kHz MP3 decoded here — ten times the CUDA gap that keeps CUDA out of
`auto` — and 0.00% on 16-bit input, which is AMI and therefore every published figure, which is why
it had gone unseen. `docs/UNPROVEN.md` carries both rows and what they do not establish; the
handoff is now held to the host's samples bit for bit by a test. The cost is a temporary file twice
the size, and the published AMI figure can only move toward the reference it already matched.

### Published 2026-08-20 — the weights, and the licence check that had to come first

**The nine files are on Hugging Face, and the Apache-2.0 §4 conditions were discharged before they
went rather than after.** Uploading is redistribution, and §4 attaches its conditions to
redistribution — not to a catalogue entry existing. Two of the four had been recorded as outstanding
since the entry was written, deliberately, and closing them was the first thing done.

**§4(d) is inapplicable and §4(c) splits in two.** The upstream repository was read at the pinned
revision `bb1ef830d5` — the API's own `sha` came back as exactly the revision the export ran
against, so the listing *is* the pinned revision and not merely `main` — and then every text file in
it was fetched at that revision. There is **no `NOTICE` file**; `NOTICE`, `NOTICE.txt` and
`NOTICE.md` were each requested and each 404ed, and so did `LICENSE`, `LICENSE.txt` and `COPYING`.
There is **no copyright, patent or trademark notice anywhere** — a case-insensitive search across
all seven text files returns nothing, and the only occurrences of those words in the whole tree are
`▁Copyright`, `▁copyright`, `▁trademark` and `▁Helsinki` inside `source.spm` and `target.spm`, each
preceded by U+2581 in the protobuf framing of a `ModelProto` piece. Those are **subword vocabulary
entries, not notices**, and nothing is reproduced from them.

**But §4(c) says "copyright, patent, trademark, and attribution notices", and the attribution
notices are real.** The card carries four — the developer line and the OPUS-MT/Marian/OPUS
provenance, the original model archive's URL, a citation request naming three publications, and the
Acknowledgements paragraph crediting the HPLT project under EU Horizon Europe grant agreement
No 101070350, CSC — IT Center for Science, and the EuroHPC supercomputer LUMI. All four are now
retained rather than summarised, in `ApacheAttribution.RetainedSourceNotices`, in `NOTICE.md` —
which had no Apache section at all until today — and in the published repository's own `README.md`.
The narrow reading, that §4(c) covers only notices inside the *files* and a model card is not a file
of the Work, would have discharged this with nothing retained. It was not taken: retaining what the
source asks to travel with it costs four paragraphs, and being wrong the other way is a breach.

**The negative finding is a field rather than a silence.** `ApacheAttribution.SourceNoticeFinding`
is `required`, like every other element in that file, and states the revision read, the date, and
that there is no NOTICE file and no copyright line. A notice that omits a NOTICE file and one that
records there is none read identically to anyone downstream; only the second says the check was
performed. Two tests hold it up, and the second exists for the tempting failure specifically: it
asserts the notice invents **no** copyright line, because filling the gap with a plausible
*Copyright (c) Helsinki-NLP* that nobody upstream ever wrote is a false notice in front of a user —
the failure `models.json`'s own comment about the deferred entries refuses.

**Hugging Face rather than a GitHub release, and the reason is verification rather than
convenience.** All six other catalogue entries are HF URLs and the translation entry was the only
`github.com` one. More to the point, HF publishes an LFS object's `oid`, which for an LFS object
**is** its SHA-256 — so the entry can be pinned against what the repository publishes rather than
against what one machine happened to upload, which is the distinction `docs/MODELS.md` draws between
a verified entry and a trusted one. That is also why the uploaded `.gitattributes` forces **all
nine** files onto LFS including the four under a kilobyte: left as ordinary git blobs those four
would carry a git SHA-1 and publish no SHA-256, and only five of nine could be checked. An entry is
as verified as its least-checked member.

**Pinned, and then installed for real.** `--verify` read the nine published oids back and every one
matched the digest the gate run recorded, so `models.json` swapped its nine placeholder URLs for
`resolve/<commit-sha>/` ones and flipped to `"verified": true` — the commit sha rather than `main`,
the way the diariser entry does it, because a URL and a digest are pinned together here. The last
exception in `ModelTests` went with it: there is no unverified entry left to excuse, so nothing is
excused, and a new one fails outright. Two other tests had quietly become claims about the data
rather than about the flag they were named for — one asserting `models list` shows exactly one
unverified line, one asserting the Models tab paints at least one — and both were rewritten to test
the flag against a catalogue built for the purpose, which is where a flag belongs.

**Then the thing none of it had ever done: an install.** `models download` assembled the nine files
in an `opus-mt-tc-bible-big-mul-en.part` staging folder, fetched, sized and hashed each, renamed the
folder into place and printed all nine digests; `models verify` re-hashed them off disk and reported
**9 of 9 files match**; and `uindosill translate` then loaded the ONNX graphs **out of that assembled
directory** with no `--translate-model-path` and translated 347 lines at 0.467 s each. Before today
every figure in `docs/UNPROVEN.md` reached this checkpoint through a directory the export script left
behind. The staging-directory install, the per-file pins and the all-or-nothing rename are experience
now rather than tests against a stub. **Interruption is still only tests** — the one install that has
ever run ran to completion, so resume-per-file has not been exercised on anything real.

### Built 2026-08-20 — the two halves of the number problem

**The cascade fails in a way neither model fails on its own, and until today the whole of the
evidence was one sentence.** The recogniser writes numbers the way they are said, so a German
speaker's 1929 arrives as `neunzehnhundertneunundzwanzig`, and the translator — whose training
corpus is Bible-derived text where numbers are digits — returned *"the nineteenth century"*. Neither
component is wrong on its own metric. What is built here is the repair and the alarm: one narrow
rewrite in front of the translator, and one language-independent check behind it.

**The rewrite is `GermanNumberWords`, and its whole safety argument is the word "compound".** A
token is turned into digits only when it parses *completely* as a German cardinal **and** is built
from at least two number words. `zwei`, `zwanzig`, `neunzehn` and `hundert` are single lexical items
the translator handles perfectly well and are left exactly as they are; `einundzwanzig`,
`zweihundert` and `neunzehnhundertneunundzwanzig` are compositions and are rewritten. Requiring the
*whole* token to parse is what keeps ordinary words out — `Achtung` parses `acht` and has `ung` left
over, `Dreieck` has `eck`, `Zweifel` has `fel` — and the two-word floor is what keeps the small
numbers that already translate correctly out. Bare `ein` is in the grammar and `eine`, `einer`,
`einem` are not, because those are the indefinite article.

**It runs without being told the source language, and that had to be earned rather than assumed.**
Nothing in this pipeline knows the source language: the translator is many-to-one, told the target
and never the source. So the rewrite runs on every segment in every language, and the question is
whether it ever fires on something that is not a German compound number. **It does not.** Over all
25 FLEURS `test` configs — **20,146 rows, 8,499 distinct sentences, every language the catalogue
claims, English included** — it changed **nothing**. (8,499 is the gate's 8,149 plus English's own
350, which is the arithmetic that says the same corpus was read.) That check is
`GermanNumberWordsTests.ItChangesNothingInFleursWrittenText`, opt-in behind `UINDOSILL_FLEURS_DIR`
because it reads a corpus this repository does not carry, and it is re-runnable rather than
performed once and written down.

**That check is not a nicety, it is the condition on shipping the rewrite at all.** The translation
gate was scored on FLEURS `raw_transcription` — written prose, where numbers are already digits. If
the rewrite changed any of that text, the sentences the shipping path sends the translator would no
longer be the sentences the published chrF++ figures describe, and every one of those figures would
have to be re-earned. It is a no-op there and fires only on recogniser output, which is exactly the
split that makes it free.

**It lives in `TranslationRequest.Mark`**, the one funnel every source string passes through, for
the same reason the `>>eng<<` target token does: something a translator implementation is trusted to
remember is something a translator implementation will one day forget. Context segments are
normalised the same way, since context is text the model reads too.

**The alarm is `TranslationNumerals`, and it needs no per-language grammar at all.** If the source
carries a numeral and the English does not, say so — which works for all twenty-four sources,
including the twenty-three nobody here has ever put audio through. Two decisions keep it from being
noise. The English side is first put through `TranscriptNormalizer`'s word-error-rate tokeniser,
whose number rule already turns runs of English cardinal words into digits, so a translation
rendering `12` as *twelve* is not a lost number; without that, every small number in every
transcript would be flagged and the one that mattered would be buried. And separators are dropped on
both sides, because German `1.000` against English `1,000`, and German `3,2` against English `3.2`,
are the one difference that reliably occurs and never carries meaning. The cost is that `3.2` and
`32` cannot be told apart, which is the right trade for something whose job is to point a human at a
segment rather than to score one.

**And the rewrite is measured to help, which it was not when it was written.** The cascade run above
translates in Python and so does *not* apply it; putting the same 347 recognised German sentences
through `uindosill translate` produces the shipping output, and
`scripts/measure-cascade.py --compare-normaliser` diffs the two. **chrF++ moves +0.15, 59.30 to
59.45 — and that understates it by design**, because a corpus metric cannot see a number error.
**Numeral recall can**: of the numbers the English reference carries, how many survive as digits.
Over all 347 sentences, **46 of 105 → 62 of 105**. Over the **17 sentences the rewrite changed**,
**2 of 29 → 18 of 29**. All 17 carry a German compound number token, which is what attributes the
difference to the rewrite rather than to the port. And the founding failure was caught again in the
wild and repaired: `im Jahr achtzehnhundertneunundachtzig` went from *"in the eighteenth century"* to
*"in 1889"*, against a reference that says *"in 1889"*.

**It is a flag, not a refusal, and it is one-directional.** A number the English *added* is not
reported: invention is a different defect from loss, it has not been observed here, and a rule
written for it would be a rule nothing calibrated. `transcribe --translate` reports it beside the
speaker-cap note; `uindosill translate` reports it by line number rather than by timestamp, because
that command's segments are one synthetic second apiece and `[00:03]` would be a time that never
existed.

### Decided 2026-08-20 — one transcription entry, unquantised

**The quantised ladder is withdrawn from the catalogue. `tdt-0.6b-v3-f16` is the only transcription
entry the product offers, and there is nothing below it.** Until today the catalogue shipped f16
and four quantisations of it — q8_0, q6_k, q5_k and q4_k — and the Models tab presented all five as
a ladder to choose from. It now presents one.

**This is not a quality finding, and saying so is the point.** The ladder was measured, and the
measurement is why the decision is cheap: over eleven hours of accented English earnings calls,
every quantisation scored within 0.08 points of f16's 10.21% word error rate, and the smallest of
them differed from f16 on 2.69% of tokens while being neither more nor less wrong. Nothing
discovered about those files made them unfit. `docs/UNPROVEN.md` keeps that ladder in full,
unedited, because it is a dated record of a real measurement on real files — the files still exist
in the upstream repository and the digests still pin them. What changed is what this product
*offers*, not what was true about them.

**The argument is a product one.** A ladder asks a user to trade download size against accuracy at
the moment they are least able to evaluate the trade — before they have transcribed anything — and
the honest answer the catalogue itself gave was the same entry every time, since f16 was the
recommended entry throughout and the four alternatives saved between 500 MB and 766 MB for a
difference no measurement here could distinguish from noise. Offering the choice made the window
look like a decision was required when the project's own evidence said it was not.

**What it costs, stated rather than waved away.** The floor for transcription is now a 1.34 GiB
download with no smaller option behind it, which is worse for a user on a metered connection or a
small disk, and the CPU decode is slower than q8_0's. That is the whole of the cost and it is
accepted deliberately. Nobody should reverse it by adding an entry back to `models.json` without
reversing this paragraph first — a catalogue that quietly regrows a ladder is how the Models tab
became a menu the first time.

**Three entries remain and all three are unquantised**: the f16 transcription weights at 1.342 GiB,
the Sortformer diariser at 453 MiB and the OPUS-MT translator at 1.337 GiB, 3.121 GiB together
against the 4.295 GiB the ladder alone occupied on the machine the uninstall hazard was checked on.
Two things that quoted the old figures were corrected the same day rather than left to drift: the
CC BY modification notice in `Licensing/Attribution.cs`, which declared quantisations this build no
longer distributes and would have put a notice about somebody else's files in front of a user, and
the Models tab's own line about what an uninstall leaves behind.

**The deferred digests are untouched and that is not a contradiction.** `models.json` records
name, size and SHA-256 for ten files in the same upstream repository, several of them quantised,
and the block's own comment already says what they are: not catalogue entries, not installable and
not selectable by this build, recorded so a later version does not have to re-derive them. A
recorded digest is not an offer.

### Built 2026-08-20 — the window on a real screen, and the three defects only that could find

The interface was ported, rendered headlessly and checked pixel by pixel against its palette
before anything was run. All of that held. Then the built application was opened on Windows 11,
and it found three things a headless render is structurally incapable of showing, because a
headless render has no window frame to draw.

**The toolkit's own title bar was drawn on top of the design's.** `ExtendClientAreaToDecorationsHint`
extends the client area but does not remove the caption: the window carried `WS_CAPTION`, and both
the title text and a second set of minimise, maximise and close buttons arrived over the headerbar
— two copies of the application name, one of them in Segoe UI.

The fix is the mechanism Avalonia 12 documents for this and it is not the one that was reached for
first. `SystemDecorations` is not it. Neither is styling a `PART_TitleBar` template part, which
matches nothing — and neither does `HasTitleBar`, which belongs to the chrome control rather than
to `Window`. What works is **`WindowDecorations="None"`** together with the
**`WindowDecorationProperties.ElementRole`** attached property: the headerbar declares itself
`TitleBar` and each window button declares its role. That also deletes code, because the platform
then owns dragging and double-click-to-maximise, where the first attempt had a `PointerPressed`
handler calling `BeginMoveDrag` — fewer lines and better behaved, since the OS also knows about
snapping.

**A hint ran off the edge of the window.** The speaker opt-in's explanation — the reason the
control is disabled rather than hidden, which is the whole argument for disabling it — was cut at
"it is a 453 MiB dow". A horizontal `StackPanel` measures its children with infinite width along
the stacking axis, so `TextWrapping` never engages inside one. It is a `DockPanel` now.

**And the corner is square.** `DwmGetWindowAttribute` reads the preference back as
`DWMWCP_DONOTROUND`, and the window draws square on screen. Per-monitor DPI is not a problem
either — the display reports 240 DPI, which is 250% scaling, and the layout scales cleanly.
`docs/UNPROVEN.md` has what that settles and the three things it does not: snap layouts, the other
scaling factors, and the shadow.

**The lesson is about the method rather than the bugs.** Every one of these was invisible to the
measuring loop that caught the earlier defects, and the palette audit and the headless renders were
not wrong — they were answering a different question. A window has to be opened.

### Built 2026-08-20 — the frame, and the decoration value that quietly removed resizing

Turning the toolkit's title bar off has three settings and only one of them is right, which is
worth writing down because two of them look right until the window is in front of you.

`WindowDecorations="None"` removes the title bar, and it removes the whole frame with it. That
takes `WS_THICKFRAME` — so **the window silently stopped being resizable** — and it takes the
compositor's shadow, so on a light desktop the application ended wherever its white pixels stopped
and there was no delimiter at all. Neither is visible in a screenshot of the window, because both
are properties of what surrounds it: the styles have to be read off the handle, and the edge has to
be looked at on a real screen.

**`WindowDecorations="BorderOnly"` is the one to use.** No title bar, `WS_THICKFRAME` intact, the
frame and its shadow intact, and the square corner preference still honoured — all four confirmed
on Windows 11 on 2026-08-20. `ExtendClientAreaToDecorationsHint` stays on beside it, and the
headerbar keeps its `WindowDecorationProperties.ElementRole="TitleBar"`, which is what moves the
window and what makes double-click-to-maximise work.

The design also asks for a 1px edge, `rgba(23,38,15,.08)`, and it is drawn inside the window now
because with the frame gone there was nothing outside it to draw on. It is kept in alpha rather
than flattened to a grey: that pixel is the boundary between the application and the desktop, so it
has to darken a light background and a dark one, where a fixed light grey vanishes against white.
It is deliberately not one of the two rules — those divide things inside the window and are opaque.

What is **not** reproduced is the design's four-layer shadow. DWM draws its own and takes no stack,
so the token sheet's three-line shadow describes the browser mock-up rather than this window;
`docs/UNPROVEN.md` says so instead of quietly implying the design shipped.

### Decided 2026-08-21 — the diariser and the translator move to a bundled Python

Both are ONNX Runtime models with a great deal of code around them, and between them
`Parakeet.Engine.Sortformer` and `Parakeet.Engine.Marian` were **5,242 lines of C# reimplementing
what NVIDIA and HuggingFace already ship** — 7,400 with the 2,158 lines of tests that existed to
hold the reimplementation to the thing it was reimplementing: an arrival-order speaker cache, a mel
featurizer, a SentencePiece processor, a Marian tokenizer, a beam search and two ONNX decoder loops.
Every one of those is a second place for a measured number to drift from the thing that produced it,
and the diariser's port had already cost 0.0044 DER points to say so. It was also **12% slower** than
the Python spike it was ported from.

So they run in a bundled Python instead, and the C# goes to `attic/` — see that directory's README
for what it carried and the commit where it last built.

**Sidecar, not embedded.** One long-lived child process per run, JSON lines over stdin and stdout,
audio handed over as a 16 kHz mono WAV by path. The models load once per batch rather than per file.
The interpreter ships inside the installer, so a user installs nothing — which is why `README.md`'s
"no cloud, no Python, no account" became "no Python you have to install" rather than staying true by
accident.

**The policy did not move with the engines**, and that was the point of drawing the boundary where
it is. The `>>eng<<` target token, the limit a source is refused against rather than truncated at,
the refusal of the word-timed subtitle format, the speaker count folded down afterwards, and the
warnings owed before a run are all still C#. The sidecar does the two things only a model can do —
count a string's tokens and translate it, or turn a WAV into speaker turns — and is told nothing
about what either means. That is the same division `ISpeakerLabeller` already drew in process, kept
deliberately, so that crossing a process boundary did not also move the decisions.

**WebGPU is the GPU provider for both, and not because it is fastest.** Measured on AMI test, 16
meetings, 9.062 h, collar 0 with overlap:

| provider | DER | vs cpu | realtime |
|---|---|---|---|
| cpu | 16.3324% | — | 70.2x |
| **webgpu** | **16.3319%** | **−0.0005** | **593.7x** |
| cuda | 16.1021% | −0.2303 | 971.7x |
| directml, ONNX Runtime's defaults | **53.1522%** | +36.82 | 945.6x |
| directml, `ORT_DISABLE_ALL` | 16.3319% | −0.0005 | 619.0x |

CUDA is 1.6x faster than WebGPU and moves the number the gate is written in. A provider that
reproduces the CPU's answer lets **one** published figure describe every machine; one that does not
means the figure describes whoever measured it. CUDA also costs about 1.65 GB of CUDA and cuDNN
libraries in the installer. **So WebGPU, and the 1.6x of speed is the price** — not the 0.2303
points, which are CUDA's distance from the CPU and not an improvement this project will read as one:
the 2026-08-20 entry establishes that the apparent gain is one meeting, with the ordering reversed
over the other fifteen.

The translator agrees, on 32 FLEURS `es_419` sentences at beam 6 with IO binding off — a floor
rather than a ceiling, since binding needs a torch device the CPU-only bundle will not have:

| provider | output against cpu | speed |
|---|---|---|
| cpu | — | 0.595 s/sentence |
| **webgpu** | **32/32 string-identical** | **0.459 s/sentence (1.30x)** |
| cuda | 240/240 identical | 1.2–1.5x |
| directml | **0/32** — repetition-loop collapse | 21.5x *slower* |

**DirectML is refused by name in both engines, for two different reasons.** For the diariser, at
optimisation level `BASIC` or above it fuses the whole graph into one node whose head output differs
from the CPU by up to **0.796** on a probability, with 2.997% of frame decisions flipping on the
first chunk with an empty cache; metacommands off, dynamic fusion off and seven named ORT passes
disabled individually all reproduced it exactly, and only `ORT_DISABLE_ALL` moves it. For the
translator there is no such lever: the encoder and the decoder are each clean on DirectML at full
optimisation when driven directly, so the collapse is in `optimum`'s merged KV-cache path rather
than in the provider. Both remain reachable behind a `-unverified` flag, so that measuring DirectML
stays possible while using it by accident does not.

**A provider can be catastrophically wrong and look healthy**, which is the finding that shaped
everything else here. 53% DER arrived with plausible RTTMs, a clean exit and a 13x speed-up. So both
engines carry a committed parity fixture and check it before any non-CPU run. The diariser's
compares probabilities over synthetic mel from a seed — no audio, no licence, 12 KB — against a
threshold of 1e-4 that is measured rather than chosen: a faithful provider lands near 1e-06 and a
diverging one near 1e-03. **The translator's is weaker and says so**: six committed sentences
compared by string equality, with no margin at all, catching the failure that has actually been seen
and nothing subtler. Neither replaces a corpus score.

**What this does not establish.** No AMD GPU has run any of it, and DirectML's defect was
driver-mediated, so "faithful on one RTX 5080" does not transfer. No Apple platform has been
attempted. The 8,149-sentence translation gate **was** re-run against the sidecar translator on
2026-08-21 and reproduced all 8,149 recorded hypotheses exactly, but on **one machine** — a second
core count computes slightly different logits and none has been tried. And no installer has been
packed with a bundle inside it, so nothing is known about what it does to a download, to a delta
package or to SmartScreen. `docs/UNPROVEN.md` carries each of these.

**Built 2026-08-21, and measured: the bundle is 1.20 GB, not the 0.55 GB it was budgeted at.**
`scripts/bundle-python.ps1` assembles it — the pinned embeddable CPython 3.12.10 with its SHA-256
checked before unpacking, the pins in `python/requirements-bundle.txt` installed from the host's pip
with `--target` so nothing pip-shaped ends up in a user's directory, and the `uindosill_engines`
source beside them — and it verifies itself by starting the result and completing the handshake over
the real protocol. Driven end to end on 2026-08-21 the assembled bundle loaded the real translator on
WebGPU in 5.5 s, passed its parity fixture 6 of 6, and translated a sentence in 0.15 s. 43,760 files.

The estimate missed the transitive set, and where the gap goes is measured: about **330 MB** is
`librosa` and what it drags in — numba, llvmlite, scipy, scikit-learn, soxr, pooch — for exactly one
call, `librosa.filters.mel`, which builds the mel filterbank matrix. It is paid because
`diariser/feats.py` is the spike's file carried across byte for byte and that fidelity is what makes
16.3324% describe this code; replacing the call with a committed filterbank is a different artefact
needing its own measurement. About 95 MB more is `sympy` and `networkx`, which are torch's.

**That measurement was made on 2026-08-26 and the librosa half of this paragraph is now history**:
the filterbank is committed, the features are bit-identical, and librosa is gone along with `soxr`,
numba, llvmlite, pooch and audioread. The entry dated that day below has it.

**The CLI zip does not carry it, and as of 2026-08-21 the bundle is its own download.** The
installer bundles the interpreter into the desktop application's publish, where `PythonRuntime`
looks for it; the CLI ships as a separate ~250 MB zip (decision 3, 2026-08-16 — Velopack has no PATH
feature). As it stood `uindosill diarise` and `uindosill transcribe --translate` from that zip
refused with "the bundled Python is not at …", and the only way through was `UINDOSILL_PYTHON`,
which is a developer override rather than an answer.

**Decided: a third release artefact, and the CLI is pointed at it.** The bundle ships beside the
installer and the zip as its own ~1.2 GB download, and a CLI that has been given one uses it. The
two rejected options are why: putting it *in* the zip charges every CLI user 1.2 GB for two opt-ins
most of them will never run, taking the artefact from ~250 MB to about 1.45 GB; and a README saying
"install the desktop app first" ships a command line that refuses two of its own documented commands.

**What it costs is a third thing to publish, pin and document.**

**Built the same day, and the discovery order is the decision inside the decision.**
`PythonRuntime` looks in three places and stops at the first whole bundle: `UINDOSILL_PYTHON` —
which now takes a bundle *directory* as well as an interpreter file, because a bundle is one thing
and pointing at it should not need two variables — then `<app>/python`, then `python` under
`%LOCALAPPDATA%\Uindosill`. That third path is where the download is meant to be unpacked, and it is
chosen rather than invented: the model weights are already there, so a user who has found one
directory has found both. **The application's own bundle wins over a downloaded one** because the
two are pinned together and only one of them was tested. `scripts/package-windows.ps1` packs the
bundle it already assembles into `uindosill-python-win-x64.zip` with `python/` at its root — so
unpacking it into that directory is the whole install instruction — and reads it back for an
interpreter and a package before it will pass. `release.yml` uploads it and **refuses a release
without it**, alongside the four that decide installability.

**Which of the three answered is now carried on the resolution rather than inferred**, which is what
makes `UINDOSILL_PYTHON` a shipping mechanism rather than a development override, and is the first
reason this project has had to report *which* interpreter a run used. **`uindosill doctor` prints
it**: the interpreter, the package root, and which of the three answered — or, when none did, the
reason with both paths in it. Until 2026-08-21 `PythonRuntime.Resolution.Overridden` was computed
and unit-tested with **no production caller at all**, which is why that day's agreement run has its
interpreter written into its prose by hand rather than recorded by the run that used it. A
measurement taken from here on can be asked instead.

**Unrun, and that is the whole of what is unproven here.** The code is built, tested and
parse-checked; **`package-windows.ps1` has not been executed with these changes**, because no
installer has ever been packed with a bundle in it. So install time, cold start, SmartScreen against
an unsigned 1.2 GB, and what a Velopack delta package does against 43,760 mostly-unchanging files
are all exactly as unknown as they were — and the release now carries a ~1.2 GB asset that CI has
never produced.

**The study is on the Drive**, in the dated folder `directml-2026-08-21` beside the other research —
the arms above with their per-meeting numbers, the raw output every table is computed from, and what
each finding does not establish. It goes there rather than here under the convention named
2026-08-16, which ends when v1.0 ships and every research folder comes back into this repository.

**What it cost besides the ports.** `SupportsCancellation` is now false for the translator: a decode
in another process cannot be interrupted, so cancelling stops the next segment being sent and the
one in flight finishes. Seven hermetic checkpoint tests went to the attic with the C# translator and
nothing in the suite replaces them. And the window's speaker limits are now a **declared** copy of
two constants that live in the sidecar — policed at load, by refusing a sidecar that disagrees, and
in CI, by reading them out of the Python source — because a warning about a four-speaker cap has to
be on screen before a batch starts and there may be no interpreter to ask.

### Fixed 2026-08-21 — the backend notice that could never fire

**The line the entry above describes was dead from the day it was written.** *Built 2026-08-20 — the
backend the application starts on* closes by saying `transcribe` had grown the notice it did not
have, because a machine carrying the CUDA drop with no working driver behind it now resolves to CUDA
automatically, falls to CPU, and runs twelve times slower with nothing on stderr to say why. The
notice was there. It was called with an engine `EngineFactory` had just constructed, and a
`ParakeetCppEngine` answers `Capabilities.Backend` with the backend it was *asked* for until
`LoadAsync` rewrites it from `ParakeetNativeLibrary.LoadedBackend`. Nothing loads at construction.
So the comparison was the requested backend against itself, the equality guard returned every time,
and the whole failure the notice exists for went on being silent — with a line of code and a
paragraph of record both asserting otherwise.

**The check now happens where the answer exists: after the load, on the first file of the batch.**
The engine loads lazily, on the first decode, so nothing knows the resolved backend until something
asks it to load; `transcribe` now asks, once, from `RunOneAsync` — after the file is known to exist,
so a queue of typos still costs no model load, and before the audio is opened, so the line is ahead
of the fifty minutes it explains rather than after them. The message and its reasoning are unchanged.
`--fake` is still excluded: the canned engine answers cpu whatever was asked for.

**It is verified against the real loader, and the test that holds it is not.** On the desktop, with
the vendored natives and the installed f16 weights, `--backend cuda` pointed at a native tree
carrying only `cpu/` prints *"cuda was requested but the native loader fell back to cpu"* and the
Vulkan hint; a bare `--backend` against the same tree prints the *chosen automatically* wording; two
files print it once; and `--backend cpu` prints nothing. None of that is reproducible in CI, which
has no natives and no weights, so the six tests in `BackendFallbackTests` hold the two halves that
are: the wording, and the timing — through a stub engine that reports one backend before its load
and another after, so a check made too early reads the requested backend back as agreement and the
suite goes red. That is the only shape of this bug CI can catch, and it is the shape it had.

### Fixed 2026-08-22 — a failed opt-in pass no longer costs the transcript, and the window loads before it decodes

**Two defects with one shape, found by an adversarial review of the v1 features.** The speaker pass
and the English pass both run after the ASR pass, and until this date a failure in either — a file
the sidecar could not read, a segment refused for its length, a child that had died — failed the
file: `RunOneAsync` and the window's `RunJobAsync` ran ASR, then labeller, then translator, then
writer, with nothing between them, and the batch runner recorded the exception as a failed job with
no output. The words were finished and unaffected; minutes of decode went unwritten. A dead sidecar
made it every remaining file, each one decoded in full and then discarded the same way, because
the diariser stages a whole file before it sends anything and the death was discovered only by the
write after the staging. The `PythonEngineException`/`PythonSidecarException` split that
`ARCHITECTURE.md` describes as what lets a caller decide was read by no caller.

**Now the transcript is the product and the pass is a decoration of it.** Both surfaces run each
pass through `OptInPass`, which returns the document as it was with the reason when the pass
throws anything but a cancellation; the file is written without speakers or without English, under
the plain name rather than the `.en` one and without the turns-only format rather than with an
empty one, and it says so — on stderr when it happens and in exit code 3 on the command line, in
the row's status ("Done — 2 files, without speaker labels") and warning in the window, and in the
window's summary so "Finished 3 files" cannot be read as three files with speakers.
`PythonSidecar` records its first failure — a child that exited, a write that failed, a handshake
that was refused — and refuses every later request at once with that reason, and both sidecar
engines ask it at `LoadAsync`, which every pass goes through, so a file after the death is refused
before it is decoded and staged. The child is not restarted: a failure of the sidecar is still
every remaining file, by design; what changed is that each of them learns it in milliseconds.

**The window also loads both engines before the first file, which the command line already did.**
`LabellerFactory` and `TranslatorFactory` load eagerly for a reason their remarks state — a
bundled Python that will not start or a checkpoint that will not load is worth finding out before a
three-hour decode — and `TranscribeViewModel.StartAsync` created the engines and let the first
file's pass load them, so the same failure arrived after that file's full decode, and again after
every other file's. It now loads in `StartAsync`, inside the try, so the failure is a sentence in
the status bar with every row still waiting. A second `StartAsync` on a sidecar whose handshake
failed also no longer returns as though it had succeeded — it was keyed on "a process exists" —
which was how a protocol-mismatched bundle refused on one file could have answered the next.

Fifteen tests hold it: the helper's three rules and the output reduction in `OptInPassTests`,
the sidecar's memory of its death and a loaded labeller refusing the next file unread in
`SidecarFaultTests`, the two fallbacks, a pass that succeeds and the exit code in the CLI's
`PassFallbackTests`, and the two load failures and two per-file failures in the window's
`OptInFailureTests`. The fakes grew `FailOnLabel` and `FailOnTranslate` so all of it runs with no
weights.

### Fixed 2026-08-22 — a format's alias walked around its guards

**`-f words --translate` wrote the word-timed file the refusal exists to prevent, and `-f .rttm`
with no `--speakers` wrote the empty `.rttm` the other refusal names.** The parser accepts several
spellings for a format — `words` and `webvtt-words` for `vtt-words`, a leading dot, any case,
`text` and `plain` for `txt` — and only the writer resolved them, at write time. The two guards on
the format list compared the spelling as typed against a canonical id, so an alias passed both;
and `-f vtt,webvtt` reached the writer as two entries and wrote one file twice, the second as
`name (2).vtt`. Reproduced with the built binary from the same review as the entry above; the
window was never affected, since its list is its own checkboxes.

**The list is canonical before anything reads it.** `TranscriptFormats.Canonical` resolves each
spelling through the registry and names each format once, in first-seen order, and `transcribe`
runs its list through it straight after validation — so the rttm guard, the word-timed refusal in
`TranslatorFactory`, the jobs and the writer all read one spelling. Nine tests: the registry's
resolution and its refusal of a spelling that names nothing, four spellings against the word-timed
refusal, two against the turns guard, and five spellings of two formats writing two files.

### Fixed 2026-08-22 — the reader died on a line it could have skipped, and descriptor 1 still led to the pipe

**Five defects at the process boundary, from the same review as the two entries above: two that
cost a run to one line, one that cost a load its reply, and two latent.** The host's reader caught
only `JsonException` around the parse. A line on stdout that was JSON but not an object — a bare
`0`, an array — threw `InvalidOperationException` from the `id` lookup, and a message whose `id`,
`completed`, `total`, `kind` or `message` had the wrong type threw from `GetInt32` or `GetString`;
the catch named neither, so the reader's loop ended, every pending request failed with "output
ended" and every later one was refused, for the rest of the run, over one line the contract in
`ARCHITECTURE.md` says is "recorded and skipped". On the other side `claim_stdout` replaced
`sys.stdout` and left file descriptor 1 where it was, so `os.write(1, …)`, `sys.__stdout__`, a C
extension's `printf` and any child process the sidecar spawned wrote into the protocol pipe —
measured on the bundled interpreter: every one of them did — and a write without a newline glued
onto the next reply, which the host then dropped as unreadable, leaving that request waiting. A
parity reply carrying `NaN` — a provider producing non-finite probabilities — went out as `NaN`,
which is not JSON, so the host dropped it and the load waited on a reply that had been sent.
`StandardInputEncoding` was never named, so the child's stdin took the console's input code page
and worked only because the request writer escapes non-ASCII. And a cancellation at the write gate
left the request's id registered with nothing to answer it, while the newline travelled in a
second write of its own, so a cancellation between the two left a request on the pipe with no
terminator.

**The envelope is the id, and everything inside it is read only when it has its type.** `Dispatch`
treats a non-object root as the stray line it is, an `id` that is not an integer as no id, a
progress report with a count that is not an integer as a line to record and skip rather than
report as zero, and an error's `kind`, `message` and `traceback` as missing when they are not
strings — and throws on nothing, so the reader outlives any one message. `claim_stdout` now
`dup2`s stderr over descriptor 1 after duplicating the pipe for the channel, so every route to
stdout other than the channel's own descriptor lands on stderr; measured again on the bundled
interpreter, the pipe carried the protocol lines and nothing else, and the shipped entry point
driven by hand answered a handshake, two errors, a non-JSON request and a shutdown with stdout
holding exactly those five replies. `Channel.send` refuses a non-finite number (`allow_nan=False`)
and answers the same id with an error that says so, and the diariser's parity check tests its
probabilities for finiteness before it subtracts, so the host gets `passed: false` with a reason
rather than an error about the reply. The host names UTF-8 for stdin as it already did for both
outputs, writes a request and its newline in one call, registers the cancellation before the write
and removes the id on any failure to reach the child.

Four tests hold the reader: a bare number, an array, a string, a non-integer id and a non-string
type on stdout ahead of the handshake with the next request still answered; a progress report
whose count is a string skipped with the result still arriving; an error with a numeric kind and
an array for a message failing its own request and not the channel; and a request cancelled before
the write surfacing as a cancellation with the channel still usable. The Python half has no test
the suite can run — CI has no interpreter, and the suite stays that way — so it was driven by hand
on the bundled CPython 3.12: the descriptor probe before and after the change, the channel with
`NaN` and `Infinity` in a reply, and the diariser check against an engine returning non-finite
probabilities. The suite is 821.

### Fixed 2026-08-22 — messages and exit codes: the `translate` stack trace, the Ctrl-C that exited 0, and three things the host was told and threw away

**Seven defects, from the same review, none of them numerical: each one a sentence that was owed
and not said, or a code that said the wrong thing.** `uindosill translate` over a line past the
tokenizer's limit threw `SegmentTooLongException` through a catch list that did not name it, so
the refusal the help promises — "refused rather than truncated, and names itself" — arrived as a
stack trace with a zero-based segment index in it. Ctrl-C during a batch marked every unfinished
file cancelled, printed "cancelled" per file, and exited 0, so a script that checked the exit code
read an interrupted run as a complete one. A parity check that crashed — the sidecar answering
`parity` with an error — was reported as null, the same null as "not run on the CPU" and "no
fixture committed", so the labels or the English went out unverified with nothing said; and the
reason the sidecar gives instead of a magnitude (a shape that does not match, probabilities that
are not finite, from the entry above) was read by nobody, so that failure printed a difference of
NaN. Under `auto`, the reasons WebGPU or CUDA did not build were kept only for the case where
every candidate failed, so a run at CPU speed on a machine with a GPU could not say why. The
window asked `PythonRuntime.TryResolve` for a reason and discarded it, telling a user whose
`UINDOSILL_PYTHON` points at nothing to reinstall; the file form of that variable never looked
beside the interpreter for the package, and then blamed a directory the interpreter was never
"pointed at"; and a packages-only override whose bundle was missing blamed a variable nobody set.
Last, `threads: 0` means "let ONNX Runtime choose" for the translator and 12 for the diariser,
and two help strings said the former of the latter.

**Every one of those is now a sentence, in the place it is owed.** `TranslateCommand` catches the
refusal itself and prints the line — counted from one, which is what the user has in front of
them — and `SegmentTooLongException` joins the entry point's catch list for every other route.
`Report` counts a cancelled file as one that did not finish: some finished is a partial failure,
none finished is a runtime error, and `ExitCodes` says so. Both parity results carry a third
state, `Ran`, and a `Reason`; each describes its own three failing shapes through one
`Describe()`, and the command line and the window print that, so "the check … could not be run:
…" and "does not reproduce the reference: 2 of 3048 probabilities are not finite" reach the user
in the sidecar's own words. Both resolvers put what `auto` passed over into the capabilities as
`fellBackFrom`, each entry with its reason, and the host prints one line — driven by hand on this
machine through a `cuda` candidate that cannot build here: `backend: cpu`, `fellBackFrom: ["cuda:
asked for CUDAExecutionProvider and onnxruntime registered ['CPUExecutionProvider'] …"]`. The
window's provider keeps the resolver's reason beside the answer and leads with it; the file form
tries the interpreter's own directory before the application's bundle and each message names what
was actually tried; the packages-only form says the interpreter is still the bundle's. The
diariser names `DEFAULT_THREADS = 12`, its options and both help strings say 12 — the number every
CPU figure was measured with — and the translator keeps ONNX Runtime's choice, the difference
stated.

Eleven tests: the over-long line refused by line number through the verb's own loop with a
translator given a limit; the cancelled batch's exit codes; the crashed check and the reason-only
failure on the diariser and the crashed check on the translator, each a result that says so; both
engines carrying what `auto` passed over; the file form with the package beside the interpreter,
the packages-only override and its message; and the window's hint carrying the resolver's reason.
The suite is 832.

### Fixed 2026-08-22 — the audio path: a gate that opened too high, a flush that trimmed the wrong segment, a cap a short fragment could walk past, and times one tick short

**Seven defects in the segmenter, the WAV reader and the resampler, from the same review.** The
adaptive gate's floor was seeded on the absolute line, so the gate opened one margin above it —
−47 dBFS — and quiet speech sat under it until a pause let the floor fall: a −45.6 dBFS tone from
the first sample produced nothing for ten seconds, and after a loud passage a −46 dBFS stretch
with no gap was never speech; neither said so, because the only sentence about the gate needed an
empty transcript. `Flush` trimmed the zero-padding it adds to the last partial frame off whichever
segment was emitted during the flush, including one the silence rule had closed short of the
padded frame, so that segment ended early and the segmented-audio figure was short by the same.
The "too short to emit" early return in the silence rule skipped the cap check, so with a minimum
segment length past the speech-plus-padding minimum — reachable through the API, set by no shipped
surface — a buffer grew to three times the cap. The WAV chunk walker stopped at any zero-size
chunk, so a valid file with an empty `JUNK` before its `data` "had no data chunk"; a `data` chunk
declaring zero with audio after it was refused while a declared `0xFFFFFFFF` was recovered; and the
resampler's flush stopped at the last input *sample* rather than the end of its period, so 8,000
samples at 8 kHz came out as 15,999 at 16 kHz. Last, every sample-indexed time on the ASR path —
segment starts and durations, the report, the WAV duration — and every parsed decimal — the native
decoder's word times, the sidecar's turns — went through `TimeSpan.FromSeconds(double)`, which
truncates to the tick: a start at sample 9,120 of a 16 kHz file is 0.57 s and printed as
`00:00:00,569` in a subtitle while the JSON said 0.57 (GOTCHAS §25, which the RTTM path alone had
fixed).

**The gate opens at the line, and what it keeps out is counted.** The floor is seeded one margin
below the absolute line; the segmenter counts every frame above the line that ended outside every
segment, `SegmentationReport` carries it with the line it was counted against and says when it is
material — a second, and a tenth of what was segmented — and the command line and the window print
"N s of audio above −55 dBFS sat below the voice-activity gate and was not decoded" with the
`--no-vad` / "fixed windows" remedy. `Flush` trims padding only from a segment whose emission
reached the buffer's end; the silence rule falls through to the cap rather than returning; the
walker advances past a zero-size chunk, reads a zero-length `data` with bytes behind it as the rest
of the file unless those bytes are a plausible chunk header, and the resampler's last output may
land anywhere before the end of the last sample's period. `AudioMath.SamplesToTime` (integer
arithmetic, exact) and `AudioMath.SecondsToTime` (rounded) replace every `FromSeconds` that carried
a measured time; `SpeakerTurns.FromSeconds` is now a name for the latter. What the seed changes on
real files is unmeasured and `UNPROVEN.md` says so: the WER figures were taken with the previous
gate, and the expected effect is confined to file starts and to quiet material after a loud
passage.

Twenty tests: quiet speech segmented from the first frame; the kept-out material counted and
material on the loud-then-quiet case and zero on an ordinary file; the flush reproduction ending
at 2.79 s with the report agreeing; the cap holding against a short fragment; five rates coming
out as sixteen thousand samples a second, chunked equal to whole, identity untouched; the zero-size
`JUNK`, the zero-length `data` with audio and the empty `data` with metadata; and times that land
on their tick — the helper at several rates, a segment's start, duration and end, the segmenter's
own start, and a decoded word's. The suite is 852.

### Fixed 2026-08-22 — output and provenance: a cue cap one path ignored, a backend the provenance guessed, two inputs that wrote one file, a write that could be cut in half, and a decoder shape read as empty

**Five defects between the transcript and the disk, from the same review.** The subtitle cue
builder enforced its 7 s cap only on the word-timed path; a segment that arrived without word
timings — every segment of a translated subtitle — was split by characters alone and timed by
share, so a 26 s cue came out of a 30 s segment under a cap the options and `ARCHITECTURE.md` both
state. The native loader recorded a library found in a flat directory as the backend that was
*requested*, and one found on the system search path as nothing, which the engine then filled in
with the request again — so a flat CPU build went into a transcript's provenance as `vulkan`, and
the fallback line had nothing to compare. Two inputs that write one stem to one place — the same
name in two folders under `-o`, or `a.wav` beside `a.mp3` — were both decoded and the second then
replaced the first under `--overwrite`, was skipped under `--skip`, or was renamed beside it; the
`translate` verb had always refused that shape. `TranscriptWriter` wrote straight to the final name
with a cancellable write, so a Ctrl-C mid-write left a truncated transcript that the Rename policy
treated as a finished one and wrote the next run beside. And `ParakeetJson` read a clip with no
string `text` as an empty clip, dropping the segment and its words with nothing said, and read a
word with no numeric time as a word at zero, stacking each one at the segment's head as a 700 ms
cue.

**Each is now what it should have been.** The no-word path tightens its character capacity until
no chunk's share of the segment is past the cap, a word being the unit. `EngineCapabilities.Backend`
is nullable, the loader records a flat-directory or search-path load as unknown, the engine takes
the loader's answer and nothing else, the transcript's provenance writes `"backend": null`, and the
command line and the Models tab say that which backend is running is not known rather than that it
fell back. `TranscriptWriter.FindOutputCollisions` groups jobs by destination stem and directory,
and `transcribe` refuses a batch with a collision by name before anything is decoded, as `translate`
does; the writer stages beside the final name and moves into place, so a write that stops leaves
nothing under the final name. A clip with no string `text` is a `ParakeetNativeException` — the
shape inside an ABI the version check already holds — and a word without a numeric time makes the
clip one with no timings, which the callers time by share; a negative or non-finite time still
clamps to zero, as it always did.

Seven tests: the no-word path under the cap; the unknown backend's sentence; the collision finder
on three shapes and the plain-beside-translated case that must not collide; the command line
refusing a collision before either file is decoded; a write leaving no staging file; the clip with
no text refused; and the word without a time emptying the clip's timings. The suite is 859.

### Fixed 2026-08-22 — the diariser's host side: a zero-length word that went the wrong way, a fold that counted twice, two figures that printed with commas, a flag whose help overstated it, and a staging that held the file twice

**Six defects around the speaker labels, from the same review, none of them in the model.** A
zero-length word — the decoder's end-before-start collapse — overlaps nothing, so inside two turns
it fell to the nearest-turn rule with a negative gap for both and went to the turn that started
earlier, the opposite of the tie-break every word around it took: B | A | B, one word under the
other name. After a fold relabelled a dropped label, the survivor's turns were coalesced only after
the last fold, so two turns of the survivor that now overlapped each other both counted their
overlap with the next label, and the next merge's evidence read 10 s where the union was 7 s.
`DescribeParityFailure` formatted its two magnitudes in a plain interpolated string, and the
`diarise` summary nested a `$"… {minutes:F1} min"` inside a hole of an invariant one — its own
interpolated string, formatted in the current culture — so on a comma-decimal machine the first read
`8,143e-04` and the second "0.0 s of speech over 0,0 min"; the translator's decode description had
the same shape in `length penalty 0,6`. `--speaker-backend-unverified`'s help said it allowed a
backend that had not passed the parity check, when it unlocks `dml` by name and nothing else, and
`ARCHITECTURE.md` said a failed check runs because "the user asked for that provider", which under
`auto` they did not. Staging a file for the diariser held it twice — the resampled samples in a
list that doubled its way up, and a second whole-file array of their bytes — so the "690 MB for
three hours" figure was half the truth. And a zero-frame WAV reaching the sidecar was an
`IndexError` in the featurizer, reported as `internal`.

**Each is now what it should be.** A zero-length word is judged by containment and takes the
crosstalk's tie-break; the fold coalesces the survivor after every relabel; the three figures are
invariant; the help says what the flag unlocks and that nothing lifts a failed check, and
`ARCHITECTURE.md` says why a failed check runs under both a named provider and `auto`;
`WavWriter.WriteFloat32` streams its samples in 16 KB blocks and the staging list is sized from
the duration, so the file is held once; and the sidecar answers a zero-frame WAV with no turns —
driven by hand on the real graph here. **Not decided here:** whether CUDA stays second in the
diariser's `auto` order, where the code and its own docstring keep it deliberately and three newer
sentences say it is out — that is the maintainer's call and is still open.

Six tests: the zero-length word in crosstalk; the fold's second merge at 7 s; the parity sentence
and the decode description under a comma-decimal culture; the `diarise` summary line likewise
(green in CI either way, red on a comma-decimal machine before the fix); and the float32 writer
round-tripping a length that is neither small nor a block multiple. The suite is 865.

### Fixed 2026-08-22 — translation: a required file the host did not name, a decode no transcript carried, chips that could disagree between panes, and an input the verb overwrote before reading it

**Four small defects in the translation path, from the same review, and one race the suite kept
tripping over.** The host's list of required checkpoint files had seven entries where the
sidecar's — the authority, since it is the one that loads them — has eight: without
`generation_config.json` the decode loads and silently loses its `bad_words_ids`, and a checkpoint
missing only that file passed the host's check to be refused by the sidecar. The decode the sidecar
reports — beam width, length cap, length penalty, early stopping — reached the `translate` verb's
stderr and nothing else, so no transcript carried the search that produced its English, where the
graphs are pinned and the search is not. The window built each pane's speaker-chip map over that
pane's non-empty segments, so a speaker whose first segment came back empty from the translator
took a different chip in the English pane. And `translate a.txt a.en.txt` wrote the first file's
English to the second input's name before reading it, because only a second destination was checked
against and never an input. Beside those, a row's status could read "Transcribing 00:00:03" under a
state of Completed: a progress report delivered on a pool thread — which only a host without a
synchronisation context does, and the test host is one — read "not finished" before `Complete` ran
and wrote its status after it, and the suite lost a run to it three times in a day.

**The host's list is the sidecar's; the decode is provenance; the chips come from one map; the
input is refused; the row is gated.** `TranslatorCapabilities.DecodeDescription` carries the
sidecar's phrase, the driver writes it as `TranscriptDocument.TranslationDecode`, and the JSON's
`translationDecode` and the Markdown's "Translation decode" row print it beside the model and the
backend. `JobViewModel.ChipMap` is built once, from the spoken document over every segment, and
both panes read it. The verb refuses a destination that is also an input, by name, before anything
is written. `JobViewModel.Apply` and `Complete` serialise on one gate, so a late report cannot land
inside a completion.

Three new tests — the two formatters writing the decode, the chip map agreeing across panes when a
segment comes back empty, and the verb refusing an input-as-destination through the real entry
point — and two existing ones extended: the required-file message now naming
`generation_config.json`, and the driver's provenance assertion carrying the fake's decode
description. The suite is 868.

### Fixed 2026-08-22 — the number every document called "decode time" was the whole pass, and now the model's own time travels beside it

**One defect, in a measured number.** `TranscriptionRunner`'s stopwatch wraps `TranscribeAsync` end
to end, and inside that stretch the container is decoded through Media Foundation, the audio mixed
down and resampled, and the segmenter run — block by block, serialised with the model, because the
read of a block and the decode of the batch before it never overlap. `TranscriptDocument.ProcessingTime`
called that "wall-clock time spent decoding", `UNPROVEN.md`'s tables called it "decode time", and
`measure-transcribe.ps1` printed it under that label. With the canned engine, which decodes nothing,
a 600 s AAC file costs 1.77 s of it on this laptop: nothing against 49 s of CPU decode, and most of
the desktop's 3.86 s on CUDA.

**Two figures now, each saying what it contains.** `processingSec` keeps its meaning — the whole
pass, and what every published real-time factor is — so nothing already recorded stops being
comparable; `SegmentingTranscriptionEngine` times its decode calls alone and the document carries
that as `DecodeTime`, written as `decodeSec` and `decodeRealTimeFactor` in the JSON and as a
"Decode real-time factor" row in the Markdown, null or absent when an engine does not time itself.
The harness prints both under honest labels; the README says what its RTFs contain. Re-timed on this
laptop the same day on `sample.m4a`: CPU 77.3 s pipeline against 74.78 s decode (3.3 % outside the
model), Vulkan 24.78 s against 23.14 s (6.6 %). **The desktop's CUDA figure is owed a re-timing with
the read separated**, and `UNPROVEN.md` says so where the figure is.

**Re-timed on the desktop 2026-08-22.** Not on `chunk.m4a`, which no longer exists, but on
`csb384-8438.m4a` — the 600.0 s cut of the same episode the bf16 experiment on that machine used —
`tdt-0.6b-v3-f16`, one fresh process per run, a warm-up each on CUDA and Vulkan, then CUDA and Vulkan
alternated five times and the CPU three: **CUDA 3.95 s pipeline against 2.59 s in the model, 34.4 %
outside it (RTF 0.0066 / 0.0043); Vulkan 6.90 s against 5.29 s, 23.3 % (0.0115 / 0.0088); CPU
47.18 s against 45.41 s, 3.8 % (0.0786 / 0.0757)** — ranges across runs 10.8, 4.6 and 1.9 % on the
pipeline figure. So on a fast GPU about a third of the number every document called decode time was
the read. The split is in `UNPROVEN.md` under the desktop table, beside the 3.86 s it could not
split after the fact; the laptop's figures above stay as they are.

Three tests: a source slow to read and an engine that decodes in no time, so the wall figure carries
the read and the decode figure does not; an engine slow to decode, so the decode figure is most of
the pass; and both formatters writing the new figure beside the old, with the old unchanged.
The suite is 871.

### Fixed 2026-08-22 — the diariser's chunk loop trimmed to the wrong width and lost frames, and its featurizer's peak was never the mel

**Two defects in the sidecar's numerics, from the same review — the first in the loop the 16.33 %
was produced by.** The loop trimmed the graph's 381-frame embedding output to the pre-encode length
of the chunk's *valid* frames, the `elen` the graph reports, where NeMo's `streaming_update_async`
takes a chunk's capacity from the tensor's physical width and clamps the valid length to it. The two
differ on every file, because the featurizer pads the mel to a multiple of 16 and the STFT is one
frame longer than the valid count: verified on the installed graph, a 2736-frame piece with 2720
valid came back as 338 rows where 340 are due, and a 600.0 s file as 7,498 rows where 7,500 are
due, its last chunk's rows concatenated 160 ms early — one or two frames lost on 7.3 % of durations.
The context-only last chunk also broke out of the loop before the progress step, so the bar stopped
at n − 1 of n. And the featurizer's peak working set, which nothing had profiled, was about 730 kB
per second of audio where the mel is 51: thirty minutes peaked 1,317 MB above resting, the complex
spectrum and every intermediate behind it alive together.

**The loop trims to the physical width; the featurizer works in blocks; the figure is marked.**
`pre_encode_len` — ⌊(n − 1)/2⌋ + 1 three times, checked against `elen` on the graph for every
length the loop produces — gives the trim, the valid length stays as `chunk_lengths`, and the step is
counted before the break: 340 of 340 and 7,500 of 7,500 on the graph, and the committed parity
fixture unchanged, because its geometry has no padding to trim. The STFT runs in hop-aligned blocks
of 8,192 frames, each seeing exactly the samples its frames would have seen, and the mel is written
straight into its final layout: bit-identical to the whole-file result on thirty minutes of real
audio, at 551 MB above resting. **The AMI re-score the fix owed was taken on the desktop the same
evening: 16.3324 % on the CPU, unchanged to four decimals, and every other row of the provider
table with it** — webgpu 16.3319 %, cuda 16.1021 %, cpu on the 1.24.4 build 16.3347 %, DirectML
unfused 16.3319 % — through `uindosill diarise` per meeting and `measure-der.ps1`, with the
2026-08-21 RTTMs byte-identical where they survive. Unchanged by arithmetic rather than by chance:
none of the 16 test durations is one of the 7.3 %, so the fixed loop produces the same rows on
them, and the re-score therefore does not measure the fix where it bites. `docs/UNPROVEN.md`
carries the table, the per-file check and that caveat, and `ARCHITECTURE.md` says what the loop
trims and what the featurizer costs. The suite cannot run the
Python, so all of it was driven by hand on the bundled interpreter and the real graph, before and
after, and the numbers above are those runs.

### Fixed 2026-08-22 — a host that died left the sidecar and its staged file behind, and a cancel-then-close paid five seconds for nothing

**Three gaps in the sidecar's lifetime, from the same review, none of them on a path a finished
run takes.** A host that ended without reaching `DisposeAsync` — killed from Task Manager, crashed,
stopped in a debugger — left the child running with its weights resident, reading a stdin nobody
would write to again, and the WAV staged for the file it was labelling beside it, which nothing
ever swept. And a close after a cancellation paid the full five-second shutdown grace every time:
the child was mid-way through the label nobody wanted any more and could not read the shutdown
line until it finished, so the graceful ask bought nothing and the kill came after the wait.

**The operating system holds the child, the sweep takes the file, and a busy child is killed
rather than asked.** On Windows every sidecar is put in a job object with kill-on-close, which the
OS closes when this process ends however it ends; `PythonSidecar.InKillOnCloseJob` says whether
that happened, and says false off Windows rather than pretending. `SidecarSpeakerLabeller` sweeps
`uindosill-diarise-*.wav` files older than an hour from the temporary directory once per process,
before the first load — old enough that no live run can own them. And the sidecar counts requests
cancelled after they reached the child and not yet answered; a dispose with one outstanding kills
at once.

Three tests: a dispose after a cancel in flight finishing in under three seconds against a child
scripted to sleep twenty; the child in the kill-on-close job on Windows and said not to be
elsewhere; and the sweep taking the stale staged file and leaving the fresh one and the stranger.
The suite is 874.

### Decided 2026-08-22 — CUDA is out of the diariser's `auto`

**The code kept CUDA second in `auto` and three newer sentences said it was out; the sentences
were right about what this project's rule requires, and the code now agrees.** `AUTO_ORDER` was
`["webgpu", "cuda"]`, with a docstring defending it: CUDA is tried only after WebGPU fails to build,
a run that lands there is warned twice, and 70× realtime where 971× was available is a cost. But
CUDA fails the parity fixture at 8.143e-04 against 1e-4 and moves AMI test to 16.1021 % where the
published 16.3324 % is the CPU's and WebGPU's, and `auto` is the setting that reads as "safe": the
rule this project runs on is that what it picks unasked reproduces the figure it publishes, and
CUDA does not. So `auto` is WebGPU where it builds and otherwise the CPU — the reference path, at
the CPU's speed — and CUDA is reachable by name, with the two warnings a named provider gets. The
resolver's docstring and both `--speaker-backend` help strings say so; `docs/UNPROVEN.md` and the
entry above on the float handoff, which already said CUDA was out, now describe the code.

### Built 2026-08-22 — the Ask tab, playing before asking

**v2's tab exists, two thirds of it works, and the third that does not says so.** The Ask tab
carries a recording with a transport, its transcript beside it as cues you click to jump there, a
find box, and a chat panel that is drawn, disabled and covered by a notice. Nothing in it is a
language model, and that ordering is the one `docs/V2-ASK-THE-TRANSCRIPT.md` asks for: *"a
transcript you can click to hear is useful before any model is involved."*

**This application had no audio playback at all before it.** `Parakeet.Audio` decodes for
transcription and sounds nothing; `scripts/preview-words-vtt.html` reads a player's clock and never
assigns to it. So the transport is new surface rather than a wrapper — `Services/IAudioPlayer.cs`,
opening a file through the same reader `AudioSources.Open` chooses (the managed WAVE reader, or
Media Foundation on Windows, sniffed from the magic bytes rather than the extension) and playing it
through WASAPI. **It costs no new package**: `NAudio.Wasapi` and `NAudio.Core` are already in the
graph through `Parakeet.Audio`, which uses the first of them to decode.

**`Services/IAudioPlayer.cs` is the name it shipped under, and the file is not there now.** The
entry below dated 2026-08-23 records the interface becoming `IMediaPlayer`. What it does not record
is that the file went with it: the two implementations moved into files of their own that day,
leaving the interface and the exception together, so the player this paragraph describes is
`Services/SystemAudioPlayer.cs` today. The rename moved no logic — the reader it opens through and
the WASAPI output are the same code, and what the player gained is the video surface that entry
describes. `docs/UNPROVEN.md` § *Playing a recording* carries the whole of it.

**Taro arrived with it**, which is the condition `Theme/Tokens.axaml` had been stating since the
design landed: nothing in v1 may draw it, and a brush that exists is a brush something will
eventually use, so the ramp waited for a v2 surface to sit on. It was not picked. Each of the six
values was produced by reading the shipped matcha hex back out to oklch and re-rendering it at hue
304, so the two ramps agree at every step by construction — **every pair within 0.0014 of lightness
and 0.0010 of chroma**, which is the "identical to within 0.001" the design claims, with 8-bit sRGB
rounding as the residue. Matcha's own hue runs 126.8 to 128.6 across its ramp because hue is least
stable where chroma is lowest, so the rotation is **175.4° to 177.2°** rather than one figure —
which is why the design says "roughly 175–180" and this does too. Contrast on white was computed,
not estimated: **taro-700 is 7.48:1 and taro-600 is 5.24:1**, so both are legal as body text, and
taro-400 at 2.22:1 is an edge or a fill only.

**The window never writes a timestamp of its own.** Every time on the tab comes off a
`TranscriptSegment` unchanged — the rule that document sets for the model's citations, kept early
in the place where it is cheapest, and `TranscriptLineViewModel` now carries `Start` and `End` so
there is nothing to compute.

**The queue is shared with the Transcribe tab rather than copied.** The same `JobViewModel` rows
appear on both, so a transcript that finishes while this tab is open fills in where it stands, and
a file is playable the moment it is dropped — before it has been transcribed at all. Two
collections would have needed reconciling, and the failure mode of getting that wrong is a
transcript shown beside the wrong recording.

**Finding a word is v1 data too, so it is here.** The find box marks every line carrying the term,
Enter steps through the hits and Shift+Enter steps back, and the counter says which of how many.
**It does not seek**, which is a decision rather than an omission: somebody scanning a three-hour
transcript for every mention of a name does not want the audio jumping under them on each press of
Enter, so a hit is scrolled to and marked, and clicking it is what plays it. The term is written
only onto the lines that carry it — a search that marked every line would rebuild every paragraph
in the transcript on every keystroke, fifteen hundred of them on a three-hour recording, all but a
handful re-rendering to exactly what they already said.

**The chat panel is a deliberate exception to this window's own rule**, and the exception is
narrow. The rule is that no control ships wired to nothing; the reason for the rule is that nothing
here may fail silently. A panel that says in so many words that it is not built is the opposite of
a silent failure — what would breach the rule is a live-looking text box that swallows a question.
So the panel is drawn in the shape the feature will take, every control in it disabled, under the
panel's own ground at 82% carrying the notice. Three assertions hold it to that: nothing in it can
be operated, the notice is over it, and the notice says what is missing.

**Two things in the drawing are worth recording because both look like accidents.** The seek bar is
a `ProgressBar` with a transparent strip over it rather than a `Slider` — Fluent's slider is themed
through template parts whose names are its own, and this repository has already shipped a resource
override on a key that did not exist, which loads without complaint and changes nothing. What that
costs is the design's circular seek handle, which cannot be positioned without a measured width;
the bar is click-and-drag without one. And the cue's left edge is always drawn and usually
transparent, because a border that appears when a line becomes the current search hit moves the
words 3 px sideways, and stepping through hits would jog every line it touched.

**The handle arrived on 2026-08-23, and the sentence above was wrong about why it could not.** The
premise was the error, not the conclusion: no binding can measure a width, but the transparent strip
has been measuring one since the day this tab shipped — that is how a press at an x becomes a
fraction of the recording — and the handle is the same arithmetic run backwards from the position.
What the `ProgressBar` still costs, and what the handle does not buy back, is **seeking from the
keyboard**: a `Slider` would have arrow keys, and this has none.

**What the suite cannot reach, and what was done about it.** `SystemAudioPlayer` needs a Windows
audio endpoint, which neither CI nor a headless run has. Everything the tab does — open, play, seek
from a cue, follow the position, find a word, stop at the end — is exercised against
`FakeAudioPlayer` — renamed `FakeMediaPlayer` on 2026-08-23 — whose clock moves only when it is
told to, so what the suite leaves untested is the device rather than the behaviour. **So the
device was driven by hand the same day**, on the laptop, against three files covering both reader
branches: an m4a and an mp3 through Media Foundation and a WAVE tone through the managed reader. On
all three the endpoint opens, the clock advances at real time, pause holds it, a seek lands exactly
and resumes, a seek while playing lands and carries on, and play at the end starts the recording
over.

**That run found two defects, and both were in the same method.** Play at the end **only wrapped
when the device had stopped by itself** — the wrap sat inside the branch that creates an output,
which is reached after a recording runs out, so the common path looked right; drag the bar to the
end or pause there and the device is *paused* rather than stopped, and play resumed a reader with
nothing left to read. And the at-the-end test was **a coin toss at the boundary**: a seek to the end
lands on a frame boundary rather than on the duration, and the mp3 and the WAVE landed exactly on it
where the m4a landed **0.006 ms** short, so `>=` wrapped two of the three. The wrap is now on every
play and allows one millisecond — 48 frames at 48 kHz, inaudible, 160 times the largest gap seen.

Both were invisible from the suite for the same reason, and it is the reason worth keeping: **the
fake player was more forgiving than the real one.** It wrapped on every play and clamped exactly, so
the tests were green over a real player that did neither. A fake is a description of a contract, and
where the two drift the tests describe the fake.

What is still not established — that a person has heard the sound, a natural end-of-stream, a seek
audited by ear, and playback beside a running transcription — is in `docs/UNPROVEN.md` § *Playing a
recording* with what would settle each. Suite green at 909.

### Decided 2026-08-23 — video plays through libmpv, and the project relicenses to make it possible

**A dropped video now plays its picture as well as its sound, and the price was the project's
licence.** The Ask tab had a transport from the day before and it played audio; a video file played
its sound track and showed nothing. This closes that, and the decision worth recording is not the
player — it is what shipping the player costs.

**Three routes were compared and the choice is not the obvious one.**

*Media Foundation* was the free option and was rejected on capability rather than on effort. It is
already in the process, it adds no binary and no licence, and it plays exactly what this application
already transcribes — which is also its ceiling: HEVC, VP9 and AV1 need Store codecs the user may
not have, and it hands over a decoder rather than a player, so keeping picture on the audio clock,
seeking both together and knowing where the end is would all have been written here. `IMFMediaEngine`
would have supplied some of that with a D3D11 frame readback nobody in this repository has written
before.

*libmpv* is a finished player behind a C API of a dozen calls. It decodes whatever FFmpeg decodes,
which is everything a user is likely to drop; synchronisation, exact seeking and end-of-file are its
problem rather than ours; and the software render API hands out RGB frames that go straight into an
Avalonia bitmap with no GPU interop at all. What it costs is a 114 MB binary and a copyleft licence.

*Doing nothing* was the third, and it was live until the moment the licence question was answered:
a dropped video already transcribes and its audio already plays, so no user is blocked.

**libmpv won, and then the licence question decided the shape of everything else.** The prebuilt
Windows libmpv is **GPLv2-or-later** — it links FFmpeg-GPL and other GPL libraries — so putting it
in the installer makes the combined distribution GPL. Three ways out were on the table: build an
LGPL libmpv (`-Dgpl=false` against an LGPL FFmpeg), ship no video, or relicense. **The maintainer
chose to relicense**, and that is a decision about the whole project rather than about a tab.

The LGPL route was declined for a reason worth writing down: **no prebuilt LGPL libmpv for Windows
exists.** Checked 2026-08-23 against shinchiro's releases and the SourceForge mpv-player-windows
builds — neither publishes one, and neither says anything about licensing at all. Taking that route
means owning a cross-compilation toolchain and maintaining it, which is a standing commitment
against a one-line pin. If such a binary ever appears the GPL obligation goes away.

**What relicensing actually meant.** `LICENSE` now states two: the source stays MIT on its own
terms, and **a build that vendors libmpv is distributed under GPLv2-or-later**. Those are not in
tension — a recipient of a GPL build may take the Uindosill source under either — and the thing that
cannot be separated from the GPL is the combination. A build without libmpv contains no GPL
component and is MIT throughout, which is a real case rather than a hypothetical: the About window
lists libmpv only when it is present, so a reader can tell which kind of copy they hold by looking.
`docs/LICENSING.md` has the full reading, including the "or later" — Apache-2.0 components are
compatible with GPLv3 and not GPLv2, so the combination resolves at v3 where one is present.

**Three notices ship beside the binary and the vendoring script refuses to finish without them** —
the GPL text, mpv's own copyright summary at the pinned commit, and a written offer naming the exact
revision of everything GPL in the distribution. The upstream archive carries no licence text at all,
so all three come from `licences/` in this repository. That refusal is the same guard the
parakeet.cpp `LICENSE` check has, for the same reason: a missing notice is a breach that fails
silently.

**The engineering, briefly, because two decisions in it look like accidents.**

*The render path is the software one*, which mpv's own header calls "very slow ... single-threaded".
That is its judgement against full-rate high-resolution video on the GL path, and the case here is a
260-pixel-tall pane. `SetVideoOutputSize` tells the player how big the pane actually is, in device
pixels, so frames are rendered at the size they are shown rather than at the file's own — a 4K
recording in a 600-pixel pane is otherwise sixteen times the pixels for nothing. Measured before
being believed: **75 frames in 2.5 s of 30 fps source, rendered to 462×260** — the full rate, with
no drops.

*Seeking is `absolute+exact`, not `absolute`.* mpv's default lands on a keyframe, which on a
long-GOP file is whole seconds from the cue that was clicked — and a citation that plays the wrong
sentence is this feature failing at the only thing it is for. Exact seeking decodes forward from the
keyframe and costs milliseconds. Measured: a seek to 6.00 s landed at 6.00 s.

**`IAudioPlayer` became `IMediaPlayer`**, gaining six members: `CanDrawVideo`, `HasVideo`,
`FrameSize`, `FrameReady`, `TryCopyFrame` and `SetVideoOutputSize`. Frames do not travel through
property notifications — they arrive at the decoder's rate on the decoder's thread, and thirty a
second is not what bindings are for — so the window subscribes to the player directly and blits
into a `WriteableBitmap`, with a coalescing flag so a burst during a seek becomes one paint rather
than a queue of stale ones. Everything else on the tab still goes through the properties it always
did.

**Which player a build gets is decided by what is on disk**, the way the transcription backends are:
`MediaPlayers.ForThisBuild()` returns the mpv player when the library is vendored and the Media
Foundation one otherwise. A build without it plays a video's sound and says on the tab that it is
not drawing the picture — a stated limitation rather than a blank rectangle.

**Driven against real files, because nothing in CI can be.** On the laptop: a 12 s H.264/AAC mp4
(picture at full rate, frame copied out with 106,455 of 120,120 pixels non-black and alpha opaque,
a mis-sized destination refused), a 2:55:23 mp3 through the same player (no video track reported —
`audio-display=no` keeps cover art from becoming one — exact seek, wrap at the end), and the same
mp4 forced onto the audio-only player (sound plays, `HasVideo` false). Suite 913, still nothing
touching either real player. `docs/UNPROVEN.md` § *Playing a recording* says what that leaves open.

### Built 2026-08-23 — a link is a recording too, and the picture is streamed rather than kept

**Paste a link and the audio is downloaded, transcribed like any file, and the picture streamed
back from the same link when the Ask tab wants one.** Two pinned binaries do it — yt-dlp, and the
Deno runtime yt-dlp needs — vendored the way the natives are and documented in
`docs/NATIVE-BINARIES.md`.

**Audio only, and that decides the shape of everything else.** A transcript is made from sound, so
a link's audio track is what comes down: a three-hour video costs a few megabytes here rather than
a few gigabytes, and the file left on disk is the same shape as one the user could have dropped in
themselves. The picture is never downloaded at all — the Ask tab hands mpv the original link, which
mpv resolves through the same yt-dlp and streams. So `JobViewModel` carries a `SourceUrl` beside
its path, and the Ask tab opens one or the other: the link where the build can draw a picture, the
downloaded audio where it cannot, since streaming would then buy nothing and cost a network round
trip on every selection.

**Deno is not an optional extra and that is upstream's decision, not a preference.** yt-dlp needs a
JavaScript runtime to answer YouTube's signature challenge, and its documentation enables exactly
one by default: *"Supported runtimes are (in order of priority, from highest to lowest): deno, node,
quickjs, bun. Only `deno` is enabled by default."* A drop with yt-dlp and no Deno is a half-drop,
and the window names which half is missing rather than saying "unavailable".

**The format selector is a measured choice, not a default.** YouTube's best audio is usually Opus in
WebM, which `AudioSources.SupportedExtensions` does not list and Media Foundation cannot decode on a
stock Windows install — so a plain "best audio" download would produce a file this application then
refuses. Asking for `bestaudio[ext=m4a]` first gets AAC.

**No ffmpeg is vendored, and that was checked rather than assumed.** Without ffmpeg, yt-dlp writes
what it calls a DASH m4a and warns that *"Only some players support this container"*. Both readers
here were driven against one: Media Foundation and libmpv **both open it and report the same 9:56
duration**. So roughly 100 MB stays out of the installer on the strength of a measurement rather
than a hope.

**Two things about the process boundary are deliberate.** Arguments go through
`ProcessStartInfo.ArgumentList`, never a joined string — the URL comes from whatever was pasted, and
the list form hands each argument to the child without a shell and without quoting rules to get
wrong. And the scheme is checked before anything is spawned: yt-dlp will happily take a local path,
and `http`/`https` only is what stops one getting there. A test drives three refusals through that
gate.

**How mpv and yt-dlp find each other, since neither can be told twice.** This application spawns
yt-dlp itself and passes `--js-runtimes deno:<absolute path>`, so that is exact. mpv spawns yt-dlp
*for* streaming and cannot be handed our layout, so `BundledTools.PrependToPath()` puts the tools
directory at the front of this process's `PATH` — process-local, nothing written to the machine —
and mpv is given `ytdl_hook-ytdl_path` pointing at the pinned binary. Prepended rather than
appended, so a different yt-dlp already on the machine cannot silently take over from the pin.

**`--no-playlist`, `--ignore-config`, `--no-plugin-dirs`.** A link with a `list=` parameter is one
video to the person who pasted it; without the first flag it is however many the playlist holds. The
other two keep the run from depending on, or writing to, whatever the user has set up for their own
yt-dlp.

**Neither binary changes the licence.** yt-dlp is Unlicense and Deno is MIT, so unlike libmpv these
are permissive and the GPL question is untouched. Their notices still travel, and `vendor-tools.ps1`
refuses to finish without them.

**Driven against a live link, because nothing in CI can be.** Big Buck Bunny — Creative Commons, and
a 9:56 recording — resolved and downloaded as a **9 MB m4a in 3.6 seconds**, came back with its
title, and opened in `SystemAudioPlayer` at the correct duration. The same URL streamed through mpv
with picture: 48 frames in 2.5 s after buffering, a full frame copied, a seek to 60.00 s exact. The
suite drives a fake fetcher that writes a real WAVE file, so the window's whole link path — fetch,
title, duplicate refusal, failure, the button's dead states, and which source the Ask tab opens — is
tested without a network. Suite 923.

**What this costs an installer is unmeasured and now substantial.** About 115 MB of tools on top of
libmpv's 114 MB. No packaging run has included either; `docs/UNPROVEN.md` says so.

### Decided 2026-08-23 — four defects found by using the built application, and what each one changes

**The maintainer ran the real window against real weights and reported four things.** None was
found by the suite, and the reason is the same in three of the four cases: every one of them is a
statement the window makes to a person, and the tests asserted the view models' data rather than
whether any view was bound to it. What follows is what each turned out to be, because two of them
were not what they looked like.

**The transcript pane was never live, and this was not a regression.** The Transcribe tab's pane
draws `JobViewModel.Lines`, which `Complete()` fills in one go when a file finishes. The streamed
transcript goes into `TranscribeViewModel.LiveTranscript`, rebuilt every 250 ms for exactly this
purpose — and `git log -S LiveTranscript -- '*.axaml'` returns **nothing in any commit**: no view
has ever bound it. So the work of streaming was done and discarded from the first commit, and a
file being transcribed showed a progress bar and nothing to read. The rows are now appended as
segments arrive, unlabelled, and `Complete()` still rebuilds them with speaker chips on.

**"Stuck on labelling speakers" is a reporting defect, not a hang — and the distinction was
measured rather than assumed.** Through the product path on this machine's WebGPU,
`csb384-8438.m4a` diarised in **25.3 s for 10 min of audio, exit 0**, and the same file through
`transcribe --speakers --speaker-count 2` — the window's exact path, including the fold the window
forces and the command line does not — finished in **37.7 s, exit 0**. What the window showed
instead was the bar the decode had left at 100%, under a status that then did not change: speaker
labelling reads and resamples the whole file a second time before the sidecar is sent anything, and
that half reported nothing at all. On a three-hour recording it is minutes long. A full bar that
does not move is the shape of a hang and was reasonably read as one.

Two changes, and the second is the one with a judgement in it. A second pass now clears the bar the
decode left full — an indeterminate bar is the honest state for work with no number yet, where a
full one is a number belonging to work already finished. And the staging half reports itself, under
`TranscriptionProgress.Detail`, as a named sub-phase rather than being folded into the labelling
figure. **The two halves are reported separately because there is no measured ratio between them**;
combining them would need a weight nobody here has measured, and a bar built on a guessed weight
lies about how far along it is. Two sweeps, each saying which it is, is the smaller dishonesty.

**The Models tab described part of its own folder.** It lists catalogue entries, and four
quantisations were withdrawn from the catalogue on 2026-08-20 (above) while staying on the disk of
everyone who had installed one — about 2.95 GiB on this machine. `uindosill models` lists them
under a heading of their own and `doctor` marks them `(sideloaded)`; the tab that manages models
would neither show nor remove them, and could not, because removal took a descriptor and there is
no descriptor for a file the catalogue does not claim. The tab now has a section for them with a
delete, `IModelStore` grew `RemoveSideloaded`, and it refuses anything a catalogue entry claims, is
not weights, or carries a path separator. Two smaller things went with it: `Refresh()` existed with
**nothing calling it**, so every fact on the tab was read once at construction and never again —
it now runs when the tab is opened and after every install or removal — and the uninstall notice
said "the three of them come to over 3 GiB", a count of catalogue entries and a total that were
both true when typed and neither of which is a fact about the reader's disk. It measures the folder
now.

**Start loads the model, and deliberately not at launch.** Start refused with "open the Models tab
and press Load", which is a second button for a decision already made by pressing the first one:
there is one transcription entry, and wanting it loaded is the only reason to press Start. The
maintainer asked for the model to load at startup instead, and was shown the consequence — a load
fixes the compute backend for the rest of the process, because a native library of one backend
cannot be swapped for another's — and **chose loading on first Start over loading at launch**. That
keeps the backend choice available until somebody actually asks for work, and keeps 1.34 GiB out of
VRAM in a session spent on the Ask tab. Nothing is loaded before a person presses something.

**A fifth thing came out of the fourth: the window never said which backend produced the speaker
labels.** Asked why WebGPU appears nowhere in the application, the answer is that it is not a
parakeet.cpp backend at all — the Models tab's list is the ASR engine's three ggml builds, and
WebGPU is an ONNX Runtime provider only the sidecar uses, resolved by `auto` with no picker by
design. But the window did not *report* it either. `SpeakerLabelling.DescribeBackend` returns a
sentence only for CUDA and DirectML, the two that do not reproduce the published figure, and
`LabellerFactory` gives the reason: a line on every run about a backend that agrees would train
people to ignore the line that matters.

**That convention was written for the command line and does not transfer.** A CLI run prints a
block of provenance the reader is already looking at; the window printed the backend the
*transcription* ran on and nothing about the labels, so a GPU diarisation and a CPU one were
indistinguishable without opening the JSON — in a product whose rule is that a figure is never
quoted without its backend. The row now carries `Speakers: <model> on <backend>` after a labelling
run, in the row's ordinary voice rather than the warning one, so the warning line keeps meaning
"something needs your attention". The existing sentences are untouched.

**The translator had the same hole and got the same line.** `DescribeTranslator` speaks only when
the parity check has a finding or when `auto` fell back, so an English run on the provider that
agrees reported nothing either. `English: <model> on <backend>` now sits beside the speakers' line.
**Two lines rather than one**, because the two passes are independent: either runs without the
other, and either can fail while the other succeeds, so a single combined string would have to be
rebuilt to say which half it was describing.

**A sixth, found by looking at the Models tab: a global panel drawn inside a per-entry pane.** The
LOADED MODEL panel is the window's one `ModelSession` — one ASR engine per window — and it sat
inside the block gated on which catalogue row is highlighted. So selecting *Speaker labelling* drew
a Backend picker and a Load button beneath it, which reads as that model's backend and is nothing of
the sort: `CanLoad` has always required a transcription entry, and the diariser resolves its own
provider inside the sidecar. It is the same misreading the WebGPU question came from, built into the
layout. The panel now sits outside the per-entry block, with the sideloaded section and the
uninstall notice — all three are about the window or the folder rather than about a row.

**Two smaller things went with it.** Load and Unload were disabled with no reason given, against
this window's own rule, stated at every checkbox in it, that a disabled control says
why; a diarisation entry is the case that made the omission visible, and `LoadHint` now names what
the panel loads and where that model is used instead. And `LoadedSummary` still said "Choose a model
and press Load before transcribing" — **a sentence this session's own change had falsified hours
earlier**, since Start now loads for itself. A window that tells somebody to press a button they do
not need is the same defect as one that hides a button they do.

**What is verified and what is not.** The build is clean at 0 warnings and the suite is 942, up
twenty-one, with the new behaviour pinned: rows appearing in the pane while a file is still decoding,
a second pass clearing the bar and naming its two halves, the sideloaded section listing and
deleting, the tab re-reading on open, Start refusing only when no weights are installed, and the
row naming each pass's backend separately — WebGPU specifically, the case that was silent for both.
The
two engine timings above are real runs on this desktop. **The window itself has not been driven by
hand for any of this** — these are headless view-model and control tests, and screen capture is not
available in this session, so nobody has yet looked at the running application and seen the pane
fill, the labelling bar move, or the sideloaded section appear. `docs/UNPROVEN.md` carries that.

### Decided 2026-08-23 — the resampler's kernel is tabulated by phase

**Asked why the speaker pass was slow, the answer turned out to be that it was not the speaker
model.** `Resampler` band-limits before it decimates, and it evaluated its Blackman-windowed sinc
per tap per output sample — one sine and two cosines each time, about 9.3 million transcendental
calls per second of audio at 48 kHz. Benchmarked alone it ran at **25.7x realtime**, and the whole
labelling pass was being reported at 24x. `docs/UNPROVEN.md` has the table; the short version is
that **roughly nine tenths of a diarisation was this filter**, and on a ten-minute file the pass
went from 25.3 s to 3.3 s with identical turns.

**The class had already named the fix and misjudged when it would matter.** Its own remarks said a
tabulated kernel was the answer "if a very high input rate ever matters". 48 kHz is not a high rate.
The arithmetic it wrote down — 9 million transcendental calls per second of audio — was right; what
was wrong was the sentence next to it calling that "still small against the model", which rested on
a 65x figure for a model whose speed had never been measured apart from this filter.

**It is a phase table and not an interpolated one, which is the decision worth recording.** Every
sample rate is a rational multiple of 16 kHz: reduce `source/target` to `A/B` and output `n` sits at
`n*A/B`, whose fractional part depends only on `n mod B` — one phase at 48 kHz, 160 at 44.1 kHz. Each
phase's taps are computed once with the same kernel function and reused, so a tap is a value that
function returned rather than an interpolation between two of them. The alternative the old remark
proposed — a table with linear interpolation between entries — would have been an approximation
needing a measurement to justify; this needs none, because nothing is approximated.

**Where the centre comes from changed, and that is the one thing that is not identical.** It is now
exact integer arithmetic on `A` and `B` rather than `n * ratio` accumulating rounding in a double.
For a ratio whose reduced denominator is a power of two — 48 kHz, 32 kHz, 96 kHz, 8 kHz — the two
agree exactly and the output is **bit-identical**, pinned by a test that holds the new filter against
a frozen copy of the old one. For 44.1 kHz and 22.05 kHz they do not: the worst single sample moves
by 5.96e-08 and 1.19e-07, half an ulp and one ulp of a float. The new arithmetic is the more accurate
of the two, and no published figure describes either — AMI is 16 kHz, so this code is bypassed on the
only corpus scored.

**It also un-hid the GPU.** CPU against WebGPU had been 37.0 s to 25.3 s, a ratio of 1.5x that reads
as "the provider barely matters here"; both runs were paying the same single-threaded filter. It is
**10.6 s to 3.2 s** now — 57x against 187x realtime — which is an ordinary GPU result and restores
the reason `auto` prefers WebGPU. And it makes the catalogue's own sentence true again: the speaker
pass "roughly doubles how long a file takes" was about 8x before this and is about double now.

### Built 2026-08-23 — the word being spoken is marked in the transcript

**The Ask tab's transcript now marks the one word being said, in the colour the design reserved for
exactly that and nothing else.** Playback already lit the line; inside the line, the word being
spoken carries `#F0D983` — the pastel yellow `Theme/Tokens.axaml` has held since the design landed,
under a comment saying it was pinned to a single job and unused until there was a view for it. This
is that job. It is **not** the *word-by-word view* the design describes — a lane per speaker, words
appearing as they are said, a lane lost when a speaker goes quiet — which is still unbuilt. It is
the same mark, on the same data, on the surface that already exists, which is why the token's rule
is unchanged rather than widened: one job, one colour, a second surface for it.

**It is v1's data and no new data at all.** `TranscriptSegment.Words` has carried word timings from
the beginning — they are what `vtt-words` writes, and what `scripts/preview-words-vtt.html` has been
highlighting from since before this application had a player. `TranscriptLineViewModel` now carries
them across from the segment unchanged, so the rule that the window never writes a timestamp of its
own survives intact: it locates a word in the text and reads the word's own start.

**Which word is lit is the prototype's rule: the last word that has *started*, held until the next
one begins.** Lighting only a word whose own span contains the playhead is the obvious rule and the
wrong one — at a 100 ms tick it blinks the mark off in the gap between two words and goes dark for
the length of every pause. Holding it draws nothing *ahead* of the moment being played, which is the
constraint the word-by-word design sets, and it is what `preview-words-vtt.html` and WebVTT's own
`:past`/`:future` already do.

**The words are located in the text, not assumed to spell it.** Joining a segment's words with
single spaces reproduces its text on nearly every segment this pipeline produces — `SpeakerAssignment`
checks exactly that before it cuts a segment on a speaker change — but nearly is not always, and
assuming it fails *silently*: every word after the first disagreement lights one word early, which
reads as a transcript rather than as a defect. So each word is searched for from where the last one
ended, at a word boundary, and one that is not there is skipped without moving the cursor. Forward
only, so a mark can never walk backwards through a line; boundary-aligned, so "read" cannot land
inside "reader"; and a skipped word simply never lights while the words around it keep their places.

**Where there are no word timings there is no mark, and nothing is guessed.** The English pane
carries none, because translation loses the timing of individual words — and neither does any
segment an engine returned text but no words for. The alternative is timing a word by its share of
the line's characters, which is precisely the guess `WordTimedVttFormatter` refuses to write,
calling it "a worthless guess about when a word is spoken". The line highlight is unaffected either
way, so a transcript without word timings behaves exactly as it did the day before.

**The mark takes a ground and never a weight**, which is layout rather than taste: a bolded word is
a wider word, and a word that changes width three times a second re-wraps the paragraph under the
reader. Where the word being said is also the word being searched for, the ground goes to the spoken
mark and the search hit keeps its weight, so the two overlap without either being lost.

**What it costs per tick is one line's worth of work.** The transcript scan for the active line is
unchanged; the word is then looked for on that line alone, which is a sentence's worth of
comparisons. Moving the mark within a line touches one line and raises two notifications on it;
moving it to the next line touches two lines and raises three each — the line's own playing flag,
the word it has or no longer has, and the marks the view draws. A test holds all three counts,
because that bound is the whole reason this is affordable at 10 Hz on a three-hour transcript.

**The pane follows the playhead, and stops when the reader does.** A mark that is correct and off
the top of the pane is invisible: the played line left the viewport within a minute of pressing
play, because the transcript followed the *search* into view and nothing else. It now follows
playback too — **while the line the playhead has just left is still on screen**, and not otherwise.
That one rule is the whole of it. A reader who has scrolled somewhere else is reading, and taking
the page back off them every ten seconds would be this window arguing with them; a reader watching
the played line gets the next one brought to them. It needs no gesture to detect and no flag to
reset, and it resumes on its own the moment the played line is in view again — by scrolling back to
it, or by clicking a cue, which seeks there and is a request to be there. The visibility question is
asked *before* the scroll is posted, because a posted call runs after the offset may have moved and
by then "where was the reader looking" can no longer be answered.

**Eleven tests, and the suite is 960.** Six drive the view model against a fake clock — the mark
appearing, holding across a gap, leaving the line the playhead left, coming back with a backwards
seek, staying away on a transcript with no timings, and skipping a word the text does not spell.
Three read the runs a live window's `TextBlock` holds, including the pastel yellow read back off the
rendered run. Two drive a sixty-line transcript through a live scroller, and they catch the rule
failing in both directions: never following fails both, and following unconditionally fails the one
that scrolls away. `docs/UNPROVEN.md` § *Playing a recording* records what nobody has watched.

### Fixed 2026-08-23 — the diariser's CPU was two thread pools spinning on a GPU

**Watching a hardware monitor during a labelling pass found something no timing would have.** The
pass ran at 179x realtime and nothing about it looked wrong; the maintainer noticed that CPU *and*
GPU were both past 80 % at the same moment and asked whether a GPU diariser should be doing that.
It should not. On the desktop the chunk loop was holding about **23 of 32 cores while doing roughly
half a core of arithmetic** — the rest was ONNX Runtime's twelve intra-op threads and PyTorch's
sixteen, both busy-waiting through every graph call.

**Neither pool was sized by anyone.** ORT's is the diariser's own default of 12, which the
application inherits by passing 0. Torch's is one thread per physical core, which nothing in the
sidecar ever set — and both spin rather than sleep while they wait. The loop is one `sess.run` per
chunk with a small state update between them, so for the 95 % of each iteration spent inside the
graph, twenty-eight threads had nothing to do and were spending a core each not doing it.

**The measurement that decided the shape of the fix** was timing the three call sites inside the
loop rather than the loop as a whole: `sess.run` **0.95 s** of a 0.99 s loop, `streaming_update_async`
**0.03 s**, `apply_mask_to_preds` **0.00 s**. Sixteen torch threads for thirty milliseconds of work.
Cutting the pool 16 → 1 left the wall time flat to two decimals and took CPU from 14.95 s to 0.52 s;
turning ORT's spinning off took another third out on top. `docs/UNPROVEN.md` has every figure.

**Two changes, and both are scoped rather than global — the scoping is the whole of the design.**

- **ORT spinning off for GPU providers only.** On the CPU provider those threads *are* the
  arithmetic, and that path is where every published figure in this repository was produced.
  Nothing has measured what taking their spin away would cost there, so nothing takes it.
- **Torch's pool narrowed to one thread inside `run_mel`, and restored in a `finally`.** Not for the
  pass — `feats.py` is the opposite case and the reason a global setting would have been a bad
  trade: over 30 minutes of audio the featurizer runs in **0.19 s at sixteen threads and 0.94 s at
  one**. So the featurizer keeps what it uses, the loop gives up what it does not, and an engine
  that raises mid-loop cannot leave the process single-threaded for the next file's features.

**What it is worth**, on the whole `run_wav` path over ten minutes of audio, three runs each:
**1.09 s wall / 24.9 CPU s / 22.9 cores** becomes **1.08 s wall / 4.5 CPU s / 4.2 cores**. It is not
a speed change and was never going to be — the GPU was already the thing taking the time. It is the
difference between a labelling pass that commits the whole machine for its duration and one that
does not.

**It changes nothing the model computes, and that is measured rather than argued** — this project
has a DirectML entry that scored 53 % while looking entirely healthy. The committed parity fixture
passes on both providers after the change (CPU **0.0**, WebGPU **1.0729e-06**, zero decision flips),
and the baseline taken on the same machine minutes earlier is the same 1.0729e-06 to every digit,
three runs running. A direct comparison of the probabilities with spinning on against off was
bit-equal: 0 of 30,000 cells differing, argmax agreeing on all 7,500 frames. No AMI re-score was
taken, and the argument for not needing one is that the outputs are byte-identical rather than close.

**And it moves the pass's real cost into plain view.** With the spin gone, the 157-minute recording
that raised the question spends about **15 seconds on the GPU at 0.4 of a core** — and **35 seconds
before that decoding and resampling the file on one thread with the GPU idle**. Yesterday's
resampler tabulation (§ *Decided 2026-08-23 — the resampler's kernel is tabulated by phase*) took
that stage from nine tenths of the pass to a little over half of it; it is now unambiguously the
largest thing left, and nothing has been done about the fact that it is single-threaded.

### Fixed 2026-08-23 — the labelling pass decoded and resampled one after the other, and had no reason to

**With the spin gone (§ above), the largest thing left in a labelling pass was the stage before the
model: reading the recording a second time and resampling it to 16 kHz, on one core, with the GPU
idle.** On the 157-minute podcast that was 35 of the pass's 53 seconds. So it was split into its
parts before anything was changed to it, because "35 seconds" does not say which part to attack:

| | | share |
| --- | --- | --- |
| decode (Media Foundation, 44.1 kHz AAC) | **19.76 s** | 59.4% |
| resample (the tabulated filter) | **13.09 s** | 39.3% |
| WAV write, 577 MB at 1383 MB/s | 0.42 s | 1.3% |

The resample at 13.09 s over 9,448 s of audio is **722x realtime**, which is exactly what
§ *Decided 2026-08-23 — the resampler's kernel is tabulated by phase* measured it at in isolation —
so the filter was not slow, and making it faster was not the opportunity. **The opportunity was that
the two halves ran one after the other inside a single loop** — a block read, that block resampled,
then the next block read — when neither waits on the other's hardware and neither needs the other's
result. The second was the first's cost paid twice over.

**They now run at the same time**, as a producer and a consumer over a bounded queue eight blocks
deep. The blocks are *copied* into pooled arrays on the way across, which is not an optimisation to
regret: `MediaFoundationAudioSource` fills one buffer and yields a window onto it, so the same array
comes back every time — correct for a consumer that finishes before asking for the next block, fatal
for one that does not. The copy is 1.6 GB of memcpy over the whole file, a fraction of a second,
against the thirteen it buys.

**A pair of stages, not the work inside either.** The resampler is a filter carrying history across
block boundaries, so its blocks still arrive in order and are still processed by one thread; nothing
here divides a filter. Parallelising the filter as well would buy nothing anyway — it is now entirely
hidden behind a decode that is 1.5x longer than it.

**Measured on the recording that prompted it**, `uindosill diarise` on WebGPU, 157 minutes:
**52.7 s becomes 39.2 s**, 179x realtime to **241x**. The 13.5 s saved is the resample's 13.09 s to
within the noise of a single run, which is what "hidden behind the decode" predicts. Peak host CPU
went from 1.55 cores to 2.73 — two threads working where there was one.

**The output is byte-identical**, and on the real file rather than a fixture: the RTTM for the
157-minute podcast has the same MD5 before and after, 1,001 turns either way. Two tests pin it in
CI, neither needing a model — one comparing a staged 44.1 kHz WAV against a single unbroken pass of
the resampler (44.1 rather than 16 kHz on purpose, or the identity path would assert nothing about
resampling), and one holding cancellation to reaching both halves rather than parking a producer on
a queue nobody is draining. Both fail if a sample is dropped per block.

**What is left, and it is now the whole stage.** The decode is 59% of the staging and unchanged: one
Media Foundation reader, one thread, 478x realtime on AAC. Overlapping bought everything overlapping
could buy, and going further means either decoding in parallel — several readers seeking to
different offsets, which is a real change with real risk at the seams — or not decoding twice at
all, by teeing the ASR pass's own read into a 16 kHz stream as it goes. The second is the larger
prize and the larger change: it would remove the stage outright for `transcribe --speakers`, at the
cost of holding 577 MB through the transcription pass, and it does nothing for `diarise` on its own.
Neither has been attempted.

## Sortformer was shelved 2026-08-27, and the gate it passed no longer describes anything that ships

The diariser that cleared the speaker gate is in `attic/sortformer/`. Speaker labelling is now
`pyannote/speaker-diarization-community-1` alone, and **it has not been scored under the gate's
protocol** — so the one criterion in the table below that this project could report as passed is, as
of this entry, being reported about an engine the product does not contain.

That is stated first because everything else in this entry is smaller than it.

**Not "unmeasured", which is what this paragraph said when it was written and is wrong.** The
shipping engine was run on 2026-08-27, hours before the shelving: AMI **ES2004a** alone scores
**14.38%** DER at collar 0.25 with overlap (18.76% at collar 0), returning 5 speakers against the
reference's 4, at **5.2× realtime** on the CPU. One meeting is not the sixteen-meeting test set the
gate names, and the two numbers are not comparable — which is exactly why the gate reads unmet rather
than either passed or unknown. `docs/UNPROVEN.md` carries both the figures and the distinction.

**The decision was the maintainer's and it was not forced.** DiariZen left the same morning because a
version conflict displaced it; Sortformer left that afternoon because it was asked to. It was passing
every check it had: AMI test 16.33% against a 23.8 bar, speaker error 0.06 against 1.0, a parity
fixture green on every provider that shipped, and a C# port that had reproduced it to 0.0044 points.
No defect prompted this and none is claimed.

**What the shelving removed, beyond the engine.** The two-arm `kind` switch that let the sidecar load
either diariser, and with it protocol version 4 — a version-4 host would send `kind: "sortformer"`
with an ONNX path and be handed a torch pipeline under a different licence, so the number went to 5
and that becomes a refusal at `hello`. The diariser's parity check on both sides, because the
pipeline that remains is torch on both stages and has one path where parity needs two;
`--speaker-backend-unverified` and `diarise --backend-unverified` went with it, since there was no
longer a provider to unlock or a check to override. Four warnings in the command line and four in the
window, every one of which quoted an AMI figure at a backend. The catalogue entry, and with it this
product's last component under the NVIDIA Open Model License — the Agreement copy §3.1 required no
longer ships, because nothing owes it.

**What was deliberately kept.** The biometric caution in `Attributions.WeightUsageRestrictions`,
restated as this project's own rather than as NVIDIA's §2.3: separating people by their voices is
voice biometrics whichever model does it, and dropping a real consent warning because the licence
that mandated it left would have been a paperwork change deciding a substantive one. A test asserts
it survives and that it is no longer phrased as a licence term. `OpenModelLicenceAttribution` stays in
the code constructed by nothing, as `CcByNcAttribution` already did, because it is a reading of a
licence family that took work to establish.

**Ten tests left, 1414 to 1404**, and the arithmetic is worth spelling out because it is not a single
subtraction. Fourteen methods were removed; two of those came back under new names; two were added.
Of the twelve genuine removals, **five** exercised the diariser's parity path, **two** the retired
licence's notice and its Agreement copy, **two** the speaker-provider picker's ONNX-registration
filter, **one** the four-speaker cap, **one** the `fellBackFrom` list a torch `auto` cannot populate,
and **one** the ONNX-speaker-embedder warning that had left with DiariZen and had nothing to fire on.
The two additions are `SpeakerChipContrastTests`, which holds the eight-chip palette at 4.5:1 and
holds each chip's appearance unique. A third assertion went inside an existing test rather than
becoming one: a settings file still naming `webgpu` or `dml` must read back as automatic rather than
reaching a sidecar that refuses it. `DeclaredLimitsTests` survived by changing what it polices rather than by being
deleted: the constants it holds the host against are `None` now, and it fails if either side acquires
a number, because a cap appearing on one side only would be this build claiming a limit nobody
established.

**What this costs, stated as a debt rather than absorbed.** The figure the product publishes is gone,
and so is its only guard against a silently wrong backend on the speaker path. What replaced the
figure is one meeting rather than sixteen. `docs/UNPROVEN.md` carries all of it and what it would
take to close it — one run of `measure-der.ps1` over AMI test under the gate's own protocol. Until
that happens this repository's answer to "how good is the speaker labelling" is **one meeting's
14.38%, and no answer to the question the gate asks**, which is a worse answer than it had on
2026-08-26 and an honest one.

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
| 5 — ship | Signed, updating installer | **Installer done, signing dropped from v1.** Two Velopack channels, a `v*` tag workflow, and an in-app update check; installed, updated and uninstalled on the desktop 2026-08-19 with the weights hashed and unchanged throughout. Unsigned by decision. **Published 2026-08-23: `v1.0.0-rc.3`, a public prerelease — the first real release, and the first packed with the Python bundle.** v1.0 itself is not tagged; the desktop end-to-end on the rc stands between them |
| translation | **Three criteria, all must hold — two ratified 2026-08-19 before any score existed, the third and the margins on 2026-08-20 with the first scores.** **(1)** chrF++ into English clears the **per-language source-copy floor** — what a hypothesis scores by echoing its untranslated source — by a per-language margin, because one number across 25 languages would be a different bar in each. **(2)** A **human adequacy check on the Spanish → English driving case**, rated for adequacy and flagged for output that is not English. Nothing anchors this from outside: no published chrF++ or BLEU for any candidate on FLEURS X→en at a stated signature was found, so unlike the DER gate it is anchored from inside its own measurement, and the corpus is FLEURS pinned by digest with both metric signatures printed on every run. Opt-in aboard v1.0. | **Seam built 2026-08-19, artefact exported 2026-08-20, criterion one scored in all 24 languages 2026-08-20.** The route is decided — `opus-mt-tc-bible-big-mul-deu_eng_nld`, apache-2.0, exported in-house to ONNX, CPU-only in v1 — and a spike on 2026-08-19 settled four things ahead of the code: the `>>eng<<` target token is mandatory and its absence returns fluent German, greedy decoding drops content beam-6 keeps over 44 real segments, English input passes through byte-identical, and an int8 export was thought to weigh 227 MiB or 404 MiB. The parts that need no model landed the same day — the `ITranscriptTranslator` contract with the target token and the dropped word timings as enforced invariants, the canned translator, `ModelTask.Translation` and its manifest word, and `--translate` on the CLI wired to the fake. **The ONNX export exists as of 2026-08-20** and replaces the last of those four: `scripts/export-translation-onnx.py` produces **nine files** in the merged layout — two graphs with past-key-values exposed, two configs, and a five-file tokenizer — at **1369.1 MiB fp32, 345.9 MiB int8, or 694.3 MiB int8 with the embedding tables left in fp32**, and **fp32-merged is what ships as of 2026-08-20**, int8 having been dropped that day on speed, on a silent GPU collapse and on the export smoke, without a quality score ever being taken of it. The recorded `optimum` failure was CPython 3.14 giving `functools.partial` the descriptor protocol, not a library skew, and a twelve-line shim defeats it. fp32 reproduces the PyTorch reference string-identically on all 44 recorded segments; int8 changes most of them and collapses into a repetition loop on one. **The multi-file catalogue schema landed the same day** — an entry may be a set of files in a directory of its own, installed all-or-nothing through a staging directory, with per-file pins and per-file resume; no entry uses it yet because no asset has been uploaded. **The harness landed 2026-08-20 and computed criterion one's bar in every language** — the per-language source-copy floors run 2.00 (Ukrainian) to 23.10 (French) on FLEURS test, an 11.5x spread that is why the gate refuses a single number. **Criterion two is unperformed and the gate is therefore not passed.** **Criterion one is scored and its bar is set**: `margin_L = 45 − floor_L` plus zero collapses, ratified 2026-08-20, **23 of 24 languages pass and Slovak fails by 0.74**. `fp32-merged` over FLEURS `test` in full, beam-6, on the desktop's CPU — 8,149 sentences in 1.40 h, chrF++ from **44.26** (Slovak, the outlier the record predicted from its absence in the sibling card's source list) to **68.52** (Portuguese), margins over floor +28.15 to +60.53, median +42.76, and **zero collapses** against 31 trailing-punctuation runs. **The decode loop landed the same day** — a SentencePiece tokenizer and a port of transformers 4.57.6's beam search in C#, driving the pinned graphs at beam 6 on the CPU — **retired to `attic/` on 2026-08-21**; the decode is `transformers.generate` itself again, in a bundled Python, at the same settings, defaulting to WebGPU, held to the 8,149 hypotheses the gate run itself recorded (§ *Built 2026-08-20 — the decode loop*). `models.json` gained its first multi-file entry, nine files pinned by size and digest and marked unverified because no release asset has been uploaded. **The weights were published to Hugging Face on 2026-08-20 and the entry is verified against the nine LFS oids the repository publishes**, with the Apache-2.0 §4(c) and §4(d) checks done before the upload rather than after — no NOTICE file upstream, no copyright line anywhere, and four attribution notices retained. The first real multi-file install ran the same day: staged, hashed, 9 of 9 verified, and the graphs then loaded out of that assembled directory. **The cascade penalty is measured** — Spanish −2.95 and German −4.34 chrF++ against ASR word error rates of 6.12% and 9.93% — recorded and deliberately not gated. **The window's half landed 2026-08-20**: an "English version" opt-in drawn as the twin of the speaker one — its own tinted strip, off by default, disabled with a reason while the entry is not installed — and a Transcript/English pill switcher over the transcript pane, drawn only for a row that has both. The window keeps the transcript as the engine wrote it beside the English rather than replacing it, which is what the switcher switches between; outputs take the same `.en` infix the command line gives them, and `vtt-words` is refused under the opt-in there too. **No spoken-language picker was added, and that is the second time the answer has come out that way** — the translator is many-to-one and never told its source, and the ASR's hint is inert on this catalogue (`docs/UNPROVEN.md` § *The language hint*), so a control for it would change nothing. **Decided 2026-08-23: the adequacy check is declined with finality — v1.0 ships with criterion two unperformed and the gate unpassed by its own definition, documented rather than redefined.** Still outstanding: a real-time factor for a translation pass over real audio; and an interrupted install, which nothing has exercised. `docs/UNPROVEN.md` § *Translating into English* has what is measured and what is not |
| speakers | **AMI test DER within 5 points of the best published figure on the same audio at the same convention** — pyannote 3.1's 18.8 on Mix-Headset at collar 0 with overlap scored, so ≤ 23.8; collar 0 because half-width and total-width definitions agree there, which is what makes the comparison convention-proof — with this project's own headline (collar 0.25 pyannote semantics, 0.125 s either side, overlap included) reported beside it. **NOTSOFAR-1 is the crosstalk check** (39% of union speech overlapped, against AMI's 14.58%), and it is a meeting corpus too, so both of the gate's corpora are now in the target domain. **VoxConverse left the gate on 2026-08-18 when the domain narrowed to meetings** — see the narrowing below; it was the web-video and beyond-four-speakers check, and web video is no longer a target. **Podcasts are ungated**, for want of any labelled material. The 5-point margin was **ratified 2026-08-18**, before any candidate had been scored at this convention. **Second criterion, added 2026-08-18: mean |speakers found − speakers in reference| ≤ 1.0 over the AMI test set — both criteria must hold.** Opt-in aboard v1.0. | Instrument built and validated, AMI dev and test set up and verified, seam in; sherpa-onnx 1.13.5 measured 2026-08-18 and **fails on AMI**, held out — 25.05% with NeMo TitaNet-L and 25.77% with 3D-Speaker ERes2Net, hyperparameters chosen on the 18 dev meetings and applied unchanged to the 16 test meetings; its threshold, min_duration, six embedders and int8 segmentation are all swept, so the toolkit's knob space is exhausted. **Streaming Sortformer 4spk v2.1, ONNX, measured 2026-08-18 on the desktop, CPU only: the gate PASSES on both criteria** — AMI test **16.33%** at collar 0 with overlap against ≤ 23.8, and speaker error **0.06** against ≤ 1.0, tuned on the 18 dev meetings and applied unchanged to the 16 test meetings, test scored once. NOTSOFAR-1 and VoxConverse still untouched, and **VoxConverse can no longer serve as this candidate's beyond-four check** — see below. **The C# port landed 2026-08-19 and reproduces it: AMI test 16.3368% against the Python reference's 16.3324%, 0.0044 points apart, same speaker error 0.06, both gate criteria hold.** Shipped as the opt-in in the CLI and the app, then **retired to `attic/` on 2026-08-21** when the engine moved into a bundled Python: what ships now is the Python the reference was taken from, so the figure the product carries is **16.3324%** on the CPU and 16.3319% on WebGPU, and the 0.0044 divergence between the CLI and the window closed with it. **Measured 2026-08-20 on whole podcasts and it does not transfer**: all four episodes returned four labels whether there were 2, 3, 5 or 7 speakers — the cap explains the last two and over-segmentation explains the first two — and a duration ladder over one episode puts the count right to 50 minutes and wrong from an hour, against AMI meetings averaging about half an hour. AMI dev re-scored the same day is 8.62% at collar 0.25 with 0.94% confusion and 4-of-4 speaker agreement on all eighteen, so this is a long-recording limit rather than a bad model. Nothing was re-tuned; the product now warns before the run, past an hour and on a count above the cap. No DER exists for any podcast and the cap is still unpriced. **NOTSOFAR-1, decided 2026-08-23: scored after v1.0 — an obligation recorded under *After v1*, not a waiver.** **Shelved 2026-08-27: this whole row describes an engine that is now in `attic/sortformer/`.** Speaker labelling is `pyannote/speaker-diarization-community-1` alone and **has not been scored against this gate** — one of the sixteen test meetings, ES2004a, scored 14.38% at collar 0.25 and 18.76% at collar 0 with 5 speakers against 4 on 2026-08-27, which is not the protocol and is not comparable to a sixteen-meeting mean — so the gate stands unmet by the shipping product rather than passed. See the section above, and `docs/UNPROVEN.md` |
| v2 — ask | A human asks three questions of a real transcript on Windows and follows a citation into the audio — the revised plan's Stage 4 exit criterion | **Built end to end 2026-08-23/24 on the laptop; the human run is still owed.** The five stages landed in order: the transcript reader and keyboard seeking (Stage 1's remnants); windowed BM25 and the citation machinery — parser, validator, the rule that the model never writes a timestamp (Stage 2); `IAnswerEngine` over a vendored `llama-server` child at b10603, run green on cpu and vulkan under a gated test with a digest-pinned 0.6B (Stage 3); the chat panel wired, streaming, citing and abstaining, with R9 enforced in both directions (Stage 4); and the vulkan drop in both installer channels with the MIT text travelling and the channel read-back holding it (Stage 5). The three register questions v1 created were all taken 2026-08-24: the English pane, no speaker labels, the hint or nothing. Nothing recommends a model — decision 2 holds until the CSB384 measurements run — so the panel serves a GGUF the user supplies. The open tail is in `docs/UNPROVEN.md`: the human exit criterion, the thirty labelled questions and recall@10, the desktop's first CUDA run, and the cudart decision the win-cuda channel waits on |
| v2 — tidy | The tidy ships when its delta on the ten-call WER corpus has been measured under both reference styles — composition included, since the contract allows deletions and forbids everything but doubted words — and the desktop has re-timed the pass and its tandem on CUDA | **Decided 2026-09-01; built 2026-09-02; both conditions measured by 2026-09-03; decided the same day to ship opt-in with the joined run as its unit; not yet in a release.** Measured first, on the second machine's Vulkan path: `gemma-4-E4B-it` tidies at 3.7 min per hour of audio with four requests in flight, saves 26% of the combined time run beside the recogniser on a ten-minute file at the cost of a transcription 31% slower in that run, and before any contract changes one word in ~300 — `docs/UNPROVEN.md`, *Gemma 4 E4B as a transcript tidy*. Built the next day, every part of the decision from the contract to the pane (*Built 2026-09-02*), and run beside the real recogniser the same evening (*Measured 2026-09-02*): the contract held on 77 of 77 lines with nothing refused, the recogniser was 31% slower for the company, and the tidied version landed 45 s after the plain one on the ten-minute sample — the line count, not the words, sets that pace, which was the open question then. The corpus delta followed the same evening (*Measured 2026-09-02, evening*): +3.43 against the verbatim transcripts, −2.94 against the edited ones, the spoken row reproducing the desktop's baseline to two decimals, the composition all deletions one way and insertions the other. The criterion reads the non-verbatim delta with the verbatim one reported beside it (*Decided 2026-09-02, late evening*), so the first condition is met on Vulkan. The unit the stage sends was measured across three units on 2026-09-03 (*Measured 2026-09-03 — the request unit*): joined runs land the tidied copy 83 s after the plain transcript against the segment's 194 s and tidy better under both references, but a refused run refuses every line in it, and by the rule as written the segment stays on that clause alone — how the clause should read is the decision now open. The desktop's CUDA delta and re-timing followed the same morning (*Measured 2026-09-03, desktop*): the delta is the laptop's to a twentieth of a point, +3.47 / −2.89, on a spoken transcript whose text is byte-identical to the 2026-08-16 baseline's, so the second condition is met on the same reading; the tandem lag is a fifth of the laptop's or less and the tandem still lands first, by 7–12% of the combined time rather than the spike's 26%; the recogniser pays 63% for the company over the corpus where the laptop paid 11.5%; and the same rule picks the sentence-run there and the segment on the laptop, on a refused count that moves by three between identical runs. The decisions followed the same day (*Decided 2026-09-03*): the clause counts refused requests, under which both longer units qualify on both machines and the joined run wins on lag; the joined run is the unit the stage sends; the tidy ships opt-in; and the tandem stays the default on every card. Driving the shipped command over one file the same day found what the unit change had opened (*Found and fixed 2026-09-03*): the contract bounded the form of an edit and never its size, so the model lifted a clause out of a line and a whole sentence out of another, and — the joined run's own half of it — the per-line empty guard, which lives where a run is judged as a composite, let three lines empty and vanish from the pane with nothing refused. `MaxDeletedFraction` (0.5) and `MaxConsecutiveDeletedWords` (4) now bound it per piece; the same call then keeps all five, at 35 of 113 lines untidied against 11. Still owed: the corpus delta and refusal count under the joined run **with the ceiling**, never scored on either machine and no longer described by the figures the ship criterion read; whether 0.5 and 4 hold off the one call they were fitted on; what the ceiling costs in tidying not done; whether one bad piece should refuse a seven-line run; and the tag |

### Fixed 2026-08-23 — one pinned button height was cutting every cue in half and shaving every speaker label, and the Ask tab was rebuilt around the recording

Running the built application again found the transcript arriving cut off mid-sentence and the
speaker chips sliced along the bottom, and reported them as two defects. They were one, and it was
not where either of them looked.

**The cause is a single inherited setter.** `Style Selector="Button"` sets `Height="30"` — right for
a button standing in a row of controls, and a rule overrides only what it names, so `Button.cue`
inherited it. A cue is not that kind of button; it is a paragraph. Thirty units less the cue
template's six of padding top and bottom leaves **eighteen**, and eighteen was handed down to both
halves of the cue:

- **The words** are set on a 20.3 line and wrap. Measured against a maximum height of eighteen, the
  text layout kept the one line that fitted and discarded every line after it — so a 221-character
  segment rendered as one line, broken at whatever the window's width happened to be and silently
  missing the rest. `TextLayout.TextLines.Count` was **1 at every width from 820 to 1400**. It is
  now 2 at 1400 and 12 at 820 on an unlabelled transcript, and it reflows both ways. (With a
  speaker chip beside it the same segment takes 50 lines at 820, because the chip is `Auto` and the
  two fixed columns either side of this one are not; that is arithmetic rather than a defect, and
  `docs/UNPROVEN.md` says nobody has looked at it.)
- **The speaker chip's label** lays out at **14.64** in Instrument Sans at 12px, inside three units
  of padding above and below. Eighteen less six is **twelve**. The label was arranged 12.00 tall
  around text needing 14.64, so roughly 2.1 units of every descender was cut off — which reads as
  shaved letters inside an intact pill, and is why it was reported as a chip defect rather than a
  text one.

The fix is `Height="NaN"` and `MinHeight="0"` on `Button.cue`, with the same reasoning `Button.window`
had already written down for the same trap in the same file. Both figures above are measured, before
and after, through the headless window; three tests fail without the fix and pass with it, and the
one that matters asserts the *cue* is as tall as its content — the other two pass over a tab that is
still drawn wrong, because an overflowing child is arranged at its desired height while every cue
draws over the next.

**A wrong first answer is recorded here because it is the more useful half.** The first fix was
`RowDefinitions="Auto"` on the cue's Grid, which made the words wrap and the chip fit and was
measured doing so. It is not the cause and it is not a defence: with the height still pinned at 30,
declaring the row `Auto` leaves both defects in place, and it was removed once that was measured
rather than kept as belt and braces. A comment saying an attribute is load-bearing when it is not is
worse than no comment.

**The column was rebuilt at the maintainer's direction the same day.** It reads top to bottom as the
picture, the controls that move it, its words, and the box that searches them — the transport was
docked to the *bottom*, below the words it moves, and the find box sat above them. It is a row
`Grid` rather than a `DockPanel` now, because one of its edges is draggable and a `DockPanel` has no
edges: it says where a child goes by declaration order, which is not a property a splitter can act
on. **The transport is in the reading row rather than the picture's, and that is a constraint rather
than a preference** — a splitter moves the two rows either side of it and nothing else, so anything
sharing the picture's row would be resized with it, and anything given a row of its own in between
would cap the drag.

**The picture is resizable from the transcript's top edge**, which is the first splitter in this
window anybody was meant to notice: a hairline with a short grip on it, taro because the edge belongs
to the Ask tab, ten units tall over a one-unit line. It and its row are both absent for an audio
recording — a handle that does nothing is worse than no handle — and the height a reader drags to
survives a podcast opened in between. **The three older splitters in this window are still invisible
and deliberately out of scope**: they separate columns nothing has asked to move.

**The seek bar has the handle the design asked for, and the reason it did not have one was wrong
rather than the conclusion.** The record said it "cannot be positioned without a measured width".
No *binding* can measure a width — but the transparent strip over the bar has been measuring one
since the tab shipped, which is how a press at an x becomes a fraction of the recording. The handle
is that arithmetic run backwards. It is inset by its own width rather than centred on the playhead,
so it sits inside the track at both ends; what that costs is recorded in `docs/UNPROVEN.md`.

**Speaker labels can be renamed, which `PHASES.md` promised on 2026-08-19 and nothing had ever
built.** Nothing in the repository renamed a speaker before today: `SpeakerTurns.RenameByFirstAppearance`
maps cluster ids to `Speaker 1`, `Speaker 2`, and there was no path from a human-typed string to a
label. A strip of fields at the top of the reading row edits one `SpeakerViewModel` per voice, and
every cue of that speaker points at it — so a rename raises one notification rather than fifteen
hundred, and the same objects serve both panes of a translated transcript, which is the arrangement
that stopped the two panes disagreeing about colours on 2026-08-22 doing the same job for names.
**Only the editable half of that promise landed. "Swappable" is still open**, and it is ambiguous
between swapping two names — trivial under this model — and reassigning which speaker a chip *is*
across a transcript, which is a correction to the diariser's output and a different feature.

**The chips stay matcha inside a tab that is otherwise entirely taro**, which looks like a mistake
and is the written rule: speaker labelling is a v1 feature, and taro would make it read as a v2 one.

**A name here is for reading and reaches nothing else** — not the transcript files already written,
not a restart, not a second run over the same audio. The window says so, once somebody has actually
renamed something rather than as a standing caveat over a feature nobody has used. Why it stops
there, and what it would cost to go further, is in `docs/UNPROVEN.md`.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** `CLAUDE.md`'s second
count said 949 and had been stale by thirty for some time, because `949 skip` does not match the
pattern `scripts/check-test-counts.py` looks for; it is reworded to `1633 tests` so the guard now
covers it.

### Built 2026-08-23 — a transcript goes back inside the recording, and ffmpeg is vendored to do it

The half of this product that is for somebody who does not want files. Transcribe a recording, press
one button, and the recording is beside its original with the words inside it as a subtitle track
any player will show. The sidecar `.srt` is still written; this is for the person who was never
going to open it.

**Which container it goes into is decided by which format was ticked, and every rule was measured
against FFmpeg 9.0.1 rather than read off a specification** — because two of the answers are the
opposite of what the specifications suggest:

- **MP4 cannot hold WebVTT at all.** Not "loses the styling": the muxer refuses the stream —
  *"Could not find tag for codec webvtt in stream, codec not currently supported in container"*. Its
  only subtitle codec is `mov_text`, 3GPP timed text, which is plain text.
- **So word-level timing always forces Matroska.** Converting a word-timed cue to `mov_text` strips
  every inline timestamp: **60 in, 0 out**. Copied into Matroska with `-c:s copy`, all 60 survive and
  come back byte for byte.
- **SubRip through `mov_text` is exact** for what this product writes — 19 lines in, the same 19
  back — so an SRT keeps the file an MP4, which is the container that plays everywhere.
- **An MP3 cannot hold subtitles** — *"Only audio streams and pictures are allowed in MP3"* — but its
  audio copies into an MP4 unchanged, **samples bit-identical**, at the cost of a couple of kilobytes
  of container. So a podcast becomes an MP4 rather than being re-encoded to AAC to get an `.m4a`,
  which is what "convert it to something that can hold a track" would otherwise have meant: `.m4a`
  goes through ffmpeg's iPod muxer, which refuses MP3 audio the general MP4 muxer accepts.
- **ASF is the exception that shapes the fallback.** A `.wma` refuses to copy into MP4 and copies
  into Matroska happily, so Windows Media takes the Matroska route whatever was asked for. The rule
  the whole thing runs on is: **never re-encode; if MP4 cannot hold it, use Matroska.**

Cover art survives either route, which matters because a podcast's cover is a video stream and
mapping only the audio would quietly throw it away.

**mkvmerge was measured against ffmpeg for this and rejected, and the reason is backwards.**
MKVToolNix writes WebVTT under `S_TEXT/WEBVTT`, the identifier Matroska actually specifies; FFmpeg
writes `D_WEBVTT/SUBTITLES`, the older WebM one. FFmpeg's demuxer reads its own and not the specified
one — it reports the track's codec as `none` and refuses to decode it, while carrying a perfectly
good WebVTT decoder. This application plays through libmpv, which *is* FFmpeg, so a file muxed by the
more correct tool is a file whose subtitles our own Ask tab cannot show. mkvmerge also cannot write
MP4 at all. SubRip is unaffected — both write `S_TEXT/UTF8` — so the split is specific to the one
format the word-level timing rides on.

**The transcript is rendered at the moment it is muxed, not taken off disk, and that is what closes
the rename gap from earlier the same day.** `TranscriptWriter.WriteAsync` runs before
`JobViewModel.Complete`, so the sidecars carry the diariser's labels and always will; nothing
retained the document, so there was no object a rename could be re-exported from. `JobViewModel` now
keeps the spoken document and `Named()` applies the reader's names to a copy of it — the engine's own
labels are never edited, so what is on screen and what is on disk still agree about where they came
from. A speaker somebody named reaches the file that goes into the recording.

**ffmpeg is vendored, reversing a decision taken earlier the same day, and the earlier reasoning was
not wrong.** "No ffmpeg is vendored" was checked rather than assumed: yt-dlp's DASH m4a warning does
not apply here, and both readers open one. That is still true. What changed is that a remux has no
other implementation.

**It is the LGPL build and not the GPL one, which is a licence decision.** Putting a transcript
inside a recording copies streams and encodes nothing, so nothing needs a GPL-only encoder — the
three subtitle codecs and the two muxers are all core FFmpeg. BtbN's GPL build ships **GPLv3**, which
this project has no reason to take on; the LGPL build is **LGPLv3**, 30 MB smaller, and was driven
over all eight input-and-format routes before it was kept. It is a separate program this application
spawns rather than a library it links, so unlike libmpv it does not reach this application's terms.

**It is not vendored beside yt-dlp, and that took a deliberate act rather than nothing.** yt-dlp
looks for ffmpeg beside its own executable before it looks at `PATH` — measured: the same binary
reports `exe versions: none` alone in a directory and `ffmpeg n9.0.1` with ffmpeg next to it, on an
identical `PATH`. The first drop put it in `tools/` and gave yt-dlp a muxer with no code change at
all. It lives in `native/win-x64/ffmpeg/` instead, `BundledTools` searches two directory lists, and a
test fails if the two ever end up together.

**yt-dlp is then given it anyway, by name on the command line, and what that changes was measured.**
Big Buck Bunny (Blender Foundation, CC-BY) — the same 9:56 the original DASH check used — downloaded
twice with this application's own arguments. Without ffmpeg: `ftyp iso6`, `major_brand: dash`,
fragment boxes present, 9,655,276 bytes, and the warning. With it: `[FixupM4a] Correcting container`,
`ftyp isom mp41`, no fragment boxes, 9,648,639 bytes, no warning. **The audio samples are
bit-identical and `AudioSources.Open` returns the same 26,306,560 samples and the same
00:09:56.5206292 either way.** So the fixup buys this application nothing, exactly as the original
check found — and it buys the person who opens the downloaded file in something else a container that
is not the one yt-dlp warns about. That is why it is on, and `--ffmpeg-location` is how, so it is a
wiring decision rather than an accident of where a file was put.

**What it adds to an installer: about 114 MB**, the largest thing this product vendors after the
models.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.**

### Built 2026-08-23 — the English is readable on the Ask tab, and the splitter stops fighting the clock

Two things found by running the built application, one asked for and one reported.

**The Ask tab shows the translation, with a pill switcher back to the transcript.** Asking for an
English version elsewhere in the window and then having nowhere to read it against the recording was
the wrong way round. The tab now switches to the English the moment one arrives on the open row, and
switches back on a pill; a reader who goes back to the transcript stays there, and another
recording gaining a translation later does not drag them off it.

**It cost almost nothing to build, and that is the interesting part.** Everything on that tab — the
highlight that follows the playhead, the find box, the cue a click seeks from — already read the
transcript through one property. Making that property answer with whichever pane is showing moved
all of them at once, and none of them had to learn what a translation is.

**The two panes are not equivalent, which is why this is a switcher and not a replacement.** A
translated segment carries its start and end and no word times: `SidecarTranscriptTranslator` writes
an empty word list, because translating loses which word was said when, and a word's position in an
English sentence is not a fact about the Spanish audio. So the English seeks and highlights by line
and marks no word, and the transcript is the pane that follows a voice. The tab says so in a line
under the pills rather than leaving a reader to notice a mark that quietly stopped — a feature that
works on one pane and not the other is indistinguishable from a broken one until something names
which.

**And the splitter added earlier the same day was fighting the clock.** Reported as: easy to resize
the transcript while the video is paused, stutters and rolls back to its original position while it
plays. That asymmetry is the whole diagnosis. `AskViewModel.Redraw` raises `HasVideo` on every tick
that moved the clock — ten times a second while a recording plays — and says the same thing every
time. The window treated each as news and wrote the picture row's height back; that height is only
remembered when a drag *completes*, so mid-gesture it was the height from before the drag started.
Ten stamps a second against a moving mouse. Paused there are no ticks, so there is no fight.

The fix is that `ShowPictureRow` only writes when the state actually changes, and the picture's size
is no longer published to the player on every layout pass of a drag — that is a render-target
reallocation per frame underneath a playing video — but once, on release.

**The first test written for it passed without the fix**, which is worth recording because it is the
failure mode a regression test is supposed to make impossible: it ticked the clock *after* the drag
completed, when the remembered height is already the right one. Ticking between mouse-moves is what
reproduces it, and removing the guard now fails with "the picture did not keep the size it was
dragged to".

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.**

### Built 2026-08-23 — the Ask tab reads by the sentence, and why its lines were thirty seconds long

**Reported as: the subtitles come out too long.** A YouTube documentary — NDR's *Hinter den Kulissen
von Hamburgs Kantinen & Co.*, 28 min 49 s — fetched from its link and transcribed through the app,
and the Ask tab's second line was 29 seconds and 389 characters: nine sentences lit as one block for
half a minute.

**The line was a segment, and the segment was the detector's.** One line per `TranscriptSegment`,
one segment per voice-activity cut, and the cut is an energy gate under a hard cap of thirty
seconds. Measured (`docs/UNPROVEN.md`, § *The Ask tab's lines are sentences*): the bed under the
narration sits at −23 dBFS median where the gate's threshold cannot rise above −35, so in that 29 s
segment 131 of 1,270 frames fall below the line and the longest run below it is 450 ms — one
silence-rule window, which is where the segment did end. The model's own word timings show what the
gate missed: pauses of 0.96 s, 0.96 s and 1.84 s after sentence-final words inside that one segment,
and six seconds of nothing inside the next. Across the whole file the cap is the exception — 285
segments, median 3.5 s, 34 of them ten seconds or longer — and the opening montage is where it
bites. This is the detector, not the recogniser: the recogniser heard the pauses.

**What was built: the tab reads a segment by its sentences, and the segment is not touched.**
`SentenceSplitter` in Core cuts a segment between two words when the first ends in `.`, `!`, `?` or
an ellipsis and the second opens with a capital, a number, a quote or a bracket — refusing a single
letter, digits alone and a stop inside the word, so `z. B.`, `am 3. Oktober` and `d.h.` hold. Each
piece is timed by its own words, the first keeping the segment's start and the last its end, exactly
as `SpeakerAssignment` cuts on a speaker change and on the same gate — the words must reproduce the
text, now one definition on `TranscriptSegment` shared by both cutters. The Ask tab's lines are the
pieces, through one factory, `TranscriptLineViewModel.LinesFor`, for the pane that fills mid-decode
and the rebuild alike, so a transcript does not re-cut itself the moment the decode ends. The
`TranscriptDocument` is unchanged: the JSON, the subtitle files, the citation unit and every
recorded segment count stand where they were.

**Measured on the same file, through the C# itself rather than a re-implementation:** 285 segments
became 478 lines, 80 of them cut, 193 cuts in all; the longest line fell from 29.4 s to 17.0 s — one
spoken sentence of 45 words — and lines of ten seconds or more from 34 to 4; the mean line is 2.9 s
and 43 characters against the segment's 5.4 s and 73; every segment's pieces join back to its text.
The rule declined once, at `bzw. die`, correctly. Its first draft refused a number as a sentence
opener to protect `ca. 40`; four of its five declines were then real sentence ends before a number
(`Genau. 50`, `Westen. 4.41`) and the `ca. 40` it protected did not occur, so the number opens a
sentence and `ca. 40` is a recorded false cut beside `Dr. Müller` — a per-language abbreviation list
was declined, because twenty-five languages of them is a second thing to keep right and a wrong cut
costs one line break. All 193 cuts were listed and read: none stands at a token that is an
abbreviation. What that reading is not is a comparison with the audio, which `docs/UNPROVEN.md`
says.

**The English pane stays one line per segment**, because a translated segment carries no word times
and a translation does not hold its source's sentence count; the notice under the pills now says
both things, since both have the one cause. *Superseded later the same day* — § *Built 2026-08-23 —
the English is translated a sentence at a time*: the translator is given the sentences instead.

**What this does not fix, named so nobody reads it as fixed.** The detector still cannot hear a
pause under a bed, so segments on such audio still run to the cap and a cap cut still lands at the
quietest frame rather than in a pause; a neural VAD is the fix for that and is not on the road. The
subtitle files still break cues mid-sentence — 24 % of the German cues and 29 % of the English ones
on this file open in lower case — because `SubtitleCueBuilder` reads characters and seconds and not
punctuation; a punctuation-aware cue was offered and declined for now.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.**

### Built 2026-08-23 — a neural speech detector, as an opt-in, because the gate cannot hear a pause under music

**Asked for in so many words**, hours after the entry above named it as the fix not on the road: the
energy gate that cuts a recording into decodable pieces reads loudness, and on a broadcast
documentary with a bed under the narration loudness never drops, so segments run to the
thirty-second cap holding nine sentences each while the recogniser's own word timings show the
pauses inside them. The sentence splitter made those *read* as sentences; this cuts them as
sentences, and it is the first change to what the segmenter hears since the gate was written.

**What was built.** `ISpeechDetector` in Core — a loaded model with a `Name`, handing out one
`ISpeechDetectorStream` per recording, fed samples in order and answering the latest speech
probability — and `StreamingSegmenter` takes a stream in place of its gate: the detector replaces
the *decision* and nothing else, with hysteresis read off two options (`SpeechProbability` 0.5,
`SilenceProbability` 0.35 — upstream's own pair, read at the pinned commit rather than chosen),
while the minimum durations, the padding, the cap, the forced cut at the quietest frame and the
gate's own report of peak, floor and audible material run as they always did. Fixed windows ignore
the detector, because they are the escape hatch for material no detector handles. The shipping
detector is **Silero VAD v5 on ONNX Runtime**, in `Parakeet.Engine.SileroVad`: the graph's contract
— 64 samples of context plus 512 new ones at 16 kHz in, a `[2,1,128]` state through, a probability
out — read from upstream's `utils_vad.py` at the pinned commit and held by the constructor, which
refuses a graph without those names; one CPU thread, in process, and each stream carries a
`Resampler` to 16 kHz because the segmenter runs at the recording's own rate and this is the one
place on the transcription path that ever needed the model's. `FakeSpeechDetector` is the scripted
stand-in the suite drives. The model is a catalogue entry, `silero-vad-v5.1.2` — 2.2 MiB, MIT,
`task: voice-activity` (the fourth task word, shipped with the code that reads it), pinned to a
commit and a digest and installed by `ModelInstaller` like every other weight — with an
`MitAttribution`, the fourth licence shape, whose permission text ships as
`licences/silero-vad-LICENSE.txt`. `--vad energy|neural` on the command line, a checkbox beside the
fixed-windows box in the app, a line in `doctor`, and the segmentation report names what cut the
audio.

**ONNX Runtime is a .NET package again**, for this and nothing else: the choice was in process on
the CPU against the sidecar, and for a two-megabyte model that must score every 32 ms window inside
the streaming segmenter, a round trip per window or a whole-file pre-pass before the decode could
start would cost more than the model does. Two copies ship — the wheel in the Python, the package
beside the assemblies — and `docs/LICENSING.md` carries the obligation for both.

**Measured, on this machine, and the measurement cuts both ways.** On the documentary that raised it
(NDR, 28:49, Vulkan): 285 segments became **342**, the longest **29.4 s → 21.7 s**, segments of
twenty seconds or more **10 → 1**, ten or more 34 → 17, at **RTF 0.0109 → 0.0126**; 416.8 s of
audible material — the bed — was judged not speech and not decoded, and the decoded words fell by
1.4 % (3,436 → 3,387), among them the *yeah / so / thank you* the recogniser had been writing over
music. On the ten-minute podcast the speed figures come from (`csb384-8438.m4a`) the detector is
**slower and cuts longer**: Vulkan RTF 0.0102 → 0.0147 (twice each, text byte-identical across
runs), CPU 0.0823 → 0.0902, and **113 segments became 78**, mean 5.1 s → 7.4 s, twenty seconds or
more 1 → 7 — Silero holds speech open across the short pauses the gate cuts at, and the upstream
thresholds were not tuned against it. Words 1,632 → 1,621. So the detector is the right tool under a
bed and not obviously the right tool for clean conversation, which is why it is an opt-in, the gate
stays the default, and every segment figure already recorded stands. `docs/UNPROVEN.md` has the
tables and what none of them establish.

**What this is not.** Not a word-error measurement — no reference transcript was scored, and the 1.4
% and 0.7 % are word counts, not accuracy. Not tuned — the two thresholds are upstream's defaults
and the podcast result says they may be wrong for conversation. Not a provider comparison — CPU
only, one thread, by decision. And not the subtitle files, which still break cues by character
count; the cue builder was not touched.

**Later the same day — on by default in the app.** The maintainer's call, on the documentary's
table: the window ticks *Neural speech detection* by default whenever its model is installed,
unticks it when the model is not there (a ticked box with nothing behind it is the inert setting the
window refuses to draw), and ticks it again when the model arrives — unless the user has answered
the box themselves, in which case their answer comes back rather than the default. **The command
line is unchanged**: `--vad` still defaults to `energy` and `--vad neural` asks for the detector,
and that is what keeps every recorded figure standing — the harnesses run through the CLI, and a
default that moved there would move `measure-transcribe.ps1`'s segment counts without a word in the
run report. So the detector is the default for a person with a recording and the gate is the
default for a measurement; the podcast table above is why the second half of that is still true,
and "opt-in" in the paragraphs above describes the command line from here on. Nothing was
re-measured, because nothing measurable moved.

**And later still — the command line too.** Asked for in as many words once the app had it, and the
reason just given for keeping the gate there was weighed and set aside: `--vad` now defaults to the
detector whenever its model is installed, so the two routes agree, and what the command-line default
used to protect is protected another way. The default resolves in three sentences — model installed
and loading, the detector runs and stderr names it; model not installed, the gate runs and stderr
says so with the download command; model installed but not loading, the run *refuses*, naming
`models verify`, rather than falling back to the gate under a transcript that would then carry the
wrong provenance in silence. `--vad energy` asks for the gate and says nothing; `--no-vad` loads no
detector at all. What is new beside the default is **provenance**: `TranscriptDocument.SpeechDetector`,
written as `speechDetector` in the JSON and as a row in the Markdown, names what cut the recording —
the gate, the detector with its runtime, or `fixed windows` — so a run report can quote a segment
count with its method, and `measure-transcribe.ps1` and `measure-wer.ps1` take `-Vad` and print the
name. **What it means for the record**: every figure recorded before this is the gate's, and a default
run of either harness from here on is not a re-run of any of them — pass `-Vad energy` to reproduce
one, and read `speechDetector` to know which you got. Nothing was re-measured; the two tables above
are the measurement of the change itself, and "opt-in" in the paragraphs above is history on both
routes.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** Two of the four
by-name skips are the detector's, which skip unless `UINDOSILL_SILERO_VAD` names the graph; run
against it on this machine they pass.

### Built 2026-08-23 — the English is translated a sentence at a time

**Asked for with two screenshots**: the Transcript pane reading "Die erste Zeit…" / "Und ich stand
hier…" / "Meine ganzen Augen…" as three lines at 02:43, 02:48 and 02:53, and the English pane
holding all three in one line at 02:43. The cause was the one the notice under the pills stated
since the morning: the translator was fed the recogniser's segments, one request per segment, and
handed back one English string per segment with no word timings — so there was nothing to cut the
English *by*, and the window never invents a timestamp. Splitting the English after the fact would
have meant guessing times; the fix is to translate what is already cut.

**What was built.** `TranscriptTranslation` splits the source with `SentenceSplitter` before the
translator sees it — the same cut the Ask tab's transcript lines are made with, on the word timings
the model reported — and sends one request per sentence. Each English segment keeps its sentence's
start and end (the first piece the segment's start, the last its end, every time between them a
word's), its `SourceSegmentIndex` and its speaker, and the driver's checks hold per sentence as they
held per segment. `TranscriptTranslation.Units` is that split, and the numeral check in both the
command line and the window now compares against it, so the pairing stays by index. A segment the
splitter leaves whole — one sentence, no words, words that do not reproduce the text — is translated
as before, which is every line `uindosill translate` reads from a text file. The English pane reads
one line per sentence at the sentence's own time; `.en.srt` and `.en.vtt` cues no longer straddle a
sentence end; the English JSON's segments are sentences and pair with the transcript JSON's by time
— each lies inside its source segment's span — rather than one to one, the source index being
carried in the document model and not written to the JSON. The notice under the pills now names
only the word mark as what does not follow across. Driven once on the real path: the ten-minute
podcast cut, Vulkan and WebGPU, exit 0 in 51 s, the detector's 78 segments became 162 English
sentences, two of them cut after `vs.` and `Mr.` — the splitter's documented abbreviation weakness,
and the only thing that read wrong.

**What this is not.** Not measured: no chrF++ and no adequacy check has been run with the sentence
as the unit — the FLEURS figures are per sentence by construction and the cascade penalty was
measured per ASR segment, and whether a shorter input moves either is not established on any file;
`docs/UNPROVEN.md` § *The Ask tab's lines are sentences* says so. Not faster: more requests of less
text each (478 where there were 285 on the documentary's gate segmentation), and the time was not
measured. And not the word mark — a translated sentence still carries no word times, and the English
pane still marks no word.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.**

### Built 2026-08-23 — subtitles and the window's lines drop the sentence-final full stop

**Asked for** the moment the English read by the sentence: "real subtitles don't have dots at the
end." `TrailingStop.Strip` takes exactly one sentence-final `.` off the end of a line — never `?` or
`!`, never an ellipsis, and a closing quote or bracket after the stop stays while the stop inside it
goes — and it is applied at two places and nowhere else. `SubtitleCueBuilder` applies it to every
finished cue, after `Tidy`: the last line, and the last word of `LineWords` with it, so SRT, VTT and
the word-timed VTT write the same text; a stop between two sentences inside one cue stays.
`TranscriptLineViewModel` applies it to the lines the window draws, on both tabs and both panes, and
locates the last word without its stop so the word mark still lands on it. The document is untouched
— it is what the sentence splitter and the word times are computed from — and so are TXT, JSON and
Markdown, which carry the text as the model wrote it. An abbreviation that ends a line on a bad cut
(`Mr.`) loses its stop too; the cut is the defect there. A presentation rule, held by tests; nothing
to measure.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.**

### Decided 2026-08-23 — the translator's `auto` prefers CUDA where the wheel carries it, and IO binding turns out to crash on the machine it was supposed to speed up

The maintainer noticed the translation leaning on the CPU with the GPU nearly idle, and the
diagnosis ran through three layers before it reached a decision.

**What was actually happening was the design working as documented.** The app asks for `auto`,
`auto` resolved to WebGPU, WebGPU builds — no silent fallback; the engine's own registered-provider
check confirms it — and eight Spanish sentences timed at 0.189 s/sentence against the CPU's 0.289,
in line with the published 1.30×. The GPU sat near idle between two-second spikes because the whole
beam search — the loop, the logits work, the beam bookkeeping — runs in torch on the CPU, the wheel
in use ships CPU torch, and IO binding is off, so only the encoder and decoder forward passes reach
the GPU and the KV cache round-trips on every step. Every part of that is in `engine.py`'s own
docstring.

**The desktop has a venv the docstring's escape clause describes** — `onnxruntime-gpu 1.29.0` with
CUDA torch — and CUDA was measured there the same day: **0.142 s/sentence, 1.33× WebGPU, 2× the
CPU**, sessions registered as `CUDAExecutionProvider`, correct English out. The committed parity
fixture then passed **6 of 6 string-identical** on CUDA, against a CPU control in the same venv that
also passed 6 of 6. The fixture is a smoke test by its own docstring; what makes CUDA safe to prefer
is the 2026-08-21 study's **240 of 240** on the gate hypotheses, and the new timing is only what
makes it worth preferring.

**So `AUTO_ORDER` is now `["cuda", "webgpu"]`**, and the reorder costs the bundle nothing by
construction: `resolve_auto` keeps only the providers the wheel was compiled with, the shipped
`onnxruntime-webgpu` wheel has no CUDA, and the shipped app therefore still tries WebGPU first
exactly as before. What changes is a machine running the sidecar on a CUDA wheel — today, the
desktop this was measured on. The 1.65 GB of CUDA libraries stay out of the installer for the same
reason they were never in it.

**The docstring's "a machine that could bind would be faster than that number, never slower" was
tested on the first machine that could bind, and the machine aborted.** `use_io_binding` flipped on
through optimum's own setter, on the CUDA stack above: the first decode step died with `CUDA error
cudaErrorIllegalAddress: an illegal memory access was encountered` in the `/Mul` node, caught by
torch's abort handler as a native process abort — not an exception the sidecar could report and fall
back from. The sentence was true and incomplete: binding would be faster if it ran. The docstring
now says both halves, and binding stays off everywhere.

**The shipping path was then driven by hand the same day**: the built application, launched with
`UINDOSILL_PYTHON` on the CUDA venv, translated a real recording with the provenance line reading
**"on cuda"** — which is stamped from the provider that initialised, not the one asked for — and the
maintainer's one-line verdict was "it says on cuda, and its fast". That is the last link between the
probe numbers and the product: app → sidecar → `auto` → CUDA, end to end. The same session runs the
diariser on the CPU, because that venv's wheel has no WebGPU and the diariser's own order does not
include CUDA — the two engines' GPU preferences do not currently meet in one venv, and that is
recorded rather than resolved.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** The C# suite does
not run any of this; what covers it is the parity fixture at load on a real machine, which is
exactly the arrangement `CLAUDE.md` records for the translator.

### Built 2026-08-23 — Export and Settings tabs, and Licences retires into an About window

The Transcribe tab had grown a right-hand column that was two features stacked on one page: what a
run writes, and how the run is done. Both were asked for by the maintainer to be split out, and the
Licences tab — the fifth page, and the one page nobody opens twice — was asked to go with them, into
an About window opened from Settings.

**Export** carries the output formats, the output folder, and *Add transcript to the recording*, in
that order: what a transcript is, where it lands, and the button for somebody who wanted no files at
all. **Settings** carries how the audio is cut (fixed windows, the segment cap, neural speech
detection) and the two v1 opt-ins that run an extra model — speaker labelling with its count, and
the English version. The 30-second segmentation footnote followed the cap it explains rather than
staying on a page where the control it describes no longer is. Both pages are drawn as reading
pages — one column, `26,22`, a `title` at the top — rather than as the three-column workbenches
Transcribe, Models and Ask use.

**What did not move, and the reason is the same both times.** The backend picker stays on Models
beside the Load button it governs; the launch update check stays on Updates beside the paragraph
explaining it. Both are settings, and both are more use next to the thing they change than in a
drawer with everything else.

**The Transcribe tab says where its column went.** One 10.5px line above the transcript naming both
tabs. A control that moves without a forwarding address is as hard to find as one that was deleted,
and this window's standing rule is that nothing fails silently — a control nobody can locate is a
quieter failure than a control that does nothing. Every one of the five things that left is named
in it; a forwarding address listing four of five is worse than none, because the reader concludes
the fifth was removed.

**One thing the move broke, found in review and fixed before it shipped.** `SpeakerDurationWarning`
is the sentence saying a queued recording is longer than the diariser's labels have been measured
on, and its whole requirement is to be in front of somebody who can still act on it. It travelled to
Settings with the opt-in it sits beside — and the sequence that breaks is the ordinary one: tick the
box and set a count on Settings, come back, drop a two-hour interview, press Start. The warning is
non-null the whole time and was drawn on a page already left behind. The Start guard does not cover
it either, because that raises the bound sentence only when the count is *missing*. It is now bound
twice, on Settings beside the count that repairs it and on Transcribe beside the queue it is a fact
about — one property, two draws, and no reader ever sees both at once.

**Four other things the same review found, all of them prose rather than code.** Three shipped
documents — `LICENSE`, `licences/mpv-WRITTEN-OFFER.txt` and `NOTICE.md` — still told a reader to
open a Licences tab to find out which of two licences governs their copy; the first two travel to
users with the binary through `build/Licences.targets`. Two status messages still said to try
"'fixed windows' below" from a tab that no longer has a checkbox on it. `AddToRecordingNotice`
returned null with nothing selected, which explained itself while the button sat under the queue and
is a dark button with no reason beside it now that the queue is a tab away. And the About window
answered Escape by doing nothing, which — since `ShowDialog` disables the owner — looks like an
application that has stopped responding.

**The About window is the Licences page plus the two things that were hiding under it.** Three
panes: *About* (what this program is, and the network promise the Updates tab also makes), *Licences*
(the same notice package, from the same one builder, now in `AboutViewModel`), and *System* — the
runtime line and the threading note, which had been sitting beneath the licence text where nobody
looking for them would think to go, joined by the version, the models folder, the settings file —
which is a file, and named as one, because the directory holding it is the models' parent — and a
button that puts all five on the clipboard. It wears the main window's chrome exactly: no OS title bar, the same
46px headerbar, the same square corner asked of DWM, the same pill switcher over a headless
`TabControl`. Modal and owned, so there is never a second copy.

**The TabControl's order is not the switcher's, and this is the second time that has paid.** The
pages are 0 Transcribe, 1 Models, 2 Export, 3 Updates, 4 Ask, 5 Settings — Export took the index
Licences vacated, which is what keeps Models at 1, Updates at 3 and Ask at 4, where tests, settings
and `MainWindowViewModel.ModelsTabIndex` already found them. Renumbering four working pages to make
one list agree with the other would have bought tidiness and spent correctness. The switcher reads
**Transcribe · Ask · Export · Settings · Models · Updates**: the work, then the two pages that
configure it, then the library and the housekeeping.

**`MinWidth` went from 820 to 920, and it was measured rather than chosen.** Six pills measure 464px
and the headerbar's two fixed 210 columns plus its 14px padding take 434 more. At 820 the switcher
was handed 384px: a `Border` does not clip its children, so the sunken rail stopped in the middle of
a word while the last pill carried on and drew under the close button — by four pixels, at a width
nobody opens the window at while checking a design. Five pills had fitted with three pixels to
spare, so this was one pill away from happening either way. The new assertion measures the pills'
own ink against the wordmark and the window buttons and fails with the figure to put in the file, so
a seventh pill cannot ship the same defect.

**One thing believed and then measured, which went the other way.** The move was made on the
assumption that a control on an unselected tab does not exist to `FindControl` — the obvious reading
of "a `TabControl` realises only the selected tab", and the reason the first draft named its two
handlers in the markup as `Click="…"` instead of wiring them in the constructor. It is wrong.
Avalonia builds the whole markup tree at load and registers every `Name` in the window's one name
scope, which is what `FindControl` reads; the visual tree is the only thing that is deferred. With
the window on tab 0 and the button on tab 2: `ctor-scope=found after-show=found inVisualTree=False`.
So constructor wiring never broke, the markup handlers came back out, and the file keeps one idiom.
The real trap is the mirror image and it is a testing one — `Assert.NotNull(FindControl(…))` passes
for a control the window never draws, and `Assert.Null` cannot detect a control duplicated onto a
second tab. That is gotcha 31, and the tests that ask about drawing now ask the visual tree.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** Twenty-two new, and
they add up: seven for the About window's three panes and its chrome, six holding every switcher
pill against the page it names — six hand-written converter parameters that nothing else checked —
two for the headerbar measurement above, two asserting each new page carries controls that write
through rather than merely draw, one asserting the Transcribe tab carries none of them and says
where they went, one for the retired Licences tab and the button that replaced it, one more case in
the press-reaches-the-pill theory, one from splitting the English opt-in's test because the checkbox
and the pane switcher it used to be asserted beside are no longer on the same page, and one holding
the long-recording warning beside the queue — see below.

### Built 2026-08-23, later the same day — the two extra passes return to the Transcribe tab

The split above put the two opt-in passes on Settings with the cut; the maintainer moved them back
the same day, and the line between the two pages is sharper for it: **Settings keeps what
configures the machinery — the cut, the detector, the way to the About window — and the Transcribe
tab carries what decides a run.** The two passes change what pressing Start produces for the queue
beside them, and a per-run decision belongs on the page where the run is launched.

Three details of the return. **Translation sits first and speakers last**, so the strip that grows
when ticked — the count field and up to two sentences appear under the speaker box — moves only the
transcript below it, never the other opt-in. **The English box now reads "Translate to English"** —
the action it performs — rather than "English version", the artifact it produces; the Models tab's
load hint quotes the new name. **The long-recording warning is bound once again**: the review fix
above drew it twice because the opt-in and the queue were on different pages, and with the strip
back beside the queue the duplicate came out rather than leaving two copies of one sentence on one
screen.

The forwarding line above the transcript now names only what is still elsewhere — the outputs on
Export, the cut on Settings — and the status messages that pointed at the Settings tab for 'Label
speakers', 'How many speakers' and the English opt-in point at the controls on the reader's own
page instead. **1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** The
count is unchanged: the page assertions moved with the controls rather than multiplying, the order
of the two strips is pinned by drawn geometry rather than markup order, and the English opt-in's
write-through test came back from Settings with its page index.

### Built 2026-08-23, evening — files come from an Export button, and a run only fills the screen

**Transcribing no longer writes files.** The maintainer's decision, the same evening: pressing
Start fills the transcript on screen and nothing else, and the Export tab carries the format
ticks, the output folder, and an **Export** button that writes the ticked files for the recording
selected in the queue. `TranscriptionJob.Formats = []` was already the writer's documented
"in memory only" mode, so the run pipeline did not change shape — the write moved from the end of
every job to a command that runs when asked, against the finished row's document under its current
speaker names, with the English written beside as `.en` files when the run translated.

**Refusals became skips with reasons, and they are better for it.** Start used to refuse RTTM
without the speaker opt-in and a word-timed WebVTT under translation, predicting the document it
did not yet have. The export path holds the finished document, so it answers instead of
predicting: a turns-only format over a transcript with no turns is skipped and the notice says
why, and the English gets no word-timed file — translation carries no word timings — while the
spoken transcript still gets its own. The button's notice is never empty: which recording the
files would be for, why the button is dark, or what the last press wrote.

**The Export pill moved next to Transcribe** — transcribe, then export, one workflow read left to
right — with the `TabControl` indices untouched underneath, as ever. **The output folder now
outlives the application**: saved as it changes, restored at launch only while the directory still
exists, and forgotten — box and file both — when it does not, because the folder people pick is
often a removable drive and a restored path with nothing behind it would aim every export at
nowhere.

**Two things the suite does not cover, said rather than hidden.** The Transcribe tab's transcript
now follows its own tail while a batch fills it — stuck to the end only while the reader was
already there, disarmed by scrolling up, re-armed by scrolling back — and no headless test drives
that scroll geometry. And the Ask tab's transport button lost its grey disabled disc for a pale
taro one; both were checked by launching the application and looking, and neither by an
assertion. **1633 tests, 1624 passed and 9 skipped, the count unchanged**: the file-writing
assertions moved from Start's tests into export presses in the same tests, and the no-format and
RTTM refusal tests became the skip-and-say tests of the same names' subjects.

### Built 2026-08-23 — the uninstall takes the data directory with it, on purpose

**The ask: nothing left behind.** Uninstalling removed the application and left
`%LOCALAPPDATA%\Uindosill` — weights, settings, the Python bundle, gigabytes of it — sitting on
the disk with nothing installed to say what it was or how to remove it. That was the packaging
design working exactly as measured (UNPROVEN.md, 2026-08-19): the data directory lives apart from
the install root so Velopack's recursive uninstall delete cannot reach it, and until now nothing
else reached it either.

**The product now deletes its own data, which is a different thing from an installer deleting a
directory that shares its name.** `Program.cs` registers Velopack's
`OnBeforeUninstallFastCallback` — Windows-only by the library's own annotation, so the
registration sits behind an `OperatingSystem.IsWindows()` guard for CA1416's sake — and
`UninstallCleanup` does the delete inside the hook's 30-second budget. Each guard covers a real
mistake rather than a hypothetical one: a root whose last segment is not `Uindosill` is refused
whole, because a refactor or an override pointing the delete elsewhere should delete nothing; an
install root nested inside the data root stops everything, because that delete would take the
running uninstaller with it; a data root that is itself a reparse point is unlinked rather than
followed, because the link is the product's and its target is not. The walk deletes entry by
entry — `Directory.Delete(recursive: true)` stops at the first refusal, which would let one open
`settings.json` strand the weights beside it — clears the read-only attribute unpacked archives
leave on files, never recurses into a reparse point, and swallows every failure, because no file
under that directory is worth failing an uninstall over. A models directory redirected with
`UINDOSILL_MODELS_DIR` is not touched: the product removes the directory it named, not an
arrangement the user made.

**Six tests, two of them a platform pair.** The whole-tree delete with a read-only file inside
and the install root untouched beside it; the refusal of a directory not named for the product;
the refusal around a nested install root; a missing directory that must end still missing, since
`UserDataPaths` resolves its special folder with `Create` and an uninstall must not end by
planting a fresh empty directory; a locked file stranding only itself, Windows only because POSIX
deletes open files without complaint; and a symbolic link unlinked without emptying its target,
skipped on Windows where creating one takes developer mode. The last two skip on opposite
platforms, like the Media Foundation pair in `Parakeet.Audio.Tests`, so the suite's skip count is
the same number on every machine — which is what lets the documents CI checks quote it.

**One consequence is accepted rather than accidental.** The data directory is shared with the
CLI, which ships as a zip Velopack does not install and the uninstaller cannot see: someone who
runs the standalone CLI and also installed the desktop application loses the CLI's models and its
downloaded Python bundle when the application is uninstalled. Sparing the shared directory in
case a CLI is watching would reopen the orphaned-gigabytes problem for every user to protect an
arrangement only some have. The CLI itself keeps working and meets the emptied directory exactly
as it meets a fresh machine, and the recovery is the same downloads that stocked it the first
time. The gotcha, `docs/MODELS.md` and the README all say so.

**The claims moved with the behaviour.** The README bullet that promised "uninstalling deletes
the first and leaves the second" now promises the opposite for uninstall while keeping the
measured update claim; gotcha 8 carries the design; and UNPROVEN.md records what nobody has seen:
no installer has been packed since the hook was added, so no real `Update.exe --uninstall` has
ever invoked it, and a cleanup that swallows everything by design would look exactly like success
if it silently achieved nothing. The 2026-08-19 procedure rerun on a current build is the proof
that is still owed.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.**

### Decided 2026-08-23 — what v1.0 ships without, and the release that comes first

Four ship questions were put to the maintainer and answered the same day. Two confirm standing
positions; two are new.

**The human adequacy check is declined with finality.** Criterion two of the translation gate —
the 60-row Spanish sheet written 2026-08-20, never rated — was declined that day and recorded as
"queued to nobody"; it is now declined for v1.0 outright. The gate is not redefined to fit: by its
own ratified definition it stays unpassed, the opt-in ships with that stated where the feature is
described (the README already says it), and the sheet stays on disk for whoever ever wants the
missing third. The same pattern as Slovak — ship with the failure named, never with the bar moved.

**Slovak's position is confirmed unchanged.** The 2026-08-20 ratification stands as written: it
fails criterion one by 0.74, the feature ships anyway, and the failure travels with every quote of
the 23-of-24 result.

**NOTSOFAR-1 is scored after v1.0** — an obligation rather than a waiver, recorded under *After
v1* below with what the run must carry and the risk the deferral accepts. The AMI pass is the
whole of what the diariser gate holds at release, and the honest summary now says so.

**The first real release is a public prerelease: 1.0.0-rc.3.** rc.1 and rc.2 were drafts, built
and deleted in the 2026-08-19 rehearsal; rc.3 is a real release marked prerelease. It is the first
time CI produces the bundle asset it has never produced, the first bundle-carrying
installers anyone can download, and the first observation of what the bundle does to installer
size and pack time — all off the release path's own machinery rather than the desktop's. Installed
copies are not offered it: `VelopackUpdater` constructs its `GithubSource` with
`prerelease: false`, and the canned-feed experiment established a prerelease is filtered before it
is a candidate (`docs/UNPROVEN.md` § *The update check has never found an update*). v1.0 follows
once the desktop session has run the rc end to end — installed interactively, transcribed from the
install, uninstalled with the weights hashed and the data directory found gone — and that session
gets a second prize: an rc.3 install watching v1.0 arrive would be the update path's first real
end-to-end run, on the maintainer's machine before any user's. Whether the v1.0 pack can seed a
delta from a prerelease is vpk behaviour nobody has observed; the step to watch is named in
UNPROVEN's release-workflow section either way.

### Published 2026-08-23 — 1.0.0-rc.3, the first real release

The tag went on the same day's decision commit and the workflow took the tag path end to end for
the first time: all steps green in **28m2s** against the bundle-less rehearsals' 7–10 minutes,
with the suite run on `windows-latest` — its only Windows run — and the publish's five-asset
refusal satisfied. The release is up, marked prerelease off the hyphen, eight assets.

**The sizes are observations now rather than plans, and one assumption did not survive.** The
bundle zip CI had never produced is **400.2 MB** — the 1.20 GB the bundle measures unpacked
compresses to a third, so the "~1.2 GB download" the 2026-08-21 decision priced is a 400 MB one.
The default installer carries the bundle at **485.4 MB** against 81.9 MB without it; the CUDA
flavour is **1187.9 MB** against 818.6. The full packages are 481.1 MB and 1183.6 MB; the CLI zip
is 60.7 MB against the rehearsal's 53.9, a growth this entry records without attributing. The
seeding step correctly found nothing to diff against, so this release is full packages only and
the delta path still waits for the second real release, exactly as the record predicted.

**What the release does not establish is everything past the byte counts.** Nothing has been
installed from it: no bundle-carrying installer has ever been run, the interactive `Setup.exe`
dialog remains unseen, the uninstall cleanup hook has still never been invoked, and the bundle
zip has never been unpacked and resolved from on a machine without a checkout. Installed copies
elsewhere are not offered the rc — `prerelease: false` in the updater filters it — so the rc's
audience is whoever downloads it by hand, which is the point: the desktop end-to-end comes next,
and v1.0 does not get tagged until it has happened.

### Fixed 2026-08-23 — installing rc.3 found five defects, and the packaging one had removed three whole features

The first candidate was installed on the laptop the day after it was published. Everything below
came out of that one install, which is the argument for the desktop end-to-end this record has been
asking for since 2026-08-19.

**Three features were missing from the package, and two independent faults put them there.**
`.github/workflows/release.yml` vendored the decoder and the CUDA drop and never called
`vendor-tools.ps1` or `vendor-mpv.ps1`; and `scripts/package-windows.ps1` deleted every directory
under `native/win-x64/` that was not in the channel's backend list, which since 2026-08-23 includes
`tools/`, `ffmpeg/` and `mpv/`. Either fault alone is enough, so vendoring in CI without fixing the
prune would have produced the same package. What shipped could not open a link, could not draw a
video's picture — `MediaPlayers.ForThisBuild()` fell back to `SystemAudioPlayer`, so a recording
played as sound and nobody was told why — and could not put a transcript back into a file. **The
relicensing to GPLv2+ taken on 2026-08-23 to make video possible bought rc.3 nothing.**

Nothing caught it because the read-back asserted `parakeet.dll` and its `LICENSE` per backend, and
those were all present. The prune is now an exclusion list — it deletes only a **named** backend
this channel does not carry, so an unrecognised directory survives — the publish is checked for the
three drops and their notices before packing, and the read-back opens the `.nupkg` and requires each
of them inside it. `docs/GOTCHAS.md` gotcha 32 records the shape: an exclusion list fails safely,
an inclusion list fails silently.

**The Models tab was telling users the opposite of what the application now does.**
`KeptOnUninstallNotice` promised uninstalling would not delete downloaded models. `UninstallCleanup`
had landed the day before and deletes all of them. The behaviour changed, the README, the gotcha
and two other documents changed with it, and the one sentence the *user* actually reads did not —
which is the failure mode this repository has a rule against, arriving by the route the rule does
not cover. It is now `UninstallNotice`, it states both halves — an update keeps the weights, an
uninstall takes them — and a test asserts it does not say "does not delete".

**Two failure messages were written for whoever builds the application.** The link box and the muxer
named `scripts/vendor-tools.ps1` and `docs/NATIVE-BINARIES.md`, on the assumption that only a
developer would ever see a build without the tools. The packaging defect above turned that
assumption into a sentence telling every user to run a PowerShell script from a clone they do not
have. Both now say what is missing in plain words and that reinstalling restores it.

**Superseded in part on 2026-08-26 — the speaker weights left the installer again.** What
follows is the 2026-08-23 decision as it was taken and is kept for its reasoning; the diariser
half of it no longer holds. Speaker labelling now has two models and neither is better, so
bundling either would pick for the user; both are downloads. The speech-detection half stands.
See *Decided 2026-08-26* below.

**Two of the four models now ship inside the installer.** Speech detection is 2.2 MiB and MIT, and
its licence notice was already in every publish — the installer shipped the notice for a file it did
not ship. Speaker labelling is 452.6 MiB, and `docs/LICENSING.md` had already established that the
NVIDIA Open Model License permits redistribution outright with two conditions this project has been
meeting since before it needed to; bundling it removes the linking-versus-distributing question
rather than answering it. `BundledModels` says which entries travel, `PathForInstalledOrBundled`
prefers a downloaded copy over the bundled one so the Models tab keeps meaning something, and the
packaging step verifies each file against the digest `models.json` already pins.

**The other two stay downloads because of arithmetic.** A GitHub release asset must be under 2 GiB.
The recogniser is 1.34 GiB and the translator 1.34 GiB, against a CUDA installer that was already
1187.9 MB: either one alone puts that channel over the limit, and both together put every channel
over it. A test carries the sum so that adding an id to the array is a decision rather than a
release that fails on its last step.

**There was no icon anywhere in Windows**, because there was no `.ico` in the repository — the mark
existed only as XAML geometry in the headerbar, so the taskbar, the shortcuts, Explorer and the
Add/Remove Programs row all drew the placeholder. `scripts/make-icon.ps1` renders that same geometry
— five round-capped bars, the hard matcha/taro seam, the centre bar split down its own middle — to
`brand/uindosill.ico` at nine sizes and to an installer splash, with no imaging library: signed
distance fields for the antialiasing and a PNG encoder over `ZLibStream`. It is committed rather
than generated at build time, because CI must not need a renderer. The exe carries it through
`ApplicationIcon`, the window through `Window.Icon`, and `vpk` gets both it and the splash.

**Velopack stays, by the maintainer's decision the same day.** Its Setup asks no questions by
design; an interactive installer with a directory picker would mean a different installer and a
different update story. What it gets instead is the icon and the splash, so the one window a user
sees during an install is branded rather than bare.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** Six of the new ones
are the uninstall cleanup's from 2026-08-23; the rest are this entry's: which ids may be bundled,
that they are single-file entries the catalogue actually has, that the sum still fits under the
asset limit, that a bundled graph counts as available, and that a downloaded copy wins over it.

### Removed 2026-08-23, the same night it shipped — the uninstall no longer deletes anything

`v1.0.0-rc.3` was installed and then uninstalled to prove the cleanup hook that had landed hours
earlier. It did not work: the install directory, the registry key and both shortcuts went, and
`%LOCALAPPDATA%\Uindosill` was left whole — 43,789 files, 4.64 GB, byte-for-byte the baseline taken
before the run. Velopack's log said `Hook executed successfully (took 98.6846ms)`, which is not
enough time to delete anything.

**Every mechanism that would explain it was tested, and every one was eliminated.**

| Hypothesis | Experiment | Result |
|---|---|---|
| Velopack does not invoke the hook | standalone probe registering the same callback | it does, every time |
| The fluent chain lost the registration | reflection: `VelopackApp` is a class and returns `this` | registration is intact |
| An exception inside the callback | probe that throws | exits **-1**, reported as a failure — not what the log shows |
| A reparse point on the data root | read the attributes | plain directory |
| Missing `VelopackPackageId` metadata | read the built assembly | present |
| **Scale — 43,789 files overwhelming it** | real installer, real uninstall, 43,789-file decoy | **deleted in 6.3 s** |

Scale was the most promising of them and failed like the rest. A full-fidelity reproduction — a
real `vpk` package, a real silent install, a real `Update.exe --uninstall` — deleted the decoy every
time. **The failure never reproduced: invoked directly the same build deleted the entire directory,
and invoked by the uninstaller it returned in 98 ms having deleted nothing.**

**So the feature is gone rather than fixed, and three reasons say it should never come back in that
shape.**

- **That folder holds other people's files.** The Models tab offers to remove "weights from an older
  version of Uindosill, or files put here by hand" — the product knows they are there. An
  uninstaller runs unattended and cannot ask.
- **Uninstall-then-reinstall is the first repair anybody tries**, and the hook made it cost a 3.9 GB
  re-download without saying so. That is user-hostile when it works perfectly.
- **Unpredictable and destructive is the worst pairing a feature can have.** A silent no-op is a
  tolerable failure; taking somebody's disk is not, and nothing here could tell the two apart in
  advance.

**An allowlist version was written and also dropped.** It deleted only what the shipped catalogue
could name — its own entries, `settings.json`, the interpreter bundle — leaving anything
unrecognised and leaving the directory whenever anything remained, with the failure modes made
deliberately asymmetric. It is genuinely safe against the first reason and tests held it there. It
does not answer the second or the third, and a safe version of a feature nobody can explain is
still a feature nobody can explain.

**What replaces it is the Models tab**, where a person sees what is on their disk, what it cost, and
removes what they choose — before uninstalling, with their hand on it. The window's notice is back
to the true sentence, asserted by a test so it cannot drift again: uninstalling does not delete
downloaded models.

**The rule this leaves behind, and it is now the standing one: nothing this application does
unattended may delete a user's files.** `docs/GOTCHAS.md` gotcha 8 carries it where the packaging
reasoning lives.

### Reversed 2026-08-23 — the managed assemblies go inside the executable, and the reason they were not was wrong

`Directory.Build.targets` had turned single-file publishing off since the deployment shape was
settled, with a reason written beside it: *"single-file extracts natives to a temp path that breaks
the backend directory layout"*. **That describes `IncludeNativeLibrariesForSelfExtract`, which is
off by default and which nothing here sets.** Publishing with it on and looking at the output
settles it: the five native libraries that arrive through NuGet — SkiaSharp, HarfBuzz, ANGLE, ONNX
Runtime — stay beside the executable, and `native/<rid>/<backend>/` is untouched, because those
files are copied in by `build/NativeAssets.targets` rather than bundled. Nothing extracts anywhere,
and `AppContext.BaseDirectory` still resolves to the executable's own directory, which is what
`BundledTools`, `BundledModels` and `PythonRuntime` all search from. `uindosill doctor` run from a
single-file publish reports that directory and finds everything it did before.

**The cost to updates was the real question, and it goes the other way.** Velopack builds a delta by
diffing against the previous package, so the expectation was that relinking a 98.5 MB executable on
every change would make every user's update enormous. Measured by packing two versions with a
one-line source edit between them and reading what `vpk` produced:

| | delta | full package | files in the publish |
|---|---|---|---|
| loose assemblies | 84,365 bytes | 209,965,214 bytes | ~200 |
| single file | **18,518 bytes** | **206,751,439 bytes** | **34** |

The update every user downloads is **4.6x smaller**, and the full package is 3.2 MB smaller besides:
zstd diffs a relinked bundle well. Fewer loose assemblies beside the executable is also fewer things
that can be side-loaded in place of one.

**Two checks were keyed to the old shape and moved with it.** `scripts/package-windows.ps1` and
`ci.yml` both proved self-containedness by finding `hostfxr.dll` and counting at least a hundred
files — and single-file removes both signals, since the runtime is inside the executable and the
publish is a few dozen files. They size the executable instead: about 98 MB with the runtime in it
against a couple of megabytes without, which is the thing that actually differs. Trimming and
NativeAOT stay off, and those reasons are unchanged — trimming cannot see through P/Invoke.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** No test changed: the
suite builds without a RuntimeIdentifier, so nothing in it publishes single-file, and what this
changes is the shape of a deployment rather than the behaviour of any code.

### Built 2026-08-23 — v2 begins: a transcript reopens, the seek bar takes the keyboard, and retrieval exists with nothing to retrieve for

The first v2 work, and none of it touches a model — which is the point of the order it was built
in. The register (`docs/V2-ASK-THE-TRANSCRIPT.md`) holds the decisions; the maintainer's revised
implementation plan, kept outside this repository until v1.0 ships, holds the stages; and the
stages it names as buildable with no language model in the room are what landed here.

**A transcript written in an earlier session can be reopened.** `JsonTranscriptFormatter` had no
inverse — checked and recorded in the plan — so a chat could only ever have been had against a
transcript from the current session's queue, and a pinned transcript could be hashed but never
loaded. `JsonTranscriptReader` is that inverse: it reads exactly what the formatter writes,
skips properties it does not know (the format has grown fields all its life, and a reader that
refused a newer file would punish exactly the transcript it exists to reopen), refuses malformed
structure with a `FormatException` naming the segment at fault, and refuses a compute backend it
cannot represent rather than silently dropping provenance. Derived values — `text`,
`realTimeFactor`, each segment's `conf` — are recomputed, never read, so a file whose derived
figures disagree with its own segments cannot smuggle the disagreement in.

One property came out of the round-trip test sharper than it went in. The file's stated
resolution is a millisecond; a live document's ticks are finer; so the first write is where that
precision is shed, and `Format(document)` is not byte-identical to `Format(Read(Format(document)))`
— the derived real-time factor recomputed from a rounded `processingSec` can move in its last
digit. What the export pin actually needs is the fixpoint from the *file*: a transcript reopened
and rewritten hashes identically, and that is what the suite asserts. Times read back exactly —
the reader parses seconds as `decimal` and multiplies to ticks, where a pass through
`double` would land `1.234` a tick off.

**The seek bar answers to arrow keys.** The cost recorded when the Ask tab's strip was chosen
over a `Slider` was that keyboard seeking did not exist: a `Border` takes no focus, so no key
could ever reach the bar. The strip is now focusable, a press hands it the keyboard along with
the seek, and the keys do what a transport's keys do — five seconds an arrow, thirty with Shift,
Home and End to the two ends. The step scrubs without changing whether the recording is playing,
and both halves are tested through the keyboard on the real window, because the handler that
turns a key into a step is the thing under test.

**Retrieval exists, with citations checked by machinery rather than trust.** Stage 2 of the plan,
`Parakeet.Core` only, no new dependency:

- `TranscriptWindowBuilder` cuts a transcript into ~60 s windows of contiguous segments at 50 %
  overlap (a 120 s variant beside it, for the comparison run the register names). A window
  carries the 1-based segment ids it spans, so **a retrieved window is a citation by
  construction** — the suite asserts that resolving a hit's `CitationId` lands on the window's
  own times.
- `Bm25Retriever` behind `IRetriever` — hand-rolled, k1 = 1.2, b = 0.75, Lucene's
  never-negative idf, ties kept in transcript order so recall runs are repeatable. About two
  hundred lines, as the register's arithmetic priced it.
- `SearchTokenizer` is the one definition of "the same text" that indexing and quote-checking
  share: letter-and-digit runs, lower-cased invariantly so the machine's locale can never change
  what retrieval finds. Unstemmed on purpose — the register wants stemming's contribution to
  recall measured, not assumed.
- `Citation`, `AnswerParser`, `CitationValidator` and `AnswerDocument` implement the register's
  rule that the model never writes a timestamp. Ids are opaque and 1-based; the parser keeps the
  raw spelling even when it parses, takes a bracket group as citations only when everything
  inside it parses as ids — `[laughs]` stays prose, inert — and treats `NOT_IN_TRANSCRIPT` and
  `[?]` as the first-class outcomes the grammar admits. The validator resolves, checks
  non-emptiness, the recording's end, the verbatim quote as a normalised token-boundary
  substring of the cited span, and monotonicity only where chronology was claimed; an
  unresolved citation carries no times at all, so nothing unresolved can ever render as a
  number a reader might trust. `AnswerDocument` carries the provenance `TranscriptDocument`
  taught this repository to carry — model, quantisation, backend, and the mode that says how
  much of the recording the answer could have seen.

What none of this claims: a recall figure. recall@10 needs the thirty labelled CSB384 questions,
and the labelling session is a person's — the one Stage 0 item no script can do. The register's
decision 3 order stands: tier 0 first, the thirty questions, and an embedding model only if the
paraphrase questions say so.

**The engine seam exists ahead of the engine.** `IAnswerEngine` is the one abstraction the app
will know — capabilities, an idempotent load, a streaming ask with prefill progress, because the
prefill is the wait that matters: 467.9 s measured for the full transcript on the laptop's
Vulkan path, and a panel that cannot show it is a panel that looks hung. The stream is raw model
text: `AnswerParser` stays the single place structure comes from, so an engine never gets to be
a second parser with a process attached. `FakeAnswerEngine` is `FakeTranscriptionEngine`'s
counterpart, and it fakes the honest behaviour rather than the convenient one — it abstains on
an empty transcript and on empty retrieval evidence, cites only the windows it was handed and
refuses evidence that belongs to some other transcript, streams a verbatim quote the validator
can actually verify, and keeps one `[?]` bullet so a renderer's uncited state cannot go
unbuilt. The suite drives the whole seam end to end with no model: fake stream → the parser →
the validator → every citation resolves against the transcript it was asked about.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped**, up from 1144;
`check-test-counts.py` agrees with every document that quotes a count.

### Built 2026-08-23, later — the second native stack is vendored and the engine runs on it

Stage 3 of the maintainer's revised plan, the part a laptop can do. In one evening the language
model went from a decision register to a child process the product path can actually start.

**The reading was re-pinned before anything was vendored**, as the plan requires. The b10448
reading was eight days and 155 builds old; the day's release is **b10603**, same asset shape,
byte-identical cudart — and one new trap recorded in the register: upstream now marks its build
releases as prereleases, so GitHub's `releases/latest` answers with something that is not a build
at all. The four issues the design leans on (#26609, #21831, #27007, #26704) were re-read the
same day; all four stand as written, so every workaround the register carries survives the
re-pin unchanged.

**`scripts/vendor-llm-natives.ps1`** (`lab.ps1 vendor-llm`, the seventeenth script) is
`vendor-natives.ps1`'s shape applied to the second stack, with its three differences stated
rather than discovered: the drop is pruned to the server set, because a llama.cpp zip carries a
dozen lab tools and `build/NativeAssets.targets` globs every `native/**/*.exe` into every build
output; the MIT text is fetched from the source tree at the pinned tag, because no release zip
ships one; and there is no inner byte-count pin, because the archive digest — the releases API's
own `digest` field, re-hashed locally — transitively pins every inner byte. The digest table at
the end of `docs/NATIVE-BINARIES.md` gains the llama.cpp section, and the script fails any run
whose trusted digest is not in it, exactly as the parakeet script does.

**`Parakeet.Engine.LlamaServer`** implements `IAnswerEngine` over the child: locate the drop
best-backend-first and say which was taken, start `llama-server` on a loopback port with an
api-key and `--fit off` (the register names `--fit on` as a way to be fooled), `/health` before
the first request, stream the ask over SSE with the prefill progress the panel will render, and
kill on stop — the one unload that cannot leak, measured when the spike watched the adapter
return to idle. The kill-on-close job moved from the Python engine into `Parakeet.Core.Hosting`
on the way, because two copies of a kernel interop is how one of them gets a fix the other does
not; the sidecar now shares it with the server. The prompt and the GBNF grammar are built
together in `AnswerPromptBuilder` — the grammar enumerates each evidence window's own citation
id literally, so an id that is not live is not discouraged but unsamplable — and the engine
never parses: its stream is raw text, and `AnswerParser` stays the single place structure comes
from. On Vulkan the child's environment carries `GGML_VK_DISABLE_BFLOAT16=1` by default, a lab
knob promoted to product behaviour because a hang at load is strictly worse than bf16 being
unavailable; Stage 0.1's driver experiment is what retires it. `uindosill doctor` gained the
tier's section — which drops are vendored, which would run.

**And it ran, on both of this machine's backends, through the product path.** A gated
integration test — the suite's fifth self-skipping test — drives the whole seam against the real
server and a pinned 0.6B: load, health, a grammar-constrained ask, stream, parse, validate,
kill. It passed on cpu and on vulkan the day it was written; what it measured, including the
grammar's id guarantee holding while a 0.6B invented its verbatim quotes, and the sentinel
smuggled into prose when the first-token abstain window is missed, is in `docs/UNPROVEN.md` §
*The engine on the product path*. What has run nowhere is still CUDA: the cuda-13.3 zip is
pinned and unvendored, its `sm_120` reading is a scan of the b10448 build, and the desktop's
first run remains the corroboration the register has been waiting for since 2026-08-16.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.**

### Decided 2026-08-24 — the model sees the English pane

The first of the three register questions v1's changes created is taken, by the maintainer: an
ask against a translated recording runs over the English translation, whole — windows, grammar
ids, quote checks and validation all against the translated document. Recording it turned up a
premise worth correcting: the plan's revision had said the two panes share segments one-for-one,
and they do not — the translated document is the original cut into sentences, one English
segment per sentence with its own guarded times — so an id minted against the English resolves
only against the English, and a pinned artefact made against a translated ask pins the
translated document. The register's new decision block carries the consequences; `AskRequest`'s
own contract now states the rule where Stage 4's wiring will read it. Still open from the same
list: whether evidence lines carry speaker labels, and where the transcript's language comes
from.

### Decided 2026-08-24 — evidence lines carry no speaker labels

The second of the three, taken by the maintainer the same day: the conservative default is now
the decision. An evidence line is the citation id and the text, nothing more, so the model is
never in a position to attribute speech and *who said it* keeps its shape — a range and a quote,
or a refusal, never a name — while the render answers the question anyway, because a resolved
citation scrolls to cues that already wear their speaker chips. `AnswerPromptBuilder` already
built exactly this line; what changed is that its remark now cites a decision rather than a
default awaiting one. Of v1's three questions, only the language source remains open.

### Decided 2026-08-24 — the transcript's language is the request hint, or nothing

The last of the three, and with it the register's list of questions v1 created is empty:
`TranscriptDocument.Language` stays the `-l` hint or null, nothing detects a language the user
did not state, and null means the prompt makes no claim about the answer's language — R6 read
honestly as enforced where known, silent where not, because a check against a language nobody
recorded would measure an invention. The hint survives translation (the English document is
built with a record `with` expression), so the one document a translated ask runs over still
carries it. No code changed hands: the prompt builder already emitted the language line only
when given one, and the decision is what makes that the design rather than the accident.

### Built 2026-08-24 — the chat panel goes live, and the covered controls finally answer

Stage 4 of the maintainer's revised plan: the wiring, exactly as the plan called it, because the
tab, the player and the queue existed and every decision the panel waited on had been taken the
same day. The panel that spent two days drawn, disabled and covered now asks.

**The shape.** A question goes to `AskChatViewModel`, which retrieves evidence windows over the
ask's one document — the English pane on a translated recording, the transcript as spoken
otherwise — hands them to whatever stands behind `IAnswerEngineProvider`, streams the model's
raw text into view as visibly provisional, and then replaces it with what the parse and the
validator made of it: bullets whose citation chips carry times the application resolved from the
cited segments. A chip seeks and plays through the same transport a clicked cue uses; an
unresolved citation renders as `[?]` and seeks nothing; a verbatim quote the check could not
find in the transcript is shown with a caveat rather than trusted or hidden. The abstention is
one sentence. Under every answer sits the line that says which model generated it and that it is
not transcribed speech, and a Sources expander holds what the model was shown, in rank order.
"Copy answer" renders decision 5's form: the marker line first, plain timestamps — never
clickable references — quotes only where verified, and both models' provenance with the date.

**The model comes from a file, not a catalogue entry.** Decision 2 keeps the model question open
until the CSB384 measurements run, so nothing is recommended and nothing downloads on this
application's advice — but a person with a GGUF of their own drops it into the models folder
(the About window names it) and the panel comes alive; where several are present the largest is
served and the model line names it. When the panel cannot work — no engine in the build, no
model file, no transcript — the same cover that said "work in progress" for two days says which
prerequisite is missing instead.

**Residency is enforced, in both directions.** R9's decided half: the first question unloads the
transcription model through the same `ModelSession` the other tabs share, with a line saying so.
The reverse — a transcription starting mid-chat — was left a recommendation by the plan, and it
is now the implemented behaviour: the language model's child is killed, fire-and-forget from the
`IsRunning` wiring because the handler cannot await, and the chat says the next question reloads
it. Best-effort during the handoff instant, recorded as such rather than promised as more, and
open to reversal if the maintainer reads the symmetric rule differently.

**What is proven and what is not.** Thirteen new view-model and window tests drive the whole
seam against `FakeAnswerEngine` and the fake player — streaming to bullets, chips seeking, the
English-pane rule observable from the quote's language, R9 in both directions, the abstain path,
copy's marker-first form, the three cover states, Enter and the suggestion chips — and the real
engine behind the same seam ran under its gated integration test. What has still happened on no
machine is the exit criterion itself: a human asking three questions of a real transcript on
Windows and following a citation into the audio. `docs/UNPROVEN.md` says so where the tab's
other unlooked-at work is recorded.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.**

### Built 2026-08-24 — the ask tier ships: Stage 5, and the second stack joins the channels

The last stage of the maintainer's revised plan, and the one that is mostly other files gaining
llama.cpp entries. What changed where:

**The channels.** `package-windows.ps1` gained a second channel table — `$llmBackendsFor` — and
both channels carry the vulkan `llama-server` drop under `native/win-x64/llm/vulkan/`. Two
decisions sit beside that table rather than inside anyone's head: no separate `llm/cpu` drop
ships, because upstream builds with `GGML_BACKEND_DL` and the vulkan drop carries every per-ISA
CPU variant — whether the server really falls back to them on a broken Vulkan driver is an
UNPROVEN marker, not an assumption — and no `llm/cuda` ships, because its cudart-13.3 beside the
ASR tier's cudart-12.8 is a second CUDA runtime major and the maintainer's open decision, not a
packaging line item. The prune, the presence check and the nupkg read-back all learned the
second stack on the same terms as the first: exactly the promised drops, no others, and
`llama-server.exe` with the MIT text inside the package or the build fails.

**The notices.** llama.cpp joins `Attributions.Components` — rendered by `uindosill notice` and
the About window, held by a suite test to its copyright line and the travelling text —
`NOTICE.md`'s component table, and `docs/LICENSING.md`, whose new section records the one
wrinkle: no release zip carries a LICENSE, so the MIT text is fetched from the pinned tag,
digest-checked, and written beside the binaries where the build and the packaging checks already
insist on it. `docs/MODELS.md` says why the ask model is a file the user supplies and not a
catalogue entry — decision 2 forbids recommending a model nothing has measured — and the README's
v2 bullet stopped saying the asking is not built, because it is.

**The non-goals, where a user looks.** The panel's empty-state blurb gained R13's second
sentence: everything runs on this computer, the conversation is not saved — copy an answer to
keep it — and the model never says who spoke. Three promises about what the panel does not do,
in the place someone meets it.

**What this stage did not do**, so nobody reads more into it: no release was tagged, so the
channel sizes users will meet are unobserved until the next one — rc.3's figures predate the ask
tier entirely — and the win-cuda channel's ask tier runs on the vulkan drop until the cudart
decision is taken. The packaging path itself ran once on the laptop, default channel with
`-SkipPython`, end to end through the nupkg read-back — the first time the second stack's checks
held against a real package. What that one run observed: the read-back listed `cpu, vulkan`,
`llm/vulkan`, all three companions and both bundled weights inside the package, and the
python-less nupkg was **731,391,055 bytes** with a 735,861,327-byte Setup.exe — not comparable
to rc.3's 485.4 MB installer, which carried the Python bundle but predated the bundled weights
(455 MB of the difference) and the ask tier; the next tagged release is where the real channel
sizes get their first observation.

### Measured 2026-08-24 — the desktop's first CUDA execution, and the reading becomes a run

The corroboration the register had been waiting for since 2026-08-16, on the RTX 5080. The
cuda-13.3 pair vendored under `native/win-x64/llm/cuda/` with both digests reproducing;
`vendor-cuda.ps1 -InspectOnly` reading the same architecture list out of the b10603
`ggml-cuda.dll` that the b10448 scan predicted (`sm_120` among the cubins); the gated
integration test passing on `ComputeBackend.Cuda` in about a second of test time on the
machine's first-ever CUDA execution; and the spike putting the same fact mechanically — two
starts to `/health` in 1.12 s and 1.03 s with the driver's JIT cache disabled, which a PTX-only
backend could not do. The first VRAM figures this project has had arrived with it — the 0.6B
holding 5,415.7 MiB of the card at a 40,960-token f16 cache, the adapter returning to its idle
figure exactly on the kill — and the desktop's idle hold itself, 2,115 MiB, went into the
machine block as decision 4's largest unmeasured term made a measurement. `docs/UNPROVEN.md`
§ *The engine on the product path* carries the run; `runs/20260824-032020-spike-cuda` is the
evidence. What this did not measure: anything at depth, and anything about answer quality —
the 9B and the transcript are the next sitting.

### Measured 2026-08-24, later — the 9B at depth, and the desktop tier gets its numbers

The next sitting was the same one. The desktop produced its own f16 reference transcript
(`runs/csb384-f16` — CUDA, the neural detector, 1,023 segments, RTF 0.0092, the labelling
session's pin target), decision 2's first file was pinned against the hub's LFS oid and
downloaded (`Qwen3.5-9B-Q8_0`, 9,527,502,048 bytes, hash matching), and the spike ran it at
`-c 53248` on CUDA: **the whole three-hour transcript prefills in 7.93 s** — 6,017.7 tok/s
over 47,721 tokens, against the laptop's 467.9 s on its own encode — **decode holds 75.4 tok/s
at full depth**, a cached follow-up costs 40.7 ms of prompt time, and `/health` arrives in
3.6 s with the JIT cache disabled. ggml's allocation lines give the register's decision 4 its
first per-buffer data — 8,045 MiB of weights, a 1,664 MiB KV cache that lands the register's
arithmetic to the MiB, 201 MiB of recurrent state, a 140 MiB compute buffer against the
1.5 GiB allowance — and the card holds ~11.7 of 16,303 MiB with the model resident. The
`--reasoning-budget 0` finding reproduces on CUDA. `docs/UNPROVEN.md` carries the run with its
caveats: one run per figure, no answer-quality claim, and no question through the panel yet on
any machine. What follows for the product: the whole-transcript path the register priced as
minutes-with-a-progress-bar is an eight-second wait on the desktop tier.

### Measured 2026-08-24, later still — the Vulkan fallback priced, and the cudart decision gets its number

The win-cuda channel ships the vulkan drop for the ask tier until the cudart-13.3 decision is
taken, and what that fallback costs an NVIDIA card had been measured nowhere. Now it is: the
vulkan drop vendored on the desktop (digest reproducing), the same 9B, transcript and flags as
the CUDA run, `GGML_VK_DISABLE_BFLOAT16=1` in the child because that is the engine's shipped
default — and it loads and runs under it on this driver. **CUDA buys 2.40× on the prefill
(7.9 s against 19.1 s on the whole transcript), about 9 % on decode (75.4 against
69.2 tok/s), and about two seconds on the load**; VRAM within 200 MiB of each other, the
adapter back to idle on the kill on both. The +391 MB question is now priced rather than
blind, and the Vulkan column read alone says the fallback is not a degraded mode: a 19-second
prefill and a 69 tok/s conversation. One run per figure, this card and driver only —
`docs/UNPROVEN.md` § *The same file over Vulkan on the same card* carries the table.

### Built 2026-08-24 — `uindosill retrieve`, and recall@10 stops being a stub

The one piece of engine-stage work the register's decision 6 left named: `measure-answers.ps1`
could score everything about an answer except the retrieval under it, because tier 0 lives in
`Parakeet.Core` and a BM25 reimplemented in PowerShell would measure the script's tokenizer
rather than the product's. The verb closes that the only honest way — by exposing the panel's
own construction. `uindosill retrieve <transcript.json> -q "…"` cuts the transcript with the
same `TranscriptWindowBuilder`, indexes it with the same `SearchTokenizer` and scores it with
the same `Bm25Retriever` the Ask panel retrieves evidence through, and prints the ranked
windows wearing their citation ids — `--json` for the script, `-k` for the depth (default 10,
the register's measurement; the panel hands the model 8), `--wide` for decision 3's 120 s
comparison variant. Empty retrieval returns an empty list and exit 0, because it is the abstain
path's input and not an error. The script now runs it over every labelled question that carries
gold ranges and prints recall@10 per kind — global excluded as the router's path and a person's
judgement, needles excluded because their hit rate is the model's citation — and the summary's
stub line is gone. Driven end to end on the desktop against the f16 transcript with a
four-question harness-validation set (synthetic labels, no quality claim; the recall path, the
needle plant, the abstain row and the new summary block all exercised against a real 9B on
CUDA). What the verb does not change: the recall *number* still waits on the thirty labelled
questions, which are a person's session. Eight new CLI tests hold the seam — the top hit
carrying the term's segment, order preserved across questions, the wide variant's shape, empty
retrieval as success, and the three refusals. **1633 tests, no weights, no display, no network —
1624 passed and 9 skipped**, up from 1262; 186 CLI tests.

### Decided 2026-08-24 — four decisions close the sitting

Taken by the maintainer in one review, each where a session had surfaced it, and each recorded
where its question was posed:

1. **The win-cuda channel ships `llm/cuda` — alone.** Taken with the cost priced first (the
   Vulkan fallback measured the same sitting: CUDA buys 2.40× prefill and ~9 % decode for the
   CUDA pair's ~537 MB of archives against vulkan's 34 MB), and executed as cuda-alone because
   `LlamaServerLocator` takes the best backend *present* with no driver probe and no product
   surface picks the ask tier's backend — a vulkan drop beside the cuda one would be bytes
   nothing could run. `package-windows.ps1`'s channel table carries the decision where it is
   enforced; `docs/NATIVE-BINARIES.md` and `docs/UNPROVEN.md` carry the record. No release has
   shipped any ask tier yet, so the next tag observes both channels' real sizes for the first
   time.
2. **rc.4 is tagged after the exit-criterion run**, not before: a release whose headline feature
   no human has exercised is the risk the ordering avoids, and the run — three questions through
   the Ask tab, a citation followed into the audio — is staged and takes minutes.
3. **R9's reverse direction is ratified as the symmetric kill.** The implemented behaviour — a
   transcription starting mid-chat kills the language model's child, best-effort during the
   handoff instant — stops being a working reading awaiting review and becomes the design. The
   register's decision 4 and the view-model's remark now cite the decision.
4. **The labelled question set lives on the Drive only.** Thirty verbatim quotes from a podcast
   do not go into a public repository; the in-repo `questions.json` stays a template
   permanently, the suite validates the shape both states share, and `measure-answers.ps1
   -QuestionsPath` takes the fetched labelled copy. The v1.0 research homecoming meets the file
   there when it arrives.

### Met 2026-08-24 — the Stage 4 exit criterion: a human asked, and the citations played

The last open claim of the revised plan's Stage 4 closed the way it was written to close: not
by a test but by a person. The maintainer, on the desktop, against a development build carrying
the llm/cuda drop and the 9B served from the models folder, asked three questions of a real
transcript through the Ask tab and clicked citations that played the audio. That is the whole
criterion — the click-through into the recording is what every rule in the register (opaque
ids, the grammar, the validator, the resolve-or-`[?]` render) exists to make trustworthy — and
it is a human's report by design, so nothing was counted or timed. `docs/UNPROVEN.md`'s chat
panel section records what the run established and the three things it deliberately did not:
whether streaming reads as provisional, the real clipboard, and an installed package's panel,
which the first rc.4 install can close. With the criterion met, rc.4 is unblocked by the
maintainer's own ordering, decided earlier the same day.

### Fixed 2026-08-25 — the suite was writing the maintainer's own settings file

Read on the laptop rather than reported by anything: `%LOCALAPPDATA%\Uindosill\settings.json`
held `"outputDirectory"` pointing at a temporary directory whose name — the
`Directory.CreateTempSubdirectory("uindosill-vm")` shape — belongs to a test helper, and the
path had changed between two readings hours apart, so it was current behaviour rather than a
fossil. `MainWindowViewModel` and `UpdatesViewModel` each turn a null `AppSettingsStore` into
`new AppSettingsStore()`, which is the real file; around thirty tests construct the window's
view model to reach a tab underneath it and none of them passed a store. Each of those wrote
that file twice — once at construction, where a stored output directory that no longer exists
is cleared, and again wherever the test chose a folder — and the suite reported nothing, because
nothing had gone wrong from its point of view.

The repair is not thirty call sites. `tests/Shared/TestUserData.cs` is a module initializer
compiled into every test project by `tests/Directory.Build.props`: before any test runs it takes
a temporary directory for the process and points `UINDOSILL_SETTINGS_PATH` and
`UINDOSILL_MODELS_DIR` into it, unconditionally, so a defaulted store cannot reach a real path
whatever a future test forgets to pass. Three tests hold it — that the two names are the ones
the product reads, that a store given nothing answers inside the redirect, and that the
construction which leaked now saves where it should — and one existing test was reading the
override where it meant the default, so `ModelsAreNotStoredInTheInstallDirectory` now asks
`LocalModelStore.DefaultRootDirectory()` the question it was written to ask. Proved by the run:
the file's SHA-256 is unchanged across a full suite that previously rewrote it. **1633 tests, no
weights, no display, no network — 1624 passed and 9 skipped.** `docs/GOTCHAS.md` gotcha 33
carries the shape.

Two 7-byte files, `decoy-a.gguf` and `decoy-b.onnx`, were found beside the weights in the same
directory and removed. They are **not** this defect and were not left by the suite: no test in
the repository names them and no commit in its history ever has, so they came from a script run
outside the tree. The redirect forecloses the models-directory half of the hazard regardless,
which is why it sets both variables rather than only the one that was proved.

### Built 2026-08-25 — the whole-transcript path, the opt-in decision 3 promised

The register's decision 3 ends with a sentence that had no code behind it — "the
whole-transcript path is an opt-in with a progress bar" — and a global question asked of the
retrieval tier is the felt version of why: BM25 ranks windows by how much they look like the
question, and *give me a summary* hands it nothing to rank on, so the model summarises eight
effectively arbitrary minutes and the answer reads fine. The opt-in now exists: a second
Settings toggle beside think-before-answering, persisted as `askWholeTranscript`, off as
shipped because retrieval remains the fast path everywhere and the laptop tier by measurement.

Three shapes carry it. The evidence is the recording tiled once —
`TranscriptWindowOptions.Cover`, non-overlapping 60 s windows, because retrieval's half-overlap
exists for hits near window edges and a prompt that carries every window would carry every
neighbour too, sending the transcript twice. The context is sized to the recording —
`AnswerContextBudget` turns the prompt's characters into the child's `-c`, floor 16,384 (the
retrieval tier's unchanged default), and the panel rebuilds the engine exactly when that figure
changes, so entering the mode on a long recording pays its KV once and leaving it shrinks back
rather than holding a whole-transcript cache for retrieval questions. And the contract
tightened underneath: empty evidence now abstains mechanically in *every* mode — the fake used
to build its own windows when a whole-transcript ask handed it none, which made it more
forgiving than the real engine, the exact shape of fake that let two v1 defects through.

Provenance follows the mode: the answer's model line says "from the whole transcript" where
retrieval says "from retrieved parts of the transcript", the Sources expander stays empty (the
source is the entire recording, already on screen), and `AnswerMode` rides the request and the
parsed answer end to end. What this deliberately does not build: the router that would send
global questions here by itself — a question's kind is the user's call via the toggle until the
labelled set exists to measure a classifier against — and the map-reduce mode, which remains
the laptop's eventual answer for long recordings, where a whole-transcript prefill is measured
in tens of minutes. Answer quality in this mode is unmeasured and `docs/UNPROVEN.md` says so.

### Built 2026-08-25 — the ask model becomes a choice, and unchecked quotes say so

Two decisions taken by the maintainer on the same day's measurements, both about the panel
telling the truth about itself.

**The model is picked, not inferred.** The Ask panel served whichever `.gguf` in the models
folder was largest — predictable, and not a choice. The same day priced what the difference
costs on this hardware: a 9B answered three retrieval questions 2.3× faster than the 26B
mixture, and the mixture's citations held up where the 9B's mostly went unchecked. Settings
gains a model picker listing what is on the disk, largest first, over a row for letting the
application choose, which stays the shipped default. The setting stores a **file name, not a
path** — the folder can move between installs, and a stored path to a deleted file is a setting
that fails silently — and a name that no longer matches anything falls back to the largest while
the picker keeps showing the stale name, so the setting explains itself instead of reverting to
a row nobody chose. The list is re-read when the Settings tab opens, on the same principle the
Models tab already follows: that disk is not this application's alone to write.

**Quoted words nobody checked now say they were not checked.** A model that writes its quotes in
ordinary marks rather than the `«…»` the prompt asks for — measured on seven of one model's ten
bullets — produced text that read as quoted, beside a citation chip, with nothing saying the
check had never run. That is the "unverified text dressed as transcript" this panel exists to
refuse. The bullet now carries *"the quoted words here were not checked"*, in the panel and in
the copied email. Saying rather than checking is the deliberate half: guessing that a quoted span
was meant as a transcript quote would verify more bullets and would eventually accuse a title or
an aside of not being at its cited time, and `false` is reserved here for checked-and-failed.

### Built 2026-08-25 — the router, and the toggle becomes a three-way setting

The whole-transcript opt-in lasted a day. What ended it was using it: a pointed question — "when
did they mention money?" — asked with the toggle on came back wrapped in a framing sentence and
section headings, because in that mode the overview instruction applies to every question. That
is the opt-in's other failure mode, and between the two of them a person has to know which tier
serves which shape of question before they can ask one.

`QuestionRouter` in `Parakeet.Core` decides it from the question, in the shape decision 3
sketched, with the model deliberately not the classifier — routing decides the context the child
is started with, so a model-based classifier would have to load a model in order to decide how to
load it. Two rules: an explicit whole-recording cue (stems and phrases), then the rule that needs
no vocabulary — every term present in at least half the windows, which is the mechanical form of
"nothing to rank on", since at half the windows BM25's classical idf reaches zero. Everything else
retrieves. A term the recording never uses is deliberately *not* ubiquitous: naming something
absent is a pointed question whose honest answer is retrieval's abstention in milliseconds, not a
seventeen-minute read to reach the same conclusion.

Two consequences were designed rather than inherited. The automatic path will not start a long
read unasked — the whole transcript must fit the context the retrieval tier already allocates, so
nobody is committed to a bigger cache or a longer prefill than the tier they were on — and above
that it retrieves and says why. And when that fallback finds nothing, which it usually does
because the words of a summary request appear in no transcript, the panel explains instead of
abstaining: "the recording doesn't answer that" is a claim about the recording, and the truth
there is a claim about the tier. The abstention stays reserved for a question the right tier
really could not answer, which a test now holds.

The setting is three-way — decide from my question, the parts that matched, the whole transcript
— shipping on the first, and the one-day-old boolean migrates by whether it carried a choice: a
stored `true` becomes the fixed whole-transcript setting, a stored `false` was the untouched
default and becomes automatic. Against the real 16:50 transcript the router sent all three
questions this session had already hand-checked the way the hand check says they should go.
Nothing about its accuracy is measured, the cue list is English, and `docs/UNPROVEN.md` says so.

### Built 2026-08-25 — the expert placement follows the graphics, and becomes a setting

Asked for by the maintainer, and the question behind it was a specific machine: what would the
Ask panel do on a 16 GB discrete AMD card. Reading the shipped path to answer that found
`LlamaServerProcess.BuildEnvironment` setting `LLAMA_ARG_CPU_MOE=1` and `LLAMA_ARG_NO_HOST=1` on
**every** Vulkan child, unconditionally, with no way to turn either off from the application.

That pair is the second machine's answer. On the 880M's UMA split, a "CPU" expert placement
without `--no-host` resolves to the pinned host-visible heap and 10.3 GiB of experts overflow it,
so a 26B-class mixture cannot load at all — measured 2026-08-24, and the pair is the one working
offload form that machine has. Applied to a card with memory of its own it does something else
entirely: it parks in system RAM the expert weights that card was bought to hold. Those are not
symmetrical costs, which is what makes the condition worth having rather than a preference.

**The condition is the Vulkan loader's own answer.** `VulkanDeviceProbe` creates an instance,
enumerates the physical devices and reads `VkPhysicalDeviceProperties.deviceType` — the enum the
question is literally about, reported by the API the backend will run on. The alternatives all
infer it: DXGI from a dedicated-memory figure an integrated adapter reports as a small non-zero
number, WMI from an `AdapterRAM` capped at 4 GB and therefore wrong on every card above it. An
inference that is usually right is the wrong shape for a setting whose failure is silent
slowness. Every failure answers `Unknown` rather than throwing — no loader is the normal state of
a machine running the CPU drop and of every Linux runner — and the probe is asked only when its
answer decides something: the Vulkan backend, with the placement left automatic.

**The type alone turned out not to be the question.** Asked what the panel would do on a laptop
pairing a Radeon 860M with an 8 GiB RTX 5060, the rule as first written answered "there is a
card, put the experts on it" — and `gemma-4-26B-A4B` at IQ4_XS is about 14 GiB. "Is there a card"
is not "does this fit", and on an 8 GiB card the two answers differ by a whole model, on exactly
the model class the setting exists for. So the probe reads the card's largest
`VK_MEMORY_HEAP_DEVICE_LOCAL` heap beside the type, and the rule weighs the model file against
it: the file, plus a quarter of it, plus a gibibyte. That allowance is anchored to the one full
load in the record — the 9B Q8_0's 8.87 GiB file held about 11.7 GiB at a 53,248-token context,
so the rule asks for more room than that load took — and it is conservative in one direction on
purpose. Refusing a card that would have fitted costs speed; accepting one that does not costs a
load, because the engine runs `--fit off` precisely so that nothing trims silently. A size that
cannot be read, at either end, is "not known", and not-known does not fit.

`Unknown` resolves to system memory, and that asymmetry is the point: a model that does not load
is worse than a model that loads slowly, so the unanswered question takes the failure that still
starts.

**Settings carries the override, as *Expert layers*** — decide from my graphics, on the graphics
card, in system memory — beside the model picker and the answer-from picker, stored by name like
every other choice in that file and read fresh before each question. The panel drops an engine
built under the other placement, so the picker takes effect at the next question rather than at
the next restart; a placement is nothing but the child's environment, fixed when the process
starts, which makes it the most literal case of the rule the thinking toggle already follows.
An explicit `LlamaServerOptions.Environment` still outranks whatever the rule resolves to, which
is how the lab script measures one placement against the other without touching a setting.

**What this does not do is measure the branch it opens.** The probe classifies the second machine
`Integrated` with no device-local figure, which is right for a Radeon 880M and means the rule
reproduces the old behaviour on the only machine that has ever run an ask — so the change is a
no-op everywhere it can be checked. No discrete-GPU Vulkan ask run exists with the pair off; the RTX 5080's Vulkan figures
were taken on the lab script with a dense model, where `--cpu-moe` matches no tensors and the
pair is a no-op either way. The two spike runs that would settle the other branch are named in
`docs/UNPROVEN.md`. **1633 tests, 1624 passed and 9 skipped, 0 warnings**, and the gated engine
trio green on cpu and vulkan against a real child with the probe in the start path.

### Built 2026-08-28 — the survey tier, and the Ask tab's headline feature starts working

**A summary of a three-hour recording had no answer at all**, and the suite asserted it:
`Assert.Equal(0, provider.Created)` in the test for a long recording's summary. The router sends a
global question to the whole-transcript path only when the recording fits the retrieval tier's
context — about 25 to 30 minutes of speech — and falls back to retrieval otherwise; a summary
request's words match nothing in an index, so retrieval returned nothing and the panel showed a
failure sentence. Both halves worked as designed. The feature did not.

`AnswerMode.Survey` is the tier between: the recording's cover windows sampled evenly by position
to fit a budget, ends always included, chosen by position rather than by score because a global
question is where a scorer has least to rank on. Every window stays real and citable, so nothing
about the citation contract changes — and the prompt opens by saying it is a sample with gaps it
cannot see, because a sample narrated as a transcript would be three hours described by a model
that read a fifth of it, with a real citation on every sentence.

Measured on CSB384 (2:55:23): the first question 120.8 s and later ones 37 to 46 s, every citation
resolving, no repeated 8-gram, cited spans reaching 99% of the recording (docs/UNPROVEN.md).

**Two speed findings came with it, and one was already shipping.** `cache_prompt` has been on in
the engine since the path was written, and it is worth far more on this tier than on retrieval: a
survey's evidence does not depend on the question, so the second question's prefill was 14 tokens
against the first's 8,642 and its wall fell from 135.9 s to 49.0 s. And the prefill batch had not
plateaued — `-b 4096 -ub 2048` are now the engine's defaults, worth 15 s on a cold three-hour
answer and, on the retrieval shape measured the day before, more than cutting the evidence from
eight windows to six.

**Recall stopped being unmeasured.** A labelled thirty-question set now exists for CSB384 — on the
Drive, not here, as the in-repo template's own comment requires — and scoring it through
`uindosill retrieve` puts recall at 81.8% at the shipped depth and 72.7% at the *Answer faster*
setting. The setting's default was chosen before that number existed and the number supports it.
The same scoring found every paraphrase question failing at every depth, one of them because the
tokenizer does not stem: `unfamiliarity` retrieves its span at rank 1 where `unfamiliar` misses it.

### Built 2026-08-27 — the answering model becomes a catalogue entry, and the Ask tab gets its speed back

Four changes, one measuring session behind them (docs/UNPROVEN.md, *The Ask tab is three times
faster*). The session's question was which dial makes the Ask tab fast on the second machine; the
answer was none of the two it set out to turn.

**Speculative decoding, from the model's own head.** `LlamaServerOptions.DraftModelPath` names a
multi-token-prediction head and `--spec-type draft-mtp -md <path> -ngld <layers>` follows. Measured
1.32x on decode at 71.7% draft acceptance, with the citation checks unchanged. `--spec-type` is
passed explicitly because its server default is `none`: a draft model handed over without it loads
a second model and drafts nothing, which is the worst of both. Prompt-lookup drafting was measured
in the same session and rejected — three n-gram variants accepted 3.0%, 11.5% and 15.3% and bought
nothing — so the option is a model path rather than a mode string, and the rejected alternatives
are recorded on it so nobody re-runs that experiment.

`DraftModelLocator` pairs a head with its weights by name alone: strip `mtp-` and `.gguf`, and the
model's filename must begin with what is left. The asymmetry is deliberate. A wrong pair is a child
that loads two models and then refuses, so the panel stops answering; a missed pair costs speed and
nothing else. Requiring the whole family name as a prefix takes the cheap failure. It is used with
no setting, because a head is the same answer faster rather than a trade — the one thing it costs
is about 0.5 GB resident, which docs/UNPROVEN.md records against the machine where that margin is
thin.

**The answering model becomes a catalogue entry**, which it could not be while Gemma shipped under
Google's own terms. Gemma 4's licence page serves the Apache License 2.0 outright (read 2026-08-27),
so there is no bespoke use restriction to carry to a user, and `ModelTask.Answering` joins the
discriminator. Two entries, the same model at two quantisations: `UD-Q4_K_XL` because it is what
the publisher recommends, and `UD-IQ4_XS` because the recommended one does not fit a 16 GiB machine
and the smaller one is what this project has measured running there. Both install into directories
of their own, and must — they ship the same drafting head under the same name and would otherwise
overwrite each other at the store root — so the Ask panel's model discovery now reads one level
down as well as the root, and skips heads, which answer nothing.

`PinnedDigestsAreDistinct` had to be told the difference between a copy-paste slip and one upstream
file used twice. It now keys on the digest *and* the URL: the same digest from the same URL is two
entries sharing a file, and the failure the test names — the second download rejected as corrupt —
cannot happen, because the bytes really are the same.

**A warning where a refusal would be wrong.** `ModelFit` is the first thing in this catalogue that
has to ask whether a machine can run an entry at all; every other task's weights are between 2 MiB
and 1.34 GiB. The rule is the file plus two gibibytes against total physical memory, anchored to
the two points measured on the second machine — 12.66 GiB ran there leaving 0.9–1.8 GiB free, and
15.85 GiB will not — and it warns rather than refuses. Total memory is a crude proxy, the reading
is not of what is free right now, and nothing here knows what a discrete card is holding: being
wrong in the direction of "we said so and you did it anyway" costs a download, while being wrong
the other way costs somebody a model that would have worked.

**The evidence depth becomes a setting, and deliberately not a new default.** Cutting the retrieval
tier from eight windows to four was the largest single measurement of the session — a median answer
fell from 42.3 s to 16.6 s — and the mechanical checks did not degrade: every citation resolved at
every depth and all three adversarial questions were abstained from at every depth. It ships as
`AskEvidenceDepth` at Thorough, which is the old behaviour, because the risk is exactly what the
session could not measure. Recall is not scored anywhere: with four windows the answer can simply
not be in front of the model, and every question in that set had its answer in a high-ranking
window, so the set cannot see that failure. Scoring it needs
`tests/fixtures/csb384/questions.json` to stop being `status: template`, which is what
`scripts/measure-answers.ps1` refuses to score around. So the dial is offered and the decision is
left where the evidence is not.

**The sampling block was changed and changed back, inside one day, and the round trip is the point.** It was made mode-dependent — greedy for retrieval, the publisher's own temperature 1.0 / top-p 0.95 / top-k 64 for the whole-transcript path — on the strength of this project's note that greedy "produced bullets of pure loop" on a summary ask. Measuring it withdrew it: over four global questions greedy repeated no 8-gram at all and cited across 97% of the recording, and three seeds of the sampled configuration matched it on repetition, coverage, citations and wall time. The loop belonged to the grammar, which stopped shipping on by default the day it was recorded. So the split bought nothing and cost determinism, and the engine sends one pinned greedy again (docs/UNPROVEN.md, *Greedy does not loop on summaries*).

What did **not** change in the end, and why it is worth writing down: the sampling block. The session measured
Google's standardized configuration for this family against the pinned greedy and found the whole
span inside the run-to-run noise, with the citation contract intact either way — and `top_k=1` at
temperature 0 produced byte-identical output to sending no sampling fields at all, which is the
proof that the pin was already greedy. The pin stays for reproducibility. The quality argument
originally recorded for it does not survive the measurement, and docs/UNPROVEN.md says so.

### Fixed 2026-08-25 — "think before answering: off" did not turn thinking off

Found by driving the new overview path against the 26B and getting an empty answer back after
79.4 seconds. The engine set `--reasoning-format` and never `--reasoning`, whose default is
`auto` — detect from the model's template — so a thinking model thought regardless of the
setting, the default parse filed the thinking under `reasoning_content`, the engine dropped it
as designed, and the whole answer budget could go before one content token existed. The panel's
report of that is "The model produced no answer", which is honest and useless. The same prompt
and model under `--reasoning off` returned a framing sentence and four cited bullets in 45.5 s.
The child now takes `--reasoning on|off` from `ThinkBeforeAnswer`, so the Settings toggle
decides the thing it is labelled with; `--reasoning-format none` keeps its narrower job of
holding a grammar-shaped stream in `content`. The register's 2026-08-24 thinking-cost figures
are unaffected — they were taken with the flag explicitly on — and the same run fixed a
cosmetic the overview path made common: a citation lifted from mid-sentence left the space in
front of it, so "…the staging environment [S1-S4]." rendered as "…the staging environment .".
Only the period and comma close up; a space before ; : ! ? is correct French typography.
**1633 tests, 1624 passed and 9 skipped, 0 warnings**, the gated engine trio green on cpu and
vulkan against a real child.

### Fixed 2026-08-25 — the suite gets one scratch root, and stops leaving 17,000 directories behind

Found by looking where the settings leak pointed. `Directory.CreateTempSubdirectory` appeared at
78 call sites across the tests and almost none deleted the result, for a structural reason
rather than a careless one: a helper returning `(ViewModel, string Directory)` hands the
directory out, so it outlives the method that made it and there is nowhere for a `using` to go.
The three sites that did clean up are the three whose directory never left the test body. On the
machine this was found on, `%TEMP%` held **17,140 such directories and 4.2 GiB**, the oldest nine
days old, growing by several dozen per run.

`tests/Shared/TestTemp.cs` takes one root per test process and hands out children of it —
`NewDirectory(prefix)` for a directory, `NewPath(fileName)` for a file inside a fresh one — then
deletes the root at process exit, so no test has to own a lifetime it is not shaped to own. All
85 allocations moved over, including the four `Path.GetTempPath()`-and-a-GUID variants; the
`TempDirectory` disposable stays for the tests that want a directory gone at a known point and
now allocates from the same root, and `TestUserData` takes its redirect directory from it too, so
there is one root and one cleanup rather than two conventions. Four paths stay unrouted on
purpose — a missing interpreter, a missing model, a missing native directory, a missing GGUF —
because a helper that creates what it names would turn each of those tests into a test of
something else. Measured rather than asserted, since a test cannot watch its own process exit:
the `%TEMP%` entries were compared **by name** across a full suite — not by count, because a
second worktree was running the same suite on this machine and its directories land in the same
place — and the set came back identical, nothing added. That run's suite was unchanged by the
change, and the count this document quotes is the current one — **1633 tests, 1624 passed and 9
skipped**; `docs/GOTCHAS.md` gotcha 34 carries the shape.

The 17,140 already there are not deleted by any of this. About 1.06 GiB of that is research
output rather than test litter — an ONNX export run of 39,936 files, a premise-scoring directory
and a link probe — so the sweep is the maintainer's to run and not a script's to guess at.

### The dictation seam

The brief said push-to-talk dictation must not be built and must not be architected out. It is now
v3, behind the question-answering panel, and nothing about that ordering changes what it will
need. Two
things it would need are recorded rather than assumed: the streaming ASR and end-of-utterance
weights are pinned in the `deferred` array of `models.json` with exact sizes and digests, and the
loader, installer and digest checking reach them unchanged once a licence is established for each.
Neither is installable in this build, and the reason is written down in `docs/MODELS.md`.

**Nothing now stands between this and a v1 anybody can install.** That is a change as of
2026-08-19, and it took two steps in two days. Phase 5 was one: everything the product does, it did,
but it could not arrive on a machine with no .NET SDK and no git clone, and now it can — there is an
installer and it was run. Speakers was the other, because the maintainer decided on 2026-08-16 —
overriding the study's v1.1 recommendation, item 4 below — that **v1.0 does not ship without
diarisation**: opt-in in the product, an option the user turns on, but aboard from the first release.
The diariser passed its gate on 2026-08-18 in Python and was **ported to C# on 2026-08-19**, where it
reproduces the passing number to four decimal places. **Translation into English joined the v1.0
gate on 2026-08-19**, by the same override this paragraph describes, so what is left before v1.0 is
now a feature as well as a release: nothing has been published to GitHub Releases, so the update
check has never found anything and the download-and-restart path has never run against a real feed.

The next actions, in order:

1. ~~**Phase 5 itself** — Velopack, and signing every PE rather than only `Setup.exe`.~~ **The
   Velopack half is done 2026-08-19** — see *Built 2026-08-19* above — and the signing half left v1
   on 2026-08-16, so what remains under this heading before v1.0 is not code but a first release:
   nothing has been published to GitHub Releases, so the update check has never found anything and
   the download-and-restart path has never run against a real feed. ~~A build
   that vendors the natives instead of expecting a manual copy~~ — done 2026-08-15, above. ~~What
   signing waits on is a certificate, which is a purchase rather than a commit; what Velopack
   waits on is a decision about how the opt-in CUDA tier arrives, since 700 MB does not belong in
   the default download.~~ Both decided 2026-08-16 — see *Decisions taken* above: the CUDA tier is a
   second download flavour, and signing goes through SignPath Foundation's free programme, which
   waits on an application to SignPath rather than on a purchase, and which by its own terms signs
   this project's binaries and not the upstream natives. **Later the same day the signing half
   left v1 entirely: v1.0 ships unsigned** (decision 2 above records what that accepts), so
   Phase 5 for v1 is Velopack without signing and the SignPath application is post-v1 work.
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
   `uindosill notice` and the About window render it, and two tests hold it up. The reading, and
   the three things about it that remain unverified — no legal review, an EULA revision not
   contemporaneous with the CUDA 12.8 binaries, and an upstream redistribution this project did not
   perform — are in `docs/LICENSING.md` and `docs/UNPROVEN.md`. What is left is Phase 5 packaging
   the result.
4. ~~**Before v1 ships: a research workflow on how best to implement speaker diarisation in this
   app.**~~ — **the study ran 2026-08-16**; its result lives in the maintainer's diarisation
   research on the Drive, outside this repository the way the v2 research is (moved out the same
   evening at the maintainer's ask — the convention `CLAUDE.md` now names), and the measurement
   design that used to live in this item moved there with it, sharpened. Asked for by
   the maintainer on 2026-08-16, after the WER work; a study, not a build. What it settled: the
   single most consequential unknown resolved — Sortformer runs without NeMo, because although no
   official ONNX exists the export recipe is public, community exports were verified
   file-by-file, and streaming v2 is CC-BY-4.0 and un-gated; every official pyannote pipeline
   repo is HF-gated, which the by-URL installer cannot use, while sherpa-onnx redistributes the
   same MIT segmentation model un-gated behind a maintained C# NuGet; DiariZen and Rev are
   non-commercial and out for shipping; and no candidate has a published number on podcast
   material, so the dev/held-out podcast set — stratified two to five voices through guest
   episodes, split by show, scored by a collar-0.25 overlap-included DER harness — remains the
   deciding instrument, with a proposed gate written down to be ratified before held-out is ever
   scored. **The study recommended v1.1; the maintainer decided otherwise the same day: speakers
   ship in v1.0, as an option the user turns on, and the maintainer sources the test data.** The
   study's machinery survives the override unchanged — the spike order, the exact artifacts, the
   gate ratified before held-out is ever scored — but the critical path inverts: labelling and
   the spikes move to the front of the queue beside Phase 5, and — signing having also been
   dropped from v1 the same day — v1.0 ships when Velopack packaging and the passed gate both
   land. The research's *The recommendation, and the decision* section carries the record, and
   the four stratified test episodes the maintainer supplied the same evening — two hosts plus
   zero, one, three and five guests, one show — live on the Drive beside it.
   (`docs/V2-ASK-THE-TRANSCRIPT.md` § *Not in v2: who said it* holds until the feature actually
   ships.)

   **Built 2026-08-17 — the instrument, the material, and the seam; nothing measured.** The
   laptop half of the build/measure split, in the order the plan fixed:

   - **The DER scorer** is `uindosill der` over `Parakeet.Core.Diarisation`: pyannote.metrics'
     algorithm — the union of both extents as the scored region, a collar cut out around every
     reference boundary, elementary intervals, the one-to-one speaker mapping that maximises
     co-occurring speech found by exhaustive search rather than greedily — and **validated against
     pyannote.metrics 4.1 on ten committed fixture pairs, all four blocks of each — headline,
     collar 0, overlap regions, and skip-overlap — agreeing to a microsecond** (`tests/fixtures/diarisation/scorer/`, `scripts/validate-der.py`; the C# test
     suite re-asserts the agreement on every run). It prints three numbers together: the headline
     at collar 0.25 s with overlap included, the strict number at collar 0, and the same components
     over reference-overlap regions only. `scripts/measure-der.ps1` — `lab.ps1 der`, the eleventh
     dispatcher task — cuts the stretches and scores hypothesis directories into
     `runs/der/`; `uindosill rttm` converts an Audacity label export.
   - **One convention finding worth reading before the gate is ratified.** pyannote's `collar` is a
     *total* width centred on the boundary — `collar=0.25` forgives 0.125 s either side — while NIST
     md-eval and NeMo quote a *half*-width, so a Sortformer model card's "collar 0.25" is this
     scorer's `--collar 0.5`. The benchmark the plan anchors to, arXiv 2509.26177, states it uses
     pyannote.metrics at `collar=0.25, skip_overlap=False`, and that is the headline convention
     here; the card numbers the proposed 10% was derived from sit on the other scale. Neither number
     is wrong; they are not on one scale, and the gate should say which it means.
   - **Five development stretches**, ten minutes each, are pinned in
     `tests/fixtures/diarisation/dev/stretches.json` — episode, onset, the exact ffmpeg line,
     ffmpeg version, byte count, and two SHA-256s (whole file, and PCM alone, because ffmpeg copies
     the episode's tags into the WAV header). `lab.ps1 der -Cut` re-creates and verifies them
     from the episodes at the repository root. Two from the two-host episode, one from each guest
     episode; onsets chosen by transcribing three candidate windows per episode and reading them
     for conversation over ad reads and for guests evidently present — text-only inference,
     recorded as such. The labelling guide is `tests/fixtures/diarisation/README.md`; no stretch is
     labelled yet, and labelling effort remains unmeasured.
   - **The seam** is in: `ISpeakerLabeller` in Core beside `ITranscriptionEngine`; a nullable
     `Speaker` on `TranscriptSegment` and `TranscriptWord`; `SpeakerAssignment` attributing words to
     turns and cutting segments where the speaker changes; every formatter naming the speaker when
     one is known and byte-identical to before when none is; `SubtitleCueBuilder` never merging a
     cue across a speaker change; a seventh format, `rttm`, writing the labeller's turns; a canned
     labeller for CI; the catalogue's `"task"` discriminator, so a diarisation entry can be
     installed through the same digest checks and never surface as a selectable ASR model. The
     opt-in shapes it: `transcribe --speakers` and a checkbox in the app, both off by
     default, and both honest about the fact that this build has no real labeller — the flag says so
     and stops, the checkbox is disabled with the reason. The suite grew from 359 to 451 at that commit.
   - **Not done, by design:** the sherpa-onnx and Sortformer spikes belong to the desktop, which is
     where the measuring half of the split runs; every DER, RTF and memory figure for a real
     candidate is still zero measurements.

   **The target domain widened on 2026-08-17 — meetings and web video beside podcasts, and the gate
   covers one of the three.** The maintainer named the feature's target as meetings, podcasts and
   YouTube. Everything written above that date describes podcasts alone, and so does every artifact
   under it: four podcast episodes, five podcast stretches, and a gate phrased around two hosts,
   which is not the shape of a meeting or a panel. That leaves the ≤ 10% figure covering one domain
   of three, the other two carrying no material, no reference labels and no gate. It is recorded
   here rather than closed: whether meetings and web video get gates of their own, or the gate is
   restated in terms that span all three, belongs to the ratification that has not happened.
   **Resolved 2026-08-18 by narrowing rather than by gating** — web video was dropped once a
   passing candidate turned out to be architecturally unable to serve it. See the narrowing below.

   **What follows from it.** Hand-labelling does not scale to three domains at the plan's own
   estimate of thirty to sixty minutes per ten minutes of audio — an estimate still unproven, and
   one the maintainer declined to spend against on 2026-08-17, before the first stretch was
   labelled. Material for the two new domains therefore has to come from existing corpora carrying
   human, time-stamped references, and a survey of them was commissioned the same day; its product
   lives on the Drive with the rest of the research. The podcast set stays what it was — the
   deciding instrument for its own domain, and the only material this project controls end to end.
   Across all three domains the measurement count is unchanged: zero.

   **What the survey found, the same day.** Forty-three corpora surveyed, forty audited against
   licence text and live download pages. Meetings and web video are covered by free CC-BY material
   carrying human, time-stamped references: AMI, which is also the only corpus where several
   toolkits publish figures whose convention can be established at source, and which is effectively
   a four-speaker set and therefore cannot price the four-speaker cap; NOTSOFAR-1, whose measured
   39% overlap makes it the crosstalk instrument and whose far-field capture is the closest public
   proxy to what this product records; and VoxConverse for web video, whose measured 3% overlap
   makes it domain coverage rather than a crosstalk test. **Podcasts came back with nothing free and
   usable** — the one podcast-specific candidate and the Spotify set both failed, the latter
   withdrawn outright. The labelling declined for three domains is therefore not avoidable for one:
   podcast material remains only what this project labels itself. The survey also sharpened the
   convention finding above — one corpus carries published figures four-fold apart on convention
   alone, and a second scoring pass at collar 0, where half-width and total-width definitions
   agree, is what buys comparability. The report is on the Drive with the rest of the research.

   **The gate was restated on 2026-08-18, against corpora that exist.** Hand-labelling was declined
   that day — the ten-minute stretch first, then a two-minute pilot after it had been cut, pinned,
   transcribed and written up — and that closes the podcast route entirely, because the held-out
   set the old gate named would have needed labelling exactly as the development stretches did.
   *Held-out two-host podcast DER ≤ 10%* had become a criterion that could never be evaluated, and
   since v1.0 ships when packaging and the passed gate both land, v1.0 was gated on something that
   could not happen. What replaces it is scored on material this project already holds and has
   verified: **AMI** as the ranking corpus, because it is the only one where several toolkits
   publish figures whose convention can be established at source; **NOTSOFAR-1** for crosstalk,
   because AMI's 14.58% overlap is mild against its 39%; **VoxConverse** for web video and for
   speaker counts past four, which AMI cannot reach at 15 of 16 test meetings holding exactly four.
   **The VoxConverse third of that restatement lasted the day**: it was removed later on 2026-08-18
   when the domain narrowed to meetings, so the live gate is AMI and NOTSOFAR-1. What is above is
   the restatement as it stood, kept because the narrowing is only legible against it.

   **What the restatement costs, said plainly.** The gate no longer asserts anything about podcast
   audio — the domain this feature was first scoped to, and the one the four stratified episodes
   were sourced for. A pass now means a diariser is close to the state of the art on meetings and
   on web video, and it means nothing about two hosts talking over each other. That is a real
   reduction in what a pass is worth, recorded here rather than absorbed quietly. The 10% figure
   goes with the material it named; it was in any case derived from Sortformer model-card numbers
   on the half-width scale, which this document had already flagged as not the scale the gate is
   written in. The 5-point margin replacing it is relative rather than absolute on purpose: what a
   shipping product has to answer is whether a local, redistributable pipeline lands near what the
   best available one would give the same user, not whether it clears a threshold picked before any
   measurement existed. The five podcast stretches stay pinned and cut, measurable for real-time
   factor and memory and not for DER, exactly as `stretches.json` says.

   **The margin was ratified the same day, 2026-08-18, and the timing is the point.** It was fixed
   while no candidate was anywhere near it and before any had been scored at the gate's own
   convention — the only moment a bar can be set honestly. The single candidate measured so far,
   sherpa-onnx, sits at 54% on the more forgiving collar 0.25 and will read worse at the collar 0
   the gate is scored on, so ratification advantages nothing. A bar chosen after a result is known
   is not a bar, for the same reason a reference adjusted after scores are seen is not a reference;
   this project already refuses the second and now refuses the first in writing. Widening it later
   is a decision that must be recorded here with its reason; narrowing it after a candidate has
   been scored is not available at all.

   **A second criterion was added on 2026-08-18, and it tightens the gate rather than loosening
   it: mean |speakers found − speakers in reference| ≤ 1.0 over the AMI test set.** What forced it
   was a measurement. Tuned on the 18 AMI dev meetings and scored held out, NeMo TitaNet-L beat
   3D-Speaker ERes2Net on DER — 25.05% against 25.77% — while reporting a mean of **14.8 speakers
   per meeting against a reference of 3.9**, up to 26 on a four-speaker meeting and 23 on a
   three-speaker one; ERes2Net reported 5.6. **A DER-only gate therefore selects the diariser whose
   transcript a user would reject on sight.** DER scores under the optimal one-to-one speaker
   mapping, so unmapped surplus clusters cost only the time they cover, and these are slivers of a
   few seconds: inventing twenty speakers is nearly free in the metric and fatal in the product.
   The gate could not express "do not invent twenty speakers", and now does.

   **Why adding this is not the move the paragraph above refuses.** Widening a margin after a
   result is known makes a gate easier and is refused; this makes it harder, and it disqualifies
   the better-scoring of the two candidates measured. It also decides nothing retroactively —
   **both candidates already fail on DER**, at +1.25 and +1.97, so no choice of threshold rescues
   or condemns anything measured to date; it binds Sortformer and what follows. The measured values
   are 10.88 for TitaNet-L and 1.75 for ERes2Net, so **≤ 1.0 fails both** and cannot be read as
   fitted to either. The number is the tightest non-trivial tolerance and is motivated by the
   product — a transcript's speaker list has to match the room — rather than by parity with a
   published system, which is why it is absolute where the DER criterion is relative. **What is not
   established is whether a strong diariser clears it:** pyannote 3.1's own speaker counts on this
   audio have not been measured, so ≤ 1.0 is asserted as a product requirement and not as a known
   achievable figure. If measurement later shows the best available system cannot meet it, that is
   grounds to revisit the number, recorded here with its reason like any other change.

   **The gate passed on 2026-08-18, and this is what the pass is and is not.** Streaming Sortformer
   4spk v2.1 through the community ONNX export of its 30.4 s streaming configuration, driven from
   Python on the desktop, CPU only, at 74x realtime over 18.73 h of AMI. Post-processing was tuned
   on the 18 dev meetings pooled — 2 179 distinct configurations — and applied unchanged to the 16
   test meetings, which were scored once. **AMI test DER 16.33% at collar 0 with overlap** (13.60%
   at the headline collar 0.25, 26.79% over reference-overlap regions), against the ≤ 23.8 the
   margin ratified earlier that day fixed, and **mean |speakers found − speakers in reference|
   0.06** against ≤ 1.0. Both criteria hold, so the gate is passed. For scale, the other candidate
   measured at this convention, sherpa-onnx, sits at 25.05%, and pyannote 3.1's published 18.8 — the
   figure the margin is measured from — is 2.5 points worse on the same references.

   **The pipeline is not merely plausible, it reproduces its source exactly, and establishing that
   required catching a reference-set trap.** NVIDIA score AMI against the forced-alignment
   ground-truth RTTMs of `nttcslab-sp/diar-forced-alignment`, **not** the pyannote
   AMI-diarization-setup `only_words` references this project and pyannote 3.1 both use. The two
   hold 6.65 h and 8.53 h of reference speech over the same sixteen recordings, and the *same*
   hypotheses score **13.59 points apart** across them, so no figure may be quoted from one against
   the other. Re-tuned on the forced-alignment dev split and scored on its test split, this
   implementation lands on **15.90%, which is NVIDIA's published figure for this configuration to
   the decimal**. Two things underwrite that: the mel featurizer is **bit-exact** against NeMo's own
   `FilterbankFeatures` on real AMI audio, noise, silence and a ramp — its `preprocessor` block was
   read out of the `.nemo` checkpoint itself, which is how `normalize: NA` was caught, a setting
   that would otherwise have made a correct model look mediocre — and the Arrival-Order Speaker
   Cache is **NVIDIA's own `streaming_update_async`, imported and called**, not a port of it. The
   16.33% above remains the gate number, because the gate's 18.8 anchor is on the pyannote scale.

   **The four-speaker cap is still unpriced, and it is now clear that no corpus in the gate can
   price it.** AMI test is 15/16 four-speaker, and the model reported exactly four speakers on all
   sixteen, so the speaker criterion passed on material whose count barely varies rather than on a
   demonstration of counting. AMI can be pushed downward, and there the result is good: cutting the
   stretches where the reference holds one or two distinct speakers, the model found one in all ten
   one-speaker stretches and **never over-counted in 25 of 25** — its errors below the cap are
   under-counts of near-silent participants, the harmless direction for a transcript. Upward,
   nothing was tested. **VoxConverse, which this gate named as the beyond-four check until the
   narrowing below removed it, could not have served as one for this candidate on two independent
   grounds anyway.** It appears in v2.1's own training-data
   list as `VoxConverse-v0.3`, unqualified by split, and NVIDIA publish no VoxConverse figure at
   all. And it is arithmetically out of reach: computed here from the published RTTMs, 63% of its
   232 test files hold more than four speakers, up to twenty-one, so a four-capped model's **best
   possible** mean |found − reference| is **3.02** on test and 1.38 on dev — unreachable against
   ≤ 1.0 **even with perfect performance**. NOTSOFAR-1 is not blocked the same way, since NVIDIA
   report a designated 160-session eval split, but 90 of those sessions hold five to seven speakers
   and it too is in the training-data list. **All three of this gate's corpora are.** AMI test is
   safe — NVIDIA evaluate on it, sixteen sessions of three to four speakers, exactly this set — but
   AMI *dev* plausibly was not held out, which explains the 11.91% dev to 16.33% test gap better
   than corpus difficulty does, and does not touch the test number.

   **What a passing candidate does not yet mean.** The export is the 30.4 s input-buffer
   configuration, so the diariser runs half a minute behind the audio: fine for transcribing a
   file, not a live-captioning latency, and the 1.04 s graph is a different export this project
   does not hold. Nothing here says anything about podcast audio, for the standing reason. And the
   C# port is **unstarted work rather than a translation** — the graph owns neither the featurizer,
   nor the speaker cache, nor the chunk loop, and the cache alone is some 250 lines of tensor
   bookkeeping that this spike deliberately did not port, because the model had to earn it first.
   The report, the code and the cached per-meeting probabilities are in a dated folder beside the
   other research on the maintainer's Drive, per `CLAUDE.md`; nothing from the spike is in this
   repository.

   **The target domain narrowed to meetings and podcasts on 2026-08-18, and web video is out.**
   This closes the question the widening of 2026-08-17 left open — whether meetings and web video
   get gates of their own, or the gate is restated to span all three — and it closes it by dropping
   one of the three rather than by gating it. The reason is a measurement rather than a preference.
   The candidate that passes caps at four speakers, and VoxConverse, the corpus chosen to represent
   web video, holds a mean of 6.5 speakers per file across its test split with 63% of files above
   four and one holding twenty-one. A four-capped model's best possible mean speaker error there is
   3.02 against a criterion of 1.0, so the product cannot be good at web video with this diariser
   and no amount of tuning changes that. Keeping web video as a target while shipping a diariser
   architecturally unable to serve it would be a claim the evidence contradicts, which is the one
   thing this document exists to prevent.

   **What the narrowing costs and what it does not.** It costs the domain, and it costs the
   corpus: VoxConverse leaves the gate, so the gate's two corpora — AMI and NOTSOFAR-1 — are both
   meeting sets, and **nothing in the gate now checks behaviour above four speakers at all**. That
   is a real loss of coverage and is recorded as such in `docs/UNPROVEN.md` rather than absorbed;
   the cap is not tested, it is scoped around. It does not cost podcasts. They remain a target
   domain and remain ungated for want of labelled material, exactly as before, and the spike's own
   evidence is favourable there rather than silent: across 25 cut stretches holding one or two
   distinct speakers the model never once over-counted, and a two-host show is precisely that case.
   That is not a podcast DER and does not become one, but it is the measured absence of the failure
   a two-host transcript would show first.

   **What follows for the product.** The four-speaker limit stops being a footnote and becomes a
   stated property: the feature is for meetings and conversations of up to four people, and the
   interface should say so rather than degrade quietly, because above four the published figures
   for this configuration are 34.81% and 38.90% — a third of speech attributed to the wrong person,
   which is a transcript a user would reject rather than a worse one they would tolerate.

   **The port landed 2026-08-19, and it reproduces the passing number.** Streaming Sortformer is
   now C# behind `ISpeakerLabeller`, in a project of its own — `Parakeet.Engine.Sortformer`, the
   same shape as `Parakeet.Engine.ParakeetCpp`, because `Parakeet.Core` may reference no NuGet and
   a build target fails if it does. Scored the way the Python was, on the 16 AMI test meetings
   through `uindosill der`, with the post-processing fixed on dev and untouched:

   | | C# port | Python spike | delta |
   |---|---|---|---|
   | DER, collar 0.25 (headline) | **13.5995%** | 13.5963% | +0.0032 |
   | **DER, collar 0 (the gate's convention)** | **16.3368%** | 16.3324% | +0.0044 |
   | DER, overlap regions only | **26.7986%** | 26.7926% | +0.0060 |
   | mean \|speakers found − in reference\| | 0.0625 | 0.0625 | 0 |

   Four of the sixteen meetings agree exactly and the worst per-meeting divergence is 0.0335 points.
   **Both gate criteria hold**, so what passed in Python is what ships.

   **Three things the graph does not own had to be written, and each is held against the reference
   rather than against a reading of it.** The ONNX export runs the pre-encoder, the encoder and the
   head; the host owns the mel featurizer, the chunk loop and the Arrival-Order Speaker Cache, and
   each is a place where a plausible implementation gives a worse DER without failing. So
   `scripts/make-diariser-fixtures.py` **imports NVIDIA's own `SortformerModules` and NeMo's own
   `FilterbankFeatures`, runs them, and commits what they returned** — 859 KiB under
   `tests/fixtures/diarisation/sortformer/`, replayed by a test project that needs no weights, no
   network and no Python. The speaker cache is exercised at embedding dimension 8 rather than 512, which
   costs no coverage (the algorithm does no arithmetic across that axis but one masked mean) and
   turns a 50 MB oracle into half a megabyte.

   **The cache is where a port goes wrong, and this one did.** It was the one part the spike
   deliberately did not port — it imported `streaming_update_async` and called it — so it was
   written from the reference source rather than translated from something already working. An
   adversarial review of the C# against the Python, five independent lenses over the same two files,
   converged on one defect and found nothing else: the port scored FIFO frames with the predictions
   they *arrived* with instead of the ones the graph had just made for them. The reference re-predicts
   the whole `[cache | FIFO | chunk]` window every step and reads `current_fifo_preds`; its stored
   `fifo_preds` is written and never read again in the asynchronous path. Left in, it would have
   corrupted the silence profile and evicted the wrong frames — no exception, a worse number.

   **What "reproduces" does and does not mean.** Not bit-identical, and it cannot be: the featurizer
   computes its transform in double where PyTorch's is single, the running silence mean is
   accumulated in double for the same reason, and where two frames score identically `torch.topk`
   leaves the order among equal values undefined, so which of them takes a cache slot is not
   something a port can be held to. All three are measured rather than asserted — the suite pins the
   featurizer's deviation under 1e-3 overall and 2e-4 in bands carrying real energy, bounds set from
   the 3.0e-4 and 8.0e-5 actually measured — and the largest
   effect any of them has on a meeting is a third of a tenth of a point.

   **What it costs.** 65x realtime on CPU with 12 intra-op threads, against the Python's 74x on the
   same machine: **about 12% slower, and not investigated**. Peak working set 1 261 MB, which is the
   graph's rather than the host's — the spike measured a bare ONNX Runtime session at 1 315 MB in
   steady state and the export's README states a 1 251 MB peak. Adding ONNX Runtime costs 16.05 MiB
   in a `win-x64` publish — measured at 17.31 MiB on this one, of which onnxruntime.dll is 15.4 —
   and about 6 MB of download, which is estimated rather than measured.

   **The weights are a catalogue entry now, and they are not CC BY.** `models.json` carries its
   first diarisation entry, pinned by size and SHA-256 to revision `db3a7b54` of a third party's
   export. The licence is the **NVIDIA Open Model License**, read in full at NVIDIA's own URL on
   2026-08-19: it permits redistribution outright, and its §3.1 wants one verbatim sentence and **a
   copy of the Agreement** rather than CC BY's seven elements. So the notice record grew a second
   shape — `OpenModelLicenceAttribution` beside `CcByAttribution`, behind an interface — because
   rendering this licence under CC BY's headings would put a false notice in front of a user.
   `docs/LICENSING.md` has the reading, including the two clauses CC BY has no equivalent of: the
   grant is **revocable**, and §2.3 forbids illegal biometric processing, which is what voice
   separation is.

   **A second review covered everything the first one did not, and it found seven more.** The
   speaker-cache audit deliberately looked at one file; the featurizer, the chunk loop, the
   labeller's lifecycle, the resampler, the CLI and app wiring and the licence plumbing were
   reviewed the same way afterwards, on the reasoning that a passing end-to-end number rules out
   gross algorithmic errors and rules out nothing about inputs the corpus never presented. It was
   right to. AMI is sixteen 16 kHz files of fourteen to fifty minutes with three or four speakers,
   and every one of these needed something outside that:

   - **`diarise` threw away the whole run on a file name containing a space** — RTTM splits on
     whitespace and `RttmFile.Write` refuses an id with any in it, *after* the diariser has run the
     entire recording. `transcribe -f rttm` had always underscored the stem; the rule now lives once,
     in `RttmFile.SanitiseFileId`, beside the check that enforces it.
   - **The model was loaded on the caller's thread**, which for the desktop app is the UI thread —
     freezing the window while a 453 MiB graph is optimised. `ISpeakerLabeller.LoadAsync` says
     "never on a UI thread" and the ASR engine takes the same care for the same reason; this one
     did not. A failed load is now remembered too, so a batch behind a corrupt model fails once
     rather than attempting the load again per file.
   - **The speaker checkbox never noticed the model arriving**, because the properties behind it
     never raised a change notification. The hint told the user to install from the Models tab and
     then went on saying so for the rest of the session — a dead end, and one this document had
     already claimed was fixed.
   - **Removing the diariser mid-session left the opt-in ticked**, producing a transcript with no
     names and a zero-byte `.rttm` reported as "Finished" — the exact failure the command line
     refuses.
   - **A size mismatch left the partial download in place**, so every later attempt asked for a
     range past the end of the file and got a 416 for ever. The digest branch three lines below had
     always deleted it; this one had not. Pre-existing, and reachable by the new 474 MB entry.
   - **The shipped Agreement had lost its section numbering** when it was extracted from NVIDIA's
     page, while the notices rendered beside it cite "section 6" and "§3.1". A copy whose citations
     resolve to nothing is not much of a copy.
   - **The resampler's own comment understated its cost by two orders of magnitude** — the kernel
     stretches with the decimation ratio, so it is 193 taps for 48 kHz rather than the fixed 64 the
     comment claimed, each costing three transcendental calls rather than a multiply. Nothing
     measured goes through that code, which is precisely why the claim went unchallenged.

   Two more were reported and rejected on inspection, and one — files dropped onto the window while
   a batch is running are queued and never processed — is real, pre-existing and unrelated, and was
   recorded rather than fixed there. **Closed 2026-08-19 by refusing rather than accepting.** The
   queue is snapshotted before the first file is opened, so a row added after that is in neither
   the snapshot nor the results, and the reconciliation at the end of the batch — written to stop a
   row sitting at "Waiting" for ever — never sees it. `AddFiles` returns early while a batch runs
   now, the way `Clear` always has, and the drop zone binds `AllowDrop` to the same condition with
   its invitation replaced by the reason: a gesture the queue will refuse is not one the window
   should accept. Taking the file into the running batch was the friendlier option and was
   rejected — it means a runner that picks up work added after it started, which changes what
   "Finished N files" counts.

   **The opt-in is live in both surfaces, and it turns on when the model arrives rather than at a
   release.** `transcribe --speakers` no longer needs `--fake`; the window's checkbox and the `rttm`
   format are gated on the file being installed, so they come alive when the download finishes. A new
   `uindosill diarise` writes speaker turns without transcribing — which is what made this
   measurement affordable, since scoring 9 h of AMI through the ASR pass would have cost orders of
   magnitude more for a file the ASR contributes nothing to.

**The research comes home when v1.0 ships — decided 2026-08-18.** Everything this project has
measured or studied lives on the maintainer's Drive rather than here, a convention named
2026-08-16 and recorded in `CLAUDE.md`. It now has an end date: **on the v1.0 release, every
research folder and run report moves into this repository**, and the Drive reverts to being a
transfer route between the two machines rather than the place the evidence lives.

The reason it was ever outside is that this repository is public and the research was not written
to be. The reason it comes back is that a public repository asserting a gate, a margin and a list
of unproven claims should carry the material those rest on, and a reader who cannot see the study
has to take the summary on trust — which is the posture `docs/UNPROVEN.md` exists to refuse. A
figure whose working is on someone's private Drive is closer to an assertion than a measurement.

**It is a small job and the size is not the obstacle.** Measured 2026-08-18: 8.8 MB across nine
folders, of which 7.9 MB is one spike's cached per-meeting probabilities and everything else —
every study, survey, report and run summary this project has produced — comes to under a megabyte.

**Three things to settle at migration time, not before.** First, the material has to be read for
publication rather than moved wholesale, because it was written for a private folder and this
repository has already had one history rewrite to remove things that should not have been in it.
Second, **session memory is not research data and does not come** — it names machines and sessions,
and `CLAUDE.md` excludes it for reasons the migration does not change. Third, cached intermediate
artifacts like those 7.9 MB of probability arrays are regenerable from the model in minutes and may
be better left out than committed as binaries; that is a judgement about each artifact rather than
a rule.

### After v1

**v2 is asking questions about a transcript** and **v3 is push-to-talk dictation.** v2 went in front
because it needs none of the Win32 surface below — it reads a transcript this product already
produces — while what it does need is a second native stack and an answer to a harder honesty
problem than v1 ever posed. Its open decisions are recorded in `docs/V2-ASK-THE-TRANSCRIPT.md` and none
of them is settled. Neither version starts before Phase 5 ships.

**NOTSOFAR-1 — decided 2026-08-23: scored after v1.0, and it is an obligation rather than a
waiver.** The gate named it the crosstalk corpus when VoxConverse left (39% of union speech
overlapped, against AMI's 14.58%), and it has never been run, so the AMI pass is the whole of what
the diariser gate holds at release. VoxConverse's exit was a waiver with a recorded reason; this is
not that: the corpus stays in the gate's definition and the scoring moves to the other side of the
release. What the run has to carry is already known from the AMI work — the gate convention (collar
0 with overlap scored), hyperparameters tuned nowhere, the split scored once — and a fail would
reopen the diariser question with the product already shipped, which is the risk this deferral
accepts and names.

**~~A research workflow on offloading to the NPU — asked for 2026-08-16, deferred until it is
relevant.~~ Run 2026-08-25, ahead of its own triggers, at the maintainer's request.** The study is
`npu-offload-research-2026-08-25` on the Drive, and what follows below it was written before any of
it. Left readable because the framing below is the question the study was sent to answer, and
because the study widened it: the record scoped this to Parakeet ASR and the escape hatch, but three
of the five workloads this product runs already sit on ONNX Runtime, which is the Vitis AI execution
provider's native input, and the study covers all five.

**The answer is no, and the reason changed the same day.** The study's own blocker did not survive
contact: on 2026-08-25 the provider was installed and models were run, and it accepts this driver.
`EnsureReadyAsync()` succeeded for both AMD providers, the NPU enumerated as an ONNX Runtime device,
synthetic graphs compiled and executed across all eight columns with zero errors, and none of the
acquisition cost the study priced was needed — no AMD account, no conda, no cmake, no Visual Studio,
and nothing to redistribute, the packages being Microsoft-signed MSIX the OS fetches on demand.
**What blocks it instead is that both ONNX graphs this product ships crash both AMD providers**, and
the NPU crash is an uncatchable access violation inside the vendor's compiler that kills the host
process. A provider that declines a graph is a fallback; one that segfaults is not something an
application can degrade around. Beside that, two numbers settle old arguments: the NPU ran the one
working graph 3.78x slower than the CPU, and its output diverged from the CPU's by 0.22% with no
quantisation asked for — three orders of magnitude past what excluded CUDA from the diariser's
`auto`, so an NPU backend could only ever be opt-in by name. Details in
`docs/UNPROVEN.md` § *NPU offload*. The paragraph that follows was the study's verdict before any of
it ran, and the ASR route it describes is blocked at step zero: the execution provider's
supported-driver list does not name the branch this machine's driver sits on, and AMD and Microsoft
publish inconsistent compatibility rules for the same provider. The translator is excluded by a growing KV
cache against a static-shape compiler, the speech detector by a per-inference budget of about 144 µs,
and the v2 answer engine by a context limit an order of magnitude under a three-hour transcript.
**On speed there is no case in the vendor's published configuration** — its own two figures for its
Parakeet demo disagree by about 1.5× and bracket this laptop's measured Vulkan tier rather than
beating it — but that establishes no ceiling in either direction, because no configuration of the
route has been benchmarked in a state anyone would ship. **What the case actually rests on is watts,
and that is unmeasured on both sides**: the vendor's own tool reports `Estimated Power : N/A` on this
driver, Windows exposes no NPU performance counter on this build, and the one working power
instrument reads zero on AC. A gate the record never drew now stands in front of all of it — this
project's rule is that what it picks unasked reproduces the figure it publishes, and a whole-graph
BF16 encoder cannot meet a 1e-4 parity tolerance by construction, so an NPU backend could at best be
reachable by name and never the unasked default, exactly as CUDA was ruled out of the diariser's
`auto` on 2026-08-22.

**Nothing was installed and no inference was run.** What was measured is the machine's own state,
read-only: the NPU is present, healthy and idle, the array is 8 columns, the driver already ships the
Vitis runtime and the array overlays but **not** the ONNX Runtime provider — which narrows the
redistribution question to the provider alone — and the 24 GB installed is 15.62 GB visible after
the iGPU carve-out, so anything on the NPU competes for the same memory as everything else. Four
cheap next steps are named in the study's § 9, ordered to be abandoned early; the two that need
neither an install nor a download are a provider enumeration through Windows ML and
`xrt-smi validate --run latency`, which would settle the dispatch-latency question every always-on
argument depends on. **The v3 tier was re-aimed and got weaker, not stronger**: v3 runs a streaming
checkpoint that carries caches across feeds, which is the graph shape that blocks the other
workloads, so a chunk-length sweep on the vendor's batch demo answers a question v3 will not ask.

The second machine carries an XDNA 2 NPU (`NPU Compute Accelerator Device`, PCI
`VEN_1022&DEV_17F0`, driver 32.0.20102.3930 of 2026-05-06), and nothing this product runs can
reach it: parakeet.cpp is ggml, and ggml's backend list, read at source that day, is cpu, blas,
cuda, hip, musa, vulkan, opencl, metal, sycl, openvino, cann, hexagon, zdnn, zendnn, rpc, webgpu,
virtgpu and et — no XDNA. The route would be `docs/ENGINE-CHOICE.md`'s escape hatch — ONNX Runtime
with a hand-written TDT decoder — under AMD's Vitis AI execution provider, reached either through
the Ryzen AI SDK (Python and conda) or through Windows ML (C#, the EP managed by Windows, 24H2 or
later, a driver-version window). AMD publishes a demo of exactly this on exactly this model
(`amd/RyzenAI-SW`, `Demos/ASR/Parakeet-TDT`, weights `istupakov/parakeet-tdt-0.6b-v3-onnx`):
conformer encoder on the NPU at BF16, LSTM decoder on the iGPU, mel on the CPU, static 15-second
chunks, a first run that pays a cached compile — and its README says RTF 0.023–0.030 on 16.5
minutes of audio, hardware unnamed. This laptop's Vulkan tier already measures RTF 0.035, so on
speed the ceiling is about 1.5× and even that is a cross-machine, cross-chunking comparison; what an
NPU actually buys is watts and a free CPU and GPU, which matters for an always-on stream and hardly
at all for a batch job that finishes a ten-minute file in ~21 s. Nothing was run; the marker is in
`docs/UNPROVEN.md` § *NPU offload*. **The maintainer asked to be reminded to run the study when it
becomes relevant, which is any of:** v3 dictation being planned; a battery, thermals or
keep-the-CPU-free question about the app; v1.0 shipped and the next research item being chosen; a
second inference stack (ONNX Runtime, Windows ML) proposed for any other reason. What the study
has to carry: BF16 with per-operator CPU fallback as a new state for the WER gate (the ONNX INT8
export collapsed silently); static shapes forcing a segment length against the segmenter's join
guarantee; hardware gating — no NPU on the desktop, the reason the Windows-native AI APIs were
rejected in `docs/V2-ASK-THE-TRANSCRIPT.md` § 1; the Ryzen AI runtime and `flexml-lite`
redistribution licences, unread; and that AMD's LLM-on-NPU path is ONNX Runtime GenAI hybrid or
the Lemonade daemon, both shapes v2 already rejected. Cheapest first measurement: AMD's own demo
on this laptop against the same ten-minute file, its RTF beside 0.035 — a dev-machine experiment,
not a shippable path. It runs under `CLAUDE.md`'s convention: the product to a dated Drive folder,
the decision record and the unproven markers here.

**~~A GPU execution provider for the two ONNX Runtime components — asked for 2026-08-20, and the
measurement that would decide it does not exist.~~ Done 2026-08-21, above.** The measurement exists;
both components ship on WebGPU, DirectML was measured wrong on both, and what follows was written
before any of it. Left readable because the reasoning below is why the question was asked. CUDA was priced that day and both components
declined it for opposite reasons (*Measured 2026-08-20 — the GPU priced against the CPU* above): the
diariser is 21.8x faster on the graph and changes its DER by up to 11 points on a meeting, the
translator's output is identical and worth only 1.2–1.5x. **Neither of those is the number that
matters for shipping**, because the product is .NET and the Windows GPU path for .NET is
**DirectML**, not CUDA — a different execution provider, different kernels, a different correctness
surface, and `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4 against the pinned 1.29.0. Nothing about
DirectML has been run here at all, and **the 240-of-240 translation identity result is a CUDA result
that does not transfer to it.** The other thing that makes this one experiment rather than two is
that it is a **swap, not an addition**: one native serves both components, so the translator cannot
be moved without moving the diariser.

**What the study has to carry:** DirectML measured on both components on both machines, since the
laptop's Radeon is the case DirectML exists for and the desktop's 5080 is the one that has a CUDA
alternative; **the diariser's 18-meeting dev grid re-tuned on DirectML probabilities and the test set
scored once after**, because a provider's DER cannot be inherited and CPU-chosen post-processing is
not that provider's honest number; whether DirectML reproduces the CPU's translations string for
string, which CUDA does and which decides whether a GPU may ever *produce* a gate score; the int8
question, since CUDA returned gibberish for it silently and int8 has been dropped anyway, so this
only matters if a small artefact is ever wanted again; and the packaging cost, which is a second
native in an installer channel that is already over 800 MB. **Cheapest first measurement:** the
diariser alone on DirectML on the desktop, 26 seconds a pass on CUDA and unlikely to be much worse,
against the recorded 16.3368% — if DirectML's probabilities differ from the CPU's the way CUDA's do,
the rest of the study is about how much re-validation is acceptable rather than about speed.
**Not before v1.0**, and the reason is the one above: the component with a real speed-up is the one
whose passed gate the swap moves. It runs under `CLAUDE.md`'s convention — the product to a dated
Drive folder, the decision record and the unproven markers here.

### Decided 2026-08-26 — a second diariser the user chooses, and the first weights this project will not ship

**Sortformer stays and DiariZen joins it.** Speaker labelling now has two catalogue entries and the
Models tab asks which to use; `AppSettings.DiarisationModelId` remembers the answer and
`EngineProvider.DiarisationModel` resolves it, falling through to whatever is installed when the
stored id names something that is not. Nothing about Sortformer changes: it is still the bundled
entry, still what a fresh install gets, and still the only diariser with a gate score.

**What prompted it is this document's own record rather than a preference.** The Sortformer row
above says the model "does not transfer" to podcasts: *all four episodes returned four labels
whether there were 2, 3, 5 or 7*, and a duration ladder puts the count right to fifty minutes and
wrong from an hour. The cap is the graph's four speaker slots. DiariZen clusters rather than
tracking and is given no count at all — `max_speakers_per_chunk = 4` in its config bounds
*simultaneous* voices inside one sixteen-second window, and its clustering's own `max_clusters`
argument is documented upstream as "not used but kept for compatibility" — so it has no total cap
to hit.

**Measured here 2026-08-26, both arms on this laptop's CPU at 12 threads, over the five ten-minute
development stretches.** These are speaker counts, not diarisation error: **the stretches carry no
reference labels**, so no DER of any podcast exists for either system and none is claimed.
`nominalVoices` is the *episode's* confirmed voice total and therefore an upper bound on a
ten-minute window, which is what makes only some of these cells verdicts.

| stretch | episode voices | Sortformer | DiariZen | Sortformer RTF | DiariZen RTF |
|---|---:|---:|---:|---:|---:|
| `two-hosts-a` | 2 | 2 | 2 | 0.024 | 1.010 |
| `two-hosts-b` | 2 | **3 — over** | 2 | 0.023 | 0.969 |
| `two-hosts-one-guest-a` | 3 | 3 | 3 | 0.034 | 1.080 |
| `two-hosts-three-guests-a` | 5 | 4 — at its ceiling | **5** | 0.042 | 1.196 |
| `two-hosts-five-guests-a` | 7 | 4 — at its ceiling | **5** | 0.032 | 0.994 |

**The counts are de-slivered, and that cut both ways rather than one.** A cluster is counted as a
voice above five seconds of speech — a floor stated once, applied to both systems identically and
never adjusted. It matters because `docs/UNPROVEN.md` already records the failure it guards against:
the sherpa-onnx candidate reported 26 speakers on a four-speaker meeting and *scored better on DER
for it*, because surplus clusters are slivers the optimal mapping absorbs. Raw, Sortformer reports
three voices on `two-hosts-a` and DiariZen three on `two-hosts-b`; the extra cluster is 0.4 s in the
first case and 0.6 s in the second, and neither is a person. **Reporting the raw counts alone would
have been unfair to Sortformer on one row and flattering to DiariZen on another.** Above the floor,
one over-count survives and it is Sortformer's: three sustained clusters on `two-hosts-b`, the
smallest carrying 198.7 s, against an episode confirmed to have two voices.

**What the last two rows show is the property the swap was wanted for.** On episodes of five and
seven voices Sortformer returns four because four is all it can return. DiariZen returns five, and
on `two-hosts-three-guests-a` — where the episode's confirmed total is exactly five — all five of
its clusters carry between 50 and 230 seconds of speech. That is a count above its rival's ceiling,
supported by sustained speech rather than by fragments, and it is the one thing here that is not a
matter of degree.

**Three costs, all measured, none of them small.**

- **About thirty times slower.** DiariZen runs at RTF 0.97–1.20 against Sortformer's 0.023–0.042 on
  the same five files and the same twelve threads. A three-hour episode is roughly three more hours
  after the words are done, and the Models tab says so before the download.
- **Peak memory near 7–8.5 GB on a ten-minute file**, on a machine with 16 GB. It is driven by
  `batch_size = 32` over a WavLM that retains all 25 layer outputs, so it is tunable — but it is
  untuned, and nothing has been run on a file long enough to say whether it grows with duration.
- **No GPU path on this machine at all.** DiariZen is torch, torch has no Vulkan backend, and this
  laptop has no CUDA. The bundle's torch is the CPU build deliberately; a CUDA build is about 2 GB
  of libraries. Where Sortformer's `auto` reaches WebGPU, this reaches nothing.

**And Sortformer left the installer the same day, so neither diariser is bundled now.** That is
a second decision and it is not about size: the win-cuda channel had already excluded the
diariser for the 2 GiB asset limit, and the default channel had room. It is about what a
default means. With two models and no ranking between them, whichever one the installer
carried would be the answer on every fresh install — chosen by the packaging script rather
than by the person whose recording it is, and chosen permanently, because a working checkbox
is not something most people go looking to replace. Both are downloads; the Models tab is
where the choice is made. `BundledModels.BundledIds` now names only the 2.2 MiB speech
detector, and `NotInCudaChannelIds` is empty, its 2026-08-24 arithmetic moot.

**It also lightens a licence obligation, which was not the reason but is worth recording.**
A bundled Sortformer made every build a redistribution of NVIDIA Open Model License material,
owing §3.1's verbatim notice and a copy of the Agreement with the binary. Both still ship — a
user who downloads the weights is owed them, and a revocable grant is one to over-notice
rather than under-notice — but the obligation now follows the file to whoever fetched it.
`docs/LICENSING.md` carries the reading.

**The weights are CC BY-NC 4.0 — non-commercial — and they are downloaded, never bundled.** That is
the licence decision and the reason the entry has the shape it does: a bundled NC weight would make
every build a redistribution of non-commercial material inside an otherwise MIT/GPL distribution.
Downloaded, the copy is the user's. `BundledModels.BundledIds` does not list it and
`BundledModelsTests.NonCommercialWeightsAreNeverCarriedByTheInstaller` asserts that over the
*licence* rather than the id, so a future entry arriving under NC terms is caught without anybody
remembering to look. The entry owes two notices, not one — the checkpoint's CC BY-NC 4.0 and the
speaker embedder's CC BY 4.0 — which is what `ModelDescriptor.AttributionIds` was widened for.
`docs/LICENSING.md` is the record.

**What is emphatically not settled, and none of it should be read past.**

- **DiariZen has not been through the ship gate and may not be described as having passed it.** The
  gate is AMI test DER ≤ 23.8% at collar 0 with overlap, plus mean |speakers found − reference| ≤
  1.0 — and it is a *DER* gate first. No AMI corpus is on this machine; that is a desktop run and it
  is owed. Upstream publishes 14.0 on AMI-**SDM** at collar 0, which is a different microphone
  condition, a different reference and somebody else's normaliser: a prior, not a result, and it does
  not enter `docs/UNPROVEN.md` as this project's number.
- **No DER exists for either diariser on any podcast**, because the five stretches carry no
  reference labels. The counts above are the whole of the evidence, and a count is a weaker claim
  than a rate: getting the number of voices right says nothing about where the boundaries fall.
- **Nothing has been run past ten minutes.** Sortformer's failure is a *long-recording* failure —
  right to fifty minutes, wrong from an hour — and ten-minute stretches cannot speak to it. Whether
  DiariZen holds a count over a three-hour episode is exactly the open question, and the one the
  swap is ultimately for.
- **The post-processing knobs do not reach it.** DiariZen binarises internally at parameters its own
  published figures describe, so the host's tuned Sortformer set is reported as not honoured rather
  than quietly applied; `honoursPostProcessing` is how the sidecar says so.
**The packaging half was done the same day, and it took four blockers to get there.** The stack is
in `python/requirements-bundle.txt` now, every line exact-pinned, and none of it was straightforward:

1. **Neither `diarizen` nor its pyannote-audio can be a pinned wheel.** The first is not on PyPI at
   all; the second is a real fork — **3,996 changed lines across 45 of upstream 3.1.1's 82 files** —
   so the released wheel is not a substitute, and `bundle-python.ps1` installs `--only-binary` on
   purpose. Both are **vendored** under `python/uindosill_engines/_vendor/`, beside NVIDIA's
   Sortformer modules and for the same reason, and travel because the bundler copies
   `uindosill_engines` wholesale. 114 files, 3.1 MB, both MIT, both licence texts beside them.
2. **pyannote-audio 3.1.1 does not run on torch 2.13 unaided**, and the breaks surface strictly one
   at a time. `torchaudio.AudioMetaData` is gone; `torch.load` flipped its `weights_only` default in
   2.6; `torchaudio.load` now delegates to a TorchCodec the bundle does not carry. All three are
   repaired in `diariser/diarizen.py`'s `_prepare_imports` — **from this project's own code, so the
   vendored copy stays byte-identical to the source the published figures describe.** speechbrain is
   1.1.0 for the same reason: 0.5.16 and 1.0.3 fail with an `AttributeError` that pyannote's
   `except ImportError` does not catch (gotcha 36) — and 1.1.0, which imports cleanly in a
   virtualenv, makes `pytorch_lightning`'s import recurse to death in the *bundle*. **speechbrain
   is therefore not shipped at all**, which costs nothing: pyannote treats it as optional and
   this project uses the pyannote embedder. That was found by driving the assembled bundle, not
   the venv, and is the reason the bundle is assembled and run rather than only resolved.
3. **`huggingface_hub` had to come down under 1.0**, because `transformers 4.57.6` requires it. The
   compatibility spike missed this by not having the translator installed — an environment that is
   not the bundle does not find the bundle's conflicts.
4. **Two packages have no wheel and never have** — `docopt` and `antlr4-python3-runtime`, the latter
   pinned by every `omegaconf` release. They go in through a named allowlist in the packaging
   script. Both are pure Python, which is what makes the exemption safe: the rule exists to stop a
   wheel being built *for this host*, and neither compiles anything.

**Measured, and then corrected the same evening.** On the bundle's own pins — numpy 2.5.2,
torch 2.13.0 — the engine returns the upstream stack's labels turn for turn on a sixty-second
clip, out of the vendored copies alone with `pyannote.audio` and `diarizen` uninstalled. **That
result does not generalise, and the batch sweep found where it stops.** On the ten-minute
`two-hosts-three-guests-a`, upstream's stack returned 223 turns and all three batch sizes on the
shipping stack return 225. So the stack does move turn boundaries slightly; what is invariant is
the **speaker count**, which is what the gate's second criterion turns on and what the swap was
wanted for. `docs/UNPROVEN.md` and the run report carry the numbers. The earlier wording here
said the stack does not change the answer, which was a sixty-second claim stated generally.

**What is still owed before a release.** `scripts/bundle-python.ps1` has **not been run**, so no
installer carries any of this yet, and the licence enumeration behind `docs/LICENSING.md`'s "fifty
distributions" is a 2026-08-21 number against a file that now resolves to **112**. Sixty-two of them
have had no notice read. And the win-cuda channel's 2 GiB arithmetic in `BundledModelsTests` still
carries rc.3's Python delta, which predates this stack: **the next win-cuda tag is what re-measures
it**, and the 474.6 MB the diariser's weights no longer occupy is the room it has to spend.


**The LGPL obligation in the bundled Python is discharged twice over: by a written offer, and then
by removing most of what it covered.** Both on 2026-08-26; `docs/LICENSING.md` carries the reading.

**The reading first.** Two components were LGPL-2.1 and they were not alike. **libsndfile is a
separate DLL `soundfile` loads with `dlopen`**, and a user can replace it — the mechanism §6(b)(2)
asks for, though 6(b)(1)'s "already present on the user's computer system" does not describe a copy
the installer ships. **libsoxr was statically linked into `soxr/soxr_ext.pyd`**, verified from its
import table, which closed §6(b) outright. Three of §6's conditions were already met by
construction — the texts travel, the notice names them, and nothing here forbids modification or
reverse engineering — and none of §6(a)–(e) had been done.
`licences/LGPL-WRITTEN-OFFER.txt` is the §6(c) instrument that closes that, and
`scripts/package-windows.ps1` refuses a publish without it.

**Then the removal, which was the better answer where it was available.** `librosa.filters.mel` in
`diariser/feats.py` is now a committed `mel-filterbank.npy`, and librosa left the pins — taking
`soxr`, numba, llvmlite, pooch and audioread with it. An assembled bundle went from **108
distributions and 1.40 GB to 99 and 1.26 GB**, and **nothing statically linked in this product is
under the LGPL any more.** libsndfile stays, because `soundfile` genuinely reads every WAV the host
writes, and it is the replaceable half; the offer covers it.

**Changing `feats.py` is the part that needed the evidence, and it has it.** That file was the
spike's byte for byte so the measured 16.3324% AMI figure would describe it. The committed matrix
*is* that call's output from the pinned librosa 1.0.0 at the same parameters, and the mel features
were compared old code against new: **identical to the last bit over two minutes of real audio,
12,016 × 128, with librosa hard-blocked at `sys.meta_path`**. So the figure still describes this
code, and describes it more tightly — a committed array cannot drift where a library call can. Both
engines were then run from the rebuilt bundle: Sortformer on the CPU and DiariZen at its reference
19 turns and 3 speakers.

**What this does not settle.** The AMI figure was not re-scored; it does not need to be, because the
input to the model is bit-identical, but that is an argument from the mel array rather than a fresh
DER. If anything downstream of `feats.py` ever changes, the argument does not carry and the score
does.

**The second diariser can put its speaker embedder on ONNX Runtime, and `auto` does not.** Studied, built and measured 2026-08-26. DiariZen had no GPU path at all: **WebGPU is an ONNX Runtime execution provider and DiariZen is torch** — verified, not assumed, since torch 2.13.0+cpu exposes no `vulkan` and no `webgpu` backend and this machine has no CUDA. `torch-directml` is blocked by a pin, requiring `torch==2.4.1`, and moving the bundle off 2.13.0 would invalidate the translator's 8,149-sentence gate and the diariser's 16.3324% together. The route was an ONNX export of the two neural stages, and **it inverted its own plan twice**: the stage it was written to accelerate is the one that stayed in torch, the stage it assumed was nearly free upstream is the one that pays — and then the stage that pays turned out to move the answer, so it ships reachable by name rather than chosen automatically.

- **Segmentation exports cleanly and gains nothing, so it did not move.** The pruned WavLM-large and Conformer go through `torch.onnx.export(dynamo=True)` in 25 s to a 282 MB, 1520-node opset-18 graph, and ORT's CPU provider reproduces torch to **1.7166e-05**, inside the 1e-4 gate. **The dynamic time axis the plan worried about is not needed**: `Inference.slide` zero-pads the final chunk, so every call is exactly 256,000 samples and only batch varies. But torch CPU, ORT CPU and ORT WebGPU all land **within about 10% of each other** — this laptop's own run-to-run variance — and WebGPU scales linearly from batch 1 to 4, so it is bandwidth-bound and there is no dispatch overhead for graph capture to recover.
- **On WebGPU it is also wrong on this checkpoint, for a reason worth reporting upstream.** One node diverges: the feature-extractor convolution that reads **153 channels**, one of the widths structured pruning left behind (1 → 512 → 153 → 224 → 255 → 302 → 368 → 211). Reduced to a **one-node `Conv` graph containing none of this project's code**, onnxruntime-webgpu 1.27.0 on a Radeon 880M returns ~100% relative error at input widths 150, 153 and 159 while adjacent widths are correct to 1e-06 — deterministically, `0.000e+00` spread over four fresh sessions of eight runs each. Zero-padding the input channels fixes it exactly, bit-identical on the CPU.
- **And it could not hold the configured batch.** At batch 8 the WebGPU device is lost, reproduced twice at 5.2 s, contained inside the Dawn device rather than a Windows TDR. The mechanism is total working set, not one buffer and not submission length: a single 1536 MB buffer allocates fine and a 9.78 s single submission survives. The pipeline was configured at batch 32 when this was measured and is configured at 32 again, the `BATCH_SIZE = 8` deviation of 2026-08-26 having been withdrawn the following day — and the device is lost at 8 as well, so a batch four times smaller is not an escape from it. The embedder is unaffected — it ran to batch 32 on WebGPU without incident — so what this closes is the segmentation half, which was not going there anyway.
- **The wespeaker embedder does move, and it is half the pipeline.** Measured stage split: segmentation 50.5%, embedding 48.9%, clustering 0.0%. The ResNet34 exports to a 26.7 MB graph dynamic in batch *and* both frame axes, and reproduces the torch embedder to **1.21e-07 on ORT CPU** and **1.94e-07 on WebGPU** at batch 32 — three orders inside the parity gate. Placement confirms it: **192 of 206 executed nodes on WebGPU, 93.2%**, so the graph runs where it says it does.

**And it still moves the answer, which is why `auto` is torch.** On `two-hosts-three-guests-a`, idle machine, torch returns **225 speaker turns** and both ONNX providers return **222** — as time, **565 of 300,000 frames, 0.19% of the timeline and 0.82% of speech**, with the speech/silence split byte-identical at 682.3 s because segmentation never left torch. The two ONNX providers return the identical answer to each other, so this is torch-versus-ONNX-Runtime and not CPU-versus-GPU. **The rule this project applies is that what it picks unasked reproduces the figure it publishes** — CUDA is excluded from the first diariser's `auto` while scoring *better*, 16.1021% against 16.3324%, so "changes the answer" has always been the criterion rather than "scores worse". `auto` therefore resolves to `torch`; `--speaker-backend webgpu` reaches the fast path, warns, and trades a difference nobody has priced for about a third of the wall clock.

**A diarisation error rate was published here on 2026-08-26 and is withdrawn.** It read "torch 16.39%, both ONNX paths 16.65%, 0.26 points worse" and it was not a DER: it was scored against `runs/der/stretches/two-hosts-three-guests-a.rttm`, which is a previous run's *hypothesis* output rather than ground truth. `tests/fixtures/diarisation/dev/stretches.json` marks that stretch **`"labelled": false"`**, its episode has five nominal voices, and the file has four speakers — as does the seven-voice stretch's, because every one of them caps at Sortformer's four slots. So the figures measured agreement with a previous run, and "torch is closer to it" is no evidence that torch is better. **No DER exists for DiariZen on any backend in this project.** The decision above does not depend on one, and did not change when the numbers went; what changed is that it can no longer be said which embedder is *better*, only that they differ. `docs/UNPROVEN.md` carries the retraction in full.

**What the end-to-end behaviour is, stated at both lengths, because they disagree.** On ninety seconds all three backends return 31 turns and 3 speakers with identical labels and boundaries identical to 0.00e+00 s. On the full ten minutes torch returns **225 turns** and both ONNX paths **222** — as time rather than turns, **565 of 300,000 frames, 0.19% of the timeline and 0.82% of speech**. The shorter clip simply holds fewer decisions close enough to a threshold to tip, so **the ten-minute answer is the one to believe**. Real-time factor, idle machine, seed pinned: **0.920 torch, 0.688 ORT CPU, 0.625 WebGPU** over ten minutes, 1.47×; 0.832 / 0.630 / 0.556 over ninety seconds, 1.50×. An earlier ten-minute timing set was discarded rather than quoted — it ran while the same machine was building and testing, and the torch pass took the worst of it, which flatters the comparison.

**It is the embedder and not the clustering's unseeded start, and that took two controls to establish.** `VBx.py:81` initialises its variational EM from `np.random.gamma` on numpy's unseeded global generator (gotcha 37), so it was the first suspect. With the initialisation **pinned to one seed** torch still returns 225 and both ONNX paths still return 222; and **torch against itself, unseeded, differs by 0 of 300,000 frames**, across four torch runs that all returned 225. What is left is float arithmetic: ONNX Runtime's kernels accumulate in a different order from torch's, worth about 1e-07 on an embedding, which occasionally tips a merge sitting near its threshold. **ORT CPU and WebGPU return the identical answer**, the same 565 frames — so this is torch-versus-ONNX-Runtime, not CPU-versus-GPU, and the GPU adds nothing to the divergence.

**Three things about how it is built are decisions rather than details.**

1. **The graph is derived from the pinned checkpoint, not downloaded.** pyannote publishes a `speaker-embedding.onnx` and the vendored fork has a loader for it, and **that path computes a different function**: `ONNXWeSpeakerPretrainedSpeakerEmbedding.__call__` binarises the speaker mask at 0.5, selects the surviving frames and runs one item at a time with no weights, where the torch path passes the soft mask into a weighted statistics pooling over every frame. It would not reproduce this project's figures, and its per-item loop discards the batching the speed-up comes from. Deriving from `pyannote-wespeaker-voxceleb-resnet34-LM.bin`, whose SHA-256 the catalogue already pins, keeps one artefact with one digest and redistributes no CC BY 4.0 weights. The cost is `onnx` and `onnxscript` in the bundle, now pinned in `python/requirements-bundle.txt`.
2. **`_vendor/` is untouched, and one identity is what made that possible.** `StatsPool.forward` vmaps its pooling over the speaker axis, and `torch.export` cannot see through `torch.vmap`: the traced graph comes back with the **batch dimension specialised to whatever it was traced at**, whatever `Dim` is declared, so a graph traced at 2 meets the pipeline's 32 with a shape error. The pipeline embeds one speaker at a time, so that axis is always 1 and the vmap is a single call to `_pool`; the export wrapper makes that call directly. It is an identity, and it is checked rather than asserted — `0.000e+00` against the vendored path.
3. **Its parity reference is torch, not the CPU provider, and it is checked on the CPU too.** Sortformer's reference *is* ORT's CPU provider, so checking the CPU against itself would measure nothing. DiariZen's reference is the torch embedder, so ORT's CPU provider is a different runtime from the thing it is compared against and earns its place the same way WebGPU does. The fixture is `diariser/embedding-parity-reference.npy`, three synthetic chunks through the embedder's own entry point at the pipeline's real geometry — 1598 fbank frames against a 799-frame mask, deliberately unequal, because an export that tied those axes together would pass a fixture built with matching sizes and fail on the first real chunk. **Three chunks and not two, because two is what the export traces at**: `torch.export` specialises a dimension to its traced size, and a fixture running at that size cannot tell a dynamic batch axis from a frozen one — it would pass and the first real batch would fail inside the pipeline. **The fixture passes at 1e-07 on a path whose labels move**, which is a limit of the instrument rather than a fault in it: it catches a graph that is *wrong*, and cannot catch one that is merely *divergent enough to matter downstream*, because clustering is a step function and no elementwise tolerance sees a threshold being crossed. What would catch that is a corpus with references, which is what the AMI run is for.

**What would promote it to `auto` is the measurement it has not had**: the AMI test set, sixteen meetings, the corpus the speaker gate is actually defined on. One ten-minute stretch is what excluded it, and one ten-minute stretch is not enough to let it back in. **What is still owed besides that**: `scripts/bundle-python.ps1` has not been run with the two new pins, so no installer carries `onnxscript` and the derivation has never been exercised from a `._pth` bundle; and transcription and diarisation have not been driven together in one run. `docs/UNPROVEN.md` has all of it.

**The window can reach both of them now, which it could not on 2026-08-27 morning.** The Settings tab gained a SPEAKER LABELS section with two controls, and the second diariser's fast path stopped being command-line-only. What made it unreachable was one line: `EngineProvider.CreateSpeakerLabeller` passed a hardcoded `"auto"`, and the comment above it gave the reason — the Models tab's backend row is parakeet.cpp's, Vulkan/CUDA/CPU, so binding the diariser to it would have offered backends it has no path to and hidden the one it wants. That reasoning was right and the conclusion was not: what it argued for is a **separate** control, not the absence of one. The two runtimes overlap only in the word "CPU" and mean different things by it, so the diariser's provider is now its own setting rather than a widening of the recogniser's.

- **`auto` is still the shipped choice and still means the published path** — `torch` on the second diariser, where ONNX Runtime is measurably faster and moves the labels. Naming a provider is how somebody takes that trade knowingly, and `SpeakerLabelling.DescribeEmbeddingBackend` has always had the sentence for it; until now the window called it and could never trigger it, which its own comment said outright.
- **Batch size is the user's, and its default is the absence of a choice.** `BATCH_SIZE = 8` was withdrawn the same day (below), so the pipeline runs the checkpoint's 32 unless somebody says otherwise. The picker offers 8, 16 and 32 — the only sizes anything has been observed at — and the copy calls it a memory setting and not a speed one, because the timing half of that sweep is withdrawn and the peak-memory half is not.
- **It is refused on the first diariser rather than ignored.** Sortformer's batching is its exported graph's streaming geometry, so `SidecarSpeakerLabeller` never sends the field for it and the sidecar raises if one arrives. The control draws disabled with the reason beside it, on the neural-speech-detection row's terms. A setting silently dropped is a person believing they configured something they did not.
- **The protocol went to 3 for exactly that reason.** An optional field whose absence is indistinguishable from acceptance is the case the version number earns its keep on: a version-2 sidecar would drop `batchSize` and leave both the window and the person believing a number was in force. `ProtocolVersionTests` holds the two copies together.
- **Verified where CI cannot reach.** The suite drives a fake sidecar, so the real path was driven by hand against the bundle's pins: no field gives 32, `batchSize` 8 and 16 give 8 and 16 read back off the loaded pipeline, `webgpu` moves the embedder while segmentation stays torch, and both the Sortformer refusal and a nonsense size raise `request`. Compiled bindings are on, so the new XAML is type-checked against the view model at build.

**The provider list is asked of the runtime, after a first version offered a row that could not work anywhere.** That first version listed CUDA unconditionally. The bundle pins `onnxruntime-webgpu`, whose wheel carries the WebGPU and CPU providers and **no CUDA one**, so the row would have failed the named-provider assertion on every machine — an NVIDIA card included, since which providers exist is a property of the installed runtime rather than of the hardware. The accompanying copy made it worse by saying "if you pick one this machine cannot use", which reads as a hardware limit and names the wrong cause. Corrected the same day: `SidecarExecutionProviders` asks the sidecar's `providers` op — the first thing on the .NET side ever to call it — and the picker offers Automatic plus what came back.

- **Null is not an empty list**, and the window depends on the difference. A probe that could not run reports "not established" and every row stays on offer, because a control emptied by a failed probe is worse than one that briefly offers too much. Empty would mean the runtime registered nothing, which cannot happen — the CPU provider is always there.
- **A stored choice the runtime no longer has stays visible, marked.** Dropping it would leave the combo reading "Automatic" while the settings file said `cuda`, which is the disagreement nobody can diagnose from the window; rewriting the file to match would be the window discarding a choice it was not asked to change.
- **It reads `available` rather than `usable`.** The latter is the sidecar's opinion about what may be chosen *automatically* and excludes DirectML on measured grounds — a different question from what a person may name.
- **The cost is a sidecar start, so it is probed once, lazily, when the Settings page is opened.** The op reports each engine's `auto` resolution as well as the raw list and pays the engines' imports to do it honestly, which is not a cost to put on every launch of an application most of whose users never open that page. Measured against the real bundle here: `["cpu", "webgpu"]` in 2 s.

**What is still owed for it to work on an installed copy**, and it is the same debt the entry above records: `scripts/bundle-python.ps1` has not been run with the two new pins. The bundle in `%LOCALAPPDATA%\Uindosill\python` carries `onnx` 1.22.0 and `onnxruntime-webgpu` 1.27.0 and **not `onnxscript`**, which `torch.onnx.export(dynamo=True)` needs — so on a shipped install today, choosing WebGPU derives no graph and stops with a refusal naming the missing module. That is the correct failure and not a silent fallback, but it is a failure: the feature is complete in a development tree and blocked on a packaging step everywhere else.

**Pinning the model digests used to head this list** and is done: all five entries carry the exact
byte size and the SHA-256 read from the repository's LFS listing, `"verified": true`, and no entry
needs `--allow-unverified`. `docs/MODELS.md` has the table. That settles *provenance* and settles
nothing about quantisation quality, which is what item 2 is for.

### Built 2026-08-28 — the diariser gets an ONNX route, and the GPU stops being unreachable on the second machine

**`--speaker-backend webgpu` selects something now.** `scripts/export-diariser-onnx.py` exports
pyannote community-1's two neural stages — `PyanNet` segmentation and the WeSpeaker ResNet34
embedder — and `PyannoteEngine._install_onnx_route` runs them through ONNX Runtime, replacing only
those two forward passes. The provider is refused by name, with the missing filenames, when the
graphs are not installed; nothing installs them, so that is the default.

**The maintainer asked for it after the alternatives were checked and none survived.** The second
machine's only GPU is an integrated Radeon 880M. There is no CUDA for it; PyTorch's ROCm wheels are
Linux-only; and `torch-directml`, the one Windows AMD torch backend, pins `torch==2.4.1` against
the bundle's 2.13.0. ONNX Runtime was the only route left, and `onnxruntime-webgpu` was already in
the bundle for the translator.

**What did not move, and that is the point.** The featuriser stays in torch — `compute_fbank` is a
`torch.vmap` over an FFT with no ONNX lowering, and wespeaker's own `infer_onnx.py` computes fbank
outside the graph for the same reason — and the sliding window, the powerset decoding, the PLDA and
the VBx clustering are all still upstream's code over upstream's objects. This project owns two
`InferenceSession`s and the shims that feed them, not a reimplementation of a diarisation pipeline.

**It agreed exactly on the recording it was checked against**: five minutes of a podcast, CPU torch
against WebGPU ONNX, 59 turns each, 2 speakers each, identical speaker labels, and a maximum
absolute difference of 0.000 s on both turn boundaries — at 1.57x the speed. Graph-level parity was
swept across batch sizes rather than spot-checked, because the TorchScript exporter warns that an
LSTM traced at one batch can bake it in and the pipeline uses two different ones.

**What it does not settle is accuracy.** No DER has been scored on either route, so this changes
which arithmetic unit runs the same numbers and says nothing about whether those numbers are right.
The speaker gate still names AMI test and is still unmet. `docs/UNPROVEN.md` carries both the
parity table and that gap, and `docs/GOTCHAS.md` 38–40 carry what the export cost to get right.

### Decided 2026-08-28 — `auto` elects the graphics route where its graphs exist

**The election moved to where the model directory is known.** The diariser's `resolve_auto` returned
`["cpu"]` unconditionally — correct while the pipeline was torch-only, and left alone by the commit
above, which made `webgpu` *reachable* by name without making it *chosen*. It is a shortlist now, on
the translator's terms: `["webgpu", "cpu"]` where the model directory holds both derived graphs, and
`["cpu"]` where it does not, which is every machine that has never run the preparation. It takes the
directory as an argument for that reason, and the `providers` op passes the loaded model's path so
the resolution it reports is the one a load would actually take.

**Only `auto` falls through.** A provider that registers can still fail to build a session, and a
diariser that drops to the CPU is better than one that will not load — but somebody who *typed*
`webgpu` and silently got the CPU has been told nothing, so a named provider still raises. Falling
through is safe because `_install_onnx_route` swaps the `forward` attribute as its last statement —
two of them until later the same day, one since the entry below — so every refusal above it —
missing graphs, an absent provider, a session that would not build, a graph ORT seated somewhere
other than where it was asked — leaves the pipeline untouched.

**What promoted it, and what did not.** Not a DER. There is none on either route, which is the same
in both directions and therefore favours neither. What promoted it is that the two were run against
each other in one process on a five-minute recording and produced the same turns to the millisecond
with the same speakers. That is an equivalence check rather than an accuracy one, and this project's
rule for automatic selection — that what it picks unasked reproduces the figure it publishes — bites
hardest where there is a published figure to reproduce, which here there is not. **Recorded as a
judgement rather than a measurement**, and `docs/UNPROVEN.md` carries it as one.

**`dml` is not in the order, and `cuda` is not either.** DirectML is exported for and has never been
executed on these graphs; the `ORT_DISABLE_ALL` precaution it carries is inherited from the diariser
in `attic/sortformer/` rather than earned here, so it stays behind a name. CUDA is out for the older
reason: it is a torch device on this pipeline, and the bundled torch is the CPU build.

**The election has a guard, because the sidecar has no test suite of its own.**
`scripts/check-diariser-auto.py` runs in CI beside the test-count checks and needs no toolchain — the
engine module defers torch and onnxruntime into the functions that use them, and the script stubs the
provider list, so the machines CI is not are checked too: one with a WebGPU build, one with DirectML,
one with neither. Thirteen cases, including that a single graph of the two counts as absent. It
caught a real defect on its first run: an argument default present on one of the two `resolve_auto`
names and missing from the other.

**The window's wording follows the election.** "Automatic is your processor, because the Python that
ships with Uindosill has no graphics build of the speaker model's runtime" was two things at once,
and both had stopped being true — the bundle has carried `onnxruntime-webgpu` since the translator
needed it, and automatic is the graphics card once the preparation is done. It has two states now,
keyed on the same file check the graphics row uses, so the two cannot disagree. The graphics row also
stops comparing itself to automatic, which it did until this change and which became circular the
moment automatic became the same route.

**A silent fall-through would have been the real cost, and it is not silent.** `auto` dropping to
the CPU because a WebGPU session would not build is the one fact that explains a slow run, and it is
known only at the moment it stops being true. The sidecar puts it in `fellBackFrom`, which
`ExecutionProviders.ReadFellBackFrom` has read from *both* engines since 2026-08-22 and which this
one had never had anything to put in; `SidecarSpeakerLabeller` surfaces it and `LabellerFactory`
says it once on stderr, as `TranslatorFactory` already did — and whose comment already claimed the
labeller did too. That is not an accuracy warning, which this engine still declines to make: it is a
statement about which arithmetic unit ran.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** One new, the twin of
the translator's: a scripted capabilities reply carrying a `fellBackFrom` entry must reach
`SidecarSpeakerLabeller.FellBackFrom`. The election itself is in the sidecar, which the C# suite
drives through a fake, so `scripts/check-diariser-auto.py` is what covers that and CI runs it.

### Measured 2026-08-28 — the graphics route was on the wrong stage, and now seats one

**An ONNX provider seats the embedder alone; segmentation stays in torch.** The route landed that
morning putting both neural stages on WebGPU, which was the obvious shape and the wrong one. Timed
per pipeline step on the second machine over a ten-minute 16 kHz stretch, the provider is **2.2x
faster at embeddings and 8.8x slower at segmentation** — 87.0 s against 192.1 s, and 48.7 s against
5.5 s — so seating both bought the embedding win and spent most of it again on the other stage.
Seating one is **92.8 s, 6.46x realtime**, against 135.8 s for both and 197.9 s for neither: 1.46x
faster than the route it replaces and 2.13x faster than the CPU.

**The output does not move, checked on the numbers.** Held against torch turn by turn, both the
old route and the new one are bit-identical — max |Δstart| and max |Δend| 0.000000000 s, no speaker
disagreement, 78 turns and 2 speakers on all three. That check is not a formality here: the
*previous* engine's ONNX embedder returned 222 turns where its torch one returned 225.

**Why the LSTM is the suspect and not the finding.** The segmentation model is SincNet, LSTM,
linear, and an LSTM is sequential. `onnx_export` records that opset 18 implements LSTM "for both
providers" — which is coverage, and coverage is not throughput. No per-node placement was inspected,
so the mechanism is a reason offered rather than a thing established.

**What this also settled, having gone looking for something else.** The clustering half is not the
cost and never was: `speaker_counting` and `discrete_diarization` together are **0.11 s of 135.8 s**,
so PLDA and VBx — the stages that cannot move to a GPU at all — are noise. The two neural stages are
effectively the whole wall clock, which is why the route they run on is worth this much attention.

**Two knobs were swept and neither is one.** Batch 32/64/128 took 135.8 s, 139.6 s and 144.2 s —
bigger is slower — and **batch 128 returned 79 turns and 3 speakers** where everything else returned
78 and 2, which is outside the sizes `PARITY_SEGMENTATION_BATCHES` checks; a repeat of it failed
outright, unexplained. Threads at 8/12/20 gave 132.1 s, 135.8 s and 136.6 s, so `DEFAULT_THREADS`
stays 12, which was chosen for comparability rather than speed in the first place.

**The two backend fields now disagree, which is what they were for.** A `webgpu` run reports
`segmentationBackend: torch:cpu` and `embeddingBackend: onnx:webgpu`. The pair existed against the
day a route split them, and the comment saying nothing did has been replaced by the reason one now
does. `Backend` answers neither half on its own and `ISpeakerLabeller` says so.

**What is not established.** No DER on any route — the equivalence above says the three agree with
each other, not that any is right. One file, one machine, single runs but for the repeats named;
`docs/UNPROVEN.md` § *Where the diariser's time goes* carries the numbers, the 0.9% run-to-run
variance, and the two runs discarded for having been executed concurrently. `dml` is still
unexecuted on these graphs, and the desktop — where a CUDA torch would carry fbank to the GPU too
and might want the opposite trade — is unmeasured.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** None new: the
seating is inside the sidecar, which the C# suite drives through a fake, and the election guard
`scripts/check-diariser-auto.py` covers `resolve_auto` rather than what a route seats once elected.
The evidence for this change is the measurement, not the suite.

### Decided 2026-08-28 — `auto` elects CUDA where torch has it, and the desktop stops being unmeasured

**The entry above closes saying the desktop is unmeasured and might want the opposite trade. It
does, by a wide margin, and this is that measurement.** An ONNX provider seats one neural stage; a
torch device moves both and the featuriser with them. On the RTX 5080 over the same ten-minute
16 kHz stretch, one venv, only `--backend` differing: **99.1 s on the CPU against 7.6 s on CUDA,
13x**, with a repeat pair at 112.4 s and 8.7 s. WebGPU's best on the second machine was about 2x.

**It changes nothing about the answer, which is the condition for electing it.** Six runs — two CPU,
two CUDA, and `auto` in each venv — returned 244 turns, 5 speakers and 670.2 s of speech, and their
RTTMs are byte-identical but for the file-id column each was given. **The CPU-against-CPU pair is
the control that makes that a result**: `VBx.py:81` seeds from numpy's unseeded global generator, so
cross-device identity without it could have been luck. WebGPU was promoted on a coarser check than
this one.

**The GPU did the work rather than registering for it** — 93–94% utilisation, 274.8 W, ~5.4 GiB of
VRAM, sampled through `nvidia-smi`. That is the torch-side answer to the question `placement.py`
exists to ask on the ONNX side, where a registered provider can own no nodes and say nothing.

**The election had to grow a second question, not a third entry.** `AUTO_ORDER` now mixes
vocabularies: `cuda` is a torch device and `webgpu` an ONNX Runtime provider, filtered on
*different* facts — whether torch reaches a device, against whether the wheel carries a provider and
the derived graphs exist. Running the graphs check over `cuda` would have made the fastest route
conditional on an export nothing installs, which is the bug `TORCH_AUTO_DEVICES` and the loop in
`resolve_auto` exist to prevent. `onnxruntime` is now imported only when an ONNX candidate is still
in play.

**A torch device is fallen through like any other candidate now.** It was resolved after the
election loop, where a raise ended the load — harmless while `cpu` was the only torch candidate
`auto` could reach, and not once `cuda` joined it. A *named* `cuda` still fails loudly.

**Nothing shipped changes, and that is not a hedge.** The bundle pins the CPU torch build, so an
installed copy still elects the CPU — measured, at 111.0 s through `auto` on `pyannote-venv`,
against 8.1 s through `auto` on a venv whose only differing line is the torch index (`whl/cpu` →
`whl/cu130`, giving torch **2.13.0+cu130**: the pinned version in its CUDA build, so the three
packages that decide the translator's decode are untouched). What this buys is a machine that
installed a CUDA torch itself, which today is one desktop.

**What is not established.** No DER on any route, here or anywhere — the identity says the routes
agree, not that any is right, and the speaker gate stays unmet by the shipping product. One
recording, one machine, one card, four speakers on a podcast cut; byte-identity here does not
generalise to overlap-heavy meeting audio or to the hour the product already warns about. The
determinism is observed rather than guaranteed. A 7.6-second load is not a thermal figure.
`docs/UNPROVEN.md` § *CUDA joined the diariser's `auto`* carries all of it, and
`runs/diariser-cuda/20260828-equivalence-5080/` the artefacts.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** None new, and one
changed: `DiariseCommandTests` asserted the help said the device's effect on labels "has not been
measured", which this makes false, so it now asserts the gap that is still open — no DER. The
election guard `scripts/check-diariser-auto.py` grew nine cases and now stubs torch as well as
onnxruntime, because a guard whose answer depends on which venv ran it is not a guard; it returns
the same result under system Python, `pyannote-venv` and `pyannote-cuda-venv`.

### Built 2026-08-29 — the CUDA pack: an overlay the bundle cannot carry, and the seam it plugs into

**The problem it solves is arithmetic.** The entry above puts `cuda` first in the diariser's `auto`,
and the shipped bundle pins the CPU torch build, so nothing an installed copy does can reach it. The
obvious fix — ship a CUDA bundle — does not fit: the `win-cuda` channel's Setup.exe was measured at
1,976,256,205 bytes against GitHub's 2 GiB asset limit, about 24 MB of headroom and that already
after dropping the diariser's weights from the channel.

**So the pack is an overlay rather than a second bundle, and the measurement is what made that
choice.** A CPU and a CUDA install of `requirements-bundle.txt` differ in exactly three packages —
`torch` 2778.4 MB against 489.8, `torchcodec` 38.2 against 23.4, `torchaudio` 9.2 against 2.3 — and
**on Windows there are no separate `nvidia_*` distributions at all**, the CUDA libraries living
inside `torch/lib`, where `cublasLt64_13.dll` alone is 456 MB. So the whole delta is three
directories: **2.76 GB, against about 4 GB for a second whole bundle**, and no change to the
three-place bundle resolution.

**The mechanism is `PYTHONPATH` order, and it was proven before anything was built on it.** CPython
puts `PYTHONPATH` ahead of site-packages, so the pack shadows the bundle's `torch` without replacing
a byte of it; deleting the directory undoes it completely. On the bundle's own interpreter,
`2.13.0+cpu` and `False` became `2.13.0+cu130` and `True` with the pack in front. **The dist-info
directories travel with it**, because `importlib.metadata` resolves along the same path and would
otherwise report the bundle's `+cpu` for a run on the CUDA build.

**End to end through the product, on the CPU-torch venv the app actually uses.** `uindosill diarise`
with `auto`, ten-minute stretch: **111.0 s without the pack, 8.0 s with it**, and the RTTM identical
to the CPU reference in both cases. The pack the new `scripts/bundle-python-cuda.ps1` builds gives
8.9 s and the same identical output, so the script's product is what was tested rather than a
hand-assembled stand-in.

**`ResolveCudaPack` checks user data before the application directory**, which is the opposite of
the bundle's order and deliberate: the pack cannot ship inside the installer, so an
`<app>/python-cuda` can only be something a hand put there, and a download must not be shadowed by
it. `UINDOSILL_PYTHON_CUDA` overrides both and names the pack itself rather than a directory holding
one, because that is the development case.

**A directory that exists but holds no torch is ignored rather than reported.** The pack accelerates
something that already works, so its honest failure is a diariser on the CPU — which is what a null
produces — rather than a load that refuses. The run's own provenance is what tells a user which
device was elected.

**What is NOT built, and it is most of the feature.** There is no download: no zip is produced by
`package-windows.ps1`, no catalogue entry describes the pack, nothing detects an NVIDIA card, and no
Settings row offers it. Today the pack reaches a machine only by being built with the script and
either unpacked as `python-cuda` beside the bundle or named by the variable. What this commit
delivers is the seam — the resolution, the wiring, the builder, and the proof that the overlay
works — which is what the download machinery would otherwise have been written blind against.

**Also unproven.** One machine, one card, one recording. The pack has never been zipped, so its
compressed size is unmeasured and whether it clears the 2 GiB asset limit as a single release asset
is unknown — which is a real question for the download design and not a detail. Nothing has been
tested against a *mismatched* pack, where the overlay's torch version differs from the bundle's pin;
the script refuses to build a non-CUDA pack but nothing checks the pair at run time.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** Seven new, all on
the resolution: the two places and their order, the variable, a bundle with no pack, a directory
holding no torch, and a pack found alongside an interpreter named by `UINDOSILL_PYTHON` — the last
being the case the `with` expression in `Resolve` exists for, since the environment branch returns
early and carried no pack until it was written. The wiring itself is one expression in
`PythonSidecar`, checked by hand against the real sidecar because the suite drives a fake one.

### Built 2026-08-29 — the pack becomes a download: four parts, a driver probe, and a measured limit

**The asset limit was the open question and it is now a number.** The pack compresses to
**1,961,716,087 bytes — 1.83 GB, 66.2% of unpacked** — which clears GitHub's 2 GiB asset limit by
**177 MB**. It fits, and it is shipped as **four parts** anyway. Two reasons, and the first is the
weaker one: 177 MB of headroom on an artefact that only reaches 66% because CUDA DLLs are already
dense is the same trap the win-cuda channel is in at 24 MB, and one torch point release would spring
it at release time. The second is the real one: **a 1.8 GB download that drops resumes at a part
boundary rather than at zero.**

**The split is a byte range, not an archive format's own spanning.** A `.zip.001` produced this way
concatenates back, so the client needs no library that understands multi-volume archives — and each
part carries its own SHA-256, which is what makes a resumed transfer *checkable* rather than merely
restartable. `manifest.json` beside them carries the whole archive's digest and size, the unpacked
size, the three package versions, and a row per part.

**The whole path was run, not reasoned about**: build → zip → split → four digests → reassemble →
whole-archive digest → unpack → `uindosill diarise`. The rejoined archive matched
`2a056a0d…` and its byte count exactly, and the diariser off the unpacked result returned **8.9 s,
67x realtime, and an RTTM identical to the CPU reference**. The zip's byte count also reproduced an
independent measurement taken before the script existed, which is the sort of agreement worth
noticing rather than assuming.

**The machine has to be asked whether it wants this, and the obvious probe is useless.**
`torch.cuda.is_available()` answers `False` on a machine with four cards in it, because the bundle
pins the CPU build — and the thing being decided is whether to *install* the CUDA build, so the
probe has to work before it exists. That puts it on the C# side, out of Python's reach.
`CudaDriverProbe` loads **`nvcuda.dll`** — the CUDA driver API the display driver installs — calls
`cuInit` and **counts devices**, on `placement.py`'s principle that a library which loads is
registration and registration is not capability: a driver DLL left behind by an uninstall loads and
enumerates nothing, and offering somebody a 1.8 GB download on a stale file is the failure the count
avoids. It answers the adapter-name question correctly in both directions a WMI query gets wrong.

**Measured on the RTX 5080: `Present`, one device, driver version 13040** — CUDA 13.4, which is what
`nvidia-smi` reports independently, so the probe is cross-checked against a source that is not
itself.

**Three answers rather than two**, following `GpuClass`: `Unknown` is not a synonym for `Absent`.
A library that loads and does not export the driver API, or an init that fails for a reason that is
not "no device", is a question this could not answer — and the caller is to say nothing on `Unknown`
rather than offer the pack on a guess. `CUDA_ERROR_NO_DEVICE`, `INSUFFICIENT_DRIVER` and
`SYSTEM_DRIVER_MISMATCH` are answers and map to `Absent`.

**What is still NOT built.** No release asset is produced by `package-windows.ps1` and nothing has
been uploaded; there is no catalogue entry, no downloader, and no Settings row. The parts and the
manifest exist as an artefact a build can make, and the probe exists to decide whether to want them,
and nothing yet joins the two. The remaining work is the install path — fetch four parts with
resume, check each digest, concatenate, check the whole, unpack, move atomically into
`python-cuda` — plus the row that starts it.

**Also unproven.** The 1.83 GB figure is one build of one pin set on one machine; a torch point
release moves it and nothing watches the number. Nothing tests a *mismatched* pack, where the
overlay's torch version differs from the bundle's pin — the builder refuses to produce a non-CUDA
pack, and there is still no run-time check of the pair. The probe's `Present` branch is unreachable
in CI and is held only by the hand check above.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** Four new, all on the
probe, and deliberately only the machine-independent part of it: that it answers rather than throws,
that a device count above zero implies `Present` and nothing else does, that the two negative answers
stay distinct, and that a second probe agrees with the first — which is the fault a Settings page
visited twice would otherwise find, since `Describe` frees the library it loaded.

### Built 2026-08-29 — the pack installs itself: a borrowed downloader, two refusals, and a row that hides

**The download is `ModelInstaller`'s and deliberately not a second one.** Fetching several pinned
files with per-file resume, per-file digests, a staging directory and an all-or-nothing move is a
problem this repository solved once already, over an entry of nine files. `CudaPackManifest`
adapts the pack into a `ModelDescriptor` whose files are the four parts, and `CudaPackInstaller`
adds only what a pack needs and a model does not: concatenation, a whole-archive digest, an unzip,
and a destination outside the model store.

**Two staging directories, for two different atomicity questions.** The installer stages parts and
moves them into `python-cuda.parts`; the pack installer stages the *unpacked tree* and moves that
into `python-cuda`. The second matters more than it looks — a half-extracted `python-cuda` holding a
torch missing some of its DLLs would satisfy `IsCudaPack`, go in front of the bundle on
`PYTHONPATH`, and break a diariser that worked yesterday. The parts directory is **kept** on
failure, which is what makes a retry resume rather than re-fetch 1.8 GB; the assembled archive is
removed either way, being a duplicate of what is already on disk in pieces.

**The whole-archive digest is checked as well as the per-part ones, and the pair is not redundant.**
The parts say each byte range arrived intact; the archive digest says they went back in the right
order and none was missed. A reassembly bug is invisible to the first and caught by the second, and
the message says which of the two failed so the reader is not sent looking for a corrupt download
that did not happen.

**Driven end to end against a local HTTP server**: four parts fetched, assembled, verified,
unpacked, moved, parts directory cleaned up — then `uindosill diarise` off the installed result at
**7.9 s, 76x realtime, RTTM identical to the CPU reference**. That run found a real bug and it was
this project's own: `Measure-Object -Sum` returns a Double, so the packaging script had been writing
`"unpackedBytes": 2965027252.0`, which `System.Text.Json` refuses as an Int64. A manifest this
repository produced could not be read by the code that consumes it, and only running the pair
together showed it.

**Two refusals, both before a byte is fetched.** A pack whose torch version differs from the
bundle's pin is refused outright — it would shadow a torch this build has never been measured
beside, and silently run a decode the translator's 8,149-sentence gate does not describe. This is
the **only** place that pairing can be checked: the builder can refuse to produce a non-CUDA pack,
but it cannot know which bundle it will land beside. And an unverified manifest is refused unless
the caller opts in, on `ModelInstaller`'s own terms. The version is checked **first**, deliberately:
a pack that is both wrong-version and unverified is first the wrong version, because that is the one
no upload would fix.

**The Settings row is drawn only where it would do something**, which takes two questions rather
than one: does the driver have CUDA, and is the pack already installed. A machine that answers no to
the first is not shown a 1.8 GB download it cannot use and **is not told about one either** — an
absent row is the honest treatment of a feature that does not apply, where a disabled row with an
explanation is an advertisement. `Unknown` from the probe counts as no, which is the whole reason
that third answer exists.

**The button is separately dead while the manifest is unverified, and the copy says why.** That is
the state this build ships in: `cuda-pack.json` carries the digests of a local build and no release
asset has been uploaded, so there is nothing at the URLs. A dead button with no reason beside it is
the failure that guards against.

**`package-windows.ps1 -CudaPack` produces the assets**, off by default because the step needs a
CUDA venv or about 3 GB of wheels and accelerates one opt-in on one vendor's hardware. It reads the
parts back like every other artefact there — each must exist, match its manifest size, and be under
the 2 GiB limit that made them parts — and prints the reminder that the digests still have to be
pinned into `cuda-pack.json` by hand off the upload.

**What is still not done, and it is now a short list.** Nothing has been uploaded, so
`cuda-pack.json` stays `verified: false` and the button stays dead; flipping it belongs in the same
commit as the upload, with the digests read back off the assets rather than off this machine. The
`baseUrl` names `v1.0.0`, a tag that does not exist yet.

**Also unproven.** The install was driven once, over localhost, on one machine — a real transfer
over a real connection, an interrupted one that resumes, and a disk that fills mid-unpack are all
untested. The 1.83 GB figure is one build of one pin set and nothing watches it. The probe's
`Present` branch remains unreachable in CI.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** Eighteen new: twelve
on the manifest and the two refusals, six on the Settings block. The app ones assert the
*relationship* between the probe, the installed state and the row's visibility rather than any of
their values, because CI has no card and the maintainer's desktop has one, and a test whose answer
depends on which machine ran it is a machine detector rather than a test.

### Fixed 2026-08-29 — a dropped connection closed the application, and the download path had no test

**What happened.** Hugging Face ended a response after **149,148 bytes of a 6,716,356,800-byte
file**. `HttpIOException` came out of `ModelInstaller`'s read loop, matched neither of the Models
tab's two catch clauses — `OperationCanceledException` and `ModelInstallException` — escaped an
async `[RelayCommand]` where nothing awaits it, and **the process was terminated**. The window went
with it. The event log named the exception, the byte count and the line.

**Everything needed to survive it was already on disk and none of it was used.** The `.part` file,
the resume metadata and the range request all existed — the installer resumes a download the *user*
interrupts. It had simply never been asked to survive one the *server* interrupts, because nothing
caught the exception that says so.

**Two defects, and they compounded.** The installer did not retry or wrap a transport failure; the
window caught only the installer's own exception type. Either alone is a bad download; together they
are a closed application. Both are fixed: `FetchAsync` retries on `IOException` and
`HttpRequestException`, re-reading the resume offset each time so a retry continues rather than
restarts, and turns a persistent failure into a `ModelInstallException` carrying the message a user
reads. The window gains a backstop clause for the same families, because **a download must never be
able to close the window** whatever it throws.

**The attempt budget resets on progress**, which is the difference between surviving a flaky link
and surviving five cut-offs. A connection that dies every 4 KB but keeps advancing is not the same
failure as a request that cannot be served, and only the second should exhaust a count.

**The backoff starts at 500 ms rather than 2 s.** The thing usually being waited out is one cut
response, not a service that is down: a retry that succeeds half a second later is invisible to
somebody watching a progress bar, where two seconds reads as a stall. Capped at 8 s, five attempts.

**There were no HTTP-level tests of this class at all**, which is how a download path that cannot
survive a dropped connection shipped. `HttpClient` is injectable on the constructor, so the
transport is faked entirely and no socket is opened: a handler that truncates the first response and
honours `Range` on the retry. Four tests — that a drop resumes rather than restarts (asserted on the
*range header of the second request*, since a retry that re-fetched from zero would pass a digest
check too and still re-download 6.3 GB), that repeated drops finish while progress is being made,
that a connection which never delivers fails as `ModelInstallException` with a message saying the
partial is kept, and that a real cancel is still a cancel rather than five retries of nothing.

**Verified against the server that broke it.** Resumed from the 149,148-byte partial the crash left
behind and ran on past 268 MiB, on the same URL a `curl` range request had already shown healthy at
4.2 MB/s — so the failure was a transient server-side drop throughout, and the defect was never
being able to take one.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** Four new, all on the
retry, and they are the first tests this repository has ever had over `ModelInstaller`'s transport.

### Built 2026-08-29 — one button for yt-dlp and Deno, and the one pin this product lets go of

**Why this exists in a repository whose whole discipline is pinning.** yt-dlp is the one vendored
binary with a shelf life of weeks: YouTube changes what it serves and yt-dlp changes to match, so
the version that worked at release is routinely the version that does not by the time somebody
installs. Deno goes with it, because yt-dlp needs a JavaScript runtime for YouTube's signature
challenge — its own documentation enables exactly one by default — and that interface moves too.
**A pinned yt-dlp is eventually a broken one**, and "reinstall the application to fix a download"
is not an answer.

**What replaces the pin is the publisher's own digest, and it is deliberately weaker.**
`vendor-tools.ps1` checks against a hash committed here; this checks against a hash fetched beside
the download. That cannot catch a compromised release — only a corrupted or tampered transfer —
and the trade is being made with eyes open rather than by omission. **Both publishers do publish
one, which was checked rather than assumed**: yt-dlp ships a combined `SHA2-256SUMS` in the
ordinary `<hash>  <name>` shape, Deno ships a per-asset file that is PowerShell's
`Get-FileHash | Format-List` output with the digest upper-case on a `Hash` line. Two formats,
neither of them this project's choice. **A release that stops publishing a digest is refused
rather than installed unverified** — that is precisely when an unchecked binary is least advisable,
and the tool already on disk still works.

**Nothing is written into the application directory.** Updates land in the user profile's
`tools` directory beside the models, which `BundledTools` now searches *before* the vendored copy
and *after* the environment override — a developer who said where the tools are meant it, and a
stale update in a profile must not quietly win over that answer. Writing beside the executable
would need elevation and would be reverted by the next Velopack update. The shipped build stays as
it shipped, and **deleting that one directory restores the pinned binaries**.

**The version comes from asking the binary**, not from a remembered string. A tool can be replaced
by hand, restored by an application update, or left half-written; a stored version would describe
something no longer on disk. Two process starts on a Settings page, and only when the button is
pressed rather than at launch.

**Different, not older.** yt-dlp versions are dates and Deno's are semantic, and a comparison
understanding both would be two parsers answering a question the publisher already answers — the
tag on the latest release *is* the newest. What is normalised is the `v`: Deno tags `v2.9.6` and
reports `2.9.6`, and treating those as different would re-download 42 MB on every press. An
installed copy *ahead* of the release reports as different rather than being silently ignored.

**One button, not a check followed by an update.** The question a user has is whether their
downloader is broken because it is old, and the answer they want is that it is not any more.
Failures are per tool and never thrown: one publisher being unreachable must not stop the other
being updated, and neither must close the window — which is the lesson of the model download that
terminated the process earlier the same day.

**Driven against the real publishers.** yt-dlp reported current at **2026.08.19** — matching
`vendor-tools.ps1`'s pin, so the parse agreeing with the committed hash is itself a check — and
**Deno was 2.9.5 against a published v2.9.6**, so the update path ran for real: downloaded,
verified against Deno's own `.sha256sum`, extracted from the zip, installed, and the search then
resolved to the new copy with the vendored 2.9.5 untouched beneath it.

**What is not done.** The CLI has no equivalent, so this is a window-only affair. Nothing checks on
a schedule or tells a user their yt-dlp is old — the button has to be pressed, which for a tool
that breaks on YouTube's timetable rather than ours is a real limitation and not a design. There is
no rollback beyond deleting the directory, and no way to pick a version.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** Ten new, all on the
reasoning that happens before a working binary is replaced: both checksum shapes read off the real
releases, a missing asset and a missing `Hash` line both refused, and the version comparison
including the `v` that would otherwise re-download 42 MB every press. The download itself is not in
the suite — it is a network fetch of tens of megabytes — and was driven by hand as above.

### Built 2026-08-29 — Settings splits in two, and Advanced says what its controls really are

**The page had stopped being one thing.** Five sections in which a button anybody might press —
update yt-dlp when YouTube breaks it — sat between a segment cap and a mixture-of-experts placement
picker. Those are not the same kind of choice and putting them on one scroll implied they were.

**The split is by one rule, which is what makes it reviewable rather than taste**: *Advanced is
anything that can make the output worse, refuse to load, or that needs a measurement to set
correctly.* Everything else is General. By that rule the audio cut, the diariser's device, the
batch size, the ask router, the evidence depth and the expert placement are Advanced; the yt-dlp
update, the graphics-acceleration offer, the access token, the ask model, the thinking toggle and
About are General.

**Two headings appear on both tabs, and that is the rule being applied rather than failing.**
SPEAKER LABELS holds both the graphics offer and the access token — things a person needs to get
speaker labelling working at all — and the device picker and batch size, which are ways to break it.
ASKING is the same shape. Splitting a heading is more honest than putting either half in the wrong
place, and settings pages do this everywhere.

**The advisory is on the page, not in a heading.** The window's one amber panel, on the terms the
Models tab's uninstall notice set. It says what a wrong choice costs — transcripts worse, or a model
that will not load, with an effect that is not always visible in the result — and it says the thing
this project has to say and most products do not: **every default here was measured and none of the
alternatives has been.**

**Advanced names its controls by what they really are.** "Run on" is prose; `diarisationProvider`,
and `--backend` on `uindosill diarise`, are what a power user can act on. Each Advanced control now
carries a technical line under it: the settings-file key, the CLI flag where one exists, and the
library or upstream field it maps to — `segmentation_batch_size` and `embedding_batch_size` for the
batch picker, Silero VAD through `Parakeet.Engine.SileroVad` for the detector, the Vulkan loader's
discrete-or-integrated answer for expert placement.

**One of those lines is a fact that was previously invisible and expensive to discover**: the three
audio-cut controls are **not written to `settings.json` at all** and reset when the window closes.
They live on `TranscribeViewModel` and no `AppSettings` property backs them. Somebody tuning a
segment cap and restarting had no way to learn that but by noticing.

**What it cost the tests, and the cost is the point.** `TheSettingsTabCarriesTheCutAndTheWayToAbout`
failed the moment the cut moved: a `TabControl` realises only its selected page, so the controls
were in the name scope and not being drawn — which is exactly the distinction `Drawn` was written to
make (gotcha 31). The test now switches the sub-tab rather than reaching past it with
`FindControl`, and asserts About *before* the switch, because the half of the claim that matters is
that somebody who opens Settings and touches nothing can still find the licences.

**Two tests added**, both on the things that are easy to quietly lose: that the advisory is drawn on
Advanced and says both halves of what it has to say, and that the technical names are present —
`diarisationProvider`, `--backend`, `segmentation_batch_size`, `Silero VAD`, `askExpertPlacement`,
and the sentence about `settings.json`. A relabelling that reverted to prose would otherwise pass
every existing test.

**What is not done.** The sub-tab is not remembered between launches, so Settings always opens on
General — which is the right default and still a choice nobody can change. Nothing links from a
General control to its Advanced counterpart. The CLI has no equivalent of the split, which matters
less than it sounds: `--help` already names every flag.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.**

### Fixed 2026-08-29 — the engine panel stops looking like the model you clicked

**Reported from the window: it appears on every model and does not make sense.** Selecting the
answering model drew a panel headed LOADED MODEL saying "Nothing loaded — press Load", with a
Backend picker reading Cuda, none of which applies to an answering model. The panel's own body text
had to say so: *"This panel loads the model that turns speech into text. Answering questions does
something else… and is never loaded here."*

**A panel whose copy apologises for the panel is a naming problem, not a copy problem.** Almost
everything in it is global — the loaded state, the backend picker, both notes are the recogniser's
and the session's — and only the Load button depends on the selection. Called LOADED MODEL and
placed under the entry somebody just clicked, it read as being about that entry.

**This was half-fixed already, which is what made the diagnosis quick.** The panel used to be drawn
*inside* the per-entry detail pane and was moved out for exactly this reason —
`LoadSaysWhyItIsDarkOnAModelItCannotLoad` records that. Moving it was not enough while its title
still named no model in particular. It is **SPEECH RECOGNITION ENGINE** now, and the hint is one
sentence saying the only thing the reader could not already see: where their model *is* used.

### Shelved 2026-08-29 — the 26B-A4B at UD-IQ4_XS, and the ambiguity that went with it

**Withdrawn from the offer by the maintainer.** It was the third answering entry and the second at
26B-A4B, differing from the shipped UD-Q4_K_XL only in quantisation. The file's digests move to
`deferred`, whose comment previously said deferral was always about licensing and now records two
reasons: that, and a quantisation withdrawn from the offer. Nothing about the file changed — only
whether it is offered.

**Shelving it fixed a real defect nobody had reported, and the tests are how that surfaced.**
`ClaimingEntry` attributes a loose file to a catalogue entry only when **exactly one** entry
declares it — a file declared twice is deliberately left unclaimed, because the catalogue cannot say
which entry it belongs to. Both 26B-A4B entries declared the same drafting head
`mtp-gemma-4-26B-A4B-it.gguf`, so a copy of it in the models folder was reported as belonging to
nothing, under a Delete button, while the entry that installs it sat above. **One claimant remains,
so the head is now correctly attributed.**

**Which retired a test by making its case unreachable.** `AHeadWithNothingToDraftForIsStillDeadWeight`
used that head and had been passing for a reason that was never the intended one: not because the
head was an orphan, but because it was *ambiguous*. It now uses a head no entry declares, which is
the case the sentence under test is actually about, and asserts `ClaimedBy` is null so the reason it
passes is the reason it is named for.

**What the shelving does not do**, and it is worth writing down rather than discovering later:
several measured defaults were taken on this exact quantisation on the second machine, 2026-08-27
and 28 — the expert-placement and context notes in `LlamaServerOptions`, `AppSettings` and
`ModelFit` cite it by name. Those records stay accurate as history and now describe a file this
build no longer offers. They were not rewritten: a measurement is a record of what was run, and
editing it to name a model that was not the one measured would be the worse of the two errors.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** None added and four
changed: two catalogue listings that enumerate every entry, and two fixtures repointed at the
surviving sibling — a rename rather than a rewrite, since both entries declare the same head and the
pairing under test is unchanged.

### Measured 2026-08-29 — the expert picker was inert on CUDA, and the fix that looked obvious was wrong

**Two things were true and only one of them was a bug.** Expert placement was written *inside* the
Vulkan block in `BuildEnvironment`, next to `GGML_VK_DISABLE_BFLOAT16`, because the measurement that
produced it was a Vulkan/UMA failure. So on a CUDA child the Expert layers picker set no environment
at all: choosing "System memory" changed nothing, silently, which is the inert control this window
refuses to ship everywhere else. **That is fixed** — both explicit placements now reach a CUDA child.
The vendored CUDA drop takes the same flags, checked rather than assumed: `--cpu-moe`, `--n-cpu-moe`
and `--no-host` are all in its `--help`.

**The obvious next step was to extend the fit rule too, and it was written, tested green, and then
withdrawn — because the measurement said it was a regression.** The reasoning that led there was
arithmetic: the 26B-A4B at UD-Q4_K_XL is 15.84 GiB of weights plus a 0.43 GiB drafting head against
a 15.92 GiB card, `-ngl 999` asks for all of it, so it cannot load. **It loads.** On an RTX 5080,
CUDA, no offload: healthy, **15,731 MiB of 16,303 MiB**, generating at **22.4 tok/s** and prompting
at 33.2.

**The arithmetic was right in its parts and wrong where they joined.** `-ngl 999` does not put every
tensor on the card. VRAM used came in some **493 MiB below the file size** — and that figure already
includes the KV cache at a 16,384-token context and the compute buffers, so well over a gibibyte of
the file is not on the GPU. `token_embd.weight` is 0.73 GiB of this model and is a lookup rather
than a matmul, which makes it the likely resident of system memory, though llama.cpp's own buffer
breakdown was not captured and that last step is inference rather than measurement.

**So "does the file fit in VRAM" is the wrong question on a discrete card** in a way it is not on a
UMA split, and `FitsOnDevice`'s allowance — the file plus a quarter of it plus a gibibyte, about
20.8 GiB here — is calibrated for the wrong machine. Applied to CUDA it would have offloaded 13.4
GiB of experts to system RAM to fix a problem this card does not have. `Automatic` on CUDA therefore
keeps doing exactly what was measured working, and the fit rule stays Vulkan's until somebody
measures what "does not fit" costs on a card.

**`--no-host` stays Vulkan's on its own merits.** The "both or neither" rule it comes from is a
statement about a UMA driver splitting memory into two ~7.8 GiB heaps, where "CPU" placement lands
in the pinned heap and overflows it. A discrete card has no such split, so pairing the flags there
would carry a workaround to hardware without the fault.

**What the model actually is, read out of the GGUF rather than estimated**: 658 tensors, 15.83 GiB
of data, of which `*_exps` are **13.43 GiB — 84.8%** — and everything else 2.40 GiB. That 85% is
why an A4B mixture is a good candidate for offload when offload is needed, and it is the number that
would make the trade worth measuring on a card that genuinely cannot hold one.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** Two new and one
rewritten. The rewritten one asserted that placement decided nothing off Vulkan, which was the
defect; it now covers the CPU backend, where the claim is still true and for a reason — every weight
is in system memory already, so the pair would name a move with nowhere to go. The new pair cover
the picker reaching a CUDA child, and `Automatic` on CUDA deliberately leaving placement alone, with
the 5080 measurement written into the test that depends on it.

### Changed 2026-08-29 — two stale descriptions, and every em dash out of the window's text

**Two Models tab descriptions had stopped being true, and one of them tonight's measurement
contradicted outright.**

The 26B-A4B said *"it wants about 18 GB of memory free once it is running. On a computer with 16 GB
of memory or less it will be slow or will not start at all."* That is `ModelFit`'s arithmetic, and
`ModelFit` reads **total system memory** and says so in its own remarks: "nothing here knows what a
discrete card is holding." The sentence was written from a shared-memory laptop and read as a
statement about every machine, so a reader with a 16 GB *card* was told it would not start - and it
does, measured that evening at 15,731 MiB of 16,303 MiB and 22.4 tok/s. The note now splits the two
machine shapes and says which one the warning can see.

The diariser's said it ran *"about five times faster than real time on a processor"* and stopped
there, which was the whole truth when it was written and has not been since two GPU routes arrived.
It now carries both, with tonight's figures: the same ten minutes in eight seconds rather than a
hundred and eleven, and the same speakers and boundaries either way.

**And the em dashes are gone from everything the window draws.** 108 of them, across the view
models, the XAML's rendered attributes and the catalogue's descriptions.

**What was deliberately left alone, because "UI text" is narrower than "text":**

- **Comments and XML docs keep theirs.** They are this repository's house style, nobody outside the
  source reads them, and rewriting them would be a several-thousand-line diff that changes nothing
  anybody sees. 103 remain in `MainWindow.axaml`, every one inside an XML comment.
- **The command line keeps its own** - `Commands.cs` alone has 27 - along with the subtitle and
  Audacity writers and the answer prompt. Those are a different surface, and the ask was the window.
- **Test fixtures keep theirs, and that one was nearly got wrong.** The first pass took 20 out of
  `tests/`, and four of them were the point of their tests: `SearchTokenizer.Tokenize("— … !?")` and
  `AlphanumericToken("—")` exist to prove an em dash in transcript text yields no token, and a
  German-number fixture carries one to prove it survives the rewrite. Substituting there would have
  left those cases untested while the suite stayed green. Reverted, and only the two assertions that
  genuinely mirror changed UI copy were followed.

**The substitution is a spaced hyphen, character for character**, because no punctuation mark
replaces an em dash mechanically: a comma makes a splice of a clause join and a semicolon is wrong
for an appositive. Keeping the role and changing the glyph is the only transform that is always
grammatical.

**One string needed a comma instead, and the suite found it.** The Ask tab's copied overview writes
its claims as `- ` bullets, so putting a hyphen in *"Generated by a language model - not transcribed
speech."* made the header contain a bullet marker;
`TheCopiedOverviewLeadsWithTheFramingSentenceAndItsTimes` locates the first claim by searching for
`"- "` and found one in the header instead. It reads "Generated by a language model, not transcribed
speech." now. **That is the argument for reviewing a mechanical substitution rather than trusting
it**, and the only collision of its kind in 108.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** None added; two
assertions followed the copy they mirror.

### Built 2026-08-29 - the models come off the disk in one action, and the uninstaller still cannot ask

**Reported from the window: tens of gigabytes must not be left behind by an uninstall.** The folder
notice was true and useless. It said the weights survive an update, a reinstall and an uninstall,
stated as a property and stopped, and left a reader who had just decided to uninstall with six
entries to select and Remove one at a time. 25.67 GiB stays behind when somebody does not know to do
that, and "it is in the notice" is no defence when the notice frames the survival as a feature.

**Remove all downloaded models is one button now**, beside that notice, and the notice says plainly
that uninstalling leaves them and that this is the only place they go. Catalogue entries only: what
else is in the folder is offered separately, because this application did not put it there and
cannot say what it is. A loaded model is skipped rather than deleted from under the engine, and
named in the result, so the count and the message agree with what actually happened.

**It is deliberately not an uninstall hook, and the reason is on the record rather than a
preference.** One was built on 2026-08-22 and withdrawn the next day: run directly it deleted the
whole data directory, run by the uninstaller it returned in **98 ms having deleted nothing**, and
Velopack declining to invoke the callback, a lost registration, an exception inside it, a reparse
point, missing assembly metadata and sheer file count were each eliminated by experiment - the last
with a real package, a real silent install, a real `Update.exe --uninstall` and a 43,789-file decoy
that deleted in 6.3 seconds. **The failure never reproduced.** The rule that came out of it is the
one this obeys: nothing this product does unattended deletes a user's files. A button is attended.

**Asked to investigate making the uninstaller ask instead, and the answer is that it cannot, yet.**
Velopack 1.2.0 exposes exactly six lifecycle hooks - `OnFirstRun`, `OnRestarted`, and fast callbacks
for after-install, before-update, after-update and before-uninstall - read off the assembly rather
than off the documentation. There is no hook that can prompt, and none that runs *after* the
uninstall. The only uninstall hook is `OnBeforeUninstallFastCallback`, whose budget is 30 seconds:
a modal question inside it either blows that budget while somebody is away from the keyboard, or is
killed mid-answer.

**The one shape that could work is blocked on the same mystery.** The fast callback could launch a
detached helper and return at once, leaving the helper to ask and to delete after the uninstaller
exits. That is built on the exact callback measured returning in 98 ms having done nothing, so a
prompt on top of it would be a prompt that may never appear. **Reproducing the 98 ms no-op is the
prerequisite, not the prompt**, and reproducing it needs a real package, a real install and a real
uninstall on a machine - which is why this entry stops here rather than guessing.

**What is therefore still true after this change**: somebody who uninstalls from Add/Remove Programs
without opening the application first still leaves the models behind. The button narrows that to
people who never open the Models tab; it does not close it.

**1633 tests, no weights, no display, no network - 1624 passed and 9 skipped.** Six new, on the one
action in this window that deletes tens of gigabytes on a single click: that it removes every
installed entry, that it reports what it freed rather than only that it worked, that a loaded model
is left alone and named, that it is refused mid-batch, and that the notice tells somebody
uninstalling to clear it here. One changed: the notice assertion tracked a phrase that the rewording
moved, and now tracks the measurement it was really about.

### Built 2026-08-29 - the uninstaller asks, which two of the three reasons it was removed allowed for

**Asked for directly, after the register said it could not be done.** The previous entry concluded
that Velopack 1.2.0 has no hook that can prompt. That was true of the hooks and wrong about the
conclusion: `OnBeforeUninstallFastCallback` cannot show an *Avalonia* dialog, because it runs from
`VelopackApp.Run()` before the toolkit is up and on a build about to be deleted. `MessageBoxW` is in
`user32`, needs no toolkit, blocks, and returns an answer. "An uninstaller cannot ask anybody
anything" had been written into `Program.cs` as a reason and had never been tested.

**Two of the three reasons the 2026-08-23 hook was withdrawn are answered by asking.** It deleted
silently from a folder people keep their own files in, and it made uninstall-then-reinstall cost a
multi-gigabyte re-download without warning. The dialog names the size and the path, leads with the
reinstall case, and defaults to keeping.

**The third reason stands and is not answered, so the design fails towards it.** That callback was
measured returning in 98 ms having done nothing, on the same machine and build that deleted 4.64 GB
in another run, with six causes eliminated by experiment and the failure never reproducing. Nothing
here fixes that. What it does is make every failure land on the behaviour the product had the day
before: no interactive desktop, a refused call, an exception, a callback that never fires, an answer
that is not an explicit Yes - each leaves the downloads exactly where they are. **The only path that
deletes anything is a human pressing Yes.**

**Keep is the default button on purpose.** Reinstalling is the common reason to be on the Installed
apps screen, and Escape or a hurried Enter should not cost 25 GiB. The wording carries which answer
is destructive rather than relying on button order, because Yes and No say nothing on their own.

**It does not ask about small folders.** Below 64 MiB the uninstall is silent as before: somebody
uninstalling is not on that screen worrying about four megabytes, and a dialog nobody needed is its
own defect.

**`UninstallCleanup` comes back unchanged, guards and tests together.** The directory must carry the
expected name, must not contain the install root, a link is unlinked rather than followed, a file
that will not delete strands only itself, and nothing throws. Those were written for the unattended
version and are worth more now rather than less: what changed is who decided, not how carefully it
is done.

**What is still not true.** Nothing has been run against a real uninstall. The dialog has never
appeared on a real Installed apps click, because that needs a real package, a real install and a
real uninstall on a machine, and the 98 ms failure it rides on is exactly the thing that does not
reproduce on demand. The register and the tests both say so rather than implying otherwise.

**1633 tests, no weights, no display, no network - 1624 passed and 9 skipped.** Thirteen new: seven
on the prompt's decisions and its wording, six restored with `UninstallCleanup`. The dialog itself
is not exercised and cannot be, since it blocks on a human, so what is tested is everything either
side of it - whether asking is warranted at all, and whether the text tells somebody reinstalling to
say No. The skip count moves from seven to eight, the new one being the link test that needs
developer mode on Windows.

### Fixed 2026-08-29 - the window, the help and four documents catch up with an uninstaller that asks

**Found while auditing the installer before a release tag, and it is the sharpest kind of drift:
the product contradicted itself about deleting tens of gigabytes.** The uninstaller learned to ask
earlier the same day. Nothing else was told. Eight surfaces still stated the old promise, and two
tests held it up:

| where | what it said |
|---|---|
| `ModelsViewModel.UninstallNotice` | "uninstalling Uindosill leaves them behind" |
| Updates tab, `MainWindow.axaml` | "not touched by an update, or by uninstalling" |
| `uindosill models --help` | "they survive updates and uninstalls" |
| `README.md` | "survives an update, a reinstall and an uninstall alike" |
| `docs/MODELS.md` | "Uninstalling the application does not touch this folder" |
| `docs/GOTCHAS.md` gotcha 8 | "an uninstaller runs unattended and cannot ask anyone anything" |
| `docs/UNPROVEN.md` | "the feature is gone", "no longer any code that does" |
| `WindowTests`, `RemoveAllModelsTests` | asserted "leaves them behind" and "remove it here first" |

**A green suite was enforcing the false claim**, which is the part worth keeping in mind: two
assertions had been written against wording rather than against the thing the wording was about, so
the change that made them wrong could not fail them. They now assert the claim.

**The notice takes its threshold from `UninstallPrompt.AskAboveBytes` rather than a number typed
beside it.** This is not tidiness. Below that threshold the uninstaller is silent and deletes
nothing, so a window promising a question that will never be asked would be a worse untruth than
the one being replaced here. Above it the implication holds in the direction that matters: the
models sit inside the directory the prompt measures, so a models total past the threshold puts that
directory past it too, and the question is certain. A redirected `UINDOSILL_MODELS_DIR` breaks that
the other way and errs towards warning about a deletion that cannot reach them, which is the safe
direction for a warning to be wrong in.

**`NoticeFor` is separated from the property so the third branch can be tested at all.** Reaching
it through a fixture would mean writing 64 MiB in a suite whose whole discipline is that it needs no
weights, so the branch that carries the new claim would have gone untested, which is how this
happened in the first place.

**What did not change, because it is still true.** *Nothing this application does unattended
deletes a file on your disk.* A dialog is attended. The rule survived the 2026-08-23 removal and it
survives the return; what changed is only that a person is now asked, and every failure short of an
explicit Yes still leaves the downloads where they are.

**The register gained the entry it was missing.** The previous entry claimed "the register and the
tests both say so" about the prompt being unproven. The tests did. `docs/UNPROVEN.md` did not: it
had never been touched, and still said the feature was gone. It now carries
*The uninstaller asks, and no real uninstall has ever seen it*, including the one interaction
nobody had written down: a Yes answered late against tens of gigabytes reaches the 30-second
fast-callback budget, and what a half-deleted `models\` directory costs is reasoned rather than
observed.

**1633 tests, no weights, no display, no network - 1624 passed and 9 skipped.** One new, on the
three branches against the threshold the uninstaller itself uses. Three changed: two that asserted
the retired wording, and one renamed for the branch it actually reaches.

### Shipped 2026-08-29 - the CUDA pack becomes a download that exists, and the pin is not reproducible

**The pack had been built, tested and wired up, and could not be installed by anybody.**
`cuda-pack.json` carried `verified: false` over a `baseUrl` naming `v1.0.0`, a tag that does not
exist, so `CanInstallCudaPack` was false in every build and the Settings copy said so: *"This build
has no published download for it yet."* That was the correct state and a deliberate one. It is not
a state to leave a feature in.

**Built fresh rather than trusting the recorded pin, and that turned out to matter.** The same
`requirements-bundle.txt`, the same `cu130` index, the same three package versions
(`torch 2.13.0+cu130`, `torchaudio 2.11.0+cu130`, `torchcodec 0.16.0+cu130`, checked against the
bundle's own `torch==2.13.0` pin) produced a **different zip**: 1,961,743,736 bytes against the
1,961,716,087 recorded on the same day, a difference of 27,649 bytes, and a different SHA-256.
Nothing about torch moved. `requirements-bundle.txt` pins 26 direct packages with `==` and pins
**nothing transitive**, so the closure drifts under both artefacts whenever an upstream point
release lands.

**The consequence is a rule rather than an observation: the pack is not byte-reproducible, so its
digests must be read off the artefact that shipped.** The previous pin described a build on this
desktop that no longer exists and that nobody could have reconstructed. `docs/PHASES.md` already
records the same mechanism eating the win-cuda channel's headroom between rc.5 and rc.6; this is
the second artefact it reaches, and the first where a stale pin would have presented to a user as a
digest mismatch after 1.8 GB had been fetched.

**The read-back happens after the upload, and the ordering is forced rather than chosen.** The
release cannot exist until the commit carrying this file is tagged, and the file must carry
`verified: true` for the tagged build to offer the download at all. So the digests go in from the
parts that are then uploaded byte for byte, and the confirmation that GitHub stored them unchanged
is performed against the uploaded assets and recorded in `docs/UNPROVEN.md`. A pin taken from a
local build whose upload has not happened goes back to `false`.

**CI does not build the pack and will not upload it.** `-CudaPack` is off by default because the
step needs about 3 GB of wheels, and `.github/workflows/release.yml` does not name the parts in its
asset list. The four parts and their manifest are a by-hand step after the release publishes, which
is the shape this stays until somebody decides the 1.8 GB is worth a release job's time.

**Two assertions were written as reminders and have now been spent.**
`TheShippedManifestIsUnverifiedUntilTheAssetsAreUploaded` and
`TheButtonIsDeadWhileTheManifestIsUnverified` both asserted `Assert.False(...Verified)` with a
comment saying that when they start failing, the release has happened. It happened. Both were
rewritten to hold the relationship in either state rather than the value in one: the button is live
only where the flag says the parts exist, and a verified manifest must name a release tag its parts
can actually be fetched from - a flag set true over an unreleased tag being precisely the failure
the flag exists to prevent.

**The win-cuda channel was measured whole before the tag went out**, because rc.4 died twice at
GitHub's per-asset limit and a release job takes 35 minutes to find out.
`UindosillDesktop-win-cuda-Setup.exe` came to **1,998,899,901 bytes**, which is **141.7 MiB under
the 2,147,483,648-byte ceiling** and 100,079 bytes larger than rc.6. The read-back held: `cpu`,
`cuda` and `vulkan` inside, `llm/cuda` as the ask engine, all three companions with their notices,
`silero-vad-v5.1.2` as the only weight, and a bundled Python of 459,562,905 bytes.

**One thing this does not change.** The historical figure at *Built 2026-08-29 - the pack becomes a
download* quotes the digest `2a056a0d…` for the local install it describes. That observation stands
as what was run that day; it is simply no longer the artefact anybody can fetch.

**1633 tests, no weights, no display, no network - 1624 passed and 9 skipped.** None added; three
rewritten as described, and one renamed with them.

### Fixed 2026-08-29 - the bundled Python's closure stops floating, after it cost three things in one afternoon

**`python/requirements-bundle.txt` pins 26 packages and pins nothing they depend on.** The other 83
distributions in the bundle were whatever pip resolved on the day. That was known and untroubling
right up until it was not, and then it cost three separate things within a few hours:

| what | how much |
|---|---|
| `v1.0.0-rc.7`'s release job | **failed after 30 minutes**, on the bundled-Python notice check |
| the CUDA pack, rebuilt from identical pins | zip **27,649 bytes** larger, different SHA-256, torch unchanged |
| the win-cuda installer | **100,079 bytes** larger than rc.6, against 141.7 MiB of margin |

The release failure was one line: `kiwisolver` went 1.5.0 to 1.5.1 between rc.6 and rc.7. Still 109
distributions, still 248 licence files, one version string. Three levels below `pyannote.audio`, by
way of `matplotlib`, and nothing in this repository had an opinion about it.

**The pack failure is the worse of the three**, because it is silent rather than loud. A pinned
digest that cannot be reproduced is not a pin: it is a record of an artefact, and the only way to
obtain a correct one is to read it off whatever happened to ship. That is how `cuda-pack.json` came
to be filled in from a build rather than from a decision.

**`python/requirements-bundle.lock.txt` is the closure, and it is applied as a pip CONSTRAINTS file
rather than as a requirements list.** The distinction is the whole design. `-c` binds a version
where a package is installed and never causes an installation, so the lock cannot add anything to
the bundle. What it does is stop the 106 packages it names from moving.

**Three are deliberately absent: `torch`, `torchaudio` and `torchcodec`.** They are the only entries
carrying a local version suffix, and that suffix is chosen by the index the install runs against,
not by this repository: `+cpu` in the bundle, `+cu130` in the pack. Constraining them would pin the
CPU build into the artefact whose entire purpose is to be the CUDA one. Their versions stay pinned
where they belong, and `CudaPackInstaller` still checks the pair at install.

**What the lock deliberately does not catch is a genuinely new transitive package.** Constraints do
not forbid one, so an arrival would install at its latest version. That is caught one step later by
`scripts/collect-python-notices.py --check`, and it belongs there: a new distribution is a new
licence, and it wants a person rather than a pin. The guard that failed rc.7 becomes the backstop
instead of the tripwire.

**Verified rather than assumed**, three ways, before it was committed:

- **Constraints bind.** A dry-run resolve of `matplotlib` under a scratch constraint chose
  `kiwisolver-1.5.0` with 1.5.1 available and current. That is the rc.7 failure, pinned shut.
- **The lock and the requirements are satisfiable together**, which is the failure that would only
  have appeared inside a release job. A dry-run resolve of the full set under the lock produced
  **109 packages**, and the only three at versions the lock does not name were `torch`,
  `torchaudio` and `torchcodec`, each at its index-chosen `+cpu` build. Everything else landed on
  exactly the pinned version.
- Both scripts parse.

**`bundle-python.ps1 -WriteLock` is the only supported way to move it**: resolve freely, then
rewrite the lock from `pip freeze --path` of the bundle just assembled, off the bundle rather than
off the host. A switch rather than the default, because every other run of that script is meant to
reproduce a bundle rather than choose one, and the note it prints says to run
`collect-python-notices.py` in the same commit so that NOTICE.md and the lock describe one bundle.
`bundle-python-cuda.ps1` takes the same constraints, which is what makes the pack's digests
reproducible rather than merely recorded. Neither throws when the lock is missing: a checkout
without it can still build a bundle, and both warn instead.

**Not claimed.** That the bundle is now byte-identical across machines. Nothing here has built it
twice on two machines and compared, and the wheels are still fetched from PyPI rather than
vendored, so this removes version drift and not every source of difference. What is established is
the resolution above: the same versions, in the same set, on this machine, under the lock.

**1633 tests, no weights, no display, no network - 1624 passed and 9 skipped.** None added: the
suite does not assemble a Python bundle, and the checks that matter here are the two resolves above
and `collect-python-notices.py`, which CI already runs against the bundle every release builds.

### Changed 2026-08-29 - the uninstall question reads the same way in the title as in the body

**The title, the body and the buttons all ask one question now.** `MB_YESNO` cannot relabel its
buttons, so the only thing that can carry which answer is destructive is the text, and the title is
the line a reader skims. It is a constant beside `UninstallPrompt.Message` rather than a literal at
the call site, so the two are read and changed together, and a test holds them in step: the body
ends "Delete them?", the title asks about deleting, and neither may frame the choice as keeping.

**The dialog also says what happens if nobody answers**, which is not hypothetical. The callback
runs inside Velopack's thirty-second budget - its own documentation says a fast callback is
terminated at thirty seconds, and `VelopackApp` exposes six hooks and no timeout - so an unanswered
dialog is closed when that expires. Measured on a real uninstall the same day: the files were left
in place, 14,625 of 14,625 and byte-identical. Walking away is the safe outcome and the text now
says so rather than leaving a reader to guess.

**1633 tests, no weights, no display, no network - 1624 passed and 9 skipped.** Two new: one that
the title and the body point the same way, one that the text says what an unanswered dialog does.

### Fixed 2026-08-29 - the update check searches the train the build is on

**Every release this project has published is a prerelease, and the updater was told to ignore
those.** `GithubSource`'s flag is documented as "if true, pre-releases will be also be searched /
downloaded. If false, only stable releases will be considered", and `VelopackUpdater` passed
`false`. With no stable release in existence the candidate list was empty **by construction**: rc.6,
rc.8 and rc.9 each arrived newer than the one before, and no installed copy could have found any of
them.

**The mechanism was already written down and its consequence was not.** `docs/UNPROVEN.md` named the
`prerelease: false` filter the day the section was written, when rc.3 was the only release and
"nothing newer exists" and "nothing newer can be seen" were indistinguishable. Three releases later
they are not, and the entry now says which one it was.

**The flag is decided by the running version rather than fixed either way.** A build whose own
version carries a prerelease label searches prereleases; a stable build does not. Fixing it to
`true` would be the same mistake deferred, offering the next candidate to somebody who chose to
install 1.0.0, which is the whole reason rcs are marked prerelease in the first place.

**Read off the version string rather than a version type.** SemVer puts the prerelease label after a
hyphen and build metadata after a plus, and only the first decides the train:
`1.0.0+5fb4a10e...` is the shape this project's own assemblies carry and it is stable. Reading the
hyphen out of the metadata would put every stable build on the candidate train.

**What this cannot do is fix an older install.** The check runs inside the installed build, so no
release published before this one can be rescued by it: the earliest release that can exercise the
path is the one after the release that carries it.

**1633 tests, no weights, no display, no network - 1624 passed and 9 skipped.** Ten new, all on the
one decision, including the two shapes that would each break it in a different direction.

### Fixed 2026-08-29 - three ways a fault or a cancel at the wrong moment took an engine down

**An adversarial review of the three pipelines confirmed nineteen findings, and these are the three
that fire on shipping configurations.** Each was re-traced by an independent second reading before
anything was changed; the sixteen low-severity findings are recorded there and are separate work.

**The diariser's `auto` falls through a CUDA fault at load instead of refusing to load.**
`resolve_auto` elects `cuda` on `torch.cuda.is_available()`, a driver query that creates no
context; the first call that actually touches the device is `pipeline.to()`, and it sat outside
the tolerant loop — so a card whose memory another process already held (a local llama-server,
say) failed the whole load with a raw traceback, where the shortlist's own contract promises the
next candidate. The move now happens inside the loop: a `.to()` that fails restores the pipeline
to the CPU whole, returns the allocator's cached blocks, records the reason in `fellBackFrom`, and
the load goes on. A *named* device still raises, by the same rule it always did. No automated
coverage, as before: the election runs only on a real machine.

**A cancelled write can no longer tear a request line on the sidecar's stdin.** A line longer than
the writer's buffer crosses the pipe in more than one flush, and the cancellation token was
honoured between them: the prefix sat on the pipe with no terminator, the next request's line
glued onto it, the child answered the glue with an id of null — recorded as noise — and that
request's caller waited forever, with translate lines (whole segments, every non-ASCII character
escaped to six) the ones long enough to hit it. Once the write gate is held the write is now
committed: it runs to completion on its own, holding the gate, and a caller that stops waiting
mid-write abandons the request exactly as one that stops waiting after it.

**The ask panel re-checks its cancellation after disposing a stale engine.** The dispose is a real
await — the old child is killed and waited for — and a transcription starting inside it cancels
the ask; but the continuation carried on to unload the session, disposing the transcriber out from
under the batch that had just borrowed it, mid-decode. The same re-check the load window already
had, at the other window that needed it.

**1633 tests, no weights, no display, no network - 1624 passed and 9 skipped.** Two new — the
mid-write cancel and the transcription-during-dispose interleaving — each run against the code it
pins as that code stood, and each fails there the way the defect says it should: a timeout where
the hang was, an unloaded session where the batch's engine went.

### Fixed 2026-08-29 - the review's sixteen low-severity findings, in one sweep

**The other sixteen findings from the same adversarial review, each needing an unusual input, a
non-shipping composition, or a hand-driven path to fire.** Grouped here by what they had in common
rather than listed, because the pattern is the finding: most of them were places where a value's
shape was checked against one contract and consumed under another.

**Numbers whose shape was checked but whose size was not.** The RTTM reader refused NaN and
Infinity and then let a finite `3e12` saturate the tick conversion into a zero-length turn that
silently scored as nothing; it now stops the read, for the onset, the duration, and their sum. The
native JSON parser clamped NaN and negatives and let huge finite word times saturate to
`TimeSpan.MaxValue`, where the first rebase overflowed; they clamp to zero with the others. The
WAV writer wrapped its 32-bit RIFF sizes past 4 GiB — every sample byte written, a header that
lies, a reader that trusts it — and now refuses with RF64 named; its PCM16 path streams in blocks
like its float sibling instead of building a second whole-file array with an int length.

**Labels read under a weaker contract than they were written under.** The RTTM writer refuses a
speaker with spaces; the reader took field eight of however many fields arrived, truncating "John
Smith" to "John" and merging speakers — an eleventh field, or a word where a confidence belongs,
now stops the read. CRC-protected ADTS (`0xF0`/`0xF8`) fell through the AAC check to the mp3
catch-all and produced a false renamed-file diagnosis; all four ADTS second bytes are AAC now.

**Validation that ran early against one record and late against the derived one.** A segment cap
of four seconds or less passed `TranscriptionOptions.Validate` and then threw from inside the
decode iterator, after the model had loaded, naming `ForcedSplitSearchWindow` — a knob the caller
never set and one the segmenter clamps to fit regardless. The derivation now lives on the options
record, shrinks the window under the cap, and is validated up front, attributed to the cap.

**Contracts documented and not kept.** `AudioSegment.SpeechDetected` could never be false from the
segmenter, whatever its doc said: fixed windows now report false and detected speech true,
including across a cap cut, which the doc now says precisely. `ProcessingTime` is documented as
excluding model load and included it for a cold engine; the runner loads before the stopwatch.
The fake transcription engine ignored `WordTimestamps` where the real engine honours it. The GUI's
unsegmented-audio warning blamed the energy gate under the neural detector — the window's own
default — and recommended decoding what is usually the music bed; it branches on the detector the
way the command line always has. `AllowBackendFallback = false` could complete a load on a backend
the caller refused, when another engine's process-global `Configure` landed in between or an
earlier load had fixed the process's one library; the answer is now checked against the demand and
refused loudly. The sidecar's `StartAsync` said idempotent and was unguarded against concurrent
first calls — two interpreters, one orphaned, one caller parked on a hello nobody would answer —
and now holds a gate across the whole handshake.

**Small lies in diagnostics.** `translate`'s lost-numbers tail printed "and  5 more" — a literal
space and five — whatever the overflow was, because `{lostNumbers.Count: 5}` is a format
specifier, not a subtraction; the canned translator gained a digit-dropping knob so the branch is
pinned at all. The sidecar's `placement`
op discarded the parity check's own report and diagnosed a missing fixture as "loaded without
`profile: true`", prescribing a reload that could never fix it; the report is read first now.

**And the two that kept a broken state broken.** A JSON array as an `op` raised `TypeError` on the
`dict.get` outside the dispatch try, killing the sidecar and its loaded models where the module's
contract says a failure is a message; non-string ops are answered as request errors. A half-deleted
CUDA pack — `torch/__init__.py` intact, `torch/lib` gone — passed the marker check forever: it won
the `PYTHONPATH` resolution and broke both opt-ins, the installer's already-installed answer made
reinstall a no-op, and the Settings button that could have repaired it was hidden by the same
check. Resolution, the installer, and the button all ask a health question now (the marker plus a
non-empty `torch/lib`), so a damaged pack degrades to the bundled CPU torch and one click repairs
it.

**An adversarial verification pass over the sweep found five defects in the fixes themselves,
all corrected before anything was committed.** The RTTM range guard was one-sided — a negative
exponent typo still saturated silently to `TimeSpan.MinValue` — and now refuses both directions.
The runner's load-before-stopwatch had moved the load ahead of the lazily-run options validation,
so a typo'd option would have paid for a model load before its refusal; the runner validates
first now, and a test holds that a refusal costs no load. The sidecar's start fast-path returned
mid-handshake for a concurrent caller, contradicting the comment beside it; it is keyed on the
handshake now. The pack health probe was the first throwing path in a resolution that promises
not to throw — an unlistable `torch/lib` reads as unhealthy instead. And the new overflow test
had been spliced so that it absorbed the token-limit test's closing assertion; both stand whole
again, with the nothing-was-written check back where it belonged.

**1633 tests, no weights, no display, no network - 1624 passed and 9 skipped.** Sixteen new
across eight suites — the refusals, the clamps, the derived-options contract, the flag semantics,
the cold-load exclusion, the overflow arithmetic, the concurrent start, the gutted pack, and the
block-streamed PCM16 — and the CUDA-pack fixtures now stage `torch/lib` the way a real pack ships
it.

### Fixed 2026-08-29 - the uninstall's delete leaves the hook, because overrunning it blocked the uninstall

**Reported from a real machine, and it is the failure the register had guessed wrong.** Remove every
model from the Models tab, uninstall, answer **Yes** to the question about the data directory, and
the application does not uninstall. Try again and nothing happens. Try again and nothing happens.

**The cause is the thirty-second budget, used for a walk that cannot fit in it.**
`UninstallCleanup.Run` deleted `%LOCALAPPDATA%\Uindosill` **synchronously inside the callback**. That
directory is measured in gigabytes: the CUDA pack alone unpacks to 2.8 GB of small files. The walk
overran, Velopack killed the process part-way, and **because the callback never returned, none of
the uninstall's remaining steps ran** — not the shortcuts, not the install directory, not the
registry entry. The product was left installed. Each further attempt ate a little more of the
directory and was killed the same way, which is why it presents as an uninstaller that does nothing
rather than one that is slow.

**The register predicted the wrong consequence.** Written the same morning: *"whether Velopack then
kills the process mid-delete, and what a half-deleted `models\` directory does to the next install,
is untested"*, with the expected cost put at a re-download. A re-download is real and it is the
lesser half. The greater half is that the application cannot be removed, and nothing in that entry
reached it.

**The guards stay here; the walk goes somewhere it cannot be killed for being slow.**
`ResolvedTarget` does what the guards always did — the directory must carry the expected name, must
exist, and must not contain the install root — and that is arithmetic over a path, so it costs
nothing inside the budget. A junctioned root is still unlinked rather than followed, one call and
done. Everything else is handed to a detached `cmd.exe` and the hook returns in milliseconds
whatever the directory holds.

**Velopack does the same thing to its own install directory**, three seconds delayed, for a
neighbouring reason: a process cannot delete the directory it is running from. This one cannot
afford to wait for a directory this size. Borrowing the shape means borrowing something already
proven in this exact position rather than inventing a second mechanism.

**It still fails towards keeping.** If the detached command never starts, or a host kills it with
the parent, the files stay — which is what an uninstall did before any of this existed. No path
through this deletes anything the person did not answer Yes to.

**What the fix is not.** It is not a faster delete, and the uninstall is still not quick: removing a
5.26 GB `win-cuda` install directory took about four minutes on the desktop, measured, and that is
Velopack's own work rather than this product's. The bug was never the duration.

**1633 tests, no weights, no display, no network - 1624 passed and 9 skipped.** Three new: that the
scheduled command names exactly the directory the guards approved and nothing above it, that it
waits before starting, and that a refused target schedules nothing at all. The six that held the
synchronous walk are unchanged and still hold it, because it is still the definition the detached
command has to match.

### Built 2026-08-30 — the CUDA flavour finishes its own set-up: the launch starts the pack, and Stop is remembered

**The complaint was the Settings row.** A win-cuda user has already chosen a 2 GB CUDA download,
and speaker labelling still arrived as a button they had to find on the General tab and press —
"package this into the cuda channel" was the maintainer's ask, and the literal reading is closed
by arithmetic that is already in this document: the channel's Setup.exe was measured at
**1,998,899,901 bytes against GitHub's 2,147,483,648-byte asset limit**, and the pack is
1,961,743,736 bytes compressed on its own. No single release asset can carry both, at any ordering
of what gives.

**Two alternatives were priced and declined, for now.** Moving the win-cuda feed off GitHub
releases to a host without the limit would permit a genuinely fat installer — at the cost of a
~3.9 GB Setup.exe, an out-of-GitHub upload on every release, an updater rewritten off
`GithubSource`, and delta behaviour at that size that nothing here has measured. Shrinking the
pack does not reach: the bytes are CUDA DLLs `torch_cuda.dll` imports at load, and what could be
stripped (headers, import libraries) is small against a 1.8 GB gap. The third road — a diariser
that needs no CUDA torch at all, ONNX on the WebGPU runtime both channels already bundle — is a
research project with a gate to re-clear, not a packaging change. What ships instead is the third
meaning of "part of the channel": **the application finishes its own set-up.**

**A launch of the CUDA flavour starts the install itself.** Three questions, all answered before a
byte moves: is the cuda backend directory on disk — the flavour read off what the build can reach,
on `BackendsPresentOnDisk`'s own reasoning, rather than off Velopack's channel name; would the
Settings button be live (`CanInstallCudaPack`: a card the driver probe reports, nothing installed,
a verified manifest); and has nobody said no. The start is the same `InstallCudaPackCommand` the
button runs — resume, per-part digests, the whole-archive check, all unchanged — fired from
`OnOpened` beside the update check, and like that check it does not sit between the user and the
window they opened.

**The strip above the tabs is where a download nobody clicked for is visible and stoppable.** The
update notice's position and colours on purpose: a fetch that only showed on a Settings tab nobody
opens would be a background download wearing a consent story. It names what is downloading, the
size, and that everything else works meanwhile; it carries the progress bar and a Stop; and it
keeps the outcome — installed, stopped, or the failure — for the rest of the session rather than
vanishing under the reader.

**Stop is remembered; closing the window is not.** Stop writes `cudaPackAutoInstallDeclined` into
`settings.json` — present only once true, the file's one-shape rule — and every later launch stays
quiet. A window closed mid-download has said nothing: the next launch starts again and the
installer resumes from the parts it kept, which is what lets the set-up complete across sessions
without being minded. The refusal is of the *self-start*, not the feature — the Settings row never
reads the flag, still offers the pack, and gained the same Stop button, which also makes the
install cancellable from inside the application for the first time: the "Stopped" message had been
sitting behind a cancellation nothing in the window could cause.

**What this does not do.** It does not ask first — the consent story is that the user chose the
CUDA channel, the machine's driver answered `Present`, and the strip is the standing offer to
stop; a metered connection's defence is that Stop is one click and final. It does not start on the
default channel, on a machine without the driver, or twice in one session; a manifest that becomes
installable mid-session gets the Settings row and the next launch.

**Unproven.** The started path itself — launch, strip, download, installed — has not been driven
end to end on a real machine. ~~The suite never fetches the 1.8 GB (the decision is held apart as
`WouldInstallCudaPackOnLaunch` precisely so it can be asserted without the action), and this
desktop already has the pack, which makes the conditions false here by construction.~~ **Struck
2026-09-01: both halves were false, and neither was checked when it was written** — see *Fixed
2026-09-01* below. The decision is indeed held apart from the action, but `MainWindow.OnOpened`
calls the action, and around sixty tests open a window; and this desktop does not have the pack,
so the conditions were true here rather than false. A `dotnet test` run fetched 122 MB of the pack
into the real `%LOCALAPPDATA%`, which is how the sentence came to be checked at all. The pieces it
composes were driven end to end on 2026-08-29 against a local server and the shipped assets. The
strip has likewise not been seen stacked under a live update notice; both are top-docked borders
and nothing has displayed the pair.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** Six new: the
default flavour never starts by itself; a recorded stop keeps every later launch quiet; on the
CUDA flavour the launch decision equals the Settings button exactly, asserted as the relationship
because both sides vary with the machine running the suite; Stop lands the refusal in the file;
the strip says nothing until a launch has started something; and the flag round-trips with the key
absent until somebody has said no.

### Fixed 2026-09-01 — the suite was downloading the CUDA pack, because a launch action had no installed-copy gate and the one user-data path had no redirect

A status report ran the suite on this desktop and found it red on
`AskTabWindowTests.TheColumnStillFitsAtTheWindowsSmallestSize` — *the rows want 360 in a column
347 tall* — with CI green on the same commit. The test was right and the layout was not the
defect: the same commit passes in a clean worktree and fails in the working copy, and what the
working copy has that the worktree does not is `native/win-x64/` vendored.

**The window was starting the 1.8 GB install, in a unit test.** `MainWindow.OnOpened` calls
`InstallCudaPackOnLaunch()`, and on this machine every term of `WouldInstallCudaPackOnLaunch` was
true: the cuda directory is on disk because it is vendored here, the driver probe answers
`Present` because there is a 5080 in the machine, the manifest has been `Verified` since rc.10,
`settings.json` records no refusal, and the pack is not installed. Around sixty tests open a
window. The evidence is not inference — `%LOCALAPPDATA%\Uindosill\models\python-cuda.parts.part\`
held **126,877,696 bytes** of `uindosill-python-cuda-win-x64.zip.001` (536,870,912 whole, one of
four parts) with a sidecar naming the rc.10 release asset it came from, written during the run.
The failing assertion was the same flag's other effect: `_cudaPackLaunchStarted` draws the strip
above the tabs, ~36 units of it, and the Ask column is what went short.

**CI is green on this because CI is cardless**, and that is luck rather than design: `ci.yml` runs
on `ubuntu-latest` with no Windows natives at all, and `release.yml` runs the suite on
`windows-latest`, which does vendor them but has no NVIDIA driver. A maintainer's machine is
exactly where the conditions come true, which is the machine you least want a test suite
downloading two gigabytes onto.

**The gate: an installed copy, which is the rule the sibling launch action already kept.**
`UpdatesViewModel.CheckOnLaunchAsync` makes no request unless `IsSupported`, so a build running
out of `bin/` had never reached the network on that path; the pack's self-start simply never asked
the question. It asks now, first, before the flavour and the driver. This is a product change and
not only a test fix: the consent story for downloading unasked is that the user chose the win-cuda
installer, and a checkout with the natives vendored — or a maintainer's `dotnet run` — chose
nothing. The Settings row is untouched and stays the manual path it was built to be.

**The isolation hole under it, which the gate alone would have left.** `TestUserData` has
redirected `UINDOSILL_MODELS_DIR` and `UINDOSILL_SETTINGS_PATH` since 2026-08-25, when a temporary
directory's name turned up in the maintainer's own settings file; its own remarks give the reason
for redirecting unconditionally rather than per call site — *a product path that reaches for a
default where no test could pass one*. `MainWindowViewModel.CudaPackRoot` is `UserDataPaths.RootDirectory()`,
which is that shape exactly, and neither variable reached it. So `UserDataPaths` gains
`UINDOSILL_USER_DATA_DIR`, the redirect for the one place this product keeps user files; the
models directory and the settings file are defined against that root and follow it. Production is
unchanged with the variable unset, and `scripts/package-windows.ps1`'s regex over
`const string DirectoryName` is undisturbed.

That collapsed an inequality in `UserDataIsolationTests` that had been quietly stating the
problem: it held `AppSettingsStore.DefaultPath()` apart from a defaulted instance's path, which
proved the variable was read *and* conceded that the default still resolved to somebody's real
`%LOCALAPPDATA%`. Both defaults now land under the run's own directory, and the test says so
instead.

**1633 tests, no weights, no display, no network — 1624 passed and 9 skipped.** One new — a copy
no installer put there never starts by itself — and the previously failing layout test passes
without being touched, which is the check that the strip was the whole of what shrank the column.
`CudaPackLaunchStartTests` now tells every view model it is an installed copy, so each of its
refusals still refuses for its own reason rather than for the new one. **What this does not
establish is anything about the started path on a real installed copy**, which is still the
paragraph above's open item: the fix removes a machine from the set that starts one, and this
desktop was the only machine that had ever run it.

### Measured 2026-09-01 — a small language model tidies a transcript faster than speech on the second machine, and what it changes is the finding

The maintainer's question, put as a question: could `gemma-4-E4B` clean up a transcript in real
time? Measured before it was reasoned about, on the second machine's Vulkan path, through the
vendored `llama-server` at the engine's own flags, with the E4B at the same QAT UD-Q4_K_XL quantisation the
catalogue's 12B ships — 3.91 GiB, every layer on the 880M, its drafting head beside it. The
numbers are in `docs/UNPROVEN.md` (*Gemma 4 E4B as a transcript tidy*); what they mean is here.

**Speed stopped being the question in the first hour.** Rewriting real transcript chunks under a
"remove fillers and false starts, fix punctuation, change nothing else" prompt, the E4B decodes at
24–29 tok/s plain and **44–60 tok/s with its head**, which accepts 87–100% of its drafts because a
rewrite mostly copies its input — 1.8–2.1× on decode where the 12B's head bought 1.27× answering
questions. One request per line, the shape a pass takes, costs **6.0 minutes per hour of audio
sequentially and 3.7 with four requests in flight**, 16× faster than the speech; a 27-word line
comes back in 0.7 s. Whole files are where the ordering shows: the recogniser transcribes at 26–32×
realtime on this adapter's Vulkan path (RTF 0.032–0.038 across the files measured), so a tidy run after it
would be the slowest stage of a job by two to three times, while still running ten to sixteen
times faster than the recording.

**What it changes is the finding.** Ten percent of words go, and read in context nearly every one
should: stutters, false starts, backchannels, *like* — 68 of 493 deletions. But there was one
substitution per ~300 words, and read in full the sixteen split into normalisations the prompt
forbade (*gonna* → *going to*, *you was* → *you were*), fragment repairs that happened to be right
(*objec* → *object*), a hyphenation, guesses that may not be (*hair about* → *worry about*, a
word invented), and two that changed meaning. A three-hour recording would carry a hundred such edits with nothing to
tell a reader which handful were wrong. Greedy decoding also turned out not to be byte-identical
across drafting modes or slot batching (gotcha 41): 193 of 200 lines matched between a sequential
and a four-in-flight pass, and one of the seven that differed had dropped a name.

**Beside the recogniser, both models ran.** The residency decision (`docs/V2-ASK-THE-TRANSCRIPT.md`,
decision 4) was worked out on 9–13 GiB answering models and had never had a both-resident
observation; the E4B beside the 1.34 GiB recogniser loaded and ran on the 880M, the recogniser
26% slower for the company and the E4B at 61% of its solo rate. Timed end to end on the ten-minute
sample, **tandem took 43.1 s against 58.4 s sequential — 26% saved, with the transcript itself
landing 7.2 s later**. The E4B on the CPU cores instead was worse on both counts: it starved the
recogniser's own CPU work and doubled its time. The cleaner never keeps up with the recogniser on
this adapter — 32 words per second absorbed against 63–80 emitted — so lines tidied as they arrive
trail by a backlog that grows for the whole file.

Ten register questions came out of the measurement, and the maintainer took all ten the same
evening. They are the four blocks below. Nothing is built; the record is what this entry adds.

### Decided 2026-09-01 — Tidy up the transcript: delete-only, verified line by line, with one door for the recogniser's own doubts

**What the pass may change in a line is the decision everything else hangs on**, because the
measurement above says the model's rare substitutions are the whole risk. The maintainer chose the
narrow contract with one exception, over the free rewrite and over the contract with no exception:

- **A tidied line is accepted only when its words are a subsequence of the spoken line's**, under
  the same normalisation the WER harness scores by — `TranscriptNormalizer.WordErrorRateTokens`:
  lower-cased, non-alphanumerics stripped, the six filler tokens dropped. `WordAlignment` already
  produces exactly this alignment, and a line is in contract when its ops are matches and
  deletions only. Punctuation and casing changes on kept words pass, because the normaliser does
  not see them. **Any other line keeps the spoken text**, and the pass says how many it refused.
  Of the 200 lines measured, sixteen carried a substitution or an insertion and would have been
  refused; the rest pass.
- **The one exception is the recogniser's own doubt.** A substitution is accepted where the spoken
  word's confidence is below **0.45** — the threshold the low-confidence report already flags
  segments by — so a fragment the recogniser was unsure of (*objec*, *behtor*) may be replaced,
  and a word it was sure of never is. **What this buys is unmeasured**: nothing has yet checked
  which of the sixteen measured substitutions sat on a doubted word, and the door will admit
  guesses as well as repairs; the WER run the tidy owes before shipping is where that is counted.
- **Not chosen:** the free rewrite, which fixed more recogniser errors and invented words at about
  one in 300 with nothing to tell a reader which; and the contract without the door, which was the
  recommendation and is the fallback if the door's run says it admits more guesses than repairs.

**What follows from it.** Every kept word maps to the timed word it came from, so the tidied pane
keeps the spoken-word highlight the English pane cannot have, and the word-timed subtitle format
stays writable from it; a replaced word takes its original's span. A tidied document carries which
words were replaced, so anything that quotes it can say so. And the rule that the window never
writes a timestamp of its own holds untouched: the pass writes words, never times.

**The name is "Tidy up the transcript"** — the checkbox's copy, the pane's label *Tidied*, and a
`.tidy` infix on exported files beside the plain ones, on the pattern the English pane's `.en`
set. Chosen over *Clean up*, which can read as *corrected*, and over *Readable*, which names the
purpose rather than the operation. It is an edited version of the transcript and never replaces
it, in the window and in the file names alike.

### Decided 2026-09-01 — the tidy runs beside the recogniser, and the residency rule gains its one exception

**An opt-in checkbox, beside *Translate to English* and *Label speakers*** in the strip that grows
when ticked, disabled with a hint when its model is not installed, exactly as translation is.
Not a button on the finished transcript, and not both: one control, one meaning.

**It runs in tandem with the recogniser, tidying lines as they arrive.** The maintainer's choice,
with the measured trade in front of it: 43.1 s against 58.4 s on the ten-minute sample on the
second machine's Vulkan path, a 26% saving of the combined time, bought with a transcription 31%
slower in that run and a tidied pane that trails further behind the longer the file runs. The alternative on the table was the
translation pass's shape — after the batch, one language-model load for every file, 3.7 minutes
per hour of audio and no residency decision at all — and it was not taken because the combined
time is what a person waits for. What tandem costs in construction is named so it is not paid by
drift: a stage over the segment stream rather than a pass over the finished document; a queue
that is never empty, with its backlog visible; and the pipeline failure modes the pass shape did
not have — a tidy that dies mid-file must leave the transcript whole, on the terms `OptInPass`
already sets for translation. Neither arm above counted reloading the recogniser for a following
file, which sequential pays and tandem does not, so the saving on a queue of files is larger than
26%; unmeasured.

**Residency: R9 keeps its shape, and gains its one exception, by task.** The recogniser is never
resident while the Ask tab's model is loaded, in both directions, as decided 2026-08-24. **The
tidying task's model is the one model that may sit beside the recogniser**, because it was
measured there: 2,493 MiB on the device and 1,872 MiB host-side beside the 1.34 GiB f16
recogniser on a 16 GB machine, both loaded, both running. Not "any model the fit rule says
fits" — `ModelFit` reads total memory rather than free and knows nothing of what a card holds —
and not a second rule for discrete cards, which nothing has measured. A model earns the seat by
being measured in it; the decision-4 amendment in `docs/V2-ASK-THE-TRANSCRIPT.md` records the
exception where the policy lives.

**The window shows the spoken line at once and swaps in the tidied one when it lands** — beside
the recogniser on the second machine that is a median of 3.4 s per line and a queue that grows for
the whole file, not the solo pass's 1.7 s — marked until then so a reader can tell a raw line from
a tidied one. Not "only tidied lines, lagging", which would have the transcript area trail the
recogniser by a growing backlog and stall at the end of a long file; not both panes filling
live, the simplest reuse of the translation switcher, because a person watching the tidied pane
would watch it stall. The rule the English pane set stands for the tidied one: where a line has
no verified word timings there is no mark, and nothing is guessed.

### Decided 2026-09-01 — the model sees the tidied pane, the E4B enters the catalogue under a task of its own, the passes stay independent, and what the tidy owes before it ships

**The ask's one document is the tidied pane when there is one.** As the 2026-08-24 decision made
it the English pane on a translated recording, so a tidied recording is asked over its tidied
document, whole — windows, ids, quote checks and validation — and a recording that is both
translated and tidied is asked over the English, as today. Under the contract above a tidied
quote is still spoken words in spoken order; the one place a quoted word may not be what was
said is a low-confidence replacement, which the tidied document marks, so the quote check can
say so rather than verify it. Not chosen: the spoken transcript always, the conservative default
this record recommended; and "whichever pane is showing", which would clear the conversation on
every switch because the ids are meaningless against the other document.

**The E4B enters the catalogue under a task of its own** — a `ModelTask` beside `Answering`, the
QAT UD-Q4_K_XL file with its drafting head installed beside it into a directory of its own, on the
answering entries' pattern, `ModelFit`'s warning applying as it does to them. The Ask picker never
offers it: it has not been measured answering a question and the picker is not the place to find
out. Not chosen: an answering entry, fewer changes and a weaker answerer nobody has gauntleted;
tidying with whatever answering model is installed, nothing to download and, on the second
machine, the catalogue's 12B at 9.0 tok/s and 11.4 with its head (the 2026-08-28 record) where
the E4B does 44–60, unmeasured on this task; and the E4B with the answering model as a fallback.

**Tidying and translation are independent passes over the spoken text.** A recording with both
has three panes — spoken, tidied, English — and translation keeps reading the spoken sentences
its published figures describe. Not chosen: translating the tidied text, which would make the
English depend on the tidy and put the translation figures out of date; and excluding the two in
this version.

**What the tidy owes before it ships, both taken as conditions rather than intentions.** First,
**the WER-harness delta on the ten-call corpus, under both reference styles**: the corpus and the
harness exist, and under the harness's normaliser the delta counts exactly the content words the
pass changed — the deletions the contract allows and the replacements the door admitted, each
countable on its own — with the refusal rate beside them. Nothing sets a threshold today; the
number is decided when it exists, and the entry does not ship before it does. Second, **the
desktop re-times the pass and its tandem on CUDA before the tag**: every tidying figure in this
repository is the second machine's, the residency exception and the 26% were measured on shared
memory, and a card with memory of its own may invert both.

**Nothing is built.** What exists is this record, the measurements under it, and the four
scratch drivers that produced them, which are not in the tree.

### Built 2026-09-02 — Tidy up the transcript, in the tree; what it owes is unchanged

The ten decisions of the day before are code. Nothing here is a measurement: the build was
verified by the suite and by the engine's gated test against a real child, and every figure the
tidy has is still the 2026-09-01 record's.

**The contract is one function and it is where every rewrite goes.** `TidyContract.Apply` in
`Parakeet.Core.Tidying` takes the spoken segment and the model's line, normalises both sides word
by word under `TranscriptNormalizer.WordErrorRateTokens`, aligns the tokens with `WordAlignment`,
and accepts the rewrite only when every operation is a match or a deletion — with the one door:
a substitution is taken where the spoken word's confidence is below the threshold (0.45, the
low-confidence report's), and the replacement keeps the spoken word's span and records what it
replaced on the word itself (`TranscriptWord.ReplacedFrom`), which the JSON writes as
`replacedFrom` and the Ask tab's evidence therefore carries. Every kept word maps to the timed
word it came from, so a tidied segment's words reproduce its text, the sentence splitter cuts it
as it cuts the spoken one, the tidied pane keeps the spoken-word mark and the word-timed
subtitle is written for it. Three construction choices the record did not make are made here and
named. The normaliser runs word by word rather than over the line, so its one cross-word rule —
joining number words into digits — cannot apply and a rewrite that joins or splits number words
is refused: both sides are treated alike and the error is in the conservative direction. A
rewrite that comes back empty for a line that held content words is refused although an empty
line is a subsequence of anything: the measurement saw that only on lines that were nothing but
*um* or *uh*, where it is right and is still accepted. And a rewrite word the normaliser cannot
see at all — a dash, a filler the model kept — borrows the span of the word beside it rather than
going untimed, so no time in the result is one the recogniser did not report.

**The stage is the tandem shape the decision named, and both surfaces run the same one.**
`TidyStage` takes segments as the recogniser produces them, keeps four in flight against the
model, lands each outcome as it arrives, and exposes its backlog; `TranscriptTidy.TidyAsync` is
that stage with every segment enqueued at once, so the pass shape and the tandem shape cannot
disagree about what a line may become. `TranscriptionRunner` hands each segment to an observer as
it lands, which is how the command line feeds the stage (`--tidy`, with `--tidy-model-path`,
`--tidy-backend` and `--tidy-server-root` for the lab), and the window feeds it from its own
streaming loop. A failure anywhere in the stage surfaces once, from its completion, and goes
through `OptInPass.Tidy` on the other passes' terms: the transcript is whole, the row says what it
is missing, and no file named `.tidy` is written. The tidied version gets the spoken document's
speakers afterwards, from the one diarisation's turns through `SpeakerAssignment.Apply` on the
tidied words' spans — the stage runs on the raw segments, the speaker pass cuts the finished ones,
and a second diarisation would be a second read of the audio for the same answer.

**The window does what the decision says, plus one thing it did not decide.** The checkbox is
"Tidy up the transcript", first in the strip beside *Translate to English* and *Label speakers*,
disabled with a reason when the entry is not installed or the build has no language-model engine.
While a file runs the transcript area shows each spoken line at once, dimmed, and swaps the tidied
line in when it lands — the outcomes land on worker threads and are applied on the window's own
publishing tick, so the collections are touched from one place — and once the recogniser is done
the row counts the backlog down: "Tidying, 12 lines to go". A refused line stays as spoken and
simply stops being dimmed. The pane switcher grows a *Tidied* pill beside *English*, each drawn
only for a row that has that pane, and Export writes the tidied version beside the plain files
under the `.tidy` infix, every format but the turns-only one. The Ask tab asks over the tidied
document when there is one and no English, and snaps to that pane once when it arrives, as it
does for the English. The one thing decided here: when a tidied run finishes on the row a person
is reading from the spoken pane, the window moves them to the Tidied pane, because `Complete`
rebuilds the spoken pane from the spoken document and without the move the text they had watched
land would snap back to the raw lines the moment the run ended. Once, and only from the spoken
pane.

**The catalogue entry is the file the spike measured.** `gemma-4-e4b-it-qat-ud-q4-k-xl`, under
the new `tidying` task, installs `gemma-4-E4B-it-qat-UD-Q4_K_XL.gguf` and the publisher's Q8_0
drafting head into a directory of its own, URLs pinned to the repository's commit `8c5a9e4f`,
sizes and SHA-256s the ones the spike hashed and the hub's LFS oids confirm; a third Gemma 4
attribution under Apache-2.0 goes with it. `ModelFit` warns about it as it warns about the
answering entries. The Ask tab's picker never lists it: the answering list is what the picker
reads, and this entry is not in it. The engine side is `LlamaServerTranscriptTidier`, the same
child, drop and flags as the answer engine with the spike's 8,192-token context, the head beside
the weights when there is one, and `-np 4` — the slot count the four-in-flight pass was measured
against, named rather than left to the server's default — asking `/v1/chat/completions` one line
per request, greedy, unstreamed, with a cap of two and a half tokens per spoken word plus a margin
that the measurement never reached and that is now refused when hit.

**What was verified.** Build at Release with 0 warnings; the suite green with no weights; the
four gated engine tests — the three answer paths and the new tidy one — run on this laptop's
Vulkan path against the vendored drop and a Gemma 4 E4B, the tidy test holding every line to the
contract's subsequence rule against a real child. And, later the same day with both catalogue
entries installed, the built stage itself beside the real recogniser — the block below.

**The prompt is a variant of the measured one, and says so.** The spike asked for a clean,
readable rewrite with nothing added; the shipped instruction says in as many words that a word is
never replaced, reordered, expanded, contracted or corrected, because the contract behind it
refuses the line if one is. What the stronger wording buys — a lower refusal rate, or nothing —
is unmeasured, and `docs/UNPROVEN.md` carries it. Until the WER-corpus run exists, the contract
rather than the prompt is what keeps a substitution out of the transcript, which is the design.

### Measured 2026-09-02 — the built stage beside the recogniser: the contract holds, the recogniser pays 31%, and the line count sets the pace

The same day, once the two entries had downloaded and verified on the second machine, the shipped
path ran as a user would run it: `uindosill transcribe sample.m4a --backend vulkan --tidy`, the
Release CLI, the catalogue's E4B with its head, four lines in flight, alternated with the
recogniser alone, twice each. The record is `docs/UNPROVEN.md` (*Gemma 4 E4B as a transcript
tidy*, the 2026-09-02 block); what it means is here.

**The contract held on every line, and the model gave it nothing to refuse.** Seventy-seven
segments, 1,806 words: 44 lines changed, two single-*Um* lines emptied, and under the harness's
normaliser 37 tokens of 1,741 deleted — *you know*, stuttered *of*/*the*/*and*, a fragment — with
**no substitution, no insertion, no refusal, and no word through the low-confidence door** on a
file where ten segments carried a doubted word. The tidied text was byte-identical across all four
tandem runs, and the recogniser's own output byte-identical across all eight, alone or with the
E4B beside it: on this pair the company changes the clock and not the words. One file of read-aloud
briefing speech, so a description of this run and not a rate; the spike's one-substitution-per-300
was a podcast under a looser prompt.

**The recogniser is 31% slower for the company, as measured before**, 19.9 s to 26.1 s of
processing on the ten-minute sample. **The whole command is 65–67 s against 20–22 s alone**, so
the tidied version lands about 45 s after the plain transcript would have — and that is not the
spike's 43.1 s tandem. The difference is the unit: the spike handed 38 lines of the same words to
a warm server at once, while the built stage was fed the recogniser's 77 segments as they came,
and a request's cost is its own prefill plus a decode that mostly copies its input, so the pace
is set by requests rather than by words. **Whether tandem beats the sequential shape on this
segmentation is unmeasured** — no sequential run was made today, and the spike's 58.4 s
sequential on 38 lines sits below the built tandem on 77. The register gains a question rather
than a change: the unit the stage sends is the lever, and a longer one — several segments
joined, or the sentence-runs the window already cuts — is the obvious candidate, unmeasured.
Silero was not installed on the machine, so the 77 are the energy gate's cut; the neural detector
would send a different count.

**What the tidy owes is now two things and a question**: the WER-corpus delta under both
reference styles, the desktop's CUDA re-timing, and the unit question above.

### Measured 2026-09-02, evening — the corpus delta is +3.43 against the verbatim transcripts and −2.94 against the edited ones, and the first condition is met

The first of the two conditions set on 2026-09-01 was measured the evening the stage was built,
on the second machine's Vulkan path: `scripts/measure-wer.ps1 -Tidy`, one recogniser pass over
the ten-call corpus with the E4B beside it, both reference styles scored off the same transcripts
(`docs/UNPROVEN.md`, *The delta on the ten-call WER corpus*). Spoken 10.21% / 13.41% against
verbatim / non-verbatim — the desktop's 2026-08-16 CUDA baseline to two decimals, per call within
0.04 points — and tidied 13.64% / 10.47%: **+3.43 against the transcript that writes stutters
down, −2.94 against the one that edits them out**, and the composition is the same deletions
counted from opposite sides: deletions 1,924 → 6,688 under verbatim, insertions 7,328 → 2,971
under non-verbatim, substitutions flat under both. The contract, silent on the ten-minute sample,
did work on eleven hours: 139 of 7,786 lines refused, 171 emptied, 39 words through the door.

**What the measurement settles, and what it hands back as a decision.** The condition asked for
the delta under both styles, composition included, and both are now in the record; the
composition says the contract held, because a delete-only pass can only move deletions one way
and insertions the other, and that is all that moved. What it cannot settle is which reference
the criterion reads: against the edited transcript the tidy is a 2.94-point improvement, against
the verbatim one a 3.43-point regression, and the same words are the reason for both. That
reading is a decision, not a measurement, and it is open. The unit question of the morning is
unchanged by this run, which sent the energy gate's 7,786 segments one request each.

**What the tidy owes is now one thing and two decisions**: the desktop's CUDA delta and
re-timing; which reference style the shipping criterion reads; and the unit the stage sends.

### Decided 2026-09-02, late evening — the edited transcript decides, the unit is measured before it is chosen, and the record-writing harnesses go invariant

Three decisions on the evening's measurement, taken the same night.

**The shipping criterion reads the non-verbatim delta; the verbatim delta is reported beside it
and is never a pass/fail.** The tidied transcript is an edited transcript, so it is judged against
the edited human transcript, where it is 2.94 points better; the verbatim transcript writes down
the stutters the tidy exists to remove, and its +3.43 is the cost the tidy is defined to pay,
recorded as such. The first condition is therefore met on the second machine's Vulkan path: the
delta is negative against the reference the criterion reads, and the composition says the
contract held. The desktop's CUDA delta is read the same way when it comes.

**The unit the stage sends is not chosen until three shapes have been measured on the same
file**: one request per segment (the shipped shape), segments joined into runs of about fifteen
seconds, and the sentence-runs the window already cuts — each with a sequential arm beside its
tandem, on the second machine's Vulkan path, about an hour of measurement. The shipped stage
keeps sending one segment per request until that session has run; nothing changes on a guess
about the pace. Scoped the same night and confirmed in three particulars: a sentence-run is cut
at sentence-final words by the splitter's own rule and **joined across segment boundaries until
a sentence ends**, capped at 30 s of speech; the six arms run on call 4482383, where the
recogniser's own cut projects to 680 requests as segments, 180 as 15-second runs and 522 as
sentence-runs; and **the rule that picks the winner is set in advance** — a shape replaces the
segment when its tandem lag (the plain transcript landing to the last tidied line) is shorter by
more than that shape's own pass-versus-tandem spread in the tidied text, its delta against the
non-verbatim reference is no worse by more than that same spread, and its refused segments are at
most twice the segment shape's; if two qualify, the shorter lag. The plumbing is built as
product code with tests behind experimental options, the default unit staying the segment.

**The invariant-culture line goes into the scripts that write run records** —
`measure-transcribe.ps1`, `measure-second-machine.ps1`, `measure-translation-agreement.ps1`,
`compare-transcripts.ps1` and `word-distance.ps1`, in the form `measure-der.ps1` has carried since
it was written — and not into the vendoring and packaging scripts, whose sizes are read by the
person at the keyboard, where the machine's own locale is the right one (`docs/GOTCHAS.md`, 42).

**Also asked for, and measured the same night: the recogniser-alone control on the corpus**, so
that the cost of the company over eleven hours is a measurement rather than the ten-minute
sample's +31%. It is 11.5%: RTF 0.037 alone against 0.041 beside the E4B, per call 6.7% to 15.4%,
and the recogniser's output byte-identical alone and in company on all ten calls. The whole
command is 2.57× longer with the tidy, 5.7 minutes per hour of audio against 2.2, which is the
tidy's pace at one request per segment — the figure the three-shape measurement above has to
beat (`docs/UNPROVEN.md`, *The delta on the ten-call WER corpus*).

### Built 2026-09-02, late — the request-unit plumbing, behind the measurement's own options

What the three-shape measurement needs is in the tree, and nothing shipped changed: the
default unit is the segment, the App keeps the shipped shape, and the two other units exist
only behind options the window never sets.

- **`TidyUnitKind`** — segment, joined run, sentence-run — and **`TidyUnitShaper`**, which cuts
  the segment stream into units in arrival order: whole segments joined to fifteen seconds of
  speech; or pieces cut at sentence-final words by the splitter's own rule and joined across
  segment boundaries until a sentence ends, capped at thirty seconds, with the decision about a
  segment's last word waiting for the next segment's first. A segment without verified word
  timings closes whatever is open and travels alone under every kind.
- **The contract over a unit.** `TidyContract.Apply(TidyUnit, …)` judges the pieces' words as
  one line — the same alignment, the same door — and cuts the result back to the pieces by the
  mapping the contract already keeps from every kept word to the spoken word it came from. A
  rewrite word whose spoken words lie on two pieces refuses the unit; a refused unit refuses
  every piece; and a unit of one whole segment goes through the one path it always took, so
  the shipped shape is unchanged by construction.
- **The stage takes a shaper.** `TidyStage` queues units, lands one outcome per segment once
  the last unit carrying a piece of it has, assembles a segment from its pieces or keeps it
  whole when any was refused, and records every request — what it carried, when it was queued,
  sent and answered, and how the contract found it — as the trace the pace measurement reads.
- **Three options on `transcribe`**: `--tidy-unit segment|run|sentence`, `--tidy-shape
  tandem|pass`, and `--tidy-trace <file>`, which also records the moments the plain transcript
  and the tidied version were complete on the stage's clock — the lag the rule turns on.
- **`scripts/measure-tidy-units.ps1`**, under `lab.ps1 tidy-units`: the seven arms on one call
  through the real CLI — each unit in the pass shape and in tandem, alternating, then the
  segment's tandem once more, because the rule compares lags and a run-to-run floor for a lag
  is something one run cannot give — scored against both references off the tidied transcripts,
  the pass-versus-tandem spread of each unit measured in the tidied text, the spoken transcripts
  checked byte-identical across arms, and the rule applied and printed. A `-Fake` switch runs
  the whole harness on the canned engine and tidier; on the real call it produced exactly the
  180 joined runs the plan projected, and the dry run's summary went nowhere.

Thirteen unit tests hold the shaper, the cut-back and the stage to the cases above, and two
end-to-end tests drive the options through the entry point; the suite is **1633 tests, 1624
passed and 9 skipped**, built at Release with 0 warnings. The measuring hour has not run.

### Measured 2026-09-03 — the request unit: the longer units win the lag and the quality, and fail the refusal clause; the rule needs a decision

The measuring hour ran the same night, 38 minutes for seven arms on call 4482383
(`docs/UNPROVEN.md`, *The request unit*). In tandem the joined run lands the tidied copy
**83 s** after the plain transcript against the segment's **194 s** (192.8 s on the repeat, a
floor of 1.5 s), the sentence-run 156 s; and both longer units tidy better under both
references — against the edited transcript −2.82 and −3.07 points against the segment's −2.11,
with a pass-versus-tandem spread of at most 0.03 points. Every refusal in every arm was a
substitution the recogniser was sure of; but a refused unit refuses every line it carries, so
the joined run's 7 refused requests cost 27 lines and the sentence-run's 11 cost 25, against the
segment's 10 (and 13 on the repeat).

**By the rule as written, neither longer unit qualifies and the segment stays**, on the third
clause alone. The first two clauses — lag and quality — are what the rule exists for, and both
longer units clear them by wide margins; the third counted refused segments as a guard on
quality, and the count it guards with scales with the unit by construction while the quality it
was guarding improved. That is not a result the rule anticipated, and it is not resolved here:
**how the refusal clause should read is a decision**, taken on the numbers rather than in
advance of them, and until it is taken the shipped unit is the segment. Two ways forward are on
the table, neither measured: read the clause as the quality clause already does, or add the
retry of a refused run one line at a time — about 27 requests more on this call, 4% of the
segment's 680 — which would leave only the lines a single request would have refused anyway,
and measure again.

**What the tidy owes is now**: the desktop's CUDA delta and re-timing; the decision above on the
rule; and, if the retry is chosen, its measurement.

### Measured 2026-09-03, desktop — the second condition is met on CUDA: the delta is the laptop's to a twentieth of a point, the lag a fifth of it or less, the company costs the recogniser 63%, and the same rule picks a different unit

The desktop's CUDA delta and re-timing, owed since 2026-09-01, ran the same morning as the
request-unit measurement above: the same seven arms on the same call, then the same corpus pass
with the E4B beside the recogniser and the recogniser-alone control after it, all on CUDA
(`docs/UNPROVEN.md`, *The request unit on the desktop* and *The delta on the ten-call WER corpus
on the desktop*). The machine had no models installed — `%LOCALAPPDATA%\Uindosill` was gone — so
the recogniser and the E4B were installed from the catalogue first and digest-checked; the E4B
ran under CUDA at the server's own flash-attention default without the abort its head's README
warns of; `nvidia-smi`, sampled every 5 s throughout, peaked at 57 °C and 145 W. Twenty-three
minutes of card for the three runs that took the laptop 38, 63 and 24 minutes.

**The delta on the card is the laptop's: +3.47 against the verbatim transcripts and −2.89 against
the edited ones**, where the Vulkan path measured +3.43 and −2.94, with the same composition —
deletions one way, insertions the other, substitutions flat — and the same nine-of-ten pattern
per call with the same two exceptions; 7,788 lines, 138 refused, 172 emptied, 40 words through
the door against 7,786 / 139 / 171 / 39. The spoken row is the 2026-08-16 CUDA baseline byte for
byte — the same text on all ten calls, eighteen days and a driver update later — and byte-identical
alone and in company. **The second condition is met on the reading of 2026-09-02: the delta is
negative against the reference the criterion reads, on both machines.**

**The re-timing inverts neither the residency exception nor the saving; it shrinks the saving,
and the recogniser's price for the company goes from a tenth to two thirds.** The tandem lag is a
fifth of the laptop's, a seventh for the joined run, in the same order — segment 38.4 s against 194.3, joined run 11.9 s against
83.3, sentence-run 31.2 s against 155.5 — and the tandem still lands the tidied copy sooner than
a pass would, by 7–12% of the combined time; the 26% of 2026-09-01 was another file and another
shape. But **the recogniser pays 63% for the company over the corpus — RTF 0.0046 alone against
0.0075 beside the E4B, both CUDA, 49–78% per call — and 65–71% on the single call, where the
laptop paid 11.5% and 16–20%**: the recogniser here is eight times faster and the tidy's share of
the card does not shrink with it. In seconds it is 115 over eleven hours. The whole run is
4.06× longer with the tidy on the basis the laptop's 2.57× was taken, 1.12 minutes per hour of
audio against 0.28, where the laptop's was 5.7 against 2.2.

**The rule as written picks the sentence-run on this card and the segment on the laptop, on the
same call and the same rule.** Both longer units clear the lag and quality clauses here as there.
The third clause's count is 24 refused segments for the sentence-run against the segment's 12 —
exactly twice, within the clause — where the laptop's segment refused 10 in the arm the rule reads
and 13 on its repeat, and the same 25 fell outside. The verdict rests on a count that moves by
three between identical runs, which is the clause the entry above already holds open; this run
takes no decision, and the shipped unit stays the segment. One thing this run did not expect: the
segment's pass-versus-tandem spread on CUDA is 45 words, 0.42 points, where the laptop's was 4–7
and the two longer units' here are 5 and 12 — the two tandem arms agree to 0.04 points at most and the
pass arm is the outlier, greedy decoding under slot batching the suspect (`docs/GOTCHAS.md`, 41),
unresolved. Nothing shipped changed.

**What the tidy owes is now**: the decision on the refusal clause, with both machines' verdicts in
front of it; and, if the retry is chosen, its measurement. Both conditions of 2026-09-01 are met.

### Decided 2026-09-03 — the refusal clause counts requests, the joined run is the unit, the tidy ships opt-in, and the tandem stays on every card

Four decisions on the morning's measurement, taken the same day.

**The rule's third clause reads refused requests, not the lines they carry.** As written on
2026-09-02 it counted refused segments at most twice the segment unit's; a refused run refuses
every line it carries, so the count scaled with the unit by construction, and the same rule on
the same call picked the segment on the laptop and the sentence-run on the desktop, on a count
that moved by three between identical runs. The request is what the contract refuses, and under
that reading the joined run's 7 and the sentence-run's 11 sit within twice the segment's 10 to
12 on both machines; the lines a refused request costs are still counted and reported beside it.
Not chosen: dropping the clause and letting the quality clause carry it, which lands the same
verdict on this data; and the retry of a refused run one segment at a time, designed on
2026-09-03 and never measured, which would have needed a code change and the seven arms again.

**The joined run is the unit the stage sends.** Under the clause as decided both longer units
qualify on both machines, and the shorter lag wins: 11.9 s against the sentence-run's 31.2 s on
the desktop's CUDA path, 83.3 s against 155.5 s on the laptop's Vulkan path. It also tidies
better than the segment under both references on both machines (−2.85 / −2.88 there and −2.82
here against the edited transcript, against the segment's −2.11), the second-best delta of the
three: the sentence-run's −3.07 is 0.2 points better and takes 1.9 to 2.6 times as long to land.
The default in `TidyOptions` and on `--tidy-unit` moves to the run; the segment and the
sentence-run stay behind the option, for measuring against it. **What that leaves unproven:** the
corpus delta and the refusal count under the joined run, which was measured on one call only —
both machines' corpus deltas sent one segment per request — and is marked so in
`docs/UNPROVEN.md`.

**The tidy ships, opt-in.** Both conditions of 2026-09-01 are met — the delta under both
reference styles and the desktop's CUDA re-timing — on the reading of 2026-09-02, so the entry
ships in the next release as the opt-in it was built as, with the joined run as its unit.

**The tandem stays the default on every card.** The desktop was measured to see whether a card
with memory of its own inverted the residency exception; it did not. The tandem lands the tidied
copy 7–12% sooner than a pass would there, at the cost of the plain transcript arriving 65–71%
later on a call (ten seconds on an hour), and that trade was judged worth keeping over the pass
shape on CUDA and over a setting for it. Not chosen: the pass shape wherever the recogniser's
backend is CUDA, a backend-dependent default to build and explain; a setting, more surface to
build and test; and measuring more first.

**What the tidy owes is now**: the corpus delta and refusal count under the joined run, marked
unproven; and the tag.

### Found and fixed 2026-09-03 — the tidy's deletions were unbounded: a clause could be lifted out of a line and a line out of a joined run, and the contract gains a ceiling

Found by driving the shipped command over one file, hours after the decision above made the joined
run the unit — `uindosill transcribe csb384-8438.m4a --tidy --tidy-trace` on this desktop's CUDA
path, build `3074ba8`. Not a harness run, not on the corpus, and no `runs/` record: the details and
every figure are in `docs/UNPROVEN.md`, *The deletions were unbounded*.

**What was wrong.** The delete-only contract bounds the form of an edit and never its size. It
refuses an insertion, and a substitution of a word the recogniser was sure of; a deletion is the
one thing it exists to permit, and nothing counted how much of a line went. So the model removing
`we, anyone who passes away, we own everything` — reading the repeated *we* as a stutter and taking
the clause between them — passed every check, as did a whole sentence removed elsewhere on the same
call. `DeletedWords` had counted exactly the right thing since the contract was built, content
words with fillers excluded, and was only ever summed and reported.

**And the per-line empty guard had stopped being per line.** The contract documents it as one: an
empty rewrite is refused for a line that held content words. It lives in `Judge`, which under a
joined run judges the composite, so a run that came back with words satisfied it while a line
inside the run went entirely — accepted, empty, and then dropped from the window's line list by
`Relines`' `!IsEmpty`, which is to say gone from the pane with no refusal recorded. Three lines on
this call. Under the segment unit — what shipped until that morning — the same call kept all three,
and the only lines it emptied were the ten that were nothing but *Um* or *Mm-hmm.* The decision
above changed the default; this was the part of it nothing had exercised.

**The fix.** `TidyOptions.MaxDeletedFraction` (0.5) and `MaxConsecutiveDeletedWords` (4), read
together, applied per piece in both `TidyContract.Apply` overloads, a piece past either refusing
its unit on the rule every other refusal here follows. Two ceilings because neither catches the
other: the proportions interleave on this call — a legitimate stutter cleanup at 43% of its line
against the clause deletion at 33% — while contiguity separates them cleanly, every clause removed
running to five words or more and every legitimate cleanup to four or fewer. Fillers are
transparent to the run, and a line the normaliser sees no content in is exempt, so a line that was
only *Um* still tidies to nothing. Not chosen: a proportional ceiling alone, which at any threshold
tight enough to catch the clause also refuses a third of the call's legitimate tidying; and
reverting the unit, which would pay 5.17 s of lag against 0.76 s to fix a defect the ceiling fixes
at 0.41 s.

**What it costs.** 35 of 113 lines keep their spoken text where 11 did, because a refused unit
refuses every line it carries and a run holds up to seven. That is the rule of *Decided
2026-09-02* meeting a unit that did not exist when it was written, and it is the open question the
ceiling leaves.

Seven tests, three on the unit path and four on the segment path; the suite moves 1626 to 1633.
Nothing was measured: no reference transcript was scored, so no WER or chrF figure moves, and the
corpus deltas recorded above are untouched and now describe a tidy that no longer ships.

**What the tidy owes is now**: the corpus delta and refusal count under the joined run **with the
ceiling**, on either machine, since the figures the ship criterion read describe the tidy as it was
before this; whether 0.5 and 4 hold anywhere but the call they were fitted on; what the ceiling
costs in tidying not done; whether one bad piece should refuse a seven-line run; and the tag.
