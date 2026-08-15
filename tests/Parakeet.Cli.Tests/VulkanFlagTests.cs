namespace Parakeet.Cli.Tests;

/// <summary>
/// The <c>--vk-disable-bf16</c> flag, checked against the real command specs rather than a fixture:
/// the flag is worthless if it is declared on a command that never loads a model, or missing from
/// one that does.
/// </summary>
public class VulkanFlagTests
{
    private const string Flag = "vk-disable-bf16";

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
}
