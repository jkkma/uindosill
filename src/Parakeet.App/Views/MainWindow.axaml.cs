using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Parakeet.App.ViewModels;

namespace Parakeet.App.Views;

public partial class MainWindow : Window
{
    private bool _shutdownRequested;

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

        WireHeaderBar();
    }

    /// <summary>
    /// Wires the headerbar's window buttons.
    /// </summary>
    /// <remarks>
    /// Dragging is not here. The headerbar carries
    /// <c>WindowDecorationProperties.ElementRole="TitleBar"</c>, so the platform moves the window
    /// and handles double-click-to-maximise itself — which is both less code than a PointerPressed
    /// handler calling BeginMoveDrag and better behaved, because the OS also knows about snapping.
    ///
    /// Every lookup is null-tolerant for the same reason the two above are — the headless test host
    /// builds this window, and a control that a future edit renames should fail a test rather than
    /// throw inside a constructor.
    /// </remarks>
    private void WireHeaderBar()
    {
        if (this.FindControl<Button>("WindowMinimise") is { } minimise)
        {
            minimise.Click += (_, _) => WindowState = WindowState.Minimized;
        }

        if (this.FindControl<Button>("WindowMaximise") is { } maximise)
        {
            maximise.Click += (_, _) => WindowState =
                WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        if (this.FindControl<Button>("WindowClose") is { } close)
        {
            // Close() rather than exiting: the shutdown ordering in OnClosing has to run.
            close.Click += (_, _) => Close();
        }
    }

    /// <summary>
    /// The launch check, started once the window exists so its answer has somewhere to appear.
    /// </summary>
    /// <remarks>
    /// Not awaited, and deliberately: an HTTPS request to GitHub must not sit between the user and
    /// a window they opened to transcribe something. It does nothing at all unless this is an
    /// installed copy and the setting is on, and every failure inside it becomes a line of text.
    /// </remarks>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Square the corner, once there is a handle to square. The design calls for a square
        // window, and on Windows 11 the compositor rounds top-level windows on its own terms —
        // extending the client area to the decorations does not hand the corner over. DWM takes a
        // preference rather than a radius, and one of its values is do-not-round, which is the
        // whole reason this design is reachable where the earlier 12px one was not.
        //
        // Nothing reads the answer: this is decoration, and a machine that refuses gets the
        // rounded corner it would have had anyway. See Services/WindowCorner.cs.
        if (TryGetPlatformHandle() is { } handle)
        {
            Services.WindowCorner.MakeSquare(handle.Handle);
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            _ = viewModel.Updates.CheckOnLaunchAsync();
        }
    }

    /// <summary>
    /// The first close request is turned into a shutdown: stop the batch, unload the model, release
    /// the native backend, and only then close. Without this the process reached its native static
    /// teardown with a CUDA backend still resident and aborted with <c>0xC0000409</c> — the app
    /// "crashed on exit" after a good run (gotcha 19). A second request while that is under way
    /// closes at once; the person asking twice has decided.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (e.Cancel || _shutdownRequested || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        _shutdownRequested = true;
        e.Cancel = true;
        _ = ShutdownThenCloseAsync(viewModel);
    }

    private async Task ShutdownThenCloseAsync(MainWindowViewModel viewModel)
    {
        // Off the Closing call stack first. With nothing running and a fake engine everything below
        // completes synchronously, and closing a window from inside its own Closing handler is a
        // re-entrancy nobody should have to reason about.
        await Task.Yield();

        try
        {
            await viewModel.ShutdownAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The window is going away; a failure here has nowhere to be shown.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        Close();
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
