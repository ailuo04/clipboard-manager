using System.Buffers.Binary;
using System.Windows.Media.Imaging;

namespace ClipboardManager.Services;

public static class ImageLoader
{
    private static readonly SemaphoreSlim DecodeGate = new(2, 2);

    public static async Task<BitmapImage> LoadAsync(string path, int maxEdge = 0, CancellationToken cancellationToken = default)
    {
        await DecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = File.OpenRead(path);
                Span<byte> header = stackalloc byte[24];
                stream.ReadExactly(header);
                int width = BinaryPrimitives.ReadInt32BigEndian(header[16..]);
                int height = BinaryPrimitives.ReadInt32BigEndian(header[20..]);
                ImageProcessor.ValidateDimensions(width, height);
                stream.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                if (maxEdge > 0 && Math.Max(width, height) > maxEdge)
                {
                    if (width >= height) bitmap.DecodePixelWidth = maxEdge;
                    else bitmap.DecodePixelHeight = maxEdge;
                }
                bitmap.EndInit();
                bitmap.Freeze();
                cancellationToken.ThrowIfCancellationRequested();
                return bitmap;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { DecodeGate.Release(); }
    }
}
