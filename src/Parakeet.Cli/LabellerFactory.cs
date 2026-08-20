using Parakeet.Core.Diarisation;
using Parakeet.Core.Models;
using Parakeet.Engine.Sortformer;

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
    public static ISpeakerLabeller Create(CliContext context, LabellerRequest request, SpeakerLabellingOptions options)
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
            labeller = new SortformerSpeakerLabeller(new SortformerLabellerOptions
            {
                ModelPath = path,
                ModelId = descriptor?.Id ?? Path.GetFileNameWithoutExtension(path),
                IntraOpThreads = request.Threads,
            });
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
