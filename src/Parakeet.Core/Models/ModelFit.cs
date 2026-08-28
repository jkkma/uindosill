namespace Parakeet.Core.Models;

/// <summary>
/// Whether a machine has the memory to run a catalogue entry, and what to say when it does not.
/// </summary>
/// <remarks>
/// <para>
/// This exists for <see cref="ModelTask.Answering"/> and would be pointless without it. Every
/// other task's weights are between 2 MiB and 1.34 GiB, so "will it run here" has never been a
/// question worth asking; an answering model is tens of gigabytes and the honest answer on a
/// small machine is no.
/// </para>
/// <para>
/// <b>The rule is a heuristic anchored to two measurements, not a model of the allocator.</b> On
/// the second machine — 15.6 GiB of system memory, integrated graphics, so the weights live in
/// system RAM — the 26B-A4B at UD-IQ4_XS is a 12.66 GiB file and ran, leaving 0.9–1.8 GiB free
/// (measured 2026-08-27, docs/UNPROVEN.md). That is the tightest configuration anybody here has
/// seen work. Adding <see cref="HeadroomBytes"/> to the file size puts that case just inside the
/// line and puts a 15.85 GiB file on the same machine outside it, which is the call this has to
/// get right.
/// </para>
/// <para>
/// <b>What it deliberately does not do is refuse.</b> The warning is a warning: a machine with
/// fast storage and a patient owner may swap its way through an answer, the reading is of total
/// physical memory rather than what is free right now, and nothing here knows what a discrete
/// card is holding. Being wrong in the direction of "we told you and you did it anyway" costs a
/// download; being wrong in the direction of refusing costs somebody a model that would have
/// worked.
/// </para>
/// </remarks>
public static class ModelFit
{
    /// <summary>
    /// What the operating system, this application and the runtime need to still be there once
    /// the weights are resident. Two gibibytes is the measured margin, rounded down: the second
    /// machine ran with less free than this and did so while a browser and an agent session were
    /// also loaded, so it is not a floor anybody has to hit.
    /// </summary>
    public const long HeadroomBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Total physical memory as the runtime reports it. Total rather than available on purpose:
    /// what is free right now is whatever else happens to be open, and a warning that changes
    /// when somebody closes a browser tab is one nobody can act on.
    /// </summary>
    public static long TotalPhysicalBytes() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <summary>
    /// Whether <paramref name="modelBytes"/> is expected to run on a machine with
    /// <paramref name="totalPhysicalBytes"/> of memory. A non-positive reading is unknown, and
    /// unknown answers yes: a machine that could not be measured is not a machine to refuse.
    /// </summary>
    public static bool Fits(long modelBytes, long totalPhysicalBytes) =>
        totalPhysicalBytes <= 0 || modelBytes + HeadroomBytes <= totalPhysicalBytes;

    /// <summary>
    /// The sentence to show beside an entry this machine probably cannot run, or null when it
    /// probably can. User copy: it names the two numbers a person can check for themselves and
    /// says what happens if they try anyway, because the download is theirs to make.
    /// </summary>
    public static string? WhyItMightNotRun(ModelDescriptor model, long totalPhysicalBytes)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Only the answering entries are big enough for this to be a real question, and saying it
        // about a 2 MiB speech detector on a small machine would be noise.
        if (model.Task != ModelTask.Answering)
        {
            return null;
        }

        var bytes = model.Files.Sum(file => file.SizeBytes ?? 0);
        if (bytes <= 0 || Fits(bytes, totalPhysicalBytes))
        {
            return null;
        }

        // The reader's own culture, not the invariant one: these two numbers are shown to a person
        // beside a download button, and a decimal separator that is not theirs reads as a typo.
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var needed = ((bytes + HeadroomBytes) / (double)(1024 * 1024 * 1024)).ToString("F0", culture);
        var have = (totalPhysicalBytes / (double)(1024 * 1024 * 1024)).ToString("F0", culture);

        return $"This one is probably too big for this computer. It wants about {needed} GB of memory "
            + $"once it is loaded and this machine has {have} GB. You can still download it — it may "
            + "run slowly by swapping to disk, or it may not load at all. A smaller version of the same "
            + "model will answer faster here.";
    }
}
