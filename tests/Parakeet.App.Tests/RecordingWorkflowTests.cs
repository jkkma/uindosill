using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Parakeet.App.Services;
using Parakeet.App.Services.Tools;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;

namespace Parakeet.App.Tests;

public class RecordingWorkflowTests
{
    [AvaloniaFact]
    public void AFailedFolderDropKeepsTheWindowAndValidSiblingRecordings()
    {
        var missing = TestTemp.NewDirectory("uindosill-missing-drop");
        Directory.Delete(missing);
        var first = NewFile("direct.wav");
        var folder = TestTemp.NewDirectory("uindosill-valid-drop");
        var second = Path.Combine(folder, "inside.wav");
        File.WriteAllText(second, "UI fixture");
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateFile(PickedFolder(missing)));
        transfer.Add(DataTransferItem.CreateFile(PickedFile(first)));
        transfer.Add(DataTransferItem.CreateFile(PickedFolder(folder)));
        var main = NewMain();
        var window = new MainWindow { DataContext = main };
        window.Show();
        try
        {
            var zone = window.FindControl<Border>("DropZone")!;
            zone.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer, zone, new Point(), KeyModifiers.None));
            Settle(window);

            Assert.True(window.IsVisible);
            Assert.Equal(new[] { first, second }, main.Transcribe.Jobs.Select(job => job.Path));
            Assert.Contains("Added 2 files", main.Transcribe.StatusMessage, StringComparison.Ordinal);
            Assert.Contains("Could not open " + Path.GetFileName(missing), main.Transcribe.StatusMessage, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void BrowseAddsSeveralLocalRecordingsAndUpdatesTheQueue()
    {
        var first = NewFile("first.wav");
        var second = NewFile("second.wav");
        var provider = new Picker { Files = [PickedFile(first), PickedFile(second)] };
        var main = NewMain();
        var window = new MainWindow(provider.Open) { DataContext = main };
        window.Show();
        try
        {
            var empty = window.FindControl<StackPanel>("QueueEmptyState")!;
            Assert.True(empty.IsEffectivelyVisible);

            Browse(window);

            Assert.NotNull(provider.Options);
            Assert.True(provider.Options!.AllowMultiple);
            Assert.Contains(provider.Options.FileTypeFilter!.SelectMany(type => type.Patterns!), pattern => pattern == "*.wav");
            Assert.Equal(new[] { first, second }, main.Transcribe.Jobs.Select(job => job.Path));
            Assert.Equal("2", window.FindControl<TextBlock>("QueueCount")!.Text);
            Assert.False(empty.IsVisible);
            Assert.Contains(window.FindControl<ListBox>("RecordingQueue")!, window.GetVisualDescendants());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CancellingThePickerPreservesTheQueueAndStatus()
    {
        var provider = new Picker();
        var main = NewMain();
        main.Transcribe.AddFiles([NewFile("keep.wav")]);
        main.Transcribe.StatusMessage = "Keep this status.";
        var selected = main.Transcribe.Jobs.Single();
        var window = new MainWindow(provider.Open) { DataContext = main };
        window.Show();
        try
        {
            Browse(window);
            Assert.Same(selected, Assert.Single(main.Transcribe.Jobs));
            Assert.Equal("Keep this status.", main.Transcribe.StatusMessage);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ASelectionWithoutLocalFilesExplainsWhyNothingWasAdded()
    {
        var provider = new Picker { Files = [PickedFile(new Uri("https://example.invalid/recording.wav"))] };
        var main = NewMain();
        var window = new MainWindow(provider.Open) { DataContext = main };
        window.Show();
        try
        {
            Browse(window);
            Assert.Empty(main.Transcribe.Jobs);
            Assert.Contains("stored on this computer", main.Transcribe.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AFailedPickerReportsTheFailureAndCanBeOpenedAgain()
    {
        var provider = new Picker { Failure = new IOException("Picker unavailable.") };
        var main = NewMain();
        var window = new MainWindow(provider.Open) { DataContext = main };
        window.Show();
        try
        {
            Browse(window);
            Assert.Empty(main.Transcribe.Jobs);
            Assert.Contains("Picker unavailable.", main.Transcribe.StatusMessage, StringComparison.Ordinal);

            var path = NewFile("after-retry.wav");
            provider.Failure = null;
            provider.Files = [PickedFile(path)];
            Browse(window);
            Assert.Equal(path, Assert.Single(main.Transcribe.Jobs).Path);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void BrowseCannotOpenThePickerWhileABatchIsRunning()
    {
        var provider = new Picker();
        var main = NewMain();
        var window = new MainWindow(provider.Open) { DataContext = main };
        window.Show();
        try
        {
            var button = window.FindControl<Button>("BrowseFiles")!;
            Assert.True(button.IsEffectivelyEnabled);
            main.Transcribe.IsRunning = true;
            Settle(window);
            Assert.False(button.IsEffectivelyEnabled);

            // Raising the event directly also exercises the handler's guard for callers that
            // bypass the disabled control, such as an accessibility or automation client.
            Browse(window);
            Assert.Equal(0, provider.Calls);
            Assert.Empty(main.Transcribe.Jobs);
            main.Transcribe.IsRunning = false;
            Settle(window);
            Assert.True(button.IsEffectivelyEnabled);
        }
        finally { main.Transcribe.IsRunning = false; window.Close(); }
    }

    [AvaloniaFact]
    public void ExportShowsTheSelectedRecordingAndClearingTheQueueRestoresTheEmptyState()
    {
        var main = NewMain();
        var first = new JobViewModel(NewFile("one.wav"));
        var second = new JobViewModel(NewFile("two.wav"));
        main.Transcribe.Jobs.Add(first);
        main.Transcribe.Jobs.Add(second);
        main.Transcribe.SelectedJob = first;
        main.SelectedTab = 2;
        var window = new MainWindow { DataContext = main };
        window.Show();
        try
        {
            var recording = window.FindControl<TextBlock>("ExportRecording")!;
            Assert.Contains(recording, window.GetVisualDescendants());
            Assert.Equal(first.DisplayName, recording.Text);

            main.Transcribe.SelectedJob = second;
            Settle(window);
            Assert.Equal(second.DisplayName, recording.Text);
            main.Transcribe.ClearCommand.Execute(null);
            Settle(window);
            Assert.False(recording.IsVisible);

            main.SelectedTab = 0;
            Settle(window);
            Assert.True(window.FindControl<StackPanel>("QueueEmptyState")!.IsEffectivelyVisible);
            Assert.Equal("0", window.FindControl<TextBlock>("QueueCount")!.Text);
        }
        finally { window.Close(); }
    }

    private static MainWindowViewModel NewMain()
    {
        var directory = TestTemp.NewDirectory("uindosill-recording-ui");
        return new MainWindowViewModel(new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default,
            settings: new AppSettingsStore(Path.Combine(directory, "settings.json")),
            backendsOnDisk: () => [ComputeBackend.Cpu], player: new FakeMediaPlayer(), fetcher: new FakeMediaUrlFetcher());
    }

    private static string NewFile(string name)
    {
        var path = TestTemp.NewPath(name);
        File.WriteAllText(path, "UI fixture");
        return path;
    }

    private static void Browse(MainWindow window)
    {
        window.FindControl<Button>("BrowseFiles")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(window);
    }

    private static void Settle(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private sealed class Picker
    {
        public IReadOnlyList<IStorageFile> Files { get; set; } = [];
        public Exception? Failure { get; set; }
        public FilePickerOpenOptions? Options { get; private set; }
        public int Calls { get; private set; }

        public Task<IReadOnlyList<IStorageFile>> Open(FilePickerOpenOptions options)
        {
            Calls++;
            Options = options;
            return Failure is { } failure ? Task.FromException<IReadOnlyList<IStorageFile>>(failure) : Task.FromResult(Files);
        }
    }

    private static IStorageFile PickedFile(string path) => PickedFile(new Uri(path));

    private static IStorageFolder PickedFolder(string path)
    {
        var folder = DispatchProxy.Create<IStorageFolder, StorageProxy>();
        ((StorageProxy)folder).Handler = (method, _) => method!.Name switch
        {
            "get_Name" => Path.GetFileName(path),
            "get_Path" => new Uri(path),
            "Dispose" => null,
            _ => throw new NotSupportedException(method.Name),
        };
        return folder;
    }

    private static IStorageFile PickedFile(Uri path)
    {
        var file = DispatchProxy.Create<IStorageFile, StorageProxy>();
        ((StorageProxy)file).Handler = (method, _) => method!.Name switch
        {
            "get_Name" => Path.GetFileName(path.LocalPath),
            "get_Path" => path,
            "get_CanBookmark" => false,
            "Dispose" => null,
            _ => throw new NotSupportedException(method.Name),
        };
        return file;
    }

    // Avalonia marks storage interfaces as not implementable by application code. A runtime
    // proxy supplies the platform seam in this test without adding a mocking dependency.
    public class StorageProxy : DispatchProxy
    {
        public Func<MethodInfo?, object?[]?, object?>? Handler { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler!(targetMethod, args);
    }
}
