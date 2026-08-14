namespace Parakeet.Cli.Tests;

public class CommandLineParserTests
{
    private static readonly CommandSpec Spec = new()
    {
        Name = "test",
        Summary = "A command for tests.",
        Positionals = "<file>",
        Options =
        [
            new OptionSpec { Name = "format", Short = 'f', TakesValue = true, Repeatable = true, Help = "format" },
            new OptionSpec { Name = "out", Short = 'o', TakesValue = true, Help = "output" },
            new OptionSpec { Name = "quiet", Short = 'q', Help = "quiet" },
        ],
    };

    private static ParsedCommandLine Parse(params string[] args) => CommandLineParser.Parse(Spec, args);

    [Fact]
    public void SeparateValueIsRead()
    {
        var parsed = Parse("--format", "srt");

        Assert.False(parsed.HasErrors);
        Assert.Equal("srt", parsed.Value("format"));
    }

    [Fact]
    public void InlineValueIsRead() =>
        Assert.Equal("srt", Parse("--format=srt").Value("format"));

    [Fact]
    public void ShortOptionTakesTheNextArgument() =>
        Assert.Equal("srt", Parse("-f", "srt").Value("format"));

    [Fact]
    public void ShortOptionTakesAGluedValue() =>
        Assert.Equal("srt", Parse("-fsrt").Value("format"));

    [Fact]
    public void BundledFlagsAreSplit()
    {
        var parsed = Parse("-qf", "vtt");

        Assert.True(parsed.HasFlag("quiet"));
        Assert.Equal("vtt", parsed.Value("format"));
    }

    [Fact]
    public void RepeatableOptionsAccumulate()
    {
        var parsed = Parse("-f", "srt", "-f", "vtt");
        Assert.Equal(["srt", "vtt"], parsed.Values("format"));
    }

    [Fact]
    public void NonRepeatableOptionGivenTwiceIsAnError()
    {
        var parsed = Parse("-o", "a", "-o", "b");

        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.Errors, e => e.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownOptionIsAnErrorRatherThanIgnored()
    {
        // A typo'd --fromat that silently falls back to the default writes the wrong file and
        // says nothing. Strict parsing is the cheapest possible fix for that class of bug.
        var parsed = Parse("--fromat", "srt");

        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.Errors, e => e.Contains("--fromat", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingValueIsAnError()
    {
        var parsed = Parse("--format");

        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.Errors, e => e.Contains("needs a value", StringComparison.Ordinal));
    }

    [Fact]
    public void ValueGivenToAFlagIsAnError()
    {
        var parsed = Parse("--quiet=yes");

        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.Errors, e => e.Contains("does not take a value", StringComparison.Ordinal));
    }

    [Fact]
    public void PositionalsAreCollectedInOrder()
    {
        var parsed = Parse("a.wav", "-q", "b.wav");

        Assert.Equal(["a.wav", "b.wav"], parsed.Positionals);
        Assert.True(parsed.HasFlag("quiet"));
    }

    [Fact]
    public void DoubleDashEndsOptionParsing()
    {
        var parsed = Parse("--", "--not-an-option.wav");

        Assert.False(parsed.HasErrors);
        Assert.Equal(["--not-an-option.wav"], parsed.Positionals);
    }

    [Fact]
    public void FilenamesStartingWithADigitAreNotMistakenForOptions()
    {
        var parsed = Parse("-3db.wav");
        Assert.Equal(["-3db.wav"], parsed.Positionals);
    }

    [Fact]
    public void CommaSeparatedListsAreSplitAndDeduplicated() =>
        Assert.Equal(["srt", "vtt", "json"], CommandLineParser.SplitList(["srt, vtt", "json", "SRT"]));

    [Fact]
    public void HelpMentionsEveryOption()
    {
        var help = CommandLineParser.RenderHelp(Spec);

        Assert.Contains("--format", help, StringComparison.Ordinal);
        Assert.Contains("-f,", help, StringComparison.Ordinal);
        Assert.Contains("<file>", help, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDeclaredCommandRendersHelpWithoutThrowing()
    {
        foreach (var command in Commands.All)
        {
            Assert.NotEmpty(CommandLineParser.RenderHelp(command));
        }
    }

    [Fact]
    public void ShortOptionLettersAreUniqueWithinEachCommand()
    {
        foreach (var command in Commands.All)
        {
            var shorts = command.Options.Where(o => o.Short is not null).Select(o => o.Short!.Value).ToList();
            Assert.Equal(shorts.Count, shorts.Distinct().Count());
        }
    }
}
