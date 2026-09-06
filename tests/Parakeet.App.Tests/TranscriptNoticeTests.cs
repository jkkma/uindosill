using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.Audio;
using Parakeet.Core.Jobs;

namespace Parakeet.App.Tests;

/// <summary>
/// The Transcribe tab's transcript area says what it is waiting for while it has nothing to show.
/// </summary>
/// <remarks>
/// <para>
/// The Ask tab has explained its empty pane since it was built; this one was a bordered blank —
/// on a first launch the largest thing in the window and the only one saying nothing. The notice
/// is two decisions in two places, and both are held here: the words come from the view models,
/// and say which nothing this is; whether they show at all is the view's, decided off the drawn
/// list's own count, so the first line to land takes the notice away and a Clear brings it back.
/// </para>
/// <para>
/// The window half is a window test for the reason every notice in this application has one: a
/// sentence that is true on the view model and bound to nothing is the shape of defect this window
/// keeps finding.
/// </para>
/// </remarks>
public class TranscriptNoticeTests
{
    [Fact]
    public void EachStateOfARowHasItsOwnSentence()
    {
        var row = new JobViewModel("/tmp/a.wav");
        Assert.Equal(JobState.Pending, row.State);
        Assert.Contains("Waiting for Start", row.TranscriptNotice, StringComparison.Ordinal);

        row.State = JobState.Running;
        Assert.Contains("Decoding", row.TranscriptNotice, StringComparison.Ordinal);

        Finish(row, JobState.Failed);
        Assert.Contains("could not be transcribed", row.TranscriptNotice, StringComparison.Ordinal);

        Finish(row, JobState.Cancelled);
        Assert.Contains("cancelled", row.TranscriptNotice, StringComparison.Ordinal);

        // A run that completed with nothing to draw is not an error: the recording nobody spoke in.
        Finish(row, JobState.Completed);
        Assert.Contains("Nothing was recognised", row.TranscriptNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStateChangeAnnouncesTheSentence()
    {
        // The view binds the sentence, so the row has to say when it changes — the state field
        // carries the notification, and a sentence read once at construction would sit on
        // "Waiting for Start" over a row that had failed.
        var row = new JobViewModel("/tmp/a.wav");
        var announced = new List<string?>();
        row.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        row.State = JobState.Running;

        Assert.Contains(nameof(JobViewModel.TranscriptNotice), announced);
    }

    [Fact]
    public void TheQueueSaysWhichNothingItIsAndFollowsTheChosenRow()
    {
        var main = WindowTests.NewViewModel(out var directory);
        var queue = main.Transcribe;
        var announced = new List<string?>();
        queue.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        // Nothing at all: the invitation, in the words the drop zone above it uses.
        Assert.Contains("Drop a recording", queue.TranscriptNotice, StringComparison.Ordinal);

        // Rows, none chosen.
        queue.AddFiles([WriteWav(directory, "a.wav")]);
        Assert.Contains("Choose a file", queue.TranscriptNotice, StringComparison.Ordinal);
        Assert.Contains(nameof(TranscribeViewModel.TranscriptNotice), announced);

        // A chosen row speaks for itself, and keeps speaking as it changes: the queue relays the
        // row's own notification, which is what lets the area follow a row that starts or fails
        // while it is the one being looked at.
        announced.Clear();
        queue.SelectedJob = queue.Jobs[0];
        Assert.Equal(queue.Jobs[0].TranscriptNotice, queue.TranscriptNotice);
        Assert.Contains("Waiting for Start", queue.TranscriptNotice, StringComparison.Ordinal);
        Assert.Contains(nameof(TranscribeViewModel.TranscriptNotice), announced);

        announced.Clear();
        queue.Jobs[0].State = JobState.Running;
        Assert.Contains("Decoding", queue.TranscriptNotice, StringComparison.Ordinal);
        Assert.Contains(nameof(TranscribeViewModel.TranscriptNotice), announced);

        // And a row let go of is not listened to any more: its changes are its own.
        queue.SelectedJob = null;
        announced.Clear();
        queue.Jobs[0].State = JobState.Failed;
        Assert.DoesNotContain(nameof(TranscribeViewModel.TranscriptNotice), announced);
        Assert.Contains("Choose a file", queue.TranscriptNotice, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task TheNoticeStandsInForTheTranscriptAndLeavesWhenTheFirstLineLands()
    {
        var main = WindowTests.NewViewModel(out var directory);
        var window = new MainWindow { DataContext = main };
        window.Show();
        window.UpdateLayout();

        var notice = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "TranscribeNotice");
        var lines = window.GetVisualDescendants().OfType<ItemsControl>()
            .Single(i => i.Name == "TranscriptLines");

        Assert.True(notice.IsVisible);
        Assert.Equal(main.Transcribe.TranscriptNotice, notice.Text);
        Assert.Contains("Drop a recording", notice.Text, StringComparison.Ordinal);

        main.Transcribe.AddFiles([WriteWav(directory, "a.wav")]);
        main.Transcribe.SelectedJob = main.Transcribe.Jobs[0];
        window.UpdateLayout();

        Assert.True(notice.IsVisible);
        Assert.Contains("Waiting for Start", notice.Text, StringComparison.Ordinal);

        // The canned engine writes lines, and the first of them is what takes the notice away —
        // off the list's own count, not off any flag a view model could forget to raise.
        await main.Transcribe.StartCommand.ExecuteAsync(null);
        window.UpdateLayout();

        Assert.Equal(JobState.Completed, main.Transcribe.Jobs[0].State);
        Assert.True(lines.ItemCount > 0, "the canned run produced no lines to draw");
        Assert.False(notice.IsVisible);

        // Clear takes the rows and their lines, and the invitation comes back.
        main.Transcribe.ClearCommand.Execute(null);
        window.UpdateLayout();

        Assert.Equal(0, lines.ItemCount);
        Assert.True(notice.IsVisible);
        Assert.Contains("Drop a recording", notice.Text, StringComparison.Ordinal);

        window.Close();
    }

    private static void Finish(JobViewModel row, JobState state) =>
        row.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = row.Path },
            State = state,
        });

    private static string WriteWav(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        var samples = new float[16_000 * 4];
        var random = new Random(5);

        for (var i = 0; i < samples.Length; i++)
        {
            var second = i / 16_000.0;
            samples[i] = second is > 0.5 and < 3.2
                ? (float)(0.5 * Math.Sin(2 * Math.PI * 200 * i / 16_000.0))
                : (float)(random.NextDouble() * 0.001 - 0.0005);
        }

        WavWriter.WriteFile(path, samples, 16_000);
        return path;
    }
}
