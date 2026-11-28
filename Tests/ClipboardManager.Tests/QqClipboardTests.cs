using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using ClipboardManager.Utilities;
using ClipboardManager.Services;
using Xunit;

namespace ClipboardManager.Tests;

public sealed class QqClipboardTests
{
    [Fact]
    public void QqImagesBecomeTextInTheirOriginalPositions()
    {
        var html = ClipboardHtml("原文[烟花]<br><img src=\"file:///[烟花]\"> 你好 &amp; 再见\n"
            + "<img src=\"file:///[比心]\"><img src=\"file:///[其他表情]\">");

        Assert.Equal("原文[烟花]\n🎆 你好 & 再见\n🫰[其他表情]",
            QqClipboardText.AddEmoji("原文[烟花]\n 你好 & 再见\n", html));
    }

    [Fact]
    public void QqSelectionCanContainOnlyAnEmoji()
    {
        Assert.Equal("🎆", QqClipboardText.AddEmoji(null, ClipboardHtml("<img src=\"file:///[烟花]\">")));
    }

    [Fact]
    public void HtmlWithoutMatchingPlainTextDoesNotReplaceIt()
    {
        Assert.Equal("原文", QqClipboardText.AddEmoji("原文", ClipboardHtml("其他文字<img src=\"file:///[烟花]\">")));
    }

    [Fact]
    public void ArbitraryImagePathsAreNotInterpretedAsEmoji()
    {
        Assert.Equal("原文", QqClipboardText.AddEmoji("原文", ClipboardHtml("<img src=\"file:///C:/test.png\">原文")));
    }

    [Fact]
    public void MalformedFragmentOffsetsLeavePlainTextAlone()
    {
        var html = Encoding.UTF8.GetBytes("Version:0.9\r\nStartFragment:1000\r\nEndFragment:2000\r\n<img src=\"file:///[烟花]\">");
        Assert.Equal("原文", QqClipboardText.AddEmoji("原文", html));
    }

    [Fact]
    public async Task SavedQqPayloadSurvivesReloadAndDoesNotGetReplacedBySimilarText()
    {
        string root = Path.Combine(Path.GetTempPath(), "clipboard-qq-" + Guid.NewGuid().ToString("N"));
        byte[] firstFormat = Encoding.UTF8.GetBytes("<QQRichEditFormat><EditElement type=\"2\" sysfaceindex=\"333\"/></QQRichEditFormat>\0");
        byte[] secondFormat = Encoding.UTF8.GetBytes("<QQRichEditFormat><EditElement type=\"2\" sysfaceindex=\"319\"/></QQRichEditFormat>\0");
        try
        {
            var store = new HistoryStore(root);
            var history = new HistoryService(store);
            await history.LoadAsync();
            var first = (await history.AddAsync(ClipboardSnapshot.FromText("🎆原文", qqRichText: firstFormat))).Record!;
            var repeated = (await history.AddAsync(ClipboardSnapshot.FromText("🎆原文", qqRichText: firstFormat.ToArray()))).Record!;
            var other = (await history.AddAsync(ClipboardSnapshot.FromText("🎆原文", qqRichText: secondFormat))).Record!;
            var plain = (await history.AddAsync(ClipboardSnapshot.FromText("🎆原文"))).Record!;
            Assert.Equal(first.Id, repeated.Id);
            Assert.NotEqual(first.Id, other.Id);
            Assert.NotEqual(first.Id, plain.Id);

            var reloaded = new HistoryService(store);
            await reloaded.LoadAsync();
            var record = Assert.Single(reloaded.Records, item => item.Id == first.Id);
            Assert.Equal("🎆原文", record.Text);
            Assert.Equal(firstFormat, record.QqRichText);
            Assert.True(record.ByteLength >= Encoding.UTF8.GetByteCount("🎆原文") + firstFormat.Length);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DragPublishesNativeQqBytesWithUnicodeFallback()
    {
        byte[] native = Encoding.UTF8.GetBytes("<QQRichEditFormat/>\0");
        var data = RecordDragData.CreateText("🎆原文", native);
        Assert.Equal("🎆原文", data.GetText(TextDataFormat.UnicodeText));

        var format = new FORMATETC
        {
            cfFormat = unchecked((short)DataFormats.GetDataFormat(QqClipboardText.RichTextFormat).Id),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL
        };
        ((System.Runtime.InteropServices.ComTypes.IDataObject)data).GetData(ref format, out var medium);
        try
        {
            var buffer = NativeMethods.GlobalLock(medium.unionmember);
            Assert.NotEqual(IntPtr.Zero, buffer);
            try
            {
                var transferred = new byte[native.Length];
                Marshal.Copy(buffer, transferred, 0, transferred.Length);
                Assert.Equal(native, transferred);
            }
            finally { NativeMethods.GlobalUnlock(medium.unionmember); }
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    private static byte[] ClipboardHtml(string fragment)
    {
        // The multibyte context ensures fragment offsets are byte positions, not UTF-16 indexes.
        const string prefix = "<html><body>前<!--StartFragment-->";
        const string footer = "<!--EndFragment--></body></html>\0";
        const string header = "Version:0.9\r\nStartFragment:{0:D10}\r\nEndFragment:{1:D10}\r\n";
        int start = string.Format(header, 0, 0).Length + Encoding.UTF8.GetByteCount(prefix);
        int end = start + Encoding.UTF8.GetByteCount(fragment);
        return Encoding.UTF8.GetBytes(string.Format(header, start, end) + prefix + fragment + footer);
    }
}
