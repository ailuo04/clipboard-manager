using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipboardManager.App.Services;

public enum AppLogLevel { Information, Warning, Error }
public enum AppLogOperation { Startup, UpdateRecovery, DatabaseMigration, ClipboardRead, ClipboardWrite, ClipboardCapture, Settings, UpdateCheck, UpdateDownload, UnhandledException, PlatformService }
public enum AppLogErrorCategory { Startup, Storage, Security, NativeClipboard, HotKeyConflict, PlatformService, Unknown }

public sealed record AppLogRecord(
    DateTime TimestampUtc,
    AppLogLevel Level,
    AppLogOperation Operation,
    Guid CorrelationId,
    AppLogErrorCategory? ErrorCategory,
    string AppVersion,
    string? ExceptionType,
    string? StackTrace);

/// <summary>Writes allow-listed structured diagnostics. It never serializes exception messages or clipboard payloads.</summary>
public sealed class AppLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _version;

    public AppLogger(string path, string? version = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _version = version ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
    }

    public void Log(AppLogLevel level, AppLogOperation operation, AppLogErrorCategory? category = null)
        => Append(new AppLogRecord(DateTime.UtcNow, level, operation, Guid.NewGuid(), category, _version, null, null));

    public void LogException(AppLogOperation operation, AppLogErrorCategory category, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Append(new AppLogRecord(DateTime.UtcNow, AppLogLevel.Error, operation, Guid.NewGuid(), category,
            _version, exception.GetType().FullName ?? exception.GetType().Name, exception.StackTrace));
    }

    private void Append(AppLogRecord record)
    {
        string line = JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine;
        lock (_gate)
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(_path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}

