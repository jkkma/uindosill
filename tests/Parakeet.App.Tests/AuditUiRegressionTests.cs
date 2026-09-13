using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.Core.Jobs;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;

namespace Parakeet.App.Tests;

/// <summary>
/// Regressions from the desktop audit: getters were current while the bound controls stayed stale.
/// These show the real window on Avalonia's headless host; no model inference or network is needed.
/// </summary>
public class AuditUiRegressionTests
{
    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SelectingFinishedRecordingsUpdatesThePaneControlsAndTheirContents(bool tidied, bool translated)
    {
        var main = NewMain(out var directory);
        var plain = CompletedRecording(Path.Combine(directory, "plain.wav"));
        var alternate = CompletedRecording(Path.Combine(directory, "alternate.wav"), tidied, translated);
        main.Transcribe.Jobs.Add(plain);
        main.Transcribe.Jobs.Add(alternate);
        main.Transcribe.SelectedJob = plain;

        var window = new MainWindow { DataContext = main };
        window.Show();
        try
        {
            var switcher = window.FindControl<Border>("TranscriptPaneSwitcher")!;
            var tidyPill = window.FindControl<RadioButton>("PaneTidied")!;
            var englishPill = window.FindControl<RadioButton>("PaneEnglish")!;
            var lines = window.FindControl<ItemsControl>("TranscriptLines")!;
            Settle(window);

            Assert.False(switcher.IsVisible);
            Assert.False(tidyPill.IsVisible);
            Assert.False(englishPill.IsVisible);
            Assert.Same(plain.Lines, lines.ItemsSource);

            // The window is already bound to a plain recording. Rebinding the whole window would
            // hide the regression: each getter already returned the correct value in the audit.
            main.Transcribe.SelectedJob = alternate;
            Settle(window);

            Assert.True(switcher.IsVisible);
            Assert.Equal(tidied, tidyPill.IsVisible);
            Assert.Equal(translated, englishPill.IsVisible);

            if (translated)
            {
                englishPill.IsChecked = true;
                Settle(window);
                Assert.Same(alternate.TranslatedLines, lines.ItemsSource);
            }

            if (tidied)
            {
                tidyPill.IsChecked = true;
                Settle(window);
                Assert.Same(alternate.TidiedLines, lines.ItemsSource);
            }

            main.Transcribe.SelectedJob = plain;
            Settle(window);
            Assert.False(switcher.IsVisible);
            Assert.False(tidyPill.IsVisible);
            Assert.False(englishPill.IsVisible);
            Assert.Same(plain.Lines, lines.ItemsSource);

            // The chosen pane is preserved, but a plain recording falls back to its spoken text.
            main.Transcribe.SelectedJob = alternate;
            Settle(window);
            Assert.True(switcher.IsVisible);
            Assert.Same(tidied ? alternate.TidiedLines : alternate.TranslatedLines, lines.ItemsSource);

            main.Transcribe.SelectedJob = null;
            Settle(window);
            Assert.False(switcher.IsVisible);
            Assert.False(tidyPill.IsVisible);
            Assert.False(englishPill.IsVisible);
            Assert.Null(lines.ItemsSource);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("single")]
    [InlineData("all")]
    [InlineData("external")]
    public void TheRemoveAllButtonFollowsInstallationAndEveryRemovalRoute(string removal)
    {
        var main = NewMain(out var directory);
        main.SelectedTab = 1;
        var model = main.Models.SelectedDescriptor!;
        var store = new LocalModelStore(directory);
        var window = new MainWindow { DataContext = main };
        window.Show();
        try
        {
            var button = window.FindControl<Button>("RemoveAllModels")!;
            Settle(window);
            AssertRemoveState(main.Models, button, expected: false);

            var commandChanges = 0;
            var properties = new List<string?>();
            main.Models.RemoveAllCommand.CanExecuteChanged += (_, _) => commandChanges++;
            main.Models.PropertyChanged += (_, e) => properties.Add(e.PropertyName);

            Install(directory, model);
            main.Models.Refresh();
            Settle(window);

            AssertRemoveState(main.Models, button, expected: true);
            Assert.True(commandChanges > 0);
            Assert.Contains(nameof(ModelsViewModel.CanRemoveAll), properties);

            commandChanges = 0;
            properties.Clear();
            switch (removal)
            {
                case "single":
                    main.Models.RemoveCommand.Execute(null);
                    break;
                case "all":
                    main.Models.RemoveAllCommand.Execute(null);
                    break;
                default:
                    Assert.True(store.Remove(model));
                    main.Models.Refresh();
                    break;
            }

            Settle(window);
            Assert.False(store.IsInstalled(model));
            AssertRemoveState(main.Models, button, expected: false);
            Assert.True(commandChanges > 0);
            Assert.Contains(nameof(ModelsViewModel.CanRemoveAll), properties);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheRemoveAllButtonDisablesDuringABatchAndRecoversWhenItEnds()
    {
        var main = NewMain(out var directory);
        var model = main.Models.SelectedDescriptor!;
        Install(directory, model);
        main.Models.Refresh();
        main.SelectedTab = 1;
        var window = new MainWindow { DataContext = main };
        window.Show();
        try
        {
            var button = window.FindControl<Button>("RemoveAllModels")!;
            Settle(window);
            AssertRemoveState(main.Models, button, expected: true);

            var commandChanges = 0;
            main.Models.RemoveAllCommand.CanExecuteChanged += (_, _) => commandChanges++;
            main.Transcribe.IsRunning = true;
            Settle(window);

            AssertRemoveState(main.Models, button, expected: false);
            Assert.True(commandChanges > 0);

            // RelayCommand.Execute itself does not check CanExecute. A caller other than the
            // button must not be able to remove a model while the batch holds it either.
            main.Models.RemoveAllCommand.Execute(null);
            Assert.True(new LocalModelStore(directory).IsInstalled(model));

            commandChanges = 0;
            main.Transcribe.IsRunning = false;
            Settle(window);
            AssertRemoveState(main.Models, button, expected: true);
            Assert.True(commandChanges > 0);
        }
        finally
        {
            main.Transcribe.IsRunning = false;
            window.Close();
        }
    }

    private static MainWindowViewModel NewMain(out string directory)
    {
        directory = TestTemp.NewDirectory("uindosill-audit-ui");
        var settings = new AppSettingsStore(Path.Combine(directory, "settings.json"));
        settings.Save(new AppSettings { CheckForUpdatesOnLaunch = false, CudaPackAutoInstallDeclined = true });
        return new MainWindowViewModel(
            new FakeEngineProvider(),
            new LocalModelStore(directory),
            ModelCatalog.Default,
            settings: settings,
            backendsOnDisk: () => [ComputeBackend.Cpu],
            player: new FakeMediaPlayer());
    }

    private static JobViewModel CompletedRecording(string path, bool tidied = false, bool translated = false)
    {
        var spoken = new TranscriptDocument
        {
            SourceName = path,
            AudioDuration = TimeSpan.FromSeconds(1),
            Segments = [new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Text = "spoken words" }],
        };
        var english = spoken with
        {
            TranslatedTo = "en",
            Segments = [spoken.Segments[0] with { Text = "English words" }],
        };
        var tidy = spoken with { Segments = [spoken.Segments[0] with { Text = "Spoken words." }] };
        var job = new JobViewModel(path);
        job.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = path },
            State = JobState.Completed,
            Document = translated ? english : spoken,
            TidiedDocument = tidied ? tidy : null,
        }, source: translated ? spoken : null);
        return job;
    }

    private static void Install(string directory, ModelDescriptor model)
    {
        foreach (var file in model.Files)
        {
            var path = model.IsMultiFile
                ? Path.Combine(directory, model.DirectoryName!, file.FileName)
                : Path.Combine(directory, file.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "weights");
        }
    }

    private static void AssertRemoveState(ModelsViewModel tab, Button button, bool expected)
    {
        Assert.Equal(expected, tab.CanRemoveAll);
        Assert.Equal(expected, tab.RemoveAllCommand.CanExecute(null));
        Assert.Equal(expected, button.IsEffectivelyEnabled);
    }

    private static void Settle(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
