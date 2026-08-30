---
name: new-engine
description: Scaffold a new Parakeet.Engine.<Name> project pair - the src project, its xUnit test project, slnx registration, and the follow-through the working agreement requires (gated-test env var, test-count check, doc lines).
disable-model-invocation: true
---

# Scaffold a Parakeet.Engine.* project pair

The template is not this file — it is the newest existing engine. **Read one before writing
anything** (`src/Parakeet.Engine.SileroVad/` and `src/Parakeet.Engine.LlamaServer/` at the time
of writing) so the scaffold matches what the repository actually does now, not what this file
remembered. What follows is the shape and the steps that get forgotten.

## 1. The src project

`src/Parakeet.Engine.<Name>/Parakeet.Engine.<Name>.csproj`:

- `net10.0`, explicit `RootNamespace`, `IsPackable=false`. Everything else (nullable, warnings
  as errors, analyzers) comes from the root `Directory.Build.props` — do not restate it.
- **One project owns one model's interop.** The csproj carries a comment block saying what this
  project owns and why it exists as its own project — SileroVad, LlamaServer and Python carry
  one at the time of writing; match their register.
- `ProjectReference` to `Parakeet.Core` (the interface it implements lives there), plus
  `Parakeet.Audio` only if it genuinely needs samples at a rate it must make for itself.
- **NuGet dependencies stay in the engine.** `Parakeet.Core` has a build target that fails if a
  package reference ever appears there. `PackageReference` entries are versionless — versions
  are central in `Directory.Packages.props`, and a new package gets its entry there *with a
  comment saying why that version*, matching the file's existing style.
- `InternalsVisibleTo` for `Parakeet.Engine.<Name>.Tests`.

## 2. The test project

`tests/Parakeet.Engine.<Name>.Tests/` — the csproj is deliberately minimal: a single
`ProjectReference` to the src project. `tests/Directory.Build.props` supplies the TFM, xunit.v3,
the shared `tests/Shared` sources, and the leak checks; a test project never restates them.

**Every test must run on Linux CI with no weights, no display, no network.** A test that needs
a real model, binary drop, or corpus skips itself unless a `UINDOSILL_*` environment variable
names the asset — follow the existing gated-test pattern (`UINDOSILL_SILERO_VAD`,
`UINDOSILL_LLM_SERVER_ROOT`) exactly: skip with a message naming the variable.

## 3. Registration

Add both projects to `Uindosill.slnx` — src under the `/src/` folder, tests under `/tests/`,
each list kept alphabetical.

## 4. Wiring

An engine that exists but is never constructed is scaffolding, not an engine. Find where the
existing engines are selected and constructed (grep the CLI and App for an existing engine's
type name) and wire the new one the same way — do not trust any wiring description in this
file, it will go stale.

## 5. The follow-through the agreement binds

- `dotnet build Uindosill.slnx -c Release` — zero warnings, warnings are errors.
- `dotnet test Uindosill.slnx -c Release`, then **`python3 scripts/check-test-counts.py`** —
  the count changed, three documents quote it, and the script prints what each must now say.
- New gated tests mean two additions to **CLAUDE.md's "Building and testing" section**: the
  env-var invocation, and a "run them after any change to ..." line. Add the matching path rule
  to `.claude/hooks/gated-test-reminder.sh` so the reminder fires for the new engine too.
- Check whether `docs/ARCHITECTURE.md` or `docs/ENGINE-CHOICE.md` needs a line — read them,
  do not assume either way.
- Any performance figure the new engine produces follows the rule: measured with a named
  backend, or into `docs/UNPROVEN.md`.
