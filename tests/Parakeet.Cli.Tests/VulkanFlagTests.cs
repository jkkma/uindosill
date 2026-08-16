namespace Parakeet.Cli.Tests;

/// <summary>
/// The <c>--vk-disable-bf16</c> flag and its opposite, <c>--vk-bf16</c>, checked against the real
/// command specs rather than a fixture: a flag is worthless if it is declared on a command that
/// never loads a model, or missing from one that does. The workaround is on by default (see
/// <c>ParakeetCppOptions.DisableVulkanBFloat16</c>), so the second flag is the one that changes
/// anything, and the first is kept so commands written before the flip still run.
/// </summary>
public class VulkanFlagTests
{
    private const string Flag = "vk-disable-bf16";
    private const string KeepFlag = "vk-bf16";

    [Fact]
    public void TranscribeAcceptsIt()
    {
        var parsed = CommandLineParser.Parse(Commands.Transcribe, ["audio.m4a", "--vk-disable-bf16"]);

        Assert.False(parsed.HasErrors);
        Assert.True(parsed.HasFlag(Flag));
    }

    [Fact]
    public void BenchAcceptsIt()
    {
        // bench loads a model on the requested backend too, so a machine that needs the knob to
        // load at all needs it here or the command is unusable there.
        var parsed = CommandLineParser.Parse(Commands.Bench, ["audio.m4a", "--vk-disable-bf16"]);

        Assert.False(parsed.HasErrors);
        Assert.True(parsed.HasFlag(Flag));
    }

    [Fact]
    public void ItIsAbsentUnlessAskedFor()
    {
        var parsed = CommandLineParser.Parse(Commands.Transcribe, ["audio.m4a"]);

        Assert.False(parsed.HasErrors);
        Assert.False(parsed.HasFlag(Flag));
    }

    [Fact]
    public void ItTakesNoValue()
    {
        var parsed = CommandLineParser.Parse(Commands.Transcribe, ["audio.m4a", "--vk-disable-bf16=1"]);

        Assert.True(parsed.HasErrors);
    }

    [Fact]
    public void ItIsDocumentedInHelp()
    {
        // The failure it works around is a NULL from the load entry point with no message, so the
        // only way anyone finds this flag is by reading it here or in the error text.
        var option = Assert.Single(Commands.Transcribe.Options, o => o.Name == Flag);

        Assert.False(option.TakesValue);
        Assert.False(string.IsNullOrWhiteSpace(option.Help));
    }

    [Theory]
    [InlineData("transcribe")]
    [InlineData("bench")]
    public void TheOppositeFlagIsAcceptedWhereverAModelIsLoaded(string command)
    {
        // Both commands load a model on the requested backend, and both need the off arm reachable:
        // the measurement that made the workaround the default is only repeatable with it.
        var spec = command == "transcribe" ? Commands.Transcribe : Commands.Bench;
        var parsed = CommandLineParser.Parse(spec, ["audio.m4a", "--vk-bf16"]);

        Assert.False(parsed.HasErrors);
        Assert.True(parsed.HasFlag(KeepFlag));

        var option = Assert.Single(spec.Options, o => o.Name == KeepFlag);
        Assert.False(option.TakesValue);
        Assert.False(string.IsNullOrWhiteSpace(option.Help));
    }

    [Fact]
    public void TheWorkaroundIsOnUnlessTheOppositeFlagIsGiven()
    {
        // The default with neither flag; the same with the old flag spelling it out; off only when
        // asked. Checked through the resolver both commands call, so the CLI cannot silently
        // disagree with ParakeetCppOptions about the default.
        Assert.True(EngineFactory.ParseVulkanBFloat16(disableRequested: false, keepRequested: false));
        Assert.True(EngineFactory.ParseVulkanBFloat16(disableRequested: true, keepRequested: false));
        Assert.False(EngineFactory.ParseVulkanBFloat16(disableRequested: false, keepRequested: true));
    }

    [Fact]
    public void BothFlagsTogetherIsAUsageError()
    {
        // Precedence would make one of them a silent no-op, and the whole point of both is that
        // the load either works or does not depending on which one won.
        var ex = Assert.Throws<CliUsageException>(
            () => EngineFactory.ParseVulkanBFloat16(disableRequested: true, keepRequested: true));

        Assert.Contains("--vk-bf16", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--vk-disable-bf16", ex.Message, StringComparison.Ordinal);
    }
}
