using System.IO;
using System.Windows.Media.Imaging;
namespace Zipora.Services;
public static class BackgroundImages
{
    public const double Aspect = 1.5;
    public static bool NeedsCrop(int w, int h) => (double)w / h < 1.25 || (double)w / h > 2;
    public static BitmapSource Read(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new IOException("图片不能超过 32 MiB。");
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 40_000_000) throw new IOException("图片不能超过 4000 万像素，请先缩小图片。");
        var bitmap = new WriteableBitmap(frame);
        bitmap.Freeze();
        return bitmap;
    }
    public static string Save(BitmapSource image)
    {
        double scale = Math.Min(1, 2560.0 / Math.Max(image.PixelWidth, image.PixelHeight));
        if (scale < 1) image = new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var path = Path.Combine(AppStorage.DataRoot, "Assets", "background.png");
        AppStorage.WriteAtomic(path, stream.ToArray());
        return path;
    }
}
