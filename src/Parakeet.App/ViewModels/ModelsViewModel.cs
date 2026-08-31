using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parakeet.App.Services;
using Parakeet.Core.Licensing;
using Parakeet.Core.Models;
using Parakeet.Engine.LlamaServer;
using Parakeet.Core.Transcription;

namespace Parakeet.App.ViewModels;

public sealed partial class ModelViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(CanUseForSpeakers))]
    private bool _isInstalled;

    [ObservableProperty]
    private double _progress;

    /// <summary>
    /// True for the one entry currently in memory. The list showed "Installed" against every
    /// downloaded model and nothing about which of them was actually loaded, which is the state
    /// the user is choosing from.
    /// </summary>
    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isBusy;

    /// <summary>
    /// True for the diariser speaker labelling will actually use. Meaningless on every other entry.
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="IsLoaded"/> for the second task that has more than one model:
    /// two diarisers can be installed at once and only one of them runs, and without this the list
    /// says "Installed" against both and nothing about which. Set by the tab rather than read from
    /// the descriptor — it is a choice, not a property of the weights.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseForSpeakers))]
    private bool _isActiveDiariser;

    public ModelViewModel(ModelDescriptor descriptor, bool installed)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
        _isInstalled = installed;
        _status = installed ? "Installed" : "Not installed";
    }

    public ModelDescriptor Descriptor { get; }

    public string Id => Descriptor.Id;

    public string DisplayName => Descriptor.DisplayName;

    /// <summary>
    /// The upstream artefact's own name, shown under the friendly one.
    /// </summary>
    /// <remarks>
    /// <see cref="DisplayName"/> answers "which of these do I need" and this answers "which exact
    /// file is that", which are different questions asked by different people — one choosing a
    /// download, the other checking that the thing on disk is the thing a licence notice or a
    /// measurement refers to. The display name used to be this string, so the tab answered the
    /// second question and left the first one to guesswork.
    ///
    /// Not the id: the id is this project's key for the entry, while the family and quantisation
    /// are what the upstream repository calls the weights, which is what somebody comparing
    /// against a model card or a run report is holding.
    /// </remarks>
    public string TechnicalName => $"{Descriptor.Family} · {Descriptor.Quantisation}";

    /// <summary>
    /// What the weights do, for the entries that do not transcribe: a diarisation or translation
    /// model can be downloaded from this tab like any other and must never be offered to Load as
    /// the engine.
    /// </summary>
    public bool IsTranscriptionModel => Descriptor.Task == ModelTask.Transcription;

    /// <summary>True for an entry that labels speakers, which is the one task with a choice to make.</summary>
    public bool IsDiarisationModel => Descriptor.Task == ModelTask.Diarisation;

    /// <summary>
    /// Whether "Use for speakers" is offered on this entry: a diariser, installed, not already the
    /// one in use.
    /// </summary>
    public bool CanUseForSpeakers => IsDiarisationModel && IsInstalled && !IsActiveDiariser;

    /// <summary>
    /// The badge on an entry that is not the ASR weights. Named per task rather than "not
    /// transcription": the badge said SPEAKERS for everything that was not an ASR model, which was
    /// true while diarisation was the only other task and would have labelled the first translation
    /// entry as a diariser.
    /// </summary>
    public string TaskLabel => Descriptor.Task switch
    {
        ModelTask.Diarisation => "SPEAKERS",
        ModelTask.Translation => "ENGLISH",
        ModelTask.VoiceActivity => "SPEECH",
        ModelTask.Answering => "ASK",
        _ => string.Empty,
    };

    /// <summary>
    /// Why this machine may not be able to run this entry, or null when nothing is in the way.
    /// Shown beside the entry rather than on the download button: it is a warning, not a refusal,
    /// and the download is the reader's to make.
    /// </summary>
    /// <remarks>
    /// Only the answering entries are ever big enough for this to say anything — see
    /// <see cref="ModelFit"/>, whose rule is anchored to what this project has measured running
    /// and not running on a 16 GiB machine.
    /// </remarks>
    public string? FitWarning => ModelFit.WhyItMightNotRun(Descriptor, ModelFit.TotalPhysicalBytes());

    /// <summary>Whether <see cref="FitWarning"/> has anything to say.</summary>
    public bool HasFitWarning => FitWarning is not null;

    public string Licence => Descriptor.License;

    public string Notes => Descriptor.Notes ?? string.Empty;

    public string Languages => Descriptor.Languages.Count == 0
        ? "unspecified"
        : string.Join(" ", Descriptor.Languages);

    /// <summary>
    /// Shown next to every entry whose provenance has not been checked. Silence here would let
    /// a guessed URL and an unpinned digest look exactly like a verified one.
    /// </summary>
    public string Provenance => (Descriptor.Verified, Descriptor.IsFullyPinned) switch
    {
        (true, true) => "Checked. Uindosill knows this file's fingerprint and will refuse the download if it does not match.",
        (true, false) => "Uindosill knows where this file comes from, but has no fingerprint to check the download against.",
        (false, true) => "Uindosill has a fingerprint to check the download against, but never confirmed where the file comes from.",
        (false, false) => "Uindosill cannot check where this file comes from or whether it arrives intact. " +
                          "You have to allow that explicitly below.",
        // An entry of several files is pinned only when every one of them is. One unpinned file
        // among nine cannot be reported as "digest pinned": the set is as checked as its weakest
        // member, and this string is the only place a user is told which it is.
    };

    /// <summary>
    /// Whether <see cref="Provenance"/> is a confirmation rather than a caveat. Only the fully
    /// checked case is one: the other three each name something that was never verified. The view
    /// painted all four in the warning colour, so "digest pinned" — the reassuring case, and the
    /// one every shipped entry is in — read as a problem.
    /// </summary>
    public bool ProvenanceIsVerified => Descriptor.Verified && Descriptor.IsFullyPinned;

    public bool NeedsUnverifiedOptIn => !Descriptor.IsFullyPinned;

    /// <summary>
    /// Downloading is only meaningful for a model that is not already here. Binding the button to
    /// <see cref="IsBusy"/> alone left Download live on an installed entry, offering to re-fetch
    /// 1.34 GiB over a file the store already has — beside a Remove button that was correctly
    /// disabled on the opposite condition. A cancelled download does not set
    /// <see cref="IsInstalled"/>, so the resume path keeps its enabled button.
    /// </summary>
    public bool CanDownload => !IsBusy && !IsInstalled;
}

/// <summary>
/// A weights file in the model directory that no catalogue entry claims.
/// </summary>
/// <remarks>
/// <para>
/// These are real and they are not small. The four quantisations withdrawn from the catalogue on
/// 2026-08-20 stayed on the disk of everyone who had installed one — about 3 GiB on this
/// maintainer's machine — and the tab that manages models did not admit they existed: it lists
/// catalogue entries, and they stopped being entries. So the folder the tab names at the top of
/// itself held several gigabytes the tab would neither show nor remove, while
/// <c>uindosill models</c> listed them under a heading of their own.
/// </para>
/// <para>
/// Deliberately thinner than <see cref="ModelViewModel"/>. There is no licence, no provenance and
/// no Load button here, because none of those are knowable: what is known about one of these is
/// its name and its size, and offering to load a file the catalogue cannot describe would be
/// offering to run weights this build cannot say anything true about.
/// </para>
/// <para>
/// <b>Unless the catalogue does know the name</b>, which it may. A multi-file entry's weights are
/// only ever looked for under that entry's own directory, so a copy of them lying in the root
/// belongs to nothing as far as the store is concerned — and the tab said exactly that, about
/// files whose names and sizes the manifest lists to the byte, directly under an entry reading Not
/// installed that was offering to download them again. <see cref="ClaimedBy"/> is that entry when
/// there is one, and it turns the row from a stray into a model in the wrong folder.
/// </para>
/// </remarks>
public sealed class SideloadedItemViewModel
{
    public SideloadedItemViewModel(
        string name,
        long sizeBytes,
        bool isDirectory = false,
        ModelDescriptor? claimedBy = null,
        bool claimedEntryInstalled = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        SizeBytes = sizeBytes;
        IsDirectory = isDirectory;
        ClaimedBy = claimedBy;
        ClaimedEntryInstalled = claimedEntryInstalled;
    }

    public string Name { get; }

    /// <summary>
    /// Whether this row is a folder rather than a file, which decides how it is removed.
    /// </summary>
    /// <remarks>
    /// A folder is never a misplaced entry's weights: only a bare file in the root can be that, so
    /// <see cref="ClaimedBy"/> is always null here and Move into place never offers itself. What a
    /// folder can be is an entry that left the catalogue — a retired diariser's 332 MB — which is
    /// the case this list could not show at all until 2026-08-28.
    /// </remarks>
    public bool IsDirectory { get; }

    public long SizeBytes { get; }

    public string SizeLabel => ByteSize.Describe(SizeBytes);

    /// <summary>
    /// The catalogue entry that declares a file of this name, or null when nothing does.
    /// </summary>
    /// <remarks>
    /// One entry, though the shape allows a name declared by more than one — until 2026-08-29 the
    /// two 26B answering entries shipped the same drafting head, and which of them a loose head
    /// belonged to was not decidable from the head. So <see cref="ModelsViewModel"/> only sets this
    /// where the answer is single, which holds for the next collision too.
    /// </remarks>
    public ModelDescriptor? ClaimedBy { get; }

    /// <summary>Whether the entry that declares this name is already installed in its own directory.</summary>
    /// <remarks>
    /// The difference between weights that are merely in the wrong place and a second copy of
    /// weights already sitting in the right one. Only the first can be moved into place; the
    /// second is a duplicate, and which copy to keep is not this tab's call to make.
    /// </remarks>
    public bool ClaimedEntryInstalled { get; }

    /// <summary>Whether this file is a known model in the wrong folder rather than a stray.</summary>
    public bool IsMisplaced => ClaimedBy is not null && !ClaimedEntryInstalled;

    /// <summary>
    /// What the row says about itself under its name: the entry it belongs to, or nothing at all
    /// when the catalogue has never heard of it.
    /// </summary>
    public string PlacementLabel => ClaimedBy is not { } model
        ? string.Empty
        : ClaimedEntryInstalled
            ? $"a second copy of “{model.DisplayName}”, which is installed"
            : $"belongs to “{model.DisplayName}”: wrong folder";

    /// <summary>Whether there is anything to say under the name. Bound rather than converted.</summary>
    public bool HasPlacementLabel => PlacementLabel.Length > 0;
}

public sealed partial class ModelsViewModel : ObservableObject
{
    private readonly IModelStore _store;
    private readonly ModelCatalog _catalog;
    private readonly Func<ModelInstaller> _installerFactory;
    private readonly ModelSession? _session;
    private readonly Func<ComputeBackend>? _backend;
    private readonly AppSettingsStore _settings;
    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoad))]
    [NotifyPropertyChangedFor(nameof(LoadHint))]
    [NotifyPropertyChangedFor(nameof(CanUnload))]
    [NotifyPropertyChangedFor(nameof(ShowEnginePanel))]
    [NotifyPropertyChangedFor(nameof(WhereItRuns))]
    [NotifyPropertyChangedFor(nameof(HasWhereItRuns))]
    private ModelViewModel? _selected;

    /// <summary>
    /// Whether to draw the recogniser's engine panel at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only on a transcription entry, since 2026-08-29.</b> The panel is the window's one ASR
    /// engine and it used to be drawn under every entry, so selecting the translator produced a
    /// block headed SPEECH RECOGNITION ENGINE with a Backend picker and a dead Load button, none of
    /// which had anything to do with what had just been clicked. It carried a sentence apologising
    /// for itself, which is what a control says when it is in the wrong place.
    /// </para>
    /// <para>
    /// <b>Retitling it was not enough and that is worth recording.</b> The first repair named the
    /// panel honestly, which fixed what it *claimed* to be and left it exactly where it was; the
    /// complaint was never the title but that it appears at all under a model it cannot load. The
    /// Unload button goes with it, which is the one thing this costs: unloading now means selecting
    /// a transcription entry first. That is where somebody looking for the recogniser would click.
    /// </para>
    /// </remarks>
    public bool ShowEnginePanel => Selected is { IsTranscriptionModel: true };

    /// <summary>
    /// Where the selected entry actually runs, for entries the engine panel no longer speaks for.
    /// </summary>
    /// <remarks>
    /// This is the useful half of the sentence the panel used to carry. It belongs to the entry
    /// rather than to the engine, so it is shown in the entry's own detail beside "Speaker
    /// labelling uses this model" and the rest of what that entry has to say about itself.
    /// </remarks>
    public string? WhereItRuns
    {
        get
        {
            if (Selected is not { } model || model.IsTranscriptionModel)
            {
                return null;
            }

            // Named per task rather than "not a transcription model", which describes what it is
            // not and leaves the reader to work out what it is for. The place is named with the
            // control, because the passes live on different tabs and a control named without its
            // page is a repair nobody can act on.
            //
            // Answering gets its own sentence: the three passes genuinely load at batch start
            // beside the recogniser, and the answer engine is a child process loaded on demand
            // after a transcript is finished — "alongside the recogniser" over that one would put
            // two claims in one sentence that contradict each other.
            if (model.Descriptor.Task == ModelTask.Answering)
            {
                return "Runs from the Ask tab, on a finished transcript, after the recogniser's "
                    + "work is done. There is nothing to load here.";
            }

            var used = model.Descriptor.Task switch
            {
                ModelTask.Diarisation => "'Label speakers' on the Transcribe tab",
                ModelTask.Translation => "'Translate to English' on the Transcribe tab",
                ModelTask.VoiceActivity => "'Neural speech detection' on the Advanced tab of Settings",
                _ => "its own opt-in",
            };

            return $"Runs from {used}, alongside the recogniser. There is nothing to load here.";
        }
    }

    public bool HasWhereItRuns => WhereItRuns is { Length: > 0 };

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _allowUnverified;

    /// <summary>
    /// Blocks load and unload while a transcription is in flight, because the running batch is
    /// holding the very engine an unload would dispose.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoad))]
    [NotifyPropertyChangedFor(nameof(LoadHint))]
    [NotifyPropertyChangedFor(nameof(CanUnload))]
    [NotifyPropertyChangedFor(nameof(CanRemoveSideloaded))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSideloadedCommand))]
    [NotifyPropertyChangedFor(nameof(CanMoveIntoPlace))]
    [NotifyCanExecuteChangedFor(nameof(MoveIntoPlaceCommand))]
    private bool _isTranscribing;

    public ModelsViewModel(
        IModelStore store,
        ModelCatalog catalog,
        Func<ModelInstaller>? installerFactory = null,
        ModelSession? session = null,
        Func<ComputeBackend>? backend = null,
        AppSettingsStore? settings = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);

        _store = store;
        _catalog = catalog;
        _session = session;
        _backend = backend;
        _settings = settings ?? new AppSettingsStore();

        // The token is read per request rather than captured here, so that pasting one into
        // Settings makes the gated entry installable without restarting the app — and so that
        // clearing it takes effect just as immediately. `_settings` is assigned above because this
        // closure reads it.
        _installerFactory = installerFactory ?? DefaultInstaller;

        ModelInstaller DefaultInstaller() => new(
            store,
            huggingFaceToken: () => HuggingFaceToken.Resolve(_settings.Load().HuggingFaceToken));

        Models = [.. catalog.Models.Select(m => new ModelViewModel(m, store.IsInstalled(m)))];
        Selected = Models.FirstOrDefault(m => m.IsInstalled && m.IsTranscriptionModel)
            ?? Models.FirstOrDefault(m => m.IsTranscriptionModel)
            ?? Models.FirstOrDefault();

        SyncActiveDiariser();

        Refresh();

        if (_session is not null)
        {
            _session.Changed += (_, _) => SyncSession();
        }
    }


    /// <summary>
    /// Makes one installed diariser the one speaker labelling uses, and remembers it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One entry does this job since 2026-08-27</b>, so this command is effectively never
    /// enabled: it needs an installed diarisation entry that is not already the active one. It is
    /// kept rather than removed because the reasoning that made it a choice is intact — two entries
    /// did the job that morning and neither was simply better, so the tab asked rather than deciding
    /// — and a second entry would want it back. A permanently disabled control is the cost, and it
    /// is noted here so the next reader knows it is deliberate.
    /// </para>
    /// <para>
    /// <b>The choice is written before the list is updated</b>, and the list is only updated when
    /// the write succeeded. A tab showing a tick against a model that a read-only settings file
    /// rejected would be telling the user their next run will use something it will not.
    /// </para>
    /// </remarks>
    [RelayCommand]
    private void UseForSpeakers(ModelViewModel? model)
    {
        if (model is not { CanUseForSpeakers: true })
        {
            return;
        }

        if (!_settings.Update(current => current with { DiarisationModelId = model.Id }))
        {
            StatusMessage = $"That choice could not be saved to {_settings.Path}, so speaker labelling "
                + "will keep using the model it was using.";
            return;
        }

        SyncActiveDiariser();
        StatusMessage = $"{model.DisplayName} will label speakers from now on.";
    }

    /// <summary>
    /// Marks the diariser a run would actually pick, which is not always the one that was chosen.
    /// </summary>
    /// <remarks>
    /// It resolves the same way <c>EngineProvider.DiarisationModel</c> does — the stored id when its
    /// entry is installed, otherwise the first installed diariser — because a tab that showed the
    /// stored choice rather than the effective one would put a tick beside a model that has been
    /// removed, while a different one did the work.
    /// </remarks>
    private void SyncActiveDiariser()
    {
        var installed = Models.Where(m => m.IsDiarisationModel && m.IsInstalled).ToList();
        var stored = _settings.Load().DiarisationModelId;

        var active = installed.FirstOrDefault(
                m => string.Equals(m.Id, stored, StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault();

        foreach (var model in Models.Where(m => m.IsDiarisationModel))
        {
            model.IsActiveDiariser = ReferenceEquals(model, active);
        }
    }

    public ObservableCollection<ModelViewModel> Models { get; }

    /// <summary>
    /// Weights in the model directory that no catalogue entry claims. Usually empty.
    /// </summary>
    public ObservableCollection<SideloadedItemViewModel> Sideloaded { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSideloadedCommand))]
    [NotifyPropertyChangedFor(nameof(CanRemoveSideloaded))]
    [NotifyCanExecuteChangedFor(nameof(MoveIntoPlaceCommand))]
    [NotifyPropertyChangedFor(nameof(CanMoveIntoPlace))]
    private SideloadedItemViewModel? _selectedSideloaded;

    /// <summary>Whether the sideloaded section is drawn at all. Nothing to say when there is nothing there.</summary>
    public bool HasSideloaded => Sideloaded.Count > 0;

    public string ModelDirectory => _store.RootDirectory;

    /// <summary>The CC BY 4.0 notice, rendered in the application rather than only in a repo file.</summary>
    public string Attribution => string.Join(
        Environment.NewLine,
        Attributions.ById.Values.Select(a => a.ToPlainText(Environment.NewLine)));

    public ModelDescriptor? SelectedDescriptor => Selected?.Descriptor;

    /// <summary>
    /// What is in memory right now, in one line, naming the backend the engine reported rather
    /// than the one that was requested. Those differ when the native loader falls back, and the
    /// difference is the whole reason somebody asks whether a run used the GPU.
    /// </summary>
    public string LoadedSummary
    {
        get
        {
            if (_session is null)
            {
                return "No model is loaded.";
            }

            if (_session.IsBusy)
            {
                return "Loading…";
            }

            if (!_session.IsLoaded)
            {
                // It said "Choose a model and press Load before transcribing", which stopped being
                // true on 2026-08-23 when Start began loading for itself. A window telling somebody
                // to press a button they do not need is the same defect as one that hides a button
                // they do — and this one had the additional problem of describing a refusal that no
                // longer happens.
                return "Nothing loaded. Transcribing loads it: press Load first only if you want "
                       + "to choose the backend.";
            }

            var name = _session.Model?.DisplayName ?? "a model";
            var backend = _session.LoadedBackend?.ToString().ToLowerInvariant() ?? "unknown backend";
            var took = _session.LoadDuration is { } d
                ? string.Create(CultureInfo.InvariantCulture, $", loaded in {d.TotalSeconds:0.0} s")
                : string.Empty;

            // Three answers, not two: the backend that was asked for, another one, or none known — a
            // library found in a flat directory or on the search path has no backend in its path, and
            // that is not a fallback, it is a gap the transcript's provenance now records as one.
            var fellBack = _session.RequestedBackend is not { } requested
                ? string.Empty
                : _session.LoadedBackend is null
                    ? "  ⚠ The library was found outside a backend directory, so which backend is running is not known."
                    : _session.LoadedBackend != requested
                        ? $"  ⚠ {requested.ToString().ToLowerInvariant()} was requested: the native loader fell back."
                        : string.Empty;

            return $"Loaded: {name} on {backend}{took}.{fellBack}";
        }
    }

    public bool IsLoaded => _session?.IsLoaded ?? false;

    public bool IsSessionBusy => _session?.IsBusy ?? false;

    /// <summary>
    /// The backend cannot change after the first load in a process, so the window says that rather
    /// than offering a control that silently does nothing.
    /// </summary>
    public string BackendNote => _session?.IsBackendFixed == true
        ? "The backend is fixed for this process once a model has loaded, restart to change it."
        : "Choose the backend before loading. It cannot be changed again without restarting.";

    public bool CanLoad =>
        _session is not null && !_session.IsBusy && !IsTranscribing && Selected is { IsInstalled: true, IsTranscriptionModel: true };

    /// <summary>
    /// Why Load is dark, or null when it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This window's rule, stated at every opt-in checkbox in it, is that a
    /// disabled control says why — because one that quietly does nothing is worse than one that explains
    /// itself. These two buttons were the exception, and the case that made it visible is a
    /// diarisation entry: the panel is the transcription engine's, so selecting Speaker labelling
    /// darkens Load with no way to find out that it was never going to apply.
    /// </para>
    /// <para>
    /// Silent while a load is in flight, because <see cref="LoadedSummary"/> already says
    /// "Loading…" directly above and two sentences about one state read as two states.
    /// </para>
    /// </remarks>
    public string? LoadHint
    {
        get
        {
            if (_session is null || CanLoad || _session.IsBusy)
            {
                return null;
            }

            if (IsTranscribing)
            {
                return "A batch is running. The engine it is decoding with cannot be swapped until it finishes.";
            }

            if (Selected is not { } model)
            {
                return "Choose a model on the left.";
            }

            // **Nothing is said here about a non-transcription entry any more.** This branch
            // explained that the panel was the recogniser's and that the selected model ran
            // somewhere else, which is a sentence the panel needed only because it was drawn
            // where it did not belong. The panel is hidden for those entries now (see
            // ShowEnginePanel) and the useful half of that sentence moved to the entry's own
            // detail as WhereItRuns, so a hint here would be text under a block nobody sees.
            if (!model.IsTranscriptionModel)
            {
                return null;
            }

            return model.IsInstalled ? null : "Download it first.";
        }
    }

    public bool CanUnload => _session is { IsLoaded: true, IsBusy: false } && !IsTranscribing;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_session is null || !CanLoad || Selected is not { } model)
        {
            return;
        }

        StatusMessage = null;

        try
        {
            await _session.LoadAsync(new EngineSelection
            {
                Backend = _backend?.Invoke() ?? ComputeBackend.Vulkan,
                Model = model.Descriptor,
            }).ConfigureAwait(true);

            StatusMessage = _session.IsLoaded ? null : "The model did not load.";
        }
#pragma warning disable CA1031 // A load failure belongs on screen next to the button that caused it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task UnloadAsync()
    {
        if (_session is null || !CanUnload)
        {
            return;
        }

        await _session.UnloadAsync().ConfigureAwait(true);
        StatusMessage = "Unloaded. The weights are out of memory.";
    }

    /// <summary>Mirrors the session onto the observable surface the window binds to.</summary>
    private void SyncSession()
    {
        var loadedId = _session?.Model?.Id;

        foreach (var model in Models)
        {
            model.IsLoaded = loadedId is not null && model.Id == loadedId;
        }

        OnPropertyChanged(nameof(LoadedSummary));
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(IsSessionBusy));
        OnPropertyChanged(nameof(BackendNote));
        OnPropertyChanged(nameof(CanLoad));
        OnPropertyChanged(nameof(LoadHint));
        OnPropertyChanged(nameof(CanUnload));
    }

    [RelayCommand]
    private void CancelDownload() => _cancellation?.Cancel();

    [RelayCommand]
    private async Task DownloadAsync()
    {
        // The same condition the button binds to, so a command reached any other way — a keyboard
        // accelerator, a test, a future context menu — cannot do what the disabled button will not.
        if (Selected is not { } model || !model.CanDownload)
        {
            return;
        }

        if (model.NeedsUnverifiedOptIn && !AllowUnverified)
        {
            StatusMessage =
                "There is no fingerprint for this file, so Uindosill cannot tell whether it arrives intact. " +
                "Tick the box above to download it anyway.";
            return;
        }

        model.IsBusy = true;
        model.Status = "Starting";
        StatusMessage = null;
        _cancellation = new CancellationTokenSource();

        try
        {
            using var installer = _installerFactory();
            var progress = new Progress<ModelInstallProgress>(p =>
            {
                model.Progress = (p.Fraction ?? 0) * 100;
                model.Status = p.Phase switch
                {
                    ModelInstallPhase.Connecting => "Connecting",
                    ModelInstallPhase.Downloading => Describe(p),
                    ModelInstallPhase.Verifying => "Verifying checksum",
                    ModelInstallPhase.Installing => "Installing",
                    _ => "Done",
                };
            });

            var result = await installer.InstallAsync(
                model.Descriptor,
                new ModelInstallOptions { AllowUnverified = AllowUnverified },
                progress,
                _cancellation.Token).ConfigureAwait(true);

            model.IsInstalled = true;
            // A first diariser becoming installed makes it the active one; a second does not
            // displace the first. SyncActiveDiariser resolves both without a special case.
            SyncActiveDiariser();
            model.Status = "Installed";
            model.Progress = 100;
            // **The unpinned branch became a normal outcome on 2026-08-27**, when a catalogue entry
            // arrived whose repository is gated and whose digests therefore cannot be published in
            // the manifest. It had been unreachable in shipped builds, so it carried a sentence
            // written for whoever maintains the catalogue — "Pin these in the catalogue" followed by
            // a list of hashes — which is not something to put in front of somebody who has just
            // downloaded a model. The digests still matter to the maintainer, so they move to a
            // developer build rather than disappearing.
            if (model.Descriptor.IsFullyPinned)
            {
                StatusMessage = "Installed and verified.";
            }
            else
            {
#if DEBUG
                StatusMessage = "Installed. Not checked against a published digest, pin these in the catalogue: " +
                                string.Join("; ", result.Files.Select(f => $"{f.FileName} {f.Sha256}"));
#else
                StatusMessage = "Installed. This one could not be checked against a published fingerprint, "
                                + "because the people who publish it do not list one.";
#endif
            }
        }
        catch (OperationCanceledException)
        {
            model.Status = "Cancelled";
            StatusMessage = "Download cancelled. Partial progress is kept and will resume.";
        }
        catch (ModelInstallException ex)
        {
            model.Status = "Failed";
            StatusMessage = ex.Message;
        }

        // **The backstop, added 2026-08-29 after this method took the application down with it.**
        // Hugging Face ended a response after 149 KB of a 6.3 GB file; `HttpIOException` matched
        // neither clause above, escaped an async command — where nothing is awaiting it and there
        // is no handler above it — and the process was terminated with the download half-written
        // and the window gone.
        //
        // `ModelInstaller` now retries a dropped connection and turns a persistent one into a
        // `ModelInstallException`, which is the real fix and is why the clause above still carries
        // the message a user reads. This clause is here because **a download must never be able to
        // close the window**, whatever it throws: the partial file survives, the catalogue is
        // untouched, and the honest outcome of any of these is a row that says it failed.
        catch (Exception ex) when (ex is IOException or HttpRequestException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            model.Status = "Failed";
            StatusMessage = $"The download stopped: {ex.Message} What arrived is kept, so starting " +
                            "again will resume rather than begin from nothing.";
        }
        finally
        {
            model.IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;

            // What the folder holds has changed, whether the download finished, failed or was
            // cancelled — a resumable partial is not installed but the total on disk moved.
            Refresh();
        }
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is not { } model)
        {
            return;
        }

        // Deleting the file under a loaded engine leaves the window claiming a model is resident
        // while its weights are gone from disk — recoverable only by noticing, which is the kind of
        // quiet inconsistency this application exists to not produce.
        if (model.IsLoaded)
        {
            StatusMessage = "That model is loaded. Unload it first, then remove it.";
            return;
        }

        var removed = _store.Remove(model.Descriptor);
        model.IsInstalled = false;
        // Removing the active diariser hands the job to whatever is left, or to nothing.
        SyncActiveDiariser();
        model.Progress = 0;
        model.Status = "Not installed";
        StatusMessage = removed ? $"Removed {model.Id}." : $"{model.Id} was not installed.";
        Refresh();
    }

    /// <summary>Whether there is anything installed for <see cref="RemoveAllCommand"/> to remove.</summary>
    public bool CanRemoveAll => !IsTranscribing && Models.Any(m => m.IsInstalled);

    /// <summary>
    /// Removes every installed catalogue entry in one action.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added 2026-08-29, because the folder notice was true and useless.</b> It stated that the
    /// weights outlive an uninstall and stopped, leaving a reader who had just decided to uninstall
    /// with six entries to select and Remove one at a time. Tens of gigabytes stay behind when
    /// somebody does not know to do that, and "it is in the notice" is not a defence when the notice
    /// frames the survival as a feature.
    /// </para>
    /// <para>
    /// <b>It stayed when the uninstaller learned to ask, later the same day.</b> The hook built and
    /// withdrawn on 2026-08-23 deleted silently; the one that replaced it asks first
    /// (<see cref="UninstallPrompt"/>). This button is still the route that does not depend on any
    /// of that: it is for somebody already looking at the folder, it works below the prompt's
    /// <see cref="UninstallPrompt.AskAboveBytes"/> threshold where nothing is asked at all, and it
    /// is the only route left on a machine where the callback does the nothing it was once measured
    /// doing. The rule both obey is unchanged: nothing this product does unattended deletes a user's
    /// files. A button is attended, and so is a dialog.
    /// </para>
    /// <para>
    /// <b>Catalogue entries only.</b> What else is in the folder is offered separately by
    /// <see cref="RemoveSideloadedCommand"/>, because this application did not put it there and
    /// cannot say what it is. A loaded model is skipped rather than deleted from under the engine,
    /// and named, so the count and the message agree with what actually happened.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRemoveAll))]
    private void RemoveAll()
    {
        var freed = 0L;
        var removed = 0;
        var skipped = new List<string>();

        foreach (var model in Models.Where(m => m.IsInstalled).ToList())
        {
            if (model.IsLoaded)
            {
                skipped.Add(model.DisplayName);
                continue;
            }

            freed += model.Descriptor.TotalSizeBytes ?? 0;
            if (_store.Remove(model.Descriptor))
            {
                removed++;
            }

            model.IsInstalled = false;
            model.Progress = 0;
            model.Status = "Not installed";
        }

        SyncActiveDiariser();

        StatusMessage = removed == 0
            ? "Nothing was removed."
            : $"Removed {removed} {(removed == 1 ? "model" : "models")}, freeing about "
              + $"{ByteSize.Describe(freed)}."
              + (skipped.Count > 0
                  ? $" {string.Join(" and ", skipped)} stayed, being loaded. Unload and try again."
                  : string.Empty);

        Refresh();
    }

    /// <summary>
    /// Re-reads the model directory: which entries are installed, what else is in there, and what
    /// the whole folder now comes to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This existed and nothing called it. Every fact on this tab was therefore established once,
    /// at construction, and the only things that ever moved afterwards were the ones this view
    /// model changed itself — so a download finishing in another copy of the application, a file
    /// deleted in Explorer, or weights left behind by an older version were all invisible until
    /// the window was restarted.
    /// </para>
    /// <para>
    /// It is cheap on purpose: file existence and lengths, no hashing. <see cref="IModelStore"/>
    /// says why that is the right trade for something a tab switch runs.
    /// </para>
    /// </remarks>
    public void Refresh()
    {
        foreach (var model in Models)
        {
            model.IsInstalled = _store.IsInstalled(model.Descriptor);
        }

        var onDisk = _store.ListInstalled(_catalog);

        // Rebuilt rather than reconciled: this list is short, changes rarely, and nothing is bound
        // to the identity of a row in it.
        var chosen = SelectedSideloaded?.Name;
        Sideloaded.Clear();

        // A drafting head that belongs to a model on this disk is not an unaccounted-for file:
        // the answering engine pairs the two by name and uses it. Listing it here put "nothing
        // uses them" and a Delete button beside the file that makes answers a third faster, and
        // deleting it would have cost that silently. A head with no model left to pair with is
        // still dead weight, and still listed.
        var modelNames = onDisk
            .Select(m => Path.GetFileName(m.Path))
            .Where(name => !DraftModelLocator.IsDraftHead(name))
            .ToList();

        foreach (var file in onDisk.Where(m => m.IsSideloaded))
        {
            var name = Path.GetFileName(file.Path);
            if (DraftModelLocator.IsDraftHead(name) && PairsWithAModelPresent(name, modelNames))
            {
                continue;
            }

            var claimedBy = ClaimingEntry(name);
            Sideloaded.Add(new SideloadedItemViewModel(
                name,
                file.SizeBytes,
                isDirectory: false,
                claimedBy,
                claimedBy is not null && _store.IsInstalled(claimedBy)));
        }

        // Folders, after the files and from a separate question. ListInstalled lists directories
        // from the catalogue rather than from the disk, so an entry that leaves the catalogue takes
        // its folder out of every listing with it — the retired diariser's 332 MB was on disk, in
        // the folder this tab names at the top of itself, invisible to the panel written for
        // exactly this and unreachable by the only command that deletes leftovers.
        foreach (var directory in _store.ListSideloadedDirectories(_catalog))
        {
            Sideloaded.Add(new SideloadedItemViewModel(directory.Name, directory.SizeBytes, isDirectory: true));
        }

        SelectedSideloaded = Sideloaded.FirstOrDefault(f =>
            string.Equals(f.Name, chosen, StringComparison.OrdinalIgnoreCase));

        _installedBytes = onDisk.Sum(m => m.SizeBytes);

        OnPropertyChanged(nameof(HasSideloaded));
        OnPropertyChanged(nameof(SideloadedSummary));
        OnPropertyChanged(nameof(UninstallNotice));
    }

    private long _installedBytes;

    /// <summary>
    /// What the sideloaded files are and what they cost, in one line.
    /// </summary>
    public string SideloadedSummary
    {
        get
        {
            if (Sideloaded.Count == 0)
            {
                return string.Empty;
            }

            // **Three groups, because the one sentence used to describe all of them was false for
            // two.** "No entry above accounts for" was written when nothing here could be a
            // catalogue entry's file, and a multi-file entry made that untrue: its weights are
            // looked for under its own directory, so a copy in the root matched nothing and was
            // announced as belonging to nothing — directly beneath the entry that declares it, by
            // name and to the byte, reading Not installed and offering to download it again.
            var strays = Sideloaded.Where(f => f.ClaimedBy is null).ToList();
            var misplaced = Sideloaded.Where(f => f.IsMisplaced).ToList();
            var duplicates = Sideloaded.Where(f => f.ClaimedBy is not null && f.ClaimedEntryInstalled).ToList();

            var sentences = new List<string>();

            if (strays.Count > 0)
            {
                var summary = $"{DescribeCount(strays)} here "
                    + $"({ByteSize.Describe(strays.Sum(f => f.SizeBytes))}) that no entry above accounts for, "
                    + "weights from an older version of Uindosill, or things put here by hand.";

                // **It used to end "Nothing uses them", and that became untrue.** The Ask tab picks
                // the model it answers with by looking in this folder, not by catalogue id, so a
                // .gguf here can be the model in use — and this sentence sits directly above a
                // Delete button. Said only when there is a .gguf among them, because for a leftover
                // that is not one the original sentence was right.
                // A head is a .gguf and is never the model the panel answers with, so it does not
                // make the sentence about the Ask tab true — only a model the panel could load does.
                sentences.Add(strays.Any(f =>
                    f.Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                    && !DraftModelLocator.IsDraftHead(f.Name))
                    ? summary + " The Ask tab answers with a .gguf from this folder, so one of these may "
                        + "be the model it is using: the Ask tab's own settings say which."
                    : summary + " Nothing uses them.");
            }

            if (misplaced.Count > 0)
            {
                var one = misplaced.Count == 1;
                sentences.Add(
                    $"{misplaced.Count} file{(one ? " is" : "s are")} the weights of an entry above "
                    + $"({ByteSize.Describe(misplaced.Sum(f => f.SizeBytes))}), sitting in this folder "
                    + $"instead of in the entry's own: which is why that entry reads Not installed and "
                    + $"offers to download {(one ? "it" : "them")} again. Move into place files "
                    + $"{(one ? "it" : "them")} where the entry expects {(one ? "it" : "them")}, and "
                    + "nothing is downloaded.");
            }

            if (duplicates.Count > 0)
            {
                var one = duplicates.Count == 1;
                sentences.Add(
                    $"{duplicates.Count} more ({ByteSize.Describe(duplicates.Sum(f => f.SizeBytes))}) "
                    + $"{(one ? "duplicates an entry" : "duplicate entries")} already installed, whose own "
                    + $"folder holds the copy in use: so {(one ? "it is" : "they are")} the spare.");
            }

            return string.Join(" ", sentences);
        }
    }

    /// <summary>
    /// "2 files", "1 folder", "2 files and 1 folder" — the two shapes counted separately, because
    /// calling a folder a file is the mistake this list spent its whole life making by omission.
    /// </summary>
    private static string DescribeCount(IReadOnlyCollection<SideloadedItemViewModel> items)
    {
        var files = items.Count(i => !i.IsDirectory);
        var folders = items.Count - files;

        var filePart = files == 1 ? "1 file" : $"{files} files";
        var folderPart = folders == 1 ? "1 folder" : $"{folders} folders";

        return (files, folders) switch
        {
            (0, _) => folderPart,
            (_, 0) => filePart,
            _ => $"{filePart} and {folderPart}",
        };
    }

    /// <summary>
    /// Whether a drafting head has a model on this disk to draft for. Same rule the engine pairs
    /// by, so the tab cannot call a head unused that the next answer will load.
    /// </summary>
    private static bool PairsWithAModelPresent(string headName, IReadOnlyList<string> modelNames) =>
        modelNames.Any(model => DraftModelLocator.Match(model, [headName]) is not null);

    /// <summary>
    /// The catalogue entry a loose file in the root belongs to, or null when the catalogue does not
    /// name it — or names it twice.
    /// </summary>
    /// <remarks>
    /// A name two entries declare is left unclaimed rather than guessed at: a loose file is not
    /// evidence of which entry it came from, and saying the wrong one and offering to file it there
    /// would be worse than the silence this keeps. Until 2026-08-29 both 26B answering entries
    /// shipped <c>mtp-gemma-4-26B-A4B-it.gguf</c> and a loose head was left unclaimed; the IQ4_XS
    /// quant is deferred now, so exactly one entry claims that head — the rule stays for the next
    /// collision. The model beside it is not consulted either, because a head that pairs with a
    /// model on this disk never reaches here — it is filtered out one level up.
    /// </remarks>
    private ModelDescriptor? ClaimingEntry(string fileName)
    {
        var claiming = _catalog.EntriesDeclaringFile(fileName);
        return claiming.Count == 1 ? claiming[0] : null;
    }

    /// <summary>
    /// What becomes of the models folder, with the size read off the folder rather than written
    /// into the window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It said "the three of them come to over 3 GiB" — a count of catalogue entries and a total
    /// that were both true when they were typed. The count is wrong for anyone who has installed
    /// one or two of the three, and the total is wrong for anyone carrying weights the catalogue no
    /// longer lists, which on this maintainer's machine put nearly 3 GiB outside the sentence. A
    /// figure a window states about the user's own disk can simply be measured.
    /// </para>
    /// <para>
    /// For one night it said the opposite — an uninstall hook deleted the folder, and this line was
    /// rewritten to match. Then the hook came back on 2026-08-29 *asking* first, so the sentence
    /// changed a third time, and this is why it is now three sentences rather than one.
    /// </para>
    /// <para>
    /// <b>The threshold is <see cref="UninstallPrompt.AskAboveBytes"/> and not a number typed
    /// here.</b> Below it the uninstaller says nothing and deletes nothing, so a window promising a
    /// question that will not be asked would be a worse lie than the one this replaced. Above it the
    /// implication holds in the direction that matters: the models are inside the directory the
    /// prompt measures, so a models total over the threshold puts the directory over it too, and the
    /// question is certain to be asked. A redirected <c>UINDOSILL_MODELS_DIR</c> can break that the
    /// other way, and errs towards warning about a deletion that will not reach them.
    /// </para>
    /// </remarks>
    public string UninstallNotice => NoticeFor(_installedBytes);

    /// <summary>
    /// The notice for a given folder total, separated from the property so the three branches can
    /// be exercised without writing 64 MiB to a test's disk.
    /// </summary>
    internal static string NoticeFor(long installedBytes)
    {
        if (installedBytes == 0)
        {
            return "Downloaded models live outside the application folder, so they survive an "
                + "update and a reinstall. There are none here at the moment.";
        }

        var here = $"There is {ByteSize.Describe(installedBytes)} in that folder now. ";

        return installedBytes > UninstallPrompt.AskAboveBytes
            ? "Downloaded models live outside the application folder, so an update and a "
              + "reinstall never touch them. " + here
              + "Uninstalling Uindosill asks whether to delete them, and keeps them unless "
              + "you answer Yes. You can also remove them here."
            : "Downloaded models live outside the application folder, so they survive an "
              + "update and a reinstall. " + here
              + "Remove them here if you want the space back.";
    }

    /// <summary>Whether there is a sideloaded file selected to delete.</summary>
    public bool CanRemoveSideloaded => SelectedSideloaded is not null && !IsTranscribing;

    /// <summary>Whether the selected file is a known entry's weights that can be filed where they belong.</summary>
    public bool CanMoveIntoPlace => SelectedSideloaded is { IsMisplaced: true } && !IsTranscribing;

    /// <summary>
    /// Moves the selected file, and every other file its entry declares that is lying beside it,
    /// into the directory that entry installs into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole set rather than the one row, because an entry is installed only when all of its
    /// files are present: moving a 12.66 GiB model and leaving its 440 MiB drafting head in the
    /// root would leave the entry reading Not installed, having moved nearly everything. The store
    /// refuses the lot rather than overwriting, so a folder that already holds part of an entry is
    /// left exactly as it was found.
    /// </para>
    /// <para>
    /// Nothing is deleted and nothing is fetched — this is a rename within one folder. That is why
    /// it is offered where the alternatives were a re-download of bytes already present and a
    /// Delete button under weights the Ask tab would have been glad to load.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanMoveIntoPlace))]
    private void MoveIntoPlace()
    {
        if (SelectedSideloaded is not { ClaimedBy: { } model } file)
        {
            return;
        }

        try
        {
            var moved = _store.GatherIntoPlace(model);
            StatusMessage = moved switch
            {
                0 => $"{file.Name} could not be moved. Something is already in "
                     + $"{model.StorageName}, or the file is in use.",
                1 => $"Moved {file.Name} into {model.StorageName}. “{model.DisplayName}” is installed.",
                _ => $"Moved {moved} files into {model.StorageName}. “{model.DisplayName}” is installed.",
            };
        }
        catch (IOException ex)
        {
            StatusMessage = $"{file.Name} could not be moved: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = $"{file.Name} could not be moved: {ex.Message}";
        }

        Refresh();
    }

    /// <summary>
    /// Deletes one sideloaded file.
    /// </summary>
    /// <remarks>
    /// It cannot be loaded from here, so the only thing this tab can offer to do with it is give
    /// the space back. The store refuses anything a catalogue entry claims, so this cannot become a
    /// second way to remove a real entry.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRemoveSideloaded))]
    private void RemoveSideloaded()
    {
        if (SelectedSideloaded is not { } file)
        {
            return;
        }

        // A folder is deleted with everything in it, exactly as removing a multi-file entry is, and
        // through a separate store method: the file path takes bare file names and refuses a
        // directory, which is what left a retired diariser's 332 MB unreachable from here.
        var removed = file.IsDirectory
            ? _store.RemoveSideloadedDirectory(file.Name, _catalog)
            : _store.RemoveSideloaded(file.Name, _catalog);

        StatusMessage = removed
            ? $"Deleted {file.Name} ({file.SizeLabel})."
            : $"{file.Name} could not be deleted. It may be in use, or already gone.";

        Refresh();
    }

    private static string Describe(ModelInstallProgress progress)
    {
        var speed = progress.BytesPerSecond is { } bps
            ? string.Create(CultureInfo.InvariantCulture, $" at {bps / 1024 / 1024:0.0} MiB/s")
            : string.Empty;

        var remaining = progress.Remaining is { } left
            ? string.Create(CultureInfo.InvariantCulture, $", {left:hh\\:mm\\:ss} left")
            : string.Empty;

        return $"Downloading{speed}{remaining}";
    }
}
