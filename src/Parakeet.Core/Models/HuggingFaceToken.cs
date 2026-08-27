namespace Parakeet.Core.Models;

/// <summary>
/// Where a Hugging Face access token comes from, for the one catalogue entry that needs one.
/// </summary>
/// <remarks>
/// <para>
/// <b>One entry, and it is the reason this type exists at all.</b> Everything else in the catalogue
/// downloads anonymously. `pyannote/speaker-diarization-community-1` is a gated repository: its
/// files return 401 without an accepted user agreement, and the agreement is between the user and
/// the model's authors, so there is no token this product could carry for them.
/// </para>
/// <para>
/// <b>The environment is preferred over the settings file, deliberately.</b> <c>HF_TOKEN</c> is the
/// variable Hugging Face's own libraries read, so a machine already set up for the hub needs
/// nothing pasted anywhere — and a token that lives only in the environment never reaches a file
/// this product writes, which is the better outcome for a credential. The settings file is the
/// fallback for the ordinary case of somebody with no such setup.
/// </para>
/// <para>
/// <b>Blank is not a token.</b> An empty or whitespace value in either place is treated as absent
/// rather than sent as an empty bearer credential, which the hub answers with a 401 that reads as
/// "your token is wrong" rather than as "you have not set one".
/// </para>
/// </remarks>
public static class HuggingFaceToken
{
    /// <summary>The variable Hugging Face's own tooling reads, checked first.</summary>
    public const string PrimaryVariable = "HF_TOKEN";

    /// <summary>
    /// The older name for the same thing, still exported by plenty of existing setups and still
    /// honoured by the hub's libraries. Checked second so that a machine setting both gets the
    /// current name's value.
    /// </summary>
    public const string LegacyVariable = "HUGGING_FACE_HUB_TOKEN";

    /// <summary>
    /// The token to use, or null when neither the environment nor <paramref name="stored"/> has one.
    /// </summary>
    /// <param name="stored">
    /// What the user pasted into settings, or null. Ignored when the environment supplies a token.
    /// </param>
    public static string? Resolve(string? stored = null)
    {
        foreach (var variable in new[] { PrimaryVariable, LegacyVariable })
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment.Trim();
            }
        }

        return string.IsNullOrWhiteSpace(stored) ? null : stored.Trim();
    }
}
