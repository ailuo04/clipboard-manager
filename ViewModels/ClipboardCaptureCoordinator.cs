using System.Windows.Threading;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.Utilities;

namespace ClipboardManager.ViewModels;

/// <summary>Owns the clipboard capture pipeline: debounce timer, sequence dedup, and the capture loop.</summary>
internal sealed class ClipboardCaptureCoordinator
{
    private readonly DispatcherTimer _timer;
    private readonly ClipboardReader _clipboard;
    private readonly HistoryService _history;
    private readonly Func<bool> _canCapture;
    private readonly Func<bool> _isPaused;
    private readonly Func<bool> _isClosing;
    private readonly CancellationToken _lifetime;
    private readonly Func<Func<Task>, string, Task> _runOperation;
    private readonly Action _refreshRecords;
    private readonly Action<string?> _setNotice;
    private readonly Action<string> _setStatus;
    private readonly CancellationTokenSource _lifetimeSource;
    private Task _captureTask = Task.CompletedTask;
    private bool _captureRunning;
    private bool _captureRequested;
    private uint? _lastSequence;
    private ClipboardMonitor? _monitor;

    public ClipboardCaptureCoordinator(ClipboardReader clipboard, HistoryService history,
        Func<bool> canCapture, Func<bool> isPaused, Func<bool> isClosing,
        CancellationTokenSource lifetimeSource, Func<Func<Task>, string, Task> runOperation,
        Action refreshRecords, Action<string?> setNotice, Action<string> setStatus)
    {
        _clipboard = clipboard;
        _history = history;
        _canCapture = canCapture;
        _isPaused = isPaused;
        _isClosing = isClosing;
        _lifetimeSource = lifetimeSource;
        _lifetime = lifetimeSource.Token;
        _runOperation = runOperation;
        _refreshRecords = refreshRecords;
        _setNotice = setNotice;
        _setStatus = setStatus;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnCaptureTimer;
    }

    public void Attach(ClipboardMonitor monitor)
    {
        _monitor = monitor;
        monitor.ClipboardChanged += OnClipboardChanged;
    }

    public void Detach()
    {
        if (_monitor is null) return;
        _monitor.ClipboardChanged -= OnClipboardChanged;
        _monitor = null;
    }

    public void Pause()
    {
        _timer.Stop();
        _captureRequested = false;
    }

    public Task ShutdownAsync()
    {
        _timer.Stop();
        Detach();
        return _captureTask;
    }

    private void OnClipboardChanged(object? sender, EventArgs args) => ScheduleCapture();

    private void ScheduleCapture()
    {
        if (!_canCapture()) return;
        _timer.Stop();
        _timer.Start();
    }

    private void OnCaptureTimer(object? sender, EventArgs args)
    {
        _timer.Stop();
        if (_isPaused() || _isClosing()) return;
        _captureRequested = true;
        if (!_captureRunning) _captureTask = CaptureLoopAsync();
    }

    private async Task CaptureLoopAsync()
    {
        _captureRunning = true;
        try
        {
            while (_captureRequested && !_isClosing() && !_isPaused())
            {
                _captureRequested = false;
                await _runOperation(async () =>
                {
                    var result = await _clipboard.ReadAsync(_lifetime);
                    if (_isPaused() || _isClosing()) return;
                    if (result.Warning is not null) _setNotice(result.Warning);
                    if (_lastSequence == result.Sequence) return;
                    _lastSequence = result.Sequence;
                    if (result.Snapshot is null) return;
                    var saved = await _history.AddAsync(result.Snapshot, _lifetime);
                    _refreshRecords();
                    _setStatus(result.Snapshot.Kind == ClipboardRecordKind.Image ? "已保存图片" : "已保存文本");
                    if (saved.Warning is not null) _setNotice(saved.Warning);
                }, "读取或保存剪贴板失败");
            }
        }
        finally { _captureRunning = false; }
    }
}
