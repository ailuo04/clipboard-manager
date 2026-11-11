using ClipboardManager.App.ViewModels;
using ClipboardManager.Core.Clipboard;
using ClipboardManager.Core.History;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClipboardManager.App.Controls;

public sealed partial class RecordDetailsView : UserControl
{
    public RecordDetailsView() => InitializeComponent();

    public void ShowRecord(RecordItemViewModel? item)
    {
        bool hasItem = item is not null;
        EmptyDetails.Visibility = hasItem ? Visibility.Collapsed : Visibility.Visible;
        RecordDetails.Visibility = hasItem ? Visibility.Visible : Visibility.Collapsed;
        if (item is null) return;

        RecordKindText.Text = item.Record.Kind == ClipboardContentKind.Text ? "文本记录" : "图片记录";
        CopiedAtText.Text = $"复制于 {item.CopiedAtDisplay}";
        bool isImage = item.IsImage;
        FullText.Visibility = isImage ? Visibility.Collapsed : Visibility.Visible;
        FullImage.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
        FullText.Text = item.Record.Text ?? string.Empty;
        FullImage.MaxDecodeDimension = 1200;
        FullImage.SourcePath = item.ImagePath;
    }
}