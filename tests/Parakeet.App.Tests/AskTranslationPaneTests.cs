using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.Core.Jobs;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;

namespace Parakeet.App.Tests;

/// <summary>
/// Reading the English on the Ask tab, and switching back to the transcript that marks words.
/// </summary>
/// <remarks>
/// The two panes are not equivalent here, which is why there is a switcher rather than a
/// replacement: a translated segment carries its start and end and no word times, because
/// translating loses which word was said when. So the English seeks and highlights by line and
/// marks no word, and the transcript is the pane that follows a voice.
/// </remarks>
public class AskTranslationPaneTests
{
    [AvaloniaFact]
    public void ATranslationArrivingPutsTheEnglishInFrontOfTheReader()
    {
        // Asking for a translation and then having to go and find where it went is the wrong way
        // round. It arrives on a row this tab is already showing, so nothing about the selection
        // changes and only the job announces it.
        var (window, viewModel) = Open();

        Assert.False(viewModel.Ask.CanShowTranslation);
        Assert.Equal(0, viewModel.Ask.TranscriptPane);

        Translate(viewModel.Transcribe.Jobs[0]);
        window.UpdateLayout();

        Assert.True(viewModel.Ask.CanShowTranslation);
        Assert.Equal(1, viewModel.Ask.TranscriptPane);
        Assert.True(viewModel.Ask.IsShowingTranslation);

        // And the cues really are the English ones.
        Assert.Equal(
            ["good morning everyone", "thanks for having me"],
            viewModel.Ask.Lines!.Select(l => l.Text));
    }

    [AvaloniaFact]
    public void TheSwitcherGoesBackToTheTranscriptAndStaysThere()
    {
        var (window, viewModel) = Open();
        Translate(viewModel.Transcribe.Jobs[0]);
        window.UpdateLayout();

        var switcher = window.FindControl<Border>("AskPaneSwitcher")!;
        Assert.True(switcher.IsVisible);

        var transcript = window.FindControl<RadioButton>("AskPaneTranscript")!;
        Assert.NotNull(window.FindControl<RadioButton>("AskPaneEnglish"));

        transcript.IsChecked = true;
        window.UpdateLayout();

        Assert.Equal(0, viewModel.Ask.TranscriptPane);
        Assert.Equal(
            ["buenos días a todos", "gracias por invitarme"],
            viewModel.Ask.Lines!.Select(l => l.Text));

        // A second recording gaining a translation must not drag them back to the English they
        // deliberately left — the snap is once, not a lock.
        var second = Transcribed("/tmp/b.wav");
        viewModel.Transcribe.Jobs.Add(second);
        Translate(second);
        window.UpdateLayout();

        Assert.Equal(0, viewModel.Ask.TranscriptPane);
    }

    [AvaloniaFact]
    public void TheSwitcherIsAbsentOnARecordingNobodyTranslated()
    {
        // Which is most of them: translation is an opt-in.
        var (window, viewModel) = Open();

        Assert.False(window.FindControl<Border>("AskPaneSwitcher")!.IsVisible);
        Assert.False(window.FindControl<TextBlock>("TranslationPaneNotice")!.IsVisible);
        Assert.Null(viewModel.Ask.TranslationPaneNotice);
    }

    [AvaloniaFact]
    public void TheEnglishSaysWhyNoWordLightsUpOnIt()
    {
        // A mark that works on one pane and not the other is indistinguishable from a broken one
        // until something explains which.
        var (window, viewModel) = Open();
        Translate(viewModel.Transcribe.Jobs[0]);
        window.UpdateLayout();

        var notice = window.FindControl<TextBlock>("TranslationPaneNotice")!;
        Assert.True(notice.IsVisible);
        Assert.Contains("Individual words are not marked here", notice.Text, StringComparison.Ordinal);

        viewModel.Ask.TranscriptPane = 0;
        window.UpdateLayout();
        Assert.False(notice.IsVisible);
    }

    [AvaloniaFact]
    public void TheHighlightFollowsThePlayheadOnEitherPaneAndMarksAWordOnOnlyOne()
    {
        // The two panes share the segment times, so the same moment is the same line in both. What
        // differs is the word: the English carries none to find.
        var (window, viewModel) = Open();
        Translate(viewModel.Transcribe.Jobs[0]);
        window.UpdateLayout();

        var player = (FakeMediaPlayer)viewModel.Ask.Player;
        player.Seek(TimeSpan.FromSeconds(1));
        viewModel.Ask.Tick();
        window.UpdateLayout();

        // English: the line lights, the word does not.
        Assert.Equal(0, viewModel.Ask.ActiveLineIndex);
        Assert.Equal(-1, viewModel.Ask.Lines![0].SpokenWord);

        viewModel.Ask.TranscriptPane = 0;
        window.UpdateLayout();

        // Transcript: the same line, and now a word inside it.
        Assert.Equal(0, viewModel.Ask.ActiveLineIndex);
        Assert.True(
            viewModel.Ask.Lines![0].SpokenWord >= 0,
            "the transcript pane marked no word, which is the whole reason to switch to it");
    }

    [AvaloniaFact]
    public void TheFindBoxSearchesWhicheverPaneIsBeingRead()
    {
        // The hits are matches in the text of one pane and mean nothing in the other's.
        var (window, viewModel) = Open();
        Translate(viewModel.Transcribe.Jobs[0]);
        window.UpdateLayout();

        viewModel.Ask.SearchTerm = "morning";
        Assert.Equal(1, viewModel.Ask.MatchCount);

        viewModel.Ask.TranscriptPane = 0;
        window.UpdateLayout();

        // The English word is not in the Spanish.
        Assert.Equal(0, viewModel.Ask.MatchCount);

        viewModel.Ask.SearchTerm = "días";
        Assert.Equal(1, viewModel.Ask.MatchCount);
    }

    /// <summary>The window on the Ask tab, with one transcribed Spanish recording open.</summary>
    private static (MainWindow Window, MainWindowViewModel ViewModel) Open()
    {
        var directory = TestTemp.NewDirectory("uindosill-pane");
        var viewModel = new MainWindowViewModel(
            new FakeEngineProvider(),
            new LocalModelStore(directory),
            ModelCatalog.Default,
            player: new FakeMediaPlayer { DurationToReport = TimeSpan.FromMinutes(2) });

        viewModel.Transcribe.Jobs.Add(Transcribed("/tmp/a.wav"));
        viewModel.SelectedTab = 4;

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        return (window, viewModel);
    }

    private static JobViewModel Transcribed(string path)
    {
        var job = new JobViewModel(path);
        job.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = path },
            State = JobState.Completed,
            Document = Spoken(),
        });

        return job;
    }

    /// <summary>Puts an English version on the row, as a finished translating run does.</summary>
    private static void Translate(JobViewModel job) =>
        job.Complete(
            new JobResult
            {
                Job = new TranscriptionJob { InputPath = job.Path },
                State = JobState.Completed,
                Document = Spoken() with
                {
                    TranslatedTo = "en",
                    Segments =
                    [
                        // No words, which is what the translator really returns: translating loses
                        // which word was said when.
                        new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(3), Text = "good morning everyone" },
                        new TranscriptSegment { Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(6), Text = "thanks for having me" },
                    ],
                },
            },
            source: Spoken());

    private static TranscriptDocument Spoken() => new()
    {
        Segments =
        [
            new TranscriptSegment
            {
                Start = TimeSpan.Zero,
                End = TimeSpan.FromSeconds(3),
                Text = "buenos días a todos",
                Words =
                [
                    new TranscriptWord { Text = "buenos", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1) },
                    new TranscriptWord { Text = "días", Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2) },
                    new TranscriptWord { Text = "a", Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(2.5) },
                    new TranscriptWord { Text = "todos", Start = TimeSpan.FromSeconds(2.5), End = TimeSpan.FromSeconds(3) },
                ],
            },
            new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(3),
                End = TimeSpan.FromSeconds(6),
                Text = "gracias por invitarme",
            },
        ],
    };
}
