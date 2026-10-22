using System.Windows.Input;
using ClipboardManager.Models;
using ClipboardManager.Utilities;

namespace ClipboardManager.ViewModels;

public sealed class RecordViewModel
{
    public RecordViewModel(ClipboardRecord record, MainViewModel owner)
    {
        Record = record;
        ImagePath = record.Kind == ClipboardRecordKind.Image ? owner.GetImagePath(record) : null;
        CopyCommand = new AsyncRelayCommand(() => owner.CopyAsync(this), () => owner.CanAct);
        DeleteCommand = new AsyncRelayCommand(() => owner.DeleteAsync(this), () => owner.CanAct);
        PinCommand = new AsyncRelayCommand(() => owner.TogglePinAsync(this), () => owner.CanAct);
        DetailsCommand = new RelayCommand(() => owner.ShowDetails(this), () => owner.CanAct);
    }

    public ClipboardRecord Record { get; }
    public bool IsImage => Record.Kind == ClipboardRecordKind.Image;
    public bool IsPinned => Record.IsPinned;
    public string? ImagePath { get; }
    public string TypeLabel => IsImage ? "图片" : "文本";
    public string PinToolTip => IsPinned ? "取消固定" : "固定记录";
    public string CopyToolTip => IsImage ? "复制图片到系统剪贴板" : "复制完整文本到系统剪贴板";
    public string TimeLabel => Record.CopiedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string FullTime => Record.CopiedAtUtc.ToLocalTime().ToString("yyyy年MM月dd日 HH:mm:ss");
    public string ContentInfo => IsImage ? $"{Record.ImageWidth} × {Record.ImageHeight} · {FormatSize(Record.ByteLength)}" : $"{Record.Text?.Length:N0} 字符";
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
        ((RelayCommand)DetailsCommand).RaiseCanExecuteChanged();
    }

    public static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.0} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes} B";
}
