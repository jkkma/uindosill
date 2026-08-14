using Parakeet.Core.Audio;

namespace Parakeet.Audio;

public sealed class UnsupportedAudioFormatException : AudioDecodeException
{
    public UnsupportedAudioFormatException(string message)
        : base(message)
    {
    }

    public UnsupportedAudioFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UnsupportedAudioFormatException()
    {
    }
}

/// <summary>Opens an audio or video file as mono float32 PCM.</summary>
public static class AudioSources
{
    /// <summary>
    /// Opens a file, choosing a reader from its magic bytes rather than its extension.
    /// </summary>
    /// <remarks>
    /// WAVE always goes through the managed reader, on every platform: it handles RF64, odd bit
    /// depths and truncated data chunks predictably, and it is the path CI can exercise.
    /// Everything else needs Media Foundation and therefore Windows.
    /// </remarks>
    public static IAudioSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Audio file not found: {path}", path);
        }

        var detection = AudioFormatSniffer.DetectFile(path);

        if (detection.Container == AudioContainer.Wav)
        {
            return WavAudioSource.Open(path);
        }

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return MediaFoundationAudioSource.Open(path, detection);
        }
#endif

        throw new UnsupportedAudioFormatException(Explain(path, detection));
    }

    /// <summary>Formats this build can open, for help text and file pickers.</summary>
    public static IReadOnlyList<string> SupportedExtensions
    {
        get
        {
#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                return
                [
                    ".wav", ".wave", ".rf64", ".bwf",
                    ".mp3", ".m4a", ".m4b", ".aac", ".mp4", ".m4v", ".mov", ".wma", ".asf", ".wmv",
                ];
            }
#endif
            return [".wav", ".wave", ".rf64", ".bwf"];
        }
    }

    private static string Explain(string path, AudioFormatDetection detection)
    {
        var name = Path.GetFileName(path);

        if (detection.Container == AudioContainer.Unknown)
        {
            return $"'{name}' does not start with any audio container this build recognises. " +
                   "The first bytes match neither RIFF/WAVE, MPEG, ISO base media, Ogg, FLAC, Matroska nor ASF.";
        }

        var mismatch = detection.ExtensionMismatch
            ? $" (its extension says {detection.ExtensionHint} but its contents are {detection.Container})"
            : string.Empty;

        return $"'{name}' is {detection.Container}{mismatch}. This build decodes only WAVE; " +
               "compressed containers need Media Foundation, which exists only on Windows.";
    }
}
