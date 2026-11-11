using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClipboardManager.App.Utilities;
using ClipboardManager.Core.Updates;

namespace ClipboardManager.App.Services;

/// <summary>Separates non-blocking update discovery from the explicit user command that downloads a validated package.</summary>
public sealed class UpdateCoordinator : INotifyPropertyChanged, IDisposable
{
    private readonly IUpdateReleaseService _releases;
    private readonly IUpdatePackageService _packages;
    private readonly IUpdateApplyService _apply;
    private readonly AppPaths _paths;
    private readonly AppLogger _logger;
    private readonly string _currentVersion;
    private readonly UpdateStateMachine _machine = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly SynchronizationContext? _notificationContext = SynchronizationContext.Current;
    private CancellationTokenSource? _downloadCancellation;
    private volatile bool _disposed;

    public UpdateCoordinator(IUpdateReleaseService releases, IUpdatePackageService packages, IUpdateApplyService apply, AppPaths paths,
        AppLogger logger, string? currentVersion = null)
    {
        _releases = releases ?? throw new ArgumentNullException(nameof(releases));
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        string version = currentVersion ?? (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0");
        _currentVersion = UpdateManifest.IsVersion(version) ? version : "0.0.0";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string CurrentVersion => _currentVersion;
    public UpdateState State { get { lock (_stateGate) return _machine.State; } }
    public UpdateState? ErrorStage { get { lock (_stateGate) return _machine.ErrorStage; } }
    public UpdateErrorKind? ErrorKind { get { lock (_stateGate) return _machine.ErrorKind; } }
    public string? RecoveryMessage { get { lock (_stateGate) return _machine.RecoveryMessage; } }
    public bool CanRetry { get { lock (_stateGate) return _machine.CanRetry; } }
    public bool CanCancel { get { lock (_stateGate) return _machine.CanCancel; } }
    public UpdateCheckResult? CurrentUpdate { get; private set; }
    public string? StagedDirectory { get; private set; }
    public DateTimeOffset? LastCheckedAtUtc { get; private set; }
    public UpdateProgress Progress { get; private set; } = new(0, 0, null);

    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            if (State is UpdateState.Downloading or UpdateState.Verifying or UpdateState.ReadyToApply or UpdateState.Applying or UpdateState.StartupHealthCheck)
                return;
            SetState(_machine.BeginChecking);
            try
            {
                UpdateCheckResult update = await _releases.CheckLatestAsync(_currentVersion, AppContext.BaseDirectory,
                    _paths.RootDirectory, cancellationToken).ConfigureAwait(false);
                CurrentUpdate = update;
                LastCheckedAtUtc = update.CheckedAtUtc;
                Progress = new UpdateProgress(0, update.DownloadSize, null);
                OnPropertyChanged(nameof(CurrentUpdate));
                OnPropertyChanged(nameof(LastCheckedAtUtc));
                OnPropertyChanged(nameof(Progress));
                SetState(() => { if (update.IsUpdateAvailable) _machine.ReportAvailable(); else _machine.ReportUpToDate(); });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetState(_machine.CancelChecking);
                throw;
            }
            catch (Exception exception)
            {
                SetError(exception, AppLogOperation.UpdateCheck);
            }
        }
        finally { _operationGate.Release(); }
    }

    /// <summary>Only the explicit UI Download and Update command should call this operation.</summary>
    public async Task DownloadAndVerifyAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        UpdateCheckResult? update = CurrentUpdate;
        UpdateState currentState = State;
        bool canStart = currentState is UpdateState.Available or UpdateState.Cancelled ||
                        currentState == UpdateState.Error && CanRetry;
        if (update?.IsUpdateAvailable != true || !canStart) return;
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;

        string? staging = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            lock (_stateGate)
            {
                bool stillCanStart = _machine.State is UpdateState.Available or UpdateState.Cancelled ||
                                     _machine.State == UpdateState.Error && _machine.CanRetry;
                if (!stillCanStart || _disposed) return;
                _downloadCancellation = linked;
                _machine.BeginDownloading();
            }
            OnStateChanged();
            Progress = new UpdateProgress(0, update.DownloadSize, null);
            OnPropertyChanged(nameof(Progress));
            staging = _packages.CreateStagingDirectory(_paths.UpdateStagingRoot);
            await _packages.DownloadAsync(update, staging, new InlineProgress<UpdateProgress>(value =>
            {
                Progress = value;
                OnPropertyChanged(nameof(Progress));
            }), linked.Token).ConfigureAwait(false);
            SetState(_machine.BeginVerifying);
            await _packages.VerifyAndExtractAsync(update, staging, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            lock (_stateGate)
            {
                if (_machine.State != UpdateState.Verifying)
                    throw new InvalidOperationException("The update state changed before staging could be committed.");
                _machine.MarkReadyToApply();
                StagedDirectory = staging;
            }
            OnPropertyChanged(nameof(StagedDirectory));
            OnStateChanged();
            SetState(_machine.BeginApplying);
            await _apply.StartAsync(update.Manifest!, staging, linked.Token).ConfigureAwait(false);
            TryCleanup(staging);
            staging = null;
            StagedDirectory = null;
            OnPropertyChanged(nameof(StagedDirectory));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (State != UpdateState.Cancelled) SetState(() => _machine.Cancel());
        }
        catch (Exception exception)
        {
            if (State != UpdateState.Cancelled) SetError(exception, AppLogOperation.UpdateDownload);
        }
        finally
        {
            if (staging is not null) TryCleanup(staging);
            lock (_stateGate) _downloadCancellation = null;
            _operationGate.Release();
        }
    }

    public bool CancelDownload()
    {
        lock (_stateGate)
        {
            if (!_machine.Cancel()) return false;
            _downloadCancellation?.Cancel();
        }
        OnStateChanged();
        return true;
    }

    public void ReportApplying() => SetState(_machine.BeginApplying);
    public void ReportStartupHealthCheck() => SetState(_machine.BeginStartupHealthCheck);
    public void ReportComplete() => SetState(_machine.Complete);

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            _downloadCancellation?.Cancel();
        }
    }

    private void SetError(Exception exception, AppLogOperation operation)
    {
        UpdateErrorKind kind = exception switch
        {
            UpdateOperationException updateException => updateException.Kind,
            HttpRequestException or TaskCanceledException => UpdateErrorKind.Network,
            InvalidDataException => UpdateErrorKind.InvalidManifest,
            IOException or UnauthorizedAccessException => State == UpdateState.Downloading ? UpdateErrorKind.DownloadFailed : UpdateErrorKind.VerificationFailed,
            _ => UpdateErrorKind.DownloadFailed
        };
        AppLogErrorCategory category = kind is UpdateErrorKind.InvalidManifest or UpdateErrorKind.InvalidRelease or UpdateErrorKind.VerificationFailed
            ? AppLogErrorCategory.Security : AppLogErrorCategory.Unknown;
        try { _logger.LogException(operation, category, exception); } catch { }
        SetState(() => _machine.Fail(kind));
    }

    private void TryCleanup(string staging)
    {
        try { _packages.Cleanup(staging); }
        catch (Exception exception)
        {
            try { _logger.LogException(AppLogOperation.UpdateDownload, AppLogErrorCategory.Security, exception); } catch { }
        }
    }

    private void SetState(Action transition)
    {
        lock (_stateGate) transition();
        OnStateChanged();
    }

    private void OnStateChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(ErrorStage));
        OnPropertyChanged(nameof(ErrorKind));
        OnPropertyChanged(nameof(RecoveryMessage));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanCancel));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChangedEventHandler? handler = PropertyChanged;
        if (handler is null) return;
        if (_notificationContext is not null && SynchronizationContext.Current != _notificationContext)
        {
            _notificationContext.Post(static state =>
            {
                var (coordinator, callback, name) = ((UpdateCoordinator, PropertyChangedEventHandler, string?))state!;
                callback(coordinator, new PropertyChangedEventArgs(name));
            }, (this, handler, propertyName));
        }
        else handler(this, new PropertyChangedEventArgs(propertyName));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}


