namespace ClipboardManager.Storage.Sqlite;

public sealed class SqliteStorageException : Exception
{
    public SqliteStorageException(string message) : base(message) { }
    public SqliteStorageException(string message, Exception innerException) : base(message, innerException) { }
}
