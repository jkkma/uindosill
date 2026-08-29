using Parakeet.App.Services.Tools;

namespace Parakeet.App.Tests;

/// <summary>
/// The parts of the tool updater that decide whether to replace a working binary.
/// </summary>
/// <remarks>
/// <para>
/// <b>The network is not exercised and the download is not either.</b> Both were driven against the
/// real publishers on 2026-08-29: yt-dlp reported current at 2026.08.19 and Deno was updated 2.9.5
/// to v2.9.6 — downloaded, checked against the digest Deno publishes beside the asset, extracted,
/// and installed into the user tools directory, after which the search resolved to it and left the
/// vendored 2.9.5 untouched. What is held here is the reasoning that happens before any of that.
/// </para>
/// <para>
/// <b>Which is where the risk actually is.</b> A wrong digest parse silently accepts a bad binary;
/// a wrong version comparison replaces a working yt-dlp for nothing, or worse leaves a broken one
/// in place because two spellings of the same version looked different.
/// </para>
/// </remarks>
public class ToolUpdaterTests
{
    // ---- The two checksum formats, both read off the real releases ---------------------------

    [Fact]
    public void TheYtDlpSumsFileIsReadByFileName()
    {
        // The real SHA2-256SUMS lists every asset; the one being installed has to be picked out by
        // name rather than by position, since the order is the publisher's business.
        const string Content = """
            aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  yt-dlp
            66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a  yt-dlp.exe
            cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc  yt-dlp_linux
            """;

        var hash = ToolUpdater.ParseChecksum(Content, ChecksumFormat.Sha256Sums, "yt-dlp.exe");

        // This is the digest the release actually publishes, and it is also what
        // scripts/vendor-tools.ps1 pins — so the parse agreeing with the pin is a real check.
        Assert.Equal("66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a", hash);
    }

    [Fact]
    public void AnAssetMissingFromTheSumsFileIsRefused()
    {
        const string Content = "aaaa  something-else.exe";

        Assert.Throws<InvalidOperationException>(
            () => ToolUpdater.ParseChecksum(Content, ChecksumFormat.Sha256Sums, "yt-dlp.exe"));
    }

    [Fact]
    public void TheDenoChecksumIsPowerShellOutputRatherThanASumsLine()
    {
        // Verbatim shape of deno-x86_64-pc-windows-msvc.zip.sha256sum, blank first line included.
        const string Content = """

            Algorithm : SHA256
            Hash      : 15E5300B0BA3C3695A7621D90160A746EC9E710228CEE639AFA9D580F6E3CD11
            Path      : C:\a\deno\deno\target\release\deno-x86_64-pc-windows-msvc.zip
            """;

        var hash = ToolUpdater.ParseChecksum(
            Content, ChecksumFormat.PowerShellFormatList, "deno-x86_64-pc-windows-msvc.zip");

        Assert.Equal("15E5300B0BA3C3695A7621D90160A746EC9E710228CEE639AFA9D580F6E3CD11", hash);
    }

    [Fact]
    public void AChecksumFileWithNoHashLineIsRefused()
    {
        // A publisher changing the shape of this file must stop the update rather than let an
        // unverified binary through — which is the whole reason the digest is fetched at all.
        Assert.Throws<InvalidOperationException>(
            () => ToolUpdater.ParseChecksum("Algorithm : SHA256", ChecksumFormat.PowerShellFormatList, "x.zip"));
    }

    // ---- When an update is offered ------------------------------------------------------------

    private static ToolStatus Status(string? installed, string? latest) => new()
    {
        Tool = UpdatableTool.Deno,
        InstalledVersion = installed,
        LatestVersion = latest,
    };

    [Fact]
    public void DenoTagsWithAVAndReportsWithoutOne()
    {
        // The case that would otherwise re-download 42 MB on every press: Deno's release is tagged
        // `v2.9.6` and the binary says `2.9.6`. Measured on the real pair on 2026-08-29.
        Assert.False(Status("2.9.6", "v2.9.6").UpdateAvailable);
    }

    [Fact]
    public void ADifferentVersionIsAnUpdate()
    {
        Assert.True(Status("2.9.5", "v2.9.6").UpdateAvailable);
        Assert.True(Status("2026.08.19", "2026.09.02").UpdateAvailable);
    }

    [Fact]
    public void TheSameVersionIsNot()
    {
        Assert.False(Status("2026.08.19", "2026.08.19").UpdateAvailable);
    }

    [Fact]
    public void NothingIsOfferedWhenEitherHalfIsUnknown()
    {
        // A failed check must not read as "an update is available" — that would offer to replace a
        // working binary on the strength of having learned nothing.
        Assert.False(Status(null, "v2.9.6").UpdateAvailable);
        Assert.False(Status("2.9.5", null).UpdateAvailable);
        Assert.False(Status(null, null).UpdateAvailable);
    }

    [Fact]
    public void AnInstalledCopyAheadOfTheReleaseStillCountsAsDifferent()
    {
        // A nightly, or something placed by hand. Reported as different rather than ignored: the
        // comparison deliberately does not try to order two unrelated version schemes, and the
        // honest thing is to let somebody put the published one back.
        Assert.True(Status("2026.09.30", "2026.08.19").UpdateAvailable);
    }

    // ---- Where an update goes -----------------------------------------------------------------

    [Fact]
    public void UpdatesLandInTheUserProfileAndNotTheInstallDirectory()
    {
        // Writing beside the application would need elevation and would be reverted by the next
        // Velopack update. This directory is also what makes the whole thing revertible: delete it
        // and the pinned binaries are what run again.
        var directory = ToolUpdater.UserToolsDirectory;

        Assert.EndsWith("tools", directory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AppContext.BaseDirectory, directory, StringComparison.OrdinalIgnoreCase);
    }
}
