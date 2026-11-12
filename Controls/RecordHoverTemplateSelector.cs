using System.Windows;
using System.Windows.Controls;
using ClipboardManager.ViewModels;

namespace ClipboardManager.Controls;

public sealed class RecordHoverTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? ImageTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is RecordViewModel { IsImage: true } ? ImageTemplate : TextTemplate;
}
