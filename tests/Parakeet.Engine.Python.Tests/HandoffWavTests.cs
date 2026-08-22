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

        var directory = Directory.CreateTempSubdirectory("uindosill-handoff");
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
}
