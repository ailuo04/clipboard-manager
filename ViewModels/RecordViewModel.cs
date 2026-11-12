using System.ComponentModel;
using System.Runtime.CompilerServices;
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
        DeleteCommand = new AsyncRelayCommand(() => owner.DeleteAsync(this), () => owner.CanAct);
        PinCommand = new AsyncRelayCommand(() => owner.TogglePinAsync(this), () => owner.CanAct);
        DetailsCommand = new AsyncRelayCommand(() => owner.ShowDetails(this), () => owner.CanAct);
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
    public string TypeLabel => IsImage ? "图片" : "文本";
    public string PinToolTip => IsPinned ? "取消固定" : "固定记录";
    public string CopyToolTip => IsImage ? "复制图片到系统剪贴板" : "复制完整文本到系统剪贴板";
    public string TimeLabel => Record.CopiedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string ShortTimeLabel => Record.CopiedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
    public string FullTime => Record.CopiedAtUtc.ToLocalTime().ToString("yyyy年MM月dd日 HH:mm:ss");
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

    /// <summary>More of a text record for the hover preview; the full record remains available in details.</summary>
    public string HoverText
    {
        get
        {
            var text = Record.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) return Preview;
            const int limit = 1500;
            return text.Length > limit ? text[..limit] + "\n\n…（已截断，完整内容请查看详情）" : text;
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
    public ICommand DeleteCommand { get; }
    public ICommand PinCommand { get; }
    public ICommand DetailsCommand { get; }

    public void RefreshCommands()
    {
        ((AsyncRelayCommand)CopyCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)DeleteCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)PinCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)DetailsCommand).RaiseCanExecuteChanged();
    }
}
