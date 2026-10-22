using System.Windows.Threading;

namespace ClipboardManager.Services;

internal sealed class StaTaskRunner : IAsyncDisposable
{
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private bool _disposed;

    public StaTaskRunner()
    {
        _thread = new Thread(() =>
        {
            _ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Clipboard STA worker" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public async Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var dispatcher = await _ready.Task.ConfigureAwait(false);
        return await dispatcher.InvokeAsync(action, DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var dispatcher = await _ready.Task.ConfigureAwait(false);
        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        await Task.Run(() => _thread.Join()).ConfigureAwait(false);
    }
}
