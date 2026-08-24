using System.Runtime.CompilerServices;

namespace Parakeet.Engine.LlamaServer;

/// <summary>
/// Reads the <c>data:</c> payloads out of a server-sent-event stream — the shape
/// <c>llama-server</c> streams completions in: one <c>data: {json}</c> line per chunk, events
/// separated by a blank line.
/// </summary>
/// <remarks>
/// Deliberately small and deliberately not an SSE library: the server emits only `data:` lines,
/// so event names, ids and retry hints are out of scope, and comment lines (leading `:`) and
/// unknown fields are skipped rather than errors — the SSE contract says unknown fields are
/// ignorable. A payload split across several `data:` lines in one event is joined with a
/// newline, per the spec, though the server is not known to produce one.
/// </remarks>
internal static class SseReader
{
    public static async IAsyncEnumerable<string> ReadPayloadsAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream);
        var payload = new List<string>();

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (payload.Count > 0)
                {
                    yield return string.Join('\n', payload);
                    payload.Clear();
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line[5..];
                payload.Add(value.StartsWith(' ') ? value[1..] : value);
            }
        }

        if (payload.Count > 0)
        {
            yield return string.Join('\n', payload);
        }
    }
}
