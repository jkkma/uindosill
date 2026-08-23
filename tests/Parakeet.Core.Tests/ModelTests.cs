using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Parakeet.Core.Licensing;
using Parakeet.Core.Models;

namespace Parakeet.Core.Tests;

public class ModelCatalogTests
{
    [Fact]
    public void EmbeddedCatalogueLoadsAndHasARecommendation()
    {
        var catalog = ModelCatalog.Default;

        Assert.NotEmpty(catalog.Models);
        Assert.NotNull(catalog.Recommended);

        // Three licences ship, and which entry has which is not incidental: the transcription
        // weights are CC BY 4.0, the diariser is under the NVIDIA Open Model License — whose notice
        // is a different shape and whose grant is revocable where CC BY's is not — and the
        // translator is Apache-2.0, whose section 4(a) wants a copy of the licence rather than a
        // link. Asserting the exact set rather than "some licence is present" is what makes adding
        // a fourth a deliberate act.
        Assert.All(
            catalog.TranscriptionModels,
            m => Assert.Equal("CC-BY-4.0", m.License));
        Assert.All(
            catalog.DiarisationModels,
            m => Assert.Equal("NVIDIA-Open-Model-License", m.License));
        Assert.All(
            catalog.TranslationModels,
            m => Assert.Equal("Apache-2.0", m.License));
    }

    [Fact]
    public void EveryCatalogueEntryHasAnAttributionRegistered()
    {
        // A model that ships without its notice package is a licence breach waiting to happen,
        // so the catalogue and the notices are checked against each other rather than trusted.
        foreach (var model in ModelCatalog.Default.Models)
        {
            Assert.True(
                Attributions.ById.ContainsKey(model.AttributionId),
                $"model '{model.Id}' names attribution '{model.AttributionId}', which is not registered");
        }
    }

    [Fact]
    public void UnverifiedEntriesAreMarkedRatherThanQuietlyShipped()
    {
        foreach (var model in ModelCatalog.Default.Models.Where(m => !m.IsFullyPinned))
        {
            Assert.False(model.Verified, $"'{model.Id}' claims to be verified but pins no digest");
        }
    }

    [Fact]
    public void EveryShippedEntryIsPinned()
    {
        // The catalogue used to ship entirely unpinned, so a user downloading 1.34 GiB of weights
        // had nothing to check them against and had to opt out of verification to get them at all.
        // The digests now come from the repository's LFS oids. This asserts the state rather than
        // the intention, so adding an entry without one fails here instead of at somebody's
        // download.
        Assert.NotEmpty(ModelCatalog.Default.Models);

        Assert.All(ModelCatalog.Default.Models, model =>
        {
            Assert.NotEmpty(model.Files);

            // Verified means the URL was checked against a live repository, which is a different
            // claim from "the digest is right". Every entry is now both: the translation entry was
            // the last exception here, and on 2026-08-20 its nine files were published and every
            // one of the published LFS oids matched the digest taken off the bytes the gate was
            // scored against. There is no longer an entry to excuse, so nothing is excused — a new
            // unverified entry fails this outright.
            Assert.True(model.Verified, $"'{model.Id}' pins a digest but is not marked verified");

            // Per file, not per entry. An entry of nine files where eight are pinned is not a
            // pinned entry, and asserting only the aggregate would let the ninth through.
            Assert.All(model.Files, file =>
            {
                Assert.NotNull(file.Sha256);
                Assert.True(ModelCatalog.IsSha256Hex(file.Sha256!), $"'{model.Id}/{file.FileName}' has a malformed digest");
                Assert.True(file.SizeBytes > 0, $"'{model.Id}/{file.FileName}' has no pinned size to compare against");
            });

            Assert.True(model.IsFullyPinned, $"'{model.Id}' is not fully pinned");
            Assert.True(model.TotalSizeBytes > 0, $"'{model.Id}' has no total size");
        });
    }

    [Fact]
    public void DeferredPinsAreRecordedAndUnreachable()
    {
        var catalog = ModelCatalog.Default;
        Assert.NotEmpty(catalog.Deferred);

        foreach (var pin in catalog.Deferred)
        {
            // Reachable by id would mean 'models download <id>' fetches a file whose licence
            // nobody has established, which is the whole reason these are not catalogue entries.
            Assert.False(catalog.TryGet(pin.Id, out _), $"deferred pin '{pin.Id}' is reachable by id");
            Assert.DoesNotContain(catalog.Models, m => string.Equals(m.Id, pin.Id, StringComparison.OrdinalIgnoreCase));

            Assert.True(ModelCatalog.IsSha256Hex(pin.Sha256));
            Assert.True(pin.SizeBytes > 0);
            Assert.NotEmpty(pin.Purpose);
        }
    }

    [Fact]
    public void DeferredPinsCarryNoLicenceClaim()
    {
        // DeferredModelPin has no licence or attribution property at all, so a pin cannot assert
        // one by being filled in carelessly. Asserted against the type rather than the data,
        // because the guarantee is structural: adding such a property is what would break it.
        var properties = typeof(DeferredModelPin).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(properties, n => n.Contains("Licen", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, n => n.Contains("Attribution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ADeferredIdThatCollidesWithAModelIsRejected() =>
        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse("""
            {
              "schema": 1,
              "models": [
                {
                  "id": "collide", "family": "f", "displayName": "d", "quantisation": "f16",
                  "fileName": "a.gguf", "url": "https://example.invalid/a.gguf",
                  "license": "CC-BY-4.0", "attributionId": "nvidia-parakeet-tdt-0.6b-v3",
                  "languages": ["en"]
                }
              ],
              "deferred": [
                {
                  "id": "collide", "family": "f", "fileName": "b.gguf",
                  "url": "https://example.invalid/b.gguf", "sizeBytes": 1,
                  "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
                  "purpose": "p"
                }
              ]
            }
            """));

    [Fact]
    public void ADeferredPinWithoutASizeIsRejected() =>
        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse("""
            {
              "schema": 1,
              "models": [],
              "deferred": [
                {
                  "id": "no-size", "family": "f", "fileName": "b.gguf",
                  "url": "https://example.invalid/b.gguf",
                  "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
                  "purpose": "p"
                }
              ]
            }
            """));

    [Fact]
    public void PinnedDigestsAreDistinct()
    {
        // Copying one entry to make another is easy and leaves two quantisations sharing a digest,
        // which would then reject whichever was downloaded second with a corruption error.
        // Across every file of every entry, not just entry to entry: two files of one multi-file
        // model sharing a digest is the same copy-paste slip one directory further down.
        var duplicate = ModelCatalog.Default.Models
            .SelectMany(m => m.Files.Select(f => (Model: m, File: f)))
            .GroupBy(x => x.File.Sha256, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        Assert.True(
            duplicate is null,
            $"digest {duplicate?.Key} is pinned by {duplicate?.Count()} files: " +
            $"{string.Join(", ", duplicate?.Select(x => $"{x.Model.Id}/{x.File.FileName}") ?? [])}");
    }

    [Fact]
    public void ManifestWithoutModelsArrayIsRejected() =>
        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse("""{"schema":1}"""));

    [Fact]
    public void NonHttpsUrlIsRejected()
    {
        const string Json = """
            {"models":[{"id":"a","family":"f","displayName":"A","quantisation":"q","fileName":"a.gguf",
            "url":"http://example.com/a.gguf","license":"CC-BY-4.0","attributionId":"x"}]}
            """;

        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(Json));
    }

    [Fact]
    public void MalformedDigestIsRejected()
    {
        const string Json = """
            {"models":[{"id":"a","family":"f","displayName":"A","quantisation":"q","fileName":"a.gguf",
            "url":"https://example.com/a.gguf","sha256":"nope","license":"CC-BY-4.0","attributionId":"x"}]}
            """;

        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(Json));
    }

    [Fact]
    public void PathTraversalInFileNameIsRejected()
    {
        const string Json = """
            {"models":[{"id":"a","family":"f","displayName":"A","quantisation":"q","fileName":"../evil.gguf",
            "url":"https://example.com/a.gguf","license":"CC-BY-4.0","attributionId":"x"}]}
            """;

        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(Json));
    }

    [Fact]
    public void DuplicateIdsAreRejected()
    {
        const string Json = """
            {"models":[
            {"id":"a","family":"f","displayName":"A","quantisation":"q","fileName":"a.gguf","url":"https://e.com/a","license":"L","attributionId":"x"},
            {"id":"A","family":"f","displayName":"A","quantisation":"q","fileName":"b.gguf","url":"https://e.com/b","license":"L","attributionId":"x"}]}
            """;

        Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(Json));
    }

    [Fact]
    public void TheDiariserAndTheTranslatorAreTheOnlyEntriesThatDoNotTranscribe()
    {
        // The manifest carries exactly one diarisation entry and one translation entry, and every
        // other entry is an ASR model. Both halves are asserted: an entry that lost its `task` field
        // would surface as a transcription model and be offered to `transcribe`, which is the
        // failure the discriminator exists to prevent.
        var catalog = ModelCatalog.Default;

        var diariser = Assert.Single(catalog.DiarisationModels);
        Assert.Equal("sortformer-4spk-v2.1", diariser.Id);
        Assert.False(diariser.Recommended);

        // And one translation entry, added 2026-08-20 once there was a decode loop to read it.
        // Neither may be Recommended: that property picks the default ASR model, and a model that
        // cannot transcribe becoming the default is the failure the discriminator exists to stop.
        var translator = Assert.Single(catalog.TranslationModels);
        Assert.Equal("opus-mt-tc-bible-big-mul-en-fp32", translator.Id);
        Assert.False(translator.Recommended);

        Assert.All(catalog.TranscriptionModels, m => Assert.Equal(ModelTask.Transcription, m.Task));
        Assert.Equal(
            catalog.Models.Count,
            catalog.TranscriptionModels.Count + catalog.DiarisationModels.Count + catalog.TranslationModels.Count);
    }

    [Fact]
    public void ADiarisationEntryIsKeptOutOfEveryAsrPath()
    {
        const string Json = """
            {"models":[
            {"id":"diar","task":"diarisation","family":"s","displayName":"D","quantisation":"int8","fileName":"d.onnx","url":"https://e.com/d","license":"L","attributionId":"x","recommended":true},
            {"id":"asr","family":"f","displayName":"A","quantisation":"q","fileName":"a.gguf","url":"https://e.com/a","license":"L","attributionId":"x"}]}
            """;

        var catalog = ModelCatalog.Parse(Json);

        // Listed first and marked recommended, and still not what transcribe resolves to.
        Assert.Equal("asr", catalog.Recommended?.Id);
        Assert.Equal(["asr"], catalog.TranscriptionModels.Select(m => m.Id));
        Assert.Equal(["diar"], catalog.DiarisationModels.Select(m => m.Id));
        Assert.Equal(ModelTask.Diarisation, catalog.Get("diar").Task);
        Assert.Equal(ModelTask.Transcription, catalog.Get("asr").Task);   // absent means transcription
    }

    [Fact]
    public void ATranslationEntryIsNeitherAnAsrModelNorADiariser()
    {
        // The second time this discriminator has had to hold: a translation model reads text and
        // returns text, so offering it to `transcribe` or to the speaker opt-in would load an ONNX
        // graph as GGUF weights or as a diariser. Both lists have to exclude it by construction
        // rather than by anybody remembering to filter.
        const string Json = """
            {"models":[
            {"id":"mt","task":"translation","family":"opus-mt","displayName":"T","quantisation":"int8","fileName":"t.onnx","url":"https://e.com/t","license":"L","attributionId":"x","recommended":true},
            {"id":"diar","task":"diarisation","family":"s","displayName":"D","quantisation":"int8","fileName":"d.onnx","url":"https://e.com/d","license":"L","attributionId":"x"},
            {"id":"asr","family":"f","displayName":"A","quantisation":"q","fileName":"a.gguf","url":"https://e.com/a","license":"L","attributionId":"x"}]}
            """;

        var catalog = ModelCatalog.Parse(Json);

        Assert.Equal(ModelTask.Translation, catalog.Get("mt").Task);
        Assert.Equal(["mt"], catalog.TranslationModels.Select(m => m.Id));

        // Listed first and marked recommended, and still not what an unspecified --model resolves to.
        Assert.Equal("asr", catalog.Recommended?.Id);
        Assert.DoesNotContain(catalog.TranscriptionModels, m => m.Id == "mt");
        Assert.DoesNotContain(catalog.DiarisationModels, m => m.Id == "mt");
    }

    [Theory]
    [InlineData("\"diarization\"")]      // a misspelling
    [InlineData("\"translate\"")]        // the flag's name, not the task's
    [InlineData("null")]                  // present, and not a string
    [InlineData("1")]
    [InlineData("[\"diarisation\"]")]
    public void ATaskThatIsNotOneOfTheKnownWordsIsRefusedRatherThanDefaulted(string task)
    {
        // Only absence means transcription. Defaulting a broken value would list a diarisation or
        // translation model as an ASR model, which is the one thing the field exists to stop.
        var json = $$"""
            {"models":[
            {"id":"d","task":{{task}},"family":"s","displayName":"D","quantisation":"q","fileName":"d.onnx","url":"https://e.com/d","license":"L","attributionId":"x"}]}
            """;

        var ex = Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(json));
        Assert.Contains(
            "known tasks are transcription, diarisation and translation", ex.Message, StringComparison.Ordinal);
    }
}

public class LocalModelStoreTests
{
    [Fact]
    public void ModelsAreNotStoredInTheInstallDirectory()
    {
        var store = new LocalModelStore();

        // Weights in the install directory are destroyed by every update and uninstall, which
        // turns each patch into a 670 MB re-download.
        Assert.DoesNotContain(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), store.RootDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentVariableOverridesTheLocation()
    {
        var previous = Environment.GetEnvironmentVariable(LocalModelStore.DirectoryEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(LocalModelStore.DirectoryEnvironmentVariable, "/tmp/uindosill-test-models");
            Assert.Equal("/tmp/uindosill-test-models", new LocalModelStore().RootDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalModelStore.DirectoryEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void ExplicitDirectoryWinsOverEverything()
    {
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);

        Assert.Equal(temp.Path, store.RootDirectory);
        Assert.Empty(store.ListInstalled());
    }

    [Fact]
    public void SideloadedFilesAreListedAndFlagged()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "something-else.gguf"), "not really a model");

        var store = new LocalModelStore(temp.Path);
        var installed = Assert.Single(store.ListInstalled(ModelCatalog.Default));

        Assert.True(installed.IsSideloaded);
    }

    [Fact]
    public void SideloadedFilesCanBeRemoved()
    {
        // Listing them without being able to delete them is where this stood until 2026-08-23:
        // `uindosill models` reported several gigabytes under a heading of their own and neither
        // surface could do anything about them, because there is no descriptor to pass Remove.
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "withdrawn-quantisation.gguf");
        File.WriteAllText(path, "not really a model");

        var store = new LocalModelStore(temp.Path);

        Assert.True(store.RemoveSideloaded("withdrawn-quantisation.gguf", ModelCatalog.Default));
        Assert.False(File.Exists(path));

        // Gone already is not an error, it is the same answer Remove gives.
        Assert.False(store.RemoveSideloaded("withdrawn-quantisation.gguf", ModelCatalog.Default));
    }

    [Theory]
    // A catalogue entry is that entry's to remove, through its descriptor — otherwise the two
    // removal paths could come to disagree about what an entry consists of.
    [InlineData("tdt-0.6b-v3-f16.gguf")]
    // Not weights at all. A stray file beside the models is not this method's to delete.
    [InlineData("notes.txt")]
    // Anything carrying a separator is refused rather than normalised, because this deletes.
    [InlineData("../outside.gguf")]
    [InlineData("nested/inside.gguf")]
    public void RemoveSideloadedRefusesWhatIsNotItsToDelete(string name)
    {
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);

        // The catalogue-claimed name is written so the refusal is about the claim rather than
        // about the file being absent.
        var claimed = Path.Combine(temp.Path, "tdt-0.6b-v3-f16.gguf");
        File.WriteAllText(claimed, "weights");

        Assert.False(store.RemoveSideloaded(name, ModelCatalog.Default));
        Assert.True(File.Exists(claimed));
    }
}

public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KiB")]
    [InlineData(1441046400, "1.34 GiB")]
    public void SizesReadTheWayTheCommandLinePrintsThem(long value, string expected) =>
        // One implementation for both surfaces: the same file reported as 1.34 GiB by
        // `uindosill models` and as something else by the Models tab is a disagreement a user has
        // to resolve by guessing which one is lying.
        Assert.Equal(expected, ByteSize.Describe(value));
}

public class ModelInstallerTests
{
    private static ModelDescriptor Descriptor(string? sha256, long? size = null) => Descriptor(
    [
        new ModelFile
        {
            FileName = "test.gguf",
            Url = new Uri("https://example.invalid/test.gguf"),
            Sha256 = sha256,
            SizeBytes = size,
        },
    ]);

    private static ModelDescriptor Descriptor(IReadOnlyList<ModelFile> files, string? directory = null) => new()
    {
        Id = "test-model",
        Family = "test",
        DisplayName = "Test",
        Quantisation = "q8_0",
        Files = files,
        DirectoryName = directory,
        License = "CC-BY-4.0",
        AttributionId = Attributions.ParakeetTdt06BV3,
    };

    private static string Sha256Of(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [Fact]
    public async Task UnpinnedDigestIsRefusedByDefault()
    {
        using var temp = new TempDirectory();
        using var installer = new ModelInstaller(new LocalModelStore(temp.Path), new HttpClient(new StubHandler([])));

        var exception = await Assert.ThrowsAsync<ModelInstallException>(
            () => installer.InstallAsync(Descriptor(sha256: null)));

        Assert.Contains("no pinned SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifiedDownloadIsInstalledAtomically()
    {
        var payload = Encoding.UTF8.GetBytes(new string('x', 4096));
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);
        var handler = new StubHandler(payload);

        using var installer = new ModelInstaller(store, new HttpClient(handler));
        var result = await installer.InstallAsync(Descriptor(Sha256Of(payload)));

        Assert.Equal(Sha256Of(payload), Assert.Single(result.Files).Sha256);
        Assert.True(File.Exists(result.Model.Path));
        Assert.False(File.Exists(result.Model.Path + ".part"));
        Assert.False(File.Exists(result.Model.Path + ".part.json"));
    }

    [Fact]
    public async Task DigestMismatchDiscardsTheDownload()
    {
        var payload = Encoding.UTF8.GetBytes("actual content");
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);

        using var installer = new ModelInstaller(store, new HttpClient(new StubHandler(payload)));
        var descriptor = Descriptor(Sha256Of(Encoding.UTF8.GetBytes("different content")));

        var exception = await Assert.ThrowsAsync<ModelInstallException>(() => installer.InstallAsync(descriptor));

        Assert.Contains("failed verification", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(store.PathFor(descriptor)));
        Assert.False(File.Exists(store.PathFor(descriptor) + ".part"));
    }

    [Fact]
    public async Task SizeMismatchIsRefusedEvenWhenNoDigestIsPinned()
    {
        var payload = Encoding.UTF8.GetBytes("short");
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);
        var descriptor = Descriptor(sha256: null, size: 999_999);

        using var installer = new ModelInstaller(store, new HttpClient(new StubHandler(payload)));

        var exception = await Assert.ThrowsAsync<ModelInstallException>(
            () => installer.InstallAsync(descriptor, new ModelInstallOptions { AllowUnverified = true }));

        Assert.Contains("bytes but the catalogue pins", exception.Message, StringComparison.Ordinal);

        // Discarded, like a digest mismatch is. A complete .part whose length disagrees with the pin
        // is a file the resume path treats as finished: the next attempt asks for a range starting
        // at its end, the server answers 416, and the user is stuck on "416
        // RequestedRangeNotSatisfiable" — which names nothing about the real cause — for ever, with
        // no way out but deleting a file in a directory nobody told them about.
        Assert.False(File.Exists(store.PathFor(descriptor) + ".part"));
        Assert.False(File.Exists(store.PathFor(descriptor) + ".part.json"));
    }

    [Fact]
    public async Task ARefusedSizeDoesNotPoisonTheNextAttempt()
    {
        // The failure this guards is only visible on the SECOND call: the first reports the size
        // clearly, and it was the leftover .part that turned every later attempt into a 416. So the
        // assertion is that a corrected catalogue installs, not that the first attempt fails well.
        var payload = Encoding.UTF8.GetBytes(new string('z', 4096));
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);

        using var installer = new ModelInstaller(store, new HttpClient(new StubHandler(payload)));

        await Assert.ThrowsAsync<ModelInstallException>(
            () => installer.InstallAsync(
                Descriptor(sha256: null, size: 999_999),
                new ModelInstallOptions { AllowUnverified = true }));

        var corrected = Descriptor(Sha256Of(payload), size: payload.Length);
        var result = await installer.InstallAsync(corrected, ModelInstallOptions.Default);

        Assert.Equal(payload.Length, result.Model.SizeBytes);
        Assert.True(File.Exists(store.PathFor(corrected)));
    }

    [Fact]
    public async Task InterruptedDownloadResumesFromWhereItStopped()
    {
        var payload = Encoding.UTF8.GetBytes(new string('y', 8192));
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);
        var descriptor = Descriptor(Sha256Of(payload));

        // First attempt dies partway through, exactly as a dropped connection would.
        var failing = new StubHandler(payload) { FailAfterBytes = 3000 };
        using (var installer = new ModelInstaller(store, new HttpClient(failing)))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => installer.InstallAsync(descriptor));
        }

        var partPath = store.PathFor(descriptor) + ".part";
        Assert.True(File.Exists(partPath), "no partial file was kept, so the retry would restart from zero");
        var partial = new FileInfo(partPath).Length;
        Assert.True(partial > 0);

        var resuming = new StubHandler(payload);
        using (var installer = new ModelInstaller(store, new HttpClient(resuming)))
        {
            var result = await installer.InstallAsync(descriptor);
            Assert.True(result.Resumed, "the second attempt did not resume");
            Assert.Equal(Sha256Of(payload), Assert.Single(result.Files).Sha256);
        }

        Assert.Equal(partial, resuming.LastRangeFrom);
        Assert.Equal(payload.Length, new FileInfo(store.PathFor(descriptor)).Length);
    }

    [Fact]
    public async Task ResumeIsAbandonedWhenTheUrlChanged()
    {
        var payload = Encoding.UTF8.GetBytes(new string('z', 2048));
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);
        var descriptor = Descriptor(Sha256Of(payload));

        // A leftover .part from a different URL must never be spliced onto a new download.
        File.WriteAllBytes(store.PathFor(descriptor) + ".part", [1, 2, 3, 4]);
        File.WriteAllText(store.PathFor(descriptor) + ".part.json", """{"url":"https://elsewhere.invalid/other"}""");

        var handler = new StubHandler(payload);
        using var installer = new ModelInstaller(store, new HttpClient(handler));
        var result = await installer.InstallAsync(descriptor);

        Assert.False(result.Resumed);
        Assert.Equal(Sha256Of(payload), Assert.Single(result.Files).Sha256);
    }

    [Fact]
    public async Task AlreadyInstalledFileIsNotDownloadedAgain()
    {
        var payload = Encoding.UTF8.GetBytes("already here");
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);
        var descriptor = Descriptor(Sha256Of(payload));

        Directory.CreateDirectory(store.RootDirectory);
        await File.WriteAllBytesAsync(store.PathFor(descriptor), payload);

        var handler = new StubHandler(payload);
        using var installer = new ModelInstaller(store, new HttpClient(handler));
        var result = await installer.InstallAsync(descriptor);

        Assert.True(result.AlreadyPresent);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task CorruptExistingFileIsReplaced()
    {
        var payload = Encoding.UTF8.GetBytes("the good bytes");
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);
        var descriptor = Descriptor(Sha256Of(payload));

        Directory.CreateDirectory(store.RootDirectory);
        await File.WriteAllTextAsync(store.PathFor(descriptor), "corrupted");

        using var installer = new ModelInstaller(store, new HttpClient(new StubHandler(payload)));
        var result = await installer.InstallAsync(descriptor);

        Assert.False(result.AlreadyPresent);
        Assert.Equal(payload, await File.ReadAllBytesAsync(store.PathFor(descriptor)));
    }

    [Fact]
    public async Task NotFoundOnAnUnverifiedEntryExplainsWhy()
    {
        using var temp = new TempDirectory();
        using var installer = new ModelInstaller(
            new LocalModelStore(temp.Path),
            new HttpClient(new StubHandler([]) { Status = HttpStatusCode.NotFound }));

        var exception = await Assert.ThrowsAsync<ModelInstallException>(
            () => installer.InstallAsync(Descriptor(sha256: null), new ModelInstallOptions { AllowUnverified = true }));

        Assert.Contains("marked unverified", exception.Message, StringComparison.Ordinal);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public StubHandler(byte[] payload) => _payload = payload;

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public int? FailAfterBytes { get; init; }

        public int RequestCount { get; private set; }

        public long LastRangeFrom { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;

            if (Status != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(Status));
            }

            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            LastRangeFrom = from;

            var body = _payload[(int)from..];
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = FailAfterBytes is { } limit
                    ? new StreamContent(new FailingStream(body, limit))
                    : new ByteArrayContent(body),
            };

            if (from > 0)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, _payload.Length - 1, _payload.Length);
            }

            return Task.FromResult(response);
        }
    }

    /// <summary>A body that dies partway, the way a dropped connection does.</summary>
    private sealed class FailingStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _limit;
        private int _position;

        public FailingStream(byte[] data, int limit)
        {
            _data = data;
            _limit = limit;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _data.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _limit)
            {
                throw new IOException("connection reset");
            }

            var take = Math.Min(Math.Min(count, 512), _limit - _position);
            Array.Copy(_data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uindosill-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}
