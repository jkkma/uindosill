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
dotnet test  Uindosill.slnx -c Release   # 299 tests, no weights, no display, no network
pwsh                                      # parses scripts/*.ps1; runs compare-transcripts.ps1
python3 scripts/check-test-counts.py     # the counts above, against the run that just happened
```

That last line is why the number in the comment can be trusted, and CI runs it too. **If you change
the test count, run it** — it prints what every document should say, and the three that quote a
count are the three you would otherwise forget.

**A session here can compile and run the tests.** Do not assume otherwise and hand the maintainer
unverified code — an earlier handoff said the sandbox had no SDK, and acting on that would have
shipped a red build.

What a container still cannot do is transcribe anything real: that needs the Windows natives and
a model, neither of which is in the clone. `--fake` exercises the whole pipeline without them.

## The rule this project runs on

Every claim is either measured or explicitly marked unproven. When reporting a number, make sure
it measures the thing being claimed, and never quote a real-time factor without naming its
backend. `docs/UNPROVEN.md` is the record; read it before quoting any figure from this repository.

That applies to your own output too. Verify a claim before writing it into a document, and when a
check is not possible from here, say so rather than reasoning to a confident answer.

## Where output goes

Everything under `runs/` is gitignored, and so are transcripts and audio at the repository root.
Nothing a measurement produces belongs in the working tree. The two harnesses use different shapes
inside it: `measure-transcribe.ps1` writes `runs/<timestamp>-<backend>/`, and
`measure-second-machine.ps1` writes `runs/<machine>/<backend>/` with a per-machine block beside it.

`scripts/lab.ps1` is one entry point for the eight scripts; run it bare to list them.

Run reports cross machines through the maintainer's Drive, because `runs/` is gitignored and
machine-local: after a measuring session, upload the new run summaries (and the JSONs, when they
carry more than the summary) to the `runs-<machine>` folder there — `runs-laptop`,
`runs-desktop` — beside the v2 handoff, and keep that folder's README index current, including its
note on which working-tree changes are not yet pushed. Multi-MB artifacts do not fit the
connector; list them in that README with how to regenerate them instead, and for byte-exact
fixtures upload a generator validated against the pin rather than a copy. **No Drive URL or file
id goes in this repository** — it is public; find the folder by name through the Drive connector,
and if the connector is not authorized in your session, say so instead of skipping silently.
