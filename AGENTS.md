# Working agreement

Operational notes for an agent session. Everything about *the project* is in `docs/` and in
`README.md` — do not restate it here, because two copies of a fact is how one of them goes stale,
which is a failure this repository has already had to fix more than once.

## Budget

**A workflow may spawn at most 16 agents.** That is a hard ceiling set by the maintainer's usage
limits, not a guideline. Prefer fewer. Before reaching for a fan-out, check whether the question
is answerable by reading and grepping, which it usually is at this repository's size.

Two things make a review workflow expensive, and neither is the agent count on its own:

- **Auditors that build or test.** The suite is fast, but seven agents each running
  `dotnet test` is not. Verify the build once yourself and tell the agents the result.
- **A verify phase that scales with findings.** One skeptic per finding is unbounded — twenty
  findings is twenty more agents. Batch them, or cap it.

## Building and testing

The toolchain comes from the cloud environment's setup script, whose text is
`scripts/cloud-setup.sh` — a pinned SDK 10.0.400 and PowerShell 7.6.4 unpacked from
`packages.microsoft.com`. See that file's header for why not the vendor's installer. If the tools
are missing, that field has not been filled in; do not try to install one yourself before saying
so.

```bash
dotnet build Uindosill.slnx -c Release   # must be 0 warnings: TreatWarningsAsErrors is on
dotnet test  Uindosill.slnx -c Release   # 1785 tests, no weights, no display, no network
python3 scripts/check-diariser-auto.py   # what the diariser's `auto` elects; CI runs it too
python3 scripts/check-test-counts.py     # the counts above, against the run that just happened
```

That last line is why the number in the comment can be trusted, and CI runs it too. **If you change
the test count, run it** — it prints what every document should say, and checks all three documents
that quote a count together.

The diariser line is the sidecar's only guard, since there is no Python suite: **run it after any
change to the election in `python/uindosill_engines/diariser/pyannote_engine.py`** (`AUTO_ORDER`,
`resolve_auto`), because it decides which arithmetic unit a user's diarisation runs on; the
check must be run manually when that file is edited. Nothing in CI or the suite parses
`scripts/*.ps1` either; parse changed scripts manually, and before a commit parse them all — the
exit code is the number of errors, each named:

```bash
pwsh -NoProfile -Command '$e = @(); Get-ChildItem scripts/*.ps1 | ForEach-Object { $t = $err = $null; [Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$t, [ref]$err) > $null; $e += $err }; $e | ForEach-Object { "{0}: {1}" -f $_.Extent.File, $_.Message }; exit $e.Count'
```

**Nine of those 1785 tests skip themselves.** Two are platform-specific: the Media Foundation
extension list, and the uninstall cleanup's link test, which needs developer mode on Windows and so
skips on Windows and runs on Linux. The other seven are asked for by name, because a count that
depends on what is installed cannot be written into a document CI checks:

```bash
UINDOSILL_FLEURS_DIR=<a google/fleurs snapshot's data/ directory> dotnet test Uindosill.slnx -c Release
```

It is the test that says the German number rewrite in `TranslationRequest.Mark` is still a no-op on
written text. **Run it after any change to `GermanNumberWords`**: if it ever fires on FLEURS, the
sentences the shipping path sends the translator are no longer the sentences the published chrF++
figures describe.

The other two are the speech detector's, in `Parakeet.Engine.SileroVad.Tests`, which need the Silero
graph — a 2.2 MiB download — and skip unless `UINDOSILL_SILERO_VAD` names it:

```bash
UINDOSILL_SILERO_VAD=<path to silero_vad.onnx> dotnet test Uindosill.slnx -c Release
```

**Run them after any change to `src/Parakeet.Engine.SileroVad/`**, and drive `uindosill transcribe`
over a real file with the model installed beside them (it is the default detector then, and the
stderr line names it): the two tests say the graph loads and scores silence low at three sample
rates, and nothing else in the suite runs the model.

The last four are the v2 language-model engine's, in `Parakeet.Engine.LlamaServer.Tests`, which
need the vendored `llama-server` drop and a small GGUF, and skip unless both are named:

```bash
UINDOSILL_LLM_SERVER_ROOT=<a native/win-x64/llm directory> UINDOSILL_LLM_TEST_MODEL=<path to a small .gguf> dotnet test Uindosill.slnx -c Release
```

**Run them after any change to `src/Parakeet.Engine.LlamaServer/`** — they drive a real child
server end to end (load, health, an ask, parse, validate) on the CPU backend, one per mode: the
grammar-constrained path, the think-before-answering path, the whole-transcript path, and the
transcript tidy — four lines in flight, every one held to the delete-only contract.
`UINDOSILL_LLM_TEST_BACKEND=vulkan` (or `cuda`) runs the same four on a machine that has one,
which is the only place a child-process argument change is really tested. Nothing else in the
suite starts the process.

**The seven checkpoint tests that used to sit beside it went to `attic/` on 2026-08-21** with the
C# translator they exercised. Nothing in the suite now reads real translation weights, and nothing
replaces them: the sidecar's own translation parity fixtures — one per checkpoint since 2026-09-04,
chosen by vocabulary size — are a smoke test that needs a checkpoint and a Python, so they run at
load on a real machine rather than in CI. **After any change to
`python/uindosill_engines/translator/`, drive them by hand** — for each translation checkpoint, a
load on the CPU and a load on `webgpu`, each reporting `parity` — because the suite cannot. The
product path skips the check on the CPU, since the CPU is what the reference is, so the CPU load is
`parity.check` through the engine directly; the `webgpu` one is `uindosill translate --backend
webgpu`, which is silent when it passes and warns when it does not.

**Every description the window draws fits in two lines**, at the window's default width and at its
minimum — every page but the Models tab, whose catalogue notes are long on purpose — and nothing in
the suite can check it: `Parakeet.App.Tests` runs on the default headless host, whose text shaper
is a stub. `tools/measure-lines` opens the window on a Skia host and counts the rendered lines of
every wrapped block on every other page, the About window included, and exits with the number that
overflow, each named with its page and its count at both widths. **Run it after any change to
`src/Parakeet.App/Views/` and after any change to `src/Parakeet.App/ViewModels/`:**

```bash
dotnet run --project tools/measure-lines -c Release   # exit code is the number of blocks over two lines
```

**A session here can compile and run the tests.** Do not assume otherwise and hand the maintainer
unverified code — an earlier handoff said the sandbox had no SDK, and acting on that would have
shipped a red build.

What a container still cannot do is transcribe anything real: that needs the Windows natives and
a model, neither of which is in the clone. `--fake` exercises the whole pipeline without them.

## Session fixtures

`.codex/` and `.agents/skills/` hold the Codex session fixtures. `.codex/hooks.json`
registers no project hooks: no automatic pull, edit guard or test reminder runs.
When an update is requested, run `git pull --ff-only` and reconcile by hand if it does not
fast-forward. Run the checks above manually after the relevant edits.
The retained `.codex/hooks/edit-hooks.py` helper reads changed paths from a Codex event's
`tool_input.command`, including both ends of a rename. Its PostToolUse mode reports the matching
obligations and runs cheap checks; a new gated test still needs a rule there as well as a line
here so the manual preflight check can compare them. Its PreToolUse mode checks `attic/` edits.
An intentional attic edit still needs explicit user approval, because a retired engine has files
named like the live ones.
The `.codex/hooks/attic-guard.sh` and `.codex/hooks/gated-test-reminder.sh` files are compatibility
wrappers around the same Python entry point. `scripts/check-codex-hooks.py` checks patch routing,
the blocking response and reminder behavior without changing product files. Run it after a hook change.
`scripts/check-drive-memory.ps1` checks the memory export route and dispatcher against a fake
rclone without network access or user memory. Run it after changing that route; its fixtures stay
under `runs/check-drive-memory/<unique>/`.
The agent definitions are `.codex/agents/*.toml`; the skills live under `.agents/skills/`, with
manual invocation configured in each skill's `agents/openai.yaml`. The auditors default to a
read-only sandbox; their instructions also forbid edits, builds and test runs when a parent
session supplies broader permissions. Review any future hook registrations with `/hooks` before
relying on them.
The read-only agents sweep and fix nothing: `claims-auditor` for the numbers, under the rule below;
`reference-auditor` for the names — paths, scripts, counts and the dated pointers into PHASES —
against the tree; `harness-reviewer` for a changed measurement script, against the gotchas the
harnesses taught. The user-invoked skills sequence this file and defer to it wherever they
disagree: `$new-engine`, `$wrap-runs`, `$preflight` (the build block above as one command, run
the way CI runs it — a TRX log for the count script to read, and its self-check — plus a check
that the reminder hook still mirrors it) and `$record-measurement` (a finished run into UNPROVEN,
PHASES and the README rows, then `claims-auditor`).

## The rule this project runs on

Every claim is either measured or explicitly marked unproven. When reporting a number, make sure
it measures the thing being claimed, and never quote a real-time factor without naming its
backend. `docs/UNPROVEN.md` is the record; read it before quoting any figure from this repository.

That applies to your own output too. Verify a claim before writing it into a document, and when a
check is not possible from here, say so rather than reasoning to a confident answer.

The other document to read before fighting a strange failure is `docs/GOTCHAS.md` — a catalogue
of measured traps, several about the very harnesses named above.

## Where output goes

Everything under `runs/` is gitignored, and so are transcripts and audio at the repository root.
Nothing a measurement produces belongs in the working tree. Each harness uses its own shape inside
it, named in its header — among them `measure-transcribe.ps1` writes `runs/<timestamp>-<backend>/`,
`measure-second-machine.ps1` writes `runs/<machine>/<backend>/` with a per-machine block beside it,
`measure-wer.ps1` writes `runs/wer/<timestamp>-<backend>/`, `measure-tidy-units.ps1` writes
`runs/tidy-units/<timestamp>-<backend>/` and `…-<backend>-fake/` for its dry run, `measure-der.ps1` writes
`runs/der/<timestamp>-<system>/` beside the cut stretches in `runs/der/stretches/`, and the v2
pair write `runs/<timestamp>-answers-<backend>/` and `runs/<timestamp>-spike-<backend>/`.
`export-translation-onnx.py` is not a harness but writes there for the same reason —
`runs/translation-onnx/<variant>/` with a `manifest.json` beside them, and the graphs run to
gigabytes.

`packaging/` is the second such tree and is gitignored for the same reason: `package-windows.ps1`
writes the publish, the packages and the release feed under it, and one channel alone is over
800 MB. Nothing there is an input to anything — delete it whenever.

`corpus/` is the third and is the opposite kind: gitignored input rather than output. Both WER
harnesses fetch into it — `measure-wer.ps1` and `measure-tidy-units.ps1`, against the pins in
`scripts/wer-corpus.json` — as `corpus/<manifest name>/{media,verbatim,nonverbatim}/`, byte count
and SHA-256 checked against the manifest before anything is scored. It is a cache: deleting it
costs a re-fetch and nothing else, and `-SkipVerify` on either harness trusts what is already
there without re-hashing it.

`scripts/lab.ps1` is one entry point for the scripts; run it bare to list them, each with the
parameters its own script declares. A leading `!` in that listing marks a parameter the
dispatcher does not forward — `measure-tidy-units.ps1 -Fake`, the harness's dry run, is one — so
run that script directly to use it.

Run reports cross machines through the maintainer's Drive, because `runs/` is gitignored and
machine-local: after a measuring session, push the new run summaries to the `runs-<machine>`
folder there — `runs-laptop`, `runs-desktop` — beside the v2 handoff. The route copies each run's
`summary.json`, `summary.md` and any markdown, and nothing else: transcripts and other multi-MB
artifacts stay on the machine, so list them in the folder's README with how to regenerate them,
and for byte-exact fixtures upload a generator validated against the pin rather than a copy. That
README is `runs/README.md` on the machine and travels with the rest; keep it current, including
its note on which working-tree changes are not yet pushed. **No Drive URL or file id goes in this
repository** — it is public; find the folder by name through the Drive connector, and if the
connector is not authorized in your session, say so instead of skipping silently.

**Transfers go through rclone — `scripts/sync-drive.ps1`, or `lab.ps1 drive`.** Not through the
Drive connector: its `create_file` takes content inline only, so uploading anything through it
means emitting the whole file as generated text, and its reader does not handle markdown at all.
The connector is for finding folders and creating them; rclone moves the files. Google Drive for desktop
is deliberately **not** the answer either — the maintainer is not installing a background sync
application on the desktop, and rclone is one binary that behaves identically on both machines.

Every transfer is `--checksum`, which is why rclone earns its place rather than merely working: a
sync tool reports success when it has copied bytes, and this reports success when the bytes agree
at both ends. The script pushes run reports and research, and pulls research and the four test
episodes with their sizes checked against what Drive reports before anything is measured against
them.

Setup is one browser consent per machine, which no script can do for you, and it takes an OAuth
client of your own: `rclone config create gdrive drive scope=drive client_id=<id>
client_secret=<secret>`. Without one, rclone falls back to a shared client id that Google is
retiring — its own warning, on every call, says it "will stop working during 2026" (observed
2026-08-16), so a workflow built on the fallback breaks mid-measurement. **That command prints the
remote it created, refresh token included: its output is a credential.** It does not get pasted
anywhere; if it has been, revoke rclone at `myaccount.google.com/permissions` and run it again. The
resulting `rclone.conf` never comes near this repository.

**Codex memory exports travel only when asked —
`lab.ps1 drive -Memory <machine> -MemorySource <folder>`.** Choose a folder outside this repository
containing the markdown notes selected for this project. The route requires that explicit source;
it does not discover or export Codex's global memory store, which can include unrelated projects.
It copies markdown with `--checksum` to `session-memory/codex/<machine>` and supports `-DryRun`.
It is **push only**: fetch with `-Fetch session-memory/codex/<machine> -Destination <scratch>`
and review the notes before requesting a memory update on the other machine. Do not overwrite
Codex's generated memory files. Earlier exports remain in their existing remote folders.
None of these notes belongs in this public repository.

**Research lives on the Drive, not in this repository — until v1.0 ships, at which point it all
comes back.** The maintainer's standing convention, named 2026-08-16 when the diarisation study
moved out (the v2 research always lived there), with an end date set 2026-08-18: **on the v1.0
release, every research folder and run report moves from the Drive into this repository.** Until
then the rule below is unchanged and a research product still goes to the Drive. Do not start that
migration early, and do not treat the convention as permanent when writing about it.

A research workflow's product — the study, the survey, the report — goes to a dated
folder inside the Drive `uindosill` folder, beside the v2 research and the runs folders. What
stays here is what binds the repository: the decision record in `docs/PHASES.md`, the unproven
markers in `docs/UNPROVEN.md`, and a pointer that, as above, names no URL and no id.

**Research goes up as markdown, and comes down the same way** — `lab.ps1 drive -Research <folder>`
to push, `-Fetch <name>` to pull, and then read the files on disk. Do not read research through the
Drive connector: `text/markdown` is not a type its `read_file_content` handles, so a `.md` comes
back base64-encoded through `download_file_content` and a session burns its time decoding, which is
the whole reason the connector is not the read path. There is no conversion step — a PDF was tried
on 2026-08-16 to work around the connector's list and dropped the same night once rclone made the
connector unnecessary; it lost the tables on the way back and was a second copy waiting to go
stale.
