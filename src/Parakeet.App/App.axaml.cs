using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;

namespace Parakeet.App;

public partial class App : Application
{
    /// <summary>
    /// Engine source, replaceable so headless tests drive the whole window without weights.
    /// </summary>
    /// <remarks>
    /// <b>The third argument is what makes the Models tab's diariser choice reach a run.</b>
    /// Without it the provider takes its no-argument default of "nobody has chosen" and resolves
    /// the first installed diariser instead, which is Sortformer on every machine that has it --
    /// so the tab would write the setting, tick the row, and change nothing. It is a delegate
    /// rather than a value because the choice can change while the window is open, and it reads
    /// the store each time for the same reason.
    /// </remarks>
    public static IEngineProvider EngineProvider { get; set; } =
        new EngineProvider(null, null, () => new AppSettingsStore().Load().DiarisationModelId);

    /// <summary>
    /// Update source, replaceable for the same reason. The default reaches no network at all, so a
    /// designer session or a headless test cannot make an HTTPS request by merely opening the
    /// window; <c>Program.Main</c> swaps in the real one.
    /// </summary>
    public static IAppUpdater Updater { get; set; } = new NotInstalledUpdater();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(EngineProvider, updater: Updater),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
