using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MiniMcp.DesktopChecks;

internal static class VisualCapture
{
    internal static async Task Save(FrameworkElement element, string file, double minimumScale = 0)
    {
        var nativeScale = element.XamlRoot.RasterizationScale;
        var scale = Math.Max(nativeScale, minimumScale);
        var width = checked((int)Math.Round(element.ActualWidth * scale));
        var height = checked((int)Math.Round(element.ActualHeight * scale));
        var bitmap = new RenderTargetBitmap();
        if (scale == nativeScale) await bitmap.RenderAsync(element);
        else await bitmap.RenderAsync(element, (int)Math.Round(element.ActualWidth * scale / nativeScale),
            (int)Math.Round(element.ActualHeight * scale / nativeScale));
        // RenderToSize uses display coordinates before the window's DPI transform.
        var rounding = (int)Math.Ceiling(nativeScale);
        if (Math.Abs(bitmap.PixelWidth - width) > rounding || Math.Abs(bitmap.PixelHeight - height) > rounding)
            throw new InvalidOperationException($"Screenshot requested {width}x{height}, returned {bitmap.PixelWidth}x{bitmap.PixelHeight}; window scale {nativeScale}.");
        var pixels = await bitmap.GetPixelsAsync();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * bitmap.PixelWidth / element.ActualWidth,
            96 * bitmap.PixelHeight / element.ActualHeight, pixels.ToArray());
        await encoder.FlushAsync();
        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
        await File.WriteAllBytesAsync(file, bytes);
        Console.WriteLine($"PNG {Path.GetFileName(file)}: {bitmap.PixelWidth}x{bitmap.PixelHeight}, render scale {scale:F2}, window scale {nativeScale:F2}");
    }
}
