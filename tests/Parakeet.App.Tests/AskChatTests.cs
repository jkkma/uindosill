using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.Core.Answers;
using Parakeet.Core.Jobs;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;

namespace Parakeet.App.Tests;

/// <summary>
/// The chat half of the Ask tab, against <see cref="FakeAnswerEngine"/>: the whole seam —
/// retrieval, streaming, the parser, the validator, the chips — with no server and no model,
/// which is R12 kept. The real engine behind the same seam is exercised by its own gated
/// integration test; what these prove is everything the window does around it.
/// </summary>
public class AskChatTests
{
    private static JobViewModel Transcribed(string path = "/tmp/a.wav")
    {
        var job = new JobViewModel(path);
        job.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = path },
            State = JobState.Completed,
            Document = new TranscriptDocument
            {
                SourceName = path,
                ModelId = "parakeet-tdt-0.6b-v3-q8_0",
                Quantisation = "q8_0",
                AudioDuration = TimeSpan.FromSeconds(30),
                Segments =
                [
                    new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "the meeting opened with the quarterly budget review" },
                    new TranscriptSegment { Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(20), Text = "maria presented the axolotl conservation project" },
                    new TranscriptSegment { Start = TimeSpan.FromSeconds(20), End = TimeSpan.FromSeconds(30), Text = "the team agreed to meet again on friday" },
                ],
            },
        });

        return job;
    }

    [Fact]
    public void ThinkingProgressShowsTheDotsAndTheFirstAnswerTextHidesThem()
    {
        // The typing indicator's contract: dots while the model reasons and nothing has
        // streamed, gone the moment answer text exists — and never a stale Status line
        // underneath them.
        var entry = new ChatEntryViewModel("q", _ => Task.CompletedTask);

        entry.OnProgress(new AskProgress { PrefillTokens = 5, PrefillTotalTokens = 10 });
        Assert.False(entry.IsThinking);
        Assert.NotNull(entry.Status);

        entry.OnProgress(new AskProgress { PrefillTokens = 10, PrefillTotalTokens = 10, ThinkingTokens = 3 });
        Assert.True(entry.IsThinking);
        Assert.Null(entry.Status);

        entry.OnStreamed("- a claim [S1]");
        Assert.False(entry.IsThinking);

        var failed = new ChatEntryViewModel("q", _ => Task.CompletedTask);
        failed.OnProgress(new AskProgress { ThinkingTokens = 1 });
        failed.Fail("boom");
        Assert.False(failed.IsThinking);
    }

    private static (AskChatViewModel Chat, FakeAnswerEngineProvider Provider, List<TimeSpan> Seeks) Chat(
        JobViewModel? job = null,
        ModelSession? session = null,
        Func<bool>? transcriptionRunning = null,
        FakeAnswerOptions? options = null)
    {
        var provider = new FakeAnswerEngineProvider(options);
        var seeks = new List<TimeSpan>();
        var chat = new AskChatViewModel(provider, session, transcriptionRunning, seeks.Add);
        chat.SetRecording(job ?? Transcribed());
        return (chat, provider, seeks);
    }

    private static async Task AskAsync(AskChatViewModel chat, string question)
    {
        chat.QuestionText = question;
        Assert.True(chat.AskCommand.CanExecute(null), chat.PanelNotice ?? "the command refused with no notice");
        await chat.AskCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task AnAnswerArrivesAsBulletsWhoseChipsCarryResolvedTimesAndSeek()
    {
        var (chat, _, seeks) = Chat();

        await AskAsync(chat, "what did maria present about the axolotl?");

        var entry = Assert.Single(chat.Entries);
        Assert.True(entry.IsDone);
        Assert.Null(entry.Failure);
        Assert.False(entry.Abstained);
        Assert.True(entry.HasBullets);

        // The fake cites the evidence it was handed, so at least one chip resolved — and its
        // display is a time the application resolved, never the model's own text.
        var chip = entry.Bullets.SelectMany(b => b.Citations).First(c => c.IsResolved);
        Assert.NotEqual("?", chip.Display);

        chip.SeekCommand.Execute(null);
        var seek = Assert.Single(seeks);
        Assert.InRange(seek.TotalSeconds, 0, 30);

        // The provenance line is on the answer, and it says the one thing that must never be
        // mistakable: this text was generated, not transcribed.
        Assert.Contains("not transcribed", entry.ModelLine, StringComparison.Ordinal);
        Assert.Contains("fake-answer-model", entry.ModelLine, StringComparison.Ordinal);

        // And the sources expander holds what the model was shown, in rank order.
        Assert.True(entry.HasSources);
        Assert.Equal(1, entry.Sources[0].Rank);
    }

    [Fact]
    public async Task AQuestionNothingMatchesAbstainsWithoutLoadingAnyModel()
    {
        var (chat, provider, _) = Chat();

        // No word of this appears in the transcript, so retrieval returns nothing — and empty
        // retrieval is the abstain path, never an invitation to answer from nothing. The search
        // costs milliseconds and runs first, so the abstention arrives without the transcriber
        // being unloaded or a byte of the language model being read.
        await AskAsync(chat, "zzz qqq xxx");

        var entry = Assert.Single(chat.Entries);
        Assert.True(entry.Abstained);
        Assert.False(entry.HasBullets);
        Assert.Equal(0, provider.Created);
    }

    [Fact]
    public async Task ATranscriptionStartingDuringTheModelLoadStillLeavesTheSentence()
    {
        // The engine field is assigned only after the load returns, and the notice branch used
        // to require it non-null — a transcription starting during the load yielded "Stopped."
        // with no line saying why, which reads as a defect rather than the residency policy.
        var gate = new TaskCompletionSource();
        var (chat, _, _) = Chat(options: new FakeAnswerOptions { LoadGate = gate.Task });

        chat.QuestionText = "what about the axolotl?";
        var asking = chat.AskCommand.ExecuteAsync(null);
        Assert.True(chat.IsAsking);

        await chat.OnTranscriptionStartedAsync();
        await asking;

        var entry = Assert.Single(chat.Entries);
        Assert.Equal("Stopped.", entry.Failure);
        Assert.Contains("unloaded while transcribing", chat.ResidencyNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEnglishIsTheAsksWorldWhenATranslationExists()
    {
        // The maintainer's decision, 2026-08-24: on a translated recording the model sees the
        // English pane. The fake quotes the evidence verbatim, so the quote coming back English
        // is the decision observable from outside.
        var job = new JobViewModel("/tmp/es.wav");
        var spoken = new TranscriptDocument
        {
            Segments =
            [
                new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "buenos días a todos los presentes hoy" },
            ],
        };

        job.Complete(
            new JobResult
            {
                Job = new TranscriptionJob { InputPath = job.Path },
                State = JobState.Completed,
                Document = spoken with
                {
                    TranslatedTo = "en",
                    Segments =
                    [
                        new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "good morning to everyone present today" },
                    ],
                },
            },
            source: spoken);

        var (chat, _, _) = Chat(job);
        await AskAsync(chat, "good morning everyone");

        var entry = Assert.Single(chat.Entries);
        var quoted = entry.Bullets.First(b => b.Quote is not null).Quote!;
        Assert.Contains("good", quoted, StringComparison.Ordinal);
        Assert.DoesNotContain("buenos", quoted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTranslatedAskCarriesNoSourceLanguageHint()
    {
        // The maintainer's decision, 2026-08-24, closing the review's collision: the hint
        // survives onto the translated document as provenance, but forwarding it into the ask
        // instructed a source-language answer over English evidence — whose grammar-forced
        // quote the verbatim check could then only fail. A translated ask is unlocalised; the
        // transcript as spoken keeps its hint exactly as decided.
        var translated = new JobViewModel("/tmp/es.wav");
        var spoken = new TranscriptDocument
        {
            Language = "es",
            Segments =
            [
                new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "buenos días a todos los presentes hoy" },
            ],
        };
        translated.Complete(
            new JobResult
            {
                Job = new TranscriptionJob { InputPath = translated.Path },
                State = JobState.Completed,
                Document = spoken with
                {
                    TranslatedTo = "en",
                    Segments =
                    [
                        new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "good morning to everyone present today" },
                    ],
                },
            },
            source: spoken);

        var (chat, provider, _) = Chat(translated);
        await AskAsync(chat, "good morning everyone");
        Assert.Null(provider.LastCreated!.LastRequest!.Language);

        var untranslated = new JobViewModel("/tmp/de.wav");
        untranslated.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = untranslated.Path },
            State = JobState.Completed,
            Document = new TranscriptDocument
            {
                Language = "de",
                Segments =
                [
                    new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "wir haben das budget zweimal geprüft" },
                ],
            },
        });

        var (spokenChat, spokenProvider, _) = Chat(untranslated);
        await AskAsync(spokenChat, "wurde das budget geprüft?");
        Assert.Equal("de", spokenProvider.LastCreated!.LastRequest!.Language);
    }

    [Fact]
    public async Task TheFirstQuestionUnloadsTheTranscriptionModelAndSaysWhy()
    {
        // R9's decided half, driven end to end: the ASR model is resident, a question arrives,
        // and by the time the answer exists the ASR model is not — with the line that says so,
        // because a model vanishing without a sentence is a defect report waiting to be filed.
        var session = new ModelSession(new FakeEngineProvider());
        await session.LoadAsync(new EngineSelection { Model = ModelCatalog.Default.Recommended });
        Assert.True(session.IsLoaded);

        var (chat, _, _) = Chat(session: session);
        await AskAsync(chat, "what about the budget review?");

        Assert.False(session.IsLoaded);
        Assert.Contains("unloaded", chat.ResidencyNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskingWhileTranscribingRefusesWithASentenceRatherThanLoadingASecondModel()
    {
        var (chat, provider, _) = Chat(transcriptionRunning: () => true);

        chat.QuestionText = "anything?";
        await chat.AskCommand.ExecuteAsync(null);

        Assert.Empty(chat.Entries);
        Assert.Equal(0, provider.Created);
        Assert.Contains("transcription", chat.ResidencyNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ATranscriptionStartingMidChatTakesTheEngineDownAndTheNextQuestionReloadsIt()
    {
        var (chat, provider, _) = Chat();

        await AskAsync(chat, "what about the axolotl?");
        Assert.Equal(1, provider.Created);

        await chat.OnTranscriptionStartedAsync();
        Assert.Contains("unloaded", chat.ResidencyNotice, StringComparison.Ordinal);

        await AskAsync(chat, "and the budget?");
        Assert.Equal(2, provider.Created);
    }

    [Fact]
    public async Task FlippingThinkingModeRebuildsTheEngineAtTheNextQuestion()
    {
        // The mode is a child-process argument, so the Settings toggle can only take effect
        // through a fresh child — and the hint under the toggle promises the next question.
        var (chat, provider, _) = Chat();

        await AskAsync(chat, "what about the axolotl?");
        await AskAsync(chat, "and the budget?");
        Assert.Equal(1, provider.Created);

        provider.ThinkingMode = true;
        await AskAsync(chat, "who presented?");
        Assert.Equal(2, provider.Created);
    }

    [Fact]
    public async Task SwitchingRecordingsClearsTheConversation()
    {
        // The citations were ids into the transcript that is no longer open; a chip that seeks
        // the wrong recording is worse than an empty panel.
        var (chat, _, _) = Chat();
        await AskAsync(chat, "what about the axolotl?");
        Assert.Single(chat.Entries);

        chat.SetRecording(Transcribed("/tmp/b.wav"));
        Assert.Empty(chat.Entries);
    }

    [Fact]
    public async Task CopyLeadsWithTheMarkerAndCarriesTimestampsAndBothProvenances()
    {
        var (chat, _, _) = Chat();
        string? copied = null;
        chat.CopyToClipboard = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        await AskAsync(chat, "what did maria present?");
        var entry = Assert.Single(chat.Entries);

        Assert.True(entry.CanCopy);
        await entry.CopyCommand.ExecuteAsync(null);

        Assert.NotNull(copied);

        // The marker line first, before anything a reader might quote (decision 5).
        Assert.StartsWith("Generated by a language model — not transcribed speech.", copied, StringComparison.Ordinal);

        // Plain timestamps, never clickable references: an email carries neither the app nor
        // the audio.
        Assert.Contains("[0", copied, StringComparison.Ordinal);

        // Both models: the one that generated and the one that transcribed, and the source.
        Assert.Contains("fake-answer-model", copied, StringComparison.Ordinal);
        Assert.Contains("parakeet-tdt-0.6b-v3-q8_0", copied, StringComparison.Ordinal);
        Assert.Contains("/tmp/a.wav", copied, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePanelSaysWhichPrerequisiteIsMissing()
    {
        // Engine first: a person who cannot ask at all should not first be sent to transcribe.
        var noEngine = new AskChatViewModel(
            new FakeAnswerEngineProvider(whyNot: "no engine here"), null, null, null);
        noEngine.SetRecording(Transcribed());
        Assert.Equal("no engine here", noEngine.PanelNotice);
        Assert.False(noEngine.IsPanelEnabled);

        var noTranscript = new AskChatViewModel(new FakeAnswerEngineProvider(), null, null, null);
        noTranscript.SetRecording(new JobViewModel("/tmp/raw.wav"));
        Assert.Contains("Transcribe", noTranscript.PanelNotice, StringComparison.Ordinal);

        var ready = new AskChatViewModel(new FakeAnswerEngineProvider(), null, null, null);
        ready.SetRecording(Transcribed());
        Assert.Null(ready.PanelNotice);
        Assert.True(ready.IsPanelEnabled);
    }

    [Fact]
    public async Task AnAnswerOfNothingIsAFailureNotAnAbstention()
    {
        // "The recording doesn't answer that." is a definite negative claim; a model that
        // produced no output has made no claim at all, and rendering its silence as the
        // abstention sentence fabricates one.
        var (chat, _, _) = Chat(options: new FakeAnswerOptions { ProduceNothing = true });

        await AskAsync(chat, "what about the axolotl?");

        var entry = Assert.Single(chat.Entries);
        Assert.False(entry.Abstained);
        Assert.NotNull(entry.Failure);
        Assert.Contains("no answer", entry.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecordingSwitchDuringTheModelLoadDoesNotPoisonTheIndex()
    {
        // The cold load is a seconds-to-minutes await, and the recordings drawer is not gated on
        // IsAsking: a switch during it used to let the in-flight ask refill the retrieval fields
        // from the old document, so every later answer retrieved from the wrong transcript.
        var gate = new TaskCompletionSource();
        var (chat, _, _) = Chat(options: new FakeAnswerOptions { LoadGate = gate.Task });

        chat.QuestionText = "what about the axolotl?";
        var asking = chat.AskCommand.ExecuteAsync(null);
        Assert.True(chat.IsAsking);

        var other = new JobViewModel("/tmp/b.wav");
        other.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = "/tmp/b.wav" },
            State = JobState.Completed,
            Document = new TranscriptDocument
            {
                SourceName = "/tmp/b.wav",
                Segments =
                [
                    new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "the salamander fund was approved unanimously" },
                ],
            },
        });
        chat.SetRecording(other);
        gate.SetResult();
        await asking;

        // The in-flight ask died with the document it was built over — the cleared conversation
        // stays cleared — and the next ask retrieves from the new recording's own index: a
        // question only the new transcript can match comes back cited, not abstained.
        Assert.Empty(chat.Entries);
        await AskAsync(chat, "what happened to the salamander fund?");
        var entry = Assert.Single(chat.Entries);
        Assert.False(entry.Abstained);
        var quoted = entry.Bullets.First(b => b.Quote is not null).Quote!;
        Assert.Contains("salamander", quoted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskingWhileTheTranscriptionModelIsLoadingRefusesTheSameWay()
    {
        // IsRunning is set only after the load await returns, so the residency probe alone reads
        // false for the whole stretch a large model spends loading — the session's busy flag is
        // the half of the probe that covers it, and this drives that stretch for real.
        var session = new ModelSession(new FakeEngineProvider(
            new FakeEngineOptions { LoadDelay = TimeSpan.FromSeconds(1) }));
        var loading = session.LoadAsync(new EngineSelection { Model = ModelCatalog.Default.Recommended });
        Assert.True(session.IsBusy);

        var (chat, provider, _) = Chat(session: session);
        chat.QuestionText = "anything?";
        await chat.AskCommand.ExecuteAsync(null);

        Assert.Empty(chat.Entries);
        Assert.Equal(0, provider.Created);
        Assert.Contains("transcription", chat.ResidencyNotice, StringComparison.OrdinalIgnoreCase);

        await loading;
    }

    [Fact]
    public void ACitedQuoteFailsLoudlyButAnUncheckedQuoteIsNotAccusedOfFailing()
    {
        // The [?]-with-quote bullet the real 9B produced: the quote was never checked against
        // anything, and the caveat has to say so rather than claim it was searched for and
        // missing — that claim is reserved for a quote a resolved span really failed to hold.
        var transcript = new TranscriptDocument
        {
            Segments =
            [
                new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "the budget was approved" },
            ],
        };

        var unanchored = AnswerParser.Parse("- Something ungrounded «the budget was approved» [?]\n");
        var unanchoredBullet = new AnswerBulletViewModel(
            CitationValidator.Validate(unanchored, transcript).Bullets[0], _ => { });
        Assert.False(unanchoredBullet.QuoteChecked);
        Assert.NotNull(unanchoredBullet.QuoteCaveat);
        Assert.DoesNotContain("not found", unanchoredBullet.QuoteCaveat, StringComparison.Ordinal);

        var wrongQuote = AnswerParser.Parse("- A wrong quote «entirely different words» [S1]\n");
        var wrongQuoteBullet = new AnswerBulletViewModel(
            CitationValidator.Validate(wrongQuote, transcript).Bullets[0], _ => { });
        Assert.True(wrongQuoteBullet.QuoteChecked);
        Assert.False(wrongQuoteBullet.QuoteVerified);
        Assert.Contains("not found", wrongQuoteBullet.QuoteCaveat, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedEngineIsDroppedSoTheNextQuestionStartsFresh()
    {
        var (chat, provider, _) = Chat(options: new FakeAnswerOptions { FailAfterChunks = 0 });

        await AskAsync(chat, "what about the axolotl?");

        var entry = Assert.Single(chat.Entries);
        Assert.NotNull(entry.Failure);
        Assert.False(entry.CanCopy);

        // The next ask does not reuse the dead engine.
        await AskAsync(chat, "again?");
        Assert.Equal(2, provider.Created);
    }
}

/// <summary>The same panel through the real window: layout, bindings, the keyboard.</summary>
public class AskChatWindowTests
{
    private static (MainWindow Window, MainWindowViewModel ViewModel, FakeMediaPlayer Player) Open(
        FakeAnswerOptions? options = null)
    {
        var player = new FakeMediaPlayer { DurationToReport = TimeSpan.FromMinutes(2) };
        var directory = Directory.CreateTempSubdirectory("uindosill-askchat").FullName;
        var viewModel = new MainWindowViewModel(
            new FakeEngineProvider(),
            new LocalModelStore(directory),
            ModelCatalog.Default,
            player: player,
            answerEngines: new FakeAnswerEngineProvider(options));

        viewModel.Transcribe.Jobs.Add(AskTabTests.Transcribed());
        viewModel.SelectedTab = 4;

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        return (window, viewModel, player);
    }

    [AvaloniaFact]
    public async Task AskingThroughTheWindowRendersBulletsAndAChipClickSeeksThePlayer()
    {
        var (window, viewModel, player) = Open();

        // Live: everything the panel needs exists, so the cover is down and the input takes.
        Assert.Null(viewModel.Ask.Chat.PanelNotice);
        Assert.False(window.FindControl<Border>("AskNotice")!.IsVisible);
        Assert.True(window.FindControl<DockPanel>("AskPanel")!.IsEnabled);

        // The fixture's segments say "one", "two", "three"; the question has to say one of them,
        // because a question retrieval cannot match is the abstain path, not an answer.
        viewModel.Ask.Chat.QuestionText = "tell me about two";
        await viewModel.Ask.Chat.AskCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var entry = Assert.Single(viewModel.Ask.Chat.Entries);
        Assert.True(entry.IsDone);

        var chip = entry.Bullets.SelectMany(b => b.Citations).First(c => c.IsResolved);
        chip.SeekCommand.Execute(null);

        // Through the same transport a clicked cue uses: the seek plays, because a citation chip
        // is a request to hear the claim.
        Assert.True(player.IsPlaying);
        Assert.True(player.Position >= TimeSpan.Zero);
    }

    [AvaloniaFact]
    public void EnterInTheAskBoxAsksTheQuestion()
    {
        var (window, viewModel, _) = Open();

        var input = window.FindControl<TextBox>("AskInput");
        Assert.NotNull(input);

        input!.Focus();
        input.Text = "what was said?";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(viewModel.Ask.Chat.Entries);
    }

    [AvaloniaFact]
    public async Task WhileAnAskRunsStopIsTheButtonAndEscapeStopsIt()
    {
        // During a 467.9-second measured prefill the panel used to be a dead room: StopCommand
        // existed and nothing was bound to it. The Stop button stands where Ask stood, and
        // Escape in the input is the keyboard's way to the same exit.
        var gate = new TaskCompletionSource();
        var (window, viewModel, _) = Open(new FakeAnswerOptions { LoadGate = gate.Task });

        viewModel.Ask.Chat.QuestionText = "tell me about two";
        var asking = viewModel.Ask.Chat.AskCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.False(window.FindControl<Button>("AskSend")!.IsVisible);
        Assert.True(window.FindControl<Button>("AskStop")!.IsVisible);

        window.FindControl<TextBox>("AskInput")!.Focus();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        await asking;

        var entry = Assert.Single(viewModel.Ask.Chat.Entries);
        Assert.Equal("Stopped.", entry.Failure);
        Assert.False(viewModel.Ask.Chat.IsAsking);

        window.UpdateLayout();
        Assert.True(window.FindControl<Button>("AskSend")!.IsVisible);
    }

    [AvaloniaFact]
    public void OpeningTheAskTabLooksAgainAtWhatThePanelNeeds()
    {
        // The cover tells the user to put a .gguf in the models folder "and come back here";
        // switching to the tab is the coming back, so it has to re-run the availability check
        // the way the Models tab re-reads its directory.
        var (_, viewModel, _) = Open();
        viewModel.SelectedTab = 0;

        var refreshed = false;
        viewModel.Ask.Chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AskChatViewModel.PanelNotice))
            {
                refreshed = true;
            }
        };

        viewModel.SelectedTab = 4;
        Assert.True(refreshed);
    }

    [AvaloniaFact]
    public void ASuggestionChipAsksItsQuestion()
    {
        var (window, viewModel, _) = Open();
        window.UpdateLayout();

        viewModel.Ask.Chat.AskSuggestionCommand.Execute(viewModel.Ask.Suggestions[0]);
        Dispatcher.UIThread.RunJobs();

        var entry = Assert.Single(viewModel.Ask.Chat.Entries);
        Assert.Equal(viewModel.Ask.Suggestions[0], entry.Question);
    }
}
