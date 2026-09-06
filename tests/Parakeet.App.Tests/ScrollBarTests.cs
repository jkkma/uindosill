using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Parakeet.App.Views;

namespace Parakeet.App.Tests;

/// <summary>
/// The scroll bars are drawn in the palette's greys rather than the toolkit's.
/// </summary>
/// <remarks>
/// Fluent's thumb is #7A7A7A, and at rest it is the only part of a bar that shows: three units
/// wide down the edge of every pane that scrolls, dark enough to read as a stray rule beside the
/// transcript. The override is a handful of resource keys in <c>Theme/Tokens.axaml</c>, and a key
/// that does not exist in the shipped theme loads without complaint and changes nothing — the
/// failure that file's own header warns about — so this asserts on the brush the thumb actually
/// carries, resolved against the token it is meant to be, rather than on the file.
/// </remarks>
public class ScrollBarTests
{
    [AvaloniaFact]
    public void TheThumbTakesTheDisabledTextGreyAtRest()
    {
        var viewModel = WindowTests.NewViewModel(out _);
        viewModel.Transcribe.Jobs.Add(LongTranscript());
        viewModel.SelectedTab = 4;

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var scroller = window.FindControl<ScrollViewer>("CueScroll");
        Assert.NotNull(scroller);

        // A bar with nothing to scroll is collapsed, and a collapsed bar has no thumb in the tree
        // to ask — which is why the transcript above is longer than the pane.
        var bar = scroller!.GetVisualDescendants().OfType<ScrollBar>()
            .Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        Assert.True(bar.IsVisible, "the transcript did not overflow the pane, so the bar never appeared");

        var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();

        var expected = Assert.IsAssignableFrom<ISolidColorBrush>(
            Application.Current!.FindResource("DisabledTextBrush"));
        var actual = Assert.IsAssignableFrom<ISolidColorBrush>(thumb.Background);

        Assert.Equal(expected.Color, actual.Color);

        // And under the pointer it is still a palette grey. The theme's own hover does not recolour
        // the thumb — a pointer moved onto it in this host leaves the resting brush in place, which
        // is measured rather than assumed, see Tokens.axaml — so what is held here is only that no
        // grey of the toolkit's comes back with the pointer.
        var centre = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
        window.MouseMove(centre);
        window.UpdateLayout();

        Assert.True(thumb.IsPointerOver, "the pointer did not reach the thumb");

        var hovered = Assert.IsAssignableFrom<ISolidColorBrush>(thumb.Background).Color;
        var palette = new[] { "DisabledTextBrush", "InkMetaBrush", "InkSecondaryBrush" }
            .Select(key => Assert.IsAssignableFrom<ISolidColorBrush>(Application.Current!.FindResource(key)).Color)
            .ToList();

        Assert.Contains(hovered, palette);

        window.Close();
    }

    /// <summary>Sixty cues, which is more than the pane holds at the window's opening size.</summary>
    private static Parakeet.App.ViewModels.JobViewModel LongTranscript()
    {
        var job = new Parakeet.App.ViewModels.JobViewModel("/tmp/long.wav");
        job.Complete(new Parakeet.Core.Jobs.JobResult
        {
            Job = new Parakeet.Core.Jobs.TranscriptionJob { InputPath = job.Path },
            State = Parakeet.Core.Jobs.JobState.Completed,
            Document = new Parakeet.Core.Transcription.TranscriptDocument
            {
                Segments = Enumerable.Range(0, 60).Select(i => new Parakeet.Core.Transcription.TranscriptSegment
                {
                    Start = TimeSpan.FromSeconds(i * 5),
                    End = TimeSpan.FromSeconds(i * 5 + 5),
                    Text = $"line {i}",
                }).ToList(),
            },
        });

        return job;
    }
}
