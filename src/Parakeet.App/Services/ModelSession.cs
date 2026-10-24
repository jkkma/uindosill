using System.Diagnostics;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;

namespace Parakeet.App.Services;

/// <summary>
/// The one model held in memory, and the only thing that knows whether anything is loaded.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed the engine was created and disposed inside a single Start, so "which model
/// is loaded" had no answer to show: nothing stayed loaded for longer than one batch. Holding it
/// here makes the question answerable, and gives somebody who has just put 1.34 GiB into VRAM a
/// way to put it back down without closing the window.
/// </para>
/// <para>
/// One caveat this surfaces rather than hides. Unloading frees the weights, not the backend:
/// <c>ParakeetNativeLibrary.Configure</c> is documented as having no effect once a library is
/// loaded, because a process cannot swap one build of the same native library for another safely.
/// So the backend of the first load is the backend for the rest of the process. Load and unload
/// cycles are free; changing Vulkan to CUDA needs a restart, and <see cref="IsBackendFixed"/>
/// exists so the window can say so instead of appearing to comply.
/// </para>
/// <para>
/// Disposing the session is the end of that process's work with models: it unloads, then asks
/// the provider to release what the engine technology holds per process. On CUDA that release
/// has to happen before the process exits — left to static destruction it aborts the process
/// with <c>0xC0000409</c> after a perfectly good run (gotcha 19) — which is why the window
/// disposes this on close rather than leaving it to the runtime.
/// </para>
/// </remarks>
public sealed class ModelSession : IAsyncDisposable
{
    private readonly IEngineProvider _engines;
    private ITranscriptionEngine? _engine;

    public ModelSession(IEngineProvider engines)
    {
        ArgumentNullException.ThrowIfNull(engines);
        _engines = engines;
        IsBackendFixed = engines.IsBackendFixed;
    }

    /// <summary>Raised after any change to what is loaded, so a view model can mirror it.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Releases other model work before a recogniser load starts. The session is busy while this
    /// runs, so a new ask cannot begin in the gap between releasing its model and loading this one.
    /// </summary>
    public Func<Task>? BeforeLoadAsync { get; set; }

    /// <summary>The loaded engine, or null. Callers borrow it and must not dispose it.</summary>
    public ITranscriptionEngine? Engine => _engine;

    public ModelDescriptor? Model { get; private set; }

    /// <summary>What was asked for.</summary>
    public ComputeBackend? RequestedBackend { get; private set; }

    /// <summary>
    /// What the engine reports after loading, which is not always what was asked for — the native
    /// loader falls back when the requested backend's library is not present. Reporting the
    /// requested backend as though it were the running one is the specific lie this property
    /// exists to prevent.
    /// </summary>
    public ComputeBackend? LoadedBackend { get; private set; }

    public TimeSpan? LoadDuration { get; private set; }

    public bool IsLoaded => _engine is not null;

    public bool IsBusy { get; private set; }

    /// <summary>
    /// True once the native library has bound its backend, including a load that subsequently
    /// failed or was cancelled. Successful loads from injected engines retain the same restart
    /// guidance without consulting another provider's process-wide native state.
    /// </summary>
    public bool IsBackendFixed { get; private set; }

    /// <summary>
    /// Loads a model, replacing whatever was loaded before. Cheap to call when the same model and
    /// backend are already resident — that case is a no-op rather than a reload, because dropping
    /// a good engine to build an identical one is a second of nothing.
    /// </summary>
    public async Task LoadAsync(EngineSelection selection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (IsBusy)
        {
            return;
        }

        if (IsLoaded && Model?.Id == selection.Model?.Id && RequestedBackend == selection.Backend)
        {
            return;
        }

        IsBusy = true;
        Raise();

        try
        {
            if (BeforeLoadAsync is { } beforeLoad)
            {
                await beforeLoad().ConfigureAwait(true);
            }

            ct.ThrowIfCancellationRequested();
            await UnloadAsync().ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();

            var engine = _engines.Create(selection);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                await engine.LoadAsync(ct).ConfigureAwait(true);
            }
            catch
            {
                // A half-built engine is not a loaded session. Without this the failure would leave
                // the native handle owned by nothing and the UI claiming a model was resident.
                await engine.DisposeAsync().ConfigureAwait(true);
                throw;
            }

            stopwatch.Stop();

            _engine = engine;
            Model = selection.Model;
            RequestedBackend = selection.Backend;
            LoadedBackend = engine.Capabilities.Backend;
            LoadDuration = stopwatch.Elapsed;
            IsBackendFixed = true;
        }
        finally
        {
            // Loading the native library precedes loading and warming up a model. Either later
            // step can fail or be cancelled after the backend has become fixed for this process.
            // Ask the provider: a failure before binding must still leave the choice available.
            IsBackendFixed |= _engines.IsBackendFixed;
            IsBusy = false;
            Raise();
        }
    }

    public async Task UnloadAsync()
    {
        if (_engine is not { } engine)
        {
            return;
        }

        // Cleared before the await so nothing observes a session that is half torn down.
        _engine = null;
        Model = null;
        RequestedBackend = null;
        LoadedBackend = null;
        LoadDuration = null;

        await engine.DisposeAsync().ConfigureAwait(true);
        Raise();
    }

    /// <summary>
    /// Unloads, then releases the process-level backend. Order matters: upstream's contract for the
    /// release is that every model has already been destroyed. Waits out a load in flight first,
    /// because releasing the backend under a load that is still realising weights on it is a race
    /// with native code.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        while (IsBusy)
        {
            await Task.Delay(50).ConfigureAwait(true);
        }

        await UnloadAsync().ConfigureAwait(true);
        _engines.ReleaseBackend();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
