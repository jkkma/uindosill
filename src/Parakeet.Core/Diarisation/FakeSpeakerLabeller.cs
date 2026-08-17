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

    public void Validate()
    {
        if (SpeakerCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SpeakerCount), SpeakerCount, "The fake needs at least one speaker.");
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
    }

    public int LoadCount { get; private set; }

    /// <summary>How many samples the last <see cref="LabelAsync"/> read, so a test can see it read the file.</summary>
    public long SamplesRead { get; private set; }

    public SpeakerLabellerCapabilities Capabilities { get; } = new()
    {
        EngineName = "fake",
        ModelId = "fake-speakers",
        Backend = ComputeBackend.Cpu,
        SupportsFixedSpeakerCount = true,
        MaxSpeakers = null,
    };

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
        var speakers = options.SpeakerCount ?? _options.SpeakerCount;

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
