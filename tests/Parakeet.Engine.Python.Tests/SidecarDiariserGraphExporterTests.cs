using Parakeet.Engine.Python;

namespace Parakeet.Engine.Python.Tests;

/// <summary>The export transaction, with real child-process file handles and no model weights.</summary>
public sealed class SidecarDiariserGraphExporterTests
{
    private const string Result = """{"id":{id},"type":"result","manifest":{}}""";
    private const string Error = """{"id":{id},"type":"error","kind":"model","message":"export failed"}""";
    private const string Progress = """{"id":{id},"type":"progress","completed":1,"total":3}""";
    private static readonly string[] Names = ["segmentation.onnx", "embedding.onnx", "manifest.json"];

    [Fact]
    public async Task ACompleteExportIsPublishedAfterTheChildReleasesItsFiles()
    {
        var model = NewModel();
        using var fake = FakeSidecarProcess.Scripted(Script(new
        {
            op = "exportDiariserGraphs",
            files = Files(holdOpen: true),
            emit = new[] { Result },
        }));
        var exporter = new SidecarDiariserGraphExporter(() => new PythonSidecar(fake.Resolution));

        var directory = await exporter.ExportAsync(model);

        Assert.Equal(DiariserGraphs.DirectoryFor(model), directory);
        Assert.True(DiariserGraphs.AreInstalled(model));
        AssertFiles(directory, "new");
        Assert.Equal("weights", File.ReadAllText(Path.Combine(model, "checkpoint.bin")));
        AssertNoTemporaryDirectories(model);
    }

    [Fact]
    public async Task AnErrorAfterBothGraphsWereCreatedLeavesNothingInstalledAndCanBeRetried()
    {
        var model = NewModel();
        using var failed = FakeSidecarProcess.Scripted(Script(new
        {
            op = "exportDiariserGraphs",
            files = new[]
            {
                new FileSpec("segmentation.onnx", "complete"),
                new FileSpec("embedding.onnx", "partial", HoldOpen: true),
            },
            emit = new[] { Error },
        }));
        using var success = FakeSidecarProcess.Scripted(Script(new
        {
            op = "exportDiariserGraphs",
            files = Files(),
            emit = new[] { Result },
        }));
        var attempts = new Queue<PythonRuntime.Resolution>([failed.Resolution, success.Resolution]);
        var exporter = new SidecarDiariserGraphExporter(() => new PythonSidecar(attempts.Dequeue()));

        await Assert.ThrowsAsync<PythonEngineException>(() => exporter.ExportAsync(model));

        Assert.False(DiariserGraphs.AreInstalled(model));
        Assert.False(Directory.Exists(DiariserGraphs.DirectoryFor(model)));
        AssertNoTemporaryDirectories(model);

        await exporter.ExportAsync(model);

        Assert.True(DiariserGraphs.AreInstalled(model));
        AssertFiles(DiariserGraphs.DirectoryFor(model), "new");
        AssertNoTemporaryDirectories(model);
    }

    [Theory]
    [InlineData("segmentation.onnx")]
    [InlineData("embedding.onnx")]
    [InlineData("manifest.json")]
    public async Task AResultWithoutEveryStagedArtifactCannotPublish(string missing)
    {
        var model = NewModel();
        using var fake = FakeSidecarProcess.Scripted(Script(new
        {
            op = "exportDiariserGraphs",
            files = Files().Where(file => file.Name != missing).ToArray(),
            emit = new[] { Result },
        }));
        var exporter = new SidecarDiariserGraphExporter(() => new PythonSidecar(fake.Resolution));

        await Assert.ThrowsAsync<PythonSidecarException>(() => exporter.ExportAsync(model));

        Assert.False(DiariserGraphs.AreInstalled(model));
        Assert.False(Directory.Exists(DiariserGraphs.DirectoryFor(model)));
        AssertNoTemporaryDirectories(model);
    }

    [Fact]
    public async Task CancellationStopsTheWriterBeforeRemovingItsPrivateFiles()
    {
        var model = NewModel();
        using var fake = FakeSidecarProcess.Scripted(Script(new
        {
            op = "exportDiariserGraphs",
            files = Files(holdOpen: true),
            announce = new[] { Progress },
            delayMilliseconds = 30_000,
            emit = new[] { Result },
        }));
        var exporter = new SidecarDiariserGraphExporter(() => new PythonSidecar(fake.Resolution));
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var export = exporter.ExportAsync(model, new SignalProgress(writing), cancellation.Token);

        try
        {
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var staging = Assert.Single(Directory.GetDirectories(model, ".onnx-export-*"));
            Assert.All(Names, name => Assert.True(File.Exists(Path.Combine(staging, name))));
            Assert.False(DiariserGraphs.AreInstalled(model));
        }
        finally
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);

        Assert.False(DiariserGraphs.AreInstalled(model));
        Assert.False(Directory.Exists(DiariserGraphs.DirectoryFor(model)));
        AssertNoTemporaryDirectories(model);
    }

    [Fact]
    public async Task AFailedExportLeavesPreviousGraphsAndUserFilesUntouched()
    {
        var model = NewModel();
        var directory = InstallOldFiles(model);
        using var fake = FakeSidecarProcess.Scripted(Script(new
        {
            op = "exportDiariserGraphs",
            files = Files(holdOpen: true),
            emit = new[] { Error },
        }));
        var exporter = new SidecarDiariserGraphExporter(() => new PythonSidecar(fake.Resolution));

        await Assert.ThrowsAsync<PythonEngineException>(() => exporter.ExportAsync(model));

        AssertFiles(directory, "old");
        Assert.Equal("keep", File.ReadAllText(Path.Combine(directory, "notes.txt")));
        Assert.True(DiariserGraphs.AreInstalled(model));
        AssertNoTemporaryDirectories(model);
    }

    [Fact]
    public async Task AReplacementChangesOnlyTheNamedDerivedFiles()
    {
        var model = NewModel();
        var directory = InstallOldFiles(model);
        var custom = Path.Combine(directory, "custom");
        Directory.CreateDirectory(custom);
        File.WriteAllText(Path.Combine(custom, "embedding.onnx"), "custom graph");
        using var fake = FakeSidecarProcess.Scripted(Script(new
        {
            op = "exportDiariserGraphs",
            files = Files(),
            emit = new[] { Result },
        }));
        var exporter = new SidecarDiariserGraphExporter(() => new PythonSidecar(fake.Resolution));

        await exporter.ExportAsync(model);

        AssertFiles(directory, "new");
        Assert.Equal("keep", File.ReadAllText(Path.Combine(directory, "notes.txt")));
        Assert.Equal("custom graph", File.ReadAllText(Path.Combine(custom, "embedding.onnx")));
        AssertNoTemporaryDirectories(model);
    }

    [Theory]
    [InlineData("embedding.onnx")]
    [InlineData("manifest.json")]
    public async Task AConflictingDirectoryRestoresPreviousFilesAndKeepsUserData(string blockedName)
    {
        var model = NewModel();
        var directory = InstallOldFiles(model);
        var blocker = Path.Combine(directory, blockedName);
        File.Delete(blocker);
        Directory.CreateDirectory(blocker);
        File.WriteAllText(Path.Combine(blocker, "custom.txt"), "keep this directory");
        using var fake = FakeSidecarProcess.Scripted(Script(new
        {
            op = "exportDiariserGraphs",
            files = Files(),
            emit = new[] { Result },
        }));
        var exporter = new SidecarDiariserGraphExporter(() => new PythonSidecar(fake.Resolution));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => exporter.ExportAsync(model));

        Assert.True(failure is IOException or UnauthorizedAccessException);
        foreach (var name in Names.Where(name => name != blockedName))
        {
            Assert.Equal($"old {name}", File.ReadAllText(Path.Combine(directory, name)));
        }

        Assert.Equal("keep this directory", File.ReadAllText(Path.Combine(blocker, "custom.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(directory, "notes.txt")));
        AssertNoTemporaryDirectories(model);
    }

    private static string NewModel()
    {
        var directory = TestTemp.NewDirectory("graph-export-model");
        File.WriteAllText(Path.Combine(directory, "checkpoint.bin"), "weights");
        return directory;
    }

    private static string InstallOldFiles(string model)
    {
        var directory = DiariserGraphs.DirectoryFor(model);
        Directory.CreateDirectory(directory);
        foreach (var name in Names)
        {
            File.WriteAllText(Path.Combine(directory, name), $"old {name}");
        }

        File.WriteAllText(Path.Combine(directory, "notes.txt"), "keep");
        return directory;
    }

    private static FileSpec[] Files(bool holdOpen = false) =>
        Names.Select(name => new FileSpec(name, $"new {name}", holdOpen)).ToArray();

    private static void AssertFiles(string directory, string prefix)
    {
        foreach (var name in Names)
        {
            Assert.Equal($"{prefix} {name}", File.ReadAllText(Path.Combine(directory, name)));
        }
    }

    private static void AssertNoTemporaryDirectories(string model) =>
        Assert.Empty(Directory.EnumerateDirectories(model, ".onnx-*"));

    private static object Script(object exportRule) => new
    {
        rules = new object[]
        {
            new { op = "hello", emit = new[] { FakeSidecarProcess.Handshake } },
            exportRule,
            new { op = "shutdown", emit = Array.Empty<string>(), exit = 0 },
        },
        @default = new { emit = new[] { Error } },
    };

    private sealed record FileSpec(string Name, string Content, bool HoldOpen = false);

    private sealed class SignalProgress(TaskCompletionSource signal) : IProgress<(int Completed, int Total)>
    {
        public void Report((int Completed, int Total) value) => signal.TrySetResult();
    }
}
