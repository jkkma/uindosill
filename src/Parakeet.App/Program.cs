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
        // THE UNINSTALL HOOK ASKS, AND THAT IS THE DECISION AS OF 2026-08-29.
        //
        // Between 2026-08-23 and the next morning this application deleted %LOCALAPPDATA%\Uindosill
        // on uninstall, unattended. That was removed for three reasons. Two of them were about the
        // deletion being silent, and asking answers both:
        //
        //   * The folder is one people keep their own files in, "and an uninstaller cannot ask
        //     anybody anything" - which was the assumption rather than a finding. It can:
        //     MessageBoxW is in user32, needs no UI toolkit brought up, and returns an answer.
        //   * Uninstall-then-reinstall is the first thing people try when something is wrong, and
        //     deleting made that cost a re-download, silently. The dialog leads with exactly that
        //     case and defaults to keeping.
        //
        // The third reason stands and is not answered: it did not work reliably and nobody found
        // out why. It deleted 4.64 GB in one run and did nothing in another, on the same machine
        // and the same build, and six causes were eliminated by experiment without the failure
        // reproducing. So this is built to fail towards the old behaviour at every step. No
        // interactive desktop, a refused call, an exception, a callback that never fires: each of
        // them leaves the downloads exactly where they are, which is what an uninstall did
        // yesterday. Nothing is deleted except on an explicit Yes.
        //
        // UninstallCleanup keeps the guards it was written with - the directory must carry the
        // expected name, must not contain the install root, a link is unlinked rather than
        // followed, and a file that will not delete strands only itself.
        var velopack = VelopackApp.Build().SetAutoApplyOnStartup(false);

        if (OperatingSystem.IsWindows())
        {
            velopack.OnBeforeUninstallFastCallback(_ =>
            {
                if (UninstallPrompt.Ask() == UninstallChoice.Delete)
                {
                    UninstallCleanup.Run();
                }
            });
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
