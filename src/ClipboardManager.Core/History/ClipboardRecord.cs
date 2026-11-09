namespace ClipboardManager.Core.History;

public enum ClipboardContentKind
{
    Text = 1,
    Image = 2
}

public sealed record ClipboardRecord(
    Guid Id,
    ClipboardContentKind Kind,
    string? Text,
    string? ImageFileName,
    long ByteLength,
    int ImageWidth,
    int ImageHeight,
    string ContentHash,
    DateTime CopiedAtUtc,
    bool IsFavorite)
{
    private long _byteLength = ValidateByteLength(ByteLength);
    private DateTime _copiedAtUtc = ValidateUtc(CopiedAtUtc, nameof(CopiedAtUtc));

    public long ByteLength
    {
        get => _byteLength;
        init => _byteLength = ValidateByteLength(value);
    }

    public DateTime CopiedAtUtc
    {
        get => _copiedAtUtc;
        init => _copiedAtUtc = ValidateUtc(value, nameof(CopiedAtUtc));
    }

    private static long ValidateByteLength(long value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(ByteLength), "Byte length cannot be negative.");
        return value;
    }

    private static DateTime ValidateUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        return value;
    }
}
