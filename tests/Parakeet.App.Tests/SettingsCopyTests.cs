using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Parakeet.App.Views;

namespace Parakeet.App.Tests;

/// <summary>
/// No description under a setting runs past three lines, measured on what the window draws.
/// </summary>
/// <remarks>
/// <para>
/// The Settings pages explain each control underneath it, and the explanations had grown into
/// paragraphs — seven lines under one combo box, with sentences about torch device names, an
/// <c>--backend</c> flag and how much of a mixture-of-experts model is experts. A page of prose
/// under every row is a page nobody reads, so the rule is three lines and this is what holds it.
/// </para>
/// <para>
/// <b>Counted off the rendered layout rather than off the string.</b> A character budget is a
/// guess about a font: it has to assume an average glyph width, and it is wrong for a line of
/// capitals and wrong again if the font size ever moves. <see cref="TextLayout"/> has already
/// broken the text at the width the control actually got, so its line count is the number a reader
/// would see.
/// </para>
/// </remarks>
public class SettingsCopyTests
{
    /// <summary>What the design allows under one control.</summary>
    private const int MostLines = 3;

    [AvaloniaTheory]
    [InlineData(5)] // Settings
    public void NoSettingDescriptionRunsPastThreeLines(int tab)
    {
        var viewModel = WindowTests.NewViewModel(out _);
        viewModel.SelectedTab = tab;

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        // Both halves: the pill switcher swaps the pages, and a page that is not current has never
        // been measured, so an over-long line on Advanced would hide behind General.
        var offenders = new List<string>();
        var examined = 0;

        foreach (var index in new[] { 0, 1 })
        {
            var pages = window.GetVisualDescendants().OfType<TabControl>()
                .FirstOrDefault(control => control.Name == "SettingsSubTabs");
            if (pages is null)
            {
                continue;
            }

            pages.SelectedIndex = index;
            window.UpdateLayout();

            foreach (var block in window.GetVisualDescendants().OfType<TextBlock>())
            {
                // The explanations are the wrapped ones. A label, a heading or a value does not
                // wrap and is not what this rule is about.
                if (block.TextWrapping == TextWrapping.NoWrap || string.IsNullOrWhiteSpace(block.Text))
                {
                    continue;
                }

                examined++;

                var drawn = block.TextLayout.TextLines.Count;
                if (drawn > MostLines)
                {
                    offenders.Add($"{drawn} lines: {block.Text[..Math.Min(70, block.Text.Length)]}…");
                }
            }
        }

        // **A rule that inspects nothing passes.** If the page stops being reachable from here —
        // a renamed TabControl, a switcher that no longer swaps these two — every description on
        // it could grow to a page and this test would still be green, which is the failure mode it
        // exists to prevent. The number only has to be low enough not to be brittle and high
        // enough to prove the page was found.
        Assert.True(examined >= 8, $"only {examined} wrapped descriptions were found on Settings");

        Assert.True(
            offenders.Count == 0,
            "these run past " + MostLines + " lines:\n" + string.Join("\n", offenders.Distinct()));

        window.Close();
    }
}
