using Avalonia;
using Parakeet.App.Services;
using Velopack;

namespace Parakeet.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // First, before Avalonia and before anything else in this method.
        //
        // Velopack re-runs this same executable to perform install, update and uninstall steps,
        // passing them as command-line arguments. Run() recognises those, does the work and exits
        // the process. Anything above it runs in every one of those short-lived invocations, and
        // for a GUI application that means a window flashing up during an install — or worse, an
        // engine load in a process that was only ever meant to move some files.
        //
        // SetAutoApplyOnStartup(false) is not the default: Velopack's own default is ON, which
        // applies an already-downloaded update during startup. This product's decision is that
        // nothing installs itself (docs/PHASES.md, Decisions taken 2026-08-16, item 4) — the
        // download and the restart both happen on a click, and never otherwise.
        // NOTHING IS REGISTERED ON THE UNINSTALL HOOK, AND THAT IS THE DECISION.
        //
        // Between 2026-08-23 and the next morning this application deleted %LOCALAPPDATA%\Uindosill
        // on uninstall. That was removed for three reasons, any one of which is sufficient, and
        // docs/PHASES.md records the whole of it:
        //
        //   · The folder is one people keep their own files in. The Models tab offers to remove
        //     "files put here by hand", so the product knows they are there — and an uninstaller
        //     cannot ask anybody anything.
        //   · Uninstall-then-reinstall is the first thing people try when something is wrong, and
        //     this made that cost a 3.9 GB re-download, silently.
        //   · It did not work reliably, and nobody could find out why: it deleted 4.64 GB in one
        //     run and did nothing in another, on the same machine and the same build.
        //
        // Unattended, unconfirmable deletion of somebody's disk is not a feature this product is
        // able to get right, so it does not have one. What replaces it is the Models tab, where a
        // person can see what is on their disk and remove it deliberately, before uninstalling.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .Run();

        // Only an installed copy has anything to update, and only this entry point knows that this
        // is one. Everything else — the designer, the headless test host — keeps the default
        // updater, which reports "not installed" and never reaches the network.
        App.Updater = new VelopackUpdater();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Used by the visual designer and by the headless test host.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
