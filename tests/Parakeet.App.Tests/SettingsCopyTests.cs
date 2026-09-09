using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Parakeet.App.Views;

namespace Parakeet.App.Tests;

/// <summary>
/// No description under a setting runs past three lines of <i>this host's</i> layout — a coarse
/// guard, not the product's rule.
/// </summary>
/// <remarks>
/// <para>
/// The Settings pages explain each control underneath it, and the explanations had grown into
/// paragraphs — seven lines under one combo box, with sentences about torch device names, an
/// <c>--backend</c> flag and how much of a mixture-of-experts model is experts. A page of prose
/// under every row is a page nobody reads, and that is the rule this stands under.
/// </para>
/// <para>
/// <b>The count here is not the count a reader sees, and this test claimed it was until
/// 2026-09-07.</b> <see cref="TextLayout"/> does break the text at the width the control actually
/// got, but <c>TestAppBuilder</c> builds the default headless platform, whose text shaper is a
/// stub with synthetic glyph advances — so the width it breaks at is invented, and the line count
/// with it. It is still worth asserting, because the stub runs wide: a description that overflows
/// here has grown by a lot, and the check costs a CI second.
/// </para>
/// <para>
/// <b>The rendered rule is two lines, and <c>tools/measure-lines</c> is what measures it</b> — the
/// same window on a Skia-backed host with the embedded typefaces, every page rather than this one,
/// at the window's default width and at its minimum, and it refuses to report if it finds itself
/// on the stub shaper. AGENTS.md names the paths that owe it a run. This is the cheap floor under
/// it, which is why the number here is three and the number there is two.
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
