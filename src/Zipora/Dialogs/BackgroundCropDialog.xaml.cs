using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Zipora.Services;
namespace Zipora.Dialogs;
public partial class BackgroundCropDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly BitmapSource _source;
    private double _scale, _x, _y;
    private Point _pointer;
    public BitmapSource? Result { get; private set; }
    private const double W = 598, H = W / BackgroundImages.Aspect;
    public BackgroundCropDialog(BitmapSource source)
    {
        _source = source; InitializeComponent(); Preview.Source = source;
        Resize(1); Backdrop.ApplyFromSetting(this);
    }
    private void Resize(double zoom)
    {
        double old = _scale;
        _scale = Math.Max(W / _source.PixelWidth, H / _source.PixelHeight) * zoom;
        _x = old == 0 ? (W - _source.PixelWidth * _scale) / 2 : W / 2 - (W / 2 - _x) * _scale / old;
        _y = old == 0 ? (H - _source.PixelHeight * _scale) / 2 : H / 2 - (H / 2 - _y) * _scale / old;
        Position();
    }
    private void Position()
    {
        Preview.Width = _source.PixelWidth * _scale; Preview.Height = _source.PixelHeight * _scale;
        _x = Math.Clamp(_x, Math.Min(0, W - Preview.Width), 0); _y = Math.Clamp(_y, Math.Min(0, H - Preview.Height), 0);
        System.Windows.Controls.Canvas.SetLeft(Preview, _x); System.Windows.Controls.Canvas.SetTop(Preview, _y);
    }
    private void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (Preview != null) Resize(e.NewValue); }
    private void StartDrag(object sender, MouseButtonEventArgs e) { _pointer = e.GetPosition(Viewport); Viewport.CaptureMouse(); }
    private void Drag(object sender, MouseEventArgs e)
    {
        if (!Viewport.IsMouseCaptured) return;
        if (e.LeftButton != MouseButtonState.Pressed) { Viewport.ReleaseMouseCapture(); return; }
        var p = e.GetPosition(Viewport); _x += p.X - _pointer.X; _y += p.Y - _pointer.Y; _pointer = p; Position();
    }
    private void EndDrag(object sender, MouseButtonEventArgs e) => Viewport.ReleaseMouseCapture();
    private void Accept(object sender, RoutedEventArgs e)
    {
        int w = Math.Min(_source.PixelWidth, Math.Max(1, (int)Math.Floor(W / _scale)));
        int h = Math.Min(_source.PixelHeight, Math.Max(1, (int)Math.Floor(w / BackgroundImages.Aspect)));
        int x = Math.Clamp((int)Math.Round(-_x / _scale), 0, _source.PixelWidth - w);
        int y = Math.Clamp((int)Math.Round(-_y / _scale), 0, _source.PixelHeight - h);
        Result = new CroppedBitmap(_source, new Int32Rect(x, y, w, h)); Result.Freeze(); DialogResult = true;
    }
}
