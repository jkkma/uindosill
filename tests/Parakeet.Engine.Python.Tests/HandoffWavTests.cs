using Parakeet.Audio;
using Parakeet.Engine.Python;

namespace Parakeet.Engine.Python.Tests;

/// <summary>
/// The WAV the host hands the diariser sidecar carries the host's samples exactly. Measured
/// 2026-08-22 (<c>docs/UNPROVEN.md</c>): written as 16-bit PCM, the same recording decoded from a
/// 48 kHz MP3 came back from the reference 2.50% DER away from itself written as float, so the
/// handoff format is part of whether the sidecar's answer is the reference's.
/// </summary>
public sealed class HandoffWavTests
{
    [Fact]
    public async Task TheSidecarIsHandedTheHostsSamplesExactly()
    {
        // Values PCM16 cannot carry: a third is not a multiple of 1/32768, a microvolt rounds to
        // zero, and a sample just under full scale lands on a different integer through *32767 than
        // the /32768 read would undo. The ramp in between is ordinary audio. If the handoff ever
        // quantises again, the bit-for-bit comparison below says so before any model is asked.
        var samples = new float[16_000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = MathF.Sin(2 * MathF.PI * 220 * i / 16_000) * 0.4f;
        }

        samples[10] = 1f / 3f;
        samples[11] = 1e-6f;
        samples[12] = 0.999_999f;
        samples[13] = -0.7f;

        // The control: these inputs are ones 16-bit PCM would have changed, or the assertion below
        // would pass for a PCM16 handoff too and prove nothing.
        var throughPcm16 = samples.Select(s => (short)Math.Round(Math.Clamp(s, -1f, 1f) * 32767f) / 32768f);
        Assert.NotEqual(samples, throughPcm16);

        var directory = new DirectoryInfo(TestTemp.NewDirectory("uindosill-handoff"));
        var source = Path.Combine(directory.FullName, "source.wav");
        WavWriter.WriteFloat32File(source, samples, 16_000);

        string? handoff = null;
        try
        {
            await using (var audio = WavAudioSource.Open(source))
            {
                handoff = await SidecarSpeakerLabeller.WriteResampledWavAsync(audio, CancellationToken.None);
            }

            await using var written = WavAudioSource.Open(handoff);
            Assert.Equal(WavSampleFormat.Float32, written.Format.SampleFormat);
            Assert.Equal(16_000, written.SampleRate);
            Assert.Equal(1, written.Channels);

            var readBack = new List<float>();
            await foreach (var block in written.ReadAsync())
            {
                readBack.AddRange(block.ToArray());
            }

            Assert.Equal(samples, readBack);
        }
        finally
        {
            if (handoff is not null && File.Exists(handoff))
            {
                File.Delete(handoff);
            }

            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TheStagedWavIsWhatOneUnbrokenPassOfTheResamplerProduces()
    {
        // Since 2026-08-23 the decode and the resample run at the same time — a producer reading
        // blocks and a consumer resampling them, over a bounded queue — because run one after the
        // other they took 32.85 s on a 157-minute file against 19.76 s for the slower half alone.
        // The resampler therefore now sees its input copied, from another thread, arriving in a
        // different rhythm. None of that is allowed to move a sample: it is a filter carrying
        // history across block boundaries, so a pipeline that reordered or re-split its input would
        // produce a file that is entirely plausible and quietly wrong.
        //
        // 44.1 kHz on purpose. At 16 kHz the resampler takes its identity path and copies samples
        // through, which would assert nothing about resampling at all — and 44.1 is the rate the
        // recording that prompted the change actually is, tabulated into 160 phases.
        const int SourceRate = 44_100;

        var samples = new float[SourceRate * 3];
        for (var i = 0; i < samples.Length; i++)
        {
            // Two tones and a slow envelope: something with content above the 8 kHz output Nyquist,
            // so the filter is doing work a plain decimation would get wrong.
            var t = i / (float)SourceRate;
            samples[i] = (MathF.Sin(2 * MathF.PI * 440 * t) * 0.4f
                          + MathF.Sin(2 * MathF.PI * 11_000 * t) * 0.25f)
                         * (0.6f + 0.4f * MathF.Sin(2 * MathF.PI * 0.7f * t));
        }

        var directory = new DirectoryInfo(TestTemp.NewDirectory("uindosill-staging"));
        var source = Path.Combine(directory.FullName, "source.wav");
        WavWriter.WriteFloat32File(source, samples, SourceRate);

        string? handoff = null;
        try
        {
            await using (var audio = WavAudioSource.Open(source))
            {
                handoff = await SidecarSpeakerLabeller.WriteResampledWavAsync(audio, CancellationToken.None);
            }

            await using var written = WavAudioSource.Open(handoff);
            Assert.Equal(16_000, written.SampleRate);

            var staged = new List<float>();
            await foreach (var block in written.ReadAsync())
            {
                staged.AddRange(block.ToArray());
            }

            // The reference: one resampler, one thread, the whole recording in a single call. That
            // this is a fair comparison against a block-fed one is itself pinned, by
            // ResamplerTests.ChunkedInputProducesTheSameSamplesAsTheWholeFile.
            var reference = new List<float>();
            var resampler = new Parakeet.Audio.Resampler(SourceRate, 16_000);
            resampler.Process(samples, reference);
            resampler.Complete(reference);

            Assert.Equal(reference.Count, staged.Count);
            Assert.Equal(reference, staged);
        }
        finally
        {
            if (handoff is not null && File.Exists(handoff))
            {
                File.Delete(handoff);
            }

            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ACancelledStagingStopsBothHalvesRatherThanLeavingOneRunning()
    {
        // The two halves are joined by a bounded queue, which is the one structure here that can
        // hang rather than fail: a producer parked on a full queue waits for a consumer, and a
        // consumer parked on an empty one waits for a producer. Cancellation has to reach both.
        const int SourceRate = 44_100;

        var samples = new float[SourceRate * 20];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = MathF.Sin(2 * MathF.PI * 440 * i / SourceRate) * 0.3f;
        }

        var directory = new DirectoryInfo(TestTemp.NewDirectory("uindosill-staging-cancel"));
        var source = Path.Combine(directory.FullName, "source.wav");
        WavWriter.WriteFloat32File(source, samples, SourceRate);

        try
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await using var audio = WavAudioSource.Open(source);

            // Raised rather than hung, and within the test's own patience rather than the runner's.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => SidecarSpeakerLabeller.WriteResampledWavAsync(audio, cancelled.Token).WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
