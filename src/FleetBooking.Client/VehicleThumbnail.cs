using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FleetBooking.Client;

public static class VehicleThumbnail
{
    public static BitmapSource Load(Stream stream)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 480;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();

        // Fit the vehicle itself into the cell, rather than its transparent canvas.
        var rgba = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = rgba.PixelWidth * 4;
        var pixels = new byte[stride * rgba.PixelHeight];
        rgba.CopyPixels(pixels, stride, 0);
        var left = rgba.PixelWidth;
        var top = rgba.PixelHeight;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < rgba.PixelHeight; y++)
        for (var x = 0; x < rgba.PixelWidth; x++)
        {
            if (pixels[y * stride + x * 4 + 3] <= 8) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }
        if (right < left || bottom < top) return image;
        const int padding = 2;
        left = Math.Max(0, left - padding);
        top = Math.Max(0, top - padding);
        right = Math.Min(rgba.PixelWidth - 1, right + padding);
        bottom = Math.Min(rgba.PixelHeight - 1, bottom + padding);
        var cropped = new CroppedBitmap(image, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
        cropped.Freeze();
        return cropped;
    }
}
