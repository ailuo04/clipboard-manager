using System.Collections.Generic;

namespace ClipboardManager;

internal sealed class OverlayPageStack<T> where T : class
{
    private readonly Stack<T> _pages = new();

    internal int Count => _pages.Count;

    internal T? Current => _pages.TryPeek(out T? page) ? page : null;

    internal void Push(T page)
    {
        ArgumentNullException.ThrowIfNull(page);
        _pages.Push(page);
    }

    internal T? Pop() => _pages.TryPop(out T? page) ? page : null;

    internal void Clear() => _pages.Clear();
}
