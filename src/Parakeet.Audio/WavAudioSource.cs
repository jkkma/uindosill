using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Parakeet.Core.Audio;

namespace Parakeet.Audio;

public class AudioDecodeException : Exception
{
    public AudioDecodeException(string message)
        : base(message)
    {
    }

    public AudioDecodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AudioDecodeException()
    {
    }
}

/// <summary>How the samples in a WAVE data chunk are encoded.</summary>
public enum WavSampleFormat
{
    UnsignedPcm8,
    Pcm16,
    Pcm24,
    Pcm32,
    Float32,
    Float64,
}

public sealed record WavFormat
{
    public required int SampleRate { get; init; }

    public required int Channels { get; init; }

    public required WavSampleFormat SampleFormat { get; init; }

    /// <summary>Bytes for one sample of one channel.</summary>
    public int BytesPerSample => SampleFormat switch
    {
        WavSampleFormat.UnsignedPcm8 => 1,
        WavSampleFormat.Pcm16 => 2,
        WavSampleFormat.Pcm24 => 3,
        WavSampleFormat.Pcm32 or WavSampleFormat.Float32 => 4,
        WavSampleFormat.Float64 => 8,
        _ => throw new AudioDecodeException($"Unhandled sample format {SampleFormat}."),
    };

    /// <summary>Bytes for one frame across all channels.</summary>
    public int BlockAlign => BytesPerSample * Channels;
}

/// <summary>
/// A pure-managed RIFF/WAVE reader covering RIFF, RF64 and BW64, 8/16/24/32-bit integer PCM,
/// 32- and 64-bit float, and WAVE_FORMAT_EXTENSIBLE.
/// </summary>
/// <remarks>
/// <para>
/// No ASR library in this space reads audio files — sherpa-onnx exports over a hundred types
/// and not one of them decodes audio — so the decoding layer is ours to own. This half of it
/// is deliberately dependency-free and platform-free: it runs in CI on Linux, which is what
/// makes the format edge cases testable at all.
/// </para>
/// <para>
/// Multi-channel input is downmixed to mono by averaging. Averaging can cancel material that
/// is out of phase between channels; the alternative — silently transcribing only the left
/// channel — loses anything panned right, which is worse and harder to notice.
/// </para>
/// </remarks>
public sealed class WavAudioSource : IAudioSource
{
    private const int ReadBufferBytes = 64 * 1024;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long _dataOffset;
    private readonly long _dataLength;
    private bool _consumed;

    private WavAudioSource(Stream stream, bool leaveOpen, WavFormat format, long dataOffset, long dataLength)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _dataOffset = dataOffset;
        _dataLength = dataLength;
        Format = format;
    }

    public WavFormat Format { get; }

    public int SampleRate => Format.SampleRate;

    public int Channels => Format.Channels;

    public TimeSpan? Duration =>
        TimeSpan.FromSeconds(_dataLength / (double)Format.BlockAlign / Format.SampleRate);

    /// <summary>Total mono frames in the file.</summary>
    public long FrameCount => _dataLength / Format.BlockAlign;

    public static WavAudioSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, ReadBufferBytes, FileOptions.SequentialScan | FileOptions.Asynchronous);

        try
        {
            return Create(stream, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static WavAudioSource Create(Stream stream, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The WAVE reader needs a readable, seekable stream.", nameof(stream));
        }

        var (format, dataOffset, dataLength) = ParseHeader(stream);
        return new WavAudioSource(stream, leaveOpen, format, dataOffset, dataLength);
    }

    private static (WavFormat Format, long DataOffset, long DataLength) ParseHeader(Stream stream)
    {
        stream.Position = 0;

        Span<byte> header = stackalloc byte[12];
        ReadExactly(stream, header, "RIFF header");

        var riffId = AudioFormatSniffer.FourCc(header[..4]);
        var isRf64 = riffId is "RF64" or "BW64";
        if (riffId != "RIFF" && !isRf64)
        {
            throw new AudioDecodeException($"Not a RIFF/WAVE file: leading chunk id is '{riffId}'.");
        }

        if (AudioFormatSniffer.FourCc(header[8..12]) != "WAVE")
        {
            throw new AudioDecodeException("RIFF container is not WAVE.");
        }

        WavFormat? format = null;
        long dataOffset = -1;
        long dataLength = -1;
        long rf64DataSize = -1;

        Span<byte> chunkHeader = stackalloc byte[8];

        while (stream.Position + 8 <= stream.Length)
        {
            ReadExactly(stream, chunkHeader, "chunk header");
            var id = AudioFormatSniffer.FourCc(chunkHeader[..4]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..8]);
            var chunkStart = stream.Position;

            switch (id)
            {
                case "ds64":
                {
                    // RF64 puts the real 64-bit sizes here because the RIFF fields max out at 4 GB.
                    Span<byte> ds64 = stackalloc byte[24];
                    ReadExactly(stream, ds64, "ds64 chunk");
                    rf64DataSize = BinaryPrimitives.ReadInt64LittleEndian(ds64[8..16]);
                    break;
                }

                case "fmt ":
                    format = ParseFormatChunk(stream, size);
                    break;

                case "data":
                    dataOffset = chunkStart;
                    dataLength = size;
                    break;

                default:
                    break;
            }

            // Chunks are word-aligned: an odd size is followed by a pad byte that is not counted.
            var advance = size + (size % 2);
            var next = chunkStart + advance;
            if (next <= chunkStart || next > stream.Length)
            {
                break;
            }

            stream.Position = next;
        }

        if (format is null)
        {
            throw new AudioDecodeException("WAVE file has no fmt chunk.");
        }

        if (dataOffset < 0)
        {
            throw new AudioDecodeException("WAVE file has no data chunk.");
        }

        if (isRf64 && rf64DataSize >= 0 && (dataLength == uint.MaxValue || dataLength == 0))
        {
            dataLength = rf64DataSize;
        }

        // A data chunk whose declared size runs past the end of the file is common in
        // recordings that were cut off. Truncating to what is actually there recovers the
        // audio; trusting the header reads garbage past the end.
        var available = stream.Length - dataOffset;
        if (dataLength < 0 || dataLength > available)
        {
            dataLength = available;
        }

        dataLength -= dataLength % format.BlockAlign;

        if (dataLength <= 0)
        {
            throw new AudioDecodeException("WAVE file contains no audio frames.");
        }

        return (format, dataOffset, dataLength);
    }

    private static WavFormat ParseFormatChunk(Stream stream, long size)
    {
        if (size < 16)
        {
            throw new AudioDecodeException($"fmt chunk is {size} bytes; at least 16 are required.");
        }

        var buffer = new byte[Math.Min(size, 40)];
        ReadExactly(stream, buffer, "fmt chunk");
        var span = buffer.AsSpan();

        var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(span[..2]);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(span[2..4]);
        var sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]);
        var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(span[14..16]);

        const int WaveFormatExtensible = 0xFFFE;
        if (formatTag == WaveFormatExtensible)
        {
            if (buffer.Length < 40)
            {
                throw new AudioDecodeException("WAVE_FORMAT_EXTENSIBLE fmt chunk is truncated.");
            }

            // The SubFormat GUID starts with the real format tag in its first two bytes.
            formatTag = BinaryPrimitives.ReadUInt16LittleEndian(span[24..26]);
        }

        if (channels is 0 or > 64)
        {
            throw new AudioDecodeException($"WAVE file declares {channels} channels.");
        }

        if (sampleRate is <= 0 or > 768_000)
        {
            throw new AudioDecodeException($"WAVE file declares a {sampleRate} Hz sample rate.");
        }

        const int WaveFormatPcm = 1;
        const int WaveFormatIeeeFloat = 3;

        var sampleFormat = (formatTag, bitsPerSample) switch
        {
            (WaveFormatPcm, 8) => WavSampleFormat.UnsignedPcm8,
            (WaveFormatPcm, 16) => WavSampleFormat.Pcm16,
            (WaveFormatPcm, 24) => WavSampleFormat.Pcm24,
            (WaveFormatPcm, 32) => WavSampleFormat.Pcm32,
            (WaveFormatIeeeFloat, 32) => WavSampleFormat.Float32,
            (WaveFormatIeeeFloat, 64) => WavSampleFormat.Float64,
            _ => throw new AudioDecodeException(
                $"Unsupported WAVE encoding: format tag 0x{formatTag:X4} at {bitsPerSample} bits per sample. " +
                "Compressed WAVE payloads (ADPCM, mu-law, GSM) are not handled by the managed reader."),
        };

        return new WavFormat
        {
            SampleRate = sampleRate,
            Channels = channels,
            SampleFormat = sampleFormat,
        };
    }

    public async IAsyncEnumerable<ReadOnlyMemory<float>> ReadAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_consumed)
        {
            throw new InvalidOperationException(
                "A WavAudioSource streams from a single stream position and can only be read once. Reopen the file.");
        }

        _consumed = true;
        _stream.Position = _dataOffset;

        var blockAlign = Format.BlockAlign;
        var bufferBytes = Math.Max(blockAlign, ReadBufferBytes - (ReadBufferBytes % blockAlign));
        var buffer = new byte[bufferBytes];
        var samples = new float[bufferBytes / blockAlign];

        var remaining = _dataLength;
        var carry = 0;

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();

            var want = (int)Math.Min(buffer.Length - carry, remaining);
            var read = await _stream.ReadAsync(buffer.AsMemory(carry, want), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            remaining -= read;
            var available = carry + read;
            var wholeFrames = available / blockAlign;
            var usable = wholeFrames * blockAlign;

            if (wholeFrames > 0)
            {
                Downmix(buffer.AsSpan(0, usable), samples.AsSpan(0, wholeFrames));
                yield return samples.AsMemory(0, wholeFrames);
            }

            carry = available - usable;
            if (carry > 0)
            {
                buffer.AsSpan(usable, carry).CopyTo(buffer);
            }
        }
    }

    private void Downmix(ReadOnlySpan<byte> source, Span<float> destination)
    {
        var channels = Format.Channels;
        var bytesPerSample = Format.BytesPerSample;
        var scale = 1f / channels;

        for (var frame = 0; frame < destination.Length; frame++)
        {
            var offset = frame * Format.BlockAlign;
            var sum = 0f;

            for (var channel = 0; channel < channels; channel++)
            {
                sum += ReadSample(source.Slice(offset + (channel * bytesPerSample), bytesPerSample));
            }

            destination[frame] = sum * scale;
        }
    }

    private float ReadSample(ReadOnlySpan<byte> bytes) => Format.SampleFormat switch
    {
        WavSampleFormat.UnsignedPcm8 => (bytes[0] - 128) / 128f,
        WavSampleFormat.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
        WavSampleFormat.Pcm24 => ReadInt24(bytes) / 8388608f,
        WavSampleFormat.Pcm32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
        WavSampleFormat.Float32 => Sanitise(BinaryPrimitives.ReadSingleLittleEndian(bytes)),
        WavSampleFormat.Float64 => Sanitise((float)BinaryPrimitives.ReadDoubleLittleEndian(bytes)),
        _ => 0f,
    };

    private static int ReadInt24(ReadOnlySpan<byte> bytes)
    {
        var value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);

        // Sign-extend from 24 bits.
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    /// <summary>
    /// Float WAVE files from broken encoders contain NaN and infinities. One of either turns a
    /// whole mel frame into NaN and the decode returns nothing, with no error anywhere.
    /// </summary>
    private static float Sanitise(float value) => float.IsFinite(value) ? value : 0f;

    public ValueTask DisposeAsync()
    {
        if (_leaveOpen)
        {
            return ValueTask.CompletedTask;
        }

        return _stream.DisposeAsync();
    }

    private static void ReadExactly(Stream stream, Span<byte> destination, string what)
    {
        var read = stream.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false);
        if (read != destination.Length)
        {
            throw new AudioDecodeException($"WAVE file ended while reading the {what}.");
        }
    }
}
