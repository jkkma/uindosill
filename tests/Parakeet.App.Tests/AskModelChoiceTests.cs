using Parakeet.App.Services;
using Parakeet.Core.Models;

namespace Parakeet.App.Tests;

/// <summary>
/// Which .gguf the Ask panel serves. Until 2026-08-25 that was whichever file was largest, which
/// is predictable but is not a choice — and the same day's measurements made the difference
/// matter: on this hardware a 9B answered 2.3x faster than a 26B mixture whose citations held up
/// better. These drive the picker's seam with files on a real disk and no model in any of them.
/// </summary>
/// <remarks>
/// Every assertion here goes through <see cref="LlamaAnswerEngineProvider.ResolveModelFileName"/>
/// rather than <c>Check()</c>, and the first draft of this file did the opposite and passed here
/// while failing on a clean runner. <c>Check()</c> answers "can this panel work at all" and
/// returns before looking at the models folder when no <c>llama-server</c> drop is vendored — so
/// asserting on its <c>ModelFileName</c> tests the machine's native drop as much as the code, and
/// a development machine that has one hides that from the person who wrote it.
/// </remarks>
public class AskModelChoiceTests
{
    private static (LlamaAnswerEngineProvider Provider, string Directory, Func<string?> Chosen, Action<string?> Choose) Provider()
    {
        var directory = TestTemp.NewDirectory("uindosill-askmodel");
        string? chosen = null;
        var provider = new LlamaAnswerEngineProvider(
            new LocalModelStore(directory),
            chosenModel: () => chosen);

        return (provider, directory, () => chosen, value => chosen = value);
    }

    private static void Write(string directory, string name, int bytes) =>
        File.WriteAllBytes(Path.Combine(directory, name), new byte[bytes]);

    [Fact]
    public void TheFilesAreOfferedLargestFirst()
    {
        var (provider, directory, _, _) = Provider();
        Write(directory, "small.gguf", 10);
        Write(directory, "big.gguf", 300);
        Write(directory, "middling.gguf", 100);

        // Not a .gguf, so not a model this panel can serve.
        Write(directory, "notes.txt", 500);

        Assert.Equal(["big.gguf", "middling.gguf", "small.gguf"], provider.AvailableModelFileNames());
    }

    [Fact]
    public void AnEmptyOrAbsentFolderOffersNothingRatherThanThrowing()
    {
        var (provider, directory, _, _) = Provider();
        Assert.Empty(provider.AvailableModelFileNames());
        Assert.Null(provider.ResolveModelFileName());

        Directory.Delete(directory, recursive: true);
        Assert.Empty(provider.AvailableModelFileNames());
        Assert.Null(provider.ResolveModelFileName());
    }

    [Fact]
    public void TheChosenModelIsServedAndTheUnchosenCaseIsStillTheLargest()
    {
        var (provider, directory, _, choose) = Provider();
        Write(directory, "small.gguf", 10);
        Write(directory, "big.gguf", 300);

        // Nobody has chosen: the largest, which is the pick a person can predict.
        Assert.Equal("big.gguf", provider.ResolveModelFileName());

        choose("small.gguf");
        Assert.Equal("small.gguf", provider.ResolveModelFileName());

        // Case is not a distinction a Windows file name makes.
        choose("SMALL.GGUF");
        Assert.Equal("small.gguf", provider.ResolveModelFileName());
    }

    [Fact]
    public void AChosenFileThatIsGoneFallsBackRatherThanRefusingToAnswer()
    {
        // The models folder is not this application's alone, and a panel that refused to answer
        // because a file someone deleted was once selected would be a setting failing loudly at
        // the wrong person. The picker still shows the stale name, so the setting explains
        // itself rather than silently reverting to a row nobody chose.
        var (provider, directory, _, choose) = Provider();
        Write(directory, "big.gguf", 300);
        choose("deleted-yesterday.gguf");

        Assert.Equal("big.gguf", provider.ResolveModelFileName());
    }
}
