using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Parakeet.App.Tests;

/// <summary>
/// Holds every speaker chip's text against its own background at WCAG 2.1 AA.
/// </summary>
/// <remarks>
/// <para>
/// <b>The palette grew from four chips to eight on 2026-08-27</b>, when the diariser that had a
/// four-speaker cap was retired and the clustering pipeline that replaced it turned a fifth speaker
/// from impossible into ordinary. Four of the eight are matcha, in the two treatments the design
/// already had; four are hues admitted for this one job. Nobody looked at them on a screen.
/// </para>
/// <para>
/// <b>So the readability claim is computed rather than eyeballed, and it is computed from the files
/// themselves.</b> The pairs come out of <c>Theme/Controls.axaml</c> and the hexes out of
/// <c>Theme/Tokens.axaml</c>, so a later edit to either — a foreground darkened, a fill lightened,
/// a chip repointed at a different brush — is caught here rather than in a screenshot nobody takes.
/// Reading the files is also what makes this test fail loudly if the shape it depends on is
/// reworded, which is the failure mode a hard-coded table would hide.
/// </para>
/// <para>
/// <b>A chip with no background of its own is judged against the page.</b> Two of the matcha four
/// are outlines drawn straight onto <c>Ground</c>, which is what their text actually sits on; a
/// border colour is not a background and is deliberately not treated as one.
/// </para>
/// </remarks>
public class SpeakerChipContrastTests
{
    /// <summary>AA for normal text. The chips are 12px and not bold, so the 3:1 large-text bar does not apply.</summary>
    private const double Minimum = 4.5;

    [Fact]
    public void EveryChipsTextIsLegibleOnItsOwnBackground()
    {
        var tokens = ReadTokens();
        var chips = ReadChips();

        // The count is asserted so that a ninth chip added without a token cannot pass by not being
        // looked at, and so that a chip quietly deleted fails rather than shrinking the set.
        Assert.Equal(8, chips.Count);

        foreach (var (index, background, _, foreground) in chips)
        {
            var bg = tokens[background];
            var fg = tokens[foreground];
            var ratio = Contrast(bg, fg);

            Assert.True(
                ratio >= Minimum,
                FormattableString.Invariant(
                    $"chip{index}: {foreground} on {background} is {ratio:0.00}:1, below the {Minimum:0.0}:1 this window holds text to. Either the token moved or the chip was repointed."));
        }
    }

    [Fact]
    public void NoTwoChipsLookAlike()
    {
        // What the palette is *for*. Contrast says each chip can be read; this says a reader can
        // tell them apart, which is the whole reason the set grew rather than kept wrapping at four.
        //
        // **Fill and edge together, because two of them have no fill.** chip2 and chip3 are outlines
        // drawn straight onto the page, so they share a background and are separated by their border
        // alone — asserting on backgrounds alone reports that as a collision, which it is not. What
        // has to be unique is the pair.
        var chips = ReadChips();
        var appearances = chips.Select(chip => $"{chip.Background}/{chip.Border ?? "none"}").ToList();

        Assert.Equal(appearances.Count, appearances.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Every <c>&lt;Color x:Key="…"&gt;#RRGGBB&lt;/Color&gt;</c> in the theme's token file.
    /// </summary>
    private static Dictionary<string, (int R, int G, int B)> ReadTokens()
    {
        var document = XDocument.Load(Path.Combine(ThemeDirectory, "Tokens.axaml"));
        var tokens = new Dictionary<string, (int, int, int)>(StringComparer.Ordinal);

        foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "Color"))
        {
            var key = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;
            var value = element.Value.Trim();
            if (key is null || !value.StartsWith('#') || value.Length != 7)
            {
                continue;
            }

            tokens[key] = (
                int.Parse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        Assert.NotEmpty(tokens);
        return tokens;
    }

    /// <summary>
    /// Each chip's background token and its text token, read out of the control styles.
    /// </summary>
    /// <remarks>
    /// A chip is two style blocks — <c>Border.speaker.chipN</c> carrying a Background or a
    /// BorderBrush, and <c>Border.speaker.chipN TextBlock</c> carrying a Foreground. The outline
    /// chips set no Background at all, and <c>Ground</c> is what their text is really on.
    /// </remarks>
    private static List<(int Index, string Background, string? Border, string Foreground)> ReadChips()
    {
        var xaml = File.ReadAllText(Path.Combine(ThemeDirectory, "Controls.axaml"));

        var backgrounds = new Dictionary<int, string>();
        var borders = new Dictionary<int, string?>();
        var foregrounds = new Dictionary<int, string>();

        var blocks = Regex.Matches(
            xaml,
            """<Style Selector="Border\.speaker\.chip(?<n>\d+)(?<text>\s+TextBlock)?">(?<body>.*?)</Style>""",
            RegexOptions.Singleline);

        foreach (Match block in blocks)
        {
            var index = int.Parse(block.Groups["n"].Value, CultureInfo.InvariantCulture);
            var body = block.Groups["body"].Value;

            if (block.Groups["text"].Success)
            {
                foregrounds[index] = Resource(body, "Foreground");
                continue;
            }

            // No Background setter means an outline chip, whose text sits on the page itself.
            backgrounds[index] = TryResource(body, "Background") ?? "Ground";
            borders[index] = TryResource(body, "BorderBrush");
        }

        Assert.NotEmpty(backgrounds);
        Assert.Equal(backgrounds.Keys.Order(), foregrounds.Keys.Order());

        return backgrounds.Keys
            .Order()
            .Select(index => (index, backgrounds[index], borders[index], foregrounds[index]))
            .ToList();
    }

    private static string Resource(string body, string property) =>
        TryResource(body, property)
        ?? throw new InvalidOperationException($"no {property} setter found in a chip style block");

    /// <summary>The token name behind <c>Value="{DynamicResource FooBrush}"</c>, without the suffix.</summary>
    private static string? TryResource(string body, string property)
    {
        var match = Regex.Match(
            body,
            $$"""<Setter\s+Property="{{property}}"\s+Value="\{DynamicResource\s+(?<brush>\w+)Brush\}"\s*/>""");

        return match.Success ? match.Groups["brush"].Value : null;
    }

    /// <summary>WCAG 2.1's contrast ratio, which is a ratio of relative luminances and not of hexes.</summary>
    private static double Contrast((int R, int G, int B) a, (int R, int G, int B) b)
    {
        var (high, low) = (Luminance(a), Luminance(b));
        if (high < low)
        {
            (high, low) = (low, high);
        }

        return (high + 0.05) / (low + 0.05);
    }

    private static double Luminance((int R, int G, int B) colour)
    {
        static double Channel(int value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(colour.R))
             + (0.7152 * Channel(colour.G))
             + (0.0722 * Channel(colour.B));
    }

    /// <summary>The theme directory, found by walking up from the test binary to the solution.</summary>
    private static string ThemeDirectory
    {
        get
        {
            var directory = AppContext.BaseDirectory;
            while (directory is not null && !File.Exists(Path.Combine(directory, "Uindosill.slnx")))
            {
                directory = Path.GetDirectoryName(directory);
            }

            Assert.NotNull(directory);
            return Path.Combine(directory, "src", "Parakeet.App", "Theme");
        }
    }
}
