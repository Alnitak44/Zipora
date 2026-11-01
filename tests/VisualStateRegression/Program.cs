using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static int _checks;
    private static readonly string Output = Path.GetFullPath($"Zipora/artifacts/visual-state-regression-v{Zipora.Services.AppInfo.Version}");

    [STAThread]
    static int Main()
    {
        var app = new Zipora.App();
        app.InitializeComponent();
        Directory.CreateDirectory(Output);
        try
        {
            foreach (string theme in new[] { "Light", "Dark" })
            {
                var palette = new ResourceDictionary
                { Source = new Uri($"/Zipora;component/Themes/{theme}.xaml", UriKind.Relative) };
                app.Resources.MergedDictionaries.Add(palette);
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme == "Dark"
                    ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);
                foreach (string surface in new[] { "Solid", "Acrylic", "Image" })
                    CheckSurface(app, theme, surface);
                app.Resources.MergedDictionaries.Remove(palette);
            }
            Console.WriteLine($"PASS: {_checks} assertions; rendered captures: {Output}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { app.Shutdown(); }
    }

    private static void CheckSurface(Application app, string theme, string surface)
    {
        bool dark = theme == "Dark";
        // Same content tints as SurfacePalette; simulate wallpaper underneath.
        var tint = surface switch
        {
            "Acrylic" => Brush(dark ? "#38202630" : "#50FFFFFF"),
            "Image" => Brush(dark ? "#CC1D222B" : "#C0FAFBFD"),
            _ => (SolidColorBrush)app.FindResource("ZiporaContentBackground")
        };
        var backdrop = Brush("#FF6C6054");
        var view = new GridView { AllowsColumnReorder = false };
        view.Columns.Add(new GridViewColumn { Header = "名称", Width = 400 });
        var list = new ListView
        {
            Style = (Style)app.FindResource("FileListView"), View = view,
            ItemsSource = Enumerable.Range(0, 1000).Select(i => $"sample-{i}.txt").ToArray(),
            SelectedIndex = 0,
        };
        // Mirror MainWindow's resource scope. GridView's explicit viewer style
        // takes precedence over this implicit style and must retain its header.
        list.Resources[typeof(ScrollViewer)] = new Style(typeof(ScrollViewer),
            (Style)app.FindResource("FileListScrollViewer"));
        var card = new Border { Child = list, Background = tint };
        var cancel = new Button { Content = "取消" };
        var root = new DockPanel { Background = backdrop };
        DockPanel.SetDock(cancel, Dock.Bottom);
        root.Children.Add(cancel);
        root.Children.Add(card);
        root.Measure(new Size(960, 640));
        root.Arrange(new Rect(0, 0, 960, 640));
        root.UpdateLayout();

        foreach (bool enabled in new[] { true, false, true, false, true })
        {
            // OLE dragging and regular extraction both disable the parent card.
            card.IsEnabled = enabled;
            root.UpdateLayout();
            Assert(list.IsEnabled == enabled, "Busy state must block list input.");
            Assert(cancel.IsEnabled, "Cancellation must remain available.");
            Assert(list.SelectedIndex == 0, "Busy transition must retain selection.");
            var frame = list.Template.FindName("Bd", list) as Border;
            Assert(frame != null, "Use an ordinary Border, not native ListBoxChrome.");
            Assert(frame!.Background is SolidColorBrush b && b.Color.A == 0, "List frame must stay transparent.");
            Assert(!Descendants(list).Any(v => v.GetType().Name == "ListBoxChrome"), "No native disabled overlay may remain.");

            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                var bitmap = new RenderTargetBitmap((int)(960 * scale), (int)(640 * scale),
                    96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(root);
                string label = $"{theme}-{surface}-{(enabled ? "idle" : "busy")}-{scale:0.##}";
                if (scale == 1.0) Save(bitmap, label);
                var expected = Composite(tint.Color, backdrop.Color);
                foreach (var point in new[] { new Point(800, 150), new Point(800, 350), new Point(800, 500) })
                {
                    var actual = Pixel(bitmap, (int)(point.X * scale), (int)(point.Y * scale));
                    Assert(Near(actual, expected), $"{label}: viewport pixel is {actual}, expected {expected}.");
                }
            }
            Assert(Descendants(list).OfType<GridViewColumnHeader>().Any(h => Equals(h.Content, "名称") && h.ActualHeight > 0),
                "Column header must remain visible with the replacement frame.");
            Console.WriteLine($"PASS {theme}/{surface}: enabled={enabled}, painted pixels correct at 100/125/150/200%.");
        }

        var viewer = Descendants(list).OfType<ScrollViewer>().First();
        Assert(viewer.ScrollableHeight > 0, "Large archives must remain scrollable.");
        Assert(Descendants(list).OfType<ListViewItem>().Count() < 1000, "Rows must remain virtualized.");
        list.ScrollIntoView(list.Items[999]);
        root.UpdateLayout();
        Assert(viewer.VerticalOffset > 0, "Scrolling to an offscreen archive item must work.");
        Assert(list.ItemContainerGenerator.ContainerFromIndex(999) != null, "Offscreen row must be realized after scrolling.");
        Assert(list.SelectedIndex == 0, "Scrolling must not change the original selection.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
    private static Color Composite(Color foreground, Color background)
    {
        byte Channel(byte f, byte b) => (byte)Math.Round((f * foreground.A + b * (255 - foreground.A)) / 255.0);
        return Color.FromRgb(Channel(foreground.R, background.R), Channel(foreground.G, background.G), Channel(foreground.B, background.B));
    }
    private static bool Near(Color a, Color b) => a.A == b.A && Math.Abs(a.R - b.R) <= 1 && Math.Abs(a.G - b.G) <= 1 && Math.Abs(a.B - b.B) <= 1;
    private static Color Pixel(BitmapSource bitmap, int x, int y)
    {
        var bytes = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }
    private static void Save(BitmapSource bitmap, string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Output, name + ".png"));
        encoder.Save(stream);
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }
}
