using System.ComponentModel;
using System.Runtime.CompilerServices;
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
