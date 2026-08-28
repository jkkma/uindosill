using System.Net;
using System.Security.Cryptography;
using System.Text;
using Parakeet.Core.Licensing;
using Parakeet.Core.Models;

namespace Parakeet.Core.Tests;

/// <summary>
/// The catalogue, the store and the installer against an entry that is more than one file.
/// </summary>
/// <remarks>
/// <para>
/// The shape shipped before its first user, which is the same order the task discriminator and the
/// diarisation entry arrived in, twice, and for the same reason: the code that has to understand a
/// shape ships before the entry that has that shape, so no build can ever meet one it does not
/// know. The user arrived on 2026-08-20 — the nine-file ONNX translation entry, once there was a
/// decode loop to read it. These tests still build their own manifests rather than leaning on the
/// shipped one, because the edges worth testing are the malformed ones the manifest will never
/// contain; exactly one of them looks at the real entry, and it is named for it.
/// </para>
/// <para>
/// The claim under all of it: <b>a multi-file model is installed or it is not.</b> There is no
/// third state on disk that an engine could be handed.
/// </para>
/// </remarks>
public class MultiFileModelTests
{
    private const string Digest0 = "0000000000000000000000000000000000000000000000000000000000000000";

    private static string Sha256Of(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string Manifest(string entry) => $$"""
        {
          "schema": 1,
          "models": [ {{entry}} ]
        }
        """;

    /// <summary>A three-file entry, pinned, in a directory of its own.</summary>
    private static string ThreeFileEntry(string directory = "opus-mt-en") => $$"""
        {
          "id": "multi", "task": "translation", "family": "test", "displayName": "Multi",
          "quantisation": "int8", "license": "Apache-2.0", "attributionId": "{{Attributions.ParakeetTdt06BV3}}",
          "verified": true,
          "directory": "{{directory}}",
          "files": [
            { "fileName": "encoder.onnx", "url": "https://example.invalid/encoder.onnx", "sizeBytes": 10, "sha256": "{{Digest0}}" },
            { "fileName": "decoder.onnx", "url": "https://example.invalid/decoder.onnx", "sizeBytes": 20, "sha256": "{{Digest0.Replace("0", "1", StringComparison.Ordinal)}}" },
            { "fileName": "vocab.json",   "url": "https://example.invalid/vocab.json",   "sizeBytes": 30, "sha256": "{{Digest0.Replace("0", "2", StringComparison.Ordinal)}}" }
          ]
        }
        """;

    // ----------------------------------------------------------------- parsing

    [Fact]
    public void AMultiFileEntryParsesIntoItsFilesAndItsDirectory()
    {
        var model = Assert.Single(ModelCatalog.Parse(Manifest(ThreeFileEntry())).Models);

        Assert.True(model.IsMultiFile);
        Assert.Equal("opus-mt-en", model.DirectoryName);
        Assert.Equal("opus-mt-en", model.StorageName);
        Assert.Equal(3, model.Files.Count);
        Assert.Equal(["encoder.onnx", "decoder.onnx", "vocab.json"], model.Files.Select(f => f.FileName));

        // Manifest order is preserved rather than sorted: it is the order the installer fetches in,
        // and a release engineer reading a progress log should see the order they wrote.
        Assert.Equal(60, model.TotalSizeBytes);
        Assert.True(model.IsFullyPinned);
    }

    [Fact]
    public void ASingleFileEntryStillParsesTheWayItAlwaysDid()
    {
        var model = Assert.Single(ModelCatalog.Parse(Manifest($$"""
            {
              "id": "single", "family": "test", "displayName": "Single", "quantisation": "q8_0",
              "license": "CC-BY-4.0", "attributionId": "{{Attributions.ParakeetTdt06BV3}}", "verified": true,
              "fileName": "test.gguf", "url": "https://example.invalid/test.gguf",
              "sizeBytes": 1024, "sha256": "{{Digest0}}"
            }
            """)).Models);

        Assert.False(model.IsMultiFile);
        Assert.Null(model.DirectoryName);
        Assert.Equal("test.gguf", model.StorageName);
        Assert.Equal(1024, model.TotalSizeBytes);

        var file = Assert.Single(model.Files);
        Assert.Equal("test.gguf", file.FileName);
        Assert.Equal(1024, file.SizeBytes);
    }

    [Fact]
    public void TheShippedMultiFileEntryIsTheTranslationOne()
    {
        // This replaces a tripwire that asserted every shipped entry was still a single file, whose
        // whole job was to fail the day one was not. It failed on 2026-08-20. What is asserted now
        // is the thing that tripwire was protecting: the schema's users are well formed — because
        // until the translation entry landed, twenty-four tests held the shape up against nothing
        // but hand-written JSON.
        //
        // **Two users since 2026-08-26**, the second being the pyannote diariser — DiariZen until
        // 2026-08-27 — whose own shape is asserted below rather than here. This half stays about
        // the translation entry because its nine files are what the schema was built for.
        // Manifest order: the second diariser sits beside the other one, ahead of the translator.
        // **Four users since 2026-08-27**, the new two being the answering entries: each installs
        // its weights and the drafting head beside them, and a `files` array is what makes the pair
        // one entry. They must have directories of their own — they ship the same head under the
        // same name and would overwrite each other at the store root.
        // **Five users since 2026-08-28**, the new one being the dense 12B, which installs its
        // weights and its own drafting head the same way and for the same reason.
        Assert.Equal(
            new[]
            {
                "pyannote-speaker-diarization-community-1",
                "opus-mt-tc-bible-big-mul-en-fp32",
                "gemma-4-12b-it-qat-ud-q4-k-xl",
                "gemma-4-26b-a4b-it-ud-q4-k-xl",
                "gemma-4-26b-a4b-it-ud-iq4-xs",
            },
            ModelCatalog.Default.Models.Where(m => m.IsMultiFile).Select(m => m.Id));

        var multiFile = ModelCatalog.Default.Get("opus-mt-tc-bible-big-mul-en-fp32");

        Assert.Equal("opus-mt-tc-bible-big-mul-en-fp32", multiFile.Id);
        Assert.Equal(ModelTask.Translation, multiFile.Task);
        Assert.Equal("opus-mt-tc-bible-big-mul-en", multiFile.DirectoryName);
        Assert.Equal(multiFile.DirectoryName, multiFile.StorageName);

        // Nine files, and the count is the point: the route was planned as five before the export
        // was run, and the tokenizer turned out to be five files on its own.
        Assert.Equal(9, multiFile.Files.Count);
        Assert.Equal(1_435_604_524, multiFile.TotalSizeBytes);

        // Every one of the nine has to be there, because a partial set loads until it does not.
        var names = multiFile.Files.Select(f => f.FileName).ToList();
        Assert.Equal(
            [
                "config.json", "decoder_model_merged.onnx", "encoder_model.onnx", "generation_config.json",
                "source.spm", "special_tokens_map.json", "target.spm", "tokenizer_config.json", "vocab.json",
            ],
            names.Order(StringComparer.Ordinal));

        // Two of those names — config.json and vocab.json — are exactly why `directory` is required.
        Assert.Contains("config.json", names);
        Assert.Contains("vocab.json", names);

        Assert.True(multiFile.IsFullyPinned);
        Assert.All(multiFile.Files, file => Assert.True(file.SizeBytes > 0));
    }

    [Fact]
    public void FilesWithoutADirectoryIsRefused()
    {
        var entry = ThreeFileEntry().Replace("\"directory\": \"opus-mt-en\",", string.Empty, StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(Manifest(entry)));

        // The message has to say why, because the fix is not obvious from "invalid manifest": these
        // files would otherwise land in a directory shared with every other entry, and config.json
        // is not a name one model can own.
        Assert.Contains("must name a 'directory'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryWithBothAnInlineFileNameAndAFilesArrayIsRefused()
    {
        var entry = ThreeFileEntry().Replace(
            "\"directory\": \"opus-mt-en\",",
            "\"directory\": \"opus-mt-en\", \"fileName\": \"legacy.gguf\", \"url\": \"https://example.invalid/legacy.gguf\",",
            StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(Manifest(entry)));

        // Refused rather than resolved. "The inline one is a fourth member" and "the inline one is
        // a leftover to ignore" are both readable from the same JSON and differ by a whole file.
        Assert.Contains("Use one", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatedFileNameWithinOneEntryIsRefused()
    {
        var entry = ThreeFileEntry().Replace("\"fileName\": \"vocab.json\"", "\"fileName\": \"encoder.onnx\"", StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(Manifest(entry)));

        // Whichever downloads last wins, and the digest that was checked is not the one left on
        // disk — a corruption that verifies clean at install time and fails at load time.
        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("has/slash")]
    [InlineData("has\\\\backslash")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    public void ADirectoryThatIsNotABareNameIsRefused(string directory)
    {
        // A manifest is data a release engineer edits, and `"directory": "../.."` would have the
        // installer delete a directory outside the store on remove.
        Assert.Throws<InvalidDataException>(
            () => ModelCatalog.Parse(Manifest(ThreeFileEntry(directory))));
    }

    [Fact]
    public void TwoEntriesStoredUnderTheSameNameAreRefused()
    {
        var manifest = $$"""
            {
              "schema": 1,
              "models": [
                {{ThreeFileEntry("shared")}},
                {
                  "id": "other", "family": "test", "displayName": "Other", "quantisation": "q8_0",
                  "license": "CC-BY-4.0", "attributionId": "{{Attributions.ParakeetTdt06BV3}}",
                  "fileName": "shared", "url": "https://example.invalid/shared", "sha256": "{{Digest0}}"
                }
              ]
            }
            """;

        var exception = Assert.Throws<InvalidDataException>(() => ModelCatalog.Parse(manifest));

        // A directory and a file compete for the same name in the same namespace: installing the
        // second would fail or clobber, and removing either would take both.
        Assert.Contains("more than one entry stored as", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryIsUnpinnedWhenAnySingleFileIsUnpinned()
    {
        var entry = ThreeFileEntry().Replace($"\"sha256\": \"{Digest0.Replace("0", "2", StringComparison.Ordinal)}\"", "\"sizeBytes\": 30", StringComparison.Ordinal);
        var model = Assert.Single(ModelCatalog.Parse(Manifest(entry)).Models);

        // Eight pinned files out of nine is not a pinned entry. The whole point of the aggregate is
        // that it is an AND: a set is as checked as its least-checked member.
        Assert.False(model.IsFullyPinned);
        Assert.Equal(2, model.Files.Count(f => f.Sha256 is not null));
    }

    [Fact]
    public void AnEntryTotalsNothingWhenAnySingleSizeIsMissing()
    {
        var entry = ThreeFileEntry().Replace("\"sizeBytes\": 30, ", string.Empty, StringComparison.Ordinal);
        var model = Assert.Single(ModelCatalog.Parse(Manifest(entry)).Models);

        // Null rather than 30: a total that silently omits a file is smaller than the truth, and
        // small is the wrong direction for a number a user reads before starting a download.
        Assert.Null(model.TotalSizeBytes);
    }

    // ------------------------------------------------------------------- store

    [Fact]
    public void TheStoreResolvesADirectoryForTheEntryAndAPathPerFile()
    {
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);
        var model = Assert.Single(ModelCatalog.Parse(Manifest(ThreeFileEntry())).Models);

        Assert.Equal(Path.Combine(temp.Path, "opus-mt-en"), store.PathFor(model));
        Assert.Equal(
            Path.Combine(temp.Path, "opus-mt-en", "encoder.onnx"),
            store.PathFor(model, model.Files[0]));
    }

    [Fact]
    public void AMissingFileMakesTheWholeEntryNotInstalled()
    {
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);
        var model = Assert.Single(ModelCatalog.Parse(Manifest(ThreeFileEntry())).Models);

        Directory.CreateDirectory(store.PathFor(model));
        Assert.False(store.IsInstalled(model));

        foreach (var file in model.Files)
        {
            File.WriteAllText(store.PathFor(model, file), "content");
        }

        Assert.True(store.IsInstalled(model));

        // Deleting one by hand is the only way to reach this state — the installer never creates
        // it — and reporting the entry as installed would hand an engine a set it cannot load.
        File.Delete(store.PathFor(model, model.Files[1]));
        Assert.False(store.IsInstalled(model));
    }

    [Fact]
    public void RemoveTakesTheWholeDirectory()
    {
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);
        var model = Assert.Single(ModelCatalog.Parse(Manifest(ThreeFileEntry())).Models);

        Assert.False(store.Remove(model));

        Directory.CreateDirectory(store.PathFor(model));
        foreach (var file in model.Files)
        {
            File.WriteAllText(store.PathFor(model, file), "content");
        }

        // Something the manifest does not list, which a later revision of the entry might add.
        File.WriteAllText(Path.Combine(store.PathFor(model), "ort_config.json"), "{}");

        Assert.True(store.Remove(model));
        Assert.False(Directory.Exists(store.PathFor(model)));
    }

    [Fact]
    public void ListInstalledReportsTheEntryOnceAndIgnoresAStagingDirectory()
    {
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);
        var catalog = ModelCatalog.Parse(Manifest(ThreeFileEntry()));
        var model = Assert.Single(catalog.Models);

        // A staging directory, exactly as an interrupted install leaves one, with a plausible
        // graph inside it. Nothing here is installed.
        Directory.CreateDirectory(store.PathFor(model) + ".part");
        File.WriteAllText(Path.Combine(store.PathFor(model) + ".part", "encoder.onnx"), "partial");
        Assert.Empty(store.ListInstalled(catalog));

        Directory.CreateDirectory(store.PathFor(model));
        foreach (var file in model.Files)
        {
            File.WriteAllText(store.PathFor(model, file), "content");
        }

        var installed = Assert.Single(store.ListInstalled(catalog));
        Assert.Equal("multi", installed.Id);
        Assert.Equal(store.PathFor(model), installed.Path);
        Assert.Equal(3 * "content".Length, installed.SizeBytes);
        Assert.False(installed.IsSideloaded);
    }

    // --------------------------------------------------------------- installing

    [Fact]
    public async Task EveryFileIsFetchedVerifiedAndTheDirectoryAppearsAtTheEnd()
    {
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);
        var handler = new PerUrlHandler();
        var model = ThreeRealFiles(handler);

        using var installer = new ModelInstaller(store, new HttpClient(handler));
        var seen = new List<ModelInstallProgress>();
        var result = await installer.InstallAsync(model, progress: new SynchronousProgress(seen.Add));

        Assert.Equal(3, result.Files.Count);
        Assert.Equal(["a.onnx", "b.onnx", "c.json"], result.Files.Select(f => f.FileName));
        Assert.True(store.IsInstalled(model));
        Assert.False(Directory.Exists(store.PathFor(model) + ".part"));

        // Nothing left behind inside the installed directory either: three files, no .part, no
        // .part.json. A stray metadata file would make the next install's resume logic read it.
        Assert.Equal(3, Directory.GetFiles(store.PathFor(model)).Length);

        // Progress climbs once across the entry rather than three times from zero, which is what a
        // progress bar bound to Fraction needs to not jump backwards twice.
        var byteCounts = seen.Select(p => p.BytesCompleted).ToList();
        Assert.Equal(byteCounts.OrderBy(b => b), byteCounts);
        Assert.Equal(3, seen.Max(p => p.FileCount));
        Assert.Contains(seen, p => p.CurrentFile == "b.onnx");
    }

    [Fact]
    public async Task AFailureMidwayLeavesNothingThatLooksInstalled()
    {
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);
        var handler = new PerUrlHandler();
        var model = ThreeRealFiles(handler, breakSecondFileDigest: true);

        using var installer = new ModelInstaller(store, new HttpClient(handler));
        var exception = await Assert.ThrowsAsync<ModelInstallException>(() => installer.InstallAsync(model));

        // The message names the file. "The download failed" for a nine-file entry is a message
        // about nine possible files.
        Assert.Contains("'b.onnx'", exception.Message, StringComparison.Ordinal);

        Assert.False(store.IsInstalled(model));
        Assert.False(Directory.Exists(store.PathFor(model)));

        // The staging directory survives, holding the one good file. That is an incomplete
        // download, which resumes; it is not an incomplete model, which nothing can load.
        Assert.True(Directory.Exists(store.PathFor(model) + ".part"));
        Assert.True(File.Exists(Path.Combine(store.PathFor(model) + ".part", "a.onnx")));
    }

    [Fact]
    public async Task AGoodFileAlreadyStagedIsNotFetchedAgain()
    {
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);
        var handler = new PerUrlHandler();
        var model = ThreeRealFiles(handler, breakSecondFileDigest: true);

        using var installer = new ModelInstaller(store, new HttpClient(handler));
        await Assert.ThrowsAsync<ModelInstallException>(() => installer.InstallAsync(model));

        Assert.Equal(1, handler.RequestsByFileName.GetValueOrDefault("a.onnx"));

        // Second attempt against the same store and the same URLs, with the entry's pins now
        // agreeing with what the remote serves — a corrected catalogue, or an upstream that has
        // stopped serving a bad file. The first file is already staged and correct: refetching it
        // would throw away a good gigabyte because a later file failed, which for the nine-file
        // ONNX route is the whole point of staging.
        var repaired = ThreeRealFiles(handler);
        var result = await installer.InstallAsync(repaired);

        Assert.Equal(1, handler.RequestsByFileName["a.onnx"]);
        Assert.Equal(3, result.Files.Count);
        Assert.True(store.IsInstalled(repaired));
        Assert.True(result.Resumed);
    }

    [Fact]
    public async Task AnAlreadyInstalledEntryIsVerifiedRatherThanRefetched()
    {
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);
        var handler = new PerUrlHandler();
        var model = ThreeRealFiles(handler);

        using var installer = new ModelInstaller(store, new HttpClient(handler));
        await installer.InstallAsync(model);
        var requestsAfterFirst = handler.TotalRequests;

        var second = await installer.InstallAsync(model);

        Assert.True(second.AlreadyPresent);
        Assert.Equal(3, second.Files.Count);
        Assert.Equal(requestsAfterFirst, handler.TotalRequests);
    }

    [Fact]
    public async Task ATamperedInstalledFileReplacesTheWholeDirectory()
    {
        using var temp = new TempDirectory();
        var store = new LocalModelStore(temp.Path);
        var handler = new PerUrlHandler();
        var model = ThreeRealFiles(handler);

        using var installer = new ModelInstaller(store, new HttpClient(handler));
        await installer.InstallAsync(model);

        File.WriteAllText(store.PathFor(model, model.Files[2]), "tampered");
        var stray = Path.Combine(store.PathFor(model), "stray.txt");
        File.WriteAllText(stray, "left over from something");

        var result = await installer.InstallAsync(model);

        Assert.False(result.AlreadyPresent);
        Assert.True(store.IsInstalled(model));

        // All three replaced, not just the one that failed: keeping the two that happened to
        // verify would leave a mixed set nobody has ever tested together. The stray goes too.
        Assert.Equal(3, Directory.GetFiles(store.PathFor(model)).Length);
        Assert.False(File.Exists(stray));
    }

    [Fact]
    public async Task AnEntryWithOneUnpinnedFileNeedsTheUnverifiedOptIn()
    {
        using var temp = new TempDirectory();
        var handler = new PerUrlHandler();
        var model = ThreeRealFiles(handler, unpinLastFile: true);

        using var installer = new ModelInstaller(new LocalModelStore(temp.Path), new HttpClient(handler));
        var exception = await Assert.ThrowsAsync<ModelInstallException>(() => installer.InstallAsync(model));

        // The count is in the message because "no pinned SHA-256" on a nine-file entry reads as
        // "none of them are pinned", which would send a release engineer looking in nine places.
        Assert.Contains("1 of its 3 files (c.json)", exception.Message, StringComparison.Ordinal);

        var result = await installer.InstallAsync(model, new ModelInstallOptions { AllowUnverified = true });
        Assert.Equal(3, result.Files.Count);
        Assert.All(result.Files, file => Assert.Equal(64, file.Sha256.Length));
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>A three-file entry whose pins match what <paramref name="handler"/> will serve.</summary>
    private static ModelDescriptor ThreeRealFiles(
        PerUrlHandler handler, bool breakSecondFileDigest = false, bool unpinLastFile = false)
    {
        var bodies = new (string Name, byte[] Body)[]
        {
            ("a.onnx", Encoding.UTF8.GetBytes(new string('a', 512))),
            ("b.onnx", Encoding.UTF8.GetBytes(new string('b', 1024))),
            ("c.json", Encoding.UTF8.GetBytes("{\"vocab\":1}")),
        };

        var files = new List<ModelFile>();
        foreach (var (name, body) in bodies)
        {
            var url = new Uri($"https://example.invalid/{name}");
            handler.Serve(url, name, body);

            files.Add(new ModelFile
            {
                FileName = name,
                Url = url,
                SizeBytes = body.Length,
                Sha256 = unpinLastFile && name == "c.json" ? null : Sha256Of(body),
            });
        }

        if (breakSecondFileDigest)
        {
            // The pin says one thing and the remote serves another, which is what a corrupted or
            // swapped upstream file looks like from here.
            files[1] = files[1] with { Sha256 = Sha256Of(Encoding.UTF8.GetBytes("something else")) };
        }

        return new ModelDescriptor
        {
            Id = "multi",
            Task = ModelTask.Translation,
            Family = "test",
            DisplayName = "Multi",
            Quantisation = "int8",
            Files = files,
            DirectoryName = "multi-model",
            Verified = true,
            License = "Apache-2.0",
            AttributionIds = [Attributions.ParakeetTdt06BV3],
        };
    }

    /// <summary>Serves a different body per URL, and counts what was asked for.</summary>
    private sealed class PerUrlHandler : HttpMessageHandler
    {
        private readonly Dictionary<Uri, (string Name, byte[] Body)> _bodies = [];

        public Dictionary<string, int> RequestsByFileName { get; } = new(StringComparer.Ordinal);

        public int TotalRequests { get; private set; }

        public void Serve(Uri url, string name, byte[] body) => _bodies[url] = (name, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            TotalRequests++;

            if (!_bodies.TryGetValue(request.RequestUri!, out var served))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            RequestsByFileName[served.Name] = RequestsByFileName.GetValueOrDefault(served.Name) + 1;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(served.Body),
            });
        }
    }

    /// <summary>
    /// <see cref="Progress{T}"/> posts to the synchronisation context, so a test that reads the
    /// reports after awaiting can miss most of them. This one just calls.
    /// </summary>
    private sealed class SynchronousProgress(Action<ModelInstallProgress> onReport) : IProgress<ModelInstallProgress>
    {
        public void Report(ModelInstallProgress value) => onReport(value);
    }
}
