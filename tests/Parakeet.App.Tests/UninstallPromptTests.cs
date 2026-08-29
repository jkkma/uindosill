using Parakeet.App.Services;

namespace Parakeet.App.Tests;

/// <summary>
/// What the uninstall dialog decides before it draws anything, and what it says when it does.
/// </summary>
/// <remarks>
/// <para>
/// <b>The dialog itself is not exercised and cannot be.</b> It is a <c>MessageBoxW</c>, it blocks
/// on a human, and a test suite has no interactive desktop. What is here is everything either side
/// of it: whether asking is warranted at all, and the wording, which is the part that decides
/// whether somebody reinstalling loses tens of gigabytes to a hurried click.
/// </para>
/// <para>
/// <b>Every path that is not an explicit Yes has to end in keeping.</b> The callback this runs from
/// was measured once returning in 98 ms having done nothing and was never explained, so the design
/// rule is that failure lands on the behaviour the product had before the dialog existed. These
/// assertions are that rule.
/// </para>
/// </remarks>
public class UninstallPromptTests
{
    private static string StageBytes(long bytes)
    {
        var directory = TestTemp.NewDirectory("uindosill-uninstall-ask");
        var models = Path.Combine(directory, "models");
        Directory.CreateDirectory(models);
        if (bytes > 0)
        {
            using var file = File.Create(Path.Combine(models, "weights.gguf"));
            file.SetLength(bytes);
        }

        return directory;
    }

    [Fact]
    public void ADirectoryThatIsNotThereIsNotWorthAsking()
    {
        var missing = Path.Combine(TestTemp.NewDirectory("uindosill-uninstall-ask"), "gone");

        Assert.Equal(UninstallChoice.NothingToAsk, UninstallPrompt.Ask(missing));
    }

    [Fact]
    public void AFewMegabytesIsNotWorthAsking()
    {
        // A dialog nobody needed is its own defect, and somebody uninstalling is not on that screen
        // worrying about 4 MiB. Below the threshold the uninstall is silent, as it was before.
        var small = StageBytes(4L * 1024 * 1024);

        Assert.Equal(UninstallChoice.NothingToAsk, UninstallPrompt.Ask(small));
    }

    [Fact]
    public void TheThresholdIsWellBelowASingleModel()
    {
        // The smallest thing the catalogue installs is a couple of megabytes and the largest is
        // tens of gigabytes. The threshold has to sit above the noise and far below one real model,
        // or it either asks about nothing or fails to ask about something that matters.
        Assert.True(UninstallPrompt.AskAboveBytes >= 16L * 1024 * 1024);
        Assert.True(UninstallPrompt.AskAboveBytes <= 256L * 1024 * 1024);
    }

    [Fact]
    public void TheQuestionNamesTheSizeAndWhereItIs()
    {
        // Somebody deciding whether to reclaim disk needs the number, and somebody who wants to go
        // and look needs the path. Both, or the dialog is asking them to guess.
        var message = UninstallPrompt.Message(25_670_000_000, @"C:\Users\someone\AppData\Local\Uindosill");

        Assert.Contains("GiB", message, StringComparison.Ordinal);
        Assert.Contains(@"C:\Users\someone\AppData\Local\Uindosill", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheQuestionNudgesAwayFromDeletingWhenReinstalling()
    {
        // The whole reason this is a question and not a deletion. Uninstall-then-reinstall is the
        // first thing people try when something is wrong, and it was silently costing a
        // multi-gigabyte re-download when this hook last existed.
        var message = UninstallPrompt.Message(25_670_000_000, @"C:\x\Uindosill");

        Assert.Contains("reinstalling", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No", message, StringComparison.Ordinal);
        Assert.Contains("not download them again", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheQuestionSaysWhichAnswerIsTheDestructiveOne()
    {
        // The buttons are Yes and No, which say nothing on their own. The text has to carry which
        // one throws the files away, because the dialog defaults to the other.
        var message = UninstallPrompt.Message(25_670_000_000, @"C:\x\Uindosill");

        Assert.Contains("Yes only if", message, StringComparison.Ordinal);
        Assert.Contains("Delete them?", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTitleAsksTheSameQuestionAsTheBody()
    {
        // The title, the body and the buttons all have to ask the same question. MB_YESNO cannot
        // relabel its buttons, and the title is the line a reader skims, so a title asking whether
        // to *keep* the files over buttons where Yes deletes them would be an inverted control:
        // the skimming reader presses Yes meaning the opposite of what happens, and what happens
        // is irreversible. This keeps the three in step.
        var message = UninstallPrompt.Message(25_670_000_000, @"C:\x\Uindosill");

        Assert.EndsWith("Delete them?", message, StringComparison.Ordinal);
        Assert.Contains("delete", UninstallPrompt.Caption, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keep", UninstallPrompt.Caption, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheQuestionSaysWhatHappensIfNobodyAnswers()
    {
        // Measured against a real uninstall: the callback runs inside Velopack's 30-second budget,
        // an unanswered dialog is closed when that expires, and the uninstall finishes with the
        // files untouched. Walking away is therefore the safe outcome, and somebody who did not
        // expect the dialog should be able to read that rather than having to guess at it.
        var message = UninstallPrompt.Message(25_670_000_000, @"C:\x\Uindosill");

        Assert.Contains("do not answer", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kept", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeepAndDeleteAreDistinctAndNothingToAskIsNeither()
    {
        // Collapsing NothingToAsk into Keep would read the same at the call site today and would
        // stop the caller ever knowing the difference between "asked and told to keep" and "never
        // asked", which is the distinction the register entry turns on.
        Assert.NotEqual(UninstallChoice.Keep, UninstallChoice.Delete);
        Assert.NotEqual(UninstallChoice.NothingToAsk, UninstallChoice.Keep);
        Assert.NotEqual(UninstallChoice.NothingToAsk, UninstallChoice.Delete);
    }
}
