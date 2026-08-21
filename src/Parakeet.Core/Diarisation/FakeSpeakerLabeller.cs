using System.Globalization;
using Parakeet.Core.Audio;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Diarisation;

public sealed record FakeSpeakerLabellerOptions
{
    public static FakeSpeakerLabellerOptions Default { get; } = new();

    /// <summary>How many voices the fake pretends to hear. They take turns in order.</summary>
    public int SpeakerCount { get; init; } = 2;

    /// <summary>How long each voice speaks before the next one starts.</summary>
    public TimeSpan TurnLength { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// How far each turn runs past the start of the next, so a test can put crosstalk in front of
    /// the cue builder and the scorer without any audio that contains it. Zero by default.
    /// </summary>
    public TimeSpan Overlap { get; init; } = TimeSpan.Zero;

    /// <summary>Throw from <see cref="ISpeakerLabeller.LoadAsync"/> instead of loading.</summary>
    public bool FailOnLoad { get; init; }

    /// <summary>
    /// The provider this labeller claims to have run on. Cpu by default, which is what the fake has
    /// always reported. It is settable so a test can prove the transcript's speaker provenance is
    /// read off the loaded labeller rather than defaulted into the document — a distinction no
    /// assertion can make while every labeller in the suite says the same word.
    /// </summary>
    public ComputeBackend Backend { get; init; } = ComputeBackend.Cpu;

    /// <summary>
    /// Whether the fake lets <see cref="SpeakerLabellingOptions.SpeakerCount"/> reach it, which is
    /// the capability it then advertises. True by default, as it has always behaved.
    /// </summary>
    /// <remarks>
    /// False is the shape that matters, because it is the shipping labeller's: Sortformer estimates
    /// the count and cannot be told one, so a caller's count is honoured afterwards by
    /// <see cref="SpeakerTurns.FoldDownTo"/> rather than by the model. With this false the fake
    /// keeps emitting <see cref="SpeakerCount"/> voices whatever it is asked for, which is what
    /// gives the fold something to fold and what makes that repair testable without 453 MiB of
    /// weights in CI.
    /// </remarks>
    public bool SupportsFixedSpeakerCount { get; init; } = true;

    /// <summary>
    /// The cap the fake advertises, or null for none — <see cref="SpeakerLabelling.DescribeLimit"/>
    /// and <see cref="SpeakerLabelling.DescribeUnreachableCount"/> are both silent without one, so a
    /// test of either has to say what the ceiling is.
    /// </summary>
    public int? MaxSpeakers { get; init; }

    /// <summary>
    /// The length the fake advertises its labels as established to, or null for no such bound.
    /// What <see cref="SpeakerLabelling.DescribeDurationRisk"/> reads, and therefore the only way to
    /// put that warning in front of a test without a three-hour recording.
    /// </summary>
    public TimeSpan? ReliableUpTo { get; init; }

    public void Validate()
    {
        if (SpeakerCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SpeakerCount), SpeakerCount, "The fake needs at least one speaker.");
        }

        if (MaxSpeakers is { } max && max < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSpeakers), max, "A cap of none is expressed as null, not zero.");
        }

        if (ReliableUpTo is { } bound && bound <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ReliableUpTo), bound, "A bound of none is expressed as null, not zero.");
        }

        if (TurnLength <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(TurnLength), TurnLength, "Turn length must be positive.");
        }

        if (Overlap < TimeSpan.Zero || Overlap >= TurnLength)
        {
            throw new ArgumentOutOfRangeException(nameof(Overlap), Overlap, "Overlap must be non-negative and shorter than a turn.");
        }
    }
}

/// <summary>
/// A labeller that hears nothing and says a fixed sequence anyway: speakers take four-second
/// turns from the start of the file to its end. It reads the source the way a real labeller
/// would — every sample, so the single-read audio path and the second-open cost are exercised —
/// and it is deterministic, so a test can assert on exactly which word got which speaker.
/// </summary>
/// <remarks>
/// The same reason the fake transcription engine exists: without it nothing downstream of the
/// labeller — the assignment, the six formatters, the cue builder's speaker breaks, the CLI flag,
/// the window's checkbox — is testable until a diarisation model is integrated, and CI could never
/// exercise the opt-in end to end.
/// </remarks>
public sealed class FakeSpeakerLabeller : ISpeakerLabeller
{
    private readonly FakeSpeakerLabellerOptions _options;
    private bool _loaded;

    public FakeSpeakerLabeller(FakeSpeakerLabellerOptions? options = null)
    {
        _options = options ?? FakeSpeakerLabellerOptions.Default;
        _options.Validate();

        Capabilities = new SpeakerLabellerCapabilities
        {
            EngineName = "fake",
            ModelId = "fake-speakers",
            Backend = _options.Backend,
            SupportsFixedSpeakerCount = _options.SupportsFixedSpeakerCount,
            MaxSpeakers = _options.MaxSpeakers,
            ReliableUpTo = _options.ReliableUpTo,
        };
    }

    public int LoadCount { get; private set; }

    /// <summary>How many samples the last <see cref="LabelAsync"/> read, so a test can see it read the file.</summary>
    public long SamplesRead { get; private set; }

    /// <summary>
    /// Built from the options rather than fixed, so the fake can stand in for a labeller that has
    /// the shipping one's limits — a cap, a length its labels are established to, and no way to be
    /// told a count. The defaults are what this fake has always advertised: no cap, no bound, and a
    /// count it honours.
    /// </summary>
    public SpeakerLabellerCapabilities Capabilities { get; }

    public ValueTask LoadAsync(CancellationToken ct = default)
    {
        if (_loaded)
        {
            return ValueTask.CompletedTask;
        }

        if (_options.FailOnLoad)
        {
            throw new InvalidOperationException("Fake speaker labeller was configured to fail on load.");
        }

        LoadCount++;
        _loaded = true;
        return ValueTask.CompletedTask;
    }

    public async Task<IReadOnlyList<SpeakerTurn>> LabelAsync(
        IAudioSource audio,
        SpeakerLabellingOptions options,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        await LoadAsync(ct).ConfigureAwait(false);

        progress?.Report(new TranscriptionProgress { Stage = TranscriptionStage.LabellingSpeakers, Total = audio.Duration });

        long samples = 0;
        await foreach (var block in audio.ReadAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            samples += block.Length;
        }

        SamplesRead = samples;
        var duration = SpeakerTurns.FromSeconds(samples / (double)audio.SampleRate);

        // The count reaches the model only where the capability says it does. A fake that quietly
        // honoured it either way would report SupportsFixedSpeakerCount = false and then behave as
        // though it were true, which is the one thing a seam's stand-in must never do — and it would
        // leave the fold downstream with nothing to fold, so the repair the shipping labeller
        // depends on would pass its tests by never running.
        var speakers = (_options.SupportsFixedSpeakerCount ? options.SpeakerCount : null)
            ?? _options.SpeakerCount;

        // Nor can it exceed a cap it advertises: above one, a real labeller merges the extra voice
        // rather than reporting it, and DescribeLimit's sentence is owed exactly when the labels
        // reach the ceiling.
        if (_options.MaxSpeakers is { } max)
        {
            speakers = Math.Min(speakers, max);
        }

        var turns = new List<SpeakerTurn>();
        var index = 0;
        for (var start = TimeSpan.Zero; start < duration; start += _options.TurnLength)
        {
            var end = start + _options.TurnLength + _options.Overlap;
            if (end > duration)
            {
                end = duration;
            }

            turns.Add(new SpeakerTurn
            {
                Start = start,
                End = end,
                Speaker = string.Create(CultureInfo.InvariantCulture, $"SPEAKER_{index % speakers:00}"),
            });
            index++;
        }

        progress?.Report(new TranscriptionProgress
        {
            Stage = TranscriptionStage.LabellingSpeakers,
            Processed = duration,
            Total = audio.Duration ?? duration,
        });

        return turns;
    }

    public ValueTask DisposeAsync()
    {
        _loaded = false;
        return ValueTask.CompletedTask;
    }
}
