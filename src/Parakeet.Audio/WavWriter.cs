using System.Buffers.Binary;

namespace Parakeet.Audio;

/// <summary>
/// Minimal RIFF/WAVE writer. Used to produce fixtures and to save the exact audio a decode
/// saw when a transcript needs to be reproduced.
/// </summary>
public static class WavWriter
{
    /// <summary>Writes 16-bit PCM. Samples outside [-1, 1] are clamped rather than wrapped.</summary>
    public static void WritePcm16(Stream stream, ReadOnlySpan<float> samples, int sampleRate, int channels = 1)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        // Header first and blocks after, the same shape as WriteFloat32 below and for its two
        // reasons: the header is also where an over-large payload is refused, which should
        // happen before any samples are copied, and a second whole-file byte array (which this
        // held until 2026-08-29) is a second copy of the recording — with an int length that
        // overflowed above a gigabyte of samples besides.
        WriteRiffHeader(stream, (long)samples.Length * 2, sampleRate, channels, bitsPerSample: 16, formatTag: 1);

        Span<byte> block = stackalloc byte[16 * 1024];
        var perBlock = block.Length / 2;
        for (var offset = 0; offset < samples.Length; offset += perBlock)
        {
            var count = Math.Min(perBlock, samples.Length - offset);
            for (var i = 0; i < count; i++)
            {
                var clamped = Math.Clamp(samples[offset + i], -1f, 1f);
                var value = (short)Math.Round(clamped * 32767f);
                BinaryPrimitives.WriteInt16LittleEndian(block.Slice(i * 2, 2), value);
            }

            stream.Write(block[..(count * 2)]);
        }
    }

    /// <summary>Writes 32-bit IEEE float, the format that survives a round trip unchanged.</summary>
    public static void WriteFloat32(Stream stream, ReadOnlySpan<float> samples, int sampleRate, int channels = 1)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Streamed in blocks rather than encoded into a second whole-file array: the caller's span
        // is already one copy of the recording, and staging a three-hour file for the diariser paid
        // for two until 2026-08-22 — 690 MB of samples and 690 MB of their bytes, alive together.
        WriteRiffHeader(stream, (long)samples.Length * 4, sampleRate, channels, bitsPerSample: 32, formatTag: 3);

        Span<byte> block = stackalloc byte[16 * 1024];
        var perBlock = block.Length / 4;
        for (var offset = 0; offset < samples.Length; offset += perBlock)
        {
            var count = Math.Min(perBlock, samples.Length - offset);
            for (var i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(block.Slice(i * 4, 4), samples[offset + i]);
            }

            stream.Write(block[..(count * 4)]);
        }
    }

    public static void WriteFile(string path, ReadOnlySpan<float> samples, int sampleRate, int channels = 1)
    {
        using var stream = File.Create(path);
        WritePcm16(stream, samples, sampleRate, channels);
    }

    /// <summary>
    /// <see cref="WriteFloat32"/> to a path: the file a reader gets back sample for sample, for when
    /// the audio is an input to a measurement rather than a fixture.
    /// </summary>
    public static void WriteFloat32File(string path, ReadOnlySpan<float> samples, int sampleRate, int channels = 1)
    {
        using var stream = File.Create(path);
        WriteFloat32(stream, samples, sampleRate, channels);
    }

    /// <summary>The 44-byte canonical header for <paramref name="dataLength"/> bytes of samples to follow.</summary>
    private static void WriteRiffHeader(
        Stream stream, long dataLength, int sampleRate, int channels, int bitsPerSample, int formatTag)
    {
        // The RIFF size fields are 32 bits, and past them the format simply ends. Written
        // unchecked, both casts below wrap modulo 2^32 for a payload over ~4.29 GB — every
        // sample byte still lands in the file, so what comes out is a header that lies about
        // the data's length, and a reader that trusts a too-small declared size (this
        // project's own WavAudioSource among them) reads back a fraction of the recording
        // with no error anywhere. RF64 is the format that holds more and this writer does
        // not speak it, so the honest answer is a refusal here, before a byte is written —
        // not a truncation discovered hours of audio later.
        if (36 + dataLength > uint.MaxValue)
        {
            throw new NotSupportedException(
                $"{dataLength:N0} bytes of samples do not fit a RIFF header's 32-bit sizes (about 4 GiB — " +
                "roughly 18 hours at 16 kHz mono float). A file this long needs RF64, which this writer " +
                "does not produce.");
        }

        var blockAlign = channels * bitsPerSample / 8;
        var byteRate = sampleRate * blockAlign;

        Span<byte> header = stackalloc byte[44];
        WriteAscii(header[..4], "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], (uint)(36 + dataLength));
        WriteAscii(header[8..12], "WAVE");
        WriteAscii(header[12..16], "fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..22], (ushort)formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..24], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..28], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..32], (uint)byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..34], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..36], (ushort)bitsPerSample);
        WriteAscii(header[36..40], "data");
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..44], (uint)dataLength);

        stream.Write(header);
    }

    private static void WriteAscii(Span<byte> destination, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            destination[i] = (byte)value[i];
        }
    }
}
