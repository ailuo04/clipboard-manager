using System.Runtime.ExceptionServices;

namespace ClipboardManager.App;

internal sealed class StartupExceptionRecorder : IDisposable
{
    private const int MaximumRecordedExceptions = 64;
    private readonly object _sync = new();
    private readonly List<string> _exceptions = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly EventHandler<FirstChanceExceptionEventArgs> _handler;
    private bool _disposed;

    internal StartupExceptionRecorder()
    {
        _handler = OnFirstChanceException;
        AppDomain.CurrentDomain.FirstChanceException += _handler;
    }

    internal IReadOnlyList<string> Snapshot()
    {
        lock (_sync) return _exceptions.ToArray();
    }

    internal void WriteToStartupLog()
    {
        foreach (string exception in Snapshot())
            Program.WriteStartupDiagnostic($"First-chance exception during WinUI initialization:{Environment.NewLine}{exception}");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            AppDomain.CurrentDomain.FirstChanceException -= _handler;
            _disposed = true;
        }
    }

    private void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs args)
    {
        Exception exception = args.Exception;
        string details = $"{exception.GetType().FullName}, HRESULT 0x{exception.HResult:X8}{Environment.NewLine}{exception}";
        lock (_sync)
        {
            if (_disposed || _exceptions.Count >= MaximumRecordedExceptions || !_seen.Add(details)) return;
            _exceptions.Add(details);
        }
    }
}
