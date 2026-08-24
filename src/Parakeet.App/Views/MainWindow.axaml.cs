using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Parakeet.App.Services;
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

    /// <summary>
    /// The smallest the recording's row is allowed to be dragged to. The splitter reads the row's
    /// MinHeight, so this is the floor the drag stops at as well as the floor the layout enforces
    /// — and it is set from here rather than left in the XAML because the row's minimum has to be
    /// lifted and dropped as the picture comes and goes. The transport shares the row since
    /// 2026-08-23, so the number covers its height as well as a picture worth keeping: it was 120
    /// when the row held the picture alone.
    /// </summary>
    private const double PictureFloor = 210;

    private readonly DispatcherTimer _transport;

    private bool _shutdownRequested;
    private bool _seeking;

    /// <summary>The line the playhead was last inside, so <see cref="Follow"/> can ask where
    /// the reader was looking when it moved. -1 when nothing has been played.</summary>
    private int _played = -1;

    /// <summary>The Ask view model this window is currently listening to, so it can stop.</summary>
    private ViewModels.AskViewModel? _watching;

    /// <summary>The player whose frames the video surface is copying, held so it can be unhooked.</summary>
    private IMediaPlayer? _watchedPlayer;

    /// <summary>The bitmap the video surface draws, recreated when the frame size changes.</summary>
    private WriteableBitmap? _videoBitmap;

    /// <summary>Whether a blit is already posted, so a burst of frames coalesces into one.</summary>
    private int _blitPending;

    /// <summary>
    /// The height the picture row had when it was last shown, so a reader who sizes the picture,
    /// opens an audio file and comes back finds the size they chose rather than the default.
    /// Seeded from the XAML, so the number lives in exactly one place.
    /// </summary>
    private GridLength _pictureHeight;

    /// <summary>The seek bar's pointer strip and its handle, held so the clock does not look them
    /// up by name ten times a second.</summary>
    private Border? _seekStrip;

    /// <inheritdoc cref="_seekStrip" />
    private Border? _seekPuck;

    /// <summary>
    /// Whether the picture row is currently shown, or null before the question has been asked.
    /// </summary>
    /// <remarks>
    /// The reason <see cref="ShowPictureRow"/> is not simply an assignment. AskViewModel.Redraw
    /// raises <c>HasVideo</c> on every tick that moved the clock — ten times a second while a
    /// recording plays — and it says the same thing every time. Acting on each one wrote the row's
    /// height back ten times a second, which is invisible while paused and, mid-drag, stamps the
    /// splitter back to where it was before the gesture started.
    /// </remarks>
    private bool? _pictureRowShown;

    /// <summary>Whether the reader has hold of the splitter right now.</summary>
    private bool _resizingPicture;

    public MainWindow()
    {
        InitializeComponent();

        var dropZone = this.FindControl<Border>("DropZone");
        if (dropZone is not null)
        {
            DragDrop.AddDragOverHandler(dropZone, OnDragOver);
            DragDrop.AddDropHandler(dropZone, OnDrop);
        }

        // Both of these are on tabs that are not the one the window opens on — Browse moved to
        // Export on 2026-08-23 and About arrived on Settings the same day — and both are still
        // found from here. A TabControl defers only the *drawing* of an unselected page; the
        // markup tree is built at load and every Name in it goes into the window's one name scope,
        // which is what FindControl reads. Measured rather than assumed, because the opposite is
        // the obvious guess and would have made both of these silently dead. Gotcha 31.
        var browse = this.FindControl<Button>("BrowseOutput");
        if (browse is not null)
        {
            browse.Click += OnBrowseOutput;
        }

        var about = this.FindControl<Button>("ShowAbout");
        if (about is not null)
        {
            about.Click += OnShowAbout;
        }

        WireHeaderBar();
        WireSeekStrip();
        WireSearchBox();
        WireVideoPane();
        WireMediaSplitter();
        WireVoices();
        WireRecordingsDrawer();
        FollowTranscript();
        WireAskChat();

        // The clock the Ask tab draws from, and it is here rather than in the view model on
        // purpose: a view model that starts a dispatcher timer needs a dispatcher to exist before
        // it is constructed, which is a requirement no test should have to satisfy. The view model
        // exposes a Tick it does nothing in unless something moved, and this is what calls it.
        _transport = new DispatcherTimer(DispatcherPriority.Background) { Interval = TransportRefresh };
        _transport.Tick += (_, _) => (DataContext as MainWindowViewModel)?.Ask.Tick();
    }

    /// <summary>
    /// The chat panel's window-level jobs: Enter asks, Escape stops the ask under way, the
    /// clipboard answers the Copy button, and a new exchange scrolls into view.
    /// </summary>
    /// <remarks>
    /// The clipboard is here because only a TopLevel has one — the view model builds the copied
    /// text and borrows the writing through a delegate, which is also what a headless test
    /// replaces to see what would have been copied. It is handed over on DataContextChanged
    /// rather than in this constructor, because the DataContext arrives after construction.
    /// </remarks>
    private void WireAskChat()
    {
        if (this.FindControl<TextBox>("AskInput") is { } input)
        {
            input.KeyDown += (_, e) =>
            {
                if (DataContext is not MainWindowViewModel viewModel)
                {
                    return;
                }

                if (e.Key == Key.Enter)
                {
                    if (viewModel.Ask.Chat.AskCommand.CanExecute(null))
                    {
                        viewModel.Ask.Chat.AskCommand.Execute(null);
                    }

                    e.Handled = true;
                }
                else if (e.Key == Key.Escape && viewModel.Ask.Chat.StopCommand.CanExecute(null))
                {
                    viewModel.Ask.Chat.StopCommand.Execute(null);
                    e.Handled = true;
                }
            };
        }

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }

            viewModel.Ask.Chat.CopyToClipboard ??= new Func<string, Task>(text =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
                {
                    return Task.CompletedTask;
                }

                var transfer = new DataTransfer();
                transfer.Add(DataTransferItem.Create(DataFormat.Text, text));
                return clipboard.SetDataAsync(transfer);
            });

            // A question lands at the bottom of a conversation that may already fill the pane.
            // On the collection change the new row is not laid out yet, so the scroll is posted
            // behind the layout pass, the same arrangement the transcript follow uses.
            viewModel.Ask.Chat.Entries.CollectionChanged += (_, _) =>
            {
                if (this.FindControl<ScrollViewer>("AskChatScroll") is { } scroll)
                {
                    Dispatcher.UIThread.Post(scroll.ScrollToEnd, DispatcherPriority.Background);
                }
            };
        };
    }

    /// <summary>
    /// Keeps the Transcribe tab's transcript scrolled to its end while a batch is filling it, so
    /// the words being decoded are the words on screen.
    /// </summary>
    /// <remarks>
    /// Only while the content is growing and only while the reader was already at the end: an
    /// extent change with the view anywhere else means they scrolled up to read something, and
    /// yanking the view out from under a reader is worse than the stale tail. The stick flag is
    /// recomputed from every offset change, so scrolling back down to the end re-arms it and
    /// scrolling up disarms it — no button, the scrollbar itself is the control. Gated on the
    /// batch actually running, because the extent also changes when a finished row is selected,
    /// and opening an old transcript at its end would lose the reader its beginning.
    /// </remarks>
    private void FollowTranscript()
    {
        if (this.FindControl<ScrollViewer>("TranscriptScroll") is not { } scroll)
        {
            return;
        }

        var stick = true;
        scroll.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y != 0)
            {
                if (stick && (DataContext as MainWindowViewModel)?.Transcribe.IsRunning == true)
                {
                    scroll.ScrollToEnd();
                }

                return;
            }

            // An offset change with a stable extent is the reader (or the ScrollToEnd above, which
            // lands at the end and therefore re-arms). Within one line-height of the end counts as
            // at it, so a rounding pixel cannot silently disarm the follow.
            stick = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 21;
        };
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

        // Held rather than looked up again. PlacePuck runs on every clock tick — ten times a second
        // while a recording plays — and a name lookup per call, twice, is work for nothing.
        _seekStrip = strip;
        _seekPuck = this.FindControl<Border>("SeekPuck");

        // A Border takes no focus by default, so before 2026-08-23 no key could ever reach the
        // bar and arrow-key seeking simply did not exist — the cost recorded when the strip was
        // chosen over a Slider. Focusable plus a Focus() on press gives the keys somewhere to
        // land, which is the half of a Slider's behaviour that choice had dropped.
        strip.Focusable = true;

        strip.PointerPressed += (_, e) =>
        {
            _seeking = true;
            strip.Focus();
            e.Pointer.Capture(strip);
            SeekTo(strip, e.GetPosition(strip).X);
        };

        strip.KeyDown += (_, e) =>
        {
            if (DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }

            // Five seconds an arrow, thirty with Shift: a phrase and a paragraph. Home and End
            // are the two places a fraction already names.
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            switch (e.Key)
            {
                case Key.Left:
                    viewModel.Ask.SeekBy(shift ? -30 : -5);
                    break;
                case Key.Right:
                    viewModel.Ask.SeekBy(shift ? 30 : 5);
                    break;
                case Key.Home:
                    viewModel.Ask.SeekToFraction(0);
                    break;
                case Key.End:
                    viewModel.Ask.SeekToFraction(1);
                    break;
                default:
                    return;
            }

            e.Handled = true;
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

        // The handle moves with the bar as well as with the clock. Without this a window resized
        // mid-recording leaves the puck at the pixel it was at, which is now a different time.
        strip.SizeChanged += (_, _) => PlacePuck();

        PlacePuck();
    }

    /// <summary>
    /// Puts the seek handle where the playhead is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inverse of <see cref="SeekTo"/>, and deliberately the same arithmetic: that turns an x
    /// on the strip into a fraction of the recording, and this turns a fraction of the recording
    /// back into an x. Both read the width off the strip, because it is the only thing that knows
    /// one — which is why this is here rather than in a binding.
    /// </para>
    /// <para>
    /// The travel is the strip's width less the puck's own, and the offset is a left margin on a
    /// left-aligned control. Centring it on the fraction instead would hang half the handle past
    /// each end of the bar, so a recording at its start and a recording at its end would both draw
    /// a puck sticking out of the track.
    /// </para>
    /// </remarks>
    private void PlacePuck()
    {
        if (DataContext is not MainWindowViewModel viewModel
            || _seekStrip is not { } strip
            || _seekPuck is not { } puck)
        {
            return;
        }

        var travel = strip.Bounds.Width - puck.Width;

        if (travel <= 0 || double.IsNaN(puck.Width))
        {
            return;
        }

        var duration = viewModel.Ask.DurationSeconds;
        var fraction = duration > 0
            ? Math.Clamp(viewModel.Ask.PositionSeconds / duration, 0, 1)
            : 0;

        puck.Margin = new Thickness(fraction * travel, 0, 0, 0);
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
    /// Follows the search to the hit it is standing on, and the playhead to the line it is inside,
    /// bringing that row into view.
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
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (e.PropertyName == nameof(ViewModels.AskViewModel.CurrentMatchLineIndex))
        {
            Scroll(viewModel.Ask.CurrentMatchLineIndex);
        }
        else if (e.PropertyName == nameof(ViewModels.AskViewModel.ActiveLineIndex))
        {
            Follow(viewModel.Ask.ActiveLineIndex);
        }
        else if (e.PropertyName == nameof(ViewModels.AskViewModel.HasVideo))
        {
            ShowPictureRow(viewModel.Ask.HasVideo);
        }
        else if (e.PropertyName == nameof(ViewModels.AskViewModel.PositionSeconds))
        {
            // Raised by AskViewModel.Redraw, which is the one place the clock is read — so the
            // handle moves on a tick, on a seek and on a change of recording, and on nothing else.
            PlacePuck();
        }
    }

    /// <summary>
    /// Keeps the line being played in view — but only while the reader is still watching the
    /// played part of the transcript.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule is: follow while the line the playhead has just left is still on screen.</b> A
    /// reader who has scrolled somewhere else is reading, and taking the page back off them every
    /// few seconds would be this window arguing with them; a reader watching the played line is
    /// watching it, and gets the next one brought to them. Nothing has to be reset, because
    /// following resumes on its own the moment the played line is in view again — by scrolling
    /// back to it, or by clicking a cue, which seeks there and is a request to be there.
    /// </para>
    /// <para>
    /// The check is made before the scroll is posted rather than inside it, because by the time a
    /// posted call runs the offset may already have moved and the question — where was the reader
    /// looking when the line changed — can no longer be asked.
    /// </para>
    /// </remarks>
    private void Follow(int index)
    {
        var left = _played;
        _played = index;

        if (index < 0
            || this.FindControl<ItemsControl>("Cues") is not { } cues
            || this.FindControl<ScrollViewer>("CueScroll") is not { } scroller)
        {
            return;
        }

        // Nothing was playing, so there is no reader to argue with: a recording that starts, or
        // one that has just been chosen, brings its first line into view.
        if (left >= 0
            && cues.ContainerFromIndex(left) is Control previous
            && !IsInView(scroller, previous))
        {
            return;
        }

        Scroll(index);
    }

    /// <summary>Brings the row at <paramref name="index"/> into view, once layout has been through.</summary>
    private void Scroll(int index)
    {
        if (index < 0 || this.FindControl<ItemsControl>("Cues") is not { } cues)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => (cues.ContainerFromIndex(index) as Control)?.BringIntoView(),
            DispatcherPriority.Background);
    }

    /// <summary>Whether any part of <paramref name="control"/> is inside <paramref name="scroller"/>.</summary>
    private static bool IsInView(ScrollViewer scroller, Control control) =>
        control.TranslatePoint(default, scroller) is { } top
        && top.Y + control.Bounds.Height > 0
        && top.Y < scroller.Bounds.Height;

    protected override void OnDataContextChanged(EventArgs e)
    {
        // Both halves, because a window whose data context is replaced must not keep answering
        // property changes — or copying frames — from the one it no longer shows.
        if (_watching is not null)
        {
            _watching.PropertyChanged -= OnAskChanged;
            _watching = null;

            // And the line it was following, which indexes a transcript this window no longer
            // shows: kept, it would be compared against a row belonging to something else.
            _played = -1;
        }

        if (_watchedPlayer is not null)
        {
            _watchedPlayer.FrameReady -= OnFrameReady;
            _watchedPlayer = null;
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            _watching = viewModel.Ask;
            _watching.PropertyChanged += OnAskChanged;

            _watchedPlayer = viewModel.Ask.Player;
            _watchedPlayer.FrameReady += OnFrameReady;
        }

        // The state nothing raises. A window whose first recording already has a picture has to
        // open with the row for one, and OnAskChanged only ever hears about a change.
        ShowPictureRow(DataContext is MainWindowViewModel current && current.Ask.HasVideo);

        base.OnDataContextChanged(e);
    }

    /// <summary>
    /// Tells the player how large the pane it is filling actually is, in device pixels, so frames
    /// are rendered at the size they will be shown rather than at the file's own.
    /// </summary>
    /// <remarks>
    /// From here rather than from the view model because only a laid-out control knows its size —
    /// the same reason the seek strip's fraction is computed here. The scaling multiplier matters:
    /// on a 250% display a 500-unit pane is 1250 device pixels, and frames rendered at 500 would
    /// be upscaled back by the compositor, soft.
    /// </remarks>
    private void WireVideoPane()
    {
        if (this.FindControl<Border>("VideoPane") is not { } pane)
        {
            return;
        }

        // Not while the splitter is being dragged. Every layout pass of a drag is a new size, and
        // telling the player about each one makes it reallocate its render target sixty times a
        // second underneath a recording that is playing. The size is published once, on release.
        pane.SizeChanged += (_, _) =>
        {
            if (!_resizingPicture)
            {
                PublishVideoSize();
            }
        };
    }

    /// <summary>Tells the player how large the pane it is filling actually is, in device pixels.</summary>
    private void PublishVideoSize()
    {
        if (this.FindControl<Border>("VideoPane") is not { } pane || pane.Bounds.Height <= 0)
        {
            return;
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

        _watchedPlayer?.SetVideoOutputSize(
            (int)(pane.Bounds.Width * scaling),
            (int)(pane.Bounds.Height * scaling));
    }

    /// <summary>
    /// Enter commits a speaker's new name, by taking the focus off the field that holds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name is bound on LostFocus rather than on every keystroke, because the binding reaches
    /// every cue of that speaker and redrawing a transcript per character typed is work for
    /// nothing. The cost of that choice is that a field only commits when you click somewhere
    /// else, which is a field people believe did not work — so Enter does the clicking-away.
    /// </para>
    /// <para>
    /// Handled on the strip rather than on each field: the boxes are made by a template, so there
    /// is no per-box place to hook, and the key press bubbles here anyway. Marked handled, or it
    /// carries on to whatever else in this window answers for Enter.
    /// </para>
    /// </remarks>
    private void WireVoices()
    {
        if (this.FindControl<ItemsControl>("Voices") is not { } strip)
        {
            return;
        }

        // An ItemsControl does not take focus by default, and a Focus() that fails is a rename that
        // silently does not commit — the exact shape of defect this window keeps finding.
        strip.Focusable = true;

        strip.AddHandler(
            KeyDownEvent,
            (object? _, KeyEventArgs e) =>
            {
                if (e.Key != Key.Enter || e.Source is not TextBox)
                {
                    return;
                }

                // Focus goes to the strip rather than nowhere. Clearing it outright leaves the
                // window with no focused element, and the next Tab starts over from the top.
                strip.Focus();
                e.Handled = true;
            },
            RoutingStrategies.Bubble);
    }

    /// <summary>
    /// A press outside the recordings drawer closes it. The scrim is the whole page behind the
    /// drawer; it exists only while the drawer is open, and it is a plain Border because a surface
    /// whose one job is to catch a press has no command to bind.
    /// </summary>
    private void WireRecordingsDrawer()
    {
        if (this.FindControl<Border>("DrawerScrim") is not { } scrim)
        {
            return;
        }

        scrim.PointerPressed += (_, e) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Ask.IsRecordingsDrawerOpen = false;
                e.Handled = true;
            }
        };
    }

    /// <summary>Remembers whatever height the reader drags the picture to.</summary>
    /// <remarks>
    /// The recording's row and the reading's are both stars — the tab opens split evenly — and the
    /// splitter writes star lengths back into both, keeping the ratio it was dragged to. So the
    /// row's height is always a star, and reading it back when the drag finishes is the whole of
    /// remembering what was chosen: restored against a different window height it is the same
    /// proportion rather than the same pixel count, which is the better memory anyway.
    /// </remarks>
    private void WireMediaSplitter()
    {
        if (this.FindControl<Grid>("MediaColumn") is not { } column
            || this.FindControl<GridSplitter>("MediaSplitter") is not { } splitter)
        {
            return;
        }

        _pictureHeight = column.RowDefinitions[0].Height;

        splitter.DragStarted += (_, _) => _resizingPicture = true;

        splitter.DragCompleted += (_, _) =>
        {
            _resizingPicture = false;
            _pictureHeight = column.RowDefinitions[0].Height;

            // The one size that matters is the one they let go on. See WireVideoPane.
            PublishVideoSize();
        };
    }

    /// <summary>Gives the picture a row of its own, or takes the row away entirely.</summary>
    /// <remarks>
    /// <para>
    /// Not a binding, and not from any dislike of bindings: a <see cref="RowDefinition"/> derives
    /// from AvaloniaObject rather than StyledElement, so it has no DataContext to bind against —
    /// and a binding that did resolve would be overwritten by the splitter's own local value on the
    /// first drag, then silently overwrite it back on the next notification. One assignment on a
    /// property change is the version that cannot fight itself.
    /// </para>
    /// <para>
    /// Auto rather than zero when there is no picture, so the row measures what is left in it —
    /// the transport, over a collapsed pane — and comes out at exactly that. No black band, and
    /// with the handle hidden beside it, nothing to drag one back out by.
    /// </para>
    /// </remarks>
    private void ShowPictureRow(bool hasVideo)
    {
        if (this.FindControl<Grid>("MediaColumn") is not { } column)
        {
            return;
        }


        // Asked far more often than it changes, and the answer is a write. See _pictureRowShown.
        if (_pictureRowShown == hasVideo)
        {
            return;
        }

        _pictureRowShown = hasVideo;

        var row = column.RowDefinitions[0];

        row.Height = hasVideo ? _pictureHeight : GridLength.Auto;
        row.MinHeight = hasVideo ? PictureFloor : 0;
    }

    /// <summary>
    /// The player's frame announcement, on the decoder's thread. One blit is posted and further
    /// announcements coalesce into it: the UI thread draws the newest frame there is, and a burst
    /// of frames during a seek becomes one paint rather than a queue of stale ones.
    /// </summary>
    private void OnFrameReady()
    {
        if (Interlocked.Exchange(ref _blitPending, 1) == 0)
        {
            Dispatcher.UIThread.Post(BlitFrame, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// Copies the newest frame into the surface's bitmap and invalidates it. UI thread only.
    /// </summary>
    private void BlitFrame()
    {
        Interlocked.Exchange(ref _blitPending, 0);

        if (_watchedPlayer is not { } player || this.FindControl<Image>("VideoSurface") is not { } surface)
        {
            return;
        }

        var (width, height) = player.FrameSize;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_videoBitmap is null
            || _videoBitmap.PixelSize.Width != width
            || _videoBitmap.PixelSize.Height != height)
        {
            // Bgra8888 opaque, which is exactly the layout TryCopyFrame writes — see the alpha
            // note there. 96 DPI because the bitmap is stretched to the pane by the Image, so its
            // own DPI never decides anything.
            _videoBitmap = new WriteableBitmap(
                new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Opaque);
        }

        using (var framebuffer = _videoBitmap.Lock())
        {
            if (!player.TryCopyFrame(framebuffer.Address, framebuffer.RowBytes, width, height))
            {
                // The frame changed size between the announcement and this copy; the next
                // announcement finds the bitmap re-created at the new size.
                return;
            }
        }

        if (!ReferenceEquals(surface.Source, _videoBitmap))
        {
            surface.Source = _videoBitmap;
        }

        surface.InvalidateVisual();
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
        // rounded corner it would have had anyway. See Services/WindowFrame.cs.
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

    /// <summary>
    /// Opens the About window: which build this is, the notice package, and the machine facts.
    /// </summary>
    /// <remarks>
    /// Modal and owned, which is what keeps there from being a second copy of it: while the dialog
    /// is up this window takes no clicks, so the button cannot be pressed again. Not awaited —
    /// there is nothing to do afterwards, and the window closes itself.
    /// </remarks>
    private void OnShowAbout(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        _ = new AboutWindow { DataContext = viewModel.About }.ShowDialog(this);
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
