namespace Parakeet.Core.Models;

/// <summary>
/// A file in the weights repository whose digest is recorded now so that a later version does not
/// have to re-derive it, and which this version deliberately cannot install.
/// </summary>
/// <remarks>
/// <para>
/// This is not a <see cref="ModelDescriptor"/> and cannot become one implicitly. A descriptor
/// carries a licence and an attribution id that must resolve against
/// <c>Attribution.ById</c>, because the application shows that notice to the user and the CC BY
/// obligations are not satisfied by attribution alone. These pins record a file name, a byte size
/// and a SHA-256 — facts read from the repository's own listing — and nothing else, because
/// nothing else about them has been established.
/// </para>
/// <para>
/// Promoting one is a deliberate act: establish the licence, register the attribution, move the
/// entry into <c>models</c>, and only then can it be selected or downloaded. Until then the
/// digest is worth having and the silence about everything else is the point.
/// </para>
/// </remarks>
public sealed record DeferredModelPin
{
    public required string Id { get; init; }

    public required string Family { get; init; }

    public required string FileName { get; init; }

    public required Uri Url { get; init; }

    /// <summary>Exact size from the repository listing, for comparison against a download.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>The LFS object id, which for this repository is the SHA-256 of the blob.</summary>
    public required string Sha256 { get; init; }

    /// <summary>What a later version would use this for. Not a claim that it works.</summary>
    public required string Purpose { get; init; }
}
