using System.Text.Json;
using System.Text.Json.Serialization;
using ClipboardManager.Models;

namespace ClipboardManager.Services;

internal static class ThemePreferencePolicy
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static AppTheme Resolve(AppTheme? saved) =>
        saved is { } value && Enum.IsDefined(value) ? value : AppTheme.Light;

    internal static AppTheme ResolveSavedPreferenceJson(string? json)
    {
        if (json is null) return Resolve(null);

        try
        {
            var document = JsonSerializer.Deserialize<ThemeDocument>(json, JsonOptions);
            return Resolve(document?.Theme);
        }
        catch (JsonException)
        {
            return Resolve(null);
        }
    }

    internal static string SerializePreference(AppTheme preference) =>
        JsonSerializer.Serialize(new ThemeDocument { Theme = Resolve(preference) }, JsonOptions);
}

internal sealed class ThemeDocument
{
    public AppTheme Theme { get; set; } = AppTheme.Light;
}
