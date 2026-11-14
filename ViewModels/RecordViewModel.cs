using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ClipboardManager.Models;
using ClipboardManager.Utilities;

namespace ClipboardManager.ViewModels;

public sealed class RecordViewModel : INotifyPropertyChanged
{
    private int _position;

    public RecordViewModel(ClipboardRecord record, MainViewModel owner)
    {
        Record = record;
        ImagePath = record.Kind == ClipboardRecordKind.Image ? owner.GetImagePath(record) : null;
        CopyCommand = new AsyncRelayCommand(() => owner.CopyAsync(this), () => owner.CanAct);
        EditImageCommand = new AsyncRelayCommand(() => owner.EditImageAsync(this), () => IsImage && owner.CanAct);
    }

    public ClipboardRecord Record { get; }
    public bool IsImage => Record.Kind == ClipboardRecordKind.Image;
    public bool IsPinned => Record.IsPinned;
    public int Position
    {
        get => _position;
        set
        {
            if (_position == value) return;
            _position = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PositionLabel)));
        }
    }
    public string? PositionLabel => Position is >= 1 and <= 9 ? Position.ToString() : null;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string? ImagePath { get; }
    public string ShortTimeLabel => Record.CopiedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
    public string ContentInfo => IsImage ? $"{Record.ImageWidth} × {Record.ImageHeight} · {Formatters.FormatSize(Record.ByteLength)}" : $"{Record.Text?.Length:N0} 字符";
    public string Preview
    {
        get
        {
            var text = Record.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) return $"（空白文本，{text.Length:N0} 字符）";
            return text.Length > 300 ? text[..300] + "…" : text;
        }
    }

    /// <summary>A bounded hover preview; copying still returns the complete record.</summary>
    public string HoverText
    {
        get
        {
            var text = Record.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) return Preview;
            const int limit = 1500;
            return text.Length > limit ? text[..limit] + "\n\n…（预览已截断，复制可获取完整内容）" : text;
        }
    }

    public double PreviewImageWidth => ImagePreviewSize.Width;
    public double PreviewImageHeight => ImagePreviewSize.Height;

    private Size ImagePreviewSize
    {
        get
        {
            const double maxWidth = 400;
            const double maxHeight = 300;
            double width = Record.ImageWidth > 0 ? Record.ImageWidth : maxWidth;
            double height = Record.ImageHeight > 0 ? Record.ImageHeight : maxHeight;
            double scale = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));
            return new Size(Math.Max(1, Math.Round(width * scale)), Math.Max(1, Math.Round(height * scale)));
        }
    }
    /// <summary>The record on one line for compact rows: text with whitespace runs collapsed, or the image summary.</summary>
    public string SingleLine
    {
        get
        {
            if (IsImage) return ContentInfo;
            var text = Record.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) return Preview;
            string head = text.Length > 200 ? text[..200] : text;
            return string.Join(' ', head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
    }

    public ICommand CopyCommand { get; }
    public ICommand EditImageCommand { get; }

    public void RefreshCommands()
    {
        ((AsyncRelayCommand)CopyCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)EditImageCommand).RaiseCanExecuteChanged();
    }
}
