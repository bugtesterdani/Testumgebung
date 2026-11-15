using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

internal static class LabelImage
{
    public static LabelElement Import(string path, int maxWidth, int maxHeight)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var scale = Math.Min(1, Math.Min((double)maxWidth / frame.PixelWidth, (double)maxHeight / frame.PixelHeight));
        BitmapSource source = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
        var pixels = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = pixels.PixelWidth * 4;
        var bgra = new byte[stride * pixels.PixelHeight];
        pixels.CopyPixels(bgra, stride, 0);
        return new LabelElement
        {
            Kind = LabelElementKind.Image,
            ImageWidth = pixels.PixelWidth,
            ImageHeight = pixels.PixelHeight,
            Bitmap = LabelLayout.PackMonochrome(bgra, pixels.PixelWidth, pixels.PixelHeight),
        };
    }

    public static BitmapSource Preview(LabelElement image)
        => BitmapSource.Create(image.ImageWidth, image.ImageHeight, 96, 96, PixelFormats.Indexed1,
            new BitmapPalette([Colors.White, Colors.Black]), image.Bitmap, LabelLayout.BytesPerRow(image.ImageWidth));
}
