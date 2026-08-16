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

**Here:** not yet done — this is Phase 5 and the repository has no signing identity. The layout is
ready for it: every native lives under `native/<rid>/<backend>/` where a signing step can enumerate
it, and nothing is bundled into a single file that would hide a binary from the signer. The route
decided on 2026-08-16 (`docs/PHASES.md`, *Decisions taken*) is SignPath Foundation's free
open-source programme, whose terms sign this project's own binaries only — so on that route the
upstream `parakeet.dll`s stay unsigned, and this gotcha stays open for Smart App Control machines
even once the installer and the app are signed.

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
