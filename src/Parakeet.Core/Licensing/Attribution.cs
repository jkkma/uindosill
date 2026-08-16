using System.Text;

namespace Parakeet.Core.Licensing;

/// <summary>
/// A CC BY 4.0 §3(a) notice package.
/// </summary>
/// <remarks>
/// The obligation is not "just attribution". §3(a) requires seven elements, and the two most
/// commonly missed are the notice referring to the warranty disclaimer and the statement that
/// the material was modified — GGUF conversion and quantisation are modifications. Modelling
/// them as separate required fields is the point: a record that cannot be constructed without
/// all seven cannot silently ship with five.
/// </remarks>
public sealed record CcByAttribution
{
    /// <summary>Element 1: identification of the creator(s).</summary>
    public required string Creator { get; init; }

    /// <summary>Element 2: the copyright notice.</summary>
    public required string CopyrightNotice { get; init; }

    /// <summary>Element 3: a notice referring to the licence.</summary>
    public required string LicenceNotice { get; init; }

    /// <summary>Element 4: a notice referring to the disclaimer of warranties.</summary>
    public required string WarrantyDisclaimerNotice { get; init; }

    /// <summary>Element 5: a URI to the material.</summary>
    public required Uri MaterialUri { get; init; }

    /// <summary>Element 6: an indication that the material was modified, and how.</summary>
    public required string ModificationNotice { get; init; }

    /// <summary>Element 7: the statement of the licence, with a link to its text.</summary>
    public required string LicenceStatement { get; init; }

    public required Uri LicenceUri { get; init; }

    /// <summary>Human title of the work, for display.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Renders every element in a fixed order. Used by both the CLI and the in-app licence
    /// panel: the notice has to be visible in the application, not only in a file in the
    /// repository, so there is exactly one renderer and both surfaces call it.
    /// </summary>
    public string ToPlainText(string newLine = "\n")
    {
        var builder = new StringBuilder();
        builder.Append(Title).Append(newLine);
        builder.Append("Creator: ").Append(Creator).Append(newLine);
        builder.Append(CopyrightNotice).Append(newLine);
        builder.Append(LicenceNotice).Append(newLine);
        builder.Append("Licence: ").Append(LicenceStatement).Append(' ').Append(LicenceUri).Append(newLine);
        builder.Append("Source: ").Append(MaterialUri).Append(newLine);
        builder.Append("Modifications: ").Append(ModificationNotice).Append(newLine);
        builder.Append("Warranties: ").Append(WarrantyDisclaimerNotice).Append(newLine);
        return builder.ToString();
    }
}

/// <summary>A third-party component and the licence it ships under.</summary>
public sealed record ComponentLicence
{
    public required string Component { get; init; }

    public required string License { get; init; }

    public required Uri Uri { get; init; }

    public string? Notes { get; init; }
}

public static class Attributions
{
    public const string ParakeetTdt06BV3 = "nvidia-parakeet-tdt-0.6b-v3";

    private static readonly Dictionary<string, CcByAttribution> ByIdMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [ParakeetTdt06BV3] = new CcByAttribution
        {
            Title = "Parakeet TDT 0.6B v3 (speech recognition model weights)",
            Creator = "NVIDIA Corporation",
            CopyrightNotice = "Copyright (c) NVIDIA Corporation.",
            LicenceNotice =
                "This material is made available under the Creative Commons Attribution 4.0 International " +
                "licence (CC BY 4.0).",
            WarrantyDisclaimerNotice =
                "The material is provided as-is and without warranties of any kind, express or implied, to the " +
                "extent permitted under the CC BY 4.0 disclaimer of warranties and limitation of liability " +
                "(section 5 of the licence).",
            MaterialUri = new Uri("https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3"),
            ModificationNotice =
                "Modified: the original NVIDIA NeMo checkpoint was converted to the GGUF format and, for some " +
                "builds, quantised (q8_0, q6_k, q5_k, q4_k). Uindosill redistributes these converted files and " +
                "does not redistribute the original checkpoint.",
            LicenceStatement = "Creative Commons Attribution 4.0 International (CC BY 4.0),",
            LicenceUri = new Uri("https://creativecommons.org/licenses/by/4.0/"),
        },
    };

    public static IReadOnlyDictionary<string, CcByAttribution> ById => ByIdMap;

    public static CcByAttribution Get(string id) =>
        ByIdMap.TryGetValue(id, out var attribution)
            ? attribution
            : throw new KeyNotFoundException($"No attribution registered for '{id}'.");

    /// <summary>
    /// Restrictions that come with the weights and constrain what the product may do.
    /// Kept as data next to the notice so they are read together.
    /// </summary>
    public static IReadOnlyList<string> WeightUsageRestrictions { get; } =
    [
        "CC BY 4.0 §2(a)(5)(B) forbids applying effective technological measures to the licensed material: " +
        "model files must not be encrypted, licence-locked or otherwise DRM-wrapped.",
        "CC BY 4.0 §2(b) withholds patent and trademark rights: nothing in this product may imply NVIDIA " +
        "endorsement or sponsorship.",
        "parakeet-tdt-0.6b-v3 covers 25 European languages. It does not cover Chinese, Japanese, Korean, " +
        "Arabic, Hindi or Thai, and the product must not offer them.",
    ];

    /// <summary>Third-party code licences, shown in the same place as the model notice.</summary>
    public static IReadOnlyList<ComponentLicence> Components { get; } =
    [
        new ComponentLicence
        {
            Component = "parakeet.cpp (ggml port of NeMo Parakeet)",
            License = "MIT",
            Uri = new Uri("https://github.com/mudler/parakeet.cpp"),
        },
        new ComponentLicence
        {
            Component = "ggml",
            License = "MIT",
            Uri = new Uri("https://github.com/ggml-org/ggml"),
        },
        new ComponentLicence
        {
            Component = "Avalonia",
            License = "MIT",
            Uri = new Uri("https://github.com/AvaloniaUI/Avalonia"),
        },
        new ComponentLicence
        {
            Component = "NAudio",
            License = "MIT",
            Uri = new Uri("https://github.com/naudio/NAudio"),
            Notes = "Windows media decoding only.",
        },
        new ComponentLicence
        {
            Component = "CommunityToolkit.Mvvm",
            License = "MIT",
            Uri = new Uri("https://github.com/CommunityToolkit/dotnet"),
        },
        // The one entry here that is not MIT, and the reason this list is rendered rather than
        // summarised as "MIT dependencies". cudart64_12.dll, cublas64_12.dll and cublasLt64_12.dll
        // are NVIDIA proprietary binaries redistributed under Attachment A of the CUDA Toolkit
        // EULA. It is listed unconditionally even though only the opt-in CUDA backend ships them:
        // a notice that appears only when a build flag says so is a notice that can go missing,
        // and the Notes field says which builds carry the files. See LICENSING.md in the project notes.
        new ComponentLicence
        {
            Component = "NVIDIA CUDA runtime (cudart64_12.dll, cublas64_12.dll, cublasLt64_12.dll)",
            License = "NVIDIA CUDA Toolkit EULA — proprietary, not MIT; redistributable under Attachment A",
            Uri = new Uri("https://docs.nvidia.com/cuda/eula/index.html"),
            Notes =
                "Shipped only by builds that vendor the opt-in CUDA backend; the CPU and Vulkan backends " +
                "contain none of these files. Redistributed as part of this application and not as a " +
                "stand-alone product, which is the condition Attachment A attaches.",
        },
    ];
}
