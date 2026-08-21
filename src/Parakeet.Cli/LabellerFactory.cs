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
            labeller = new SidecarSpeakerLabeller(new SidecarLabellerOptions
            {
                ModelPath = path,
                ModelId = descriptor?.Id ?? Path.GetFileNameWithoutExtension(path),
                IntraOpThreads = request.Threads,
                Provider = ResolveBackend(request),
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
        // moves the published figure says so. Measured on AMI test 2026-08-21: cpu 16.3347%,
        // webgpu 16.3319%, cuda 16.1021%, DirectML at its own defaults 53.15%.
        //
        // Nothing is said for cpu or webgpu, and that silence is a measurement rather than an
        // oversight: webgpu lands 0.0028 points from the CPU, which is closer than this project's
        // own C#-against-Python port managed, so the published figure describes it. Warning on
        // every run about a backend that agrees would train people to ignore the line that matters.
        var warning = labeller.Capabilities.Backend switch
        {
            ComputeBackend.Cuda =>
                "Speaker labelling on cuda. It does not reproduce the CPU's probabilities — on AMI test it " +
                "scores 16.10% against the CPU's 16.33%, and two CUDA runs on different driver and library " +
                "versions have differed by 0.40 points — so these labels are this machine's result rather " +
                "than the published one. Use webgpu for a figure that transfers.",
            ComputeBackend.DirectMl =>
                "WARNING: speaker labelling on DirectML, which has not passed parity here. At ONNX Runtime's " +
                "default settings it scores 53.15% diarisation error against the CPU's 16.33% while producing " +
                "speaker turns that look entirely normal. Treat these labels as unverified.",
            _ => null,
        };

        if (warning is { } line)
        {
            context.WriteError(line);
        }

        // What the machine itself found, which outranks anything measured elsewhere. A backend can
        // be faithful on the hardware it was measured on and not on this one — DirectML's defect is
        // driver-mediated — so the reference figures above are a prior and this is the evidence.
        if (labeller is SidecarSpeakerLabeller { Parity: { } parity } && !parity.Passed)
        {
            context.WriteError(
                $"WARNING: this machine's diariser does not reproduce the reference. Its probabilities differ by " +
                $"up to {parity.MaxAbsoluteDifference:0.###e+00} against a tolerance of {parity.Tolerance:0.###e+00}. " +
                "The speaker labels below are this machine's own result and no diarisation error rate published by " +
                $"this project describes them. {request.BackendOption} cpu is the one that does.");
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
            "auto" or "cpu" or "cuda" or "webgpu" => backend,
            "dml" or "directml" when request.AllowUnverifiedBackend => "dml",
            "dml" or "directml" => throw new CliUsageException(
                $"{request.BackendOption} dml is refused. At ONNX Runtime's default settings DirectML scores " +
                "53.15% diarisation error on AMI test against the CPU's 16.33%, at 13x the speed and with speaker " +
                "turns that look entirely normal, so a run that used it by accident would be indistinguishable " +
                $"from a good one. Add {request.BackendOption}-unverified to measure it anyway, or use " +
                $"{request.BackendOption} webgpu, which measured faithful at every optimisation level and scores " +
                "16.33% — the CPU's own figure."),
            _ => throw new CliUsageException(
                $"Unknown diariser backend '{asked}' for {request.BackendOption}. Choose cpu, cuda, webgpu or dml."),
        };
    }

    public static (string Path, ModelDescriptor? Descriptor) ResolveModel(CliContext context, LabellerRequest request)
    {
        if (request.ModelPath is { Length: > 0 } explicitPath)
        {
            if (!File.Exists(explicitPath))
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
            descriptor = context.Catalog.DiarisationModels.Count switch
            {
                0 => throw new CliUsageException("The model catalogue has no diarisation model."),
                1 => context.Catalog.DiarisationModels[0],
                _ => throw new CliUsageException(
                    "The catalogue has more than one diarisation model; name one. Candidates: " +
                    string.Join(", ", context.Catalog.DiarisationModels.Select(m => m.Id))),
            };
        }

        var path = context.Store.PathFor(descriptor);
        if (!File.Exists(path))
        {
            throw new CliUsageException(
                $"The diarisation model '{descriptor.Id}' is not installed. Run " +
                $"'uindosill models download {descriptor.Id}' first (it would be at {path}).");
        }

        return (path, descriptor);
    }
}
