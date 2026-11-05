using ClipboardManager.Models;

namespace ClipboardManager.Services;

internal static class ThemePreferencePolicy
{
    internal static AppTheme Resolve(AppTheme? saved) =>
        saved is { } value && Enum.IsDefined(value) ? value : AppTheme.Light;
}
