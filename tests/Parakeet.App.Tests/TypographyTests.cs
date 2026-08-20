using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace Parakeet.App.Tests;

/// <summary>
/// The typefaces the interface is drawn in, held to the two things that go wrong silently with an
/// embedded font: resolving to a different family, and resolving to a synthesised weight.
/// </summary>
/// <remarks>
/// <para>
/// A missing font does not throw. It falls back, and the window renders in whatever the platform
/// offers — which on a developer's machine is usually close enough that nobody notices until a
/// screenshot goes out. The design this window implements hit that failure from the other side:
/// every layout number in it had to be measured with the webfonts <em>confirmed</em> loaded,
/// because an unloaded face measures the fallback and each figure comes out wrong.
/// </para>
/// <para>
/// Two specific hazards, both found by measuring rather than by reading, and both pinned below.
/// </para>
/// <para>
/// <b>The family name disagrees with itself.</b> Upstream publishes these as variable fonts; what
/// ships is the static instances, and those carry two different names. The 500 weight's legacy
/// family (name ID 1) is "Instrument Sans Medium" while its typographic family (name ID 16) is
/// "Instrument Sans"; the 400 and 700 weights have no typographic name at all, so theirs is empty
/// and the legacy name is already right. Avalonia 12.1.1 groups by the typographic name where
/// there is one, which is what lets a single <c>#Instrument Sans</c> reference carry four weights
/// — but that is a property of this version, not a guarantee, so it is asserted.
/// </para>
/// <para>
/// <b>A weight that is not shipped is invented rather than refused.</b> Ask for a bold that is not
/// in the folder and Avalonia returns the nearest face with <see cref="FontSimulations.Bold"/>
/// set: a real glyph typeface, at the requested weight, algorithmically smeared. Nothing about the
/// call fails. Chivo Mono 600 was reaching the window that way until the face was added — the
/// licence notices draw their headings at 600 — so every weight the design names is checked here
/// for <see cref="FontSimulations.None"/>.
/// </para>
/// </remarks>
public class TypographyTests
{
    // Kept in step with App.axaml. If a URI moves, these fail rather than the window quietly
    // rendering in Segoe UI.
    private const string SansUri = "avares://Uindosill/Assets/Fonts#Instrument Sans";
    private const string MonoUri = "avares://Uindosill/Assets/Fonts#Chivo Mono";

    [AvaloniaTheory]
    // The weights the design names: 500 body, 600 titles, 700 emphasis and section headings.
    // 400 is here because a control that never asks for a weight inherits it.
    [InlineData(400)]
    [InlineData(500)]
    [InlineData(600)]
    [InlineData(700)]
    public void TheSansIsEmbeddedAndRealAtEveryWeightTheDesignDraws(int weight)
        => AssertResolvesExactly(SansUri, "Instrument Sans", weight);

    [AvaloniaTheory]
    // Monospace is only for text you copy — paths, extensions, hex, licence notices — so it needs
    // fewer weights: 500 for the body of a notice, 600 for its headings.
    [InlineData(400)]
    [InlineData(500)]
    [InlineData(600)]
    public void TheMonospaceIsEmbeddedAndRealAtEveryWeightTheDesignDraws(int weight)
        => AssertResolvesExactly(MonoUri, "Chivo Mono", weight);

    [AvaloniaFact]
    public void TheMonospaceIsActuallyMonospaced()
    {
        // Chivo Mono was chosen over nineteen other families by inspecting a single glyph, so the
        // least this suite can do is confirm the file that arrived is the fixed-pitch face and not
        // a proportional one wearing the name. In a monospace every advance is the same one.
        var typeface = new Typeface(new FontFamily(MonoUri), FontStyle.Normal, FontWeight.Medium);
        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var face));

        var advances = new List<ushort>();
        foreach (var c in "0Wil1M.")
        {
            Assert.True(face!.CharacterToGlyphMap.TryGetGlyph(c, out var glyph), $"no glyph for '{c}'");
            Assert.True(face.TryGetHorizontalGlyphAdvance(glyph, out var advance));
            advances.Add(advance);
        }

        Assert.Single(advances.Distinct());
    }

    /// <summary>
    /// Resolves a typeface and insists it is the real thing: right family, right weight, and
    /// nothing synthesised.
    /// </summary>
    private static void AssertResolvesExactly(string uri, string expectedFamily, int weight)
    {
        var typeface = new Typeface(new FontFamily(uri), FontStyle.Normal, (FontWeight)weight);

        // This is what a TextBlock does. It returning true is necessary and nowhere near
        // sufficient — a fallback resolves successfully to the wrong face.
        Assert.True(
            FontManager.Current.TryGetGlyphTypeface(typeface, out var face),
            $"{expectedFamily} {weight} did not resolve to a glyph typeface at all.");

        // The static instances split their identity across two name records: the typographic
        // family where the face has one, the legacy family where it does not.
        var family = string.IsNullOrEmpty(face!.TypographicFamilyName)
            ? face.FamilyName
            : face.TypographicFamilyName;

        Assert.Equal(expectedFamily, family);
        Assert.Equal(weight, (int)face.Weight);

        // The assertion that catches a face that is missing from the folder, which is the failure
        // that otherwise reaches a release looking merely a little heavy.
        Assert.Equal(FontSimulations.None, face.FontSimulations);
    }
}
