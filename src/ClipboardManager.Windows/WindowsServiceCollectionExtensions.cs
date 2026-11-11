using System.Runtime.InteropServices;
using ClipboardManager.Core.Clipboard;
using ClipboardManager.Windows.Clipboard;
using ClipboardManager.Windows.Images;
using ClipboardManager.Windows.Input;
using ClipboardManager.Windows.Theme;
using ClipboardManager.Windows.Tray;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.UI.Dispatching;

namespace ClipboardManager.Windows;

public static class WindowsServiceCollectionExtensions
{
    /// <summary>Registers Windows adapters; call from the WinUI UI thread so callbacks are marshalled to its dispatcher.</summary>
    public static IServiceCollection AddClipboardManagerWindowsServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        DispatcherQueue? dispatcher = null;
        try { dispatcher = DispatcherQueue.GetForCurrentThread(); }
        catch (COMException) { /* DI can be composed before WinUI initialization. */ }
        SynchronizationContext? synchronizationContext = SynchronizationContext.Current;
        services.TryAddSingleton<IWindowsMessageNativeApi, WindowsMessageNativeApi>();
        services.TryAddSingleton<WindowsMessageWindow>(provider => new WindowsMessageWindow(
            provider.GetRequiredService<IWindowsMessageNativeApi>(),
            synchronizationContext,
            dispatcher is null ? null : action => dispatcher.TryEnqueue(() => action())));
        services.TryAddSingleton<WindowsImageCodec>();
        services.TryAddSingleton<NativeClipboardReader>();
        services.TryAddSingleton<IClipboardReader>(provider => provider.GetRequiredService<NativeClipboardReader>());
        services.TryAddSingleton<IClipboardWriter>(provider => provider.GetRequiredService<NativeClipboardReader>());
        services.TryAddSingleton<ClipboardChangeMonitor>();
        services.TryAddSingleton<IClipboardMonitor>(provider => provider.GetRequiredService<ClipboardChangeMonitor>());
        services.TryAddSingleton<GlobalHotKeyService>();
        services.TryAddSingleton<SystemTrayService>();
        services.TryAddSingleton<SystemThemeService>();
        return services;
    }
}