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

**Status:** met. 777 tests, no weights, no display, no network — **775 passed and 2 skipped**, and
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

**Status:** usable, tested end to end against the canned engine (91 of the project's 144 CLI
tests drive the real entry point; the other 44 never construct it — 18 on the backend default and
the resolver that turns `--vk-disable-bf16` and its opposite `--vk-bf16` into an engine option,
17 parser unit tests, and 9 checking those two flags against the real command specs through
`CommandLineParser` — because no invocation can reach what they check). `bench` has not yet been pointed at real weights, so the RTF 0.10 figure above came
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
`uindosill notice` and the Licences tab render it; the licence was read off the restored package's
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
an Ask tab exists. It becomes an outlined matcha badge, which still separates it from the solid
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

**A v1 Transcribe-tab opt-in that produces an English version of the transcript beside it, decided
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
reason this project has had to report *which* interpreter a run used. **Nothing prints it yet.**
Until 2026-08-21 `PythonRuntime.Resolution.Overridden` was computed and unit-tested with no
production caller at all; it now has a `Source` and a phrase to describe it, and still no caller —
which is why the 2026-08-21 agreement run's interpreter is written into its prose by hand.

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
| 5 — ship | Signed, updating installer | **Installer done, signing dropped from v1.** Two Velopack channels, a `v*` tag workflow, and an in-app update check; installed, updated and uninstalled on the desktop 2026-08-19 with the weights hashed and unchanged throughout. Unsigned by decision, and no release has been published |
| translation | **Three criteria, all must hold — two ratified 2026-08-19 before any score existed, the third and the margins on 2026-08-20 with the first scores.** **(1)** chrF++ into English clears the **per-language source-copy floor** — what a hypothesis scores by echoing its untranslated source — by a per-language margin, because one number across 25 languages would be a different bar in each. **(2)** A **human adequacy check on the Spanish → English driving case**, rated for adequacy and flagged for output that is not English. Nothing anchors this from outside: no published chrF++ or BLEU for any candidate on FLEURS X→en at a stated signature was found, so unlike the DER gate it is anchored from inside its own measurement, and the corpus is FLEURS pinned by digest with both metric signatures printed on every run. Opt-in aboard v1.0. | **Seam built 2026-08-19, artefact exported 2026-08-20, criterion one scored in all 24 languages 2026-08-20.** The route is decided — `opus-mt-tc-bible-big-mul-deu_eng_nld`, apache-2.0, exported in-house to ONNX, CPU-only in v1 — and a spike on 2026-08-19 settled four things ahead of the code: the `>>eng<<` target token is mandatory and its absence returns fluent German, greedy decoding drops content beam-6 keeps over 44 real segments, English input passes through byte-identical, and an int8 export was thought to weigh 227 MiB or 404 MiB. The parts that need no model landed the same day — the `ITranscriptTranslator` contract with the target token and the dropped word timings as enforced invariants, the canned translator, `ModelTask.Translation` and its manifest word, and `--translate` on the CLI wired to the fake. **The ONNX export exists as of 2026-08-20** and replaces the last of those four: `scripts/export-translation-onnx.py` produces **nine files** in the merged layout — two graphs with past-key-values exposed, two configs, and a five-file tokenizer — at **1369.1 MiB fp32, 345.9 MiB int8, or 694.3 MiB int8 with the embedding tables left in fp32**, and **fp32-merged is what ships as of 2026-08-20**, int8 having been dropped that day on speed, on a silent GPU collapse and on the export smoke, without a quality score ever being taken of it. The recorded `optimum` failure was CPython 3.14 giving `functools.partial` the descriptor protocol, not a library skew, and a twelve-line shim defeats it. fp32 reproduces the PyTorch reference string-identically on all 44 recorded segments; int8 changes most of them and collapses into a repetition loop on one. **The multi-file catalogue schema landed the same day** — an entry may be a set of files in a directory of its own, installed all-or-nothing through a staging directory, with per-file pins and per-file resume; no entry uses it yet because no asset has been uploaded. **The harness landed 2026-08-20 and computed criterion one's bar in every language** — the per-language source-copy floors run 2.00 (Ukrainian) to 23.10 (French) on FLEURS test, an 11.5x spread that is why the gate refuses a single number. **Criterion two is unperformed and the gate is therefore not passed.** **Criterion one is scored and its bar is set**: `margin_L = 45 − floor_L` plus zero collapses, ratified 2026-08-20, **23 of 24 languages pass and Slovak fails by 0.74**. `fp32-merged` over FLEURS `test` in full, beam-6, on the desktop's CPU — 8,149 sentences in 1.40 h, chrF++ from **44.26** (Slovak, the outlier the record predicted from its absence in the sibling card's source list) to **68.52** (Portuguese), margins over floor +28.15 to +60.53, median +42.76, and **zero collapses** against 31 trailing-punctuation runs. **The decode loop landed the same day** — a SentencePiece tokenizer and a port of transformers 4.57.6's beam search in C#, driving the pinned graphs at beam 6 on the CPU — **retired to `attic/` on 2026-08-21**; the decode is `transformers.generate` itself again, in a bundled Python, at the same settings, defaulting to WebGPU, held to the 8,149 hypotheses the gate run itself recorded (§ *Built 2026-08-20 — the decode loop*). `models.json` gained its first multi-file entry, nine files pinned by size and digest and marked unverified because no release asset has been uploaded. **The weights were published to Hugging Face on 2026-08-20 and the entry is verified against the nine LFS oids the repository publishes**, with the Apache-2.0 §4(c) and §4(d) checks done before the upload rather than after — no NOTICE file upstream, no copyright line anywhere, and four attribution notices retained. The first real multi-file install ran the same day: staged, hashed, 9 of 9 verified, and the graphs then loaded out of that assembled directory. **The cascade penalty is measured** — Spanish −2.95 and German −4.34 chrF++ against ASR word error rates of 6.12% and 9.93% — recorded and deliberately not gated. **The window's half landed 2026-08-20**: an "English version" opt-in drawn as the twin of the speaker one — its own tinted strip, off by default, disabled with a reason while the entry is not installed — and a Transcript/English pill switcher over the transcript pane, drawn only for a row that has both. The window keeps the transcript as the engine wrote it beside the English rather than replacing it, which is what the switcher switches between; outputs take the same `.en` infix the command line gives them, and `vtt-words` is refused under the opt-in there too. **No spoken-language picker was added, and that is the second time the answer has come out that way** — the translator is many-to-one and never told its source, and the ASR's hint is inert on this catalogue (`docs/UNPROVEN.md` § *The language hint*), so a control for it would change nothing. Outstanding: **the human adequacy check**, which is what keeps the gate unpassed; a real-time factor for a translation pass over real audio; and an interrupted install, which nothing has exercised. `docs/UNPROVEN.md` § *Translating into English* has what is measured and what is not |
| speakers | **AMI test DER within 5 points of the best published figure on the same audio at the same convention** — pyannote 3.1's 18.8 on Mix-Headset at collar 0 with overlap scored, so ≤ 23.8; collar 0 because half-width and total-width definitions agree there, which is what makes the comparison convention-proof — with this project's own headline (collar 0.25 pyannote semantics, 0.125 s either side, overlap included) reported beside it. **NOTSOFAR-1 is the crosstalk check** (39% of union speech overlapped, against AMI's 14.58%), and it is a meeting corpus too, so both of the gate's corpora are now in the target domain. **VoxConverse left the gate on 2026-08-18 when the domain narrowed to meetings** — see the narrowing below; it was the web-video and beyond-four-speakers check, and web video is no longer a target. **Podcasts are ungated**, for want of any labelled material. The 5-point margin was **ratified 2026-08-18**, before any candidate had been scored at this convention. **Second criterion, added 2026-08-18: mean |speakers found − speakers in reference| ≤ 1.0 over the AMI test set — both criteria must hold.** Opt-in aboard v1.0. | Instrument built and validated, AMI dev and test set up and verified, seam in; sherpa-onnx 1.13.5 measured 2026-08-18 and **fails on AMI**, held out — 25.05% with NeMo TitaNet-L and 25.77% with 3D-Speaker ERes2Net, hyperparameters chosen on the 18 dev meetings and applied unchanged to the 16 test meetings; its threshold, min_duration, six embedders and int8 segmentation are all swept, so the toolkit's knob space is exhausted. **Streaming Sortformer 4spk v2.1, ONNX, measured 2026-08-18 on the desktop, CPU only: the gate PASSES on both criteria** — AMI test **16.33%** at collar 0 with overlap against ≤ 23.8, and speaker error **0.06** against ≤ 1.0, tuned on the 18 dev meetings and applied unchanged to the 16 test meetings, test scored once. NOTSOFAR-1 and VoxConverse still untouched, and **VoxConverse can no longer serve as this candidate's beyond-four check** — see below. **The C# port landed 2026-08-19 and reproduces it: AMI test 16.3368% against the Python reference's 16.3324%, 0.0044 points apart, same speaker error 0.06, both gate criteria hold.** Shipped as the opt-in in the CLI and the app, then **retired to `attic/` on 2026-08-21** when the engine moved into a bundled Python: what ships now is the Python the reference was taken from, so the figure the product carries is **16.3324%** on the CPU and 16.3319% on WebGPU, and the 0.0044 divergence between the CLI and the window closed with it. **Measured 2026-08-20 on whole podcasts and it does not transfer**: all four episodes returned four labels whether there were 2, 3, 5 or 7 speakers — the cap explains the last two and over-segmentation explains the first two — and a duration ladder over one episode puts the count right to 50 minutes and wrong from an hour, against AMI meetings averaging about half an hour. AMI dev re-scored the same day is 8.62% at collar 0.25 with 0.94% confusion and 4-of-4 speaker agreement on all eighteen, so this is a long-recording limit rather than a bad model. Nothing was re-tuned; the product now warns before the run, past an hour and on a count above the cap. No DER exists for any podcast and the cap is still unpriced |

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
   `uindosill notice` and the Licences tab render it, and two tests hold it up. The reading, and
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
     opt-in shapes it: `transcribe --speakers` and a checkbox on the Transcribe tab, both off by
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

**A research workflow on offloading to the NPU — asked for 2026-08-16, deferred until it is
relevant.** The second machine carries an XDNA 2 NPU (`NPU Compute Accelerator Device`, PCI
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

**Pinning the model digests used to head this list** and is done: all five entries carry the exact
byte size and the SHA-256 read from the repository's LFS listing, `"verified": true`, and no entry
needs `--allow-unverified`. `docs/MODELS.md` has the table. That settles *provenance* and settles
nothing about quantisation quality, which is what item 2 is for.
