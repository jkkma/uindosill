namespace Parakeet.Core.Licensing;

/// <summary>
/// An MIT notice package for redistributed weights or a graph.
/// </summary>
/// <remarks>
/// MIT asks for less than CC BY's seven elements and more than a licence name: the copyright notice
/// and the permission text — "the above copyright notice and this permission notice shall be
/// included in all copies or substantial portions" — travel with the material. So this carries the
/// copyright line and names the file the permission text ships in, which <c>Licences.targets</c>
/// copies into every build output; a record that cannot be constructed without both cannot ship
/// with a licence name and nothing behind it. The fourth licence shape in
/// <see cref="Attributions"/>, added 2026-08-23 for the speech-detection graph.
/// </remarks>
public sealed record MitAttribution : IModelAttribution
{
    public required string Title { get; init; }

    public required string Creator { get; init; }

    /// <summary>The copyright line as the upstream LICENSE carries it.</summary>
    public required string CopyrightNotice { get; init; }

    /// <summary>Where the permission text ships, relative to the repository root and the build output.</summary>
    public required string LicencePath { get; init; }

    public required Uri LicenceUri { get; init; }

    public required Uri MaterialUri { get; init; }

    /// <summary>Whether the material was modified, stated either way rather than left to be assumed.</summary>
    public required string ModificationNotice { get; init; }

    public string ToPlainText(string newLine = "\n")
    {
        ArgumentNullException.ThrowIfNull(newLine);

        return string.Join(newLine,
        [
            Title,
            $"Creator: {Creator}",
            $"Copyright: {CopyrightNotice}",
            $"Licence: MIT License, {LicenceUri} - the permission notice and disclaimer ship with this application at {LicencePath}.",
            $"Source: {MaterialUri}",
            $"Modifications: {ModificationNotice}",
        ]);
    }
}
