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
    public static IEngineProvider EngineProvider { get; set; } = new EngineProvider();

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
