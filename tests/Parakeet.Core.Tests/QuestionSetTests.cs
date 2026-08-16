using System.Text.Json;

namespace Parakeet.Core.Tests;

/// <summary>
/// Validates the shape of the hand-labelled CSB384 question set
/// (<c>tests/fixtures/csb384/questions.json</c>) — decision 6 of docs/V2-ASK-THE-TRANSCRIPT.md.
///
/// The file is data a person writes against a recording, and the lab script that will consume it
/// (recall@10, needle, abstain rate, citation diffs) cannot tell a mislabelled file from a bad
/// model. So the suite holds the file to its own rules: kinds it knows, ids that are unique, gold
/// of the shape each kind requires, quotes short enough to be verbatim, and — once the file says it
/// is labelled rather than a template — the composition it promised and the transcript pin filled
/// in, because a segment id is only meaningful against one transcript.
///
/// Nothing here needs a transcript, weights, or a model. When the pinned transcript is present on
/// a machine, the lab script checks ranges against it; the suite does not, so that CI stays exactly
/// what CLAUDE.md says it is.
/// </summary>
public class QuestionSetTests
{
    private static readonly string[] Kinds = ["pointed", "paraphrase", "global", "adversarial", "who-said", "needle"];

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "csb384", "questions.json");

    private static JsonDocument Load() => JsonDocument.Parse(File.ReadAllText(FixturePath));

    private static bool IsLabelled(JsonElement root) =>
        root.GetProperty("status").GetString() == "labelled";

    [Fact]
    public void TheFixtureIsPresentAndParses()
    {
        Assert.True(File.Exists(FixturePath), $"Expected the question set at {FixturePath}.");
        using var doc = Load();
        var status = doc.RootElement.GetProperty("status").GetString();
        Assert.NotNull(status);
        Assert.Contains(status, new[] { "template", "labelled" });
    }

    [Fact]
    public void TheTranscriptPinHasEveryFieldTheIdsDependOn()
    {
        using var doc = Load();
        var pin = doc.RootElement.GetProperty("transcript");
        foreach (var key in new[] { "source", "model", "quantisation", "backend", "segments", "sha256" })
        {
            Assert.True(pin.TryGetProperty(key, out _), $"transcript.{key} is missing.");
        }

        Assert.False(string.IsNullOrWhiteSpace(pin.GetProperty("source").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(pin.GetProperty("model").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(pin.GetProperty("quantisation").GetString()));

        if (IsLabelled(doc.RootElement))
        {
            // A labelled file with an empty pin would let a re-transcription silently move every id.
            Assert.False(string.IsNullOrWhiteSpace(pin.GetProperty("backend").GetString()), "labelled: transcript.backend must be set.");
            var segments = pin.GetProperty("segments");
            Assert.True(segments.ValueKind == JsonValueKind.Number && segments.GetInt32() > 0, "labelled: transcript.segments must be the segment count.");
            var sha = pin.GetProperty("sha256").GetString();
            Assert.Matches("^[0-9a-f]{64}$", sha ?? string.Empty);
        }
    }

    [Fact]
    public void EveryQuestionHasAKnownKindAUniqueIdAndAQuestion()
    {
        using var doc = Load();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in doc.RootElement.GetProperty("questions").EnumerateArray())
        {
            var id = q.GetProperty("id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.True(seen.Add(id!), $"duplicate question id {id}");
            var kind = q.GetProperty("kind").GetString();
            Assert.NotNull(kind);
            Assert.Contains(kind, Kinds);
            Assert.False(string.IsNullOrWhiteSpace(q.GetProperty("question").GetString()), $"{id}: empty question");
            Assert.True(q.TryGetProperty("gold", out _), $"{id}: no gold");
        }
    }

    [Fact]
    public void GoldHasTheShapeItsKindRequires()
    {
        using var doc = Load();
        var labelled = IsLabelled(doc.RootElement);
        foreach (var q in doc.RootElement.GetProperty("questions").EnumerateArray())
        {
            var id = q.GetProperty("id").GetString();
            var kind = q.GetProperty("kind").GetString();
            var gold = q.GetProperty("gold");
            var abstain = gold.GetProperty("abstain").GetBoolean();
            var segments = gold.GetProperty("segments").EnumerateArray().ToList();

            foreach (var range in segments)
            {
                Assert.Equal(2, range.GetArrayLength());
                var from = range[0].GetInt32();
                var to = range[1].GetInt32();
                Assert.True(from >= 1, $"{id}: segment ids are 1-based; got {from}");
                Assert.True(to >= from, $"{id}: range [{from}, {to}] runs backwards");
            }

            switch (kind)
            {
                case "adversarial":
                    Assert.True(abstain, $"{id}: adversarial gold is an abstention");
                    Assert.Empty(segments);
                    break;
                case "needle":
                    Assert.False(abstain, $"{id}: needle is not an abstention");
                    Assert.Empty(segments);
                    Assert.True(gold.TryGetProperty("plant", out var plant), $"{id}: needle needs gold.plant");
                    Assert.True(plant.TryGetProperty("afterSegment", out _) && plant.TryGetProperty("text", out _), $"{id}: plant needs afterSegment and text");
                    break;
                default:
                    Assert.False(abstain, $"{id}: {kind} gold is not an abstention");
                    if (labelled)
                    {
                        Assert.NotEmpty(segments);
                        if (kind is "pointed" or "paraphrase" or "who-said")
                        {
                            Assert.False(string.IsNullOrWhiteSpace(gold.GetProperty("quote").GetString()), $"{id}: {kind} needs a verbatim quote");
                        }
                    }
                    break;
            }
        }
    }

    [Fact]
    public void QuotesAreShortEnoughToBeVerbatim()
    {
        // Twelve words: long enough to be unambiguous inside a three-hour transcript, short enough
        // that a person copies it exactly and the normalised-substring check has something to hold.
        using var doc = Load();
        foreach (var q in doc.RootElement.GetProperty("questions").EnumerateArray())
        {
            var quote = q.GetProperty("gold").GetProperty("quote").GetString();
            if (string.IsNullOrWhiteSpace(quote))
            {
                continue;
            }

            var words = quote.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.True(words <= 12, $"{q.GetProperty("id").GetString()}: quote is {words} words; the limit is 12");
        }
    }

    [Fact]
    public void ALabelledFileHasTheCompositionItPromised()
    {
        using var doc = Load();
        var promised = doc.RootElement.GetProperty("composition").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal);
        Assert.Equal(Kinds.OrderBy(k => k, StringComparer.Ordinal), promised.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(30, promised.Values.Sum());

        if (!IsLabelled(doc.RootElement))
        {
            // A template carries one placeholder per kind and promises the composition it will
            // have; the promise is checked, the count is not, and no placeholder may read as a label.
            foreach (var q in doc.RootElement.GetProperty("questions").EnumerateArray())
            {
                Assert.StartsWith("TEMPLATE", q.GetProperty("question").GetString(), StringComparison.Ordinal);
            }

            return;
        }

        var actual = doc.RootElement.GetProperty("questions").EnumerateArray()
            .GroupBy(q => q.GetProperty("kind").GetString()!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var (kind, count) in promised)
        {
            Assert.True(actual.TryGetValue(kind, out var have) && have == count, $"labelled: {kind} has {actual.GetValueOrDefault(kind)} questions, promised {count}");
        }

        foreach (var q in doc.RootElement.GetProperty("questions").EnumerateArray())
        {
            Assert.DoesNotContain("TEMPLATE", q.GetProperty("question").GetString(), StringComparison.Ordinal);
        }
    }
}
