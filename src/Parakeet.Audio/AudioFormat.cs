using System.Text;

namespace Parakeet.Audio;

public enum AudioContainer
{
    Unknown,

    /// <summary>RIFF/WAVE, including the RF64 and BW64 64-bit variants.</summary>
    Wav,

    Mp3,

    /// <summary>ISO base media: mp4, m4a, m4v, mov.</summary>
    Mp4,

    /// <summary>Raw ADTS/ADIF AAC.</summary>
    Aac,

    Flac,

    Ogg,

    /// <summary>Matroska and WebM.</summary>
    Matroska,

    Aiff,

    /// <summary>Windows Media (ASF).</summary>
    Asf,
}

public sealed record AudioFormatDetection
{
    public required AudioContainer Container { get; init; }

    /// <summary>What the file extension claimed, lowercase with the dot, or null.</summary>
    public string? ExtensionHint { get; init; }

    /// <summary>True when the extension and the magic bytes disagree.</summary>
    public bool ExtensionMismatch { get; init; }

    public bool IsSupportedEverywhere => Container == AudioContainer.Wav;
}

/// <summary>
/// Identifies containers by their leading bytes.
/// </summary>
/// <remarks>
/// Extension-only detection is not enough: people rename files, export tools write .wav
/// extensions onto mp3 data, and a mis-parse of a renamed file produces noise rather than an
/// error. The extension is kept as a hint so the mismatch can be reported.
/// </remarks>
public static class AudioFormatSniffer
{
    /// <summary>Bytes needed to make a determination.</summary>
    public const int HeaderBytes = 16;

    public static AudioFormatDetection Detect(ReadOnlySpan<byte> header, string? fileName = null)
    {
        var container = DetectContainer(header);
        var extension = fileName is null ? null : Path.GetExtension(fileName).ToLowerInvariant();
        var extensionContainer = extension is null ? AudioContainer.Unknown : FromExtension(extension);

        return new AudioFormatDetection
        {
            Container = container,
            ExtensionHint = string.IsNullOrEmpty(extension) ? null : extension,
            ExtensionMismatch = container != AudioContainer.Unknown
                && extensionContainer != AudioContainer.Unknown
                && extensionContainer != container,
        };
    }

    public static AudioFormatDetection DetectFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[HeaderBytes];
        var read = stream.ReadAtLeast(header, HeaderBytes, throwOnEndOfStream: false);
        return Detect(header[..read], path);
    }

    private static AudioContainer DetectContainer(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
        {
            return AudioContainer.Unknown;
        }

        if (Matches(header, "RIFF") || Matches(header, "RF64") || Matches(header, "BW64"))
        {
            // RIFF also carries AVI and other payloads; the WAVE tag at offset 8 is the check.
            if (header.Length >= 12 && Matches(header[8..], "WAVE"))
            {
                return AudioContainer.Wav;
            }

            return header.Length >= 12 ? AudioContainer.Unknown : AudioContainer.Wav;
        }

        if (Matches(header, "fLaC"))
        {
            return AudioContainer.Flac;
        }

        if (Matches(header, "OggS"))
        {
            return AudioContainer.Ogg;
        }

        if (Matches(header, "FORM") && header.Length >= 12
            && (Matches(header[8..], "AIFF") || Matches(header[8..], "AIFC")))
        {
            return AudioContainer.Aiff;
        }

        if (header.Length >= 8 && Matches(header[4..], "ftyp"))
        {
            return AudioContainer.Mp4;
        }

        if (header is [0x1A, 0x45, 0xDF, 0xA3, ..])
        {
            return AudioContainer.Matroska;
        }

        if (header is [0x30, 0x26, 0xB2, 0x75, ..])
        {
            return AudioContainer.Asf;
        }

        if (Matches(header, "ID3"))
        {
            return AudioContainer.Mp3;
        }

        if (Matches(header, "ADIF"))
        {
            return AudioContainer.Aac;
        }

        if (header.Length >= 2 && header[0] == 0xFF)
        {
            // 12 sync bits then the layer field: MPEG audio layers I–III are mp3-ish, and layer
            // bits of zero under a full 0xFFF sync are ADTS AAC. That is four second bytes, not
            // two — 0xF0/0xF8 are the CRC-protected variants (protection_absent is the low bit)
            // of the 0xF1/0xF9 this read until 2026-08-29, and a protected stream classified by
            // the 0xE0 catch-all below was reported as a renamed mp3 that never was.
            var second = header[1];
            if ((second & 0xF6) is 0xF0 or 0xF2 or 0xF4 or 0xF6 && (second & 0x06) != 0x00)
            {
                return AudioContainer.Mp3;
            }

            if ((second & 0xF6) == 0xF0)
            {
                return AudioContainer.Aac;
            }

            if ((second & 0xE0) == 0xE0)
            {
                return AudioContainer.Mp3;
            }
        }

        return AudioContainer.Unknown;
    }

    private static AudioContainer FromExtension(string extension) => extension switch
    {
        ".wav" or ".wave" or ".rf64" or ".bwf" or ".w64" => AudioContainer.Wav,
        ".mp3" => AudioContainer.Mp3,
        ".m4a" or ".mp4" or ".m4v" or ".mov" or ".m4b" => AudioContainer.Mp4,
        ".aac" => AudioContainer.Aac,
        ".flac" => AudioContainer.Flac,
        ".ogg" or ".oga" or ".opus" => AudioContainer.Ogg,
        ".mkv" or ".webm" or ".mka" => AudioContainer.Matroska,
        ".aif" or ".aiff" or ".aifc" => AudioContainer.Aiff,
        ".wma" or ".asf" or ".wmv" => AudioContainer.Asf,
        _ => AudioContainer.Unknown,
    };

    private static bool Matches(ReadOnlySpan<byte> header, string ascii)
    {
        if (header.Length < ascii.Length)
        {
            return false;
        }

        for (var i = 0; i < ascii.Length; i++)
        {
            if (header[i] != (byte)ascii[i])
            {
                return false;
            }
        }

        return true;
    }

    internal static string FourCc(ReadOnlySpan<byte> value) => Encoding.ASCII.GetString(value);
}
