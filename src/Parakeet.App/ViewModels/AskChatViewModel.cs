using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parakeet.App.Services;
using Parakeet.Core.Answers;
using Parakeet.Core.Formatting;
using Parakeet.Core.Retrieval;
using Parakeet.Core.Transcription;

namespace Parakeet.App.ViewModels;

/// <summary>
/// The chat half of the Ask tab: questions in, streamed answers out, every claim carrying a
/// citation that resolves to a time in the recording or renders as unresolved — never as a
/// number a reader might trust. The transcript half of the tab worked for a day short of the
/// model arriving; this is the model arriving.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ask's world is one document.</b> On a translated recording that is the English — the
/// maintainer's decision, 2026-08-24 — and otherwise the transcript as spoken: retrieval,
/// evidence, the grammar's ids, quote checks and validation all run over the same
/// <see cref="TranscriptDocument"/>, and a change of recording clears the conversation, because
/// its ids are meaningless against any other transcript. A chat is transient by design
/// (decision 5): nothing here is persisted, and copying an answer is the export.
/// </para>
/// <para>
/// <b>Residency is the policy, not a heuristic (R9).</b> Asking the first question unloads the
/// transcription model before the language model loads, and the panel says so; a transcription
/// starting mid-chat kills the language model's child process the same way — the symmetric
/// reading the maintainer ratified on 2026-08-24 (docs/V2-ASK-THE-TRANSCRIPT.md, decision 4),
/// because the alternative is both resident, the one arrangement the register rules out.
/// </para>
/// </remarks>
public sealed partial class AskChatViewModel : ObservableObject
{
    /// <summary>Windows handed to the model per question: ~2k tokens of evidence at the
    /// default window length, well inside the engine's context.</summary>
    private const int EvidenceCount = 8;

    private readonly IAnswerEngineProvider? _provider;
    private readonly ModelSession? _session;
    private readonly Func<bool> _transcriptionRunning;
    private readonly Action<TimeSpan> _seekAndPlay;

    private IAnswerEngine? _engine;
    private bool _engineThinking;
    private JobViewModel? _recording;
    private TranscriptDocument? _document;
    private IReadOnlyList<TranscriptWindow>? _windows;
    private Bm25Retriever? _retriever;
    private CancellationTokenSource? _asking;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    private string? _questionText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isAsking;

    /// <summary>
    /// The one line under the input that explains what just happened to the models — the R9
    /// unload, or the language model making way for a transcription. Null while there is nothing
    /// to explain.
    /// </summary>
    [ObservableProperty]
    private string? _residencyNotice;

    public AskChatViewModel(
        IAnswerEngineProvider? provider,
        ModelSession? session,
        Func<bool>? transcriptionRunning,
        Action<TimeSpan>? seekAndPlay)
    {
        _provider = provider;
        _session = session;
        _transcriptionRunning = transcriptionRunning ?? (static () => false);
        _seekAndPlay = seekAndPlay ?? (static _ => { });
    }

    /// <summary>The window sets this once it exists; copying an answer goes through it.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    public ObservableCollection<ChatEntryViewModel> Entries { get; } = [];

    public bool HasEntries => Entries.Count > 0;

    /// <summary>
    /// Why the panel is covered, or null when it is live. Unavailability of the engine outranks
    /// the missing transcript: a person who cannot ask at all should not first be sent off to
    /// transcribe something.
    /// </summary>
    public string? PanelNotice
    {
        get
        {
            if (_provider is null)
            {
                return "Asking needs the language-model engine, and this build does not include it.";
            }

            if (_provider.Check() is { IsAvailable: false } unavailable)
            {
                return unavailable.WhyNot;
            }

            return _document is null
                ? "Transcribe this recording first — a finished transcript is what questions are asked of."
                : null;
        }
    }

    public bool IsPanelEnabled => PanelNotice is null;

    /// <summary>
    /// Moves the chat onto another recording. The conversation clears: its citations were ids
    /// into the transcript that is no longer open, and a chip that seeks the wrong recording is
    /// worse than an empty panel.
    /// </summary>
    public void SetRecording(JobViewModel? recording)
    {
        _recording = recording;
        RefreshDocument();
    }

    /// <summary>
    /// Re-reads which document the ask runs over — the English when the recording has one, the
    /// transcript as spoken otherwise. Called when the selection changes and when a transcript
    /// or a translation arrives on the open recording.
    /// </summary>
    public void RefreshDocument()
    {
        var document = _recording?.TranslatedDocument ?? _recording?.Document;
        if (!ReferenceEquals(document, _document))
        {
            // An in-flight ask was built over the document being replaced: its evidence, grammar
            // ids and validation are meaningless against the new one, and letting it finish would
            // refill the index fields below from the old transcript.
            _asking?.Cancel();

            _document = document;
            _windows = null;
            _retriever = null;
            Entries.Clear();
            OnPropertyChanged(nameof(HasEntries));
        }

        OnPropertyChanged(nameof(PanelNotice));
        OnPropertyChanged(nameof(IsPanelEnabled));
        AskCommand.NotifyCanExecuteChanged();
    }

    private bool CanAsk => IsPanelEnabled && !IsAsking && !string.IsNullOrWhiteSpace(QuestionText);

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private Task Ask() => AskCore(QuestionText!);

    /// <summary>A suggestion chip is a question somebody else typed: it fills the box and asks.</summary>
    [RelayCommand]
    private Task AskSuggestion(string question)
    {
        QuestionText = question;
        return CanAsk ? AskCore(question) : Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(IsAsking))]
    private void Stop() => _asking?.Cancel();

    private async Task AskCore(string question)
    {
        if (_document is not { } document || _provider is null)
        {
            return;
        }

        // Both halves of the residency probe: a batch that is running, and a batch still inside
        // its model load — IsRunning is only set once the load await returns, so the session's
        // own busy flag is what covers the minutes a large model spends loading.
        if (_transcriptionRunning() || _session is { IsBusy: true })
        {
            ResidencyNotice = "Wait for the transcription to finish — the transcriber and the "
                + "ask model cannot be loaded at the same time.";
            return;
        }

        var entry = new ChatEntryViewModel(question.Trim(), CopyAsync);
        Entries.Add(entry);
        OnPropertyChanged(nameof(HasEntries));
        QuestionText = null;

        IsAsking = true;
        using var cancellation = new CancellationTokenSource();
        _asking = cancellation;

        try
        {
            // The search costs milliseconds and the model load can cost minutes, in that order
            // on purpose: a question retrieval cannot anchor abstains right here, before the
            // transcriber is unloaded or a byte of the language model is read.
            entry.Status = "Searching the transcript…";
            _windows ??= TranscriptWindowBuilder.Build(document);
            _retriever ??= new Bm25Retriever(_windows);
            var evidence = _retriever.Retrieve(question, EvidenceCount).Select(hit => hit.Window).ToList();

            if (evidence.Count == 0)
            {
                entry.AbstainWithoutAsking();
                return;
            }

            await EnsureEngineAsync(entry, cancellation.Token).ConfigureAwait(true);

            // The load await is the window a recording switch can slip through; its cancel may
            // land after the load completed without observing the token, so it is re-checked
            // here before the ask runs over the document it was built against.
            cancellation.Token.ThrowIfCancellationRequested();

            // The English pane's ask drops the source hint — the maintainer's decision,
            // 2026-08-24, closing the review's collision: the hint survives onto the translated
            // document as provenance, but forwarding it here instructed source-language answers
            // over English evidence, whose grammar-forced quotes the verbatim check could only
            // fail. A translated ask is unlocalised; the transcript as spoken keeps its hint.
            var language = document.IsTranslated ? null : document.Language;

            var request = new AskRequest
            {
                Question = question,
                Transcript = document,
                Mode = AnswerMode.Retrieval,
                Evidence = evidence,
                Language = language,
            };

            entry.Status = "Answering…";

            // Progress captures this (UI) context at construction, so the engine may report from
            // wherever it runs and the entry is still only ever touched here.
            var progress = new Progress<AskProgress>(entry.OnProgress);
            var text = new StringBuilder();

            await foreach (var chunk in _engine!.AskAsync(request, progress, cancellation.Token).ConfigureAwait(true))
            {
                text.Append(chunk);
                entry.OnStreamed(text.ToString());
            }

            var answer = AnswerParser.Parse(text.ToString()) with
            {
                ModelId = _engine.Capabilities.ModelId,
                Quantisation = _engine.Capabilities.Quantisation,
                Backend = _engine.Capabilities.Backend,
                Mode = AnswerMode.Retrieval,
                Language = language,
            };

            if (answer.IsEmpty)
            {
                // Neither claims nor the sentinel. Rendering this as the abstention would turn
                // silence into the definite claim "the recording doesn't answer that".
                entry.Fail("The model produced no answer. Ask again, or ask differently.");
            }
            else
            {
                entry.Complete(answer, CitationValidator.Validate(answer, document), evidence, document, _seekAndPlay);
            }
        }
        catch (OperationCanceledException)
        {
            entry.Fail("Stopped.");
        }
        catch (Exception exception)
        {
            entry.Fail(exception.Message);

            // A dead child cannot answer the next question either; drop it so the next ask
            // starts fresh rather than failing the same way twice.
            if (_engine is not null)
            {
                await _engine.DisposeAsync().ConfigureAwait(true);
                _engine = null;
            }
        }
        finally
        {
            _asking = null;
            IsAsking = false;
        }
    }

    private async Task EnsureEngineAsync(ChatEntryViewModel entry, CancellationToken ct)
    {
        // An engine built under the other thinking mode is dropped, not reused: the mode is a
        // child-process argument, so the settings toggle can only take effect through a fresh
        // child — at the next question, which is when a person expects a setting to matter.
        if (_engine is not null && _engineThinking != _provider!.ThinkingMode)
        {
            await _engine.DisposeAsync().ConfigureAwait(true);
            _engine = null;
        }

        if (_engine is not null)
        {
            return;
        }

        // R9, the decided half: the transcription model is always unloaded when the chat opens.
        // Always — not per model, not behind arithmetic about what would fit beside what.
        if (_session is { IsLoaded: true })
        {
            await _session.UnloadAsync().ConfigureAwait(true);
            ResidencyNotice = "The transcription model was unloaded to make room for the ask "
                + "model. Transcribing again reloads it.";
        }

        entry.Status = "Loading the model — a large one takes a while…";
        _engineThinking = _provider!.ThinkingMode;
        var engine = _provider.Create();
        try
        {
            await engine.LoadAsync(ct).ConfigureAwait(true);
        }
        catch
        {
            await engine.DisposeAsync().ConfigureAwait(true);
            throw;
        }

        _engine = engine;
    }

    /// <summary>
    /// Lets go of the language model — the child process is killed, which is the one unload that
    /// cannot leak. Called when a transcription starts mid-chat and when the window closes.
    /// </summary>
    public async Task ReleaseEngineAsync(string? reason = null)
    {
        // An ask still inside its cold load has no engine yet, but taking it down deserves the
        // same sentence — "Stopped." with no explanation reads as a defect, not a policy.
        var hadModelWork = _engine is not null || _asking is not null;
        _asking?.Cancel();

        if (_engine is not null)
        {
            await _engine.DisposeAsync().ConfigureAwait(true);
            _engine = null;
        }

        if (reason is not null && hadModelWork)
        {
            ResidencyNotice = reason;
        }
    }

    /// <summary>The wiring's half of the symmetric residency rule: a transcription starting
    /// mid-chat takes the language model down with a line saying so.</summary>
    public Task OnTranscriptionStartedAsync() =>
        ReleaseEngineAsync("The ask model was unloaded while transcribing — your next question reloads it.");

    private Task CopyAsync(string text) => CopyToClipboard?.Invoke(text) ?? Task.CompletedTask;
}

/// <summary>One question and whatever became of it: a stream, then bullets or an abstention or a
/// failure — exactly one of the three, and the raw stream is never left looking like an answer.</summary>
public sealed partial class ChatEntryViewModel : ObservableObject
{
    private readonly Func<string, Task> _copy;
    private string? _copyText;

    [ObservableProperty]
    private string? _status;

    /// <summary>
    /// The model is reasoning and no answer text exists yet — the stretch the typing-indicator
    /// dots cover, because a silent panel over a thinking model reads as a hang.
    /// </summary>
    [ObservableProperty]
    private bool _isThinking;

    [ObservableProperty]
    private string? _streamingText;

    [ObservableProperty]
    private bool _isDone;

    [ObservableProperty]
    private bool _abstained;

    [ObservableProperty]
    private string? _failure;

    [ObservableProperty]
    private string? _modelLine;

    public ChatEntryViewModel(string question, Func<string, Task> copy)
    {
        Question = question;
        _copy = copy;
    }

    public string Question { get; }

    public ObservableCollection<AnswerBulletViewModel> Bullets { get; } = [];

    public ObservableCollection<SourceRowViewModel> Sources { get; } = [];

    public bool HasBullets => Bullets.Count > 0;

    public bool HasSources => Sources.Count > 0;

    public bool CanCopy => IsDone && Failure is null && _copyText is not null;

    /// <summary>What the abstention says. One sentence, no apology, no invention.</summary>
    public string AbstainedText => "The recording doesn't answer that.";

    public void OnProgress(AskProgress progress)
    {
        if (progress.GeneratedTokens > 0)
        {
            // Answer text is arriving; the stream itself is the indicator now.
            IsThinking = false;
            return;
        }

        if (progress.ThinkingTokens > 0)
        {
            IsThinking = true;
            Status = null;
            return;
        }

        if (progress.PrefillFraction is { } fraction)
        {
            Status = string.Create(
                CultureInfo.InvariantCulture, $"Reading the transcript… {fraction * 100:F0}%");
        }
    }

    public void OnStreamed(string textSoFar)
    {
        Status = null;
        IsThinking = false;
        StreamingText = textSoFar;
    }

    public void Complete(
        AnswerDocument answer,
        AnswerValidation validation,
        IReadOnlyList<TranscriptWindow> evidence,
        TranscriptDocument document,
        Action<TimeSpan> seekAndPlay)
    {
        StreamingText = null;
        Status = null;
        IsThinking = false;
        Abstained = answer.Abstained;

        foreach (var bullet in validation.Bullets)
        {
            Bullets.Add(new AnswerBulletViewModel(bullet, seekAndPlay));
        }

        foreach (var (window, rank) in evidence.Select((window, index) => (window, index + 1)))
        {
            Sources.Add(new SourceRowViewModel(window, rank, seekAndPlay));
        }

        ModelLine = BuildModelLine(answer);
        _copyText = BuildCopyText(answer, validation, document);
        IsDone = true;

        OnPropertyChanged(nameof(HasBullets));
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(CanCopy));
        CopyCommand.NotifyCanExecuteChanged();
    }

    public void Fail(string message)
    {
        StreamingText = null;
        Status = null;
        IsThinking = false;
        Failure = message;
        IsDone = true;
        OnPropertyChanged(nameof(CanCopy));
        CopyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Empty retrieval's outcome: the abstention, reached without loading any model — and with
    /// no model line or copy, because nothing generated anything.
    /// </summary>
    public void AbstainWithoutAsking()
    {
        StreamingText = null;
        Status = null;
        IsThinking = false;
        Abstained = true;
        IsDone = true;
        OnPropertyChanged(nameof(CanCopy));
        CopyCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private Task Copy() => _copyText is { } text ? _copy(text) : Task.CompletedTask;

    private static string BuildModelLine(AnswerDocument answer)
    {
        var name = answer.ModelId ?? "a language model";
        var backend = answer.Backend?.ToString().ToLowerInvariant();
        var parts = string.Join(", ", new[] { answer.Quantisation, backend }.Where(p => !string.IsNullOrEmpty(p)));
        var model = parts.Length > 0 ? $"{name} ({parts})" : name;
        return $"Generated by {model} from retrieved parts of the transcript — not transcribed speech.";
    }

    /// <summary>
    /// The rendered form an email gets (decision 5): the generated-not-transcribed line first,
    /// then each claim with a plain timestamp and — only where the check verified it — the
    /// verbatim quote, then the provenance of both models and the date. Never a clickable
    /// reference, and never unverified text dressed as transcript.
    /// </summary>
    private string BuildCopyText(AnswerDocument answer, AnswerValidation validation, TranscriptDocument document)
    {
        var text = new StringBuilder();
        text.AppendLine("Generated by a language model — not transcribed speech.");
        text.AppendLine();
        text.Append("Q: ").AppendLine(Question);
        text.AppendLine();

        if (Abstained)
        {
            text.AppendLine(AbstainedText);
        }

        foreach (var bullet in validation.Bullets)
        {
            text.Append("- ");

            var resolved = bullet.Citations.Where(c => c.Check.Resolves).ToList();
            if (resolved.Count > 0)
            {
                text.Append('[')
                    .Append(string.Join("; ", resolved.Select(c => Range(c.Start!.Value, c.End!.Value))))
                    .Append("] ");
            }
            else
            {
                text.Append("[unverified] ");
            }

            if (bullet.Bullet.Label is { } label)
            {
                text.Append(label).Append(": ");
            }

            text.Append(bullet.Bullet.Text);

            if (bullet.Bullet.Quote is { } quote && resolved.Any(c => c.Check.QuoteMatches == true))
            {
                text.Append(" — “").Append(quote).Append('”');
            }

            text.AppendLine();
        }

        text.AppendLine();
        text.Append(ModelLine).AppendLine();

        var askedOf = document.SourceName ?? "the open recording";
        var transcriber = document.ModelId is { } asr
            ? $"{asr}{(document.Quantisation is { } q ? $" ({q})" : string.Empty)}"
            : "an unknown model";
        text.Append("Asked of ").Append(askedOf)
            .Append(document.IsTranslated ? ", in its English translation, transcribed by " : ", transcribed by ")
            .Append(transcriber)
            .Append(" — ")
            .Append(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .AppendLine();

        return text.ToString();
    }

    private static string Range(TimeSpan start, TimeSpan end) =>
        end - start > TimeSpan.FromSeconds(1)
            ? $"{Timecode.Format(start)}–{Timecode.Format(end)}"
            : Timecode.Format(start);
}

/// <summary>One claim: its text, its verified-or-not quote, and the chips that seek.</summary>
public sealed class AnswerBulletViewModel
{
    public AnswerBulletViewModel(ResolvedBullet bullet, Action<TimeSpan> seekAndPlay)
    {
        Label = bullet.Bullet.Label;
        Text = bullet.Bullet.Text;
        Quote = bullet.Bullet.Quote;
        QuoteVerified = bullet.Citations.Any(c => c.Check.QuoteMatches == true);
        QuoteChecked = bullet.Citations.Any(c => c.Check.QuoteMatches is not null);
        Citations = [.. bullet.Citations.Select(c => new CitationChipViewModel(c, seekAndPlay))];
        IsUncited = bullet.Bullet.IsUncited || Citations.All(c => !c.IsResolved);
    }

    public string? Label { get; }

    public string Text { get; }

    public string? Quote { get; }

    /// <summary>True when the quote was found verbatim in the cited span. An unverified quote is
    /// still shown — it is what the model said — but marked, never silently trusted.</summary>
    public bool QuoteVerified { get; }

    /// <summary>Whether any citation resolved to a span the quote could be checked against. A
    /// claim citing only <c>[?]</c> was never checked, which is not the same as failing.</summary>
    public bool QuoteChecked { get; }

    public IReadOnlyList<CitationChipViewModel> Citations { get; }

    /// <summary>Nothing on this claim resolves; it renders with the unresolved marker.</summary>
    public bool IsUncited { get; }

    public bool HasLabel => Label is not null;

    public bool HasQuote => Quote is not null;

    public string? QuoteDisplay => Quote is null ? null : $"“{Quote}”";

    public string? QuoteCaveat => Quote is null || QuoteVerified
        ? null
        : QuoteChecked
            ? "quote not found in the transcript"
            : "quote not checked — no place in the recording to check it against";
}

/// <summary>One citation as a chip: a time that seeks, or the unresolved marker that does not.</summary>
public sealed partial class CitationChipViewModel
{
    private readonly Action<TimeSpan> _seekAndPlay;
    private readonly TimeSpan? _start;

    public CitationChipViewModel(ResolvedCitation citation, Action<TimeSpan> seekAndPlay)
    {
        _seekAndPlay = seekAndPlay;
        _start = citation.Start;
        IsResolved = citation.Check.Resolves;

        Display = IsResolved
            ? citation.End!.Value - citation.Start!.Value > TimeSpan.FromSeconds(1)
                ? $"{Timecode.Format(citation.Start.Value)}–{Timecode.Format(citation.End.Value)}"
                : Timecode.Format(citation.Start.Value)
            : "?";

        Detail = !IsResolved
            ? "The model gave no place in the recording for this."
            : !citation.Check.NonEmpty
                ? "Points at silence."
                : !citation.Check.WithinDuration
                    ? "Points past the end of the recording."
                    : citation.Check.QuoteMatches == false
                        ? "The quoted words are not at this place."
                        : "Click to listen from here.";
    }

    /// <summary>The rendered time — resolved by the application from the cited segments, exactly
    /// as the rule requires. The model's own output never reaches this string.</summary>
    public string Display { get; }

    public string Detail { get; }

    public bool IsResolved { get; }

    [RelayCommand]
    private void Seek()
    {
        if (_start is { } start)
        {
            _seekAndPlay(start);
        }
    }
}

/// <summary>One row of the Sources expander: what the model was shown, in rank order.</summary>
public sealed partial class SourceRowViewModel
{
    private readonly Action<TimeSpan> _seekAndPlay;
    private readonly TimeSpan _start;

    public SourceRowViewModel(TranscriptWindow window, int rank, Action<TimeSpan> seekAndPlay)
    {
        _seekAndPlay = seekAndPlay;
        _start = window.Start;
        Rank = rank;
        TimeRange = $"{Timecode.Format(window.Start)}–{Timecode.Format(window.End)}";
        Snippet = window.Preview(120);
    }

    public int Rank { get; }

    public string TimeRange { get; }

    public string Snippet { get; }

    [RelayCommand]
    private void Seek() => _seekAndPlay(_start);
}
