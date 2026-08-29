using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.Core.Models;

namespace Parakeet.App.Tests;

/// <summary>
/// The one action in this window that deletes tens of gigabytes on a single click.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The folder notice stated that the weights outlive an uninstall and
/// stopped, leaving somebody who had just decided to uninstall with one Remove per entry. Tens of
/// gigabytes stay behind when a reader does not know to do that.
/// </para>
/// <para>
/// <b>Why it is a button as well as an uninstall hook.</b> A silent hook was built and withdrawn on
/// 2026-08-23: run directly it deleted the data directory, run by the uninstaller it returned in
/// 98 ms having deleted nothing, and six causes were eliminated by experiment without the failure
/// reproducing. What came back on 2026-08-29 asks first. The rule both obey is that nothing this
/// product does unattended deletes a user's files; this button is the route that does not depend on
/// that callback ever firing, and the only one below the 64 MiB under which nothing is asked.
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
    public void TheNoticeTellsSomebodyWithASmallFolderWhereToClearIt()
    {
        // The old wording stated the survival as a property and stopped. Someone reading it while
        // deciding to uninstall has to be told where it happens. A fixture entry is a few bytes, so
        // this is the branch below the prompt's threshold, where the uninstaller says nothing.
        var tab = NewTab(out var directory);
        Install(directory, ModelCatalog.Default.Models[0]);
        tab.Refresh();

        Assert.Contains("Remove them here", tab.UninstallNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("asks whether to delete", tab.UninstallNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNoticePromisesTheUninstallQuestionOnlyWhereItWillBeAsked()
    {
        // Through NoticeFor rather than a fixture: reaching the third branch on disk would mean
        // writing 64 MiB in a suite whose whole discipline is that it needs no weights. The
        // threshold is read off UninstallPrompt rather than retyped, so this cannot pass while the
        // window promises a question the uninstaller will not ask.
        var empty = ModelsViewModel.NoticeFor(0);
        Assert.Contains("none here at the moment", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("asks whether to delete", empty, StringComparison.Ordinal);

        // At the threshold exactly, the uninstaller is still silent: it asks strictly above it.
        var small = ModelsViewModel.NoticeFor(UninstallPrompt.AskAboveBytes);
        Assert.Contains("Remove them here", small, StringComparison.Ordinal);
        Assert.DoesNotContain("asks whether to delete", small, StringComparison.Ordinal);

        // One byte over, the question is certain, because the models sit inside the directory the
        // prompt measures: a models total past the threshold puts that directory past it too.
        var large = ModelsViewModel.NoticeFor(UninstallPrompt.AskAboveBytes + 1);
        Assert.Contains("asks whether to delete", large, StringComparison.Ordinal);
        Assert.Contains("keeps them unless you answer Yes", large, StringComparison.Ordinal);

        // And no branch says an uninstall leaves them alone, which is what this window, the Updates
        // tab, the CLI help and four documents all said until 2026-08-29.
        foreach (var notice in new[] { empty, small, large })
        {
            Assert.DoesNotContain("leaves them behind", notice, StringComparison.Ordinal);
        }
    }
}
