namespace ClipboardManager.Core.Clipboard;

public sealed record ClipboardReadResult(uint SequenceNumber, ClipboardSnapshot? Snapshot, string? Warning);
