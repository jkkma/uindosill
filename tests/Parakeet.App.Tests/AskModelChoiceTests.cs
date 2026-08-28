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
    public void WeightsThisApplicationInstalledForAnotherJobAreNotOffered()
    {
        // The recogniser's weights are a .gguf in the same folder and can answer nothing. Until
        // 2026-08-28 the largest-file rule hid this — a 1.34 GiB recogniser never outweighed a
        // 12.66 GiB answering model — but the row was in the picker the whole time, and choosing
        // it bought a load failure rather than a refusal.
        var (provider, directory, _, _) = Provider();

        var recogniser = Assert.Single(
            ModelCatalog.Default.TranscriptionModels
                .First(model => model.Files.Count == 1)
                .Files).FileName;

        // Larger than the real model on purpose: if the filter is not doing the work, the
        // largest-file rule serves this and the assertions below fail loudly.
        Write(directory, recogniser, 900);
        Write(directory, "an-answering-model.gguf", 10);

        Assert.Equal(["an-answering-model.gguf"], provider.AvailableModelFileNames());
        Assert.Equal("an-answering-model.gguf", provider.ResolveModelFileName());
    }

    [Fact]
    public void AFileNoManifestKnowsAboutIsStillOffered()
    {
        // The exclusion is by name against the catalogue, never a guess about contents: the models
        // folder is not this application's alone, and somebody's own weights are not refused for
        // being unrecognised.
        var (provider, directory, _, _) = Provider();
        Write(directory, "something-nobody-here-has-heard-of.gguf", 50);

        Assert.Equal(["something-nobody-here-has-heard-of.gguf"], provider.AvailableModelFileNames());
        Assert.Equal("something-nobody-here-has-heard-of.gguf", provider.ResolveModelFileName());
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
    public void TheCatalogueDefaultBeatsTheLargestFileButNotAnExplicitChoice()
    {
        // The largest-file rule stopped being right on 2026-08-28. The answering entry the
        // catalogue marks as the default is a smaller file than the mixture beside it and matched
        // it on the labelled question set at half the memory, so "the biggest thing installed"
        // would serve the wrong one to everybody who installed both and never said which.
        var (provider, directory, _, choose) = Provider();

        var preferred = ModelCatalog.Default.RecommendedAnswering;
        Assert.NotNull(preferred);

        // The weights, not the drafting head beside them — that file answers nothing. The rule is
        // restated here rather than shared, so a change to it has to be made twice on purpose.
        var weights = Assert.Single(
            preferred.Files,
            file => !file.FileName.StartsWith("mtp-", StringComparison.OrdinalIgnoreCase)).FileName;

        Write(directory, "enormous.gguf", 900);
        Write(directory, weights, 10);

        Assert.Equal(weights, provider.ResolveModelFileName());

        // The catalogue advises; the person decides.
        choose("enormous.gguf");
        Assert.Equal("enormous.gguf", provider.ResolveModelFileName());

        // And with the default not installed, the largest is served exactly as before — the step
        // is a preference among files that are there, not a demand for a particular one.
        choose(null);
        File.Delete(Path.Combine(directory, weights));
        Assert.Equal("enormous.gguf", provider.ResolveModelFileName());
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
