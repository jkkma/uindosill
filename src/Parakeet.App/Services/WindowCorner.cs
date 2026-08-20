using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Parakeet.App.Services;

/// <summary>
/// Asks Windows not to round the window's corners.
/// </summary>
/// <remarks>
/// <para>
/// The design specifies a square window. On Windows 11 that is not something a toolkit can simply
/// draw: the desktop compositor rounds top-level windows on its own terms, outside the client
/// area, and extending the client area to the decorations does not hand the corner over.
/// </para>
/// <para>
/// What makes square reachable is that DWM takes a corner <em>preference</em> rather than a
/// radius, and one of its values is do-not-round. The earlier 12&#160;px version of this design was
/// unreachable for the mirror-image reason — an arbitrary radius is not on the menu, so it would
/// have needed a borderless window painting its own shadow and backdrop, with the snap-layout and
/// per-monitor-DPI consequences that brings. Going square made the frame cheaper to build rather
/// than dearer, which is the opposite of how it looked before anyone checked.
/// </para>
/// <para>
/// Best effort by construction, and that is deliberate. This is decoration: a machine that refuses
/// the call, or predates the attribute, gets the rounded corner it would have had anyway. Nothing
/// downstream reads the result, so a failure here must never reach the person trying to transcribe
/// something — hence a bool return that the caller is free to ignore, and no exception path.
/// </para>
/// </remarks>
internal static class WindowCorner
{
    // DWMWA_WINDOW_CORNER_PREFERENCE. Introduced in Windows 11 (build 22000); on Windows 10 the
    // attribute is unknown and DwmSetWindowAttribute returns E_INVALIDARG, which is why the return
    // value is checked rather than the OS version. Asking and being refused is cheaper than
    // deciding in advance whether to ask, and it does not go stale as builds move.
    private const int WindowCornerPreference = 33;

    // DWMWCP_DONOTROUND. The other values are DEFAULT (0), ROUND (2) and ROUNDSMALL (3).
    private const int DoNotRound = 1;

    [SupportedOSPlatform("windows")]
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Squares off the corners of the window with the given handle.
    /// </summary>
    /// <returns>
    /// True when DWM accepted the preference. False on any other platform, on a Windows build that
    /// does not know the attribute, and if dwmapi.dll cannot be reached at all.
    /// </returns>
    internal static bool MakeSquare(nint handle)
    {
        if (!OperatingSystem.IsWindows() || handle == 0)
        {
            return false;
        }

        try
        {
            var preference = DoNotRound;
            var result = DwmSetWindowAttribute(
                handle, WindowCornerPreference, ref preference, sizeof(int));

            // S_OK. Anything else — E_INVALIDARG on Windows 10, most likely — leaves the compositor
            // doing what it was going to do.
            return result == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
