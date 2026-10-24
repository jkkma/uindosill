using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.Audio;
using Parakeet.Core.Answers;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Jobs;
using Parakeet.Core.Models;
using Parakeet.Core.Segmentation;
using Parakeet.Core.Tidying;
using Parakeet.Core.Transcription;
using Parakeet.Core.Translation;
using Parakeet.Engine.LlamaServer;

namespace Parakeet.App.Tests;

public class LocalChatResidencyTests
{
    [AvaloniaFact]
    public async Task LoadingFromModelsReleasesTheResidentAnswerBeforeCreatingTheTranscriber()
    {
        var answers = new ControlledAnswerProvider { DisposeGate = Gate() };
        var (main, engines, _) = Create(answers);
        // An earlier no-op release must not make a later eviction return prematurely.
        await main.Ask.Chat.ReleaseEngineAsync();
        await AskAsync(main, "what about the budget?");
        var previous = Assert.Single(answers.Created);

        var loading = main.Models.LoadCommand.ExecuteAsync(null);
        await ObserveAsync(previous.Disposing.Task);
        Assert.True(main.Session.IsBusy);
        Assert.False(main.Session.IsLoaded);
        Assert.Equal(0, engines.Created);
        Assert.False(main.Models.CanLoad);
        main.Ask.Chat.QuestionText = "another question";
        Assert.False(main.Ask.Chat.AskCommand.CanExecute(null));

        answers.DisposeGate!.SetResult();
        await ObserveAsync(loading);
        Assert.True(previous.Disposed);
        Assert.True(main.Session.IsLoaded);
        Assert.Null(main.Models.StatusMessage);
        Assert.Single(main.Ask.Chat.Entries);
        Assert.Contains("unloaded for the transcriber", main.Ask.Chat.ResidencyNotice, StringComparison.Ordinal);

        await AskAsync(main, "what about the budget?");
        Assert.Equal(2, answers.Created.Count);
        Assert.False(main.Session.IsLoaded);
        Assert.Null(main.Ask.Chat.Entries[^1].Failure);
        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task AManualLoadStopsTheStreamAndEveryReleaseWaitsForTheSameDisposal()
    {
        var answers = new ControlledAnswerProvider { StreamGate = Gate(), DisposeGate = Gate() };
        var (main, engines, _) = Create(answers);
        main.Ask.Chat.QuestionText = "what about the budget?";
        var asking = main.Ask.Chat.AskCommand.ExecuteAsync(null);
        var previous = Assert.Single(answers.Created);
        await ObserveAsync(previous.Streaming.Task);
        Assert.NotNull(Assert.Single(main.Ask.Chat.Entries).StreamingText);

        var loading = main.Models.LoadCommand.ExecuteAsync(null);
        await ObserveAsync(previous.Disposing.Task);
        var release = main.Ask.Chat.ReleaseEngineAsync();
        var repeated = main.Ask.Chat.OnTranscriptionStartedAsync();
        Assert.Same(release, repeated);
        Assert.False(release.IsCompleted);
        Assert.False(loading.IsCompleted);
        Assert.Equal(0, engines.Created);

        answers.DisposeGate!.SetResult();
        await ObserveAsync(Task.WhenAll(asking, loading, release, repeated));
        Assert.Equal(1, previous.DisposeCalls);
        Assert.True(main.Session.IsLoaded);
        Assert.False(main.Session.IsBusy);
        Assert.False(main.Ask.Chat.IsAsking);
        Assert.Equal("Stopped.", Assert.Single(main.Ask.Chat.Entries).Failure);

        await AskAsync(main, "what about the budget?");
        Assert.Null(main.Ask.Chat.Entries[^1].Failure);
        Assert.Equal(2, answers.Created.Count);
        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task AManualLoadWaitsForACancelledColdLoadAndItsLocalEngineToBeDisposed()
    {
        // Native loading may return after cancellation. The unpublished engine still owns its
        // allocation until the ask's cancellation unwind has finished disposing it.
        var answers = new ControlledAnswerProvider { LoadGate = Gate(), DisposeGate = Gate() };
        var (main, engines, _) = Create(answers);
        main.Ask.Chat.QuestionText = "what about the budget?";
        var asking = main.Ask.Chat.AskCommand.ExecuteAsync(null);
        var previous = Assert.Single(answers.Created);
        await ObserveAsync(previous.Loading.Task);

        var loading = main.Models.LoadCommand.ExecuteAsync(null);
        var release = main.Ask.Chat.ReleaseEngineAsync();
        Assert.False(loading.IsCompleted);
        Assert.True(main.Session.IsBusy);
        Assert.Equal(0, engines.Created);

        answers.LoadGate!.SetResult();
        await ObserveAsync(previous.Disposing.Task);
        Assert.False(loading.IsCompleted);
        Assert.False(release.IsCompleted);
        Assert.Equal(0, engines.Created);
        answers.DisposeGate!.SetResult();

        await ObserveAsync(Task.WhenAll(asking, loading, release));
        Assert.True(previous.Disposed);
        Assert.Equal(1, previous.DisposeCalls);
        Assert.Equal(0, previous.AskCalls);
        Assert.True(main.Session.IsLoaded);
        Assert.False(main.Ask.Chat.IsAsking);
        Assert.Equal("Stopped.", Assert.Single(main.Ask.Chat.Entries).Failure);

        await AskAsync(main, "what about the budget?");
        Assert.Null(main.Ask.Chat.Entries[^1].Failure);
        Assert.Equal(2, answers.Created.Count);
        await main.ShutdownAsync();
    }

    [AvaloniaFact]
    public async Task BatchAutoloadAlsoWaitsForTheAnsweringModelBeforeCreatingTheTranscriber()
    {
        var answers = new ControlledAnswerProvider { DisposeGate = Gate() };
        var (main, engines, directory) = Create(answers);
        await AskAsync(main, "what about the budget?");
        var previous = Assert.Single(answers.Created);
        var path = Path.Combine(directory, "next.wav");
        WavWriter.WriteFile(path, new float[16_000], 16_000);
        main.Transcribe.OutputDirectory = directory;
        foreach (var format in main.Transcribe.Formats)
        {
            format.IsSelected = format.Id == "txt";
        }
        main.Transcribe.AddFiles([path]);

        var batch = main.Transcribe.StartCommand.ExecuteAsync(null);
        await ObserveAsync(previous.Disposing.Task);
        Assert.False(batch.IsCompleted);
        Assert.True(main.Session.IsBusy);
        Assert.Equal(0, engines.Created);
        answers.DisposeGate!.SetResult();

        await ObserveAsync(batch);
        Assert.True(previous.Disposed);
        Assert.Equal(1, engines.Created);
        Assert.Equal(JobState.Completed, main.Transcribe.Jobs[^1].State);
        Assert.False(main.Transcribe.IsRunning);
        await main.ShutdownAsync();
    }

    private static (MainWindowViewModel Main, CheckedTranscriptionProvider Engines, string Directory) Create(
        ControlledAnswerProvider answers)
    {
        var directory = TestTemp.NewDirectory("uindosill-chat-residency");
        var engines = new CheckedTranscriptionProvider(() =>
            Assert.All(answers.Created, answer => Assert.True(answer.Disposed,
                "The transcriber was created while an answering model still owned its allocation.")));
        var main = new MainWindowViewModel(engines, new LocalModelStore(directory),
            settings: new AppSettingsStore(Path.Combine(directory, "settings.json")),
            backendsOnDisk: () => [ComputeBackend.Cpu],
            player: new FakeMediaPlayer(), answerEngines: answers);
        var model = main.Models.Models.First(item => item.IsTranscriptionModel);
        model.IsInstalled = true;
        main.Models.Selected = model;
        var path = Path.Combine(directory, "finished.wav");
        var job = new JobViewModel(path);
        job.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = path }, State = JobState.Completed,
            Document = new TranscriptDocument
            {
                AudioDuration = TimeSpan.FromSeconds(10),
                Segments = [new() { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "The budget was approved." }],
            },
        });
        main.Transcribe.Jobs.Add(job);
        return (main, engines, directory);
    }

    private static async Task AskAsync(MainWindowViewModel main, string question)
    {
        main.Ask.Chat.QuestionText = question;
        Assert.True(main.Ask.Chat.AskCommand.CanExecute(null));
        await ObserveAsync(main.Ask.Chat.AskCommand.ExecuteAsync(null));
    }

    private static Task ObserveAsync(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ControlledAnswerProvider : IAnswerEngineProvider
    {
        public TaskCompletionSource? LoadGate { get; init; }
        public TaskCompletionSource? StreamGate { get; init; }
        public TaskCompletionSource? DisposeGate { get; init; }
        public List<ControlledAnswerEngine> Created { get; } = [];
        public bool ThinkingMode => false;
        public AskModePreference ModePreference => AskModePreference.Retrieval;
        public MoeExpertPlacement ExpertPlacement => MoeExpertPlacement.Automatic;
        public int EvidenceWindows => 8;
        public AnswerEngineAvailability Check() => new() { ModelFileName = "controlled-answer.gguf" };
        public IAnswerEngine Create(int promptChars = 0)
        {
            var engine = new ControlledAnswerEngine(Created.Count == 0 ? this : null);
            Created.Add(engine);
            return engine;
        }
    }

    private sealed class ControlledAnswerEngine(ControlledAnswerProvider? controls) : IAnswerEngine
    {
        private Task? _disposal;
        public TaskCompletionSource Loading { get; } = Gate();
        public TaskCompletionSource Streaming { get; } = Gate();
        public TaskCompletionSource Disposing { get; } = Gate();
        public bool Disposed { get; private set; }
        public int DisposeCalls { get; private set; }
        public int AskCalls { get; private set; }
        public AnswerEngineCapabilities Capabilities { get; } = new()
        {
            EngineName = "controlled", ModelId = "controlled-answer", Backend = ComputeBackend.Cpu,
        };
        public async ValueTask LoadAsync(CancellationToken ct = default)
        {
            Loading.TrySetResult();
            if (controls?.LoadGate is { } gate)
            {
                // Deliberately ignores cancellation, as a native model load may do.
                await gate.Task;
            }
        }
        public async IAsyncEnumerable<string> AskAsync(AskRequest request,
            IProgress<AskProgress>? progress = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Assert.False(Disposed);
            AskCalls++;
            ct.ThrowIfCancellationRequested();
            yield return "The budget was ";
            Streaming.TrySetResult();
            if (controls?.StreamGate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            yield return "approved. [S1]";
        }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return new ValueTask(_disposal ??= DisposeCoreAsync());
        }
        private async Task DisposeCoreAsync()
        {
            Disposing.TrySetResult();
            if (controls?.DisposeGate is { } gate)
            {
                await gate.Task;
            }
            Disposed = true;
        }
    }

    private sealed class CheckedTranscriptionProvider(Action beforeCreate) : IEngineProvider
    {
        private readonly FakeEngineProvider _inner = new();
        public int Created { get; private set; }
        public bool IsModelAvailable(EngineSelection selection) => _inner.IsModelAvailable(selection);
        public ITranscriptionEngine Create(EngineSelection selection)
        {
            beforeCreate();
            Created++;
            return _inner.Create(selection);
        }
        public bool SupportsSpeakerLabelling => _inner.SupportsSpeakerLabelling;
        public ISpeakerLabeller? CreateSpeakerLabeller() => _inner.CreateSpeakerLabeller();
        public SpeakerLabellerLimits? SpeakerLimits => _inner.SpeakerLimits;
        public bool SupportsDiariserBatchSize => _inner.SupportsDiariserBatchSize;
        public bool DiariserRunsInTorch => _inner.DiariserRunsInTorch;
        public string? DiarisationModelDirectory => _inner.DiarisationModelDirectory;
        public Task<IReadOnlyList<string>?> AvailableDiariserProvidersAsync(CancellationToken ct = default) => _inner.AvailableDiariserProvidersAsync(ct);
        public string? DescribeLabeller(ISpeakerLabeller labeller) => _inner.DescribeLabeller(labeller);
        public string? DescribeTranslator(ITranscriptTranslator translator) => _inner.DescribeTranslator(translator);
        public string? DescribeUnavailable(ModelTask task) => _inner.DescribeUnavailable(task);
        public bool SupportsTranslation(ModelDescriptor? recogniser) => _inner.SupportsTranslation(recogniser);
        public string? DescribeUnavailableTranslation(ModelDescriptor? recogniser) => _inner.DescribeUnavailableTranslation(recogniser);
        public ITranscriptTranslator? CreateTranslator(ModelDescriptor? recogniser) => _inner.CreateTranslator(recogniser);
        public bool SupportsTidying => _inner.SupportsTidying;
        public ITranscriptTidier? CreateTidier() => _inner.CreateTidier();
        public bool SupportsNeuralSpeechDetection => _inner.SupportsNeuralSpeechDetection;
        public ISpeechDetector? CreateSpeechDetector() => _inner.CreateSpeechDetector();
        public void ReleaseBackend() => _inner.ReleaseBackend();
    }
}
