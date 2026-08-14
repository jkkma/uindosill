using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Parakeet.Cli;

internal sealed record OptionSpec
{
    public required string Name { get; init; }

    public char? Short { get; init; }

    public bool TakesValue { get; init; }

    public bool Repeatable { get; init; }

    public required string Help { get; init; }

    public string? ValueName { get; init; }

    public string Display
    {
        get
        {
            var builder = new StringBuilder();
            builder.Append(Short is { } s ? $"-{s}, " : "    ");
            builder.Append("--").Append(Name);
            if (TakesValue)
            {
                builder.Append(' ').Append('<').Append(ValueName ?? "value").Append('>');
            }

            return builder.ToString();
        }
    }
}

internal sealed record CommandSpec
{
    public required string Name { get; init; }

    public required string Summary { get; init; }

    public IReadOnlyList<OptionSpec> Options { get; init; } = [];

    /// <summary>Description of the positional arguments, or null when the command takes none.</summary>
    public string? Positionals { get; init; }

    public string? Details { get; init; }
}

internal sealed class ParsedCommandLine
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

    public required CommandSpec Command { get; init; }

    public List<string> Positionals { get; } = [];

    public List<string> Errors { get; } = [];

    public bool HasErrors => Errors.Count > 0;

    public bool HasFlag(string name) => _values.ContainsKey(name);

    public string? Value(string name) => _values.TryGetValue(name, out var values) ? values[^1] : null;

    public IReadOnlyList<string> Values(string name) => _values.TryGetValue(name, out var values) ? values : [];

    internal void Add(string name, string? value)
    {
        if (!_values.TryGetValue(name, out var list))
        {
            list = [];
            _values[name] = list;
        }

        if (value is not null)
        {
            list.Add(value);
        }
    }
}

/// <summary>
/// A small, strict argument parser.
/// </summary>
/// <remarks>
/// Strict on purpose. An unrecognised option is an error, not something to ignore: a typo in
/// <c>--format</c> that silently falls back to the default writes the wrong file and says
/// nothing, which is the exact failure mode this project is built to avoid.
/// </remarks>
internal static class CommandLineParser
{
    public static ParsedCommandLine Parse(CommandSpec command, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(arguments);

        var result = new ParsedCommandLine { Command = command };
        var byName = command.Options.ToDictionary(o => o.Name, StringComparer.Ordinal);
        var byShort = command.Options.Where(o => o.Short is not null).ToDictionary(o => o.Short!.Value);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var literal = false;

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];

            if (literal)
            {
                result.Positionals.Add(argument);
                continue;
            }

            if (argument == "--")
            {
                literal = true;
                continue;
            }

            if (argument.Length > 2 && argument.StartsWith("--", StringComparison.Ordinal))
            {
                var body = argument[2..];
                string? inlineValue = null;
                var equals = body.IndexOf('=', StringComparison.Ordinal);
                if (equals >= 0)
                {
                    inlineValue = body[(equals + 1)..];
                    body = body[..equals];
                }

                if (!byName.TryGetValue(body, out var option))
                {
                    result.Errors.Add($"Unknown option '--{body}'. Try 'uindosill {command.Name} --help'.");
                    continue;
                }

                Apply(result, option, inlineValue, arguments, ref i, seen);
                continue;
            }

            if (argument.Length >= 2 && argument[0] == '-' && argument != "-" && !char.IsDigit(argument[1]))
            {
                var handled = true;
                for (var c = 1; c < argument.Length; c++)
                {
                    if (!byShort.TryGetValue(argument[c], out var option))
                    {
                        result.Errors.Add($"Unknown option '-{argument[c]}'. Try 'uindosill {command.Name} --help'.");
                        handled = false;
                        break;
                    }

                    if (option.TakesValue)
                    {
                        // -f srt, or -fsrt when the value is glued to a single-letter option.
                        var inline = c + 1 < argument.Length ? argument[(c + 1)..] : null;
                        Apply(result, option, inline, arguments, ref i, seen);
                        break;
                    }

                    Apply(result, option, null, arguments, ref i, seen);
                }

                if (!handled)
                {
                    continue;
                }

                continue;
            }

            result.Positionals.Add(argument);
        }

        return result;
    }

    private static void Apply(
        ParsedCommandLine result,
        OptionSpec option,
        string? inlineValue,
        IReadOnlyList<string> arguments,
        ref int index,
        HashSet<string> seen)
    {
        if (!option.Repeatable && !seen.Add(option.Name))
        {
            result.Errors.Add($"Option '--{option.Name}' was given more than once.");
            return;
        }

        if (!option.TakesValue)
        {
            if (inlineValue is not null)
            {
                result.Errors.Add($"Option '--{option.Name}' does not take a value.");
                return;
            }

            result.Add(option.Name, string.Empty);
            return;
        }

        if (inlineValue is not null)
        {
            result.Add(option.Name, inlineValue);
            return;
        }

        if (index + 1 >= arguments.Count)
        {
            result.Errors.Add($"Option '--{option.Name}' needs a value.");
            return;
        }

        index++;
        result.Add(option.Name, arguments[index]);
    }

    /// <summary>Splits comma-separated values so <c>--format srt,vtt</c> works as well as repeats.</summary>
    public static IReadOnlyList<string> SplitList(IEnumerable<string> values)
    {
        var result = new List<string>();
        foreach (var value in values)
        {
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!result.Contains(part, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(part);
                }
            }
        }

        return result;
    }

    public static bool TryParseInt(string? value, [NotNullWhen(true)] out int parsed)
    {
        parsed = 0;
        return value is not null
            && int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out parsed);
    }

    public static bool TryParseDouble(string? value, out double parsed) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed);

    public static string RenderHelp(CommandSpec command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var builder = new StringBuilder();
        builder.Append("uindosill ").Append(command.Name);
        if (command.Positionals is { } positionals)
        {
            builder.Append(' ').Append(positionals);
        }

        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine(command.Summary);

        if (command.Options.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Options:");
            var width = command.Options.Max(o => o.Display.Length);
            foreach (var option in command.Options)
            {
                builder.Append("  ").Append(option.Display.PadRight(width)).Append("  ").AppendLine(option.Help);
            }
        }

        if (command.Details is { Length: > 0 } details)
        {
            builder.AppendLine();
            builder.AppendLine(details);
        }

        return builder.ToString();
    }
}
