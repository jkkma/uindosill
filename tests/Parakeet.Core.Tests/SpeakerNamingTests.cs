using Parakeet.Core.Diarisation;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

/// <summary>
/// Putting a reader's names for the speakers onto a transcript, which is what lets a rename reach
/// an output file rather than staying on screen.
/// </summary>
public class SpeakerNamingTests
{
    [Fact]
    public void ANameReplacesItsLabelOnSegmentsWordsAndTurnsAlike()
    {
        // All three, because a file that renamed the segments and left the turns would make an SRT
        // and an RTTM of the same recording disagree about who is in it.
        var named = Document().WithSpeakerNames(new Dictionary<string, string> { ["Speaker 1"] = "Ada" });

        Assert.Equal("Ada", named.Segments[0].Speaker);
        Assert.Equal("Ada", named.Segments[0].Words[0].Speaker);
        Assert.Equal("Ada", named.SpeakerTurns[0].Speaker);

        // And the speaker nobody renamed keeps the label the diariser gave.
        Assert.Equal("Speaker 2", named.Segments[1].Speaker);
        Assert.Equal("Speaker 2", named.Segments[1].Words[0].Speaker);
        Assert.Equal("Speaker 2", named.SpeakerTurns[1].Speaker);
    }

    [Fact]
    public void TheDocumentTheEngineWroteIsNeverChanged()
    {
        // The engine's labels are what tie what is on screen to the transcript files already
        // written. A rename makes a second document; it does not edit the first.
        var original = Document();

        var named = original.WithSpeakerNames(new Dictionary<string, string> { ["Speaker 1"] = "Ada" });

        Assert.NotSame(original, named);
        Assert.Equal("Speaker 1", original.Segments[0].Speaker);
        Assert.Equal("Speaker 1", original.Segments[0].Words[0].Speaker);
        Assert.Equal("Speaker 1", original.SpeakerTurns[0].Speaker);
    }

    [Fact]
    public void EverythingElseAboutTheTranscriptSurvivesTheRename()
    {
        // Provenance especially: a renamed transcript that had lost which model made it would be a
        // result nobody could re-examine, which is the whole objection this repository has to
        // unattributed figures.
        var named = Document().WithSpeakerNames(new Dictionary<string, string> { ["Speaker 1"] = "Ada" });

        Assert.Equal("parakeet-v3", named.ModelId);
        Assert.Equal("pyannote", named.SpeakerModelId);
        Assert.Equal(2, named.Segments.Count);
        Assert.Equal("hello there", named.Segments[0].Text);
        Assert.Equal(TimeSpan.Zero, named.Segments[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(1), named.Segments[0].Words[0].End);
    }

    [Fact]
    public void AMapThatChangesNothingReturnsTheDocumentItself()
    {
        // The common case is a transcript nobody has renamed, and rebuilding three hours of
        // segments to apply an empty map would be work for nothing.
        var original = Document();

        Assert.Same(original, original.WithSpeakerNames(null));
        Assert.Same(original, original.WithSpeakerNames(new Dictionary<string, string>()));

        // A name identical to the label is not a rename either.
        Assert.Same(
            original,
            original.WithSpeakerNames(new Dictionary<string, string> { ["Speaker 1"] = "Speaker 1" }));

        // Nor is a blank one — an emptied field is somebody undoing a rename.
        Assert.Same(
            original,
            original.WithSpeakerNames(new Dictionary<string, string> { ["Speaker 1"] = "   " }));
    }

    [Fact]
    public void ALabelNobodyNamedIsLeftExactlyAsItWas()
    {
        var named = Document().WithSpeakerNames(new Dictionary<string, string> { ["Speaker 9"] = "Nobody" });

        Assert.Equal("Speaker 1", named.Segments[0].Speaker);
        Assert.Equal("Speaker 2", named.Segments[1].Speaker);
    }

    [Fact]
    public void AnUnlabelledTranscriptIsUntouched()
    {
        // Most transcripts: the labelling pass is opt-in, and a null speaker stays null rather than
        // acquiring a name from a map that cannot be about it.
        var plain = new TranscriptDocument
        {
            Segments =
            [
                new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), Text = "hello there" },
            ],
        };

        var named = plain.WithSpeakerNames(new Dictionary<string, string> { ["Speaker 1"] = "Ada" });

        Assert.Null(named.Segments[0].Speaker);
    }

    private static TranscriptDocument Document() => new()
    {
        ModelId = "parakeet-v3",
        SpeakerModelId = "pyannote",
        Segments =
        [
            new TranscriptSegment
            {
                Start = TimeSpan.Zero,
                End = TimeSpan.FromSeconds(2),
                Text = "hello there",
                Speaker = "Speaker 1",
                Words =
                [
                    new TranscriptWord { Text = "hello", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Speaker = "Speaker 1" },
                    new TranscriptWord { Text = "there", Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2), Speaker = "Speaker 1" },
                ],
            },
            new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(2),
                End = TimeSpan.FromSeconds(4),
                Text = "hello back",
                Speaker = "Speaker 2",
                Words =
                [
                    new TranscriptWord { Text = "hello", Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(3), Speaker = "Speaker 2" },
                ],
            },
        ],
        SpeakerTurns =
        [
            new SpeakerTurn { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), Speaker = "Speaker 1" },
            new SpeakerTurn { Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(4), Speaker = "Speaker 2" },
        ],
    };
}
