using System.Buffers.Binary;
using System.Text;
using Parakeet.Audio;

namespace Parakeet.Audio.Tests;

public class WavAudioSourceTests
{
    private static async Task<float[]> ReadAllAsync(WavAudioSource source)
    {
        var samples = new List<float>();
        await foreach (var block in source.ReadAsync())
        {
            samples.AddRange(block.ToArray());
        }

        return [.. samples];
    }

    private static float[] Ramp(int count)
    {
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = (float)Math.Sin(i * 0.05) * 0.8f;
        }

        return samples;
    }

    [Fact]
    public async Task Pcm16RoundTrips()
    {
        var expected = Ramp(1000);
        using var stream = new MemoryStream();
        WavWriter.WritePcm16(stream, expected, 16_000);
        stream.Position = 0;

        await using var source = WavAudioSource.Create(stream);
        var actual = await ReadAllAsync(source);

        Assert.Equal(16_000, source.SampleRate);
        Assert.Equal(1, source.Channels);
        Assert.Equal(expected.Length, actual.Length);
        // The writer scales by 32767 so +1.0 cannot clip, the reader divides by 32768 so the
        // range stays symmetric. That asymmetry is deliberate and costs up to two 16-bit steps.
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], actual[i], tolerance: 2f / 32768f);
        }
    }

    [Fact]
    public async Task Float32RoundTripsExactly()
    {
        var expected = Ramp(500);
        using var stream = new MemoryStream();
        WavWriter.WriteFloat32(stream, expected, 48_000);
        stream.Position = 0;

        await using var source = WavAudioSource.Create(stream);
        var actual = await ReadAllAsync(source);

        Assert.Equal(48_000, source.SampleRate);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public async Task IntegerBitDepthsDecodeToTheSameShape(int bits)
    {
        var stream = WavFixtures.IntegerPcm(bits, sampleRate: 22_050, channels: 1, frames: 300);

        await using var source = WavAudioSource.Create(stream);
        var samples = await ReadAllAsync(source);

        Assert.Equal(300, samples.Length);
        Assert.All(samples, s => Assert.InRange(s, -1.05f, 1.05f));

        // A full-scale positive value must land near +1 whatever the bit depth.
        Assert.InRange(samples.Max(), 0.9f, 1.01f);
        Assert.InRange(samples.Min(), -1.01f, -0.9f);
    }

    [Fact]
    public async Task StereoIsDownmixedByAveraging()
    {
        // Left is +0.8 everywhere, right is -0.4: the average is +0.2 rather than either channel.
        var interleaved = new float[200];
        for (var i = 0; i < interleaved.Length; i += 2)
        {
            interleaved[i] = 0.8f;
            interleaved[i + 1] = -0.4f;
        }

        using var stream = new MemoryStream();
        WavWriter.WriteFloat32(stream, interleaved, 16_000, channels: 2);
        stream.Position = 0;

        await using var source = WavAudioSource.Create(stream);
        var samples = await ReadAllAsync(source);

        Assert.Equal(100, samples.Length);
        Assert.All(samples, s => Assert.Equal(0.2f, s, 5));
    }

    [Fact]
    public async Task ExtensibleFormatIsUnwrapped()
    {
        var stream = WavFixtures.Extensible(frames: 128);

        await using var source = WavAudioSource.Create(stream);
        var samples = await ReadAllAsync(source);

        Assert.Equal(WavSampleFormat.Pcm16, source.Format.SampleFormat);
        Assert.Equal(128, samples.Length);
    }

    [Fact]
    public async Task Rf64UsesTheDs64DataSize()
    {
        var stream = WavFixtures.Rf64(frames: 256);

        await using var source = WavAudioSource.Create(stream);
        var samples = await ReadAllAsync(source);

        Assert.Equal(256, samples.Length);
    }

    [Fact]
    public async Task UnknownChunksAndOddSizesAreSkipped()
    {
        var stream = WavFixtures.WithExtraChunks(frames: 64);

        await using var source = WavAudioSource.Create(stream);
        var samples = await ReadAllAsync(source);

        Assert.Equal(64, samples.Length);
    }

    [Fact]
    public async Task TruncatedDataChunkRecoversWhatIsActuallyThere()
    {
        // Recordings that were cut off declare more data than the file holds. Trusting the
        // header reads past the end; refusing to open it loses the whole recording.
        var stream = WavFixtures.IntegerPcm(16, 16_000, 1, 1000);
        var bytes = stream.ToArray();
        var truncated = new MemoryStream(bytes[..(bytes.Length - 400)]);

        await using var source = WavAudioSource.Create(truncated);
        var samples = await ReadAllAsync(source);

        Assert.Equal(800, samples.Length);
    }

    [Fact]
    public async Task NonFiniteFloatSamplesAreZeroed()
    {
        // One NaN turns an entire mel frame into NaN and the decode silently returns nothing.
        var samples = new float[] { 0.1f, float.NaN, 0.2f, float.PositiveInfinity, 0.3f };
        using var stream = new MemoryStream();
        WavWriter.WriteFloat32(stream, samples, 16_000);
        stream.Position = 0;

        await using var source = WavAudioSource.Create(stream);
        var actual = await ReadAllAsync(source);

        Assert.Equal([0.1f, 0f, 0.2f, 0f, 0.3f], actual);
    }

    [Fact]
    public void FileWithoutFmtChunkIsRejected()
    {
        var stream = WavFixtures.MissingChunk(dropFmt: true);
        var exception = Assert.Throws<AudioDecodeException>(() => WavAudioSource.Create(stream));

        Assert.Contains("fmt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FileWithoutDataChunkIsRejected()
    {
        var stream = WavFixtures.MissingChunk(dropFmt: false);
        var exception = Assert.Throws<AudioDecodeException>(() => WavAudioSource.Create(stream));

        Assert.Contains("data", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompressedWavePayloadIsRejectedWithAnExplanation()
    {
        var stream = WavFixtures.IntegerPcm(16, 16_000, 1, 32, formatTag: 0x0011);
        var exception = Assert.Throws<AudioDecodeException>(() => WavAudioSource.Create(stream));

        Assert.Contains("ADPCM", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonRiffFileIsRejected()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("NOTAWAVEFILEATALL...."));
        Assert.Throws<AudioDecodeException>(() => WavAudioSource.Create(stream));
    }

    [Fact]
    public async Task ReadingTwiceIsRefusedRatherThanReturningNothing()
    {
        var stream = WavFixtures.IntegerPcm(16, 16_000, 1, 100);
        await using var source = WavAudioSource.Create(stream);

        await ReadAllAsync(source);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await ReadAllAsync(source));
    }

    [Fact]
    public async Task DurationMatchesTheFrameCount()
    {
        var stream = WavFixtures.IntegerPcm(16, 8_000, 2, 4_000);
        await using var source = WavAudioSource.Create(stream);

        Assert.Equal(TimeSpan.FromSeconds(0.5), source.Duration);
        Assert.Equal(4_000, source.FrameCount);
    }
}

internal static class WavFixtures
{
    public static MemoryStream IntegerPcm(int bits, int sampleRate, int channels, int frames, int formatTag = 1)
    {
        var bytesPerSample = bits / 8;
        var blockAlign = bytesPerSample * channels;
        var data = new byte[frames * blockAlign];

        for (var frame = 0; frame < frames; frame++)
        {
            // Alternate full-scale positive and negative so range handling is exercised.
            var positive = frame % 2 == 0;

            for (var channel = 0; channel < channels; channel++)
            {
                var offset = (frame * blockAlign) + (channel * bytesPerSample);
                switch (bits)
                {
                    case 8:
                        data[offset] = positive ? (byte)255 : (byte)1;
                        break;
                    case 16:
                        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset, 2), positive ? short.MaxValue : (short)(short.MinValue + 1));
                        break;
                    case 24:
                        var value24 = positive ? 0x7FFFFF : -0x7FFFFF;
                        data[offset] = (byte)(value24 & 0xFF);
                        data[offset + 1] = (byte)((value24 >> 8) & 0xFF);
                        data[offset + 2] = (byte)((value24 >> 16) & 0xFF);
                        break;
                    case 32:
                        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), positive ? int.MaxValue : int.MinValue + 1);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(bits));
                }
            }
        }

        var stream = new MemoryStream();
        WriteHeader(stream, "RIFF", data.Length, sampleRate, channels, bits, formatTag);
        stream.Write(data);
        stream.Position = 0;
        return stream;
    }

    public static MemoryStream Extensible(int frames)
    {
        var data = new byte[frames * 2];
        var stream = new MemoryStream();

        WriteAscii(stream, "RIFF");
        WriteUInt32(stream, (uint)(4 + 8 + 40 + 8 + data.Length));
        WriteAscii(stream, "WAVE");

        WriteAscii(stream, "fmt ");
        WriteUInt32(stream, 40);
        WriteUInt16(stream, 0xFFFE);           // WAVE_FORMAT_EXTENSIBLE
        WriteUInt16(stream, 1);                // channels
        WriteUInt32(stream, 16_000);
        WriteUInt32(stream, 32_000);
        WriteUInt16(stream, 2);                // block align
        WriteUInt16(stream, 16);               // bits per sample
        WriteUInt16(stream, 22);               // cbSize
        WriteUInt16(stream, 16);               // valid bits
        WriteUInt32(stream, 4);                // channel mask
        WriteUInt16(stream, 1);                // SubFormat GUID starts with the real format tag
        stream.Write(new byte[14]);            // rest of the GUID

        WriteAscii(stream, "data");
        WriteUInt32(stream, (uint)data.Length);
        stream.Write(data);
        stream.Position = 0;
        return stream;
    }

    public static MemoryStream Rf64(int frames)
    {
        var data = new byte[frames * 2];
        var stream = new MemoryStream();

        WriteAscii(stream, "RF64");
        WriteUInt32(stream, uint.MaxValue);    // -1: the real size lives in ds64
        WriteAscii(stream, "WAVE");

        WriteAscii(stream, "ds64");
        WriteUInt32(stream, 28);
        WriteUInt64(stream, 0);                // riff size
        WriteUInt64(stream, (ulong)data.Length);
        WriteUInt64(stream, (ulong)frames);
        WriteUInt32(stream, 0);                // table length

        WriteAscii(stream, "fmt ");
        WriteUInt32(stream, 16);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, 1);
        WriteUInt32(stream, 16_000);
        WriteUInt32(stream, 32_000);
        WriteUInt16(stream, 2);
        WriteUInt16(stream, 16);

        WriteAscii(stream, "data");
        WriteUInt32(stream, uint.MaxValue);    // -1: see ds64
        stream.Write(data);
        stream.Position = 0;
        return stream;
    }

    public static MemoryStream WithExtraChunks(int frames)
    {
        var data = new byte[frames * 2];
        var stream = new MemoryStream();

        WriteAscii(stream, "RIFF");
        WriteUInt32(stream, 0);
        WriteAscii(stream, "WAVE");

        // An odd-sized chunk, which is followed by a pad byte that the size does not count.
        WriteAscii(stream, "LIST");
        WriteUInt32(stream, 5);
        stream.Write("INFOx"u8);
        stream.WriteByte(0);

        WriteAscii(stream, "fmt ");
        WriteUInt32(stream, 16);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, 1);
        WriteUInt32(stream, 16_000);
        WriteUInt32(stream, 32_000);
        WriteUInt16(stream, 2);
        WriteUInt16(stream, 16);

        WriteAscii(stream, "fact");
        WriteUInt32(stream, 4);
        WriteUInt32(stream, (uint)frames);

        WriteAscii(stream, "data");
        WriteUInt32(stream, (uint)data.Length);
        stream.Write(data);
        stream.Position = 0;
        return stream;
    }

    public static MemoryStream MissingChunk(bool dropFmt)
    {
        var stream = new MemoryStream();
        WriteAscii(stream, "RIFF");
        WriteUInt32(stream, 0);
        WriteAscii(stream, "WAVE");

        if (dropFmt)
        {
            WriteAscii(stream, "data");
            WriteUInt32(stream, 4);
            stream.Write(new byte[4]);
        }
        else
        {
            WriteAscii(stream, "fmt ");
            WriteUInt32(stream, 16);
            WriteUInt16(stream, 1);
            WriteUInt16(stream, 1);
            WriteUInt32(stream, 16_000);
            WriteUInt32(stream, 32_000);
            WriteUInt16(stream, 2);
            WriteUInt16(stream, 16);
        }

        stream.Position = 0;
        return stream;
    }

    private static void WriteHeader(
        Stream stream, string riffId, int dataLength, int sampleRate, int channels, int bits, int formatTag)
    {
        var blockAlign = channels * bits / 8;

        WriteAscii(stream, riffId);
        WriteUInt32(stream, (uint)(36 + dataLength));
        WriteAscii(stream, "WAVE");
        WriteAscii(stream, "fmt ");
        WriteUInt32(stream, 16);
        WriteUInt16(stream, (ushort)formatTag);
        WriteUInt16(stream, (ushort)channels);
        WriteUInt32(stream, (uint)sampleRate);
        WriteUInt32(stream, (uint)(sampleRate * blockAlign));
        WriteUInt16(stream, (ushort)blockAlign);
        WriteUInt16(stream, (ushort)bits);
        WriteAscii(stream, "data");
        WriteUInt32(stream, (uint)dataLength);
    }

    private static void WriteAscii(Stream stream, string value) =>
        stream.Write(Encoding.ASCII.GetBytes(value));

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }
}
