namespace ClipboardManager.Windows.Clipboard;

public static class ClipboardFormatPolicy
{
    public const int MaxImageBytes = 128 * 1024 * 1024;
    public const int MaxTextBytes = 8 * 1024 * 1024;
    public const long MaxImagePixels = 40_000_000;

    private static readonly string[] ImageFormatNames = ["PNG", "image/png", "JFIF", "JPEG", "image/jpeg", "BMP", "image/bmp"];

    public static IReadOnlyList<uint> ImageFormatPriority(IReadOnlyDictionary<string, uint> registeredFormats)
    {
        ArgumentNullException.ThrowIfNull(registeredFormats);
        var ids = new List<uint>(ImageFormatNames.Length);
        var seen = new HashSet<uint>();
        foreach (string name in ImageFormatNames)
            if (registeredFormats.TryGetValue(name, out uint id) && id != 0 && seen.Add(id)) ids.Add(id);
        return ids.AsReadOnly();
    }

    public static uint? SelectBestAvailable(IEnumerable<uint> priorityFormats, ISet<uint> availableFormats)
    {
        ArgumentNullException.ThrowIfNull(priorityFormats);
        ArgumentNullException.ThrowIfNull(availableFormats);
        foreach (uint format in priorityFormats)
            if (format != 0 && availableFormats.Contains(format)) return format;
        return null;
    }
}
