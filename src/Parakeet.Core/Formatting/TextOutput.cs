using System.Text;

namespace Parakeet.Core.Formatting;

/// <summary>How every text file this product writes is encoded: UTF-8, no byte order mark.</summary>
/// <remarks>
/// Named once and shared, because the encoding is a property of the output rather than of whichever
/// call happens to write it. <c>File.WriteAllText</c> with no encoding argument already writes UTF-8
/// without a mark, so a caller that says nothing is correct by default and a caller that spells the
/// encoding out as <c>Encoding.UTF8</c> — which emits one — is not. That difference is invisible: a
/// mark does not show in a diff, in a terminal, or in the string a formatter returns, and most
/// readers strip it silently, so the one format it breaks breaks alone and late.
/// <para>
/// RTTM is that format. Its first field is a record type, and NIST md-eval and pyannote both compare
/// it to the literal <c>SPEAKER</c>; behind a mark it matches nothing, and a reader whose tolerance
/// for unknown record types is a feature drops the first turn of the file and scores the rest
/// without a word. Hence <see cref="Utf8NoBom"/> at the writers rather than a default at each.
/// </para>
/// </remarks>
public static class TextOutput
{
    /// <summary>UTF-8 with no byte order mark.</summary>
    public static Encoding Utf8NoBom { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
