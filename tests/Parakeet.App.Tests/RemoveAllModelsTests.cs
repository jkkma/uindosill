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
    private sealed class FaultingModelStore(IModelStore inner) : IModelStore
    {
        public Func<ModelDescriptor, Exception?>? RemoveFailure { get; init; }
        public Exception? RemoveSideloadedFileFailure { get; init; }
        public Exception? RemoveSideloadedDirectoryFailure { get; init; }

        public string RootDirectory => inner.RootDirectory;

        public string PathFor(ModelDescriptor model) => inner.PathFor(model);

        public string PathFor(ModelDescriptor model, ModelFile file) => inner.PathFor(model, file);

        public bool IsInstalled(ModelDescriptor model) => inner.IsInstalled(model);

        public IReadOnlyList<InstalledModel> ListInstalled() => inner.ListInstalled();

        public IReadOnlyList<InstalledModel> ListInstalled(ModelCatalog catalog) => inner.ListInstalled(catalog);

        public bool Remove(ModelDescriptor model)
        {
            if (RemoveFailure?.Invoke(model) is { } failure)
            {
                throw failure;
            }

            return inner.Remove(model);
        }

        public int GatherIntoPlace(ModelDescriptor model) => inner.GatherIntoPlace(model);

        public bool RemoveSideloaded(string fileName) => inner.RemoveSideloaded(fileName);

        public bool RemoveSideloaded(string fileName, ModelCatalog catalog)
        {
            if (RemoveSideloadedFileFailure is { } failure)
            {
                throw failure;
            }

            return inner.RemoveSideloaded(fileName, catalog);
        }

        public IReadOnlyList<SideloadedDirectory> ListSideloadedDirectories(ModelCatalog catalog) =>
            inner.ListSideloadedDirectories(catalog);

        public bool RemoveSideloadedDirectory(string directoryName, ModelCatalog catalog)
        {
            if (RemoveSideloadedDirectoryFailure is { } failure)
            {
                throw failure;
            }

            return inner.RemoveSideloadedDirectory(directoryName, catalog);
        }
    }

    private static ModelsViewModel NewTab(out string directory)
    {
        directory = TestTemp.NewDirectory("uindosill-removeall");
        return NewTab(new LocalModelStore(directory));
    }

    private static ModelsViewModel NewTab(IModelStore store)
    {
        return new MainWindowViewModel(
            new FakeEngineProvider(),
            store,
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
    public void AFailedSingleRemoveIsReportedAndTheInstalledStateIsRestored()
    {
        var directory = TestTemp.NewDirectory("uindosill-remove-one-failure");
        var model = ModelCatalog.Default.Models[0];
        Install(directory, model);
        var local = new LocalModelStore(directory);
        var store = new FaultingModelStore(local)
        {
            RemoveFailure = candidate => candidate.Id == model.Id
                ? new IOException("The weights are locked.")
                : null,
        };
        var tab = NewTab(store);
        tab.Selected = tab.Models.Single(candidate => candidate.Id == model.Id);

        tab.RemoveCommand.Execute(null);

        Assert.True(local.IsInstalled(model));
        Assert.True(tab.Selected.IsInstalled);
        Assert.Contains(model.DisplayName, tab.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("could not be removed", tab.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("locked", tab.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void APartiallyDeletedModelRemainsVisibleAndCanBeRetried()
    {
        var directory = TestTemp.NewDirectory("uindosill-remove-one-partial");
        var model = ModelCatalog.Default.Models.First(candidate => candidate.Files.Count > 1);
        Install(directory, model);
        var local = new LocalModelStore(directory);
        var removedFile = local.PathFor(model, model.Files[0]);
        var remainingFile = local.PathFor(model, model.Files[1]);
        var failOnce = true;
        var store = new FaultingModelStore(local)
        {
            RemoveFailure = candidate =>
            {
                if (candidate.Id != model.Id || !failOnce)
                {
                    return null;
                }

                failOnce = false;
                File.Delete(removedFile);
                return new IOException("A later file is locked.");
            },
        };
        var tab = NewTab(store);
        tab.Selected = tab.Models.Single(candidate => candidate.Id == model.Id);

        tab.RemoveCommand.Execute(null);

        Assert.False(File.Exists(removedFile));
        Assert.True(File.Exists(remainingFile));
        Assert.False(local.IsInstalled(model));
        Assert.False(tab.Selected.IsInstalled);
        Assert.True(tab.Selected.HasStoredFiles);
        Assert.True(tab.Selected.CanRemove);
        Assert.Equal("Incomplete", tab.Selected.Status);
        Assert.True(tab.CanRemoveAll);
        Assert.True(tab.RemoveAllCommand.CanExecute(null));
        Assert.Contains("could not be removed", tab.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("later file is locked", tab.StatusMessage, StringComparison.Ordinal);

        // A retrying download can also occupy an incomplete directory. It must not be deleted
        // while its files are being written, even though the remainder stays visible.
        tab.Selected.IsBusy = true;
        Assert.False(tab.Selected.CanRemove);
        Assert.False(tab.CanRemoveAll);
        tab.RemoveCommand.Execute(null);
        Assert.True(File.Exists(remainingFile));
        tab.Selected.IsBusy = false;

        tab.RemoveAllCommand.Execute(null);

        Assert.False(Directory.Exists(local.PathFor(model)));
        Assert.False(tab.Selected.HasStoredFiles);
        Assert.False(tab.CanRemoveAll);
        Assert.Equal("Removed 1 model.", tab.StatusMessage);
    }

    [Fact]
    public void RemoveAllContinuesPastAnAccessFailureAndCountsOnlySuccessfulDeletes()
    {
        var directory = TestTemp.NewDirectory("uindosill-removeall-partial");
        var failed = ModelCatalog.Default.Models[0];
        var removed = ModelCatalog.Default.Models[1];
        Install(directory, failed);
        Install(directory, removed);
        var local = new LocalModelStore(directory);
        var store = new FaultingModelStore(local)
        {
            RemoveFailure = candidate => candidate.Id == failed.Id
                ? new UnauthorizedAccessException("Access was denied.")
                : null,
        };
        var tab = NewTab(store);

        tab.RemoveAllCommand.Execute(null);

        Assert.True(local.IsInstalled(failed));
        Assert.False(local.IsInstalled(removed));
        Assert.True(tab.Models.Single(candidate => candidate.Id == failed.Id).IsInstalled);
        Assert.False(tab.Models.Single(candidate => candidate.Id == removed.Id).IsInstalled);
        Assert.True(tab.CanRemoveAll);
        Assert.StartsWith(
            $"Removed 1 model, freeing about {ByteSize.Describe(removed.TotalSizeBytes ?? 0)}.",
            tab.StatusMessage,
            StringComparison.Ordinal);
        Assert.Contains(failed.DisplayName, tab.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Access was denied", tab.StatusMessage, StringComparison.Ordinal);

        tab.RemoveAllCommand.Execute(null);

        Assert.StartsWith("No models were fully removed.", tab.StatusMessage, StringComparison.Ordinal);
        Assert.True(local.IsInstalled(failed));
    }

    [Fact]
    public void AFailedSideloadedFileDeleteIsReportedAndTheRowRemains()
    {
        var directory = TestTemp.NewDirectory("uindosill-remove-stray-file-failure");
        var path = Path.Combine(directory, "withdrawn-model.gguf");
        File.WriteAllText(path, "weights");
        var store = new FaultingModelStore(new LocalModelStore(directory))
        {
            RemoveSideloadedFileFailure = new IOException("The file is in use."),
        };
        var tab = NewTab(store);
        tab.SelectedSideloaded = Assert.Single(tab.Sideloaded);

        tab.RemoveSideloadedCommand.Execute(null);

        Assert.True(File.Exists(path));
        Assert.Single(tab.Sideloaded);
        Assert.Equal("withdrawn-model.gguf", tab.SelectedSideloaded?.Name);
        Assert.Contains("could not be deleted", tab.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("in use", tab.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedSideloadedDirectoryDeleteIsReportedAndTheRowIsRemeasured()
    {
        var directory = TestTemp.NewDirectory("uindosill-remove-stray-directory-failure");
        var orphan = Path.Combine(directory, "retired-model");
        Directory.CreateDirectory(orphan);
        var path = Path.Combine(orphan, "weights.bin");
        File.WriteAllText(path, "weights");
        var store = new FaultingModelStore(new LocalModelStore(directory))
        {
            RemoveSideloadedDirectoryFailure = new UnauthorizedAccessException("The directory is read-only."),
        };
        var tab = NewTab(store);
        tab.SelectedSideloaded = Assert.Single(tab.Sideloaded);

        // Simulate another writer changing the directory between the scan and the delete. Refresh in
        // the failure path must replace the stale size along with preserving the row.
        File.AppendAllText(path, " and more weights");
        tab.RemoveSideloadedCommand.Execute(null);

        var remaining = Assert.Single(tab.Sideloaded);
        Assert.True(Directory.Exists(orphan));
        Assert.Equal(new FileInfo(path).Length, remaining.SizeBytes);
        Assert.Equal("retired-model", tab.SelectedSideloaded?.Name);
        Assert.Contains("could not be deleted", tab.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("read-only", tab.StatusMessage, StringComparison.Ordinal);
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
