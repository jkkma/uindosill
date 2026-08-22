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

        var data = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            var value = (short)Math.Round(clamped * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2, 2), value);
        }

        WriteRiffHeader(stream, data.Length, sampleRate, channels, bitsPerSample: 16, formatTag: 1);
        stream.Write(data);
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
