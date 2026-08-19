using System.Text.Json;

namespace Parakeet.Engine.Sortformer.Tests;

/// <summary>
/// Reads what the reference implementation produced, from
/// <c>tests/fixtures/diarisation/sortformer/</c>.
/// </summary>
/// <remarks>
/// Written by <c>scripts/make-diariser-fixtures.py</c>, never by hand: it imports NVIDIA's own
/// <c>SortformerModules</c> and NeMo's own <c>FilterbankFeatures</c> and records what they returned.
/// CI never runs that script — it has no weights, no network and no Python — which is the point:
/// the check travels as data.
/// </remarks>
internal static class Fixtures
{
    public static string Directory { get; } = Find();

    public static JsonDocument Manifest() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, "expected.json")));

    /// <summary>Reads a little-endian float32 file whole.</summary>
    public static float[] ReadFloats(string fileName)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Directory, fileName));
        var values = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BitConverter.ToSingle(bytes, i * sizeof(float));
        }

        return values;
    }

    private static string Find()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "tests", "fixtures", "diarisation", "sortformer");
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException("tests/fixtures/diarisation/sortformer was not found above the test binary.");
    }
}

/// <summary>Comparison helpers that report the worst deviation rather than the first one.</summary>
internal static class Deviation
{
    /// <summary>
    /// The largest absolute difference between two equally-sized blocks, and where it was.
    /// </summary>
    /// <remarks>
    /// A first-failure assertion answers "is it wrong"; these fixtures need "how wrong", because
    /// the port is not bit-identical to the reference by construction — it computes in a different
    /// precision, in a different order, on a different runtime's <c>log</c>. The number this returns
    /// is the evidence, and the tests assert on its size.
    /// </remarks>
    public static (double Worst, int Index) Max(ReadOnlySpan<float> actual, ReadOnlySpan<float> expected)
    {
        if (actual.Length != expected.Length)
        {
            throw new ArgumentException($"Length {actual.Length} against {expected.Length}.", nameof(actual));
        }

        var worst = 0.0;
        var at = -1;
        for (var i = 0; i < actual.Length; i++)
        {
            var difference = Math.Abs((double)actual[i] - expected[i]);
            if (difference > worst)
            {
                worst = difference;
                at = i;
            }
        }

        return (worst, at);
    }

    public static void Within(ReadOnlySpan<float> actual, ReadOnlySpan<float> expected, double tolerance, string what)
    {
        var (worst, at) = Max(actual, expected);
        Assert.True(
            worst <= tolerance,
            $"{what}: worst deviation {worst:g6} at index {at} " +
            $"(got {(at >= 0 ? actual[at] : 0)}, expected {(at >= 0 ? expected[at] : 0)}), tolerance {tolerance:g6}.");
    }
}
