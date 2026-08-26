using System.Globalization;
using System.Text.RegularExpressions;
using Parakeet.Engine.Python;

namespace Parakeet.Engine.Python.Tests;

/// <summary>
/// Holds <see cref="PythonSidecar.ProtocolVersion"/> against the sidecar's own
/// <c>PROTOCOL_VERSION</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This test exists because the absence of it shipped a break.</b> On 2026-08-26 the diariser
/// gained a second engine, <c>python/uindosill_engines/protocol.py</c> moved to 2, and the constant
/// in the host stayed at 1. The host refuses a sidecar whose number differs — so the effect was not
/// a degraded diariser but a sidecar rejected at <c>hello</c>, taking translation down with it — and
/// <b>the suite stayed green throughout</b>, because <c>FakeSidecarProcess</c> answers with whatever
/// the host constant says. Every test that exercises the mismatch path uses obviously wrong numbers
/// like 99, which pass for any host value. Nothing in CI could see the real one.
/// </para>
/// <para>
/// It reads the file rather than importing it, on the same terms as <c>DeclaredLimitsTests</c>: this
/// suite has no Python — <c>scripts/cloud-setup.sh</c> installs the SDK and PowerShell and no
/// interpreter — and one integer in a <c>#:</c>-documented module constant is well within what a
/// regular expression can be trusted with. A rename that put it out of reach fails this test rather
/// than passing it silently.
/// </para>
/// <para>
/// <b>Two copies of this number remain, and the third is now gone.</b> The bundler had one as a
/// literal in its handshake and rejected a correctly-assembled bundle for answering 2; it reads the
/// Python source now. The host cannot do that — it is compiled, and the interpreter it will drive is
/// not present at build time — so the copy here stays and this test is what keeps it honest.
/// </para>
/// </remarks>
public sealed class ProtocolVersionTests
{
    [Fact]
    public void TheHostSpeaksTheNumberTheSidecarSpeaks()
    {
        Assert.Equal(PythonSidecar.ProtocolVersion, ReadSidecarProtocolVersion());
    }

    [Fact]
    public void TheSidecarDeclaresItOnceAndAsAPlainInteger()
    {
        // The regular expression above is only trustworthy while the constant is written the way it
        // is written. A second assignment, or one built from an expression, would let this file read
        // a number that is not the one the sidecar reports.
        var matches = Regex.Matches(Source, @"^PROTOCOL_VERSION\s*=", RegexOptions.Multiline);

        Assert.Single(matches);
    }

    private static int ReadSidecarProtocolVersion()
    {
        var match = Regex.Match(Source, @"^PROTOCOL_VERSION\s*=\s*(\d+)\s*$", RegexOptions.Multiline);

        Assert.True(match.Success, "PROTOCOL_VERSION was not found in python/uindosill_engines/protocol.py.");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static string Source { get; } = File.ReadAllText(
        Path.Combine(RepositoryRoot, "python", "uindosill_engines", "protocol.py"));

    private static string RepositoryRoot
    {
        get
        {
            var directory = AppContext.BaseDirectory;
            while (directory is not null && !File.Exists(Path.Combine(directory, "Uindosill.slnx")))
            {
                directory = Path.GetDirectoryName(directory);
            }

            return directory ?? throw new InvalidOperationException(
                "The repository root was not found above the test binary.");
        }
    }
}
