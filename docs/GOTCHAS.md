# Gotchas

Each of these is a *silent* failure: wrong output or a dead app, not an exception. Each entry says
where it is handled, so the handling is not deleted by somebody who does not know why it is there.

## 1. The AVX2 static-initialiser crash

A prebuilt native compiled with an `/arch:AVX2` baseline can execute BMI2/AVX instructions in a
**static initialiser** and crash at **process startup** on pre-Haswell CPUs (Sandy/Ivy Bridge, older
AMD). Uncatchable, no stack trace, presents as "the app won't launch". `cjpais/Handy` hit this via a
prebuilt ONNX Runtime and dropped a whole feature over it.

**Here:** `uindosill doctor` probes each backend **in a child process**, so a crash becomes an exit
code and a printed diagnosis instead of the tool vanishing. Nothing in-process can catch it — that
is the point of the child. Check the ISA baseline of every native you vendor; for ggml builds,
`GGML_NATIVE=OFF` targets a portable baseline and `GGML_CPU_ALL_VARIANTS` gives runtime dispatch.
See `src/Parakeet.Cli/DoctorCommand.cs`.

## 2. Warm up before you measure

The first decode pays arena allocation and graph construction. Without a warm-up every benchmark's
first number is inflated, which makes your own figures exactly as unreliable as the vendor marketing
you set out to replace.

**Here:** `SegmentingTranscriptionEngine.WarmUpAsync` runs one throwaway decode over ~0.5 s of
deterministic near-silent dither at load. Cold load is reported as its own metric, never folded in.
`uindosill bench --no-warmup` exists to demonstrate the difference, and prints a warning when used.

## 3. Beam search is lossy on Parakeet TDT — use greedy

Measured across 80 real production captures in `ChrisMcKee1/scribe`: 19 transcripts changed and
*every change was a loss* — a closing sentence vanished, "MAU" dropped from a list of three, and a
near-silent capture that greedy correctly returned empty came back as the invented word "Yeah."
Independently matches sherpa-onnx issue #3267.

**Here:** greedy is the only decode path the product can reach.
`ParakeetCppEngine.RunBeamSearchDiagnosticAsync` exists so the result can be reproduced and is not
wired to any option. `TranscriptionOptions.BeamSearch` is null by default and a test asserts it.

## 4. Synthetic test audio will not catch decoder regressions

From the same source: *"clean text-to-speech decodes identically either way; only real, disfluent
microphone audio diverges."* A WER regression corpus of TTS output passes while the decoder is
quietly broken.

**Here:** treated as a requirement. `uindosill bench` refuses to run without a file and says why
synthetic audio is not enough. The synthetic fixtures in `tests/` exercise *segmentation and
formatting*, which is what they are good for; they make no claim about decode quality and cannot.

## 5. Long audio degrades

Parakeet degrades past roughly 24 minutes single-pass; chunk-boundary text gluing is reported even at
2.5-minute chunks; Handy has an open issue about *silently dropping* ~5-minute recordings.

**Here:** everything is VAD-segmented with a hard 30-second cap, and this is treated as a correctness
requirement rather than a tuning default. `VoiceActivityOptions.Validate` and
`TranscriptionOptions.Validate` both **refuse** a cap beyond five minutes rather than warning about
it. The segmenter streams rather than buffering: three hours of 16 kHz mono float32 is 690 MB, and an
app that needs the whole recording resident dies on the recordings people actually have. Segments are
cut at the quietest frame within a four-second window before the cap, so a forced cut lands between
words rather than through one.

Two invariants have tests: audio classified as speech is never dropped
(`EveryDetectedSpeechFrameEndsUpInsideSomeSegment`), and forced cuts leave no hole
(`ForcedCutsAreContiguousSoNoAudioIsLost`).

## 6. No ASR library here reads audio files

sherpa-onnx exports 102 types and none of them decode audio. The decoding layer is yours to own.

**Here:** a pure-managed RIFF/WAVE reader (RIFF, RF64, BW64; 8/16/24/32-bit PCM; 32/64-bit float;
`WAVE_FORMAT_EXTENSIBLE`) that runs everywhere and is tested in CI, plus Media Foundation via NAudio
guarded at runtime by `OperatingSystem.IsWindows()` for mp3/m4a/aac/mp4. Content is sniffed by
**magic bytes as well
as extension**, because people rename files, and a mismatch is reported rather than guessed at.
A truncated data chunk recovers what is actually in the file instead of trusting the header or
refusing to open it. Non-finite float samples are zeroed: one NaN turns a whole mel frame into NaN
and the decode returns nothing with no error anywhere.

## 7. Cap decode threads at about eight

Past that, returns flatten while UI responsiveness suffers.

**Here:** `DecodeThreadPlanner` implements the policy — and **it cannot currently be applied.** No
entry point in parakeet.cpp's ABI v6 takes a thread count. `EngineCapabilities.SupportsThreadCount`
is `false`, `--threads` prints a line saying the value does not reach the decoder, and the UI shows
no thread control rather than a slider connected to nothing. See `docs/UNPROVEN.md`.

## 8. Keep models out of the install directory

Models there are destroyed by every update and uninstall, turning each patch into a 670 MB
re-download. `OpenWhispr` broke folder redirection and roaming profiles by hardcoding
`%USERPROFILE%\.cache\...`.

**Here:** `LocalModelStore` resolves `Environment.SpecialFolder.LocalApplicationData` through the
platform API — never a hardcoded path — and appends `Uindosill/models`. `UINDOSILL_MODELS_DIR`
overrides it for portable installs and tests. A test asserts the directory is not under the install
directory.

**And the second half of it, which only appeared when there was an installer.** Velopack installs a
Windows application under `%LOCALAPPDATA%\<package id>` and its uninstall deletes that directory's
contents recursively — one `remove_dir_contents(&root_path)` in `src/bins/src/commands/uninstall.rs`,
with no keep list, no exclusion and no flag. A package id of `Uindosill` would therefore have made
uninstall delete every downloaded weight: on the machine this was checked on, 4.295 GiB. Keeping
models out of the install directory is not enough on its own — **the install directory must also not
be their parent**, and that is decided by a string in a csproj that nothing about the build would
otherwise check.

The id is `UindosillDesktop`, it lives once in `VelopackPackageId`
(`src/Parakeet.App/Parakeet.App.csproj`), and three things hold it: five tests in
`tests/Parakeet.App.Tests/PackagingTests.cs` — including one that rebuilds both directories and runs
the real recursive delete — a refusal in `scripts/package-windows.ps1` before it publishes anything,
and an install/update/uninstall on a real desktop with every weight hashed before and after
(`docs/UNPROVEN.md`). Setting the id to `Uindosill` fails four of the five tests; that was checked by
doing it, not assumed.

## 9. Pin `SelfContained` in the project, not the CI workflow

On .NET 8+ a RuntimeIdentifier no longer implies self-contained, so a lost `--self-contained` flag
produces a build that is green in CI and broken on every machine without the exact runtime.

**Here:** in `Directory.Build.targets`, gated on `IsPublishableApp`. **Not** in
`Directory.Build.props` — that file is imported before the project body, so the condition would be
evaluated before the project sets the property and would silently do nothing. That exact mistake was
made and caught here by counting the files in a publish output: eleven means framework-dependent,
~200 means self-contained. The comment in that file records the check.

## 10. Sign every PE, not just `Setup.exe`

Smart App Control and WDAC/AppLocker evaluate every loaded binary, and unsigned native DLLs are
exactly what gets blocked. A signed installer dropping unsigned executables is itself a recognised
malware shape. Budget for SmartScreen reputation: a 29k-star project still fields "Windows Defender
detected Trojan in installer" reports.

**Here:** not done, and now deliberately so — v1.0 ships unsigned (`docs/PHASES.md`, *Decisions
taken*). The installer exists and passes `neither --signParams nor --signTemplate`; `vpk` says so on
every pack, twice, and it is not a warning to silence: *"No signing parameters provided, 229 file(s)
will not be signed."* The layout is still ready for signing whenever it is taken up: every native
lives under `native/<rid>/<backend>/` where a signing step can enumerate it, nothing is bundled into
a single file that would hide a binary from the signer, and `vpk pack --signTemplate` takes a command
with `{{file}}` substituted, so the step is one argument rather than a rewrite. The route decided on
2026-08-16 is SignPath Foundation's free open-source programme, whose terms sign this project's own
binaries only — so on that route the upstream `parakeet.dll`s stay unsigned, and this gotcha stays
open for Smart App Control machines even once the installer and the app are signed.

## 11. Avalonia 12 breaking changes

`SystemDecorations` is obsolete in favour of `WindowDecorations`, and
`OnLostFocus(RoutedEventArgs)` became `OnLostFocus(FocusChangedEventArgs)`. Two more found while
building this:

- **`TextBox.Watermark` is obsolete → `PlaceholderText`.** The XAML compiler reports this as
  `AVLN5001`, which is *not* affected by `TreatWarningsAsErrors`, so it will sit in your build log
  until it is removed.
- **Drag-and-drop moved from `DragEventArgs.Data` to `DragEventArgs.DataTransfer`,** with
  `DataFormat.File` and `TryGetFiles()` in place of `DataFormats.Files` and `GetFiles()`.

`Avalonia.Diagnostics` has no 12.x release (latest 11.3.20); referencing it downgrades the whole
Avalonia graph. It is deliberately absent from `Directory.Packages.props`.

## 12. The NAudio metapackage cannot be built on a Linux SDK

`NAudio` pulls in `NAudio.WinForms`, whose `FrameworkReference` on
`Microsoft.WindowsDesktop.App.WindowsForms` cannot be resolved without the Windows Desktop targeting
pack — which ends cross-building the Windows target from CI.

**Here:** `NAudio.Wasapi` (which contains `MediaFoundationReader`) plus `NAudio.Core`, referenced
in a single `net10.0` assembly — see gotcha 14 for why the target framework matters here.

## 13. Do not name your executable after your native library

The CLI was originally `parakeet`, which publishes a managed `parakeet.dll` into the same directory
the native loader searches for `parakeet.dll`. A managed assembly is a valid PE and `LoadLibrary`
will happily load it; the failure then arrives as a missing export and reads as a corrupt native
build. The executable is `uindosill`.

## 14. Do not `#if` a feature out of the target framework your apps actually reference

`Parakeet.Audio` multi-targeted `net10.0;net10.0-windows` with the Media Foundation reader behind
`#if WINDOWS`. `Parakeet.Cli` and `Parakeet.App` target plain `net10.0`, so a project reference
always resolved the `net10.0` flavour — the one the decoder is compiled *out* of. Media Foundation
was unreachable dead code in every build that shipped, and on Windows the app told the user
"compressed containers need Media Foundation, which exists only on Windows".

CI never had a chance: the `-windows` flavour compiled cleanly and nothing referenced it. Neither
did any test, because there was no test that the type was *present*.

`Parakeet.Audio` is now a single `net10.0` assembly with the Windows surface guarded by
`OperatingSystem.IsWindows()`, which the platform-compatibility analyser understands. The guard is
a reflection test asserting the type exists in the assembly — the failure was that the code did not
exist, not that it misbehaved, so that is what is asserted, and it runs on Linux where the mistake
was invisible.

## 15. `PeakWorkingSet64` is gone the moment the process exits

`Start-Process -PassThru -Wait` returns a `Process` whose `PeakWorkingSet64` reads back as **zero**,
not as an error. Windows discards the counter at exit, and PowerShell turns the resulting property
failure into `$null`, which a `{0:N0}` format string renders as a confident `0 MB`. A three-hour
transcription that used gigabytes reported a peak of nothing at all, and reported it in the same
shape as a real measurement.

Sample it while the process is alive instead: start without `-Wait`, poll `Refresh()` and keep the
highest value seen. The counter is itself a peak and never falls, so polling is exact — unlike
sampling `WorkingSet64`, which really can miss a spike between reads. `scripts/measure-transcribe.ps1`
does this, and prints "not sampled" rather than a zero when the run ends inside one poll interval.

## 16. Do not report the output file you assumed was written

`TranscriptWriter` renames rather than clobbers, so transcribing `chunk.m4a` when `chunk.srt`
already exists writes `chunk (2).srt`. The measurement harness reconstructed `<stem>.<ext>` to
report output sizes, so it read the *stale* `chunk.srt` from a previous run and printed its size as
a result of this one. The CLI had said `wrote chunk (2).srt` on the line above.

It was harmless there only because the two runs produced identical bytes. On a run where the output
changed it would have shown the old output with nothing to indicate it was old — the same failure
as gotcha 15, a number that looks like a measurement of the thing you ran and is a measurement of
something else. The harness now finds outputs by modification time and says `NOT WRITTEN BY THIS
RUN` when nothing fresh exists, and the invariant checks parse the JSON that this run produced.

## 17. A CUDA library that will not load is indistinguishable from one that is not there

`NativeLibrary.TryLoad` returns `false` for a missing file and for a file whose dependencies cannot
be resolved. The loader treats both as "this backend is not vendored" and moves on. For a CUDA
request the next backend is CPU — Vulkan is deliberately skipped — so the predicted outcome is a run
that finishes with a correct transcript at CPU speed and no error anywhere, the only trace being the
RTF. Read out of the loader's code path rather than reproduced: `VCOMP140.DLL` was present in
`System32` on the machine measured, and no machine actually missing a dependency has been tried.

CUDA is the only backend this can happen to, because it is the only one with siblings. The v0.5.0
CUDA `parakeet.dll` imports `cudart64_12.dll` and `cublas64_12.dll` from its own directory, and
**`VCOMP140.DLL`**, the MSVC OpenMP runtime, which ships in the Visual C++ redistributable rather
than with Windows — a machine can have `MSVCP140` and `VCRUNTIME140` and still not have that one.

**Here:** two things. Windows only searches a module's own directory for that module's imports when
`LoadLibrary` was handed an **absolute** path, so `--native-dir` and
`UINDOSILL_PARAKEET_NATIVE_DIR` are rooted through `Path.GetFullPath` before use — a relative path
passes `File.Exists`, which resolves against the working directory, and then loads without the
sibling search. Test: `ARelativeNativeDirectoryIsSearchedAsAnAbsolutePath`. And
`scripts/vendor-cuda.ps1` reads `parakeet.dll`'s import table after unpacking and names any import
that is neither in the drop nor in `System32`, so a missing dependency is a named file rather than a
transcription that came out mysteriously slow.

The check that costs nothing: the `backend` field in the JSON and the `on <backend>` line from
`scripts/measure-transcribe.ps1` report the backend that **loaded**. Never quote an RTF without
reading it.

## 18. `doctor` proves the library loads, not that it can compute

`DoctorCommand.Probe` calls exactly one entry point, `parakeet_capi_abi_version`, which returns an
integer and touches no GPU state. That is the right check for what it was built for — the AVX2
static-initialiser crash in gotcha 1, and dependency resolution in gotcha 17 — and it is **not** a
check that the backend can decode.

A CUDA build with no kernels for the installed card loads perfectly, answers the ABI question, and
is reported `ok — abi 6`. It fails later, at the first kernel launch, with `no kernel image is
available for execution on the device`. An `ok` from `doctor` is therefore necessary and nowhere
near sufficient for a GPU backend; only a transcription settles it.

There is a second way the line can mislead. The loader's flat pass — the shape you get from
unzipping one upstream release into a directory — tags whatever it finds with the *requested*
backend, because a flat directory carries no evidence of which build it holds. A Vulkan
`parakeet.dll` in `native/win-x64/` is loaded by a `cuda` probe and reported as `cuda`.

**Here:** the probe prints `ok — abi 6 from <path>`, and the path is the discriminator. Read it.
`docs/NATIVE-BINARIES.md` says so where the command is documented.

## 19. ggml-CUDA prints a fatal-looking error at teardown, after a clean run

Both CUDA runs made through `scripts/measure-transcribe.ps1` ended like this, *after* all three
output files were written (the later sweep runs were not inspected for it):

```
CUDA error: driver shutting down
  current device: -1, in function ~ggml_backend_cuda_buffer_context at ...ggml-cuda.cu:635
  cudaFree(dev_ptr)
...ggml-cuda.cu:102: CUDA error
```

Exit code 0. Transcript complete, and byte-identical to the other run. This is ggml's CUDA buffer
destructor running during process teardown, after the driver has begun unloading, so `cudaFree`
returns `cudaErrorCudartUnloading`. It is upstream's shutdown ordering, not this codebase's.

Two ways it bites. Read literally it says a run failed that did not, and a harness keying on stderr
would discard a good result. And ggml's error path is `GGML_ABORT` — the only reason the process
still exited 0 is that the abort landed after the exit code was set, which is an ordering accident
rather than a guarantee. On another runtime or another driver the same teardown could plausibly
present as a crashed process with complete, correct output on disk.

**That last sentence is no longer hypothetical.** On 2026-08-15, on the same machine and the same
driver, eight consecutive CUDA runs took the abort *before* the exit code was set and returned
`0xC0000409` with complete, correct output on disk — so `measure-transcribe.ps1`, which keys on the
exit code, printed `THE RUN FAILED` over a run whose every figure was sound. The desktop app did
the same thing on close whenever a CUDA model had been loaded: a good session, then a crash on the
way out. The measurement and its limits are in `docs/UNPROVEN.md`.

**Where it actually comes from — read off the crash dumps on 2026-08-16, GUI and CLI alike:**
`ExitProcess → LdrShutdownProcess → parakeet.dll DLL_PROCESS_DETACH → execute_onexit_table →
pk::Backend::~Backend → abort`. parakeet.cpp keeps one `pk::Backend` per process — the ggml
backend plus a persistent device compute buffer — in a static `unique_ptr`, and its static
destructor frees device memory after the driver has torn down. It is not our `parakeet_ctx`: the
CLI frees its context and aborted anyway, and disposing the app's engine before closing changed
nothing. Upstream knows: `pk::shutdown_backend()` exists for exactly this, documented "call once at
program exit, after all model objects are destroyed", and their own CLI calls it after every
subcommand.

**Here:** that call is made — `ParakeetNativeLibrary.TryShutdownBackend()`, reached through the
export `?shutdown_backend@pk@@YAXXZ` because the function is not in the C ABI and only happens to be
exported (upstream exports every symbol). The CLI calls it in `CliEntryPoint.RunAsync`'s `finally`,
after every command's `await using` engine has gone; the app's `MainWindow` turns the first close
into `MainWindowViewModel.ShutdownAsync` — cancel and wait out a running batch, dispose the
`ModelSession`, which unloads and then calls `IEngineProvider.ReleaseBackend` — and only then closes.
Measured on the RTX 5080: eight CUDA processes without the call all exited `0xC0000409`, sixteen
with it exited 0 (GUI and console), the fixed app exits 0 on an idle close and a mid-batch close,
Vulkan and CPU exit 0 either way, and the CLI's CUDA run exits 0 with the error line gone. Two things survive from before. A
vendored build that stops exporting the symbol gets the old behaviour back — `uindosill doctor`
prints a warning under that backend's line when the export is missing, and
`ShutdownBackendAvailable` says so in code. And the exit code alone was wrong in both directions
for a while, so `scripts/measure-transcribe.ps1` still prints it first and the outputs are still
worth reading.

## 20. The first GPU run on a machine measures the driver compiling shaders, and calls it decoding

Vulkan on this project was first recorded at RTF 0.0230, 13.8 s for a ten-minute file. Every later
run on the same machine, the same binary and the same file came back at 6.4–6.8 s. Emptying the
NVIDIA driver's shader cache at `%LOCALAPPDATA%\NVIDIA\GLCache` and running twice reproduces it
exactly: **14.07 s, then 6.77 s.** About 7.3 seconds of that first run was the driver compiling
pipelines, not the model decoding audio.

The trap is where the cost lands. It is not in the model-load figure and not in the separately
reported warm-up decode — it is inside `processingSec`, the number the real-time factor is computed
from. So the first GPU benchmark anyone runs on a fresh machine is inflated by a factor of two, in
the one field that looks like it measures decoding, and it never happens again on that machine. Two
people benchmarking the same build on the same hardware get answers 2× apart and neither is wrong.

Gotcha 2 is the same lesson one level up and does not cover this: `WarmUpAsync` runs a throwaway
decode over ~0.5 s of dither at load, and that demonstrably does not compile the pipelines a real
workload needs. **CUDA does not have this problem** — its kernels ship as precompiled cubins for the
architectures upstream targeted, so its first-ever run on this machine decoded in 3.90 s against
3.84 s for the second. A CUDA build with *no* cubin for the installed card would have the same shape
as Vulkan: driver JIT from PTX is not free, and what it costs has not been measured here, because no
card that takes the PTX path was ever run.

**Here:** not fixed, because fixing it means either compiling every pipeline at startup (which moves
the cost rather than removing it) or persisting a pipeline cache this project does not own. It is
recorded so that a GPU number is never quoted from a single run on an unfamiliar machine. Run it
twice; if the two disagree by more than the few per cent of ordinary run-to-run noise, the first one
was measuring the driver. `docs/UNPROVEN.md` reports steady-state and first-run figures as separate
rows for this reason.

## 21. `Environment.SetEnvironmentVariable` is invisible to the native library you set it for

Setting a knob a native library reads — `GGML_VK_DISABLE_BFLOAT16`, or any other `getenv` lookup —
with `Environment.SetEnvironmentVariable` **does nothing on Windows, and reports no error**. .NET
calls `SetEnvironmentVariableW`, which updates the process environment block. The UCRT keeps a
*separate* table, filled once at startup and thereafter only through `_putenv`, and `getenv` reads
that one. The managed call succeeds, `Environment.GetEnvironmentVariable` reads the value back, and
the library never sees it.

Measured rather than reasoned about. The same variable, same value, same position before the load:

| How it was set | Native `getenv` | Model load |
|---|---|---|
| `Environment.SetEnvironmentVariable` | did not see it | failed |
| `ucrtbase!_putenv` | saw it | loaded |

`Interop/NativeEnvironment` writes both — the CRT copy because native code reads it, the managed
copy so a later managed reader is not told something different — and returns whether the native
write actually took, because "only the managed copy was set" is indistinguishable from success at
the call site otherwise.

Two traps sit behind this one. **Setting the knob after the thing that reads it has already run is
useless**, and for ggml's Vulkan backend that means before the *first model load*, since device
initialisation happens there and once per process. And **a failed Vulkan load cannot be retried in
the same process**: the device does not survive it, and a second `parakeet_capi_load` dies in
`vkCreateFence` with an invalid device rather than returning NULL again. So there is no
try-then-fix-then-retry; the decision has to be made before the first attempt.

## 22. Run the vendor's own binary before believing your bindings about their library

The Vulkan failure above returns NULL from `parakeet_capi_load`, and the C ABI carries no message —
so from this side the cause is unobservable and the temptation is to infer one. Two rounds of
inference here produced a confident, wrong answer (bf16 *shader variants* failing to compile).

The `bin-` archive beside the `lib-` one contains upstream's `parakeet-cli`, built from the same
source at the same tag. One command through it printed the actual cause:

```
transcribe failed: vk::PhysicalDevice::createDevice: ErrorExtensionNotPresent
```

Device creation, not shader compilation — a Vulkan device extension being requested that the driver
does not expose, which no amount of reading this repository's code could have revealed. `parakeet-cli
info <model.gguf>` is *not* the reproduction to reach for, incidentally: it reads GGUF metadata and
never creates a backend, so it succeeds on a machine where every transcription fails.

**Here:** the `bin-` archive is deliberately not vendored (see docs/NATIVE-BINARIES.md — it holds no
shared library and is of no use to the build). Downloading it for ten minutes to diagnose a native
failure is a different act from shipping it, and it is worth doing before filing anything upstream:
a bug report that says "returns NULL" is a bug report the maintainer has to reproduce from scratch.

## 23. Two transcripts compared by word index measure the offset, not the difference

Pair word *i* of one transcript with word *i* of the other and a single inserted word desynchronises
every pair after it, so each is counted as a difference. A guard on the two word totals catches the
common case and cannot catch the one that matters when two variants of one model are compared —
insertions and deletions that cancel out. f16 against q4_k on ten minutes gave exactly 1,606 words
each; the guard passed and the tool reported 727 differing tokens where a word-level edit distance
found 50. The tell was in its own output: joined text of 8,326 against 8,319 characters is not what
727 different words look like.

**Here:** `scripts/compare-transcripts.ps1` aligns the two word streams by word-level Levenshtein
distance (`src/Parakeet.Core/Text/WordAlignment.cs`, Hirschberg in linear memory, shared with
`word-distance.ps1` and `uindosill wer`, compiled into the scripts with `Add-Type` so they still need
no build) and reports substitutions, deletions and insertions separately, with timestamp and
confidence figures over the aligned pairs. `docs/UNPROVEN.md` keeps the 727 as the record of the
artefact.

## 24. A word error rate over speech with numbers in it measures the number convention first

The model writes numbers as words (`two hundred and fifty two`, `eighty-seven`); every human
transcript this project scores against writes digits (`262`, `87`). Score the two as written and
each number is several errors before recognition is even involved. On the first Earnings-22 call
scored, that was the largest single class of error, and rendering cardinal number words as digits on
both sides moved the call from 15.7% to 13.9% — a fifth of the apparent error rate — and left the
one real error in that phrase (`262` heard as `252`) as one substitution. Beyond numbers, the raw
figure over whitespace tokens is ~29% against a normalised ~10%, and the *style* of the human
transcript moves every model by three points (verbatim 10.2%, non-verbatim 13.4%), because the model
writes down repetitions a readability edit removes and expands `gonna` to `going to`.

**Here:** `TranscriptNormalizer.WordErrorRateTokens` states its rules — lower-case, punctuation off,
hyphens split, brackets dropped, six fillers dropped, `%` to `percent`, number words to digits — and
`uindosill wer` prints the normalised and the raw rate side by side and names the normaliser on
every run. What it does not do is also stated (paired years, contractions, spellings), which is
why a figure from here is comparable to another figure from here and not to a leaderboard entry for
the same model, and `docs/UNPROVEN.md` quotes both transcript styles rather than one.

## 25. `TimeSpan.FromSeconds(double)` truncates to the tick, and a scorer notices

`TimeSpan.FromSeconds` on .NET Core 3.0 and later converts the value to ticks and *truncates*
(`(long)ticks`), so a value that arithmetic left a hair under its decimal — an RTTM turn's end
computed as `onset + duration`, say `10.200 + 8.100`, which in binary64 is `18.299999999999997` and
scales to `182999999.99999997` ticks — comes back one tick (100 ns) short. Not every such sum does:
`0.42 + 5.63` is exactly representable enough to scale to `60500000.0` and survives, which is why
the bug looks absent until enough turns are read at once. Individually invisible; the diarisation
scorer's validation against pyannote.metrics found it as a ~1 µs disagreement on a ten-minute
fixture, several turns each a tick short of the reference implementation.
`SpeakerTurns.FromSeconds` rounds to the nearest tick instead, and every turn boundary parsed from
an RTTM file or an Audacity label export goes through it. The general lesson is older than this bug:
validate a scorer against the reference implementation on material long enough for a hundred small
errors to add up to one you can see.

## 26. The updater runs your `Main`, and its defaults are not your decisions

Three traps in one place, all of them quiet.

**Velopack re-runs the application's own executable to perform install, update and uninstall steps,**
passing them as command-line arguments. `VelopackApp.Build().Run()` is what recognises those, does
the work, and exits the process — so every line above it runs in each of those short-lived
invocations. For a GUI application that means a window flashing up during an install, and for this
one it would have meant a `MainWindowViewModel`, an engine provider and a model store constructed in
a process that exists to move some files. Nothing warns you: the install still succeeds.

`vpk pack` does statically decompile the main executable and refuse to build if the call is absent —
*"Unable to verify VelopackApp is called"*, with `--skipVeloAppCheck` as the escape hatch — but it
checks that the call **exists**, not that it is first. Ours prints
`Verified VelopackApp.Run() in 'System.Int32 Parakeet.App.Program::Main(System.String)'` (vpk's own rendering, quoted as printed — the method takes `string[]`) on every
pack; that line is not evidence of correct placement.

**`SetAutoApplyOnStartup` defaults to ON.** An update already downloaded is applied during the next
startup, without asking. That is a reasonable default and it is not this product's decision
(`docs/PHASES.md`, decision 4: nothing installs itself), so `Program.Main` sets it to `false`
explicitly. A decision that happens to match a default still has to be written down, because the
default is the vendor's to change.

**Applying an update exits the process without a `Closing` event.** The window's close handler is
where a running batch is stopped, the model unloaded and the native backend released while the GPU
driver is still alive — gotcha 19. `ApplyUpdatesAndRestart` never returns, so a CUDA user pressing
*Download and restart* would have reached the native static teardown with a backend resident and
aborted with `0xC0000409` after a perfectly good run. **Here:** `UpdatesViewModel` awaits the
window's own `ShutdownAsync` between the download and the restart, and a test asserts that ordering
rather than the two calls merely both happening.

## 27. `OrtValue` exposes four members that are compile errors under `TreatWarningsAsErrors`

`Microsoft.ML.OnnxRuntime` depends on `System.Numerics.Tensors`, which marks twelve of its public
types `[Experimental("SYSLIB5001")]` — `Tensor<T>`, `TensorSpan<T>`, `ReadOnlyTensorSpan<T>` and
friends. **`[Experimental]` is an error by default, not a warning**, so it does not need this
repository's `TreatWarningsAsErrors` to bite; it bites anyway.

Referencing the package is fine and nullability is a non-issue — the ORT assembly carries no
nullable annotations at all, so `Nullable=enable` produces nothing. What fails is calling
`OrtValue.GetTensorDataAsTensorSpan`, `GetTensorMutableDataAsTensorSpan`,
`GetTensorSpanMutableRawData` or `CreateTensorValueFromSystemNumericsTensorObject`. Each has a plain
`Span<T>` sibling that does the same job — `GetTensorDataAsSpan<T>`, `GetTensorMutableDataAsSpan<T>`
— and those are the ones to use.

The trap is the fix that looks obvious: adding `SYSLIB5001` to `NoWarn`. It compiles, and it silences
the diagnostic for the whole project rather than for the call that needed it, so the next preview API
to arrive under the same id arrives silently. Two `Tensor<T>` types are also in scope once ORT is
referenced — its own `Microsoft.ML.OnnxRuntime.Tensors.Tensor<T>` and the BCL's — and only one of
them is experimental, which makes a stray `using` enough to produce the error in code that never
meant to touch a preview API.

## 28. `check-test-counts.py` reads yesterday's results if they are still on disk

It reads the TRX files `dotnet test --logger trx` leaves under `tests/*/TestResults/`, and **runs the
suite itself only if it finds none** — which its docstring says, and which is easy to read as "it
measures the suite" rather than "it measures whatever TRX are lying around".

On 2026-08-20 it reported **549 tests and 94 CLI tests** against documents claiming 637 and 125, from
TRX left by an earlier run, and the suite had in fact just passed 637. Two minutes went into looking
for 88 tests that had never gone missing. The fix is one line before the check:

```bash
rm -rf tests/*/TestResults && dotnet test Uindosill.slnx -c Release --logger trx && python3 scripts/check-test-counts.py --no-run
```

CI does not have this problem, because its workspace is clean and it passes `--no-run` so a missing
TRX is a failure rather than a silent re-run. A working copy is the opposite: the TRX are always
there and always older than the change being checked.

**And do not pipe `dotnet test` through `tail`.** The per-assembly `Passed!` lines are what say which
projects ran, and `| tail -8` in the same session hid two of the six assemblies — which is how a run
that covered everything looked like a run that had lost a third of the suite.

**A retired project's leftovers used to keep voting, and the staleness guard could not see them.**
`bin/`, `obj/` and `TestResults/` are gitignored, so moving a project to `attic/` takes its sources
out of the solution and leaves all three sitting in the working copy. The leftover TRX is not stale
by the rule above — it is *newer* than the leftover DLL, because one run produced both — so the
pair is self-consistent and sailed through. On 2026-08-22 the remains of
`Parakeet.Engine.Sortformer.Tests` and `Parakeet.Engine.Marian.Tests` added 46 and 31 tests to a
suite that no longer contains either, and the check reported **854 against a documented 777** with
every per-assembly line looking plausible. A `TestResults/` now counts only when a `*.csproj` sits
beside it, and the directories skipped are **named in a line above the totals** rather than dropped
in silence: they do not appear in `git status`, so that notice is the only thing in the repository
that says they are on the disk at all. Deleting them is safe and is the actual fix.

That rule and the staleness rule are both exercised by `python3 scripts/check-test-counts.py
--self-check`, which builds a live project and a retired one's leftovers in a temporary directory
and needs no toolchain. It exists because each failure needs the working copy in one particular
shape and a checkout is only ever in one shape at a time, so the real tree can demonstrate neither.
CI runs it beside the count check.

## 29. Scripted logits mean nothing in absolute terms, because the search takes a log-softmax first

The beam search's tests write their own distributions — `Logits((Eos, -5f))` and the like — so that
cases a real model will never produce on demand can be reached: a banned token as the single most
likely continuation, a short hypothesis losing to a long one by a hair. **Only the gaps between the
listed values do anything.** A step that lists one token gives that token a log probability of about
zero however small a number it was written with, because normalising one entry produces certainty.

Three of the first six such tests failed for that reason and each looked like a bug in the search.
The one written to prove beam search beats a one-beam decode gave the bad branch `Logits((Eos, 0f))`
as its continuation, meaning to make finishing there expensive; it made it free, and the bad branch
won on merit. The one meant to prove the cache is permuted gave the leading beam a continuation of
`-5f` alone, which normalised to the same zero as its rival's, so nothing overtook anything and the
surviving order was the identity.

**A step that is meant to cost something has to have somewhere else for the probability to go.** All
three were fixed by giving the branch a four-way tie to pay for rather than a smaller number.

## 30. Under `Set-StrictMode -Version Latest`, a one-element result is not an array and has no `.Count`

Every script here sets it, and PowerShell unwraps a single-element collection to a scalar on
assignment. Wrapping each branch is not enough — the unwrap happens on the way out of the
conditional:

```powershell
# Throws "The property 'Count' cannot be found on this object" for -Languages es, and only for
# a single language, which is exactly how it will be run the first time.
$codes = if ($Languages) { @($Languages -split ',') } else { @(Get-ChildItem ... ) }
if ($codes.Count -eq 0) { ... }

# The @() has to be around the whole expression.
$codes = @(if ($Languages) { $Languages -split ',' } else { Get-ChildItem ... })
```

Slicing has the same edge: `$rows[0..($n - 1)]` is an array for `$n` above one and a scalar for
`$n` of one. The failure arrives with no line number from the caller's point of view and names a
property nobody wrote, which is why it is worth recognising on sight rather than debugging.
