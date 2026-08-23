using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Parakeet.Core.Licensing;
using Parakeet.Core.Transcription;

namespace Parakeet.App.ViewModels;

/// <summary>
/// What the About window says: which build this is, what it is, the machine it is running on, and
/// the full notice package.
/// </summary>
/// <remarks>
/// <para>
/// A view model of its own rather than three properties on
/// <see cref="MainWindowViewModel"/>, which is where they were until the Licences tab was retired
/// on 2026-08-23. The reason is not tidiness: the notice, the environment line and the threading
/// note are the whole content of a window that the main one opens and does not otherwise know
/// about, and a window that binds to <c>MainWindowViewModel</c> to read three strings can reach
/// the queue, the model session and the updater as well. This is the surface the About window is
/// allowed to see.
/// </para>
/// <para>
/// Every value here is a fact about this build or this machine, so nothing is settable and nothing
/// raises a change except <see cref="SelectedTab"/>, which is which of the three panes is showing.
/// </para>
/// </remarks>
public sealed partial class AboutViewModel : ObservableObject
{
    /// <summary>Which pane is showing: 0 About, 1 Licences, 2 System.</summary>
    [ObservableProperty]
    private int _selectedTab;

    public AboutViewModel(string version, string modelDirectory, string settingsPath)
    {
        Version = version;
        ModelDirectory = modelDirectory;
        SettingsPath = settingsPath;
    }

    /// <summary>The build's own version, as the updater reports it.</summary>
    public string Version { get; }

    /// <summary>Where weights are downloaded to, named because a bug report needs it.</summary>
    public string ModelDirectory { get; }

    /// <summary>The settings file, named for the same reason.</summary>
    public string SettingsPath { get; }

    public string Tagline => "local transcription";

    /// <summary>
    /// What this application is, in the two sentences somebody opening an About box will read.
    /// </summary>
    /// <remarks>
    /// The second sentence is the same promise the Updates tab makes, and it is here as well
    /// because this is where a person comes to find out what a program does on their network. The
    /// two must not drift: if the launch check ever stops being the only unprompted request, both
    /// say so.
    /// </remarks>
    public string Summary =>
        "Uindosill turns recordings into text on this machine. The audio, the transcript and the " +
        "models all stay on your own disk — nothing is uploaded, and there is no account to make.";

    /// <inheritdoc cref="Summary" />
    public string NetworkNote =>
        "The only thing this application does on the network without being asked is check once at " +
        "launch whether a newer version exists, and that can be switched off on the Updates tab. " +
        "Everything else that reaches the network — downloading a model, fetching a link — you " +
        "start yourself.";

    /// <summary>
    /// The full notice package, shown in the application because the licences require it to be
    /// present where the material is used, not only in a file in the source repository.
    /// </summary>
    public string LicenceText
    {
        get
        {
            var lines = new List<string>();

            foreach (var attribution in Attributions.ById.Values)
            {
                lines.Add(attribution.ToPlainText(Environment.NewLine));
            }

            lines.Add("Restrictions that come with these weights:");
            lines.AddRange(Attributions.WeightUsageRestrictions.Select(r => "  - " + r));
            lines.Add(string.Empty);
            lines.Add("Third-party components:");

            foreach (var component in Attributions.Components)
            {
                lines.Add($"  {component.Component} — {component.License} — {component.Uri}");

                // The notes carry the qualifying text — which builds ship a component, and on what
                // terms. This panel used to drop them while `uindosill notice` printed them, so the
                // two surfaces disagreed about a licence notice. They are rendered in both now.
                if (component.Notes is { Length: > 0 } notes)
                {
                    lines.Add($"    {notes}");
                }
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    public string LicenceNotice =>
        "Shown here because the licences require the notice to be present where the material is " +
        "used, not only in a file in the source repository.";

    public string EnvironmentSummary =>
        $"{RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription} " +
        $"({RuntimeInformation.ProcessArchitecture}), {Environment.ProcessorCount} logical processors";

    /// <summary>
    /// Stated plainly in the window rather than buried: the ABI takes no thread count, so a
    /// thread control here would be a slider connected to nothing.
    /// </summary>
    public string ThreadingNote =>
        $"Decode threads are chosen by the engine. The parakeet.cpp ABI takes no thread count, so this build " +
        $"cannot cap them at the recommended {DecodeThreadPlanner.MaxRecommended}.";

    /// <summary>
    /// Everything on the System pane as one block, which is what the Copy button puts on the
    /// clipboard.
    /// </summary>
    /// <remarks>
    /// It exists because the System pane's whole purpose is being pasted into a bug report, and
    /// asking somebody to select five lines out of a scrolling window is asking them to send four.
    /// Built here rather than in the code-behind so what is copied is what is drawn: both read
    /// these same properties, in this order.
    /// </remarks>
    public string SystemReport => string.Join(
        Environment.NewLine,
        $"Uindosill {Version}",
        EnvironmentSummary,
        ThreadingNote,
        $"Models: {ModelDirectory}",
        $"Settings: {SettingsPath}");
}
