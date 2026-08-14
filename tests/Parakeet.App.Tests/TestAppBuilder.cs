using Avalonia;
using Avalonia.Headless;
using Parakeet.App;

[assembly: AvaloniaTestApplication(typeof(Parakeet.App.Tests.TestAppBuilder))]

namespace Parakeet.App.Tests;

/// <summary>
/// Boots the real application in Avalonia's headless platform, so the window, its bindings and
/// its view models are exercised in CI on Linux with no display and no model weights.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
