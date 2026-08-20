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
        Help = "Compute backend to load. Defaults to the fastest this build has: cuda if its "
             + "binaries are installed, else vulkan. Falls back to cpu, and says so when it does.",
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
             + "applied by any catalogue model: only prompt-conditioned checkpoints read it. It translates "
             + "nothing and it is not what --translate reads; see that flag.",
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
                Name = "speakers",
                Help = "Label who is speaking: a second pass over the audio, off by default. Adds 'Speaker 1:' to every " +
                       "format and speaker turns to json and rttm. Needs the diarisation model installed " +
                       "('uindosill models list'); with --fake it uses the canned labeller instead and needs nothing.",
            },
            new OptionSpec
            {
                Name = "speaker-count",
                TakesValue = true,
                ValueName = "n",
                Help = "With --speakers: how many voices there are, when known. Default: let the labeller decide. " +
                       "The diariser estimates the count and cannot be told it, and says so when given one.",
            },
            new OptionSpec
            {
                Name = "speaker-model",
                TakesValue = true,
                ValueName = "id",
                Help = "Catalogue id of the diarisation model. Default: the only diarisation entry there is.",
            },
            new OptionSpec
            {
                Name = "speaker-model-path",
                TakesValue = true,
                ValueName = "file",
                Help = "Use an .onnx diarisation model directly instead of a catalogue entry.",
            },
            new OptionSpec
            {
                Name = "speaker-threads",
                TakesValue = true,
                ValueName = "n",
                Help = "Intra-op threads for the diariser. Default: whatever ONNX Runtime chooses.",
            },
            new OptionSpec
            {
                Name = "translate",
                Help = "Write the transcript in English instead of the language it was spoken in: a pass over the " +
                       "finished text, off by default. Output files take an .en infix (call.en.srt), so a translated " +
                       "run never overwrites a plain one. NOT --language, which is a hint to the speech model about " +
                       "what it is listening to and reaches no translator. Needs the translation model installed " +
                       "('uindosill models list'); with --fake it uses the canned translator and needs nothing.",
            },
            new OptionSpec
            {
                Name = "translate-model",
                TakesValue = true,
                ValueName = "id",
                Help = "Catalogue id of the translation model. Default: the only translation entry there is.",
            },
            new OptionSpec
            {
                Name = "translate-model-path",
                TakesValue = true,
                ValueName = "dir",
                Help = "Use an exported checkpoint directory directly instead of a catalogue entry. A directory, "
                     + "not a file: the route is nine files.",
            },
            new OptionSpec
            {
                Name = "translate-threads",
                TakesValue = true,
                ValueName = "n",
                Help = "Intra-op threads for the translator. Default: whatever ONNX Runtime chooses.",
            },
            new OptionSpec
            {
                Name = "context-segments",
                TakesValue = true,
                ValueName = "n",
                Help = "With --translate: how many preceding segments to hand the translator as context. Default 0, " +
                       "each segment on its own. Nothing here has measured what context buys.",
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
            "across chunk boundaries well before it collapses.\n\n" +
            "--speakers is an opt-in and stays off by default: it reads the file a second time and runs a second model,\n" +
            "and it names voices 'Speaker 1', 'Speaker 2' in order of first appearance — a label, not an identity.\n" +
            "The diariser tells apart at most four speakers; a fifth voice is merged into one of the four, and the\n" +
            "command says so on any file where four were found. To score speaker turns without transcribing, use\n" +
            "'uindosill diarise'.\n\n" +
            "--translate is the other opt-in, and it runs last: decode, then label speakers, then translate. That\n" +
            "order belongs to the code — speakers are attributed word by word and a translated segment has no words,\n" +
            "so translating first would coarsen every label instead of failing where anyone could see it. Word\n" +
            "timings do not survive translation and nothing pretends they do: -f vtt-words is refused under\n" +
            "--translate, and SRT and VTT space each cue across its segment as they already do for any segment the\n" +
            "engine returned no word timings for. There is no source language to choose, and that is a property of\n" +
            "the pass rather than an omission: it is many-to-one into English, so it is told the target and never\n" +
            "asked what it is reading.",
    };

    public static readonly CommandSpec Diarise = new()
    {
        Name = "diarise",
        Summary = "Write speaker turns as RTTM without transcribing.",
        Positionals = "<file> [file...]",
        Options =
        [
            new OptionSpec
            {
                Name = "out",
                Short = 'o',
                TakesValue = true,
                ValueName = "dir",
                Help = "Output directory. Default: beside each input file.",
            },
            new OptionSpec
            {
                Name = "id",
                TakesValue = true,
                ValueName = "name",
                Help = "Name the output <name>.rttm and put <name> in the RTTM's file-id column, instead of deriving " +
                       "both from the input's name. One input file only.",
            },
            new OptionSpec
            {
                Name = "model",
                Short = 'm',
                TakesValue = true,
                ValueName = "id",
                Help = "Catalogue id of the diarisation model. Default: the only diarisation entry there is.",
            },
            new OptionSpec
            {
                Name = "model-path",
                TakesValue = true,
                ValueName = "file",
                Help = "Use an .onnx file directly instead of a catalogue entry.",
            },
            new OptionSpec
            {
                Name = "threads",
                Short = 't',
                TakesValue = true,
                ValueName = "n",
                Help = "Intra-op threads for the ONNX session. Default: whatever ONNX Runtime chooses.",
            },
            new OptionSpec
            {
                Name = "speaker-count",
                TakesValue = true,
                ValueName = "n",
                Help = "How many voices there are, when known. The diariser estimates the count and cannot be told it, " +
                       "so this is reported as ignored rather than applied.",
            },
            Fake,
            Help,
        ],
        Details =
            "Audio in, RTTM out, no transcription — the same labeller behind the same seam as 'transcribe --speakers',\n" +
            "without the ASR pass, which costs orders of magnitude more and contributes nothing to a speaker turn.\n" +
            "This is what the diarisation measurements are run through, and what 'uindosill der' scores.\n\n" +
            "Speakers are labelled spk0..spk3 by the model's own column rather than renamed in order of appearance:\n" +
            "the column is what the speaker cache works to keep meaning the same person for a whole recording, and a\n" +
            "scorer wants to see the labels the model actually produced.\n\n" +
            "'der' pairs hypotheses to references by file stem, which is what --id is for: AMI's audio is\n" +
            "ES2004a.Mix-Headset.wav and its reference is ES2004a.rttm.\n\n" +
            "At most four speakers are told apart. Above that a fifth voice is merged into one of the four, and no\n" +
            "measurement in this repository prices what that costs — see docs/UNPROVEN.md.",
    };

    public static readonly CommandSpec Translate = new()
    {
        Name = "translate",
        Summary = "Translate lines of text into English without transcribing.",
        Positionals = "<file> [file...]",
        Options =
        [
            new OptionSpec
            {
                Name = "out",
                Short = 'o',
                TakesValue = true,
                ValueName = "dir",
                Help = "Output directory. Default: beside each input file.",
            },
            new OptionSpec
            {
                Name = "id",
                TakesValue = true,
                ValueName = "name",
                Help = "Name the output <name>.en.txt instead of deriving it from the input's name. One input file only.",
            },
            new OptionSpec
            {
                Name = "model",
                Short = 'm',
                TakesValue = true,
                ValueName = "id",
                Help = "Catalogue id of the translation model. Default: the only translation entry there is.",
            },
            new OptionSpec
            {
                Name = "model-path",
                TakesValue = true,
                ValueName = "dir",
                Help = "Use an exported checkpoint directory directly instead of a catalogue entry.",
            },
            new OptionSpec
            {
                Name = "threads",
                Short = 't',
                TakesValue = true,
                ValueName = "n",
                Help = "Intra-op threads for the ONNX sessions. Default: whatever ONNX Runtime chooses.",
            },
            Fake,
            Help,
        ],
        Details =
            "Text in, English out, line by line, no audio and no ASR — the same translator behind the same seam as\n" +
            "'transcribe --translate', without the decode that costs orders of magnitude more and contributes\n" +
            "nothing to a translation. This is the path the translation measurements are run through, which is why\n" +
            "it exists at all: a translator that can only be reached through a three-hour transcription is one\n" +
            "nobody checks against a corpus.\n\n" +
            "One line in, one line out, in order, blank lines included — a blank line comes back blank rather than\n" +
            "being dropped, because a file whose line numbers no longer line up is a file nothing can be scored\n" +
            "against. A line past the tokenizer's 512-token limit is refused rather than truncated, and names\n" +
            "itself.\n\n" +
            "Every line is translated on its own at beam 6, which is what every published figure for this model was\n" +
            "produced with. There is no beam or context option here on purpose: they are the degrees of freedom that\n" +
            "would quietly make the output something nobody scored.",
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

    public static readonly CommandSpec Der = new()
    {
        Name = "der",
        Summary = "Score speaker turns against a hand-labelled reference: diarisation error rate, with the convention stated.",
        Positionals = "<hypothesis.rttm> [hypothesis.rttm...]",
        Options =
        [
            new OptionSpec
            {
                Name = "reference",
                Short = 'r',
                TakesValue = true,
                ValueName = "file.rttm",
                Help = "The hand-labelled turns every hypothesis is scored against.",
            },
            new OptionSpec
            {
                Name = "reference-dir",
                TakesValue = true,
                ValueName = "dir",
                Help = "Instead of --reference: a directory holding one <stem>.rttm per hypothesis, matched by file stem.",
            },
            new OptionSpec
            {
                Name = "collar",
                TakesValue = true,
                ValueName = "seconds",
                Help = "No-score zone around every reference boundary, as a total width centred on it (pyannote.metrics " +
                       "semantics: 0.25 forgives 0.125 s either side). Default 0.25. md-eval's and NeMo's \"0.25\" is a " +
                       "half-width, so their number is --collar 0.5 here.",
            },
            new OptionSpec
            {
                Name = "skip-overlap",
                Help = "Leave regions where two or more reference speakers talk at once out of the score. Off by default: " +
                       "crosstalk is the thing being measured.",
            },
            new OptionSpec
            {
                Name = "json",
                Help = "Machine-readable output: per-hypothesis and summed components and rates, at the headline collar, " +
                       "at collar 0, and over reference-overlap regions.",
            },
            Help,
        ],
        Details =
            "Both files are RTTM: 'SPEAKER <file-id> 1 <onset> <duration> <NA> <NA> <speaker> <NA> <NA>' per turn, as\n" +
            "'uindosill rttm' writes from Audacity labels and 'uindosill transcribe --speakers -f rttm' writes from a run.\n" +
            "DER is (missed + false alarm + confusion) / reference speech, over the union of both files' extents with the\n" +
            "collar cut out around every reference boundary, under the one-to-one speaker mapping that maximises\n" +
            "co-occurring speech (found exhaustively — greedy mapping is not DER). Computed the way pyannote.metrics\n" +
            "computes it and validated against it on the fixture pairs in tests/fixtures/diarisation/scorer/.\n\n" +
            "Three numbers come out together and travel together: the headline at the collar given, the strict number at\n" +
            "collar 0, and the same components over reference-overlap regions only — where the target audio is hardest\n" +
            "and where a headline averaged over every second of one person talking says least.",
    };

    public static readonly CommandSpec Rttm = new()
    {
        Name = "rttm",
        Summary = "Convert an Audacity label export to RTTM speaker turns.",
        Positionals = "<labels.txt>",
        Options =
        [
            new OptionSpec
            {
                Name = "file-id",
                TakesValue = true,
                ValueName = "id",
                Help = "The RTTM file id every line carries. Default: the label file's name without its extension.",
            },
            new OptionSpec
            {
                Name = "bridge",
                TakesValue = true,
                ValueName = "seconds",
                Help = "Merge same-speaker labels separated by at most this many seconds. Default 0: only overlapping " +
                       "or touching labels merge. Record the value used with the fixture.",
            },
            new OptionSpec
            {
                Name = "out",
                Short = 'o',
                TakesValue = true,
                ValueName = "file",
                Help = "Write the RTTM here instead of to standard output.",
            },
            Help,
        ],
        Details =
            "Reads Audacity's Export Labels format — 'start<TAB>end<TAB>text', every label track merged into one file —\n" +
            "with the label text as the speaker's name: one track per speaker, each labelled independently, so overlap\n" +
            "falls out on its own. Point labels are dropped, same-speaker overlaps are merged, whitespace in a name becomes\n" +
            "an underscore because RTTM splits on whitespace. A summary of who spoke how much goes to stderr; the RTTM\n" +
            "goes to stdout or --out.",
    };

    public static IReadOnlyList<CommandSpec> All { get; } =
        [Transcribe, Diarise, Translate, Models, Bench, Doctor, Probe, Notice, Formats, Wer, Der, Rttm];
}
