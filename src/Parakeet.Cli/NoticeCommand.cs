using Parakeet.Core.Formatting;
using Parakeet.Core.Licensing;

namespace Parakeet.Cli;

internal static class NoticeCommand
{
    public static int Run(CliContext context)
    {
        context.WriteLine("Model weights");
        context.WriteLine();

        foreach (var attribution in Attributions.ById.Values)
        {
            context.WriteLine(attribution.ToPlainText());
        }

        context.WriteLine("Restrictions that come with these weights");
        foreach (var restriction in Attributions.WeightUsageRestrictions)
        {
            context.WriteLine($"  - {restriction}");
        }

        context.WriteLine();
        context.WriteLine("Third-party components");
        foreach (var component in Attributions.Components)
        {
            context.WriteLine($"  {component.Component}");
            context.WriteLine($"    {component.License}  {component.Uri}");
            if (component.Notes is { Length: > 0 } notes)
            {
                context.WriteLine($"    {notes}");
            }
        }

        return ExitCodes.Success;
    }

    public static int Formats(CliContext context)
    {
        foreach (var formatter in TranscriptFormats.All)
        {
            // Wide enough for "vtt-words" and ".words.vtt"; a format id is not an extension and
            // the two columns no longer have the same width.
            context.WriteLine($"{formatter.Id,-9} {formatter.FileExtension,-10} {formatter.DisplayName}");
        }

        return ExitCodes.Success;
    }
}
