#if WINDOWS
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.Wave;
using Parakeet.Core.Audio;

namespace Parakeet.Audio;

/// <summary>
/// Decodes mp3, m4a, aac, mp4 and the other compressed containers through Media Foundation.
/// </summary>
/// <remarks>
/// <para>
/// Media Foundation decodes whatever codecs the machine has, which is why this path is not
/// portable and not exercised in CI. The managed WAVE reader is what the tests cover; this is
/// the Windows-only extension on top.
/// </para>
/// <para>
/// Which containers actually open depends on the installed codecs. Media Foundation on a
/// stock Windows install handles MP3, AAC/M4A and MP4; Matroska, WebM and FLAC depend on
/// codec packs and may fail, so the error is surfaced verbatim rather than swallowed.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationAudioSource : IAudioSource
{
    private const int FramesPerRead = 16384;

    private readonly MediaFoundationReader _reader;
    private readonly ISampleProvider _samples;
    private readonly int _channels;
    private bool _consumed;

    private MediaFoundationAudioSource(MediaFoundationReader reader)
    {
        _reader = reader;
        _samples = reader.ToSampleProvider();
        _channels = _samples.WaveFormat.Channels;
        SampleRate = _samples.WaveFormat.SampleRate;
        Duration = reader.TotalTime;
    }

    public int SampleRate { get; }

    public TimeSpan? Duration { get; }

    public static MediaFoundationAudioSource Open(string path, AudioFormatDetection detection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            return new MediaFoundationAudioSource(new MediaFoundationReader(path));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            throw new UnsupportedAudioFormatException(
                $"Media Foundation could not open '{Path.GetFileName(path)}' ({detection.Container}). " +
                "The container is recognised but this machine has no decoder for it. " +
                $"Underlying error: {ex.Message}",
                ex);
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<float>> ReadAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_consumed)
        {
            throw new InvalidOperationException(
                "A MediaFoundationAudioSource can only be read once. Reopen the file.");
        }

        _consumed = true;

        var interleaved = new float[FramesPerRead * _channels];
        var mono = new float[FramesPerRead];
        var scale = 1f / _channels;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // The provider is synchronous; keeping the read off the calling thread stops a slow
            // disk or a network share from stalling whatever is consuming the segments.
            var read = await Task.Run(() => _samples.Read(interleaved, 0, interleaved.Length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var frames = read / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                var offset = frame * _channels;
                for (var channel = 0; channel < _channels; channel++)
                {
                    var value = interleaved[offset + channel];
                    sum += float.IsFinite(value) ? value : 0f;
                }

                mono[frame] = sum * scale;
            }

            yield return mono.AsMemory(0, frames);
        }
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        return ValueTask.CompletedTask;
    }
}
#endif
