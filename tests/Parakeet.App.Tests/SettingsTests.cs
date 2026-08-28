using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.Core.Models;
using Parakeet.Engine.LlamaServer;

namespace Parakeet.App.Tests;

public class AppSettingsStoreTests
{
    private static string TempFile() =>
        TestTemp.NewPath("settings.json");

    [Fact]
    public void AMissingFileIsTheShippedDefault()
    {
        var store = new AppSettingsStore(TempFile());

        Assert.True(store.Load().CheckForUpdatesOnLaunch);
    }

    [Fact]
    public void TheSettingSurvivesARoundTrip()
    {
        var path = TempFile();
        try
        {
            Assert.True(new AppSettingsStore(path).Save(new AppSettings { CheckForUpdatesOnLaunch = false }));

            Assert.False(new AppSettingsStore(path).Load().CheckForUpdatesOnLaunch);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void AskThinkingIsOffAsShippedAndSurvivesARoundTrip()
    {
        var path = TempFile();
        try
        {
            Assert.False(new AppSettingsStore(path).Load().AskThinking);

            Assert.True(new AppSettingsStore(path).Save(new AppSettings { AskThinking = true }));
            Assert.True(new AppSettingsStore(path).Load().AskThinking);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void TheAskModeIsAutomaticAsShippedAndSurvivesARoundTrip()
    {
        // Automatic is the register's decision 3 router, and it ships on: the alternative is a
        // person having to know which tier answers which shape of question.
        var path = TempFile();
        try
        {
            Assert.Equal(AskModePreference.Automatic, new AppSettingsStore(path).Load().AskMode);

            Assert.True(new AppSettingsStore(path).Save(
                new AppSettings { AskMode = AskModePreference.WholeTranscript }));
            Assert.Equal(
                AskModePreference.WholeTranscript, new AppSettingsStore(path).Load().AskMode);

            Assert.True(new AppSettingsStore(path).Save(
                new AppSettings { AskMode = AskModePreference.Retrieval }));
            Assert.Equal(AskModePreference.Retrieval, new AppSettingsStore(path).Load().AskMode);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void TheOneDayOldBooleanIsHonouredOnlyWhereItCarriedAChoice()
    {
        // askWholeTranscript shipped 2026-08-25 and lived one day. A stored true was somebody
        // deliberately turning it on and becomes the fixed whole-transcript setting; a stored
        // false was the default nobody had to touch, so it carries no choice and becomes
        // Automatic rather than pinning a user to retrieval on a value they never set.
        var path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            File.WriteAllText(path, "{\"askWholeTranscript\":true}");
            Assert.Equal(
                AskModePreference.WholeTranscript, new AppSettingsStore(path).Load().AskMode);

            File.WriteAllText(path, "{\"askWholeTranscript\":false}");
            Assert.Equal(AskModePreference.Automatic, new AppSettingsStore(path).Load().AskMode);

            // And the new name wins wherever both are present.
            File.WriteAllText(path, "{\"askWholeTranscript\":true,\"askMode\":\"retrieval\"}");
            Assert.Equal(AskModePreference.Retrieval, new AppSettingsStore(path).Load().AskMode);

            // An unreadable name degrades to as-shipped, like every other setting here.
            File.WriteAllText(path, "{\"askMode\":\"whatever\"}");
            Assert.Equal(AskModePreference.Automatic, new AppSettingsStore(path).Load().AskMode);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void TheEvidenceDepthRoundTripsAndDegradesToTheSlowSetting()
    {
        var path = Path.Combine(TestTemp.NewDirectory("uindosill-settings"), "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var store = new AppSettingsStore(path);
            store.Update(current => current with { AskEvidence = AskEvidenceDepth.Fast });
            Assert.Equal(AskEvidenceDepth.Fast, new AppSettingsStore(path).Load().AskEvidence);

            // Absent means as-shipped, and as-shipped is the depth every measured citation figure
            // in this project was taken at rather than the fastest one.
            File.WriteAllText(path, "{}");
            Assert.Equal(AskEvidenceDepth.Thorough, new AppSettingsStore(path).Load().AskEvidence);

            // A value this build does not know degrades the same way. The faster depths trade
            // recall nobody has measured, so they are chosen deliberately or not at all.
            File.WriteAllText(path, "{\"askEvidence\":\"whatever\"}");
            Assert.Equal(AskEvidenceDepth.Thorough, new AppSettingsStore(path).Load().AskEvidence);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void EachDepthNamesItsOwnWindowCount()
    {
        // The numbers the 2026-08-27 run measured: 8 windows at 37.8 s, 6 at 31.7 s, 4 at 16.6 s.
        Assert.Equal(8, AppSettings.WindowsFor(AskEvidenceDepth.Thorough));
        Assert.Equal(6, AppSettings.WindowsFor(AskEvidenceDepth.Balanced));
        Assert.Equal(4, AppSettings.WindowsFor(AskEvidenceDepth.Fast));
        Assert.Equal(8, AppSettings.Default.EvidenceWindows);
    }

    [Fact]
    public void TheExpertPlacementIsAutomaticAsShippedAndSurvivesARoundTrip()
    {
        // Automatic asks the Vulkan loader which kind of graphics this is. The two fixed rows are
        // for a machine it reads wrongly, and both have to outlive a restart or the picker is a
        // control somebody has to find again every launch.
        var path = TempFile();
        try
        {
            Assert.Equal(
                MoeExpertPlacement.Automatic, new AppSettingsStore(path).Load().AskExpertPlacement);

            foreach (var placement in new[]
            {
                MoeExpertPlacement.Device,
                MoeExpertPlacement.SystemMemory,
                MoeExpertPlacement.Automatic,
            })
            {
                Assert.True(new AppSettingsStore(path).Save(
                    new AppSettings { AskExpertPlacement = placement }));
                Assert.Equal(placement, new AppSettingsStore(path).Load().AskExpertPlacement);
            }

            // The name, never the enum's number: reordering the enum must not turn one user's
            // saved choice into another's.
            Assert.Contains("\"askExpertPlacement\":\"automatic\"", File.ReadAllText(path), StringComparison.Ordinal);

            // A name this build does not know — a future setting read by an older build, or a
            // hand-edited file — degrades to as-shipped like every other setting here.
            File.WriteAllText(path, "{\"askExpertPlacement\":\"somewhere-else\"}");
            Assert.Equal(
                MoeExpertPlacement.Automatic, new AppSettingsStore(path).Load().AskExpertPlacement);

            // And a file written before the setting existed reads as automatic rather than as a
            // failure to load.
            File.WriteAllText(path, "{\"askThinking\":true}");
            var older = new AppSettingsStore(path).Load();
            Assert.True(older.AskThinking);
            Assert.Equal(MoeExpertPlacement.Automatic, older.AskExpertPlacement);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void EveryExpertPlacementHasAPickerRowAndChoosingOneWritesTheFile()
    {
        // The picker's getter finds the row for the current setting, so a placement with no row
        // is a bind-time throw on the Settings tab rather than a missing line in a list. Cheap to
        // hold here, and the kind of thing an enum gains a member without anyone noticing.
        var path = TempFile();
        try
        {
            var viewModel = new MainWindowViewModel(
                new FakeEngineProvider(),
                new LocalModelStore(TestTemp.NewDirectory("uindosill-placement")),
                ModelCatalog.Default,
                settings: new AppSettingsStore(path),
                answerEngines: new FakeAnswerEngineProvider());

            Assert.Equal(MoeExpertPlacement.Automatic, viewModel.AskExpertPlacement);

            foreach (var placement in Enum.GetValues<MoeExpertPlacement>())
            {
                var row = Assert.Single(
                    viewModel.AskExpertPlacements, choice => choice.Placement == placement);

                // The label is what a person reads, so it is neither the enum's spelling nor
                // either environment variable's name.
                Assert.NotEqual(placement.ToString(), row.Label);

                viewModel.SelectedAskExpertPlacement = row;
                Assert.Equal(placement, viewModel.AskExpertPlacement);
                Assert.Equal(row, viewModel.SelectedAskExpertPlacement);
                Assert.Equal(placement, new AppSettingsStore(path).Load().AskExpertPlacement);
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void TheChosenAskModelIsRememberedByNameAndOmittedWhenUnchosen()
    {
        // A name rather than a path: the models folder may move between installs, and a stored
        // path to a file since deleted is a setting that fails silently. Unchosen is omitted
        // from the file, so "never chosen" and "cleared" are one shape — the same rule the
        // backend and the output folder follow.
        var path = TempFile();
        try
        {
            Assert.Null(new AppSettingsStore(path).Load().AskModelFileName);

            Assert.True(new AppSettingsStore(path).Save(
                new AppSettings { AskModelFileName = "Qwen3.5-9B-Q4_K_M.gguf" }));
            Assert.Equal("Qwen3.5-9B-Q4_K_M.gguf", new AppSettingsStore(path).Load().AskModelFileName);
            Assert.Contains("askModelFileName", File.ReadAllText(path), StringComparison.Ordinal);

            Assert.True(new AppSettingsStore(path).Save(new AppSettings { AskModelFileName = null }));
            Assert.Null(new AppSettingsStore(path).Load().AskModelFileName);
            Assert.DoesNotContain("askModelFileName", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void AFileThatIsNotJsonIsTheShippedDefaultRatherThanAThrow()
    {
        // Whatever a hand-edited or half-written settings file contains, the window opens.
        var path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not json");

            Assert.True(new AppSettingsStore(path).Load().CheckForUpdatesOnLaunch);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void AJsonFileMissingTheKeyIsTheShippedDefault()
    {
        var path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"somethingElse\":1}");

            Assert.True(new AppSettingsStore(path).Load().CheckForUpdatesOnLaunch);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void TheDiariserProviderIsAutomaticAsShippedAndSurvivesARoundTrip()
    {
        // Null is automatic. The window passed a hardcoded "auto" until this setting existed, so "it
        // round-trips" is the whole feature working.
        //
        // **"webgpu" left this list on 2026-08-27 and returned on 2026-08-28**, when the diariser's
        // two neural stages gained an ONNX export for a provider to run. While it had none the name
        // selected nothing and had to read back as automatic; now it names a real route and is
        // stored verbatim. "dml" did not come back and its absence is still asserted below.
        var path = TempFile();
        try
        {
            Assert.Null(new AppSettingsStore(path).Load().DiarisationProvider);

            foreach (var provider in new[] { "cpu", "cuda", "webgpu" })
            {
                Assert.True(new AppSettingsStore(path).Save(
                    new AppSettings { DiarisationProvider = provider }));
                Assert.Equal(provider, new AppSettingsStore(path).Load().DiarisationProvider);
            }

            // Automatic is written as an absent key rather than the word, so that "never chosen"
            // and "chose automatic" are one shape in the file rather than two Load must reconcile.
            Assert.True(new AppSettingsStore(path).Save(new AppSettings { DiarisationProvider = null }));
            Assert.DoesNotContain("diarisationProvider", File.ReadAllText(path), StringComparison.Ordinal);

            // And the word itself, if a hand-edited file carries it, reads back as the same null.
            File.WriteAllText(path, "{\"diarisationProvider\":\"auto\"}");
            Assert.Null(new AppSettingsStore(path).Load().DiarisationProvider);

            // **A hand-edited "webgpu" is honoured now, and that is the change.** Storing it is
            // only permission to name the route: whether this machine has the exported graphs is
            // the Settings window's question, not this reader's, and the window offers the row's
            // one-time preparation rather than failing at load.
            File.WriteAllText(path, "{\"diarisationProvider\":\"webgpu\"}");
            Assert.Equal("webgpu", new AppSettingsStore(path).Load().DiarisationProvider);

            // A name this window does not offer still degrades to automatic rather than being
            // passed to the sidecar. dml is the case that matters: the sidecar would accept it, and
            // on the previous ONNX diariser it scored 53% diarisation error at ONNX Runtime's own
            // defaults. Nothing has been run on it since, which is why it is not offered.
            File.WriteAllText(path, "{\"diarisationProvider\":\"dml\"}");
            Assert.Null(new AppSettingsStore(path).Load().DiarisationProvider);

            // A file written before the setting existed reads as automatic, not as a failed load.
            File.WriteAllText(path, "{\"askThinking\":true}");
            var older = new AppSettingsStore(path).Load();
            Assert.True(older.AskThinking);
            Assert.Null(older.DiarisationProvider);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void TheDiariserBatchSizeIsTheModelsOwnAsShippedAndSurvivesARoundTrip()
    {
        // Null means the checkpoint's own value, which is what makes running the published
        // artefact's configuration the thing a person gets by not touching this.
        var path = TempFile();
        try
        {
            Assert.Null(new AppSettingsStore(path).Load().DiarisationBatchSize);

            foreach (var size in AppSettingsStore.DiarisationBatchSizes)
            {
                Assert.True(new AppSettingsStore(path).Save(
                    new AppSettings { DiarisationBatchSize = size }));
                Assert.Equal(size, new AppSettingsStore(path).Load().DiarisationBatchSize);
            }

            Assert.True(new AppSettingsStore(path).Save(new AppSettings { DiarisationBatchSize = null }));
            Assert.DoesNotContain("diarisationBatchSize", File.ReadAllText(path), StringComparison.Ordinal);

            // A size this window does not offer degrades to the model's own rather than reaching
            // the pipeline. A hand-edited 512 would be accepted by the sidecar and would ask a
            // 16 GB machine for a working set it cannot produce, failing far less legibly.
            File.WriteAllText(path, "{\"diarisationBatchSize\":512}");
            Assert.Null(new AppSettingsStore(path).Load().DiarisationBatchSize);

            File.WriteAllText(path, "{\"diarisationBatchSize\":\"eight\"}");
            Assert.Null(new AppSettingsStore(path).Load().DiarisationBatchSize);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void EveryDiariserSettingHasAPickerRowAndChoosingOneWritesTheFile()
    {
        // The getters find the row for the current setting, so a stored value with no row is a
        // bind-time throw on the Settings tab rather than a quietly missing selection.
        var path = TempFile();
        try
        {
            var viewModel = new MainWindowViewModel(
                new FakeEngineProvider(),
                new LocalModelStore(TestTemp.NewDirectory("uindosill-diariser")),
                ModelCatalog.Default,
                settings: new AppSettingsStore(path),
                answerEngines: new FakeAnswerEngineProvider());

            Assert.Null(viewModel.DiarisationProvider);
            Assert.Null(viewModel.SelectedDiarisationProvider.Provider);
            Assert.Null(viewModel.SelectedDiarisationBatchSize.Size);

            // **The graphics row is the one exception, and it is the feature rather than a gap.**
            // Every other row is a name the sidecar can act on immediately, so choosing it writes
            // it. `webgpu` needs the exported graphs, so choosing it starts a one-time preparation
            // and keeps the choice only if that works — and under the canned provider there is no
            // speaker model to export from, so it must come straight back. Asserted here rather
            // than skipped, because "the row does not stick when it cannot work" is exactly what
            // stops a stored choice failing the next transcription.
            foreach (var row in viewModel.DiarisationProviders.Where(row => row.Provider != "webgpu"))
            {
                viewModel.SelectedDiarisationProvider = row;
                Assert.Equal(row.Provider, viewModel.DiarisationProvider);
                Assert.Equal(row.Provider, new AppSettingsStore(path).Load().DiarisationProvider);
            }

            var graphics = viewModel.DiarisationProviders.Single(row => row.Provider == "webgpu");
            viewModel.SelectedDiarisationProvider = graphics;
            Assert.Null(viewModel.DiarisationProvider);
            Assert.True(viewModel.HasSpeakerGraphsMessage);

            // Left as the automatic row rather than as a selection the picker cannot resolve.
            Assert.Null(viewModel.SelectedDiarisationProvider.Provider);

            // Put back, so the batch-size loop below starts from the state it used to.
            viewModel.SelectedDiarisationProvider =
                viewModel.DiarisationProviders.Single(row => row.Provider is null);

            foreach (var row in viewModel.DiarisationBatchSizes)
            {
                viewModel.SelectedDiarisationBatchSize = row;
                Assert.Equal(row.Size, viewModel.DiarisationBatchSize);
                Assert.Equal(row.Size, new AppSettingsStore(path).Load().DiarisationBatchSize);
            }

            // Every offered provider is one the settings reader will accept back. The two lists
            // are separate declarations and would otherwise drift into a picker whose choices do
            // not survive a restart.
            foreach (var row in viewModel.DiarisationProviders.Where(row => row.Provider is not null))
            {
                Assert.Contains(row.Provider, AppSettingsStore.DiarisationProviders);
            }

            foreach (var row in viewModel.DiarisationBatchSizes.Where(row => row.Size is not null))
            {
                Assert.Contains(row.Size!.Value, AppSettingsStore.DiarisationBatchSizes);
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void TheBatchSizePickerIsDisabledWhenTheChosenDiariserHasNoBatchToSet()
    {
        // False is a real answer rather than a missing one: the first diariser's batching is its
        // exported graph's geometry and the sidecar refuses the field outright, so a control left
        // enabled for it would offer a setting that turns every load into an error. The canned
        // provider is neither diariser and has no pipeline at all.
        var viewModel = new MainWindowViewModel(
            new FakeEngineProvider(),
            new LocalModelStore(TestTemp.NewDirectory("uindosill-nobatch")),
            ModelCatalog.Default,
            settings: new AppSettingsStore(TempFile()),
            answerEngines: new FakeAnswerEngineProvider());

        Assert.False(viewModel.CanSetDiarisationBatchSize);

        // Disabled with a reason beside it rather than hidden, on the speech-detection row's terms.
        Assert.NotNull(viewModel.DiarisationBatchSizeHint);
    }

    [Fact]
    public void TheSettingsFileSitsBesideTheWeightsAndNotInTheInstallDirectory()
    {
        // Same reason as the weights: a settings file under the install root is destroyed by every
        // update, so an update check the user switched off would switch itself back on.
        var settings = AppSettingsStore.DefaultPath();

        Assert.Equal(UserDataPaths.RootDirectory(), Path.GetDirectoryName(settings));
        Assert.Equal(
            Path.GetDirectoryName(LocalModelStore.DefaultRootDirectory()),
            Path.GetDirectoryName(settings));
        Assert.DoesNotContain(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            settings,
            StringComparison.Ordinal);
    }
}
