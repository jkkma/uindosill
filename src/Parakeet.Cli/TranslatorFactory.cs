using Parakeet.Core.Formatting;
using Parakeet.Core.Translation;

namespace Parakeet.Cli;

/// <summary>
/// Resolves the translator behind <c>--translate</c>.
/// </summary>
/// <remarks>
/// There is nothing to resolve yet, and this says so rather than failing somewhere further in. The
/// contract, the canned translator and the flag are the seam; the model behind it is a catalogue
/// schema change and a decode loop away (docs/PHASES.md § <i>Decided 2026-08-19</i>), and no
/// translation entry exists in <c>models.json</c> to point at — every entry there is one file and
/// the decided route is five. The shape of the refusal is the speaker opt-in's, from when that seam
/// shipped ahead of its diariser: name what is missing, name what does work today, and stop before
/// any transcription runs.
/// </remarks>
internal static class TranslatorFactory
{
    public static ITranscriptTranslator Create(CliContext context, bool fake)
    {
        ArgumentNullException.ThrowIfNull(context);

        Resolve(context, fake);
        context.WriteError("Using the canned translator: the pass is real, the English is not.");
        return new FakeTranscriptTranslator();
    }

    /// <summary>
    /// Checks that a translator can be had, without building one — so <c>transcribe</c> refuses
    /// before it loads 1.34 GiB of ASR weights and decodes a file whose translation it cannot write.
    /// </summary>
    public static void Resolve(CliContext context, bool fake)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (fake)
        {
            return;
        }

        throw new CliUsageException(
            "This build has no translation model. --translate is decided and its model is not integrated yet " +
            "(docs/PHASES.md), so the only translator here is the canned one: add --fake to run the pass with it " +
            "and see the shape of the output.");
    }

    /// <summary>
    /// What a caller must be told before a pass runs, given what the translator turned out to be.
    /// </summary>
    /// <remarks>
    /// The capabilities are the translator's to declare and the caller's to honour, the way
    /// <c>LabellerFactory</c> says out loud that a speaker count was ignored rather than dropping
    /// it. Two of them bind here. A translator that needs to be told the source language cannot be
    /// driven at all, because nothing in this pipeline detects one. And one that does not preserve
    /// word timings — which is every translator this product can ship — makes the word-timed
    /// subtitle format meaningless, so that format is refused rather than written against times
    /// that no longer belong to the words.
    /// </remarks>
    public static void Check(ITranscriptTranslator translator, IReadOnlyList<string> formats)
    {
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(formats);

        var capabilities = translator.Capabilities;

        if (capabilities.RequiresSourceLanguage)
        {
            throw new CliUsageException(
                $"The translator '{capabilities.EngineName}' has to be told which language it is reading, and " +
                "nothing here detects one: --language is a hint for the ASR model and is inert on this checkpoint. " +
                "Only a many-to-one translator can run this pass.");
        }

        if (!capabilities.PreservesWordTimings
            && formats.Contains(TranscriptFormats.WordTimedVtt.Id, StringComparer.OrdinalIgnoreCase))
        {
            throw new CliUsageException(
                $"-f {TranscriptFormats.WordTimedVtt.Id} times every word, and translation does not carry word " +
                "timings: the English words are not the words that were spoken and nothing aligns them. It is " +
                "refused rather than written with the old timings on the new text. Drop the format, or drop " +
                "--translate and get the word timings of what was actually said.");
        }
    }
}
