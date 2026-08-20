using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Parakeet.App.Services;

/// <summary>
/// The two things the window's frame has to ask Windows for, because a toolkit cannot draw either
/// of them: a square corner, and a shadow.
/// </summary>
/// <remarks>
/// <para>
/// Both live outside the client area, in the desktop compositor, which is why neither shows up in
/// a headless render and why both had to be checked on a real screen.
/// </para>
/// <para>
/// Best effort by construction, and deliberately so. This is decoration: a machine that refuses
/// either call gets whatever frame it would have had, and nothing downstream reads the result. A
/// failure here must never reach the person trying to transcribe something.
/// </para>
/// </remarks>
internal static class WindowFrame
{
    // DWMWA_WINDOW_CORNER_PREFERENCE. Introduced in Windows 11 (build 22000); on Windows 10 the
    // attribute is unknown and DwmSetWindowAttribute returns E_INVALIDARG, which is why the return
    // value is checked rather than the OS version. Asking and being refused is cheaper than
    // deciding in advance whether to ask, and it does not go stale as builds move.
    private const int WindowCornerPreference = 33;

    // DWMWCP_DONOTROUND. The others are DEFAULT (0), ROUND (2) and ROUNDSMALL (3).
    private const int DoNotRound = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [SupportedOSPlatform("windows")]
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    /// <summary>
    /// Squares off the corners of the window with the given handle.
    /// </summary>
    /// <remarks>
    /// The design specifies a square window, and on Windows 11 that is not something a toolkit can
    /// simply draw: the compositor rounds top-level windows on its own terms, outside the client
    /// area. What makes square reachable is that DWM takes a corner <em>preference</em> rather than
    /// a radius, and one of its values is do-not-round. The earlier 12&#160;px version of this
    /// design was unreachable for the mirror-image reason — an arbitrary radius is not on the menu.
    /// Confirmed working on Windows 11: the preference reads back as set, and the window draws
    /// square.
    /// </remarks>
    /// <returns>True when DWM accepted the preference.</returns>
    internal static bool MakeSquare(nint handle)
    {
        if (!OperatingSystem.IsWindows() || handle == 0)
        {
            return false;
        }

        try
        {
            var preference = DoNotRound;
            return DwmSetWindowAttribute(handle, WindowCornerPreference, ref preference, sizeof(int)) == 0;
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

    /// <summary>
    /// Asks the compositor for the standard window shadow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A window with <c>WindowDecorations="None"</c> has no frame, and on Windows no frame means no
    /// shadow: the application ends wherever its own white pixels stop, which against a light
    /// desktop is nowhere the eye can find. That was the complaint this exists to answer — there
    /// was no delimiter showing where the app ended.
    /// </para>
    /// <para>
    /// Extending the frame one pixel into the client area is the documented way to get the shadow
    /// back on a custom-chrome window. The pixel itself is drawn over by the content; what it buys
    /// is the compositor treating this as a framed window again, which is what puts the drop shadow
    /// around it. The design's own four-layer shadow cannot be reproduced exactly — DWM draws the
    /// one it draws — so what ships is the platform's, and the 1px border inside the window is what
    /// carries the design's edge.
    /// </para>
    /// </remarks>
    /// <returns>True when DWM accepted the call.</returns>
    internal static bool GiveShadow(nint handle)
    {
        if (!OperatingSystem.IsWindows() || handle == 0)
        {
            return false;
        }

        try
        {
            var margins = new Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
            return DwmExtendFrameIntoClientArea(handle, ref margins) == 0;
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
