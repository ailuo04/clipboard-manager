using Microsoft.Data.Sqlite;

namespace ClipboardManager.Storage.Sqlite;

public sealed class SqliteConnectionFactory
{
    public const int DefaultBusyTimeoutMilliseconds = 5_000;

    private readonly string _connectionString;

    public SqliteConnectionFactory(string databasePath, int busyTimeoutMilliseconds = DefaultBusyTimeoutMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (busyTimeoutMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(busyTimeoutMilliseconds));

        DatabasePath = Path.GetFullPath(databasePath);
        BusyTimeoutMilliseconds = busyTimeoutMilliseconds;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(busyTimeoutMilliseconds / 1000d))
        }.ToString();
    }

    public string DatabasePath { get; }
    public int BusyTimeoutMilliseconds { get; }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {BusyTimeoutMilliseconds};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
