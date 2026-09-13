using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Threading;
using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.Core.Answers;
using Parakeet.Core.Jobs;
using Parakeet.Core.Models;
using Parakeet.Core.Retrieval;
using Parakeet.Core.Transcription;
using Parakeet.Engine.LlamaServer;

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

    [Fact]
    public void AProgressReportThatLandsAfterTheAnswerChangesNothing()
    {
        // Progress<T> posts each report through the synchronisation context, so the last prefill
        // report can arrive behind the completion that cleared the status line — and it wrote
        // "Reading the transcript… 100%" back under a finished answer, where it stayed. A done
        // entry has nothing left to report.
        var entry = new ChatEntryViewModel("q", _ => Task.CompletedTask);
        entry.OnProgress(new AskProgress { PrefillTokens = 5, PrefillTotalTokens = 10 });
        Assert.NotNull(entry.Status);

        entry.Fail("boom");
        Assert.Null(entry.Status);

        entry.OnProgress(new AskProgress { PrefillTokens = 10, PrefillTotalTokens = 10 });
        Assert.Null(entry.Status);

        entry.OnProgress(new AskProgress { ThinkingTokens = 1 });
        Assert.False(entry.IsThinking);
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

        // Pinned to retrieval: it is the one mode whose prompt still asks for a quote, and the
        // quote is what makes the decision observable here.
        var (chat, provider, _) = Chat(job);
        provider.ModePreference = AskModePreference.Retrieval;
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
    public async Task ATranscriptionStartingDuringTheStaleEngineDisposeDoesNotUnloadTheSessionUnderIt()
    {
        // The stale-engine dispose in EnsureEngineAsync is a real await — the old child is killed
        // and waited for — and it sits between the residency probe and the session unload. A
        // transcription starting inside that window cancels the ask, but a continuation that did
        // not look would unload the session anyway, disposing the engine the batch had just
        // borrowed, mid-decode. This drives the interleaving: the ask must come back "Stopped."
        // and the session must still be loaded.
        var running = false;
        var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ModelSession(new FakeEngineProvider());
        var (chat, provider, _) = Chat(
            session: session,
            transcriptionRunning: () => running,
            options: new FakeAnswerOptions { DisposeGate = disposeGate.Task });

        // A first question makes the ask engine resident; the Models tab then loads the
        // transcriber beside it, which nothing prevents — loading is that tab's job, and the ask
        // engine holds no lock on it.
        await AskAsync(chat, "what about the budget review?");
        await session.LoadAsync(new EngineSelection { Model = ModelCatalog.Default.Recommended });
        Assert.True(session.IsLoaded);

        // The flipped mode is what makes the second question dispose the resident engine rather
        // than reuse it — the same trigger the mode-flip test below drives.
        provider.ThinkingMode = true;
        chat.QuestionText = "and the axolotl?";
        var asking = chat.AskCommand.ExecuteAsync(null);
        Assert.True(chat.IsAsking);

        // The transcription starts while the dispose is still in flight: the window's wiring
        // cancels the ask the moment IsRunning goes true, exactly as MainWindowViewModel does.
        running = true;
        var released = chat.OnTranscriptionStartedAsync();

        disposeGate.SetResult();
        await asking;
        await released;

        Assert.True(session.IsLoaded);
        Assert.Equal("Stopped.", chat.Entries[^1].Failure);
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
    public async Task ChangingTheExpertPlacementRebuildsTheEngineAtTheNextQuestion()
    {
        // The placement is nothing but the child's environment, fixed when the process starts —
        // the most literal case of the rule the thinking toggle follows above. A panel that kept
        // its engine would leave the Settings picker doing nothing until the next transcription,
        // which is a control that silently lies about when it takes effect.
        var (chat, provider, _) = Chat();

        await AskAsync(chat, "what about the axolotl?");
        Assert.Equal(1, provider.Created);

        provider.ExpertPlacement = MoeExpertPlacement.Device;
        await AskAsync(chat, "and the budget?");
        Assert.Equal(2, provider.Created);

        // Unchanged between questions keeps the child, so an ask is not a reload.
        await AskAsync(chat, "who presented?");
        Assert.Equal(2, provider.Created);
    }

    [Fact]
    public async Task ChangingTheAskModelRebuildsTheEngineAtTheNextQuestion()
    {
        // Settings promises the picked model "is used from your next question" — the same
        // promise the thinking and placement controls above make and honour, and the least
        // negotiable of the set: the engine serves one file for its whole life. The comparison
        // skipped the model until 2026-08-30, so a mid-chat pick stayed inert until some
        // unrelated teardown, with only the provenance line telling on it.
        var (chat, provider, _) = Chat();

        await AskAsync(chat, "what about the axolotl?");
        Assert.Equal(1, provider.Created);

        provider.ModelFileName = "some-other-model.gguf";
        await AskAsync(chat, "and the budget?");
        Assert.Equal(2, provider.Created);

        // Unchanged between questions keeps the child, exactly as the other dials do.
        await AskAsync(chat, "who presented?");
        Assert.Equal(2, provider.Created);
    }

    [Fact]
    public async Task ALoadOnTheModelsTabBetweenQuestionsIsUnloadedByTheNextAsk()
    {
        // R9 says always — and the reuse fast path used to return before the unload, so a
        // transcriber loaded from the Models tab between questions stayed resident beside the
        // held engine for the rest of the session (found 2026-08-30). The next ask keeps its
        // engine and still enforces the rule, saying so.
        var session = new ModelSession(new FakeEngineProvider());
        var (chat, provider, _) = Chat(session: session);

        await AskAsync(chat, "what about the budget review?");
        Assert.Equal(1, provider.Created);

        await session.LoadAsync(new EngineSelection { Model = ModelCatalog.Default.Recommended });
        Assert.True(session.IsLoaded);

        await AskAsync(chat, "and the axolotl?");
        Assert.Equal(1, provider.Created);
        Assert.False(session.IsLoaded);
        Assert.Contains("unloaded to make room", chat.ResidencyNotice, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task AReleaseLandingInTheLoadsFinalUnwindStillEvictsTheEngine()
    {
        // The gate is released before the transcription starts, so the load completes without
        // ever observing the cancel — the unwind ordering where ReleaseEngineAsync used to find
        // no engine, dispose nothing, and the load then published a child that sat resident
        // through the very transcription that evicted it (found 2026-08-30). The publish-guard
        // is the only look that can catch it, and the dispatcher makes the ordering real: the
        // release runs before the load's posted continuation does.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (chat, provider, _) = Chat(options: new FakeAnswerOptions { LoadGate = gate.Task });

        chat.QuestionText = "what about the axolotl?";
        var asking = chat.AskCommand.ExecuteAsync(null);
        Assert.True(chat.IsAsking);

        gate.SetResult();
        var released = chat.OnTranscriptionStartedAsync();
        await asking;
        await released;

        Assert.Equal("Stopped.", Assert.Single(chat.Entries).Failure);

        // The proof the engine did not survive the unwind: the next question builds afresh.
        await AskAsync(chat, "and the budget?");
        Assert.Equal(2, provider.Created);
    }

    [Fact]
    public void ResetNullsTheDocumentsBeforeTheLinesAnnounceTheClear()
    {
        // The ask panel hears the line collections clear and re-reads the documents. Nulled
        // after the clear, they left the chat live over — its citation chips seeking into — a
        // transcript the row no longer showed (found 2026-08-30). The ordering is the contract,
        // so the ordering is what this pins.
        var job = Transcribed();
        var heardClear = false;
        TranscriptDocument? documentAtTheClear = job.Document;
        job.Lines.CollectionChanged += (_, _) =>
        {
            heardClear = true;
            documentAtTheClear = job.Document;
        };

        job.Reset();

        Assert.True(heardClear);
        Assert.Null(documentAtTheClear);
        Assert.Null(job.TranslatedDocument);
    }

    [Fact]
    public async Task WholeTranscriptModeSendsTheRecordingTiledOnceWithNoSourceRows()
    {
        // The opt-in the register's decision 3 names: no retrieval — every question is global to
        // it — and the evidence is the recording tiled once in the non-overlapping cover shape,
        // because the retrieval windows' overlap would send the transcript twice. The Sources
        // expander stays empty (the source is the whole recording, already on screen) and the
        // provenance line claims the coverage the answer really had.
        var (chat, provider, _) = Chat();
        provider.ModePreference = AskModePreference.WholeTranscript;

        await AskAsync(chat, "give me a summary");

        var request = provider.LastCreated!.LastRequest!;
        Assert.Equal(AnswerMode.WholeTranscript, request.Mode);

        var window = Assert.Single(request.Evidence);
        Assert.Equal(1, window.FirstSegment);
        Assert.Equal(3, window.LastSegment);

        var entry = Assert.Single(chat.Entries);
        Assert.True(entry.IsDone);
        Assert.True(entry.HasBullets);
        Assert.False(entry.HasSources);
        Assert.Contains("whole transcript", entry.ModelLine, StringComparison.Ordinal);
        Assert.DoesNotContain("retrieved", entry.ModelLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOverviewOpensWithAFramingSentenceThatCarriesItsOwnChips()
    {
        // The shape a summary is read in: a sentence saying what the recording is, above the
        // claims. It is a claim itself, so it resolves and seeks exactly as a bullet does —
        // a lead exempt from the citation rule would be the one paragraph nobody could check.
        var (chat, provider, seeks) = Chat();
        provider.ModePreference = AskModePreference.WholeTranscript;

        await AskAsync(chat, "give me a summary");

        var entry = Assert.Single(chat.Entries);
        Assert.True(entry.HasLead);
        Assert.NotNull(entry.Lead);
        Assert.False(entry.Lead!.IsUncited);
        Assert.Null(entry.Lead.Label);

        var chip = entry.Lead.Citations.First(c => c.IsResolved);
        chip.SeekCommand.Execute(null);
        Assert.Single(seeks);

        // And the lead is not also one of the claims below it.
        Assert.DoesNotContain(entry.Bullets, b => b.Text == entry.Lead.Text);
    }

    [Fact]
    public async Task RetrievalAnswersOpenWithTheLeadTheirPromptAsksFor()
    {
        // The opening sentence has belonged to both modes since 2026-08-25, and the fake now
        // hands retrieval one too — this panel shape shipped exercised only on the overview
        // path until 2026-08-30. Where a lead was not asked for, stray prose keeps rendering
        // as the uncited claim it is rather than being promoted to unmarked prose.
        var (chat, _, _) = Chat();

        await AskAsync(chat, "what about the axolotl?");

        var entry = Assert.Single(chat.Entries);
        Assert.True(entry.HasLead);
        Assert.NotNull(entry.Lead);

        var parsed = AnswerParser.Parse("Here is what I found\n- A claim [S1]\n");
        Assert.Null(parsed.Lead);
        Assert.Equal(2, parsed.Bullets.Count);
        Assert.True(parsed.Bullets[0].IsUncited);
    }

    [Fact]
    public async Task TheCopiedOverviewLeadsWithTheFramingSentenceAndItsTimes()
    {
        var (chat, provider, _) = Chat();
        provider.ModePreference = AskModePreference.WholeTranscript;

        string? copied = null;
        chat.CopyToClipboard = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        await AskAsync(chat, "give me a summary");
        var entry = Assert.Single(chat.Entries);
        await entry.CopyCommand.ExecuteAsync(null);

        Assert.NotNull(copied);

        // The marker still comes first; the framing sentence sits above the claims, with the
        // times resolved by the application rather than written by the model.
        Assert.StartsWith("Generated by a language model", copied, StringComparison.Ordinal);
        var lead = copied!.IndexOf(entry.Lead!.Text, StringComparison.Ordinal);
        var firstClaim = copied.IndexOf("- ", StringComparison.Ordinal);
        Assert.True(lead > 0 && lead < firstClaim, "the framing sentence did not lead the claims");
        Assert.Contains("whole transcript", copied, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUncitedLeadIsMarkedInTheEmailExactlyAsItIsOnScreen()
    {
        // Observed on the first real overview, 2026-08-25: the 26B wrote a good framing sentence
        // and cited nothing. The panel draws [unverified] on it; the copied form used to print
        // it bare, so the same sentence read MORE confident in an email than in the application
        // — the inverse of the rule the bullet marker exists for.
        var transcript = new TranscriptDocument
        {
            SourceName = "/tmp/a.wav",
            AudioDuration = TimeSpan.FromSeconds(10),
            Segments = [new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "the budget was approved" }],
        };

        var answer = AnswerParser.Parse(
            "This recording is about a budget\n- Budget: it was approved [S1]\n", allowLead: true);
        var entry = new ChatEntryViewModel("give me a summary", _ => Task.CompletedTask);

        string? copied = null;
        var copying = new ChatEntryViewModel("give me a summary", text =>
        {
            copied = text;
            return Task.CompletedTask;
        });

        copying.Complete(answer, CitationValidator.Validate(answer, transcript), [], transcript, _ => { });
        copying.CopyCommand.Execute(null);

        Assert.NotNull(copied);

        // In the lead's own words, not a bullet's — the maintainer's decision, 2026-08-25: an
        // opening sentence that cites nothing failed no check, because there was none to fail.
        // The marker stays (a lead can assert more than its bullets support); the wording says
        // what is true of it. Screen and email read from the same constant so they cannot drift.
        Assert.Contains(
            ChatEntryViewModel.LeadUncitedNotice + " This recording is about a budget",
            copied,
            StringComparison.Ordinal);
        Assert.DoesNotContain(ChatEntryViewModel.UncitedNotice, copied, StringComparison.Ordinal);

        // And a lead that does cite carries its times instead of the marker.
        var cited = AnswerParser.Parse(
            "This recording is about a budget [S1]\n- Budget: it was approved [S1]\n", allowLead: true);
        entry.Complete(cited, CitationValidator.Validate(cited, transcript), [], transcript, _ => { });
        Assert.False(entry.Lead!.IsUncited);
    }

    [Theory]
    [InlineData("They said «the budget was rejected» [S1]", false, "the quoted words are not at the time cited")]
    [InlineData("They said \"the budget was approved\" [S1]", false, "the quoted words here were not checked")]
    [InlineData("They said «the budget was approved» [?]", false, "quote not checked: no place in the recording to check it against")]
    [InlineData("They said «the budget was approved» [S1]", true, "quote not checked: cited part was not shown to the model")]
    public async Task ALeadsQuoteCaveatTravelsIntoTheCopiedAnswer(string lead, bool unshown, string caveat)
    {
        var transcript = new TranscriptDocument
        {
            AudioDuration = TimeSpan.FromSeconds(10),
            Segments = [new TranscriptSegment
            {
                Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "the budget was approved",
            }],
        };
        var answer = AnswerParser.Parse(lead + "\n- Budget: an update was discussed [S1]\n", allowLead: true);
        string? copied = null;
        var entry = new ChatEntryViewModel("give me a summary", text =>
        {
            copied = text;
            return Task.CompletedTask;
        });
        entry.Complete(answer,
            CitationValidator.Validate(answer, transcript, shownEvidence: unshown ? [] : null),
            [], transcript, _ => { });

        await entry.CopyCommand.ExecuteAsync(null);

        Assert.Equal(caveat, entry.Lead!.QuoteCaveat);
        Assert.NotNull(copied);
        // Check the lead's own line: a caveat on a later bullet would not warn about this quote.
        var leadLine = Assert.Single(copied.Split('\n'), line => line.Contains(entry.Lead.Text, StringComparison.Ordinal));
        Assert.Contains("[" + caveat + "]", leadLine, StringComparison.Ordinal);
        if (unshown)
        {
            Assert.Contains("[the model cited a part it was not shown]", leadLine, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheQuestionPicksTheModeWhenNobodyHasPickedOne()
    {
        // Automatic is the shipped setting, so this is the default path through the panel: a
        // summary reads the recording, a pointed question retrieves, and neither needed anyone
        // to know that those are different tiers.
        var (chat, provider, _) = Chat();
        Assert.Equal(AskModePreference.Automatic, provider.ModePreference);

        await AskAsync(chat, "give me a summary");
        Assert.Equal(AnswerMode.WholeTranscript, provider.LastCreated!.LastRequest!.Mode);

        await AskAsync(chat, "what did maria present about the axolotl?");
        Assert.Equal(AnswerMode.Retrieval, provider.LastCreated!.LastRequest!.Mode);

        // Nothing was routed against the person's back: no notice on either, because the
        // answer's own provenance line already says which mode produced it.
        Assert.All(chat.Entries, e => Assert.Null(e.RoutingNotice));
    }

    [Fact]
    public async Task AFixedSettingOverrulesTheQuestion()
    {
        // The two fixed settings exist for someone who would rather decide once, and they are
        // not advisory: a summary asked under "the parts that matched" retrieves.
        var (chat, provider, _) = Chat();

        // "summarise" is a global cue, so Automatic would read the recording; pinned to the
        // matched parts it retrieves, and the axolotl in the question gives it something to
        // match so the difference is visible rather than swallowed by an abstention.
        provider.ModePreference = AskModePreference.Retrieval;
        await AskAsync(chat, "summarise what maria said about the axolotl");
        Assert.Equal(AnswerMode.Retrieval, provider.LastCreated!.LastRequest!.Mode);

        provider.ModePreference = AskModePreference.WholeTranscript;
        await AskAsync(chat, "what did maria present about the axolotl?");
        Assert.Equal(AnswerMode.WholeTranscript, provider.LastCreated!.LastRequest!.Mode);
    }

    [Fact]
    public async Task ASectionSummaryNamesItsCoverageOnScreenAndInTheCopiedAnswer()
    {
        var (chat, provider) = SectionChat();
        string? copied = null;
        chat.CopyToClipboard = text => { copied = text; return Task.CompletedTask; };
        await AskAsync(chat, "give me a summary");

        Assert.Equal(AnswerMode.MapReduce, provider.Requests[^1].Mode);

        var entry = Assert.Single(chat.Entries);
        Assert.Null(entry.Failure);
        Assert.Contains("from summaries of every transcript section", entry.ModelLine, StringComparison.Ordinal);
        await entry.CopyCommand.ExecuteAsync(null);
        Assert.NotNull(copied);
        Assert.Contains("from summaries of every transcript section", copied, StringComparison.Ordinal);
        Assert.DoesNotContain("even sample", copied, StringComparison.Ordinal);
        Assert.DoesNotContain("Generated section note", copied, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALongAutomaticSummaryReadsEverySectionBeforeSynthesizingOriginalCitations()
    {
        var (chat, provider) = SectionChat();
        await AskAsync(chat, "give me a summary");

        var entry = Assert.Single(chat.Entries);
        Assert.Null(entry.Failure);
        Assert.False(entry.Abstained);

        var sections = provider.Requests.Where(r => r.SummaryStage == SummaryStage.Section).ToArray();
        Assert.True(sections.Length > 1, "a long transcript must be read in several bounded requests");
        Assert.All(sections, request =>
        {
            Assert.Equal(AnswerMode.MapReduce, request.Mode);
            Assert.True(request.Evidence.Sum(w => w.Text.Length) <= 24_000);
            Assert.True(string.IsNullOrEmpty(request.SummaryNotes));
        });
        var readSegments = sections.SelectMany(r => r.Evidence)
            .SelectMany(w => Enumerable.Range(w.FirstSegment, w.LastSegment - w.FirstSegment + 1));
        Assert.Equal(Enumerable.Range(1, 120), readSegments);

        var synthesis = provider.Requests[^1];
        Assert.Equal(SummaryStage.Synthesis, synthesis.SummaryStage);
        Assert.Contains("Generated section note", synthesis.SummaryNotes, StringComparison.Ordinal);
        Assert.All(synthesis.Evidence, window => Assert.Equal(
            TranscriptWindowBuilder.FromRun(synthesis.Transcript, window.FirstSegment, window.LastSegment),
            window));
        Assert.All(entry.Bullets.SelectMany(b => b.Citations), chip => Assert.True(chip.IsResolved));
        Assert.Equal(
            "This recording is long, so I’m reading it in sections before combining the summary.",
            entry.RoutingNotice);

        // The explicit setting still performs one direct whole-transcript request.
        var requestsBefore = provider.Requests.Count;
        provider.ModePreference = AskModePreference.WholeTranscript;
        await AskAsync(chat, "give me a summary");
        Assert.Equal(requestsBefore + 1, provider.Requests.Count);
        Assert.Equal(AnswerMode.WholeTranscript, provider.Requests[^1].Mode);
        Assert.Null(chat.Entries[^1].RoutingNotice);
    }

    [Theory]
    [InlineData("what about the budget?", false)]
    [InlineData("Summarize the video", false)]
    [InlineData("Summarize the video", true)]
    public async Task StoppingAsTheFinalFrameIsDisplayedDoesNotCompleteTheAnswer(string question, bool longRecording)
    {
        var (chat, provider) = SectionChat();
        if (!longRecording)
        {
            chat.SetRecording(Transcribed());
        }

        var stoppedOnFrame = false;
        chat.Entries.CollectionChanged += (_, change) =>
        {
            foreach (ChatEntryViewModel entry in change.NewItems!)
            {
                entry.PropertyChanged += (_, property) =>
                {
                    if (property.PropertyName == nameof(ChatEntryViewModel.StreamingText)
                        && entry.StreamingText is not null)
                    {
                        stoppedOnFrame = true;
                        chat.StopCommand.Execute(null);
                    }
                };
            }
        };

        // The test engine returns after its final yield without another token check, as a
        // completed response may do. The application must honor Stop before it publishes it.
        await AskAsync(chat, question);

        Assert.True(stoppedOnFrame);
        var stopped = Assert.Single(chat.Entries);
        Assert.Equal("Stopped.", stopped.Failure);
        Assert.False(chat.IsAsking);
        Assert.Null(stopped.StreamingText);
        Assert.Null(stopped.Lead);
        Assert.Empty(stopped.Bullets);
        Assert.Empty(stopped.Sources);
        Assert.False(stopped.CanCopy);
        Assert.Equal(longRecording, provider.Requests.Any(request => request.SummaryStage == SummaryStage.Synthesis));
    }

    [Fact]
    public async Task StoppingDuringASecondSectionDoesNotPublishTheFirstSectionsNotes()
    {
        var secondSection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seenSections = 0;
        var (chat, provider) = SectionChat(async (request, _, ct) =>
        {
            if (request.SummaryStage == SummaryStage.Section && ++seenSections == 2)
            {
                secondSection.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return SectionAnswer(request);
        });

        chat.QuestionText = "Summarize the video";
        var asking = chat.AskCommand.ExecuteAsync(null);
        await secondSection.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var entry = Assert.Single(chat.Entries);
        Assert.False(entry.IsDone);
        Assert.Empty(entry.Bullets);
        Assert.Null(entry.Lead);
        Assert.Null(entry.StreamingText);
        Assert.False(entry.CanCopy);
        chat.StopCommand.Execute(null);
        await asking;

        Assert.Equal("Stopped.", entry.Failure);
        Assert.False(chat.IsAsking);
        Assert.Empty(entry.Bullets);
        Assert.False(entry.CanCopy);
        Assert.DoesNotContain(provider.Requests, r => r.SummaryStage == SummaryStage.Synthesis);
    }

    [Fact]
    public async Task SwitchingRecordingsDuringASectionDiscardsItsNotesAndUsesTheNewTranscript()
    {
        var secondSection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seenSections = 0;
        var (chat, provider) = SectionChat(async (request, _, ct) =>
        {
            if (request.SummaryStage == SummaryStage.Section && ++seenSections == 2)
            {
                secondSection.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return SectionAnswer(request);
        });

        chat.QuestionText = "Summarize the video";
        var asking = chat.AskCommand.ExecuteAsync(null);
        await secondSection.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var previousEntry = Assert.Single(chat.Entries);
        chat.SetRecording(Transcribed("/tmp/new-recording.wav"));
        await asking;

        Assert.Empty(chat.Entries);
        Assert.False(previousEntry.CanCopy);
        Assert.DoesNotContain(provider.Requests, r => r.SummaryStage == SummaryStage.Synthesis);
        await AskAsync(chat, "what about the axolotl?");
        var request = provider.Requests[^1];
        Assert.Equal("/tmp/new-recording.wav", request.Transcript.SourceName);
        Assert.Equal(AnswerMode.Retrieval, request.Mode);
        Assert.True(string.IsNullOrEmpty(request.SummaryNotes));
        Assert.Contains(request.Evidence, w => w.Text.Contains("axolotl", StringComparison.Ordinal));
        Assert.Null(Assert.Single(chat.Entries).Failure);
    }

    [Fact]
    public async Task ASynthesisFailureCannotBeCopiedAsACompleteSummaryAndDropsTheEngine()
    {
        var (chat, provider) = SectionChat((request, _, _) => request.SummaryStage == SummaryStage.Synthesis
            ? Task.FromException<string>(new InvalidOperationException("The synthesizer failed."))
            : Task.FromResult(SectionAnswer(request)));

        await AskAsync(chat, "Summarize the video");
        var entry = Assert.Single(chat.Entries);
        Assert.Equal("The synthesizer failed.", entry.Failure);
        Assert.False(entry.Abstained);
        Assert.Empty(entry.Bullets);
        Assert.Null(entry.Lead);
        Assert.False(entry.CanCopy);
        Assert.True(provider.Disposed > 0);

        var createdBefore = provider.Created;
        chat.SetRecording(Transcribed());
        await AskAsync(chat, "what about the axolotl?");
        Assert.Equal(createdBefore + 1, provider.Created);
        Assert.Null(Assert.Single(chat.Entries).Failure);
    }

    [Theory]
    [InlineData("S9999")]
    [InlineData("S120")]
    public async Task ASectionCitationThatIsInventedOrNotShownStopsTheSummary(string citation)
    {
        // S120 exists in this transcript but is outside the first section. Resolving an id in
        // the whole document alone cannot establish that this pass had that evidence.
        var (chat, provider) = SectionChat((request, _, _) => Task.FromResult(
            request.SummaryStage == SummaryStage.Section
                ? $"- Fabricated: a claim the section cannot support [{citation}]\n"
                : SectionAnswer(request)));

        await AskAsync(chat, "Summarize the video");
        var entry = Assert.Single(chat.Entries);
        Assert.NotNull(entry.Failure);
        Assert.False(entry.Abstained);
        Assert.Empty(entry.Bullets);
        Assert.Null(entry.Lead);
        Assert.False(entry.CanCopy);
        Assert.Single(provider.Requests);
        Assert.DoesNotContain(provider.Requests, r => r.SummaryStage == SummaryStage.Synthesis);
    }

    [Fact]
    public async Task AValidButUnshownRetrievalCitationIsMarkedAndCannotSeekOrCopyATimestamp()
    {
        var seeks = new List<TimeSpan>();
        var (chat, _) = SectionChat((_, _, _) =>
            Task.FromResult("- Hidden: they discussed «quarterly budget review» [S120]\n"), seeks.Add);
        string? copied = null;
        chat.CopyToClipboard = text => { copied = text; return Task.CompletedTask; };

        await AskAsync(chat, "what about the quarterly budget?");

        var entry = Assert.Single(chat.Entries);
        Assert.Null(entry.Failure);
        var bullet = Assert.Single(entry.Bullets);
        Assert.True(bullet.IsUncited);
        Assert.False(bullet.QuoteVerified);
        var chip = Assert.Single(bullet.Citations);
        Assert.False(chip.IsResolved);
        Assert.Equal("?", chip.Display);
        Assert.Equal("The model cited a part it was not shown.", chip.Detail);
        chip.SeekCommand.Execute(null);
        Assert.Empty(seeks);
        await entry.CopyCommand.ExecuteAsync(null);
        Assert.Contains("[unverified]", copied, StringComparison.Ordinal);
        Assert.Contains("the model cited a part it was not shown", copied, StringComparison.Ordinal);
        Assert.DoesNotContain("19:50", copied, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFinalSummaryWithOneBroadenedCitationKeepsTheAnswerAndMarksThatCitation()
    {
        var (chat, _) = SectionChat((request, _, _) => Task.FromResult(
            request.SummaryStage == SummaryStage.Section
                ? $"- Section: a supported note [{request.Evidence[0].CitationId}]\n"
                : $"- Broad: an overly broad reference [S1-S120]\n- Kept: a supported point [{request.Evidence[0].CitationId}]\n"));

        await AskAsync(chat, "Summarize the video");

        var entry = Assert.Single(chat.Entries);
        Assert.Null(entry.Failure);
        Assert.True(entry.IsDone);
        Assert.True(entry.CanCopy);
        Assert.Equal(2, entry.Bullets.Count);
        Assert.True(entry.Bullets[0].IsUncited);
        Assert.Equal("?", Assert.Single(entry.Bullets[0].Citations).Display);
        Assert.False(entry.Bullets[1].IsUncited);
        Assert.True(Assert.Single(entry.Bullets[1].Citations).IsResolved);
    }

    [Fact]
    public async Task ARealAbstentionIsStillAnAbstention()
    {
        // The guard above must not swallow the honest case: a pointed question about something
        // the recording never covers is answered by retrieval coming up empty, and that IS a
        // claim about the recording — the one the abstention sentence exists to make.
        var (chat, provider, _) = Chat();

        await AskAsync(chat, "did they mention reggie?");

        var entry = Assert.Single(chat.Entries);
        Assert.True(entry.Abstained);
        Assert.Null(entry.Failure);
        Assert.Null(entry.RoutingNotice);
        Assert.Equal(0, provider.Created);
    }

    [Fact]
    public async Task AShortRecordingKeepsItsEngineAcrossTheModeFlip()
    {
        // Unlike thinking, the mode is a per-request fact, not a child-process argument: on a
        // recording whose prompt fits the retrieval-tier context either way, flipping the toggle
        // changes the next request and keeps the engine that is already loaded.
        var (chat, provider, _) = Chat();

        await AskAsync(chat, "what about the axolotl?");
        Assert.Equal(1, provider.Created);
        Assert.Equal(AnswerMode.Retrieval, provider.LastCreated!.LastRequest!.Mode);

        provider.ModePreference = AskModePreference.WholeTranscript;
        await AskAsync(chat, "give me a summary");
        Assert.Equal(1, provider.Created);
        Assert.Equal(AnswerMode.WholeTranscript, provider.LastCreated!.LastRequest!.Mode);

        provider.ModePreference = AskModePreference.Retrieval;
        await AskAsync(chat, "and the budget?");
        Assert.Equal(1, provider.Created);
        Assert.Equal(AnswerMode.Retrieval, provider.LastCreated!.LastRequest!.Mode);
    }

    [Fact]
    public async Task ALongRecordingRebuildsTheEngineWhenTheContextItNeedsChanges()
    {
        // The whole-transcript prompt on a long recording outgrows the retrieval-tier context,
        // so entering the mode rebuilds the engine sized to the recording — and leaving it
        // rebuilds again at the floor, because a whole-transcript KV cache kept past its ask is
        // memory held for nothing.
        var segments = new List<TranscriptSegment>();
        for (var i = 0; i < 100; i++)
        {
            segments.Add(new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(i * 10),
                End = TimeSpan.FromSeconds((i * 10) + 10),
                Text = $"filler segment {i} " + new string('x', 480),
            });
        }

        var job = new JobViewModel("/tmp/long.wav");
        job.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = job.Path },
            State = JobState.Completed,
            Document = new TranscriptDocument
            {
                SourceName = job.Path,
                AudioDuration = TimeSpan.FromSeconds(1_000),
                Segments = segments,
            },
        });

        var (chat, provider, _) = Chat(job);
        provider.ModePreference = AskModePreference.WholeTranscript;

        await AskAsync(chat, "give me a summary");
        Assert.Equal(1, provider.Created);
        Assert.True(
            AnswerContextBudget.ContextTokensFor(provider.LastPromptChars) > AnswerContextBudget.Minimum,
            "the whole-transcript prompt was meant to outgrow the retrieval-tier context");

        // The same mode over the same recording needs the same context: no rebuild.
        await AskAsync(chat, "main topics?");
        Assert.Equal(1, provider.Created);

        provider.ModePreference = AskModePreference.Retrieval;
        await AskAsync(chat, "what about filler segment three?");
        Assert.Equal(2, provider.Created);
        Assert.Equal(
            AnswerContextBudget.Minimum, AnswerContextBudget.ContextTokensFor(provider.LastPromptChars));
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
        Assert.StartsWith("Generated by a language model, not transcribed speech.", copied, StringComparison.Ordinal);

        // Plain timestamps, never clickable references: an email carries neither the app nor
        // the audio.
        Assert.Contains("[0", copied, StringComparison.Ordinal);

        // Both models: the one that generated and the one that transcribed, and the source.
        Assert.Contains("fake-answer-model", copied, StringComparison.Ordinal);
        Assert.Contains("parakeet-tdt-0.6b-v3-q8_0", copied, StringComparison.Ordinal);
        Assert.Contains("/tmp/a.wav", copied, StringComparison.Ordinal);

        // And the transcript pin (decision 5): the segmentation the timestamps resolve against,
        // as segment count and hash prefix — the source name and ASR model alone cannot say
        // which segmentation, and a re-transcription moves every id while looking fine.
        Assert.Contains("(3 segments, sha256 ", copied, StringComparison.Ordinal);
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

        // Two different nothings, said differently. With no recording chosen at all there is no
        // "this recording" to be sent off and transcribe, so the panel says to choose one — the
        // same distinction AskViewModel.TranscriptNotice draws for the tab's other half.
        var noRecording = new AskChatViewModel(new FakeAnswerEngineProvider(), null, null, null);
        Assert.Contains("Choose a recording", noRecording.PanelNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("this recording first", noRecording.PanelNotice, StringComparison.Ordinal);
        Assert.False(noRecording.IsPanelEnabled);

        // And it survives the selection being cleared, which is how a person gets back here.
        noRecording.SetRecording(Transcribed());
        noRecording.SetRecording(null);
        Assert.Contains("Choose a recording", noRecording.PanelNotice, StringComparison.Ordinal);

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
        Assert.Contains("not checked", unanchoredBullet.QuoteCaveat, StringComparison.Ordinal);

        var wrongQuote = AnswerParser.Parse("- A wrong quote «entirely different words» [S1]\n");
        var wrongQuoteBullet = new AnswerBulletViewModel(
            CitationValidator.Validate(wrongQuote, transcript).Bullets[0], _ => { });
        Assert.True(wrongQuoteBullet.QuoteChecked);
        Assert.False(wrongQuoteBullet.QuoteVerified);

        // And it says *at the time cited*, not *in the transcript*: the check runs against the
        // span the citation names, and the words are often really in the recording seconds away
        // — a real bullet quoted "Just Ship It mentality" from 09:57 under a citation covering
        // 10:00 onwards (2026-08-25). Calling that absent from the transcript is a claim about
        // the recording that this check never made.
        Assert.Contains("at the time cited", wrongQuoteBullet.QuoteCaveat, StringComparison.Ordinal);
        Assert.DoesNotContain("transcript", wrongQuoteBullet.QuoteCaveat, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotedWordsOutsideTheConventionAreSaidToBeUnchecked()
    {
        // The 9B quoted the transcript in ordinary marks on seven of ten bullets (2026-08-25),
        // which this parser does not lift and this check therefore never saw. Left alone, such
        // a bullet showed quoted words beside a citation chip with nothing saying they were
        // unchecked — the "unverified text dressed as transcript" the panel promises never to
        // show. The maintainer's decision: say so, rather than guess the words were meant as a
        // transcript quote and risk accusing a title of not being at its cited time.
        var transcript = new TranscriptDocument
        {
            AudioDuration = TimeSpan.FromSeconds(10),
            Segments = [new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "the budget was approved" }],
        };

        var straight = AnswerParser.Parse("- They said \"the budget was approved\" plainly [S1]\n");
        var bullet = new AnswerBulletViewModel(
            CitationValidator.Validate(straight, transcript).Bullets[0], _ => { });

        Assert.Null(bullet.Quote);
        Assert.True(bullet.HasUncheckedQuotedText);
        Assert.Equal("the quoted words here were not checked", bullet.QuoteCaveat);

        // A bullet with no quotation of any kind says nothing — the caveat is for quoted words,
        // not for every uncited sentence.
        var plain = AnswerParser.Parse("- They approved the budget [S1]\n");
        var plainBullet = new AnswerBulletViewModel(
            CitationValidator.Validate(plain, transcript).Bullets[0], _ => { });
        Assert.False(plainBullet.HasUncheckedQuotedText);
        Assert.Null(plainBullet.QuoteCaveat);

        // And a bullet that used the convention is checked as before, not merely reported on.
        var proper = AnswerParser.Parse("- They said «the budget was approved» [S1]\n");
        var properBullet = new AnswerBulletViewModel(
            CitationValidator.Validate(proper, transcript).Bullets[0], _ => { });
        Assert.False(properBullet.HasUncheckedQuotedText);
        Assert.True(properBullet.QuoteVerified);
        Assert.Null(properBullet.QuoteCaveat);
    }

    [Fact]
    public async Task TheUncheckedQuoteCaveatTravelsIntoTheEmail()
    {
        // A claim must not read more confident away from the application than inside it, and an
        // email carries no tooltip.
        var (chat, _, _) = Chat(options: new FakeAnswerOptions { StraightQuotes = true });

        string? copied = null;
        chat.CopyToClipboard = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        await AskAsync(chat, "what did maria present about the axolotl?");
        var entry = Assert.Single(chat.Entries);
        await entry.CopyCommand.ExecuteAsync(null);

        Assert.NotNull(copied);
        Assert.Contains("[the quoted words here were not checked]", copied, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUncheckableQuoteCaveatTravelsIntoTheEmailToo()
    {
        // The third of the panel's three quote states — a «quote» whose only citation is [?],
        // so there was no span to check it against — showed its caveat on screen and dropped it
        // from the copy until 2026-08-30, letting the same sentence read more confident away
        // from the application than inside it. The shape is real 9B output, not a hypothetical.
        var transcript = new TranscriptDocument
        {
            SourceName = "meeting.wav",
            AudioDuration = TimeSpan.FromSeconds(10),
            Segments = [new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "the budget was approved" }],
        };

        string? copied = null;
        var entry = new ChatEntryViewModel("q", text =>
        {
            copied = text;
            return Task.CompletedTask;
        });

        var answer = AnswerParser.Parse("- Something ungrounded «the budget was approved» [?]\n");
        entry.Complete(answer, CitationValidator.Validate(answer, transcript), [], transcript, _ => { });
        entry.CopyCommand.Execute(null);

        Assert.NotNull(copied);
        Assert.Contains("[quote not checked: no place in the recording to check it against]", copied, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWhoSaidQuestionYieldsARangeAndAQuoteNeverAName()
    {
        // Decision 6's in-suite bullet, unexercised until 2026-08-30. The transcript carries the
        // diariser's label and a reader-typed name, and neither reaches the evidence — the
        // 2026-08-24 no-speaker-labels decision — so neither can reach the answer: what arrives
        // is a range that seeks and a quote, and the render's speaker chips are where the reader
        // learns who spoke, as the diariser's claim rather than the model's.
        var path = "/tmp/diarised.wav";
        var job = new JobViewModel(path);
        job.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = path },
            State = JobState.Completed,
            Document = new TranscriptDocument
            {
                SourceName = path,
                AudioDuration = TimeSpan.FromSeconds(20),
                Segments =
                [
                    new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "the budget was approved this morning", Speaker = "Maria" },
                    new TranscriptSegment { Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(20), Text = "then everyone adjourned for lunch", Speaker = "SPEAKER_01" },
                ],
            },
        });

        var (chat, _, _) = Chat(job);
        await AskAsync(chat, "who said the budget was approved?");

        var entry = Assert.Single(chat.Entries);
        Assert.True(entry.HasBullets);
        Assert.Contains(entry.Bullets.SelectMany(b => b.Citations), c => c.IsResolved);
        Assert.Contains(entry.Bullets, b => b.Quote is not null);

        var rendered = string.Join('\n',
            entry.Bullets.Select(b => b.Text + " " + b.Quote).Append(entry.Lead?.Text ?? string.Empty));
        Assert.DoesNotContain("Maria", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("SPEAKER", rendered, StringComparison.Ordinal);
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

    private static (AskChatViewModel Chat, SectionAnswerProvider Provider) SectionChat(
        Func<AskRequest, int, CancellationToken, Task<string>>? answer = null,
        Action<TimeSpan>? seek = null)
    {
        var provider = new SectionAnswerProvider(answer);
        var chat = new AskChatViewModel(provider, null, null, seek ?? (_ => { }));
        var path = "/tmp/long.wav";
        var job = new JobViewModel(path);
        job.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = path },
            State = JobState.Completed,
            Document = new TranscriptDocument
            {
                SourceName = path,
                AudioDuration = TimeSpan.FromSeconds(1_200),
                Segments = Enumerable.Range(0, 120).Select(i => new TranscriptSegment
                {
                    Start = TimeSpan.FromSeconds(i * 10),
                    End = TimeSpan.FromSeconds((i + 1) * 10),
                    Text = $"segment {i} about the quarterly budget review " + new string('x', 420),
                }).ToArray(),
            },
        });
        chat.SetRecording(job);
        return (chat, provider);
    }

    private static string SectionAnswer(AskRequest request)
    {
        var text = request.SummaryStage == SummaryStage.Section
            ? "Generated section note" : "Final synthesized topic";
        return string.Join('\n', request.Evidence.Select(window =>
            $"- Topic: {text} [{window.CitationId}]")) + "\n";
    }

    /// <summary>Records every pass and lets a test block or fail at a specific pipeline stage.</summary>
    private sealed class SectionAnswerProvider(
        Func<AskRequest, int, CancellationToken, Task<string>>? answer) : IAnswerEngineProvider
    {
        public List<AskRequest> Requests { get; } = [];
        public int Created { get; private set; }
        public int Disposed { get; private set; }
        public bool ThinkingMode => false;
        public AskModePreference ModePreference { get; set; } = AskModePreference.Automatic;
        public MoeExpertPlacement ExpertPlacement => MoeExpertPlacement.Automatic;
        public int EvidenceWindows => 8;
        public AnswerEngineAvailability Check() => new() { ModelFileName = "section-test-model.gguf" };
        public IAnswerEngine Create(int promptChars = 0)
        {
            Created++;
            return new SectionAnswerEngine(this, answer);
        }

        private sealed class SectionAnswerEngine(SectionAnswerProvider provider,
            Func<AskRequest, int, CancellationToken, Task<string>>? answer) : IAnswerEngine
        {
            private bool loaded;
            private bool disposed;
            public AnswerEngineCapabilities Capabilities { get; } = new()
            {
                EngineName = "section-test", ModelId = "section-test-model", Backend = ComputeBackend.Cpu,
            };

            public ValueTask LoadAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                loaded = true;
                return ValueTask.CompletedTask;
            }

            public async IAsyncEnumerable<string> AskAsync(AskRequest request,
                IProgress<AskProgress>? progress = null,
                [EnumeratorCancellation] CancellationToken ct = default)
            {
                if (!loaded || disposed)
                {
                    throw new InvalidOperationException("The test engine must be loaded and alive.");
                }
                ct.ThrowIfCancellationRequested();
                provider.Requests.Add(request);
                var response = answer is null ? SectionAnswer(request)
                    : await answer(request, provider.Requests.Count, ct);
                ct.ThrowIfCancellationRequested();
                yield return response;
            }

            public ValueTask DisposeAsync()
            {
                if (!disposed)
                {
                    disposed = true;
                    provider.Disposed++;
                }
                return ValueTask.CompletedTask;
            }
        }
    }
}

/// <summary>The arithmetic both halves of the rebuild decision share.</summary>
public class AnswerContextBudgetTests
{
    [Fact]
    public void RetrievalScalePromptsLandOnTheFloor()
    {
        // The retrieval tier's ~2k-token evidence, and anything near it, maps to the default
        // the engine has always run at — the budget only ever grows past it.
        Assert.Equal(AnswerContextBudget.Minimum, AnswerContextBudget.ContextTokensFor(0));
        Assert.Equal(AnswerContextBudget.Minimum, AnswerContextBudget.ContextTokensFor(10_000));
        Assert.Equal(AnswerContextBudget.Minimum, AnswerContextBudget.ContextTokensFor(30_000));
    }

    [Fact]
    public void ALongTranscriptGetsTheEstimateTheMarginAndTheGenerationAllowance()
    {
        // 207,000 chars is the measured three-hour transcript's scale: chars/4 estimates
        // 51,750 tokens, the quarter margin adds 12,937, the flat allowance 4,096, and the sum
        // lands on the next 4,096 boundary. The point pinned is the shape, not the constant.
        Assert.Equal(69_632, AnswerContextBudget.ContextTokensFor(207_000));
    }

    [Fact]
    public void TheResultIsAlwaysAWholeNumberOfPages()
    {
        for (var chars = 0; chars < 400_000; chars += 17_321)
        {
            Assert.Equal(0, AnswerContextBudget.ContextTokensFor(chars) % 4_096);
        }
    }

    [Fact]
    public void TheBudgetAlwaysClearsTheEnginesOwnOverflowGuard()
    {
        // The engine refuses a prompt whose chars/4 estimate exceeds its context; a context
        // sized by this budget must always clear that guard, or the whole-transcript path
        // would refuse the very prompt it was sized for.
        for (var chars = 0; chars < 400_000; chars += 9_973)
        {
            Assert.True(chars / 4 <= AnswerContextBudget.ContextTokensFor(chars));
        }
    }
}

/// <summary>The same panel through the real window: layout, bindings, the keyboard.</summary>
public class AskChatWindowTests
{
    private static (MainWindow Window, MainWindowViewModel ViewModel, FakeMediaPlayer Player) Open(
        FakeAnswerOptions? options = null)
    {
        var player = new FakeMediaPlayer { DurationToReport = TimeSpan.FromMinutes(2) };
        var directory = TestTemp.NewDirectory("uindosill-askchat");
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
    public void LeadQuoteCaveatsAreVisibleThroughTheWindow()
    {
        var (window, viewModel, _) = Open();
        var transcript = new TranscriptDocument
        {
            AudioDuration = TimeSpan.FromSeconds(10),
            Segments = [new TranscriptSegment
            {
                Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "the budget was approved",
            }],
        };

        foreach (var lead in new[]
        {
            "They said «the budget was rejected» [S1]",
            "They said \"the budget was approved\" [S1]",
        })
        {
            var answer = AnswerParser.Parse(lead + "\n- Budget: an update was discussed [S1]\n", allowLead: true);
            var entry = new ChatEntryViewModel("give me a summary", _ => Task.CompletedTask);
            entry.Complete(answer, CitationValidator.Validate(answer, transcript), [], transcript, _ => { });
            viewModel.Ask.Chat.Entries.Add(entry);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.NotNull(entry.Lead!.QuoteCaveat);
            var rendered = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
                block => ReferenceEquals(block.DataContext, entry.Lead)
                    && block.Text == entry.Lead.QuoteCaveat);
            Assert.True(rendered.IsVisible);
            Assert.All(rendered.GetVisualAncestors().OfType<Control>(), control => Assert.True(control.IsVisible));
        }

        window.Close();
    }

    /// <summary>
    /// The answer's sources fold under a disclosure line of the design's own, and open on it.
    /// </summary>
    /// <remarks>
    /// They were under a Fluent Expander, which drew a bordered box the height of a button with a
    /// chevron out of an icon font the window does not ship — the one control on the Ask tab not
    /// drawn in the design's terms. The list now follows a chrome-less toggle's own checked
    /// state, inside the entry's own name scope, and both halves of that are asserted: shut until
    /// pressed, open after, and no Expander anywhere in the window.
    /// </remarks>
    [AvaloniaFact]
    public async Task TheSourcesFoldUnderADisclosureLineAndOpenWhenItIsPressed()
    {
        var (window, viewModel, _) = Open();

        viewModel.Ask.Chat.QuestionText = "tell me about two";
        await viewModel.Ask.Chat.AskCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var entry = Assert.Single(viewModel.Ask.Chat.Entries);
        Assert.True(entry.HasSources);

        var toggle = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.Primitives.ToggleButton>()
            .Single(t => t.Name == "SourcesToggle");
        var rows = window.GetVisualDescendants().OfType<ItemsControl>()
            .Single(i => i.Name == "SourceRows");

        Assert.Contains("disclosure", toggle.Classes);
        Assert.Equal(false, toggle.IsChecked);
        Assert.False(rows.IsVisible);
        Assert.Empty(window.GetVisualDescendants().OfType<Expander>());

        toggle.IsChecked = true;
        window.UpdateLayout();

        Assert.True(rows.IsVisible);
        Assert.Equal(entry.Sources.Count, rows.ItemCount);
    }

    /// <summary>
    /// A chip that cannot be pressed keeps its ground and dims, rather than losing its shape.
    /// </summary>
    /// <remarks>
    /// The window-wide disabled rule paints a disabled button's presenter white inside a grey
    /// hairline, which on a pill left a bare grey glyph on nothing: the "?" of a claim the model
    /// could not anchor, and every citation chip while the transport is dead. Asserted on the
    /// presenter's rendered brush rather than the style file, because a selector that matches
    /// nothing loads without complaint and changes nothing.
    /// </remarks>
    [AvaloniaFact]
    public void AChipThatCannotBePressedKeepsItsGroundAndDims()
    {
        var (window, _, _) = Open();

        // A suggestion chip, live, then switched off by hand: the state every citation chip takes
        // while nothing can play.
        var chip = window.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("chip"));
        var presenter = chip.GetVisualDescendants()
            .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
            .Single(p => p.Name == "PART_ContentPresenter");

        var live = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(presenter.Background).Color;
        Assert.Equal(1.0, chip.Opacity);

        chip.IsEnabled = false;
        window.UpdateLayout();

        var off = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(presenter.Background).Color;
        Assert.Equal(live, off);
        Assert.Equal(0.55, chip.Opacity, 2);
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
