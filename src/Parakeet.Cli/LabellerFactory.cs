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

    /// <summary>A model directory to load directly instead of a catalogue entry.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Torch intra-op threads, or 0 for the diariser's own default.</summary>
    public int Threads { get; init; }

    /// <summary>The torch device, or null for <c>auto</c>, which is the cpu.</summary>
    public string? Backend { get; init; }

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
/// entry in the catalogue, an entry that is not installed, a path that is neither a file nor a
/// directory — and each wants a message that says which one happened and what to do about it. Two
/// copies of that would become two different messages.
/// </remarks>
internal static class LabellerFactory
{
    /// <summary>
    /// Where <c>scripts/export-diariser-onnx.py</c> puts the derived graphs, relative to the model
    /// directory. Must agree with <c>ONNX_SUBDIR</c> in the sidecar's <c>pyannote_engine.py</c>,
    /// which is the side that actually loads them; this constant exists so the usage error can be
    /// raised before a subprocess starts.
    /// </summary>
    private const string DiariserOnnxSubdirectory = "onnx";

    /// <summary>The two graphs an ONNX provider needs, matching the sidecar's <c>ONNX_FILES</c>.</summary>
    private static readonly string[] DiariserOnnxFiles = ["segmentation.onnx", "embedding.onnx"];

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
    /// It also means the device is known rather than assumed. With the engine out of process it is
    /// resolved inside the sidecar, so the answer has to come back before it can be reported. That
    /// mattered more when the diariser negotiated an ONNX Runtime provider and a provider could move
    /// the labels; it is reported now because it belongs in the transcript's provenance, and because
    /// whether it moves the labels here is exactly the sort of thing nobody has measured.
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
            var provider = ResolveBackend(request);

            // **ONNX Runtime's execution providers name something here as of 2026-08-28, and only
            // when the derived graphs exist.** The pipeline is still torch — the featuriser, the
            // powerset decoding, the PLDA and the VBx clustering all are — but its two neural
            // stages have an ONNX export (`scripts/export-diariser-onnx.py`), and with it a
            // provider has a graph to run. Without it there is still nothing to select, so the
            // refusal stays for that case and says which files are missing rather than what the
            // engine is.
            //
            // Checked here rather than only in the sidecar — which must check too, since nothing
            // stops another host asking — because this costs no subprocess and produces a usage
            // error with a usage exit code, which is what an unbuilt route deserves.
            if (provider is "webgpu" or "dml")
            {
                var graphs = Path.Combine(path, DiariserOnnxSubdirectory);
                var missing = DiariserOnnxFiles
                    .Where(name => !File.Exists(Path.Combine(graphs, name)))
                    .ToArray();
                if (missing.Length > 0)
                {
                    throw new CliUsageException(
                        $"{request.BackendOption} {provider} runs the diariser's two neural stages through ONNX " +
                        $"Runtime, and {graphs} is missing {string.Join(", ", missing)}. Export them with " +
                        "scripts/export-diariser-onnx.py against this model directory, or choose cpu.");
                }
            }

            labeller = new SidecarSpeakerLabeller(new SidecarLabellerOptions
            {
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

        // **Four warnings stood here and went with the diariser on 2026-08-27.** Three quoted AMI
        // figures at a backend — cuda's 16.10% and DirectML's 53.15% against the CPU's 16.33% — and
        // one reported a failed or unrun parity check. Every one of those numbers was measured on
        // the ONNX diariser now in `attic/sortformer/`, and they describe neither this pipeline nor
        // its graphs. There is nothing measured left to warn about, so nothing is said.
        //
        // **A second path exists again as of 2026-08-28**, and it still earns no warning here. The
        // ONNX export agreed with the torch pipeline exactly on the one recording it has been run
        // against — 59 of 59 turns, identical speakers, max |Δ| 0.000 s on both boundaries — but
        // one five-minute clip is not a corpus and no DER has been scored on either route.
        // `docs/UNPROVEN.md` is where that stays visible.
        //
        // **That silence is weaker than the silence it replaces, and the difference matters.** The
        // old code said nothing for cpu and webgpu because both had been *measured* to reproduce the
        // published figure. Nothing here has been measured at all. `docs/UNPROVEN.md` is where that
        // stays visible; a warning on every run would be this project asserting a doubt it has not
        // earned either, and the honest place for an unmeasured engine is the record, not stderr.

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

        // The second, and the one that changes what a user does next. The line above promises a fold
        // down to the count, and for a count above a labeller's cap that promise cannot be kept —
        // there would be nothing to fold, and nothing said would tell the user the number was never
        // on offer. **Dormant since 2026-08-27**, because the diariser that had a cap is in the
        // attic and this one reports none; kept because the promise above is still made and a
        // capped labeller is what would make it false again.
        if (SpeakerLabelling.DescribeUnreachableCount(labeller.Capabilities, options.SpeakerCount) is { } unreachable)
        {
            context.WriteError($"WARNING: {unreachable}");
        }

        return labeller;
    }

    /// <summary>
    /// Which torch device the diariser is allowed to use, from what the user asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A device rather than an execution provider, since 2026-08-27.</b> This was a whitelist
    /// over ONNX Runtime providers, and it was a whitelist rather than a passthrough because of a
    /// measurement: DirectML at ONNX Runtime's default settings scored <b>53.15%</b> DER on AMI test
    /// against the CPU's 16.33%, at 13x the speed, while emitting entirely plausible speaker turns.
    /// That finding belongs to the graph it was taken on, which is in <c>attic/sortformer/</c>.
    /// </para>
    /// <para>
    /// The diariser's pipeline is torch. <c>auto</c> is the CPU — the bundled torch is the CPU
    /// build — and <c>cuda</c> is reachable by name on a machine whose torch has it. ONNX
    /// Runtime's provider names are resolved here and checked above, where the model directory is
    /// known: since 2026-08-28 the two neural stages have an ONNX export, so <c>webgpu</c> and
    /// <c>dml</c> select something when the graphs are present and are refused by name when they
    /// are not.
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
            "auto" or "cpu" or "cuda" => backend,

            // Passed through to the guard in CreateAsync, which is where the sentence explaining
            // that there is no graph for them to select lives. Recognised here rather than falling
            // into the unknown-backend arm below, because "unknown backend 'webgpu'" is a worse
            // answer than naming the graphs the export would have produced. Whether they exist is
            // a property of the install, not of the spelling, so it is decided in CreateAsync.
            "webgpu" => "webgpu",
            "dml" or "directml" => "dml",

            _ => throw new CliUsageException(
                $"Unknown diariser backend '{asked}' for {request.BackendOption}. Choose cpu or cuda for the torch " +
                "pipeline, or webgpu or dml where the exported graphs are installed, or leave it unset for auto, " +
                "which is the cpu."),
        };
    }

    public static (string Path, ModelDescriptor? Descriptor) ResolveModel(CliContext context, LabellerRequest request)
    {
        if (request.ModelPath is { Length: > 0 } explicitPath)
        {
            if (!Exists(explicitPath))
            {
                throw new CliUsageException($"Diarisation model directory not found: {explicitPath}");
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
            // **Resolved over what is installed, not over the catalogue**, and that is worth keeping
            // now that one entry labels speakers rather than two. Counting catalogue entries would
            // make `--speakers` start failing on every machine the moment a second entry is added,
            // including machines with only one installed — which is exactly the trap this avoided
            // when the second entry arrived, and would spring again on a third.
            //
            // The `> 1` arm below is therefore unreachable against today's catalogue and is kept
            // deliberately: it is a guard over what a machine has, not over what this build ships.
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
    /// A bare <see cref="File.Exists(string)"/> was right while every diariser was one <c>.onnx</c>
    /// file. The diariser installs a directory now — five files, in subdirectories — and against one
    /// of those the file check answers "not installed" about weights that are sitting on the disk.
    /// Both are still accepted because the ASR and translation entries are single files.
    /// </remarks>
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
