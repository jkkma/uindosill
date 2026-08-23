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

/// <summary>
/// An Apache License 2.0 §4 notice package.
/// </summary>
/// <remarks>
/// <para>
/// The third licence in this file and the third differently-shaped obligation. §4 attaches four
/// conditions to redistribution: <b>(a)</b> give recipients a copy of the License — a copy, like
/// the NVIDIA agreement and unlike CC BY, which is why <see cref="LicencePath"/> names a file that
/// ships rather than a URL; <b>(b)</b> carry prominent notices stating that the files were changed;
/// <b>(c)</b> retain the copyright, patent, trademark and attribution notices found in the source
/// form; and <b>(d)</b> reproduce the attribution notices of any <c>NOTICE</c> file the work ships.
/// </para>
/// <para>
/// <b>(c) and (d) are discharged by what the source actually carries, and this record cannot invent
/// either.</b> A copyright line nobody published is a false notice in front of a user, which is the
/// thing the catalogue's own comment about the deferred entries refuses. So the upstream tree was
/// read rather than recalled — the file listing and every text file in it, at the pinned revision,
/// on 2026-08-20 — and both halves of the answer are fields here: what it carries is
/// <see cref="RetainedSourceNotices"/>, and what it does not is <see cref="SourceNoticeFinding"/>,
/// stated as a finding with its revision and its date so a later revision is a visible change
/// rather than a silent one. <c>docs/LICENSING.md</c> records how the reading was done.
/// </para>
/// </remarks>
public sealed record ApacheAttribution : IModelAttribution
{
    public required string Title { get; init; }

    /// <summary>Who publishes the work, as the source repository states it.</summary>
    public required string Creator { get; init; }

    /// <summary>The statement of the licence, with a link to its canonical text.</summary>
    public required string LicenceStatement { get; init; }

    public required Uri LicenceUri { get; init; }

    /// <summary>Repository-relative path of the copy that ships, per §4(a).</summary>
    public required string LicencePath { get; init; }

    /// <summary>Where the weights come from.</summary>
    public required Uri MaterialUri { get; init; }

    /// <summary>§4(b): what was changed, prominently.</summary>
    public required string ModificationNotice { get; init; }

    /// <summary>
    /// §4(c) and §4(d): what the upstream tree was found to carry and not carry, with the revision
    /// read and the date it was read on. The negative half is the half worth stating — "there is no
    /// NOTICE file" is a check that was performed, where silence is a check that might not have
    /// been.
    /// </summary>
    public required string SourceNoticeFinding { get; init; }

    /// <summary>
    /// §4(c): the copyright, patent, trademark and attribution notices found in the source form,
    /// retained. Each entry is what the source says, not a summary of it — a paraphrased
    /// acknowledgement is not the acknowledgement the source asked to travel with the work.
    /// </summary>
    public required IReadOnlyList<string> RetainedSourceNotices { get; init; }

    /// <summary>§7, in the terms the licence sets out itself.</summary>
    public required string WarrantyDisclaimerNotice { get; init; }

    public string ToPlainText(string newLine = "\n")
    {
        var builder = new StringBuilder();
        builder.Append(Title).Append(newLine);
        builder.Append("Creator: ").Append(Creator).Append(newLine);
        builder.Append("Licence: ").Append(LicenceStatement).Append(' ').Append(LicenceUri).Append(newLine);
        builder.Append("A copy ships at ").Append(LicencePath).Append('.').Append(newLine);
        builder.Append("Source: ").Append(MaterialUri).Append(newLine);
        builder.Append("Modifications: ").Append(ModificationNotice).Append(newLine);
        builder.Append("Source notices: ").Append(SourceNoticeFinding).Append(newLine);
        foreach (var notice in RetainedSourceNotices)
        {
            builder.Append("  - ").Append(notice).Append(newLine);
        }

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

    public const string OpusMtBibleBigMulEn = "helsinki-opus-mt-tc-bible-big-mul-deu-eng-nld-onnx";

    /// <summary>Where the Apache License 2.0 copy lives, relative to the repository root.</summary>
    public const string ApacheLicencePath = "licences/Apache-License-2.0.txt";

    /// <summary>Where the NVIDIA Open Model License copy lives, relative to the repository root.</summary>
    public const string OpenModelLicencePath = "licences/NVIDIA-Open-Model-License-2025-10-24.txt";

    public const string SileroVad = "silero-vad";

    /// <summary>Where the Silero VAD MIT notice lives, relative to the repository root and the build output.</summary>
    public const string SileroVadLicencePath = "licences/silero-vad-LICENSE.txt";

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
            // CC BY 4.0 §3(a)(1)(B) wants modifications indicated, and what this build actually
            // hands a user is one converted file. It said "quantised (q8_0, q6_k, q5_k, q4_k)"
            // until 2026-08-20, when the quantised ladder was withdrawn from the catalogue: a
            // notice describing conversions the product no longer distributes is a notice about
            // somebody else's files. The GGUF conversion itself is still a modification and still
            // has to be declared.
            ModificationNotice =
                "Modified: the original NVIDIA NeMo checkpoint was converted to the GGUF format (f16, " +
                "unquantised). Uindosill redistributes that converted file and does not redistribute the " +
                "original checkpoint.",
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

        // The translation weights, and the first Apache-2.0 entry. Uindosill exports these itself —
        // the ONNX graphs are produced here from the upstream PyTorch checkpoint by
        // scripts/export-translation-onnx.py — so the modification notice §4(b) wants is a
        // description of this project's own work rather than of somebody else's.
        [OpusMtBibleBigMulEn] = new ApacheAttribution
        {
            Title = "OPUS-MT tc-bible-big mul→deu+eng+nld (machine translation model weights)",
            Creator = "Helsinki-NLP, the Language Technology Research Group at the University of Helsinki",
            LicenceStatement = "Apache License, Version 2.0,",
            LicenceUri = new Uri("https://www.apache.org/licenses/LICENSE-2.0"),
            LicencePath = ApacheLicencePath,
            MaterialUri = new Uri("https://huggingface.co/Helsinki-NLP/opus-mt-tc-bible-big-mul-deu_eng_nld"),
            ModificationNotice =
                "Modified: the original Marian checkpoint at revision bb1ef830d5 was exported to ONNX in the "
                + "merged decoder layout by scripts/export-translation-onnx.py, which splits it into an encoder "
                + "graph and a decoder graph with past key values exposed. The weights are unchanged and "
                + "unquantised — float32 in, float32 out. Uindosill redistributes the exported graphs and does "
                + "not redistribute the original checkpoint.",
            SourceNoticeFinding =
                "The upstream repository was read at the pinned revision bb1ef830d5 on 2026-08-20 — its file "
                + "listing and every text file in it. It ships no NOTICE file, so §4(d) has nothing to "
                + "reproduce, and it carries no copyright, patent or trademark notice anywhere, so none is "
                + "reproduced here rather than one being invented. The attribution notices it does carry are "
                + "these:",
            RetainedSourceNotices =
            [
                "Developed by the Language Technology Research Group at the University of Helsinki, as part of "
                + "the OPUS-MT project (https://github.com/Helsinki-NLP/Opus-MT). Originally trained with "
                + "Marian NMT and converted to PyTorch with the transformers library; training data from OPUS "
                + "(https://opus.nlpl.eu/).",

                "Original model: opusTCv20230926max50+bt+jhubc_transformer-big_2024-08-18.zip, at "
                + "https://object.pouta.csc.fi/Tatoeba-MT-models/mul-deu+eng+nld/"
                + "opusTCv20230926max50+bt+jhubc_transformer-big_2024-08-18.zip",

                "The source asks to be cited: Tiedemann et al., \"Democratizing neural machine translation "
                + "with OPUS-MT\" (Language Resources and Evaluation 58, 2023, doi:10.1007/s10579-023-09704-w); "
                + "Tiedemann and Thottingal, \"OPUS-MT – Building open translation services for the World\" "
                + "(EAMT 2020); and Tiedemann, \"The Tatoeba Translation Challenge – Realistic Data Sets for "
                + "Low Resource and Multilingual MT\" (WMT 2020).",

                "Acknowledgements, in the source's own words: \"The work is supported by the HPLT project, "
                + "funded by the European Union’s Horizon Europe research and innovation programme under "
                + "grant agreement No 101070350. We are also grateful for the generous computational resources "
                + "and IT infrastructure provided by CSC -- IT Center for Science, Finland, and the EuroHPC "
                + "supercomputer LUMI.\"",
            ],
            WarrantyDisclaimerNotice =
                "The work is provided on an \"AS IS\" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, "
                + "either express or implied (section 7 of the License).",
        },

        // The speech-detection graph, and the first MIT entry — the fourth licence shape here. MIT
        // asks for the copyright line and the permission text to travel with the material, so the
        // LICENSE file ships at SileroVadLicencePath and this names it. The graph is installed by URL
        // from the upstream repository at a pinned commit and is not modified; the one thing this
        // project adds is the C# that drives it, which is its own.
        [SileroVad] = new MitAttribution
        {
            Title = "Silero VAD v5 (voice activity detection model, silero_vad.onnx)",
            Creator = "Silero Team",
            CopyrightNotice = "Copyright (c) 2020-present Silero Team",
            LicencePath = SileroVadLicencePath,
            LicenceUri = new Uri("https://opensource.org/license/mit"),
            MaterialUri = new Uri("https://github.com/snakers4/silero-vad"),
            ModificationNotice =
                "Unmodified: the ONNX graph is installed by URL from the upstream repository at commit "
                + "6478567951ae5c9979ad7b234185b5515f4be7a1 (tag v5.1.2) and driven as published. Uindosill "
                + "hosts no copy of it.",
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
        "Apache-2.0 §3 grants a patent licence with the translation weights and terminates it for anyone " +
        "who files patent litigation alleging the model infringes. CC BY 4.0, which the transcription " +
        "weights ship under, licenses no patent rights at all (§2(b)(1)), so the three licences in this " +
        "product make three different patent bargains and none of them is the other's.",

        "The translation checkpoint's own card disclaims its coverage list — \"for a large number of " +
        "language pairs it will not work at all\" — so the language tags on that entry are list " +
        "membership rather than a quality claim, and the product must not present them as one. Measured " +
        "on FLEURS into English, 23 of the 24 clear this project's own bar and Slovak falls below it " +
        "by 0.74.",

        "parakeet-tdt-0.6b-v3 covers 25 European languages. It does not cover Chinese, Japanese, Korean, " +
        "Arabic, Hindi or Thai, and the product must not offer them.",

        // The one restriction that is about what the diariser does rather than about paperwork.
        // NVIDIA Open Model License §2.3 incorporates the Trustworthy AI terms by reference, and
        // their clause (b) names biometric processing specifically — which is what telling voices
        // apart is, under several jurisdictions' definitions. It is listed with the others rather
        // than left in the Agreement because this list is what the CLI and the About window render.
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
            Notes = "Windows media decoding and audio-only playback.",
        },
        new ComponentLicence
        {
            Component = "yt-dlp",
            License = "Unlicense (public domain)",
            Uri = new Uri("https://github.com/yt-dlp/yt-dlp"),
            Notes =
                "Shipped only by builds that vendor the link downloader. Run as a separate process to fetch " +
                "the audio track of a link the user pastes, and by mpv to stream that link's picture.",
        },
        new ComponentLicence
        {
            Component = "Deno",
            License = "MIT",
            Uri = new Uri("https://github.com/denoland/deno"),
            Notes =
                "Shipped beside yt-dlp, which needs a JavaScript runtime for YouTube and enables only this " +
                "one by default. Never runs anything of this application's; it is yt-dlp's dependency.",
        },
        new ComponentLicence
        {
            Component = "FFmpeg (the ffmpeg command-line tool)",
            License = "LGPL-3.0-or-later",
            Uri = new Uri("https://ffmpeg.org"),
            Notes =
                "Shipped only by builds that vendor the muxer. Run as a separate process to put a transcript " +
                "inside a recording as a subtitle track, which copies every stream and encodes nothing — so " +
                "the LGPL build is enough and the GPL one, which would be GPLv3, is deliberately not used. A " +
                "separate program rather than a linked library, so unlike libmpv it does not reach this " +
                "application's own terms. Its licence text travels beside the binary; see " +
                "docs/NATIVE-BINARIES.md for the pin and why it is not vendored beside yt-dlp.",
        },
        new ComponentLicence
        {
            Component = "libmpv (mpv media player), and the FFmpeg and other libraries linked into it",
            License = "GPL-2.0-or-later — copyleft, and the reason a build carrying it is distributed under the GPL",
            Uri = new Uri("https://github.com/mpv-player/mpv"),
            Notes =
                "Shipped only by builds that vendor the video player; a build without it draws no picture and " +
                "contains no GPL component. Uindosill's own source is MIT, but a distribution including this " +
                "binary is GPLv2-or-later as a whole. The licence text, mpv's copyright summary and the written " +
                "offer naming where the corresponding source lives travel beside the binary — see " +
                "licences/mpv-WRITTEN-OFFER.txt and docs/LICENSING.md.",
        },
        new ComponentLicence
        {
            Component = "CommunityToolkit.Mvvm",
            License = "MIT",
            Uri = new Uri("https://github.com/CommunityToolkit/dotnet"),
        },
        // The two typefaces the window is drawn in, embedded in the desktop application as static
        // instances of the upstream variable fonts. The SIL Open Font Licence is what makes that
        // legal, and it is the one licence here with a condition about the *name*: §5 forbids
        // redistributing a modified font under its reserved name, so these ship unmodified and
        // under their own names rather than subsetted, which is also why the whole face is here
        // when the interface uses a few hundred glyphs. The licence text travels in licences/,
        // because OFL requires the copyright notice and the licence with every copy — the CLI zip
        // carries neither font, having no window to draw.
        new ComponentLicence
        {
            Component = "Instrument Sans (typeface)",
            License = "OFL-1.1",
            Uri = new Uri("https://github.com/Instrument/instrument-sans"),
            Notes =
                "Copyright 2022 The Instrument Sans Project Authors. Shipped in the desktop application "
                + "only; licences/InstrumentSans-OFL.txt travels with it.",
        },
        new ComponentLicence
        {
            Component = "Chivo Mono (typeface)",
            License = "OFL-1.1",
            Uri = new Uri("https://github.com/Omnibus-Type/Chivo"),
            Notes =
                "Copyright 2019 The Chivo Project Authors. Shipped in the desktop application only; "
                + "licences/ChivoMono-OFL.txt travels with it.",
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
        // Ships twice, and one row says so rather than two rows saying half each. Since 2026-08-21
        // it arrives as the `onnxruntime-webgpu` wheel inside the bundled Python, where the diariser
        // and the translator run; since 2026-08-23 it is a NuGet package beside the managed
        // assemblies again, where the speech-detection graph runs in process. The obligation is the
        // same for both copies: it is MIT and it statically links dozens of third-party components
        // with their own notices — Intel MKL, protobuf, Eigen, oneDNN, abseil, XNNPACK and the rest —
        // whose ThirdPartyNotices.txt is shipped verbatim rather than summarised into a row here,
        // because summarising it would mean inventing a licence for each.
        new ComponentLicence
        {
            Component = "ONNX Runtime (Microsoft.ML.OnnxRuntime 1.29.0 beside the .NET assemblies, for speech detection; " +
                        "onnxruntime-webgpu 1.27.0 inside the bundled Python, for the diariser and the translator)",
            License = "MIT",
            Uri = new Uri("https://github.com/microsoft/onnxruntime"),
            Notes =
                "Copyright (c) Microsoft Corporation. Runs the speech-detection model in process and the " +
                "speaker diarisation and translation models in the bundled Python. Bundles many third-party " +
                "components under their own licences; ONNX Runtime's ThirdPartyNotices.txt is redistributed " +
                "verbatim rather than summarised here. The copy in licences/ is the 1.29.0 .NET package's, " +
                "which is the in-process copy's own; reconciling it against the 1.27.0 wheel's is an open " +
                "item in docs/LICENSING.md.",
        },
        // The interpreter and its packages, as one row rather than fifty-one. That is a decision and
        // not a shortcut: enumerating them is owed, docs/LICENSING.md records the enumeration as far
        // as it has been taken, and every attempt so far has been made against a development virtual
        // environment rather than against an assembled bundle — which is known to differ by at least
        // one package. A row per package written from the wrong environment would be a notice that
        // names things this product does not ship and omits things it does.
        new ComponentLicence
        {
            Component = "Bundled CPython 3.12.10 and the Python packages the engines run in",
            License = "PSF License (CPython) and the packages' own — see docs/LICENSING.md",
            Uri = new Uri("https://docs.python.org/3/license.html"),
            Notes =
                "The speaker labelling and English opt-ins run out of process in an interpreter that " +
                "ships inside this application; nothing is installed on the machine. Its packages are " +
                "pinned in python/requirements-bundle.txt and are predominantly Apache-2.0, BSD and MIT, " +
                "with LGPL-2.1 native libraries among them. The full enumeration and what is still " +
                "unverified about it are in docs/LICENSING.md.",
        },
        // NVIDIA's own source, called rather than reimplemented. Two files, unmodified, under
        // Apache-2.0 — separate from the diarisation *weights*, which are under the NVIDIA Open
        // Model License and have their own row. NOTICE.md carries the §4 check.
        new ComponentLicence
        {
            Component = "NVIDIA NeMo (vendored source, python/uindosill_engines/_vendor/nemo/)",
            License = "Apache-2.0",
            Uri = new Uri("https://github.com/NVIDIA/NeMo"),
            Notes =
                "Copyright (c) 2025, NVIDIA CORPORATION. Two files carried unmodified so the diariser's " +
                "arrival-order speaker cache is NVIDIA's own code rather than a port of it; the rest of " +
                "the vendored tree is stubs written for this project. See NOTICE.md.",
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
