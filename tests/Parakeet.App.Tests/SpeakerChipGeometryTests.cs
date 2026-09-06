using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.Core.Jobs;
using Parakeet.Core.Transcription;

namespace Parakeet.App.Tests;

/// <summary>
/// Every speaker chip is the same shape, whichever of the eight treatments it wears.
/// </summary>
/// <remarks>
/// <para>
/// Two of the eight are outlines, and an edge that only they carried made them a unit larger on
/// every side than the chips beside them. Rendered, that was two things: on the strip that names
/// the voices the chips stood at two heights, and in a transcript every line spoken by an outlined
/// speaker began two units to the right of the lines above and below it. Neither is a thing a
/// contrast test or a height floor measures — <see cref="SpeakerChipContrastTests"/> holds the
/// colours and the Ask tab's own test holds a chip against its label — so this holds the chips
/// against each other.
/// </para>
/// <para>
/// Three speakers, because the third is the first outline: chips 0 and 1 are fills, chip 2 is the
/// first edge-only treatment, and a transcript that reaches it has both shapes on one screen.
/// </para>
/// </remarks>
public class SpeakerChipGeometryTests
{
    [AvaloniaFact]
    public void ChipsOfEveryTreatmentShareOneGeometry()
    {
        var viewModel = WindowTests.NewViewModel(out _);
        var job = ThreeSpeakers();
        viewModel.Transcribe.Jobs.Add(job);
        viewModel.SelectedTab = 4;

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        viewModel.Ask.SelectedRecording = job;
        window.UpdateLayout();

        // Both shapes are on screen: the fixture reaches the first outline.
        var treatments = job.Speakers.Select(v => v.Chip).ToList();
        Assert.Equal([0, 1, 2], treatments);

        // The strip that names the voices: one height for all three.
        var strip = window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "Voices");
        var stripChips = strip.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("speaker"))
            .ToList();

        Assert.Equal(3, stripChips.Count);
        Assert.Single(stripChips.Select(c => Math.Round(c.Bounds.Height, 1)).Distinct());

        // The cues: the words start at the same offset on every line, whoever is speaking.
        var cues = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("cue"))
            .ToList();

        Assert.Equal(3, cues.Count);

        var wordOffsets = cues.Select(cue =>
        {
            var words = cue.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "CueWords");
            return Math.Round(words.TranslatePoint(default, cue)!.Value.X, 1);
        }).ToList();

        Assert.Single(wordOffsets.Distinct());

        // And the reason: the edge is on every chip, not only the ones that colour it.
        var everyChip = window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("speaker"))
            .ToList();

        Assert.All(everyChip, chip => Assert.Equal(new Thickness(1), chip.BorderThickness));

        window.Close();
    }

    private static JobViewModel ThreeSpeakers()
    {
        var job = new JobViewModel("/tmp/three.wav");
        job.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = job.Path },
            State = JobState.Completed,
            Document = new TranscriptDocument
            {
                Segments =
                [
                    new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Speaker = "Speaker 1", Text = "one" },
                    new TranscriptSegment { Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(20), Speaker = "Speaker 2", Text = "two" },
                    new TranscriptSegment { Start = TimeSpan.FromSeconds(20), End = TimeSpan.FromSeconds(30), Speaker = "Speaker 3", Text = "three" },
                ],
            },
        });

        return job;
    }
}
