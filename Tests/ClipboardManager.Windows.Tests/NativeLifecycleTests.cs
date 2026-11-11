using ClipboardManager.Windows;
using ClipboardManager.Windows.Clipboard;
using ClipboardManager.Windows.Input;
using ClipboardManager.Windows.Theme;
using ClipboardManager.Windows.Tray;
using Xunit;

namespace ClipboardManager.Windows.Tests;

public sealed class NativeLifecycleTests
{
    private const uint WmClipboardUpdate = 0x031D;
    private const uint WmSettingChange = 0x001A;
    private const uint WmHotKey = 0x0312;
    private const uint WmTrayCallback = 0x8001;
    private const int ErrorHotKeyAlreadyRegistered = 1409;

    [Fact]
    public void ClipboardChangeMonitor_start_stop_are_idempotent_and_detach_on_dispose()
    {
        var native = new FakeWindowsMessageNativeApi();
        using var window = new WindowsMessageWindow(native, synchronizationContext: new InlineSynchronizationContext());
        var monitor = new ClipboardChangeMonitor(window);
        int changed = 0;
        monitor.ClipboardChanged += (_, _) => changed++;

        monitor.Start();
        monitor.Start();
        native.Send(window.Handle, WmClipboardUpdate);
        monitor.Stop();
        monitor.Stop();
        native.Send(window.Handle, WmClipboardUpdate);
        monitor.Dispose();

        Assert.Equal(1, native.AddClipboardListenerCalls);
        Assert.Equal(1, native.RemoveClipboardListenerCalls);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void ClipboardChangeMonitor_retains_registration_state_when_native_stop_fails_so_disposal_can_retry()
    {
        var native = new FakeWindowsMessageNativeApi { RemoveClipboardListenerResult = false };
        using var window = new WindowsMessageWindow(native, synchronizationContext: new InlineSynchronizationContext());
        using var monitor = new ClipboardChangeMonitor(window);
        int changed = 0;
        monitor.ClipboardChanged += (_, _) => changed++;
        monitor.Start();

        Assert.Throws<System.ComponentModel.Win32Exception>(() => monitor.Stop());
        native.Send(window.Handle, WmClipboardUpdate);
        native.RemoveClipboardListenerResult = true;
        monitor.Stop();
        native.Send(window.Handle, WmClipboardUpdate);

        Assert.Equal(2, native.RemoveClipboardListenerCalls);
        Assert.Equal(0, changed);
    }
    [Fact]
    public void GlobalHotKeyService_maps_conflict_to_readable_error_without_throwing()
    {
        var native = new FakeWindowsMessageNativeApi
        {
            RegisterHotKeyResult = false,
            LastErrorCode = ErrorHotKeyAlreadyRegistered
        };
        using var window = new WindowsMessageWindow(native, synchronizationContext: new InlineSynchronizationContext());
        using var hotKey = new GlobalHotKeyService(window);

        bool started = hotKey.TryStart(out string? error);

        Assert.False(started);
        Assert.Contains("Ctrl+Shift+Space", error, StringComparison.Ordinal);
        Assert.Contains("占用", error, StringComparison.Ordinal);
        Assert.Equal(1, native.RegisterHotKeyCalls);
        Assert.Equal(0, native.UnregisterHotKeyCalls);
    }

    [Fact]
    public void GlobalHotKeyService_registers_once_and_activates_on_hotkey_message()
    {
        var native = new FakeWindowsMessageNativeApi();
        using var window = new WindowsMessageWindow(native, synchronizationContext: new InlineSynchronizationContext());
        using var hotKey = new GlobalHotKeyService(window);
        int activated = 0;
        hotKey.Activated += (_, _) => activated++;

        Assert.True(hotKey.TryStart(out string? error));
        Assert.Null(error);
        Assert.True(hotKey.TryStart(out _));
        native.Send(window.Handle, WmHotKey, (IntPtr)GlobalHotKeyService.HotKeyId);
        hotKey.Stop();
        hotKey.Stop();

        Assert.Equal(1, activated);
        Assert.Equal(1, native.RegisterHotKeyCalls);
        Assert.Equal(1, native.UnregisterHotKeyCalls);
    }

    [Fact]
    public void SystemTrayService_manages_icon_and_routes_open_pause_exit_actions()
    {
        var native = new FakeWindowsMessageNativeApi();
        using var window = new WindowsMessageWindow(native, synchronizationContext: new InlineSynchronizationContext());
        using var tray = new SystemTrayService(window);
        int opened = 0;
        int exited = 0;
        int pauseChanges = 0;
        tray.OpenRequested += (_, _) => opened++;
        tray.ExitRequested += (_, _) => exited++;
        tray.PauseChanged += (_, _) => pauseChanges++;

        tray.Start();
        tray.Start();
        native.Send(window.Handle, WmTrayCallback, IntPtr.Zero, (IntPtr)0x0203);
        native.TrayMenuCommand = SystemTrayService.PauseCommand;
        native.Send(window.Handle, WmTrayCallback, IntPtr.Zero, (IntPtr)0x0205);
        native.TrayMenuCommand = SystemTrayService.OpenCommand;
        native.Send(window.Handle, WmTrayCallback, IntPtr.Zero, (IntPtr)0x0205);
        native.TrayMenuCommand = SystemTrayService.ExitCommand;
        native.Send(window.Handle, WmTrayCallback, IntPtr.Zero, (IntPtr)0x0205);
        tray.Stop();
        tray.Stop();

        Assert.Equal(1, native.AddTrayIconCalls);
        Assert.Equal(1, native.RemoveTrayIconCalls);
        Assert.Equal(2, opened);
        Assert.Equal(1, pauseChanges);
        Assert.True(tray.IsPaused);
        Assert.Equal(1, exited);
    }

    [Fact]
    public void SystemTrayService_respects_disabled_setting_and_recreates_icon_after_explorer_restart()
    {
        var native = new FakeWindowsMessageNativeApi();
        using var window = new WindowsMessageWindow(native, synchronizationContext: new InlineSynchronizationContext());
        using var tray = new SystemTrayService(window);

        tray.Start(enabled: false);
        Assert.Equal(0, native.AddTrayIconCalls);
        tray.Start(enabled: true);
        native.Send(window.Handle, native.TaskbarCreatedMessage);
        tray.Stop();

        Assert.Equal(2, native.AddTrayIconCalls);
        Assert.Equal(1, native.RemoveTrayIconCalls);
    }
    [Fact]
    public void SystemTrayService_can_enable_and_disable_without_duplicate_icon_registration()
    {
        var native = new FakeWindowsMessageNativeApi();
        using var window = new WindowsMessageWindow(native, synchronizationContext: new InlineSynchronizationContext());
        using var tray = new SystemTrayService(window);

        tray.SetEnabled(true);
        tray.SetEnabled(true);
        tray.SetEnabled(false);
        tray.SetEnabled(false);
        tray.SetEnabled(true);

        Assert.Equal(2, native.AddTrayIconCalls);
        Assert.Equal(1, native.RemoveTrayIconCalls);
    }
    [Fact]
    public void SystemTrayService_reports_disabled_after_icon_creation_fails()
    {
        var native = new FakeWindowsMessageNativeApi { AddTrayIconResult = false };
        using var window = new WindowsMessageWindow(native, synchronizationContext: new InlineSynchronizationContext());
        using var tray = new SystemTrayService(window);

        Assert.Throws<System.ComponentModel.Win32Exception>(() => tray.Start());

        Assert.False(tray.IsEnabled);
        Assert.Equal(1, native.AddTrayIconCalls);
        Assert.Equal(0, native.RemoveTrayIconCalls);
    }
    [Fact]
    public void WindowsMessageNativeApi_uses_the_user32_hotkey_exports()
    {
        var register = typeof(WindowsMessageNativeApi).GetMethod("NativeRegisterHotKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var unregister = typeof(WindowsMessageNativeApi).GetMethod("NativeUnregisterHotKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.Equal("RegisterHotKey", register?.GetCustomAttributes(typeof(System.Runtime.InteropServices.DllImportAttribute), false)
            .Cast<System.Runtime.InteropServices.DllImportAttribute>().Single().EntryPoint);
        Assert.Equal("UnregisterHotKey", unregister?.GetCustomAttributes(typeof(System.Runtime.InteropServices.DllImportAttribute), false)
            .Cast<System.Runtime.InteropServices.DllImportAttribute>().Single().EntryPoint);
    }
    [Fact]
    public void SystemThemeService_refreshes_only_when_system_settings_change()
    {
        var native = new FakeWindowsMessageNativeApi();
        using var window = new WindowsMessageWindow(native, synchronizationContext: new InlineSynchronizationContext());
        bool isLight = true;
        using var theme = new SystemThemeService(window, () => isLight);
        var changes = new List<bool>();
        theme.ThemeChanged += (_, value) => changes.Add(value);

        theme.Start();
        theme.Start();
        isLight = false;
        native.Send(window.Handle, WmSettingChange);
        native.Send(window.Handle, WmSettingChange);
        theme.Stop();
        isLight = true;
        native.Send(window.Handle, WmSettingChange);

        Assert.Equal([true, false], changes);
        Assert.Equal(false, theme.IsLightTheme);
    }

    [Fact]
    public void WindowsMessageWindow_dispose_destroys_handle_and_suppresses_late_messages()
    {
        var native = new FakeWindowsMessageNativeApi();
        var window = new WindowsMessageWindow(native, synchronizationContext: new InlineSynchronizationContext());
        int received = 0;
        window.MessageReceived += (_, _) => received++;
        var callback = native.LastCallback;
        IntPtr handle = window.Handle;

        window.Dispose();
        window.Dispose();
        callback!(handle, 0x0400, IntPtr.Zero, IntPtr.Zero);

        Assert.Equal(1, native.DestroyWindowCalls);
        Assert.Equal(0, received);
    }

    [Fact]
    public void WindowsMessageWindow_marshals_native_messages_to_the_captured_ui_context()
    {
        var native = new FakeWindowsMessageNativeApi();
        var context = new QueuedSynchronizationContext();
        using var window = new WindowsMessageWindow(native, context);
        int received = 0;
        window.MessageReceived += (_, _) => received++;

        native.Send(window.Handle, 0x0400);
        Assert.Equal(0, received);
        context.Drain();
        Assert.Equal(1, received);
    }
    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Enqueue((callback, state));
        public void Drain()
        {
            while (_queue.TryDequeue(out var work)) work.Callback(work.State);
        }
    }
    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }
    private sealed class FakeWindowsMessageNativeApi : IWindowsMessageNativeApi
    {
        public NativeWindowProcedure? LastCallback { get; private set; }
        public int LastErrorCode { get; set; }
        public int AddClipboardListenerCalls { get; private set; }
        public int RemoveClipboardListenerCalls { get; private set; }
        public bool RemoveClipboardListenerResult { get; set; } = true;
        public int RegisterHotKeyCalls { get; private set; }
        public int UnregisterHotKeyCalls { get; private set; }
        public int AddTrayIconCalls { get; private set; }
        public bool AddTrayIconResult { get; set; } = true;
        public int RemoveTrayIconCalls { get; private set; }
        public int DestroyWindowCalls { get; private set; }
        public uint TrayMenuCommand { get; set; }
        public bool RegisterHotKeyResult { get; set; } = true;
        public int ErrorCode => LastErrorCode;
        public uint TaskbarCreatedMessage => 0xC001;
        private readonly ManualResetEventSlim _quit = new();

        public IntPtr CreateHiddenMessageWindow(NativeWindowProcedure procedure)
        {
            LastCallback = procedure;
            return new IntPtr(123);
        }

        public bool DestroyMessageOnlyWindow(IntPtr handle)
        {
            DestroyWindowCalls++;
            return true;
        }

        public IntPtr DefWindowProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam) => IntPtr.Zero;
        public void RunMessageLoop() => _quit.Wait();
        public void QuitMessageLoop() => _quit.Set();
        public bool AddClipboardFormatListener(IntPtr handle) { AddClipboardListenerCalls++; return true; }
        public bool RemoveClipboardFormatListener(IntPtr handle) { RemoveClipboardListenerCalls++; return RemoveClipboardListenerResult; }
        public bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint virtualKey) { RegisterHotKeyCalls++; return RegisterHotKeyResult; }
        public bool UnregisterHotKey(IntPtr handle, int id) { UnregisterHotKeyCalls++; return true; }
        public bool AddTrayIcon(IntPtr handle, uint callbackMessage) { AddTrayIconCalls++; return AddTrayIconResult; }
        public bool RemoveTrayIcon(IntPtr handle) { RemoveTrayIconCalls++; return true; }
        public uint ShowTrayMenu(IntPtr handle, bool isPaused) => TrayMenuCommand;

        public void Send(IntPtr handle, uint message, IntPtr wParam = default, IntPtr lParam = default)
            => LastCallback?.Invoke(handle, message, wParam, lParam);
    }
}