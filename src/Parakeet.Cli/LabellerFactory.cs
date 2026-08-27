using Parakeet.Core.Diarisation;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;
using Parakeet.Engine.Python;

namespace Parakeet.Cli;

internal sealed record LabellerRequest
{
    /// <summary>Use the canned labeller: real reading, canned speakers, no model.</summary>
    public bool Fake { get; init; }

    /// <summary>Catalogue id of a diarisation entry, or null for the only one installed.</summary>
    public string? ModelId { get; init; }

    /// <summary>An <c>.onnx</c> file to load directly instead of a catalogue entry.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Intra-op threads for the ONNX session, or 0 to let ONNX Runtime choose.</summary>
    public int Threads { get; init; }

    /// <summary>
    /// Execution provider, or null for <c>auto</c> — the fastest one whose parity with the CPU has
    /// been measured, resolved inside the sidecar because only ONNX Runtime knows what will load.
    /// </summary>
    public string? Backend { get; init; }

    /// <summary>Allow a backend that has not passed parity on this machine.</summary>
    public bool AllowUnverifiedBackend { get; init; }

    /// <summary>
    /// The flag the calling command spells the backend with — <c>transcribe</c> has
    /// <c>--speaker-backend</c> and <c>diarise</c> has <c>--backend</c>.
    /// </summary>
    /// <remarks>
    /// Carried rather than hardcoded for the same reason <c>ParseThreads</c> takes one: a shared
    /// message that names one command's flag tells half its readers to fix a flag their command
    /// does not have.
    /// </remarks>
    public string BackendOption { get; init; } = "--speaker-backend";
}

/// <summary>
/// Resolves the speaker labeller, for the two commands that need one.
/// </summary>
/// <remarks>
/// Shared rather than duplicated because the resolution has three ways to fail — no diarisation
/// entry in the catalogue, an entry that is not installed, a path that is not a file — and each
/// wants a message that says which one happened and what to do about it. Two copies of that would
/// become two different messages.
/// </remarks>
internal static class LabellerFactory
{
    /// <summary>
    /// Builds the labeller and loads it, so that its capabilities are real before anything is said
    /// about them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Loading here rather than lazily is what lets the two warnings below fire before a byte of
    /// audio is read, which is the whole point of them — "seven speakers was never reachable" is
    /// worth saying up front and worthless after a three-hour decode. It is the same reason this
    /// class resolves the model before the ASR engine is built.
    /// </para>
    /// <para>
    /// It also means the backend is known rather than assumed. With the engine out of process the
    /// provider is chosen inside the sidecar — only ONNX Runtime knows what will actually
    /// initialise — so the answer has to come back before it can be reported, and a provider
    /// changes the speaker labels rather than only their speed.
    /// </para>
    /// </remarks>
    public static async Task<ISpeakerLabeller> CreateAsync(
        CliContext context,
        LabellerRequest request,
        SpeakerLabellingOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        ISpeakerLabeller labeller;
        if (request.Fake)
        {
            context.WriteError(
                "Using the canned speaker labeller: the second pass over the audio is real, the speakers are not.");
            labeller = new FakeSpeakerLabeller();
        }
        else
        {
            var (path, descriptor) = ResolveModel(context, request);

            // A catalogue entry states its engine; a path given with --speaker-model-path has no
            // entry to state one, and a directory there can only be the one engine that installs a
            // directory.
            var kind = descriptor?.Engine is { Length: > 0 } engine
                ? engine
                : Directory.Exists(path) ? DiariserKinds.Pyannote : DiariserKinds.Sortformer;
            var provider = ResolveBackend(request);

            // `torch` names the second diariser's own runtime and the first has none. The sidecar
            // refuses it too — it must, since nothing stops a host asking — but refusing here costs
            // no subprocess and produces a usage error with a usage exit code, which is what a
            // mistyped flag deserves.
            if (string.Equals(provider, "torch", StringComparison.Ordinal)
                && !string.Equals(kind, DiariserKinds.Pyannote, StringComparison.Ordinal))
            {
                throw new CliUsageException(
                    $"{request.BackendOption} torch names the second diariser's runtime, and this model is an ONNX " +
                    $"graph with no torch path. Choose cpu, cuda or webgpu, or select a model whose engine is " +
                    $"'{DiariserKinds.Pyannote}'.");
            }

            // **The other half of the same rule**, which was missing until an adversarial review
            // found the guard above was one-sided. `webgpu` and `dml` are ONNX Runtime execution
            // providers and the second diariser is torch on both stages with no ONNX route, so it
            // refuses them — correctly, since silently giving somebody the CPU tells them nothing.
            // Refused here for the same reason as above: no subprocess, and a usage exit code.
            if (string.Equals(kind, DiariserKinds.Pyannote, StringComparison.Ordinal)
                && provider is "webgpu" or "dml")
            {
                throw new CliUsageException(
                    $"{request.BackendOption} {provider} names an ONNX Runtime execution provider, and this model is " +
                    "a torch pipeline with no ONNX route for one to select. Choose cpu or cuda, or select the " +
                    $"'{DiariserKinds.Sortformer}' model, which is an ONNX graph.");
            }

            labeller = new SidecarSpeakerLabeller(new SidecarLabellerOptions
            {
                Kind = kind,
                ModelPath = path,
                ModelId = descriptor?.Id ?? Path.GetFileNameWithoutExtension(path),
                IntraOpThreads = request.Threads,
                Provider = provider,
            });
        }

        // Disposed on a failed load rather than leaked: the sidecar is a process, and a batch that
        // fails to load its model once per file would otherwise leave one running each time.
        try
        {
            await labeller.LoadAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await labeller.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // Which provider ran changes the speaker labels and not only the clock, so a backend that
        // moves the published figure says so. Measured on AMI test 2026-08-21: cpu 16.3324%,
        // webgpu 16.3319%, cuda 16.1021%, DirectML at its own defaults 53.15%.
        //
        // Nothing is said for cpu or webgpu, and that silence is a measurement rather than an
        // oversight: webgpu lands 0.0005 points from the CPU, which is closer than this project's
        // own C#-against-Python port managed, so the published figure describes it. Warning on
        // every run about a backend that agrees would train people to ignore the line that matters.
        // The finding itself lives in Parakeet.Core, where the window reads the same one; what this
        // side adds is the remedy, which is a flag the window does not have.
        // **Those figures are Sortformer's, and until this session no other diariser could reach
        // this line.** The second diariser reported `cpu` unconditionally; now that its embedder
        // negotiates a provider it can report cuda or dml too, and the sentences below would then
        // assert one model's AMI numbers about another. So the AMI-figure warnings are scoped to
        // the diariser they were measured on, and the second one gets its own sentence further
        // down — which does not quote a DER, because it does not have one.
        // Read off the loaded engine's own name rather than the requested kind: it is what actually
        // answered, and a labeller that reports its own identity cannot disagree with itself.
        //
        // **Matched positively, on the engine that owns the figures.** This tested `!StartsWith
        // ("diarizen")` until 2026-08-27, which named the *other* engine — so when the second
        // diariser was replaced and began reporting `pyannote-torch-python`, the negation silently
        // flipped to true and these sentences would have quoted Sortformer's AMI numbers about a
        // model that has none. Naming the engine whose measurements these are cannot fail that way:
        // a third diariser is excluded by default rather than included by default.
        var quotesSortformerFigures =
            labeller.Capabilities.EngineName.StartsWith("sortformer", StringComparison.Ordinal);
        if (quotesSortformerFigures
            && SpeakerLabelling.DescribeBackend(labeller.Capabilities.Backend) is { } finding)
        {
            context.WriteError(labeller.Capabilities.Backend == ComputeBackend.Cuda
                ? finding + $" {request.BackendOption} webgpu is the one that transfers."
                : finding);
        }

        // The second diariser's embedder, which the line above cannot see: torch and ONNX Runtime's
        // CPU provider both report `cpu`, and only one of them is the path the published figures
        // describe. Reached only by naming a provider — `auto` is torch — so this fires on a
        // deliberate choice and names the flag that undoes it.
        if (SpeakerLabelling.DescribeEmbeddingBackend(labeller.Capabilities.EmbeddingBackend) is { } embedder)
        {
            context.WriteError(embedder + $" {request.BackendOption} torch is the published path.");
        }

        // What the machine itself found, which outranks anything measured elsewhere. A backend can
        // be faithful on the hardware it was measured on and not on this one — DirectML's defect is
        // driver-mediated — so the reference figures above are a prior and this is the evidence.
        if (labeller is SidecarSpeakerLabeller sidecar)
        {
            // `auto` tried something better first and it did not build. Said once, here, because
            // the reason explains the run — a diarisation at CPU speed on a machine with a GPU is
            // a question until somebody reads why — and until 2026-08-22 the sidecar kept these
            // reasons only for the case where every candidate failed.
            if (sidecar.FellBackFrom.Count > 0)
            {
                context.WriteError(
                    $"{request.BackendOption} auto passed over {string.Join("; ", sidecar.FellBackFrom)} — this run " +
                    $"is on {labeller.Capabilities.Backend.ToString().ToLowerInvariant()}.");
            }

            // Three failing shapes and one sentence per shape, from the result itself: a magnitude
            // past the tolerance, a reason the sidecar gave instead of one, and a check that could
            // not run — which until 2026-08-22 was reported as nothing at all.
            // **The remedy differs by diariser, because "the reference path" is a different flag for
            // each.** For Sortformer the reference IS ONNX Runtime's CPU provider, so `cpu` is the
            // answer by definition. For the second diariser the reference is its torch embedder, and
            // `cpu` names ONNX Runtime's CPU provider there too — the very kind of path under
            // suspicion when a parity check fails. Pointing a user at it at the moment a fault is
            // detected would send them off the reference rather than onto it.
            if (sidecar.Parity?.Describe() is { } parityLine)
            {
                context.WriteError(parityLine
                    + (quotesSortformerFigures
                        ? $" {request.BackendOption} cpu is the one that does."
                        : $" {request.BackendOption} torch is the one that does."));
            }
        }

        // The seam's capabilities are the caller's to honour, and there are two separate things a
        // caller can be owed here. Both are said, because they are different facts and only one of
        // them was being said.
        if (options.SpeakerCount is { } count && !labeller.Capabilities.SupportsFixedSpeakerCount)
        {
            context.WriteError(
                $"--speaker-count {count}: this labeller estimates the count itself and cannot be told one, so its " +
                $"labels are folded down to {count} afterwards, merging the pair that talk over each other least. " +
                "If it finds that many or fewer, nothing is merged.");
        }

        // The second, and the one that changes what a user does next. The line above now promises a
        // fold down to the count, and for a count ABOVE the cap that promise cannot be kept: the
        // labeller never produces more than four labels, so there is nothing to fold and nothing
        // said would tell the user that seven was never on offer. This says it, before a byte of
        // audio is read — because after the run the only thing left to say is DescribeLimit's
        // "4 speakers were labelled", which reads as a fact about the recording rather than the tool.
        if (SpeakerLabelling.DescribeUnreachableCount(labeller.Capabilities, options.SpeakerCount) is { } unreachable)
        {
            context.WriteError($"WARNING: {unreachable}");
        }

        return labeller;
    }

    /// <summary>
    /// Which execution provider the diariser is allowed to use, from what the user asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list is a whitelist rather than a passthrough, and the reason is a measurement. On
    /// 2026-08-21 DirectML at ONNX Runtime's default settings scored <b>53.15%</b> DER on AMI test
    /// against the CPU's 16.33%, at 13x the speed, while emitting entirely plausible speaker turns
    /// — nothing about the output says it is wrong. A provider that can fail that way is not one to
    /// accept because somebody typed its name.
    /// </para>
    /// <para>
    /// <c>webgpu</c> is allowed because it measured faithful at every optimisation level on this
    /// project's own hardware (2.7e-06, no decision flips); <c>dml</c> is not, and needs
    /// <c>--speaker-backend-unverified</c> so that measuring it stays possible while using it by
    /// accident does not.
    /// </para>
    /// </remarks>
    private static string ResolveBackend(LabellerRequest request)
    {
        if (request.Backend is not { Length: > 0 } asked)
        {
            return "auto";
        }

        var backend = asked.Trim().ToLowerInvariant();
        return backend switch
        {
            // `torch` names the second diariser's own runtime rather than an execution provider,
            // because that diariser has one: its embedder can run in torch or on ONNX Runtime, and
            // torch is the path its figures describe. It is what `auto` resolves to there, so
            // naming it is a way to be explicit rather than a way to change anything. The first
            // diariser has no torch path and refuses it at load with that as the reason.
            "auto" or "cpu" or "cuda" or "webgpu" or "torch" => backend,
            "dml" or "directml" when request.AllowUnverifiedBackend => "dml",
            "dml" or "directml" => throw new CliUsageException(
                $"{request.BackendOption} dml is refused. At ONNX Runtime's default settings DirectML scores " +
                "53.15% diarisation error on AMI test against the CPU's 16.33%, at 13x the speed and with speaker " +
                "turns that look entirely normal, so a run that used it by accident would be indistinguishable " +
                $"from a good one. Add {request.BackendOption}-unverified to measure it anyway, or use " +
                $"{request.BackendOption} webgpu, which measured faithful at every optimisation level and scores " +
                "16.33% — the CPU's own figure."),
            _ => throw new CliUsageException(
                $"Unknown diariser backend '{asked}' for {request.BackendOption}. Choose cpu, cuda, webgpu, dml, " +
                "or torch for the second diariser's own runtime."),
        };
    }

    public static (string Path, ModelDescriptor? Descriptor) ResolveModel(CliContext context, LabellerRequest request)
    {
        if (request.ModelPath is { Length: > 0 } explicitPath)
        {
            if (!Exists(explicitPath))
            {
                throw new CliUsageException($"Diarisation model file not found: {explicitPath}");
            }

            return (explicitPath, null);
        }

        ModelDescriptor descriptor;
        if (request.ModelId is { Length: > 0 } id)
        {
            if (!context.Catalog.TryGet(id, out var found))
            {
                throw new CliUsageException(
                    $"Unknown model '{id}'. Run 'uindosill models list' to see the catalogue.");
            }

            if (found.Task != ModelTask.Diarisation)
            {
                throw new CliUsageException(
                    $"'{found.Id}' is a {found.Task.ToString().ToLowerInvariant()} model, not a diarisation model; " +
                    "it cannot label speakers. Run 'uindosill models list' to see which entries do.");
            }

            descriptor = found;
        }
        else
        {
            // **Resolved over what is installed, not over the catalogue.** Two entries label
            // speakers and neither is a default: one is bundled and capped at four voices, the
            // other is a download with no cap, gated on its authors' user agreement and far
            // slower. **Non-commercially licensed until 2026-08-27, and no longer** — that clause
            // described DiariZen, and the pyannote pipeline that replaced it is CC BY 4.0, which
            // is the whole reason the swap was worth making. Choosing
            // between them on the user's behalf is the window's job, where the choice is
            // remembered and visible; here, the one that is present wins, and a machine with both
            // is asked. Counting catalogue entries instead would have made `--speakers` start
            // failing on every machine the moment a second entry was added, including the ones
            // with only the original installed.
            var installed = context.Catalog.DiarisationModels
                .Where(m => Exists(context.Store.PathFor(m)))
                .ToList();

            descriptor = installed.Count switch
            {
                1 => installed[0],
                > 1 => throw new CliUsageException(
                    "More than one diarisation model is installed; name one with --speaker-model. Installed: " +
                    string.Join(", ", installed.Select(m => m.Id))),
                _ when context.Catalog.DiarisationModels.Count == 0 =>
                    throw new CliUsageException("The model catalogue has no diarisation model."),
                _ => throw new CliUsageException(
                    "No diarisation model is installed. Run 'uindosill models download <id>' first. Candidates: " +
                    string.Join(", ", context.Catalog.DiarisationModels.Select(m => m.Id))),
            };
        }

        var path = context.Store.PathFor(descriptor);
        if (!Exists(path))
        {
            throw new CliUsageException(
                $"The diarisation model '{descriptor.Id}' is not installed. Run " +
                $"'uindosill models download {descriptor.Id}' first (it would be at {path}).");
        }

        return (path, descriptor);
    }

    /// <summary>
    /// Whether a resolved model path is there — as a file or as a directory.
    /// </summary>
    /// <remarks>
    /// A bare <see cref="File.Exists(string)"/> was right while every diariser was one
    /// <c>.onnx</c>. The second diariser installs a directory — five files, in subdirectories since
    /// the pyannote pipeline replaced DiariZen on 2026-08-27 — and against one of those the file
    /// check answers "not installed" about weights that are sitting on the disk.
    /// </remarks>
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
