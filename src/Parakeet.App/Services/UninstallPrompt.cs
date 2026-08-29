using System.Runtime.InteropServices;
using Parakeet.Core.Models;

namespace Parakeet.App.Services;

/// <summary>What the person uninstalling said about the downloaded files.</summary>
public enum UninstallChoice
{
    /// <summary>Nothing was asked, because there was nothing worth asking about.</summary>
    NothingToAsk,

    /// <summary>Keep the downloads. The shipped default, and what a reinstall wants.</summary>
    Keep,

    /// <summary>Remove them.</summary>
    Delete,
}

/// <summary>
/// Asks, during an uninstall, whether to remove the downloaded models and the bundles beside them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why anything is asked at all.</b> This product's data lives outside the install root so an
/// update cannot destroy it and Velopack's recursive delete cannot reach it (gotcha 8). The price is
/// that nothing else ever removes it: an uninstall leaves tens of gigabytes behind with no
/// application left to say what they are. The Models tab now has a button for it, which only helps
/// somebody who opens the Models tab; this is for the person who goes straight to Installed apps.
/// </para>
/// <para>
/// <b>Why a Win32 message box and not the application's own window.</b> The hook runs from
/// <c>VelopackApp.Run()</c>, before Avalonia is started and on a build that is about to be deleted.
/// <c>MessageBoxW</c> is in <c>user32</c>, needs no framework brought up, and returns a value; an
/// Avalonia dialog here would mean starting a UI toolkit inside an uninstaller.
/// </para>
/// <para>
/// <b>Keep is the default button, deliberately.</b> Reinstalling is the common reason to be on this
/// screen, and somebody who dismisses the dialog with Escape or Enter should not lose a 25 GiB
/// download to a keystroke. The wording says so rather than relying on the button order.
/// </para>
/// <para>
/// <b>The whole thing is best effort and fails towards keeping.</b> Every failure here — no
/// interactive desktop, the call refused, an exception — returns <see cref="UninstallChoice.Keep"/>,
/// which is exactly the behaviour the product had before this existed. See the register entry for
/// why that matters: the callback this runs from was measured once returning in 98 ms having done
/// nothing, and was never explained.
/// </para>
/// </remarks>
public static class UninstallPrompt
{
    private const uint MbYesNo = 0x00000004;
    private const uint MbIconQuestion = 0x00000020;
    private const uint MbDefaultButton2 = 0x00000100;
    private const uint MbTopMost = 0x00040000;
    private const uint MbSetForeground = 0x00010000;

    private const int IdYes = 6;

    /// <summary>
    /// Below this, the question is not worth asking: a few megabytes is not what somebody is on
    /// the Installed apps screen worrying about, and a dialog nobody needed is its own defect.
    /// </summary>
    public const long AskAboveBytes = 64L * 1024 * 1024;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    /// <summary>Asks about the canonical data directory, and does nothing else.</summary>
    public static UninstallChoice Ask() => Ask(UserDataPaths.RootDirectory());

    /// <summary>Asks about <paramref name="userDataRoot"/>, returning what to do with it.</summary>
    public static UninstallChoice Ask(string userDataRoot)
    {
        long bytes;
        try
        {
            var directory = new DirectoryInfo(userDataRoot);
            if (!directory.Exists)
            {
                return UninstallChoice.NothingToAsk;
            }

            bytes = MeasureOrZero(directory);
        }
        catch
        {
            return UninstallChoice.NothingToAsk;
        }

        if (bytes <= AskAboveBytes)
        {
            return UninstallChoice.NothingToAsk;
        }

        try
        {
            var answer = MessageBoxW(
                0,
                Message(bytes, userDataRoot),
                "Uindosill: keep your downloaded models?",
                MbYesNo | MbIconQuestion | MbDefaultButton2 | MbTopMost | MbSetForeground);

            return answer == IdYes ? UninstallChoice.Delete : UninstallChoice.Keep;
        }
        catch (Exception exception) when (exception is DllNotFoundException
                                              or EntryPointNotFoundException
                                              or MarshalDirectiveException)
        {
            // No interactive desktop, or user32 unavailable. Keeping is the old behaviour.
            return UninstallChoice.Keep;
        }
    }

    /// <summary>
    /// The question. Written so the safe answer is the obvious one.
    /// </summary>
    /// <remarks>
    /// It leads with what keeping buys rather than with the space deleting frees, because the
    /// expensive mistake here is one-directional: keeping costs disk that a later visit can
    /// reclaim, and deleting costs a download measured in tens of gigabytes.
    /// </remarks>
    internal static string Message(long bytes, string userDataRoot) =>
        $"Uindosill is being removed. Its downloaded models and runtimes are kept somewhere else, "
        + $"so they are still on this computer: {ByteSize.Describe(bytes)} in {userDataRoot}.\n\n"
        + "If you are reinstalling, or trying a different version, choose No. The files are reused "
        + "as they are and you will not download them again.\n\n"
        + "Choose Yes only if you are done with Uindosill and want the space back.\n\n"
        + "Delete them?";

    private static long MeasureOrZero(DirectoryInfo directory)
    {
        try
        {
            // Links are not followed: a redirected models directory is somebody's own arrangement
            // and its size is not this dialog's to quote, nor its contents this hook's to delete.
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return 0;
            }

            var total = 0L;
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    total += file.Length;
                }
                catch (IOException)
                {
                }
            }

            return total;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
