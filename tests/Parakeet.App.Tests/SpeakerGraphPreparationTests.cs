using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.Core.Models;
using Parakeet.Engine.Python;

namespace Parakeet.App.Tests;

/// <summary>
/// The graphics row's one-time preparation, driven through the window rather than the sidecar.
/// </summary>
/// <remarks>
/// <b>The whole point of the seam these use.</b> Preparing the graphs for real starts a Python,
/// loads a checkpoint and writes 32 MiB beside somebody's weights; none of that belongs in a suite
/// that must run on a machine with no model installed. What is asserted here is the behaviour the
/// window owns: when it prepares, what it says while it does, and — the one that matters — that a
/// preparation which fails takes the setting down with it.
/// </remarks>
public class SpeakerGraphPreparationTests
{
    /// <summary>A provider with a model directory, which <see cref="FakeEngineProvider"/> has not.</summary>
    private sealed class ProviderWithModelDirectory : IEngineProvider
    {
        private readonly FakeEngineProvider _inner = new();

        public ProviderWithModelDirectory(string? directory) => DiarisationModelDirectory = directory;

        public string? DiarisationModelDirectory { get; }

        public bool IsModelAvailable(EngineSelection selection) => _inner.IsModelAvailable(selection);

        public Parakeet.Core.Transcription.ITranscriptionEngine Create(EngineSelection selection) =>
            _inner.Create(selection);

        public bool SupportsSpeakerLabelling => _inner.SupportsSpeakerLabelling;

        public Parakeet.Core.Diarisation.ISpeakerLabeller? CreateSpeakerLabeller() => _inner.CreateSpeakerLabeller();

        public Parakeet.Core.Diarisation.SpeakerLabellerLimits? SpeakerLimits => _inner.SpeakerLimits;

        public bool SupportsDiariserBatchSize => _inner.SupportsDiariserBatchSize;

        public bool DiariserRunsInTorch => _inner.DiariserRunsInTorch;

        public Task<IReadOnlyList<string>?> AvailableDiariserProvidersAsync(CancellationToken ct = default) =>
            _inner.AvailableDiariserProvidersAsync(ct);

        public string? DescribeLabeller(Parakeet.Core.Diarisation.ISpeakerLabeller labeller) =>
            _inner.DescribeLabeller(labeller);

        public string? DescribeUnavailable(Parakeet.Core.Models.ModelTask task) =>
            _inner.DescribeUnavailable(task);

        public string? DescribeTranslator(Parakeet.Core.Translation.ITranscriptTranslator translator) =>
            _inner.DescribeTranslator(translator);

        public bool SupportsTranslation(Parakeet.Core.Models.ModelDescriptor? recogniser) =>
            _inner.SupportsTranslation(recogniser);

        public string? DescribeUnavailableTranslation(Parakeet.Core.Models.ModelDescriptor? recogniser) =>
            _inner.DescribeUnavailableTranslation(recogniser);

        public Parakeet.Core.Translation.ITranscriptTranslator? CreateTranslator(Parakeet.Core.Models.ModelDescriptor? recogniser) =>
            _inner.CreateTranslator(recogniser);

        public bool SupportsTidying => _inner.SupportsTidying;

        public Parakeet.Core.Tidying.ITranscriptTidier? CreateTidier() => _inner.CreateTidier();

        public bool SupportsNeuralSpeechDetection => _inner.SupportsNeuralSpeechDetection;

        public Parakeet.Core.Segmentation.ISpeechDetector? CreateSpeechDetector() => _inner.CreateSpeechDetector();

        public void ReleaseBackend() => _inner.ReleaseBackend();
    }

    private sealed class StubExporter : IDiariserGraphExporter
    {
        private readonly Exception? _throws;

        public StubExporter(Exception? throws = null) => _throws = throws;

        public int Calls { get; private set; }

        public string? AskedFor { get; private set; }

        public Task<string> ExportAsync(
            string modelDirectory,
            IProgress<(int Completed, int Total)>? progress = null,
            CancellationToken ct = default)
        {
            Calls++;
            AskedFor = modelDirectory;

            if (_throws is not null)
            {
                return Task.FromException<string>(_throws);
            }

            // The real one writes the files; a stub that only returned a path would let the window's
            // own check pass on nothing, so it writes them too.
            var graphs = Directory.CreateDirectory(
                Path.Combine(modelDirectory, DiariserGraphs.Subdirectory));
            foreach (var name in DiariserGraphs.FileNames)
            {
                File.WriteAllText(Path.Combine(graphs.FullName, name), "not a real graph");
            }

            return Task.FromResult(graphs.FullName);
        }
    }

    private static MainWindowViewModel NewViewModel(
        IEngineProvider provider, IDiariserGraphExporter exporter)
    {
        var directory = TestTemp.NewDirectory("uindosill-app");
        return new MainWindowViewModel(
            provider,
            new LocalModelStore(directory),
            ModelCatalog.Default,
            settings: new AppSettingsStore(Path.Combine(directory, "settings.json")),
            player: new FakeMediaPlayer(),
            graphExporter: () => exporter);
    }

    [Fact]
    public async Task ChoosingGraphicsPreparesTheGraphsOnceAndKeepsTheChoice()
    {
        var model = TestTemp.NewDirectory("uindosill-speaker-model");
        var exporter = new StubExporter();
        var viewModel = NewViewModel(new ProviderWithModelDirectory(model), exporter);

        Assert.False(viewModel.SpeakerGraphsInstalled);

        viewModel.DiarisationProvider = "webgpu";
        await WaitForPreparationAsync(viewModel);

        Assert.Equal(1, exporter.Calls);
        Assert.Equal(model, exporter.AskedFor);
        Assert.True(viewModel.SpeakerGraphsInstalled);
        Assert.Equal("webgpu", viewModel.DiarisationProvider);
        Assert.Null(viewModel.SpeakerGraphsMessage);

        // Chosen again with the graphs already there, nothing is prepared a second time.
        viewModel.DiarisationProvider = null;
        viewModel.DiarisationProvider = "webgpu";
        await WaitForPreparationAsync(viewModel);
        Assert.Equal(1, exporter.Calls);
    }

    [Fact]
    public async Task APreparationThatFailsPutsTheSettingBackAndSaysWhy()
    {
        // **The regression that matters.** A stored `webgpu` with no graphs fails inside the
        // sidecar at load — after a recording has been read — so a preparation that did not work
        // has to take the setting with it rather than leave a choice that breaks the next run.
        var model = TestTemp.NewDirectory("uindosill-speaker-model");
        var exporter = new StubExporter(new InvalidOperationException("the graphics driver said no"));
        var viewModel = NewViewModel(new ProviderWithModelDirectory(model), exporter);

        viewModel.DiarisationProvider = "webgpu";
        await WaitForPreparationAsync(viewModel);

        Assert.Equal(1, exporter.Calls);
        Assert.Null(viewModel.DiarisationProvider);
        Assert.False(viewModel.SpeakerGraphsInstalled);
        Assert.True(viewModel.HasSpeakerGraphsMessage);
        Assert.Contains("the graphics driver said no", viewModel.SpeakerGraphsMessage!, StringComparison.Ordinal);
        Assert.Contains("automatic", viewModel.SpeakerGraphsMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithNoSpeakerModelInstalledNothingIsExportedAndTheChoiceGoesBack()
    {
        var exporter = new StubExporter();
        var viewModel = NewViewModel(new ProviderWithModelDirectory(null), exporter);

        viewModel.DiarisationProvider = "webgpu";
        await WaitForPreparationAsync(viewModel);

        Assert.Equal(0, exporter.Calls);
        Assert.Null(viewModel.DiarisationProvider);
        Assert.True(viewModel.HasSpeakerGraphsMessage);
    }

    [Fact]
    public void TheGraphNamesAgreeAcrossTheProcessBoundary()
    {
        // Three places name these files: the sidecar's `onnx_export`, the CLI's `LabellerFactory`,
        // and `DiariserGraphs` here. A shared constant across a language boundary is not available;
        // this is what stands in for it, and it is why the Python is read rather than trusted.
        var python = Path.Combine(
            RepositoryRoot(), "python", "uindosill_engines", "diariser", "onnx_export.py");
        var source = File.ReadAllText(python);

        foreach (var name in DiariserGraphs.FileNames)
        {
            Assert.Contains($"\"{name}\"", source, StringComparison.Ordinal);
        }

        Assert.Contains($"\"{DiariserGraphs.Subdirectory}\"", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The preparation is started without being awaited — a property setter cannot await — so the
    /// tests wait for the flag it raises rather than for a task they cannot see.
    /// </summary>
    private static async Task WaitForPreparationAsync(MainWindowViewModel viewModel)
    {
        for (var attempt = 0; attempt < 200 && viewModel.IsPreparingSpeakerGraphs; attempt++)
        {
            await Task.Delay(10);
        }

        // One more turn, so a continuation that completed synchronously has run its finally.
        await Task.Yield();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Uindosill.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
