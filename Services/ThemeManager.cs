using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipboardManager.Models;
using ClipboardManager.Utilities;
using Microsoft.Win32;

namespace ClipboardManager.Services;

public static class ThemeManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static ResourceDictionary? _palette;
    private static bool _initialized;
    private static AppTheme _preference = AppTheme.Dark;
    private static bool _isDark = true;

    public static AppTheme Preference => _preference;
    public static bool IsDark => _isDark;
    public static event EventHandler? ThemeChanged;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        Apply(ReadSavedPreference());
        try { SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged; }
        catch (Exception exception) { AppLogger.LogException("监听系统主题变化失败", exception); }
    }

    public static void SetPreference(AppTheme preference)
    {
        Apply(preference);
        SavePreference(preference);
    }

    public static void Apply(AppTheme preference)
    {
        if (!Enum.IsDefined(preference)) preference = AppTheme.Dark;
        bool dark = preference switch
        {
            AppTheme.Light => false,
            AppTheme.System => IsSystemDark(),
            _ => true
        };
        bool changed = preference != _preference || dark != _isDark || _palette is null;
        _preference = preference;
        _isDark = dark;
        if (!changed) return;

        var application = Application.Current;
        if (application is null) return;
        var palette = ViewLoader.Load<ResourceDictionary>(dark ? "Views/LiquidGlass.xaml" : "Views/LightGlass.xaml");
        if (_palette is not null) application.Resources.MergedDictionaries.Remove(_palette);
        application.Resources.MergedDictionaries.Insert(0, palette);
        _palette = palette;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Keeps the native title bar and DWM backdrop in sync with the app theme.</summary>
    public static void RegisterWindow(Window window)
    {
        void ApplyChrome()
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;
                int darkMode = _isDark ? 1 : 0;
                _ = NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DwmwaUseImmersiveDarkMode, ref darkMode,
                    Marshal.SizeOf<int>());
            }
            catch (Exception exception) { AppLogger.LogException("设置窗口标题栏主题失败", exception); }
        }

        window.SourceInitialized += (_, _) => ApplyChrome();
        EventHandler handler = (_, _) => ApplyChrome();
        ThemeChanged += handler;
        window.Closed += (_, _) => ThemeChanged -= handler;
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs args)
    {
        if (args.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)) return;
        var application = Application.Current;
        if (application is null) return;
        try
        {
            application.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_initialized && _preference == AppTheme.System) Apply(AppTheme.System);
            }));
        }
        catch (Exception exception) { AppLogger.LogException("处理系统主题变化失败", exception); }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value ? value == 0 : true;
        }
        catch (Exception exception)
        {
            AppLogger.LogException("读取系统主题失败，沿用深色主题", exception);
            return true;
        }
    }

    private static AppTheme ReadSavedPreference()
    {
        try
        {
            if (!File.Exists(AppPaths.ThemeFilePath)) return AppTheme.Dark;
            var document = JsonSerializer.Deserialize<ThemeDocument>(File.ReadAllText(AppPaths.ThemeFilePath), JsonOptions);
            return document is not null && Enum.IsDefined(document.Theme) ? document.Theme : AppTheme.Dark;
        }
        catch (Exception exception)
        {
            AppLogger.LogException("读取主题设置失败，沿用深色主题", exception);
            return AppTheme.Dark;
        }
    }

    private static void SavePreference(AppTheme preference)
    {
        string temporaryPath = AppPaths.ThemeFilePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(AppPaths.RootDirectory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new ThemeDocument { Theme = preference }, JsonOptions));
            File.Move(temporaryPath, AppPaths.ThemeFilePath, overwrite: true);
        }
        catch (Exception exception)
        {
            AppLogger.LogException("保存主题设置失败", exception);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception exception) { AppLogger.LogException("删除主题设置临时文件失败", exception); }
        }
    }

}

internal sealed class ThemeDocument
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;
}
