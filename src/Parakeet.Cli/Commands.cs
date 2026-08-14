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
        Help = "Language hint for multilingual models, e.g. en, de, auto.",
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

    public static IReadOnlyList<CommandSpec> All { get; } =
        [Transcribe, Models, Bench, Doctor, Probe, Notice, Formats];
}
