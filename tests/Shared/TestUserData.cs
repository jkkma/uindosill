using System.Runtime.CompilerServices;

namespace Parakeet.Tests;

/// <summary>
/// Points every user-data path a test assembly can reach at a directory of its own, before a
/// single test runs.
/// </summary>
/// <remarks>
/// <para>
/// The suite is meant to be hermetic, and in one direction it was not: <c>MainWindowViewModel</c>
/// and <c>UpdatesViewModel</c> both turn a null settings store into <c>new AppSettingsStore()</c>,
/// which resolves <c>%LOCALAPPDATA%\Uindosill\settings.json</c> — the real one, belonging to
/// whoever ran the suite. Around thirty tests construct the window's view model without passing a
/// store, and each of them wrote that file: on construction, because an output directory that no
/// longer exists is cleared from the file as the view model starts, and again on any test that
/// chooses an output folder. A temporary directory's name was left in a maintainer's settings file
/// twice over, found on 2026-08-25.
/// </para>
/// <para>
/// A module initializer rather than a fixture, and unconditional rather than a default, because
/// the defect was one of omission. Passing an explicit store at each of those call sites fixes the
/// call sites that exist and none of the ones written next month; this runs first and takes the
/// choice away, so a test that forgets — or a product path that reaches for a default store where
/// no test could pass one — still lands under the temporary directory. It is compiled into every
/// test project by <c>tests/Directory.Build.props</c> for the same reason.
/// </para>
/// <para>
/// The variable names are spelled out here rather than read from the constants that declare them,
/// because this file is compiled into test assemblies that do not reference the assemblies those
/// constants live in. <c>UserDataIsolationTests</c> in <c>Parakeet.App.Tests</c> references both
/// and holds these two strings against <c>LocalModelStore.DirectoryEnvironmentVariable</c> and
/// <c>AppSettingsStore.PathEnvironmentVariable</c>, so the copies cannot drift apart unnoticed.
/// </para>
/// </remarks>
internal static class TestUserData
{
    /// <summary>The directory this run owns. Empty before the module initializer has run, which
    /// no test can observe.</summary>
    internal static string RootDirectory { get; private set; } = string.Empty;

    /// <summary>The models directory <c>LocalModelStore</c> resolves to for the whole run.</summary>
    internal static string ModelsDirectory => Path.Combine(RootDirectory, "models");

    /// <summary>The settings file <c>AppSettingsStore</c> resolves to for the whole run.</summary>
    internal static string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    [ModuleInitializer]
    internal static void Redirect()
    {
        // Fresh per run, not a fixed name: a settings file surviving from the previous run would
        // make a test asserting a shipped default pass or fail on what the last one happened to
        // write. Per process rather than per assembly, so two assemblies running side by side —
        // which is how the suite runs — cannot write each other's settings.
        RootDirectory = Directory.CreateTempSubdirectory("uindosill-test-user-data-").FullName;
        Directory.CreateDirectory(ModelsDirectory);

        Environment.SetEnvironmentVariable("UINDOSILL_MODELS_DIR", ModelsDirectory);
        Environment.SetEnvironmentVariable("UINDOSILL_SETTINGS_PATH", SettingsPath);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Remove(RootDirectory);
    }

    /// <summary>
    /// Best effort, and deliberately not a failure: a suite that cannot tidy up after itself has
    /// still run, and the alternative to swallowing this is a red build over a locked file.
    /// </summary>
    private static void Remove(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
