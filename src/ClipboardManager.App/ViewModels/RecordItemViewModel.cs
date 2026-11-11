using ClipboardManager.Core.History;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace ClipboardManager.App.ViewModels;

public sealed class RecordItemViewModel : ObservableObject
{
    private ClipboardRecord _record;

    public RecordItemViewModel(ClipboardRecord record, string? imagePath = null)
    {
        _record = record ?? throw new ArgumentNullException(nameof(record));
        ImagePath = imagePath;
    }

    public ClipboardRecord Record
    {
        get => _record;
        private set
        {
            if (!SetProperty(ref _record, value)) return;
            OnPropertyChanged(nameof(IsImage));
            OnPropertyChanged(nameof(IsText));
            OnPropertyChanged(nameof(ThumbnailVisibility));
            OnPropertyChanged(nameof(TextIconVisibility));
            OnPropertyChanged(nameof(IsFavorite));
            OnPropertyChanged(nameof(FavoriteGlyph));
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(PreviewText));
            OnPropertyChanged(nameof(AccessibleName));
            OnPropertyChanged(nameof(CopiedAtDisplay));
        }
    }

    public string? ImagePath { get; }
    public bool IsImage => Record.Kind == ClipboardContentKind.Image;
    public bool IsText => Record.Kind == ClipboardContentKind.Text;
    public Visibility ThumbnailVisibility => IsImage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextIconVisibility => IsImage ? Visibility.Collapsed : Visibility.Visible;
    public bool IsFavorite => Record.IsFavorite;
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public string DisplayText => Record.Kind == ClipboardContentKind.Text ? Record.Text ?? string.Empty : "图片";
    public string PreviewText => Record.Kind == ClipboardContentKind.Text ? Record.Text ?? string.Empty : $"{Record.ImageWidth} × {Record.ImageHeight} 图片";
    public string AccessibleName => Record.Kind == ClipboardContentKind.Text ? $"文本记录：{PreviewText}" : $"图片记录：{Record.ImageWidth} × {Record.ImageHeight}";
    public string CopiedAtDisplay => Record.CopiedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public void Update(ClipboardRecord record) => Record = record;
}