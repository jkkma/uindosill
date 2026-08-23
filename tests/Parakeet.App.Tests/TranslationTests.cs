using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.Audio;
using Parakeet.Core.Jobs;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;

namespace Parakeet.App.Tests;

/// <summary>
/// The window's half of translating into English: the opt-in, and the pane switcher that shows the
/// result beside the transcript rather than instead of it.
/// </summary>
public class TranslationTests
{
    private static (TranscribeViewModel ViewModel, string Directory) Create()
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-tr").FullName;
        var main = new MainWindowViewModel(new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
        main.Transcribe.OutputDirectory = directory;

        main.Session.LoadAsync(new EngineSelection { Model = main.Models.SelectedDescriptor })
            .GetAwaiter().GetResult();

        return (main.Transcribe, directory);
    }

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

    /// <summary>
    /// Only <c>txt</c>, so a test that is about the opt-in is not also about six formatters.
    /// </summary>
    private static void SelectPlainTextOnly(TranscribeViewModel viewModel)
    {
        foreach (var format in viewModel.Formats)
        {
            format.IsSelected = format.Id == "txt";
        }
    }

    [Fact]
    public void TheChipMapIsBuiltOnceFromTheSpokenDocumentSoBothPanesAgree()
    {
        // A speaker whose first segment came back empty from the translator took a later chip in
        // the English pane until 2026-08-22, because each pane walked its own non-empty segments
        // and built its own map. One map, from the spoken document, over every segment.
        var spoken = new TranscriptDocument
        {
            Segments =
            [
                new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Text = "hola", Speaker = "A" },
                new TranscriptSegment { Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2), Text = "buenas", Speaker = "B" },
                new TranscriptSegment { Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(3), Text = "gracias", Speaker = "A" },
            ],
        };
        var translated = spoken with
        {
            TranslatedTo = "en",
            Segments =
            [
                spoken.Segments[0] with { Text = string.Empty },
                spoken.Segments[1] with { Text = "good day" },
                spoken.Segments[2] with { Text = "thanks" },
            ],
        };

        var job = new JobViewModel("/tmp/a.wav");
        job.Complete(
            new JobResult { Job = new TranscriptionJob { InputPath = "/tmp/a.wav" }, State = JobState.Completed, Document = translated },
            source: spoken);

        var spokenChips = job.Lines.DistinctBy(l => l.Speaker).ToDictionary(l => l.Speaker!, l => l.Chip);
        var englishChips = job.TranslatedLines.DistinctBy(l => l.Speaker).ToDictionary(l => l.Speaker!, l => l.Chip);

        Assert.Equal(0, spokenChips["A"]);
        Assert.Equal(1, spokenChips["B"]);
        Assert.Equal(spokenChips["A"], englishChips["A"]);
        Assert.Equal(spokenChips["B"], englishChips["B"]);
    }

    [Fact]
    public void TheRowNamesTheBackendBehindEachPassSeparately()
    {
        // Neither pass said which backend produced it. DescribeBackend speaks only for CUDA and
        // DirectML, and DescribeTranslator only when parity has a finding or auto fell back — so a
        // run on the provider that agrees, which is the ordinary case, reported nothing. The two
        // lines are separate because the two passes are: either runs without the other.
        var spoken = new TranscriptDocument
        {
            Segments = [new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Text = "hola", Speaker = "A" }],
            SpeakerModelId = "sortformer-4spk-v2.1",
            SpeakerBackend = ComputeBackend.WebGpu,
        };

        var translated = spoken with
        {
            TranslatedTo = "en",
            Segments = [spoken.Segments[0] with { Text = "hello" }],
            TranslationModelId = "opus-mt-tc-bible-big-mul-en-fp32",
            TranslationBackend = ComputeBackend.WebGpu,
        };

        var job = new JobViewModel("/tmp/a.wav");
        job.Complete(
            new JobResult { Job = new TranscriptionJob { InputPath = "/tmp/a.wav" }, State = JobState.Completed, Document = translated },
            source: spoken);

        Assert.Equal("Speakers: sortformer-4spk-v2.1 on webgpu", job.SpeakerProvenance);
        Assert.Equal("English: opus-mt-tc-bible-big-mul-en-fp32 on webgpu", job.TranslationProvenance);

        // Running the file again clears both, so a row cannot describe a run that is no longer
        // happening — the rule Reset already held for the transcript and the outputs.
        job.Reset();

        Assert.Null(job.SpeakerProvenance);
        Assert.Null(job.TranslationProvenance);
    }

    [Fact]
    public void APassThatDidNotRunClaimsNoBackend()
    {
        // A transcription-only run: no labels, no English, so neither line is drawn. Absent rather
        // than "none", which would be a row reporting on work nobody asked for.
        var document = new TranscriptDocument
        {
            Segments = [new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Text = "hello" }],
        };

        var job = new JobViewModel("/tmp/a.wav");
        job.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = "/tmp/a.wav" },
            State = JobState.Completed,
            Document = document,
        });

        Assert.Null(job.SpeakerProvenance);
        Assert.Null(job.TranslationProvenance);
    }

    [Fact]
    public async Task TheEnglishOptInIsOffByDefaultAndProducesATranslationBesideTheTranscript()
    {
        var (viewModel, directory) = Create();
        Assert.False(viewModel.TranslateToEnglish);
        Assert.True(viewModel.CanTranslate);      // the fake provider has a translator
        Assert.Null(viewModel.TranslationHint);

        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        SelectPlainTextOnly(viewModel);

        // Off: one transcript, nothing to switch to, and the output keeps its plain name.
        await viewModel.StartCommand.ExecuteAsync(null);
        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        Assert.False(viewModel.Jobs[0].HasTranslation);
        Assert.False(viewModel.CanShowTranslation);
        Assert.True(File.Exists(Path.Combine(directory, "a.txt")));

        // On, through 'Run again': the row is finished, and turning an opt-in on and asking for the
        // same file back is exactly what that button is for.
        viewModel.TranslateToEnglish = true;
        await viewModel.RunAgainCommand.ExecuteAsync(null);

        var job = viewModel.Jobs[0];
        Assert.Equal(JobState.Completed, job.State);

        // Beside, not instead of. The canned translator marks its output visibly, so the two panes
        // are told apart by more than their position.
        Assert.True(job.HasTranslation);
        Assert.DoesNotContain("[en]", job.Transcript, StringComparison.Ordinal);
        Assert.Contains("[en]", job.TranslatedTranscript, StringComparison.Ordinal);
        Assert.NotEmpty(job.Lines);
        Assert.Equal(job.Lines.Count, job.TranslatedLines.Count);
    }

    [Fact]
    public async Task ATranslatedRunWritesItsOwnFilesRatherThanOverTheTranscriptionRuns()
    {
        // SubRip has no comment syntax, so SRT cannot carry the marker in-band and is covered by
        // its name instead. The infix is also what keeps a translated run from destroying the
        // output of the plain one when both are asked for the same recording.
        var (viewModel, directory) = Create();
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        SelectPlainTextOnly(viewModel);

        await viewModel.StartCommand.ExecuteAsync(null);
        var plain = Path.Combine(directory, "a.txt");
        Assert.True(File.Exists(plain));
        var beforeTranslating = await File.ReadAllTextAsync(plain);

        viewModel.TranslateToEnglish = true;
        await viewModel.RunAgainCommand.ExecuteAsync(null);

        var english = Path.Combine(directory, "a.en.txt");
        Assert.True(File.Exists(english), "the translated run wrote no .en file");
        Assert.Contains("[en]", await File.ReadAllTextAsync(english), StringComparison.Ordinal);

        // Untouched, rather than overwritten with English under its old name.
        Assert.Equal(beforeTranslating, await File.ReadAllTextAsync(plain));
        Assert.DoesNotContain("[en]", await File.ReadAllTextAsync(plain), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWordTimedFormatIsRefusedUnderTheOptInRatherThanWrittenAgainstTheWrongWords()
    {
        // Translation carries no word timings: the English words are not the words that were
        // spoken and nothing aligns them. A file written anyway would highlight the wrong word at
        // every moment and look entirely correct doing it, so the run is refused before it starts.
        var (viewModel, directory) = Create();
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);

        foreach (var format in viewModel.Formats)
        {
            format.IsSelected = format.Id == "vtt-words";
        }

        viewModel.TranslateToEnglish = true;
        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Pending, viewModel.Jobs[0].State);
        Assert.Contains("word timings", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(directory, "a.en.vtt")));

        // The same format is fine the moment the opt-in is off: what is refused is the pair.
        viewModel.TranslateToEnglish = false;
        await viewModel.StartCommand.ExecuteAsync(null);
        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
    }

    [Fact]
    public async Task TheSwitcherAppearsOnlyForARowThatHasEnglishAndNeverStrandsAnybodyOnAnEmptyPane()
    {
        var (viewModel, directory) = Create();
        SelectPlainTextOnly(viewModel);

        // One translated file, then a second that is not: the queue holds both kinds at once.
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        viewModel.TranslateToEnglish = true;
        await viewModel.StartCommand.ExecuteAsync(null);

        viewModel.AddFiles([WriteWav(directory, "b.wav")]);
        viewModel.TranslateToEnglish = false;
        await viewModel.StartCommand.ExecuteAsync(null);

        var translated = viewModel.Jobs.First(j => j.FileName == "a.wav");
        var plain = viewModel.Jobs.First(j => j.FileName == "b.wav");

        viewModel.SelectedJob = translated;
        Assert.True(viewModel.CanShowTranslation);

        viewModel.TranscriptPane = 1;
        Assert.Same(translated.TranslatedLines, viewModel.VisibleLines);

        // The pane a person chose is theirs to keep, so the index is not reset on the way past a
        // row that cannot honour it — what changes is that the switcher is not drawn and the lines
        // fall back to what was spoken. Without the fallback this reads as a transcript that came
        // back blank.
        viewModel.SelectedJob = plain;
        Assert.False(viewModel.CanShowTranslation);
        Assert.Equal(1, viewModel.TranscriptPane);
        Assert.Same(plain.Lines, viewModel.VisibleLines);

        viewModel.SelectedJob = translated;
        Assert.True(viewModel.CanShowTranslation);
        Assert.Same(translated.TranslatedLines, viewModel.VisibleLines);
    }

    [Fact]
    public async Task ARowThatFinishesWhileItIsSelectedGetsItsSwitcherWithoutBeingClickedAway()
    {
        // Selecting a different row notifies through SelectedJob; a row that finishes while it is
        // already selected does not, and the switcher would stay hidden on the transcript that had
        // just been translated until the user clicked another file and back.
        var (viewModel, directory) = Create();
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        SelectPlainTextOnly(viewModel);
        viewModel.TranslateToEnglish = true;

        var notified = new List<string?>();
        viewModel.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.True(viewModel.CanShowTranslation);
        Assert.Contains(nameof(TranscribeViewModel.CanShowTranslation), notified);
        Assert.Contains(nameof(TranscribeViewModel.VisibleLines), notified);
    }

    [Fact]
    public void WithoutTheTranslationModelTheOptInIsDisabledWithAReasonAUserCanActOn()
    {
        var viewModel = new TranscribeViewModel(
            new EngineProvider(new LocalModelStore(Directory.CreateTempSubdirectory("uindosill-tr").FullName), () => true),
            () => new EngineSelection());

        Assert.False(viewModel.CanTranslate);
        Assert.Contains("not installed", viewModel.TranslationHint, StringComparison.Ordinal);
        Assert.Contains("Models tab", viewModel.TranslationHint, StringComparison.Ordinal);
    }

    [Fact]
    public void APartialCheckpointIsNotAnInstalledOneAndTheOptInStaysOff()
    {
        // Nine files, all or nothing. A set missing its tokenizer loads until it does not, so the
        // question is asked of the whole entry rather than of whichever file arrived first.
        var directory = Directory.CreateTempSubdirectory("uindosill-tr").FullName;
        var store = new LocalModelStore(directory);
        var model = Assert.Single(ModelCatalog.Default.TranslationModels);

        var target = Path.GetDirectoryName(store.PathFor(model, model.Files[0]))!;
        Directory.CreateDirectory(target);

        foreach (var file in model.Files.Take(model.Files.Count - 1))
        {
            File.WriteAllText(store.PathFor(model, file), "not really a checkpoint");
        }

        var partial = new TranscribeViewModel(new EngineProvider(store, () => true), () => new EngineSelection());
        Assert.False(partial.CanTranslate);

        File.WriteAllText(store.PathFor(model, model.Files[^1]), "not really a checkpoint");

        var complete = new TranscribeViewModel(new EngineProvider(store, () => true), () => new EngineSelection());
        Assert.True(complete.CanTranslate);
        Assert.Null(complete.TranslationHint);
    }

    [Fact]
    public void RemovingTheTranslatorTurnsTheOptInOffRatherThanLeavingItTicked()
    {
        // A ticked box with nothing behind it would write the source transcript into files named
        // .en and report it as "Finished" — the silent failure the command line refuses.
        var directory = Directory.CreateTempSubdirectory("uindosill-tr").FullName;
        var store = new LocalModelStore(directory);
        var model = Assert.Single(ModelCatalog.Default.TranslationModels);

        Directory.CreateDirectory(Path.GetDirectoryName(store.PathFor(model, model.Files[0]))!);
        foreach (var file in model.Files)
        {
            File.WriteAllText(store.PathFor(model, file), "not really a checkpoint");
        }

        var viewModel = new TranscribeViewModel(new EngineProvider(store, () => true), () => new EngineSelection());
        viewModel.TranslateToEnglish = true;
        Assert.True(viewModel.CanTranslate);

        File.Delete(store.PathFor(model, model.Files[0]));
        viewModel.RefreshTranslationAvailability();

        Assert.False(viewModel.CanTranslate);
        Assert.False(viewModel.TranslateToEnglish);
        Assert.NotNull(viewModel.TranslationHint);
    }

    [AvaloniaFact]
    public void TheModelsTabTellsTheTranscribeTabWhenTheTranslatorArrives()
    {
        // The two tabs are siblings that do not know about each other, and each opt-in is wired to
        // its own model: one call for both would light a checkbox whose model is still missing.
        var directory = Directory.CreateTempSubdirectory("uindosill-tr").FullName;
        var store = new LocalModelStore(directory);
        var main = new MainWindowViewModel(new FakeEngineProvider(), store, ModelCatalog.Default, player: new FakeMediaPlayer());
        var translator = Assert.Single(
            main.Models.Models, m => m.Descriptor.Task == ModelTask.Translation);

        var translation = 0;
        var speakers = 0;
        main.Transcribe.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TranscribeViewModel.CanTranslate))
            {
                translation++;
            }

            if (e.PropertyName == nameof(TranscribeViewModel.CanLabelSpeakers))
            {
                speakers++;
            }
        };

        translator.IsInstalled = true;

        Assert.True(translation > 0, "the Transcribe tab was never told the translator arrived");
        Assert.Equal(0, speakers);
    }


    [AvaloniaFact]
    public void TheWindowCarriesTheOptInAndASwitcherThatIsHiddenUntilThereIsEnglish()
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-tr").FullName;
        var main = new MainWindowViewModel(new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
        var window = new MainWindow { DataContext = main };
        window.Show();
        window.UpdateLayout();

        // Bound to something, rather than a control the view never reads: this window has shipped
        // one of those before.
        var optIn = window.FindControl<CheckBox>("TranslateToEnglish");
        Assert.NotNull(optIn);
        Assert.False(optIn!.IsChecked);
        Assert.True(optIn.IsEnabled);   // the fake provider has a translator

        main.Transcribe.TranslateToEnglish = true;
        window.UpdateLayout();
        Assert.True(optIn.IsChecked);

        // No row has English yet, so there is nothing to switch between.
        var switcher = window.FindControl<Border>("TranscriptPaneSwitcher");
        Assert.NotNull(switcher);
        Assert.False(switcher!.IsVisible);
    }

    [AvaloniaFact]
    public async Task TheSwitcherIsDrawnOnceARowHasEnglishAndItsPillsChangeThePane()
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-tr").FullName;
        var main = new MainWindowViewModel(new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
        main.Transcribe.OutputDirectory = directory;
        await main.Session.LoadAsync(new EngineSelection { Model = main.Models.SelectedDescriptor });

        var window = new MainWindow { DataContext = main };
        window.Show();

        main.Transcribe.AddFiles([WriteWav(directory, "a.wav")]);
        SelectPlainTextOnly(main.Transcribe);
        main.Transcribe.TranslateToEnglish = true;
        await main.Transcribe.StartCommand.ExecuteAsync(null);
        window.UpdateLayout();

        var switcher = window.FindControl<Border>("TranscriptPaneSwitcher");
        Assert.NotNull(switcher);
        Assert.True(switcher!.IsVisible, "a translated row was not given a switcher");

        var english = window.FindControl<RadioButton>("PaneEnglish");
        Assert.NotNull(english);
        Assert.False(english!.IsChecked);

        // Through the control rather than the property, because what is under test is the binding.
        english.IsChecked = true;
        window.UpdateLayout();

        Assert.Equal(1, main.Transcribe.TranscriptPane);
        Assert.Same(main.Transcribe.SelectedJob!.TranslatedLines, main.Transcribe.VisibleLines);
    }
}
