using Parakeet.Core.Formatting;

namespace Parakeet.Cli;

/// <summary>The command surface, declared as data so help and parsing cannot drift apart.</summary>
internal static class Commands
{
    private static readonly OptionSpec Help = new()
    {
        Name = "help",
        Short = 'h',
        Help = "Show this help.",
    };

    private static readonly OptionSpec Model = new()
    {
        Name = "model",
        Short = 'm',
        TakesValue = true,
        ValueName = "id",
        Help = "Catalogue model id. Defaults to the recommended entry.",
    };

    private static readonly OptionSpec ModelPath = new()
    {
        Name = "model-path",
        TakesValue = true,
        ValueName = "file",
        Help = "Use a GGUF file directly instead of a catalogue entry.",
    };

    private static readonly OptionSpec Backend = new()
    {
        Name = "backend",
        TakesValue = true,
        ValueName = "cpu|vulkan|cuda",
        Help = "Compute backend to load. Default vulkan, falling back to cpu.",
    };

    private static readonly OptionSpec NativeDirectory = new()
    {
        Name = "native-dir",
        TakesValue = true,
        ValueName = "dir",
        Help = "Directory holding the parakeet.cpp native library.",
    };

    private static readonly OptionSpec VulkanDisableBFloat16 = new()
    {
        Name = "vk-disable-bf16",
        Help = "Vulkan: disable bf16 kernels before loading. This is the default — it is what lets " +
               "the model load on devices whose driver mishandles bf16 cooperative matrices, and it " +
               "measured at no cost on NVIDIA — so the flag only spells the default out.",
    };

    private static readonly OptionSpec VulkanKeepBFloat16 = new()
    {
        Name = "vk-bf16",
        Help = "Vulkan: leave bf16 kernels enabled, undoing the default workaround. For measuring " +
               "what the workaround costs, or on a driver known to have fixed the bf16 extension " +
               "request. On an affected device the model will not load.",
    };

    private static readonly OptionSpec Fake = new()
    {
        Name = "fake",
        Help = "Use the canned engine: real reading, segmentation and output, no model.",
    };

    private static readonly OptionSpec Language = new()
    {
        Name = "language",
        Short = 'l',
        TakesValue = true,
        ValueName = "tag",
        Help = "Language hint for multilingual models, e.g. en, de, auto. Passed to the ABI but not "
             + "applied by any catalogue model: only prompt-conditioned checkpoints read it.",
    };

    private static readonly OptionSpec Threads = new()
    {
        Name = "threads",
        Short = 't',
        TakesValue = true,
        ValueName = "n",
        Help = "Requested decode threads. Reported but not applied: the ABI takes no thread count.",
    };

    public static readonly CommandSpec Transcribe = new()
    {
        Name = "transcribe",
        Summary = "Transcribe audio or video files to text, subtitles, JSON or Markdown.",
        Positionals = "<file> [file...]",
        Options =
        [
            new OptionSpec
            {
                Name = "format",
                Short = 'f',
                TakesValue = true,
                Repeatable = true,
                ValueName = "id",
                Help = $"Output format(s), comma separated or repeated: {string.Join(", ", TranscriptFormats.Ids)}. Default txt.",
            },
            new OptionSpec
            {
                Name = "out",
                Short = 'o',
                TakesValue = true,
                ValueName = "dir",
                Help = "Output directory. Default: beside each input file.",
            },
            Model,
            ModelPath,
            Backend,
            NativeDirectory,
            VulkanDisableBFloat16,
            VulkanKeepBFloat16,
            Language,
            Threads,
            new OptionSpec
            {
                Name = "max-segment",
                TakesValue = true,
                ValueName = "seconds",
                Help = "Segment length cap in seconds. Default 30. Raising it is a correctness risk.",
            },
            new OptionSpec
            {
                Name = "no-vad",
                Help = "Fixed windows instead of voice-activity segmentation, still cut at the quietest nearby frame.",
            },
            new OptionSpec
            {
                Name = "overwrite",
                Help = "Overwrite existing output files instead of writing 'name (2).srt'.",
            },
            new OptionSpec
            {
                Name = "skip-existing",
                Help = "Leave existing output files alone and write nothing for them.",
            },
            new OptionSpec
            {
                Name = "quiet",
                Short = 'q',
                Help = "Suppress progress; print only results and errors.",
            },
            Fake,
            Help,
        ],
        Details =
            "Files are processed one after another and a failure on one does not stop the rest; the exit code is 3\n" +
            "when some files succeeded and others did not.\n\n" +
            "Every recording is cut into segments of at most 30 seconds before decoding. That is a correctness\n" +
            "requirement rather than a tuning default: Parakeet degrades on long single-pass audio and glues text\n" +
            "across chunk boundaries well before it collapses.",
    };

    public static readonly CommandSpec Models = new()
    {
        Name = "models",
        Summary = "List, download and remove model weights.",
        Positionals = "list|download <id>|remove <id>|path|verify <id>",
        Options =
        [
            new OptionSpec
            {
                Name = "allow-unverified",
                Help = "Install a model whose catalogue entry carries no SHA-256. Off by default.",
            },
            new OptionSpec
            {
                Name = "force",
                Help = "Re-download even when the file is already present.",
            },
            Help,
        ],
        Details =
            "Models live under %LOCALAPPDATA% (or the platform equivalent), never in the install directory, so\n" +
            "they survive updates and uninstalls. Override the location with UINDOSILL_MODELS_DIR.\n\n" +
            "Entries marked 'unverified' have file names, sizes and digests that were never checked against the\n" +
            "live repository. Downloading one requires --allow-unverified, and 'models verify' prints the digest\n" +
            "of what actually arrived so it can be pinned in the catalogue.",
    };

    public static readonly CommandSpec Bench = new()
    {
        Name = "bench",
        Summary = "Measure decode speed on a real file, after warming up.",
        Positionals = "<file>",
        Options =
        [
            Model,
            ModelPath,
            Backend,
            NativeDirectory,
            VulkanDisableBFloat16,
            VulkanKeepBFloat16,
            Language,
            Fake,
            new OptionSpec
            {
                Name = "repeat",
                Short = 'r',
                TakesValue = true,
                ValueName = "n",
                Help = "Timed passes after the warm-up pass. Default 3.",
            },
            new OptionSpec
            {
                Name = "batch",
                TakesValue = true,
                Repeatable = true,
                ValueName = "n",
                Help = "Batch sizes to sweep, comma separated or repeated. Default 4.",
            },
            new OptionSpec
            {
                Name = "no-warmup",
                Help = "Skip the warm-up decode. Produces an inflated first number; for demonstrating that fact.",
            },
            Help,
        ],
        Details =
            "The first decode pays arena allocation and graph construction, so a warm-up pass runs before anything\n" +
            "is timed and cold load is reported as its own number rather than folded in. Peak working set is\n" +
            "reported because it is what decides whether a machine can run the model at all.\n\n" +
            "Thread count is not swept: the parakeet.cpp ABI takes no thread parameter, so a thread sweep would be\n" +
            "measuring nothing.",
    };

    public static readonly CommandSpec Doctor = new()
    {
        Name = "doctor",
        Summary = "Report the environment and probe each compute backend in a child process.",
        Options =
        [
            NativeDirectory,
            Help,
        ],
        Details =
            "Each backend is probed in a separate process on purpose. A native library built with an AVX2 baseline\n" +
            "can execute AVX/BMI2 instructions from a static initialiser and kill the process at load time on a\n" +
            "pre-Haswell CPU — no exception, no stack trace, just 'the app won't launch'. A child process turns\n" +
            "that into an exit code this one can report.",
    };

    public static readonly CommandSpec Probe = new()
    {
        Name = "probe",
        Summary = "Load one backend and print its ABI version. Used by 'doctor'; not meant to be run directly.",
        Options =
        [
            Backend,
            NativeDirectory,
            Help,
        ],
    };

    public static readonly CommandSpec Notice = new()
    {
        Name = "notice",
        Summary = "Print the licence notices for the model weights and third-party code.",
        Options = [Help],
    };

    public static readonly CommandSpec Formats = new()
    {
        Name = "formats",
        Summary = "List the output formats.",
        Options = [Help],
    };

    public static readonly CommandSpec Wer = new()
    {
        Name = "wer",
        Summary = "Score transcripts against a human reference: word error rate, with the normalisation stated.",
        Positionals = "<hypothesis> [hypothesis...]",
        Options =
        [
            new OptionSpec
            {
                Name = "reference",
                Short = 'r',
                TakesValue = true,
                ValueName = "file",
                Help = "The human transcript every hypothesis is scored against: plain text, or an Earnings-22 .nlp file.",
            },
            new OptionSpec
            {
                Name = "reference-dir",
                TakesValue = true,
                ValueName = "dir",
                Help = "Instead of --reference: a directory holding one <stem>.txt or <stem>.nlp per hypothesis, matched by file stem.",
            },
            new OptionSpec
            {
                Name = "reference-format",
                TakesValue = true,
                ValueName = "auto|text|nlp",
                Help = "How to read the reference. Default auto: .nlp by extension, plain text otherwise.",
            },
            new OptionSpec
            {
                Name = "keep-fillers",
                Help = "Count uh, um, hmm, mm, mhm and mmm as words. By default both sides drop them, as the leaderboard normaliser does.",
            },
            new OptionSpec
            {
                Name = "show",
                TakesValue = true,
                ValueName = "n",
                Help = "Print the first n error sites of each hypothesis with three words of context either side.",
            },
            new OptionSpec
            {
                Name = "json",
                Help = "Machine-readable output: per-hypothesis and summed counts and rates.",
            },
            Help,
        ],
        Details =
            "A hypothesis is a transcript this tool wrote: the .json (its \"text\" field) or the .txt (its [hh:mm:ss] prefixes\n" +
            "are stripped). WER is (substitutions + deletions + insertions) / reference words, over tokens normalised the\n" +
            "same way on both sides: lower-cased, punctuation removed, hyphens split, bracketed annotations dropped, fillers\n" +
            "dropped. That is NOT the normaliser the published leaderboards use — numbers, spellings and contractions are\n" +
            "compared as written, and this model spells numbers out — so a figure from here is comparable to another figure\n" +
            "from here and not to a leaderboard. The raw column is the same score over whitespace tokens with nothing\n" +
            "normalised, so the size of the normalisation is visible.",
    };

    public static IReadOnlyList<CommandSpec> All { get; } =
        [Transcribe, Models, Bench, Doctor, Probe, Notice, Formats, Wer];
}
