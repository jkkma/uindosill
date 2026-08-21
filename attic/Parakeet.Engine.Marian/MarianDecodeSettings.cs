namespace Parakeet.Engine.Marian;

/// <summary>
/// The decode every published translation figure was produced with.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are not defaults in the usual sense.</b> The graphs are pinned and the search over them
/// is not, so every field here is a degree of freedom that changes the English and would quietly
/// stop it being the thing that was measured. They are set to what the 2026-08-20 gate run passed —
/// 8,149 sentences across 24 languages — and changing one is a decision to be recorded rather than
/// a knob to be turned.
/// </para>
/// <para>
/// <b>Six beams, and the file says four.</b> <c>generation_config.json</c> declares
/// <c>num_beams: 4</c>; nothing this project has measured used it. The 2026-08-19 spike measured
/// greedy against beam-6 over 44 real segments and found greedy dropping content beam-6 keeps, at
/// 2.1× to 2.3× less time, and the gate was then scored at six. Reading the file and trusting it
/// would ship a decode nobody scored.
/// </para>
/// <para>
/// <b>Length penalty 1.0 and early stopping off</b> are HuggingFace's own defaults, which is
/// precisely why they are written down here: they were never chosen, they were inherited, and an
/// inherited value that nobody wrote down is one somebody later changes thinking it was arbitrary.
/// Together they mean a finished hypothesis is scored by its mean log probability per token, and
/// that the search runs on while an open beam could still beat the worst finished one.
/// </para>
/// </remarks>
internal sealed record MarianDecodeSettings
{
    public static MarianDecodeSettings Default { get; } = new();

    /// <summary>Beam width. Six — see the type's remarks, and do not take it from the config file.</summary>
    public int Beams { get; init; } = 6;

    /// <summary>
    /// The longest continuation, in tokens. 512, matching the <c>max_new_tokens=512</c> the gate run
    /// passed rather than the <c>max_length</c> in the config, which counts differently.
    /// </summary>
    public int MaxNewTokens { get; init; } = 512;

    /// <summary>Exponent on the length a finished hypothesis is divided by. 1.0.</summary>
    public float LengthPenalty { get; init; } = 1.0f;

    /// <summary>
    /// Whether to stop as soon as every beam has finished. False, which is HuggingFace's default
    /// and means the loop instead runs until no open beam can improve on the finished set.
    /// </summary>
    public bool EarlyStopping { get; init; }

    public void Validate()
    {
        if (Beams < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Beams), Beams, "A search needs at least one beam.");
        }

        if (MaxNewTokens < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxNewTokens), MaxNewTokens, "A decode that may emit nothing translates nothing.");
        }
    }
}
