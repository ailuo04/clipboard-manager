using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;

namespace ClipboardManager.Storage.Migration;

internal sealed class LegacyHistoryDocument
{
    public int Version { get; init; }
    public List<LegacyHistoryRecord>? Records { get; init; }
    public LegacySettings? Settings { get; init; }
    public List<string>? PendingImageDeletes { get; init; }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter<LegacyContentKind>(), new LegacyUtcDateTimeConverter() }
    };

    public IReadOnlyList<ClipboardRecord> ToRecords()
    {
        if (Version != 1 || Records is null || Settings is null)
            throw new InvalidDataException("Legacy history structure is incomplete or unsupported.");

        var ids = new HashSet<Guid>();
        return Records.Select(item =>
        {
            if (item.Id == Guid.Empty) throw new InvalidDataException("Legacy record ID is empty.");
            if (!ids.Add(item.Id)) throw new InvalidDataException($"Duplicate legacy record ID: {item.Id:D}.");
            ClipboardContentKind kind = item.Kind switch
            {
                LegacyContentKind.Text => ClipboardContentKind.Text,
                LegacyContentKind.Image => ClipboardContentKind.Image,
                _ => throw new InvalidDataException("Legacy content kind is invalid.")
            };
            if (item.ContentHash is not { Length: 64 } || item.ByteLength < 0)
                throw new InvalidDataException("Legacy record metadata is invalid.");
            if (kind == ClipboardContentKind.Text && string.IsNullOrEmpty(item.Text))
                throw new InvalidDataException("Legacy text record is empty.");
            if (kind == ClipboardContentKind.Image && (item.ImageFileName != $"{item.Id:N}.png" || item.ImageWidth <= 0 || item.ImageHeight <= 0))
                throw new InvalidDataException("Legacy image record metadata is invalid.");
            return new ClipboardRecord(item.Id, kind, item.Text, item.ImageFileName, item.ByteLength,
                item.ImageWidth, item.ImageHeight, item.ContentHash, item.CopiedAtUtc, item.IsPinned);
        }).ToArray();
    }

    public AppSettings ToSettings(string themePreference)
    {
        if (Settings is null) throw new InvalidDataException("Legacy settings are missing.");
        return new AppSettings(Settings.MaxRecords, Settings.MaxStorageMb, Settings.AlwaysOnTop,
            Settings.AutoCheckUpdates, Settings.MinimizeToTray, Settings.HideAfterCopy, themePreference).Normalize();
    }

    internal sealed class LegacyHistoryRecord
    {
        public Guid Id { get; init; }
        public LegacyContentKind Kind { get; init; }
        public string? Text { get; init; }
        public string? ImageFileName { get; init; }
        public long ByteLength { get; init; }
        public int ImageWidth { get; init; }
        public int ImageHeight { get; init; }
        public string ContentHash { get; init; } = string.Empty;
        public DateTime CopiedAtUtc { get; init; }
        public bool IsPinned { get; init; }
    }

    internal enum LegacyContentKind { Text = 0, Image = 1 }

    private sealed class LegacyUtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || !DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
                throw new JsonException("Legacy UTC timestamp is invalid.");
            return parsed.UtcDateTime;
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToUniversalTime());
    }

    internal sealed class LegacySettings
    {
        public int MaxRecords { get; init; } = 500;
        public int MaxStorageMb { get; init; } = 512;
        public bool AlwaysOnTop { get; init; }
        public bool AutoCheckUpdates { get; init; } = true;
        public bool MinimizeToTray { get; init; }
        public bool HideAfterCopy { get; init; } = true;
    }
}
