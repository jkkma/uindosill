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
        var velopack = VelopackApp.Build()
            .SetAutoApplyOnStartup(false);

        // Velopack's uninstall removes only its own install root; the models, settings and Python
        // bundle live apart from it precisely so that delete cannot reach them (docs/GOTCHAS.md
        // gotcha 8). This hook is therefore the one thing that removes them when the product goes
        // — deliberately, with UninstallCleanup's guards, inside the 30-second budget these fast
        // callbacks get. The OS guard is CA1416's, not a behaviour choice: the hook only ever
        // fires on Windows, where the installer exists.
        if (OperatingSystem.IsWindows())
        {
            velopack.OnBeforeUninstallFastCallback(_ => UninstallCleanup.Run());
        }

        velopack.Run();

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
