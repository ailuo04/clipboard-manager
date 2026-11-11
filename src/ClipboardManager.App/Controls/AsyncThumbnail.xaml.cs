using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClipboardManager.App.Controls;

/// <summary>Loads bounded-size image previews off-thread with limited concurrency and unload cancellation.</summary>
public sealed partial class AsyncThumbnail : UserControl
{
    private const long MaxEncodedBytes = 128L * 1024 * 1024;
    private static readonly SemaphoreSlim DecodeGate = new(2, 2);
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(AsyncThumbnail), new PropertyMetadata(null, OnSourcePathChanged));
    public static readonly DependencyProperty MaxDecodeDimensionProperty = DependencyProperty.Register(
        nameof(MaxDecodeDimension), typeof(int), typeof(AsyncThumbnail), new PropertyMetadata(256));

    private CancellationTokenSource? _loadLifetime;
    private long _generation;

    public AsyncThumbnail()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string? SourcePath
    {
        get => (string?)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public int MaxDecodeDimension
    {
        get => (int)GetValue(MaxDecodeDimensionProperty);
        set => SetValue(MaxDecodeDimensionProperty, value);
    }

    private static void OnSourcePathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (AsyncThumbnail)sender;
        control.CancelLoad();
        control.ThumbnailImage.Source = null;
        control.Placeholder.Visibility = Visibility.Collapsed;
        if (control.IsLoaded) control.StartLoad();
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => StartLoad();

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        CancelLoad();
        ThumbnailImage.Source = null;
    }

    private void StartLoad()
    {
        string? path = SourcePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            Placeholder.Visibility = Visibility.Visible;
            return;
        }
        CancelLoad();
        var lifetime = new CancellationTokenSource();
        _loadLifetime = lifetime;
        long generation = Interlocked.Increment(ref _generation);
        _ = LoadAsync(path, generation, lifetime);
    }

    private async Task LoadAsync(string path, long generation, CancellationTokenSource lifetime)
    {
        CancellationToken cancellationToken = lifetime.Token;
        bool acquired = false;
        try
        {
            await DecodeGate.WaitAsync(cancellationToken);
            acquired = true;
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
            if (stream.Size <= 0 || stream.Size > MaxEncodedBytes)
                throw new InvalidDataException("Image preview exceeds the supported size.");
            cancellationToken.ThrowIfCancellationRequested();

            int decodeSize = Math.Clamp(MaxDecodeDimension, 64, 1200);
            var bitmap = new BitmapImage { DecodePixelWidth = decodeSize, DecodePixelHeight = decodeSize };
            await bitmap.SetSourceAsync(stream);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _generation) || !IsLoaded) return;
            ThumbnailImage.Source = bitmap;
            Placeholder.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            if (generation == Volatile.Read(ref _generation) && IsLoaded)
                Placeholder.Visibility = Visibility.Visible;
        }
        finally
        {
            if (acquired) DecodeGate.Release();
            Interlocked.CompareExchange(ref _loadLifetime, null, lifetime);
            lifetime.Dispose();
        }
    }

    private void CancelLoad()
    {
        Interlocked.Increment(ref _generation);
        CancellationTokenSource? lifetime = Interlocked.Exchange(ref _loadLifetime, null);
        if (lifetime is null) return;
        lifetime.Cancel();
    }
}