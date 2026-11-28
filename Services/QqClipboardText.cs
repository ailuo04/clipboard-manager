using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ClipboardManager.Services;

internal static class QqClipboardText
{
    internal const string RichTextFormat = "QQ_Unicode_RichEdit_Format";
    private static readonly Regex Tags = new(
        "<img\\s+src\\s*=\\s*\"file:///\\[(?<emoji>[^\\[\\]<>\"/\\r\\n]{1,32})\\]\"\\s*/?>|<(?<break>br)\\s*/?>|<[^<>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static string? AddEmoji(string? text, byte[] html)
    {
        // CF_HTML offsets are byte offsets, even when the fragment contains Chinese text.
        var header = html.AsSpan(0, Math.Min(html.Length, 512));
        int tag = header.IndexOf((byte)'<');
        string metadata = Encoding.ASCII.GetString(tag < 0 ? header : header[..tag]);
        int start = ReadOffset(metadata, "StartFragment:");
        int end = ReadOffset(metadata, "EndFragment:");
        if (start < metadata.Length || end <= start || end > html.Length) return text;

        string fragment = Encoding.UTF8.GetString(html, start, end - start);
        var plain = new StringBuilder();
        var withEmoji = new StringBuilder();
        int position = 0;
        bool foundEmoji = false;
        foreach (Match match in Tags.Matches(fragment))
        {
            AppendText(fragment[position..match.Index]);
            if (match.Groups["break"].Success) AppendText("\n");
            if (match.Groups["emoji"].Success)
            {
                foundEmoji = true;
                string name = WebUtility.HtmlDecode(match.Groups["emoji"].Value);
                withEmoji.Append(name switch { "烟花" => "🎆", "比心" => "🫰", _ => $"[{name}]" });
            }
            position = match.Index + match.Length;
        }
        if (!foundEmoji) return text;
        AppendText(fragment[position..]);

        // Recover only emoji. If the fragment's text differs (e.g. unfamiliar markup), keep the original.
        if (!string.Equals(plain.ToString().ReplaceLineEndings("\n"), (text ?? "").ReplaceLineEndings("\n"), StringComparison.Ordinal))
            return text;
        string result = withEmoji.ToString();
        return text?.Contains("\r\n", StringComparison.Ordinal) == true ? result.ReplaceLineEndings("\r\n") : result;

        void AppendText(string segment)
        {
            string decoded = WebUtility.HtmlDecode(segment);
            plain.Append(decoded);
            withEmoji.Append(decoded);
        }
    }

    private static int ReadOffset(string header, string name)
    {
        foreach (string line in header.Split('\r', '\n'))
        {
            if (line.StartsWith(name, StringComparison.Ordinal) && int.TryParse(line.AsSpan(name.Length).Trim(),
                NumberStyles.None, CultureInfo.InvariantCulture, out int offset))
                return offset;
        }
        return -1;
    }
}
