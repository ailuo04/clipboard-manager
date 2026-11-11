using ClipboardManager.App.ViewModels;
using ClipboardManager.Core.Clipboard;
using ClipboardManager.Core.History;
using Microsoft.UI.Xaml;
using Xunit;

namespace ClipboardManager.App.Tests;

public sealed class RecordItemViewModelTests
{
    private static readonly DateTime CopiedAtUtc = new(2026, 11, 10, 12, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Text_item_exposes_full_text_accessible_name_and_favorite_glyph()
    {
        var item = new RecordItemViewModel(Record(ClipboardContentKind.Text, "a full clipboard text", favorite: false));

        Assert.Equal("a full clipboard text", item.DisplayText);
        Assert.Equal("文本记录：a full clipboard text", item.AccessibleName);
        Assert.Equal("☆", item.FavoriteGlyph);
        Assert.False(item.IsImage);
        Assert.Equal(Visibility.Collapsed, item.ThumbnailVisibility);
        Assert.Equal(Visibility.Visible, item.TextIconVisibility);
        Assert.Null(item.ImagePath);
    }

    [Fact]
    public void Updating_favorite_notifies_bound_row_properties()
    {
        var item = new RecordItemViewModel(Record(ClipboardContentKind.Text, "copy", favorite: false));
        var changed = new List<string?>();
        item.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        item.Update(item.Record with { IsFavorite = true });

        Assert.True(item.IsFavorite);
        Assert.Equal("★", item.FavoriteGlyph);
        Assert.Contains(nameof(RecordItemViewModel.FavoriteGlyph), changed);
        Assert.Contains(nameof(RecordItemViewModel.PreviewText), changed);
    }

    [Fact]
    public void Image_item_exposes_dimensions_and_safe_preview_path()
    {
        Guid id = Guid.NewGuid();
        var item = new RecordItemViewModel(Record(ClipboardContentKind.Image, null, false, id) with
        {
            ImageFileName = $"{id:N}.png",
            ImageWidth = 320,
            ImageHeight = 200
        }, Path.Combine(Path.GetTempPath(), $"{id:N}.png"));

        Assert.True(item.IsImage);
        Assert.Equal(Visibility.Visible, item.ThumbnailVisibility);
        Assert.Equal(Visibility.Collapsed, item.TextIconVisibility);
        Assert.Equal("320 × 200 图片", item.PreviewText);
        Assert.Equal($"图片记录：320 × 200", item.AccessibleName);
        Assert.EndsWith($"{id:N}.png", item.ImagePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Image_item_without_local_path_still_uses_image_preview_placeholder()
    {
        var item = new RecordItemViewModel(Record(ClipboardContentKind.Image, null, false));

        Assert.Equal(Visibility.Visible, item.ThumbnailVisibility);
        Assert.Equal(Visibility.Collapsed, item.TextIconVisibility);
    }
    private static ClipboardRecord Record(ClipboardContentKind kind, string? text, bool favorite, Guid? id = null)
        => new(id ?? Guid.NewGuid(), kind, text, null, text?.Length * 2L ?? 0, 0, 0, new string('0', 64), CopiedAtUtc, favorite);
}