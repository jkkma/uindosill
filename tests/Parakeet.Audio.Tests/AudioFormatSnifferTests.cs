using System.Text;
using Parakeet.Audio;

namespace Parakeet.Audio.Tests;

public class AudioFormatSnifferTests
{
    private static byte[] Header(params object[] parts)
    {
        var bytes = new List<byte>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case string text:
                    bytes.AddRange(Encoding.ASCII.GetBytes(text));
                    break;
                case byte value:
                    bytes.Add(value);
                    break;
                case int count:
                    bytes.AddRange(new byte[count]);
                    break;
                default:
                    throw new ArgumentException("unsupported fixture part", nameof(parts));
            }
        }

        while (bytes.Count < AudioFormatSniffer.HeaderBytes)
        {
            bytes.Add(0);
        }

        return [.. bytes];
    }

    [Fact]
    public void RiffWaveIsWav() =>
        Assert.Equal(AudioContainer.Wav, AudioFormatSniffer.Detect(Header("RIFF", 4, "WAVE")).Container);

    [Fact]
    public void Rf64IsWav() =>
        Assert.Equal(AudioContainer.Wav, AudioFormatSniffer.Detect(Header("RF64", 4, "WAVE")).Container);

    [Fact]
    public void Bw64IsWav() =>
        Assert.Equal(AudioContainer.Wav, AudioFormatSniffer.Detect(Header("BW64", 4, "WAVE")).Container);

    [Fact]
    public void RiffThatIsNotWaveIsNotClaimed() =>
        Assert.Equal(AudioContainer.Unknown, AudioFormatSniffer.Detect(Header("RIFF", 4, "AVI ")).Container);

    [Fact]
    public void Id3TaggedFileIsMp3() =>
        Assert.Equal(AudioContainer.Mp3, AudioFormatSniffer.Detect(Header("ID3", (byte)3, (byte)0)).Container);

    [Fact]
    public void RawMpegSyncIsMp3() =>
        Assert.Equal(AudioContainer.Mp3, AudioFormatSniffer.Detect(Header((byte)0xFF, (byte)0xFB)).Container);

    [Fact]
    public void AdtsSyncIsAac()
    {
        // All four second bytes ADTS can put under its sync: MPEG-4 and MPEG-2, each with and
        // without CRC protection (the low bit). 0xF0 and 0xF8 are the protected variants, and
        // until 2026-08-29 they fell through to the raw-MPEG catch-all and came back as Mp3 —
        // so a correctly named .aac file was reported as a renamed mp3 that never was.
        foreach (var second in new byte[] { 0xF0, 0xF1, 0xF8, 0xF9 })
        {
            Assert.Equal(AudioContainer.Aac, AudioFormatSniffer.Detect(Header((byte)0xFF, second)).Container);
        }
    }

    [Fact]
    public void FtypBoxIsMp4() =>
        Assert.Equal(AudioContainer.Mp4, AudioFormatSniffer.Detect(Header(4, "ftypM4A ")).Container);

    [Fact]
    public void OggIsOgg() =>
        Assert.Equal(AudioContainer.Ogg, AudioFormatSniffer.Detect(Header("OggS")).Container);

    [Fact]
    public void FlacIsFlac() =>
        Assert.Equal(AudioContainer.Flac, AudioFormatSniffer.Detect(Header("fLaC")).Container);

    [Fact]
    public void EbmlIsMatroska() =>
        Assert.Equal(
            AudioContainer.Matroska,
            AudioFormatSniffer.Detect(Header((byte)0x1A, (byte)0x45, (byte)0xDF, (byte)0xA3)).Container);

    [Fact]
    public void AsfGuidIsAsf() =>
        Assert.Equal(
            AudioContainer.Asf,
            AudioFormatSniffer.Detect(Header((byte)0x30, (byte)0x26, (byte)0xB2, (byte)0x75)).Container);

    [Fact]
    public void AiffIsAiff() =>
        Assert.Equal(AudioContainer.Aiff, AudioFormatSniffer.Detect(Header("FORM", 4, "AIFF")).Container);

    [Fact]
    public void UnrecognisedBytesAreUnknown() =>
        Assert.Equal(AudioContainer.Unknown, AudioFormatSniffer.Detect(Header("ZZZZ")).Container);

    [Fact]
    public void EmptyHeaderIsUnknown() =>
        Assert.Equal(AudioContainer.Unknown, AudioFormatSniffer.Detect([]).Container);

    [Fact]
    public void RenamedFileIsIdentifiedByContentAndTheMismatchIsReported()
    {
        // People rename files. Parsing an mp3 as WAVE because of its extension produces noise,
        // not an error, which is exactly the class of failure worth spending code on.
        var detection = AudioFormatSniffer.Detect(Header("ID3", (byte)3, (byte)0), "interview.wav");

        Assert.Equal(AudioContainer.Mp3, detection.Container);
        Assert.True(detection.ExtensionMismatch);
        Assert.Equal(".wav", detection.ExtensionHint);
    }

    [Fact]
    public void MatchingExtensionIsNotAMismatch()
    {
        var detection = AudioFormatSniffer.Detect(Header("RIFF", 4, "WAVE"), "clip.wav");

        Assert.False(detection.ExtensionMismatch);
        Assert.True(detection.IsSupportedEverywhere);
    }
}

public class AudioSourcesTests
{
    [Fact]
    public async Task WaveFilesOpenOnEveryPlatform()
    {
        var path = TestTemp.NewPath("scratch.wav");
        try
        {
            WavWriter.WriteFile(path, new float[16_000], 16_000);

            await using var source = AudioSources.Open(path);
            Assert.Equal(16_000, source.SampleRate);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFileIsReportedAsMissing()
    {
        var missing = TestTemp.NewPath("scratch.wav");
        Assert.Throws<FileNotFoundException>(() => AudioSources.Open(missing));
    }

    [Fact]
    public void CompressedFormatsExplainWhyTheyCannotBeOpenedHere()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Media Foundation handles these on Windows.");

        var path = TestTemp.NewPath("scratch.mp3");
        try
        {
            File.WriteAllBytes(path, [0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0, 0]);

            var exception = Assert.Throws<UnsupportedAudioFormatException>(() => AudioSources.Open(path));
            Assert.Contains("Media Foundation", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Mp3", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SupportedExtensionsAlwaysIncludeWave() =>
        Assert.Contains(".wav", AudioSources.SupportedExtensions);

    [Fact]
    public void TheWindowsDecoderIsCompiledIntoTheAssemblyTheApplicationsReference()
    {
        // This assembly used to multi-target net10.0 and net10.0-windows, with Media Foundation
        // behind #if WINDOWS. Parakeet.Cli and Parakeet.App target plain net10.0, so they always
        // resolved the flavour the decoder was compiled OUT of: mp3 and m4a were unreachable in
        // every shipped build, on Windows, while CI stayed green because the -windows flavour
        // compiled fine and nothing referenced it.
        //
        // Asserting on the type's presence rather than on behaviour is the point — the failure was
        // that the code did not exist, not that it misbehaved. This test runs on Linux, which is
        // exactly where the mistake was invisible.
        var type = typeof(AudioSources).Assembly.GetType("Parakeet.Audio.MediaFoundationAudioSource");

        Assert.NotNull(type);
        Assert.NotNull(type!.GetMethod("Open", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
    }

    [Fact]
    public void CompressedExtensionsAreOfferedOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is a Windows decoder.");

        Assert.Contains(".mp3", AudioSources.SupportedExtensions);
        Assert.Contains(".m4a", AudioSources.SupportedExtensions);
        Assert.Contains(".mp4", AudioSources.SupportedExtensions);
    }
}
