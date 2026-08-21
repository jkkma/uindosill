using System.Text;
using System.Text.Json;

namespace Uindosill.FakeSidecar;

/// <summary>
/// A child process that speaks the sidecar's line protocol from a script on disk.
/// </summary>
/// <remarks>
/// <para>
/// <b>How a test reaches it.</b> The host passes its package root to the child in
/// <c>PYTHONPATH</c>, and a test constructs the resolution itself, so that variable is a private
/// channel from one test to one child: the test writes <c>script.json</c> into a temporary
/// directory and hands that directory over as the package root. No parent-process environment is
/// touched, so nothing races and no test needs to be serialised against another.
/// </para>
/// <para>
/// <b>What it will not do is interpret.</b> The lines it emits are written out verbatim from the
/// script, with only <c>{id}</c> substituted. That is what lets a test send a message with no id, a
/// line that is not JSON at all, or a reply to a request that was never made — all of which the
/// host has to survive, and none of which a well-behaved emitter could produce.
/// </para>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        // The arguments are the host's — `-u -m uindosill_engines` — and mean nothing here. Read and
        // ignored rather than left unmentioned, because an executable that silently ignored its
        // arguments would be indistinguishable from one that failed to receive them.
        _ = args;

        var root = Environment.GetEnvironmentVariable("PYTHONPATH");
        if (string.IsNullOrEmpty(root))
        {
            Console.Error.WriteLine("FakeSidecar: no PYTHONPATH, so there is no script to run.");
            return 2;
        }

        var scriptPath = Path.Combine(root, "script.json");
        if (!File.Exists(scriptPath))
        {
            Console.Error.WriteLine($"FakeSidecar: no script at {scriptPath}.");
            return 2;
        }

        var script = JsonSerializer.Deserialize<Script>(
            File.ReadAllText(scriptPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Script();

        var output = Console.OpenStandardOutput();
        var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        foreach (var line in script.Stderr)
        {
            Console.Error.WriteLine(line);
        }

        // Lines emitted before any request has arrived: how a test produces the unsolicited traffic
        // the host has to shrug off rather than die on.
        foreach (var line in script.Unsolicited)
        {
            writer.WriteLine(line);
        }

        while (Console.In.ReadLine() is { } request)
        {
            if (request.Length == 0)
            {
                continue;
            }

            var (id, op) = Parse(request);
            var rule = script.Rules.FirstOrDefault(r => r.Op == op) ?? script.Default;
            if (rule is null)
            {
                continue;
            }

            // Announced before the delay, so a test can wait for proof that the child has the
            // request in hand. Without it "cancel something in flight" is unwriteable: SendAsync's
            // first await is the write gate, so a cancel raced against the call can land before the
            // request is ever put on the wire — and the test would then be about a request that was
            // never sent, which is a different and much weaker claim.
            foreach (var line in rule.Announce)
            {
                writer.WriteLine(line.Replace("{id}", id ?? "null", StringComparison.Ordinal));
            }

            if (rule.DelayMilliseconds > 0)
            {
                Thread.Sleep(rule.DelayMilliseconds);
            }

            foreach (var line in rule.Emit)
            {
                writer.WriteLine(line.Replace("{id}", id ?? "null", StringComparison.Ordinal));
            }

            foreach (var line in rule.Stderr)
            {
                Console.Error.WriteLine(line);
            }

            if (rule.Exit is { } code)
            {
                // Straight out, with stdout closing under whatever the host is still waiting for.
                // This is the death the host has to turn into one exception per pending request
                // rather than a hang.
                return code;
            }
        }

        return 0;
    }

    /// <summary>
    /// The id and op of a request, by hand.
    /// </summary>
    /// <remarks>
    /// The id comes back as the text that was in the message rather than a number, so that whatever
    /// the host wrote is echoed exactly. A stand-in that re-serialised it would quietly correct a
    /// host that had sent something odd, which is the one thing worth catching here.
    /// </remarks>
    private static (string? Id, string? Op) Parse(string request)
    {
        try
        {
            using var document = JsonDocument.Parse(request);
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var value) ? value.GetRawText() : null;
            var op = root.TryGetProperty("op", out var name) ? name.GetString() : null;
            return (id, op);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private sealed class Script
    {
        /// <summary>Lines written to stderr before anything else, for the crash-report tail.</summary>
        public List<string> Stderr { get; set; } = [];

        /// <summary>Lines written to stdout before any request arrives.</summary>
        public List<string> Unsolicited { get; set; } = [];

        public List<Rule> Rules { get; set; } = [];

        /// <summary>What to do with an op no rule names. Null means silence, which is a valid test.</summary>
        public Rule? Default { get; set; }
    }

    private sealed class Rule
    {
        public string? Op { get; set; }

        /// <summary>Lines written the moment the request arrives, before any delay.</summary>
        public List<string> Announce { get; set; } = [];

        /// <summary>Lines to write to stdout, verbatim but for <c>{id}</c>.</summary>
        public List<string> Emit { get; set; } = [];

        public List<string> Stderr { get; set; } = [];

        /// <summary>Wait before emitting, so a test can cancel something that is in flight.</summary>
        public int DelayMilliseconds { get; set; }

        /// <summary>Exit with this code after emitting, killing the channel.</summary>
        public int? Exit { get; set; }
    }
}
