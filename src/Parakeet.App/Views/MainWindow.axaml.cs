using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Parakeet.App.ViewModels;

namespace Parakeet.App.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// How often the Ask tab's transport is re-read. Ten times a second: fast enough that the
    /// highlight lands on the line being spoken rather than trailing it, slow enough that a
    /// three-hour transcript is not searched sixty times a second for no visible gain.
    /// </summary>
    private static readonly TimeSpan TransportRefresh = TimeSpan.FromMilliseconds(100);

    private readonly DispatcherTimer _transport;

    private bool _shutdownRequested;
    private bool _seeking;

    /// <summary>The Ask view model this window is currently listening to, so it can stop.</summary>
    private ViewModels.AskViewModel? _watching;

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
        WireSeekStrip();
        WireSearchBox();

        // The clock the Ask tab draws from, and it is here rather than in the view model on
        // purpose: a view model that starts a dispatcher timer needs a dispatcher to exist before
        // it is constructed, which is a requirement no test should have to satisfy. The view model
        // exposes a Tick it does nothing in unless something moved, and this is what calls it.
        _transport = new DispatcherTimer(DispatcherPriority.Background) { Interval = TransportRefresh };
        _transport.Tick += (_, _) => (DataContext as MainWindowViewModel)?.Ask.Tick();
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
    /// Makes the seek bar seekable: a press anywhere along it, and a drag along it, is a position
    /// in the recording.
    /// </summary>
    /// <remarks>
    /// It is here rather than in a binding because the answer needs a width. A press arrives as an
    /// x inside a control, and only the control knows how wide it is; turning that into a time is
    /// arithmetic the view model cannot do and should not be handed the pixels for. So the strip
    /// reports a fraction and the view model turns it into a seek.
    ///
    /// Pointer capture is what makes the drag work: without it the pointer leaving the 18px strip
    /// mid-drag ends the gesture, which for a scrub along a bar is most of the time.
    /// </remarks>
    private void WireSeekStrip()
    {
        if (this.FindControl<Border>("SeekStrip") is not { } strip)
        {
            return;
        }

        strip.PointerPressed += (_, e) =>
        {
            _seeking = true;
            e.Pointer.Capture(strip);
            SeekTo(strip, e.GetPosition(strip).X);
        };

        strip.PointerMoved += (_, e) =>
        {
            if (_seeking)
            {
                SeekTo(strip, e.GetPosition(strip).X);
            }
        };

        strip.PointerReleased += (_, e) =>
        {
            _seeking = false;
            e.Pointer.Capture(null);
        };

        // A capture lost to something else — another window taking the pointer, the control going
        // away — has to end the drag too, or the next move over the strip resumes a scrub nobody
        // started.
        strip.PointerCaptureLost += (_, _) => _seeking = false;
    }

    private void SeekTo(Border strip, double x)
    {
        if (DataContext is not MainWindowViewModel viewModel || strip.Bounds.Width <= 0)
        {
            return;
        }

        viewModel.Ask.SeekToFraction(x / strip.Bounds.Width);
    }

    /// <summary>
    /// Enter in the find box steps to the next hit. Shift+Enter steps back, which is what every
    /// find bar does and what nobody thinks to look for a button for.
    /// </summary>
    /// <remarks>
    /// Marked handled, or the key press carries on to the default button and to whatever else in
    /// the window would answer for Enter.
    /// </remarks>
    private void WireSearchBox()
    {
        if (this.FindControl<TextBox>("SearchBox") is not { } box)
        {
            return;
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }

            var back = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var command = back ? viewModel.Ask.PreviousMatchCommand : viewModel.Ask.NextMatchCommand;

            if (command.CanExecute(null))
            {
                command.Execute(null);
            }

            e.Handled = true;
        };
    }

    /// <summary>
    /// Follows the search to the hit it is standing on, bringing that row into view.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scrolling is the one thing in this feature a view model cannot do: the row it wants exists
    /// only as a container the list has realised, and only the list knows about that. So the view
    /// model publishes an index and this turns it into a container.
    /// </para>
    /// <para>
    /// Posted rather than done here, because the index changes at the moment the term does, and the
    /// container for a row the search has just found may not have been created yet. By the time the
    /// post runs the layout pass has been through.
    /// </para>
    /// </remarks>
    private void OnAskChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewModels.AskViewModel.CurrentMatchLineIndex)
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var index = viewModel.Ask.CurrentMatchLineIndex;

        if (index < 0 || this.FindControl<ItemsControl>("Cues") is not { } cues)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => (cues.ContainerFromIndex(index) as Control)?.BringIntoView(),
            DispatcherPriority.Background);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        // Both halves, because a window whose data context is replaced must not keep answering
        // property changes from the one it no longer shows.
        if (_watching is not null)
        {
            _watching.PropertyChanged -= OnAskChanged;
            _watching = null;
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            _watching = viewModel.Ask;
            _watching.PropertyChanged += OnAskChanged;
        }

        base.OnDataContextChanged(e);
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
            Services.WindowFrame.MakeSquare(handle.Handle);

            // And the shadow, which is the other half of "where does this window end". Removing
            // the title bar removed the whole frame, and a frameless window on Windows casts no
            // shadow — so against a light desktop the application had no visible edge at all.
            Services.WindowFrame.GiveShadow(handle.Handle);
        }

        // Running for as long as the window is open, rather than only while the Ask tab is
        // showing: a recording keeps playing when somebody switches to Transcribe, and a transport
        // whose clock stops while the sound carries on is worse than no clock. A tick that finds
        // nothing moved raises nothing, so the cost of it on the other four tabs is a comparison.
        _transport.Start();

        if (DataContext is MainWindowViewModel viewModel)
        {
            _ = viewModel.Updates.CheckOnLaunchAsync();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _transport.Stop();
        base.OnClosed(e);
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
