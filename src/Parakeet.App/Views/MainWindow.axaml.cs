using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Parakeet.App.ViewModels;

namespace Parakeet.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var dropZone = this.FindControl<Border>("DropZone");
        if (dropZone is not null)
        {
            DragDrop.AddDragOverHandler(dropZone, OnDragOver);
            DragDrop.AddDropHandler(dropZone, OnDrop);
        }

        var browse = this.FindControl<Button>("BrowseOutput");
        if (browse is not null)
        {
            browse.Click += OnBrowseOutput;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (!e.DataTransfer.Formats.Contains(DataFormat.File))
        {
            return;
        }

        var items = e.DataTransfer.TryGetFiles();
        if (items is null)
        {
            return;
        }

        var paths = new List<string>();
        foreach (var item in items)
        {
            // A dropped folder is a reasonable thing to do with a transcription app, so it is
            // expanded rather than ignored; the queue filters out what cannot be opened.
            if (item is IStorageFolder folder && folder.TryGetLocalPath() is { } folderPath)
            {
                paths.AddRange(Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly));
                continue;
            }

            if (item.TryGetLocalPath() is { } path)
            {
                paths.Add(path);
            }
        }

        viewModel.Transcribe.AddFiles(paths);
    }

    private async void OnBrowseOutput(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose where transcripts are written",
                AllowMultiple = false,
            }).ConfigureAwait(true);

            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            {
                viewModel.Transcribe.OutputDirectory = path;
            }
        }
#pragma warning disable CA1031 // A picker that fails must not take the window with it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            viewModel.Transcribe.StatusMessage = ex.Message;
        }
    }
}
