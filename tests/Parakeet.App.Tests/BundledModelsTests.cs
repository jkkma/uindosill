using Parakeet.App.Services;
using Parakeet.Core.Models;

namespace Parakeet.App.Tests;

/// <summary>
/// The weights the installer carries, and the order they lose to a downloaded copy in.
/// </summary>
/// <remarks>
/// Tests in one class run one after another, which is what makes the environment variable safe to
/// set here: nothing else in the suite reads
/// <see cref="BundledModels.DirectoryEnvironmentVariable"/>. It is restored either way, because a
/// leaked value would point every later test at a directory that has been deleted.
/// </remarks>
public sealed class BundledModelsTests : IDisposable
{
    private readonly string? _previous =
        Environment.GetEnvironmentVariable(BundledModels.DirectoryEnvironmentVariable);

    private readonly string _bundle =
        TestTemp.NewDirectory("uindosill-bundled-models");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BundledModels.DirectoryEnvironmentVariable, _previous);
        if (Directory.Exists(_bundle))
        {
            Directory.Delete(_bundle, recursive: true);
        }
    }

    [Fact]
    public void EveryBundledIdIsASingleFileEntryTheCatalogueActuallyHas()
    {
        // scripts/package-windows.ps1 reads this array and then looks each id up in models.json to
        // find the url and the digest. A typo there fails a release build on a Windows runner after
        // twenty minutes of packing; here it fails in a second.
        foreach (var id in BundledModels.BundledIds)
        {
            var model = ModelCatalog.Default.Models.SingleOrDefault(m => m.Id == id);

            Assert.True(model is not null, $"BundledModels.BundledIds names '{id}', which models.json does not have.");
            Assert.False(model!.IsMultiFile,
                $"'{id}' is a multi-file entry. BundledModels.PathFor answers null for those, so the "
                + "installer would carry a file nothing reads.");
        }
    }

    [Fact]
    public void TheOnesThatDoNotFitAreNotBundled()
    {
        // A GitHub release asset must be under 2 GiB, and the llm/cuda decision spent win-cuda's
        // room: the python-less Setup.exe measured 1,976,256,205 bytes on 2026-08-24 with both
        // weights inside, and rc.3's observed Python delta (+369.3 MB) projected the release
        // asset ~200 MB past the limit. The maintainer's decision, same day: the diariser's
        // weight leaves the win-cuda bundle (BundledModels.NotInCudaChannelIds), which this
        // arithmetic projects back under the limit — treating weights as incompressible, with
        // the next win-cuda tag as the observation (docs/UNPROVEN.md § the shipped ask tier).
        // Growing either list re-runs this sum, so adding a weight is a decision taken against
        // recorded numbers rather than a change that fails on the upload at the end of a release.
        var bundled = ModelCatalog.Default.Models.Where(m => BundledModels.BundledIds.Contains(m.Id)).ToList();
        long BytesOf(string id) => bundled.Single(m => m.Id == id).Files.Sum(f =>
            f.SizeBytes ?? throw new InvalidOperationException($"'{id}' has a file without a pinned size."));

        Assert.All(bundled, model =>
            Assert.True(model.Files.Sum(f => f.SizeBytes) < 1_000_000_000L,
                $"'{model.Id}' is bundled and is {model.Files.Sum(f => f.SizeBytes):N0} bytes."));

        // Every exclusion must name something the bundle actually carries — an exclusion of
        // nothing is the decision quietly not applying. The packaging script holds the same guard.
        Assert.All(BundledModels.NotInCudaChannelIds, id => Assert.Contains(id, BundledModels.BundledIds));

        const long MeasuredCudaSetupBytes = 1_976_256_205L;   // python-less, both weights inside
        const long BundledBytesWhenMeasured = 476_957_770L;
        const long ObservedPythonDeltaBytes = 369_300_000L;   // rc.3: 1187.9 MB against 818.6
        const long GitHubAssetLimit = 2L * 1024 * 1024 * 1024;

        var cudaBundleBytes = BundledModels.BundledIds
            .Except(BundledModels.NotInCudaChannelIds)
            .Sum(BytesOf);
        var projected = MeasuredCudaSetupBytes - BundledBytesWhenMeasured + cudaBundleBytes + ObservedPythonDeltaBytes;

        Assert.True(projected < GitHubAssetLimit,
            $"The projected win-cuda asset is {projected:N0} bytes against GitHub's "
            + $"{GitHubAssetLimit:N0}-byte limit. Something has to leave the channel before it is tagged.");
    }

    [Fact]
    public void AGraphBesideTheApplicationIsFoundWhenNothingHasBeenDownloaded()
    {
        var model = ModelCatalog.Default.VoiceActivityModels.First();
        WriteBundled(model);

        var store = new LocalModelStore(TestTemp.NewDirectory("uindosill-empty-store"));
        var provider = new EngineProvider(store, () => true);

        Assert.True(provider.SupportsNeuralSpeechDetection,
            "The installer carries this graph, so the opt-in has to be live on a fresh install.");
        Assert.Equal(Path.Combine(_bundle, model.StorageName), provider.PathForInstalledOrBundled(model));
        Assert.Null(provider.DescribeUnavailable(ModelTask.VoiceActivity));

        // Not CreateSpeechDetector(): it loads the graph through ONNX Runtime as it is constructed,
        // so exercising it here would mean committing a real 2.2 MiB model to the repository to
        // test path resolution. That the graph loads and scores is Parakeet.Engine.SileroVad.Tests',
        // against the real file, and it says so when the file is not there.
    }

    [Fact]
    public void ADownloadedCopyWinsOverTheBundledOne()
    {
        var model = ModelCatalog.Default.VoiceActivityModels.First();
        WriteBundled(model);

        // The store's copy is the one the Models tab updates and removes, and the one a user chose
        // to fetch. A bundle that quietly took precedence would make both of those do nothing.
        var storeDirectory = TestTemp.NewDirectory("uindosill-store");
        var store = new LocalModelStore(storeDirectory);
        var downloaded = store.PathFor(model);
        Directory.CreateDirectory(Path.GetDirectoryName(downloaded)!);
        File.WriteAllText(downloaded, "the copy the user downloaded");

        var provider = new EngineProvider(store, () => true);

        Assert.True(provider.SupportsNeuralSpeechDetection);
        Assert.Equal(downloaded, provider.PathForInstalledOrBundled(model));
    }

    [Fact]
    public void NothingBesideTheApplicationMeansTheDownloadIsStillHowItArrives()
    {
        Environment.SetEnvironmentVariable(BundledModels.DirectoryEnvironmentVariable, _bundle);

        var store = new LocalModelStore(TestTemp.NewDirectory("uindosill-empty-store"));
        var provider = new EngineProvider(store, () => true);

        // A build from source carries no weights until the packaging script has run, and on one the
        // opt-in has to say so rather than fail at Start.
        Assert.False(provider.SupportsNeuralSpeechDetection);
        Assert.Contains("Models tab", provider.DescribeUnavailable(ModelTask.VoiceActivity), StringComparison.Ordinal);
    }

    [Fact]
    public void AMultiFileEntryIsNeverAnsweredFromTheBundle()
    {
        Environment.SetEnvironmentVariable(BundledModels.DirectoryEnvironmentVariable, _bundle);

        var translation = ModelCatalog.Default.Models.First(m => m.IsMultiFile);

        Assert.Null(BundledModels.PathFor(translation));
    }

    [Fact]
    public void NonCommercialWeightsAreNeverCarriedByTheInstaller()
    {
        // **A licence obligation asserted as code, because it is the whole of the decision.**
        // DiariZen's weights are CC BY-NC 4.0 and every other model here permits redistribution.
        // Bundling them would make each Uindosill build a redistribution of non-commercial material
        // inside an otherwise MIT/GPL distribution, and would hand every commercial recipient a file
        // they may not use. Downloaded, the copy is the user's and this project ships nothing under
        // NC terms at all -- so the exclusion is not a packaging preference that a later size
        // decision may quietly reverse. docs/LICENSING.md is the record; this is the guard.
        //
        // Written over the licence rather than over the id, so that a sixth entry arriving under a
        // non-commercial licence is caught by a test nobody remembered to update.
        var nonCommercial = ModelCatalog.Default.Models
            .Where(m => m.License.Contains("NC", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(nonCommercial);
        foreach (var model in nonCommercial)
        {
            Assert.DoesNotContain(model.Id, BundledModels.BundledIds);
            Assert.Null(BundledModels.PathFor(model));
        }
    }

    [Fact]
    public void TheChosenDiariserSurvivesBeingSavedAndReadBack()
    {
        // Save() writes an explicit dictionary of keys rather than serialising the record, so a new
        // property is stored only if somebody adds it in two places. One that is added to the record
        // and to neither compiles, round-trips as null, and loses the user's choice on the next
        // write of any *other* setting -- silently, and only on their machine.
        var path = Path.Combine(TestTemp.NewDirectory("uindosill-settings"), "settings.json");
        var store = new AppSettingsStore(path);

        Assert.Null(store.Load().DiarisationModelId);
        Assert.True(store.Update(current => current with { DiarisationModelId = "diarizen-wavlm-large-s80-md-v2" }));
        Assert.Equal("diarizen-wavlm-large-s80-md-v2", store.Load().DiarisationModelId);

        // And it survives a write that is about something else, which is the failure above.
        Assert.True(store.Update(current => current with { CheckForUpdatesOnLaunch = false }));
        Assert.Equal("diarizen-wavlm-large-s80-md-v2", store.Load().DiarisationModelId);
    }

    private void WriteBundled(ModelDescriptor model)
    {
        Environment.SetEnvironmentVariable(BundledModels.DirectoryEnvironmentVariable, _bundle);
        File.WriteAllText(Path.Combine(_bundle, model.StorageName), "the copy the installer carried");
    }
}
