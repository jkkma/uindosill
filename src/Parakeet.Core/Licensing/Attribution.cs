using System.Text;

namespace Parakeet.Core.Licensing;

/// <summary>
/// A notice that has to travel with a set of weights, whatever licence demands it.
/// </summary>
/// <remarks>
/// Two licences now, and they want different things: CC BY 4.0 wants a seven-element package, and
/// the NVIDIA Open Model License wants one verbatim sentence plus a copy of the agreement. Modelling
/// them as one record would mean rendering a diarisation model under headings — "Modifications",
/// "Warranties" — that its licence never asked for, which is a false notice in front of a user and
/// exactly what the catalogue's own comment about the deferred Nemotron entries refuses. So each
/// licence gets a record shaped like its own obligations, and the surfaces that render notices
/// depend only on this.
/// </remarks>
public interface IModelAttribution
{
    /// <summary>Human title of the work, for display.</summary>
    string Title { get; }

    /// <summary>Renders every element the licence requires, in a fixed order.</summary>
    string ToPlainText(string newLine = "\n");
}

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
public sealed record CcByAttribution : IModelAttribution
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

/// <summary>
/// An NVIDIA Open Model License notice package.
/// </summary>
/// <remarks>
/// <para>
/// Section 3.1 of the Agreement (version 24 October 2025, read at NVIDIA's own URL) attaches two
/// conditions to redistribution, and only two: <i>"If you distribute the Model, You must give any
/// other recipients of the Model a copy of this Agreement and include the following attribution
/// notice within a "Notice" text file with such copies: "Licensed by NVIDIA Corporation under the
/// NVIDIA Open Model License""</i>. So the mandated sentence is a field of its own and is rendered
/// verbatim and unlabelled — prefixing it, as every other line here is prefixed, would stop it being
/// the required string.
/// </para>
/// <para>
/// Where it differs from CC BY, and it matters twice. The Agreement requires a <b>copy</b>, not a
/// link, which is why <see cref="AgreementPath"/> names a file that ships. And the grant is
/// <b>revocable</b> and unilaterally updatable (§2.1), where CC BY 4.0 is neither — a real
/// difference in shipping risk, recorded in <c>docs/LICENSING.md</c> rather than absorbed.
/// </para>
/// </remarks>
public sealed record OpenModelLicenceAttribution : IModelAttribution
{
    public required string Title { get; init; }

    /// <summary>The string §3.1 mandates, verbatim. Not a template.</summary>
    public required string RequiredNotice { get; init; }

    /// <summary>Which version of the Agreement was read, so a later one is a visible change.</summary>
    public required string AgreementVersion { get; init; }

    /// <summary>Repository-relative path of the copy that ships beside the notice, per §3.1.</summary>
    public required string AgreementPath { get; init; }

    public required Uri AgreementUri { get; init; }

    /// <summary>Where the weights come from, and who made them if not NVIDIA.</summary>
    public required Uri MaterialUri { get; init; }

    /// <summary>What was done to the original checkpoint, and by whom.</summary>
    public required string ProvenanceNotice { get; init; }

    /// <summary>§6, which disclaims warranties in terms the Agreement sets out itself.</summary>
    public required string WarrantyDisclaimerNotice { get; init; }

    public string ToPlainText(string newLine = "\n")
    {
        var builder = new StringBuilder();
        builder.Append(Title).Append(newLine);
        builder.Append(RequiredNotice).Append(newLine);
        builder.Append("Agreement: ").Append(AgreementVersion).Append(", ").Append(AgreementUri).Append(newLine);
        builder.Append("A copy ships at ").Append(AgreementPath).Append('.').Append(newLine);
        builder.Append("Source: ").Append(MaterialUri).Append(newLine);
        builder.Append("Provenance: ").Append(ProvenanceNotice).Append(newLine);
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

    public const string SortformerDiarisation4Spk = "nvidia-sortformer-diar-4spk-v2.1-onnx";

    /// <summary>Where the NVIDIA Open Model License copy lives, relative to the repository root.</summary>
    public const string OpenModelLicencePath = "licences/NVIDIA-Open-Model-License-2025-10-24.txt";

    private static readonly Dictionary<string, IModelAttribution> ByIdMap = new(StringComparer.OrdinalIgnoreCase)
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

        // The diarisation weights, and the first entry here that is not CC BY. The catalogue's own
        // note about the deferred Nemotron entries said what had to happen first — "establish the
        // licence, register the attribution, and move it into models" — and this is that, done for
        // a different family: the Agreement was read in full at NVIDIA's URL on 2026-08-19 and
        // permits redistribution outright, with the notice below and a copy of the text as its only
        // conditions. See docs/LICENSING.md.
        [SortformerDiarisation4Spk] = new OpenModelLicenceAttribution
        {
            Title = "Streaming Sortformer Diarizer 4spk v2.1 (speaker diarisation model weights)",
            RequiredNotice = "Licensed by NVIDIA Corporation under the NVIDIA Open Model License",
            AgreementVersion = "version dated 24 October 2025",
            AgreementPath = OpenModelLicencePath,
            AgreementUri = new Uri("https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/"),
            MaterialUri = new Uri("https://huggingface.co/soniqo/Sortformer-Diarization-4spk-ONNX"),
            ProvenanceNotice =
                "NVIDIA trained diar_streaming_sortformer_4spk-v2.1; a third party, soniqo, exported it "
                + "to ONNX with no retraining and no change to weights, architecture or configuration, "
                + "composing the pre-encoder and head into one graph traced at static shapes. That "
                + "export is a Model Derivative under section 1 of the Agreement and is published under "
                + "the same terms. Uindosill hosts neither file and installs soniqo's copy by URL.",
            WarrantyDisclaimerNotice =
                "NVIDIA provides the model on an \"AS IS\" basis, without warranties or conditions of any "
                + "kind, express or implied (section 6 of the Agreement).",
        },
    };

    public static IReadOnlyDictionary<string, IModelAttribution> ById => ByIdMap;

    public static IModelAttribution Get(string id) =>
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

        // The one restriction that is about what the diariser does rather than about paperwork.
        // NVIDIA Open Model License §2.3 incorporates the Trustworthy AI terms by reference, and
        // their clause (b) names biometric processing specifically — which is what telling voices
        // apart is, under several jurisdictions' definitions. It is listed with the others rather
        // than left in the Agreement because this list is what the CLI and the Licences tab render.
        "NVIDIA Open Model License §2.3 requires use consistent with NVIDIA's Trustworthy AI terms, which " +
        "forbid use in violation of applicable law — naming illegal surveillance and the illegal collection " +
        "or processing of biometric information without the subject's consent where consent is required. " +
        "Speaker diarisation is voice biometrics: recording and separating people's voices may need their " +
        "consent, and that is the user's responsibility on their own material.",

        "NVIDIA Open Model License §2.1 makes the diarisation grant revocable and lets NVIDIA update the " +
        "Agreement for legal or regulatory reasons, and terminates it automatically on filing patent or " +
        "copyright litigation over the model. CC BY 4.0, which the transcription weights ship under, is " +
        "irrevocable; the two are not interchangeable and the difference is a shipping risk, not a detail.",
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
        // Ships in the desktop application only: it is what builds the installer and what checks
        // for a newer version. The CLI zip carries none of it. The licence was read off the
        // package as restored — velopack.nuspec 1.2.0 declares `<license type="expression">MIT`
        // and `Copyright (c) Velopack Ltd. All rights reserved.` — rather than assumed from the
        // repository, because a package and its repository can disagree.
        new ComponentLicence
        {
            Component = "Velopack (installer and update framework)",
            License = "MIT",
            Uri = new Uri("https://github.com/velopack/velopack"),
            Notes =
                "Copyright (c) Velopack Ltd. All rights reserved. Shipped in the desktop application "
                + "only; the command-line tool carries none of it.",
        },
        // Ships with any build that carries the speaker opt-in: one native library per RID, about
        // 16 MB on win-x64. MIT itself, but it statically links 69 third-party components with
        // their own notices — Intel MKL, protobuf, Eigen, oneDNN, abseil, XNNPACK and the rest —
        // and the package's own ThirdPartyNotices.txt is 343 KB of them. Summarising that into a
        // row here would be inventing a licence for each; the file is shipped verbatim instead.
        new ComponentLicence
        {
            Component = "ONNX Runtime (onnxruntime.dll, Microsoft.ML.OnnxRuntime 1.29.0)",
            License = "MIT",
            Uri = new Uri("https://github.com/microsoft/onnxruntime"),
            Notes =
                "Copyright (c) Microsoft Corporation. Runs the speaker diarisation model. Bundles 69 " +
                "third-party components under their own licences; ONNX Runtime's ThirdPartyNotices.txt " +
                "is redistributed verbatim rather than summarised here.",
        },
        // The one entry here that is not MIT, and the reason this list is rendered rather than
        // summarised as "MIT dependencies". cudart64_12.dll, cublas64_12.dll and cublasLt64_12.dll
        // are NVIDIA proprietary binaries redistributed under Attachment A of the CUDA Toolkit
        // EULA. It is listed unconditionally even though only the opt-in CUDA backend ships them:
        // a notice that appears only when a build flag says so is a notice that can go missing,
        // and the Notes field says which builds carry the files. See docs/LICENSING.md.
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
