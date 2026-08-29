using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.Core.Models;

namespace Parakeet.App.Tests;

/// <summary>
/// The one action in this window that deletes tens of gigabytes on a single click.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The folder notice said the weights survive an uninstall, which is true,
/// and left somebody who had just decided to uninstall with one Remove per entry. Tens of gigabytes
/// stay behind when a reader does not know to do that.
/// </para>
/// <para>
/// <b>Why it is a button and not an uninstall hook.</b> A hook was built and withdrawn on
/// 2026-08-23: run directly it deleted the data directory, run by the uninstaller it returned in
/// 98 ms having deleted nothing, and six causes were eliminated by experiment without the failure
/// reproducing. The rule that came out of it is that nothing this product does unattended deletes a
/// user's files, and these tests are the guard on the attended replacement.
/// </para>
/// </remarks>
public class RemoveAllModelsTests
{
    private static ModelsViewModel NewTab(out string directory)
    {
        directory = TestTemp.NewDirectory("uindosill-removeall");
        return new MainWindowViewModel(
            new FakeEngineProvider(),
            new LocalModelStore(directory),
            ModelCatalog.Default,
            player: new FakeMediaPlayer()).Models;
    }

    private static void Install(string directory, ModelDescriptor model)
    {
        // Enough of a file per declared name that the store calls the entry installed.
        foreach (var file in model.Files)
        {
            var path = model.IsMultiFile
                ? Path.Combine(directory, model.DirectoryName!, file.FileName)
                : Path.Combine(directory, file.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "weights");
        }
    }

    [Fact]
    public void WithNothingInstalledThereIsNothingToRemove()
    {
        var tab = NewTab(out _);

        Assert.False(tab.CanRemoveAll);
        Assert.False(tab.RemoveAllCommand.CanExecute(null));
    }

    [Fact]
    public void ItRemovesEveryInstalledEntryInOneAction()
    {
        var tab = NewTab(out var directory);
        var store = new LocalModelStore(directory);

        var installed = ModelCatalog.Default.Models.Take(3).ToList();
        foreach (var model in installed)
        {
            Install(directory, model);
        }

        tab.Refresh();
        Assert.True(tab.CanRemoveAll);

        tab.RemoveAllCommand.Execute(null);

        Assert.All(installed, m => Assert.False(store.IsInstalled(m)));
        Assert.All(tab.Models, m => Assert.False(m.IsInstalled));
        Assert.False(tab.CanRemoveAll);
    }

    [Fact]
    public void ItSaysHowMuchItFreedRatherThanJustThatItWorked()
    {
        // The number is the whole point of the button: somebody presses it to reclaim disk, and a
        // bare "Removed." leaves them checking Explorer to find out whether it did anything.
        var tab = NewTab(out var directory);
        Install(directory, ModelCatalog.Default.Models[0]);
        tab.Refresh();

        tab.RemoveAllCommand.Execute(null);

        Assert.Contains("Removed", tab.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("freeing about", tab.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ALoadedModelIsLeftAloneAndNamed()
    {
        // Deleting weights from under a loaded engine leaves the window claiming a model is
        // resident while its files are gone, which is the quiet inconsistency the single Remove
        // already refuses to produce. Skipping silently would be worse: the folder would still
        // hold gigabytes after a button that said it had removed everything.
        var tab = NewTab(out var directory);
        var first = ModelCatalog.Default.Models[0];
        var second = ModelCatalog.Default.Models[1];
        Install(directory, first);
        Install(directory, second);
        tab.Refresh();

        tab.Models.Single(m => m.Id == first.Id).IsLoaded = true;

        tab.RemoveAllCommand.Execute(null);

        var store = new LocalModelStore(directory);
        Assert.True(store.IsInstalled(first));
        Assert.False(store.IsInstalled(second));
        Assert.Contains(first.DisplayName, tab.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Unload", tab.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ItIsRefusedWhileABatchIsRunning()
    {
        var tab = NewTab(out var directory);
        Install(directory, ModelCatalog.Default.Models[0]);
        tab.Refresh();
        Assert.True(tab.CanRemoveAll);

        tab.IsTranscribing = true;

        Assert.False(tab.CanRemoveAll);
    }

    [Fact]
    public void TheNoticeTellsSomebodyUninstallingToClearItHere()
    {
        // The old wording stated the survival as a property and stopped. Someone reading it while
        // deciding to uninstall has to be told that this is the only place it happens.
        var tab = NewTab(out var directory);
        Install(directory, ModelCatalog.Default.Models[0]);
        tab.Refresh();

        Assert.Contains("uninstalling", tab.UninstallNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("remove it here first", tab.UninstallNotice, StringComparison.OrdinalIgnoreCase);
    }
}
