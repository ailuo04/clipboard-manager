namespace ClipboardManager.Core.Storage;

public sealed record DatabaseSchema(int Version)
{
    public const int CurrentVersion = 1;
}
