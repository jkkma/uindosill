using Parakeet.App.ViewModels;
using Parakeet.Core.Transcription;

namespace Parakeet.App.Tests;

public class TranscriptLineTests
{
    [Fact]
    public void ALineIsDrawnWithoutItsFinalFullStopAndItsLastWordStillLightsUp()
    {
        // Asked for on 2026-08-23: the line reads "Das ist gut", the way a subtitle does, while the
        // document still says "Das ist gut." — and the word that carried the stop is located without
        // it, so the mark lands on "gut" when it is spoken rather than never landing at all.
        var words = new List<TranscriptWord>
        {
            new() { Text = "Das", Start = TimeSpan.FromSeconds(0.0), End = TimeSpan.FromSeconds(0.3) },
            new() { Text = "ist", Start = TimeSpan.FromSeconds(0.3), End = TimeSpan.FromSeconds(0.5) },
            new() { Text = "gut.", Start = TimeSpan.FromSeconds(0.5), End = TimeSpan.FromSeconds(0.9) },
        };
        var line = new TranscriptLineViewModel(null, "Das ist gut.", TimeSpan.Zero, TimeSpan.FromSeconds(1), words);

        Assert.Equal("Das ist gut", line.Text);
        Assert.True(line.HasWordTimings);

        line.SpokenWord = line.WordAt(TimeSpan.FromSeconds(0.6));
        Assert.Equal(2, line.SpokenWord);
        Assert.Equal(8, line.Marked.SpokenStart);
        Assert.Equal(3, line.Marked.SpokenLength);

        // A question keeps its mark, and an ellipsis its trailing-off.
        Assert.Equal("Wirklich?", new TranscriptLineViewModel(null, "Wirklich?", TimeSpan.Zero, TimeSpan.FromSeconds(1)).Text);
        Assert.Equal("Naja...", new TranscriptLineViewModel(null, "Naja...", TimeSpan.Zero, TimeSpan.FromSeconds(1)).Text);
    }
}
