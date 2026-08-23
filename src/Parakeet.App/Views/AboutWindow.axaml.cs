using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Parakeet.App.ViewModels;

namespace Parakeet.App.Views;

/// <summary>
/// The About window: which build this is, the notice package, and the machine it is running on.
/// </summary>
/// <remarks>
/// Opened modally from the Settings tab — see <see cref="MainWindow"/> — and owned by the window
/// that opened it, so it cannot be lost behind it and there is never a second copy.
/// </remarks>
public partial class AboutWindow : Window
{
    /// <summary>
    /// Wires the three buttons, in the same shape and for the same reason the main window's
    /// constructor does: by name, and null-tolerant, so that renaming a control in the markup fails
    /// a test rather than throwing inside a constructor.
    /// </summary>
    /// <remarks>
    /// <c>CopySystemReport</c> is on the third pane and the window opens on the first, which
    /// does not matter — the markup tree is built at load and every Name in it is registered,
    /// whether or not the page it is on is being drawn. Same fact as
    /// <see cref="MainWindow"/>'s Browse and About buttons. Gotcha 31.
    /// </remarks>
    public AboutWindow()
    {
        InitializeComponent();

        foreach (var name in new[] { "WindowClose", "Dismiss" })
        {
            if (this.FindControl<Button>(name) is { } exit)
            {
                exit.Click += OnClose;
            }
        }

        if (this.FindControl<Button>("CopySystemReport") is { } copy)
        {
            copy.Click += OnCopySystemReport;
        }
    }

    /// <summary>
    /// Two of the three ways out: the glyph in the corner and the button at the foot. The third is
    /// Escape, which the platform routes to the <c>IsCancel</c> button in the markup without
    /// passing through here.
    /// </summary>
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Puts the System pane's five lines on the clipboard, which is what that pane is for.
    /// </summary>
    /// <remarks>
    /// The text comes from <see cref="AboutViewModel.SystemReport"/> rather than from the controls,
    /// so what is copied is built from the same properties the pane draws and in the same order.
    /// Failure is swallowed and answered by simply not confirming: a clipboard this process is not
    /// allowed to open — another application holding it is the ordinary case on Windows — must not
    /// take the About window down with it.
    /// </remarks>
    private async void OnCopySystemReport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AboutViewModel about || Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            // Avalonia 12 took SetTextAsync off IClipboard: a clipboard entry is now a
            // DataTransfer carrying one item per format. Not disposed here, and that is the
            // documented contract rather than an oversight — the clipboard owns what it is given
            // and disposes it when it becomes unused, so a `using` on this would pull the text
            // out from under the next paste.
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(about.SystemReport));

            await clipboard.SetDataAsync(transfer).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A clipboard another process is holding must not close this window.
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        // Said rather than assumed. A copy button with no acknowledgement is one people press
        // twice, and the second press is the one that makes them wonder whether either worked.
        if (this.FindControl<TextBlock>("CopyNotice") is { } notice)
        {
            notice.IsVisible = true;
        }
    }

    /// <summary>
    /// The same square corner and shadow the main window asks DWM for, for the same reason: the
    /// toolkit cannot draw either, and a rounded dialog beside a square window reads as a bug.
    /// </summary>
    /// <remarks>
    /// Nothing reads the answer. This is decoration, and a machine that refuses gets the frame it
    /// would have had — see <see cref="Services.WindowFrame"/>.
    /// </remarks>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (TryGetPlatformHandle() is { } handle)
        {
            Services.WindowFrame.MakeSquare(handle.Handle);
            Services.WindowFrame.GiveShadow(handle.Handle);
        }
    }
}
