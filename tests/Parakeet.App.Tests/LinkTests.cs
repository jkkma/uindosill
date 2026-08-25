using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Parakeet.App.Services;
using Parakeet.App.Services.Tools;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.Core.Models;

namespace Parakeet.App.Tests;

/// <summary>
/// Taking a link instead of a file: the fetch, what it puts in the queue, and what the Ask tab
/// then plays.
/// </summary>
/// <remarks>
/// All of it against <see cref="FakeMediaUrlFetcher"/>, which writes a real WAVE file and touches
/// no network. <c>YtDlpMediaUrlFetcher</c> needs the vendored tools and a live site, so nothing in
/// the suite runs it — see <c>docs/UNPROVEN.md</c> § <i>Fetching a link</i> for what was driven by
/// hand instead.
/// </remarks>
public class LinkTests
{
    [Fact]
    public async Task AFetchedLinkJoinsTheQueueUnderTheTitleTheSiteGaveIt()
    {
        // A temporary file name nobody chose is not what a person should see in their queue, so the
        // row carries the title and keeps the path for the pipeline.
        var (viewModel, fetcher, _) = Create();
        fetcher.Title = "Big Buck Bunny";
        viewModel.Url = "https://example.com/watch?v=abc";

        await viewModel.FetchUrlCommand.ExecuteAsync(null);

        var job = Assert.Single(viewModel.Jobs);
        Assert.Equal("Big Buck Bunny", job.DisplayName);
        Assert.Equal("https://example.com/watch?v=abc", job.SourceUrl);
        Assert.True(job.IsFromUrl);
        Assert.True(File.Exists(job.Path));

        // The duration is read from the file it actually fetched, like any other queued row.
        Assert.NotNull(job.Duration);

        // And the box empties, so the next paste is not appended to the last link.
        Assert.Empty(viewModel.Url!);
    }

    [Fact]
    public async Task TheSameLinkTwiceIsNotTwoRows()
    {
        var (viewModel, _, _) = Create();

        viewModel.Url = "https://example.com/watch?v=abc";
        await viewModel.FetchUrlCommand.ExecuteAsync(null);

        viewModel.Url = "https://example.com/watch?v=abc";
        await viewModel.FetchUrlCommand.ExecuteAsync(null);

        Assert.Single(viewModel.Jobs);
        Assert.Contains("already in the queue", viewModel.UrlStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFetchThatFailsSaysWhyAndLeavesTheQueueAlone()
    {
        // The failure has to land somewhere a person is looking, and it must not cost them the
        // link they pasted — retyping it because the site was briefly down is the kind of small
        // insult this window avoids elsewhere.
        var (viewModel, fetcher, _) = Create();
        fetcher.RefuseWith = "Could not fetch that link. Video unavailable.";
        viewModel.Url = "https://example.com/watch?v=gone";

        await viewModel.FetchUrlCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Jobs);
        Assert.Equal(fetcher.RefuseWith, viewModel.UrlStatus);
        Assert.Equal("https://example.com/watch?v=gone", viewModel.Url);
        Assert.False(viewModel.IsFetchingUrl);
    }

    [Fact]
    public void ABuildWithNoDownloaderSaysSoRatherThanOfferingADeadBox()
    {
        var (viewModel, fetcher, _) = Create();
        fetcher.IsAvailable = false;

        // Re-made, because availability is asked at construction the way every other opt-in in
        // this window asks it.
        var (unavailable, _, _) = Create(fetcher);

        Assert.False(unavailable.CanAddUrl);
        Assert.False(unavailable.CanFetchUrl);
        Assert.NotNull(unavailable.UrlHint);
        Assert.Contains("cannot open links", unavailable.UrlHint, StringComparison.Ordinal);

        unavailable.Url = "https://example.com/watch?v=abc";
        Assert.False(unavailable.CanFetchUrl);
        Assert.False(unavailable.FetchUrlCommand.CanExecute(null));
    }

    [Fact]
    public void TheButtonIsDeadOnAnEmptyBoxAndWhileABatchRuns()
    {
        var (viewModel, _, _) = Create();

        Assert.False(viewModel.CanFetchUrl);

        viewModel.Url = "   ";
        Assert.False(viewModel.CanFetchUrl);

        viewModel.Url = "https://example.com/watch?v=abc";
        Assert.True(viewModel.CanFetchUrl);

        // The queue behind it is shut while a batch runs, so this is too — the same rule the drop
        // zone follows, for the same reason.
        viewModel.IsRunning = true;
        Assert.False(viewModel.CanFetchUrl);

        viewModel.IsRunning = false;
        Assert.True(viewModel.CanFetchUrl);
    }

    [Fact]
    public async Task AFetchInFlightShutsTheButtonUntilItFinishes()
    {
        // Without this a second press queues a second download of the same link.
        var (viewModel, fetcher, _) = Create();
        fetcher.Gate = new TaskCompletionSource();
        viewModel.Url = "https://example.com/watch?v=abc";

        var fetching = viewModel.FetchUrlCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsFetchingUrl);
        Assert.False(viewModel.CanFetchUrl);
        Assert.Equal("Reading the link", viewModel.UrlStatus);

        fetcher.Gate.SetResult();
        await fetching;

        Assert.False(viewModel.IsFetchingUrl);
        Assert.Single(viewModel.Jobs);
    }

    [Fact]
    public async Task OnlyHttpLinksReachTheDownloader()
    {
        // The one place a string from the clipboard becomes an argument to a process. yt-dlp will
        // happily take a local path, and this is what stops one getting there.
        var fetcher = new YtDlpMediaUrlFetcher();
        var root = TestTemp.NewDirectory("uindosill-link");

        foreach (var bad in new[] { "file:///C:/Windows/System32/notepad.exe", "not a url", "ftp://example.com/x.mp3" })
        {
            var refused = await Assert.ThrowsAsync<MediaFetchException>(
                () => fetcher.FetchAudioAsync(bad, root));

            // Either the scheme check or the missing-tools check answers first depending on what
            // this machine has vendored; both are refusals, and neither starts a process.
            Assert.True(
                refused.Message.Contains("http", StringComparison.OrdinalIgnoreCase)
                || refused.Message.Contains("cannot open links", StringComparison.Ordinal),
                $"unexpected refusal: {refused.Message}");
        }
    }

    [Fact]
    public async Task TheAskTabStreamsThePictureFromTheLinkRatherThanTheDownloadedAudio()
    {
        // The whole point of downloading audio alone: the transcript comes from the file, and the
        // picture is streamed from the link on demand. So the player is handed the URL, not the m4a.
        var player = new FakeMediaPlayer { DurationToReport = TimeSpan.FromMinutes(10) };
        var (viewModel, _, main) = Create(player: player);

        viewModel.Url = "https://example.com/watch?v=abc";
        await viewModel.FetchUrlCommand.ExecuteAsync(null);

        Assert.Equal("https://example.com/watch?v=abc", player.Path);
        Assert.Same(main.Transcribe.Jobs[0], main.Ask.SelectedRecording);
    }

    [Fact]
    public async Task AnAudioOnlyBuildPlaysTheFileItDownloadedInsteadOfTheLink()
    {
        // Streaming buys nothing without a picture, and costs a network round trip on every
        // selection, so a build that cannot draw one opens the audio it already has.
        var player = new FakeMediaPlayer
        {
            DurationToReport = TimeSpan.FromMinutes(10),
            CanDrawVideo = false,
        };

        var (viewModel, _, main) = Create(player: player);

        viewModel.Url = "https://example.com/watch?v=abc";
        await viewModel.FetchUrlCommand.ExecuteAsync(null);

        Assert.Equal(main.Transcribe.Jobs[0].Path, player.Path);
        Assert.NotEqual("https://example.com/watch?v=abc", player.Path);

        // And it says why there is no picture, because a link is a thing that usually has one.
        Assert.NotNull(main.Ask.VideoNotice);
    }

    [AvaloniaFact]
    public void TheLinkBoxAndItsButtonAreBoundRatherThanMerelyDrawn()
    {
        // Every interface defect this window has shipped was a control wired to nothing.
        var directory = TestTemp.NewDirectory("uindosill-link");
        var fetcher = new FakeMediaUrlFetcher();
        var main = new MainWindowViewModel(
            new FakeEngineProvider(),
            new LocalModelStore(directory),
            ModelCatalog.Default,
            player: new FakeMediaPlayer(),
            fetcher: fetcher,
            downloadRoot: directory);

        var window = new MainWindow { DataContext = main };
        window.Show();
        window.UpdateLayout();

        var box = window.FindControl<TextBox>("LinkBox");
        var button = window.FindControl<Button>("FetchLink");
        Assert.NotNull(box);
        Assert.NotNull(button);

        Assert.False(button!.IsEnabled);

        box!.Text = "https://example.com/watch?v=abc";
        window.UpdateLayout();

        Assert.Equal("https://example.com/watch?v=abc", main.Transcribe.Url);
        Assert.True(button.IsEnabled);
        Assert.NotNull(button.Command);
    }

    private static (TranscribeViewModel Transcribe, FakeMediaUrlFetcher Fetcher, MainWindowViewModel Main) Create(
        FakeMediaUrlFetcher? fetcher = null,
        FakeMediaPlayer? player = null)
    {
        var directory = TestTemp.NewDirectory("uindosill-link");
        var used = fetcher ?? new FakeMediaUrlFetcher();

        var main = new MainWindowViewModel(
            new FakeEngineProvider(),
            new LocalModelStore(directory),
            ModelCatalog.Default,
            player: player ?? new FakeMediaPlayer(),
            fetcher: used,
            downloadRoot: directory);

        main.Transcribe.OutputDirectory = directory;
        return (main.Transcribe, used, main);
    }
}
