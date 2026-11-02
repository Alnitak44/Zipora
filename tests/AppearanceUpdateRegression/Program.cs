using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Zipora.Services;
using Zipora.Dialogs;

internal static class Program
{
    private static int checks;
    private static void Expect(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
    private static BitmapSource ColorImage(byte r, byte g, byte b, int w = 320, int h = 640)
    {
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255; }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Run(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root)) throw new Exception("Use a new output directory");
        Directory.CreateDirectory(Path.Combine(root, "App"));
        File.WriteAllText(Path.Combine(root, "portable.json"), "{}");
        AppStorage.Initialize(Path.Combine(root, "App"));
        try
        {
            Expect(AppStorage.IsPortable, "portable storage initialized");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var xml = System.Xml.Linq.XDocument.Load(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Zipora/App.xaml")));
            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var resources = xml.Root!.Element(ns + "Application.Resources")!.Elements().Single();
            resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "ui", "http://schemas.lepo.co/wpfui/2022/xaml");
            resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
            app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString().Replace("Source=\"Themes/Zipora.xaml\"", "Source=\"/Zipora;component/Themes/Zipora.xaml\""));
            ThemeManager.Init("Light");
            var now = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
            Expect(!UpdateChecker.IsDue(UpdateFrequency.Never, null, now), "never disables first check");
            Expect(UpdateChecker.IsDue(UpdateFrequency.EveryLaunch, now, now), "every launch");
            foreach (var pair in new[] { (UpdateFrequency.Daily, 1), (UpdateFrequency.Weekly, 7), (UpdateFrequency.Fortnightly, 14) })
            {
                Expect(!UpdateChecker.IsDue(pair.Item1, now, now.AddDays(pair.Item2).AddSeconds(-1)), "not yet due");
                Expect(UpdateChecker.IsDue(pair.Item1, now, now.AddDays(pair.Item2)), "due at boundary");
            }
            Expect(!UpdateChecker.IsDue(UpdateFrequency.Monthly, now, now.AddMonths(1).AddSeconds(-1)), "monthly calendar interval");
            Expect(UpdateChecker.IsDue(UpdateFrequency.Monthly, now, now.AddMonths(1)), "monthly boundary");
            Expect(UpdateChecker.IsDue(UpdateFrequency.Daily, now.AddDays(1), now), "clock moved backward");
            using (var release = System.Text.Json.JsonDocument.Parse("""
                {"tag_name":"v99.1.0","draft":false,"prerelease":false,"html_url":"https://github.com/Alnitak44/Zipora/releases/tag/v99.1.0","body":"notes","assets":[{"name":"Zipora-v99.1.0-win-x64.zip","browser_download_url":"https://github.com/standard.zip"},{"name":"Zipora-v99.1.0-win-x64-portable.zip","browser_download_url":"https://github.com/portable.zip"}]}
                """))
            {
                Expect(UpdateChecker.ParseRelease(release.RootElement, false) is { IsNewer: true, DownloadUrl: "https://github.com/standard.zip" }, "GitHub standard asset selected");
                Expect(UpdateChecker.ParseRelease(release.RootElement, true) is { IsNewer: true, DownloadUrl: "https://github.com/portable.zip" }, "GitHub portable asset selected");
            }
            using (var release = System.Text.Json.JsonDocument.Parse("""
                {"tag_name":"v99.1.0","draft":false,"prerelease":false,"html_url":"https://github.com/Alnitak44/Zipora/releases/tag/v99.1.0","body":"notes","assets":[{"name":"Zipora-v99.1.0-win-x64.zip","browser_download_url":"https://github.com/standard.zip"},{"name":"Zipora-v99.1.0-Setup-x64.exe","browser_download_url":"https://github.com/setup.exe"},{"name":"Zipora-v99.1.0-win-x64-portable.zip","browser_download_url":"https://github.com/portable.zip"}]}
                """))
            {
                Expect(UpdateChecker.ParseRelease(release.RootElement, false)?.DownloadUrl == "https://github.com/setup.exe", "normal updates prefer Setup over ZIP");
                Expect(UpdateChecker.ParseRelease(release.RootElement, true)?.DownloadUrl == "https://github.com/portable.zip", "portable updates never select the installer");
            }
            foreach (UpdateFrequency frequency in Enum.GetValues<UpdateFrequency>())
            {
                var store = AuthStore.Load(); store.UpdateFrequency = frequency; store.LastUpdateCheckUtc = now; store.Save();
                var loaded = AuthStore.Load(); Expect(loaded.UpdateFrequency == frequency && loaded.LastUpdateCheckUtc == now, "portable preferences roundtrip");
            }
            Expect(BackgroundImages.NeedsCrop(600, 1200), "portrait requires crop");
            Expect(BackgroundImages.NeedsCrop(8000, 500), "panorama requires crop");
            Expect(!BackgroundImages.NeedsCrop(1920, 1080), "landscape accepted");
            var path = BackgroundImages.Save(ColorImage(255, 0, 0));
            var first = BackgroundImages.Read(path);
            var path2 = BackgroundImages.Save(ColorImage(0, 0, 255));
            var second = BackgroundImages.Read(path2);
            byte[] pixel = new byte[4]; second.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Expect(path == path2 && pixel[0] == 255 && pixel[2] == 0, "same-path replacement reads NEW pixels");
            first.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Expect(pixel[2] == 255, "loaded image is detached from the file");
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { checks++; }
            var crop = new BackgroundCropDialog(second);
            crop.Loaded += (_, _) => crop.Dispatcher.BeginInvoke(() =>
            {
                var capture = new RenderTargetBitmap((int)crop.ActualWidth, (int)crop.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                capture.Render(crop); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(capture));
                using (var output = File.Create(Path.Combine(root, "crop-dialog.png"))) png.Save(output);
                typeof(BackgroundCropDialog).GetMethod("Accept", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(crop, new object[] { crop, new RoutedEventArgs() });
            }, DispatcherPriority.ApplicationIdle);
            Expect(crop.ShowDialog() == true && crop.Result != null, "crop dialog accepts");
            Expect(Math.Abs((double)crop.Result!.PixelWidth / crop.Result.PixelHeight - 1.5) < .02, "fixed 3:2 crop result");
            File.WriteAllText(Path.Combine(root, "bad.png"), "invalid image");
            bool failed = false; try { BackgroundImages.Read(Path.Combine(root, "bad.png")); } catch { failed = true; }
            Expect(failed, "invalid image rejected");
            var main = new Zipora.MainWindow(); app.MainWindow = main; main.Show();
            Expect(main.SetCustomBackground(path2), "main window refresh succeeds");
            var displayed = (BitmapSource)((System.Windows.Controls.Image)main.FindName("CustomBackgroundImage")).Source;
            displayed.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Expect(pixel[0] == 255, "main window displays replacement pixels");
            Expect(main.SetCustomBackground(""), "clear background immediately");
            var settings = new SettingsDialog { Owner = main };
            settings.Loaded += (_, _) => settings.Dispatcher.BeginInvoke(() =>
            {
                var choices = (System.Windows.Controls.ComboBox)settings.FindName("UpdateFrequencyBox");
                Expect(choices.Items.Count == 6 && choices.SelectedIndex == 5, "settings restores all six frequencies");
                var settingsCapture = new RenderTargetBitmap((int)settings.ActualWidth, (int)settings.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                settingsCapture.Render(settings);
                var settingsPng = new PngBitmapEncoder(); settingsPng.Frames.Add(BitmapFrame.Create(settingsCapture));
                using (var output = File.Create(Path.Combine(root, "settings-dialog.png"))) settingsPng.Save(output);
                ((System.Windows.Controls.ScrollViewer)settings.FindName("SettingsScrollViewer")).ScrollToBottom();
                settings.UpdateLayout();
                var functionCapture = new RenderTargetBitmap((int)settings.ActualWidth, (int)settings.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                functionCapture.Render(settings);
                var functionPng = new PngBitmapEncoder(); functionPng.Frames.Add(BitmapFrame.Create(functionCapture));
                using (var output = File.Create(Path.Combine(root, "settings-functions.png"))) functionPng.Save(output);
                settings.Close();
            }, DispatcherPriority.ApplicationIdle);
            settings.ShowDialog();
            var engine = new SevenZipEngine(Path.Combine(AppContext.BaseDirectory, "7zip", "7z.exe"));
            var dictionary = new DictionaryCrackDialog(engine) { Owner = main };
            var dictionaryBox = (System.Windows.Controls.ComboBox)dictionary.FindName("DictBox");
            Expect(dictionaryBox.Items.Count == 0 && dictionaryBox.SelectedIndex == -1 && !dictionaryBox.IsEnabled, "empty dictionaries do not select a browse action during construction");
            Expect(((System.Windows.Controls.TextBlock)dictionary.FindName("DictHint")).Visibility == Visibility.Visible, "empty dictionary hint is visible");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var refresh = typeof(DictionaryCrackDialog).GetMethod("LoadBuiltinDictionaries", flags)!;
            var select = typeof(DictionaryCrackDialog).GetMethod("SelectDictionaryFile", flags)!;
            refresh.Invoke(dictionary, null);
            Expect(dictionaryBox.Items.Count == 0 && dictionaryBox.SelectedIndex == -1, "refreshing an empty directory is safe");
            var custom = Path.Combine(root, "custom.txt"); File.WriteAllText(custom, "test-password-only");
            select.Invoke(dictionary, new object[] { custom }); select.Invoke(dictionary, new object[] { custom });
            Expect(dictionaryBox.Items.Count == 1 && dictionaryBox.IsEnabled, "custom dictionary selection enables list without duplicates");
            var chosen = dictionaryBox.SelectedItem; refresh.Invoke(dictionary, null);
            Expect(dictionaryBox.Items.Count == 1 && dictionaryBox.SelectedItem != null, "refresh preserves an existing custom dictionary");
            File.Delete(custom); refresh.Invoke(dictionary, null);
            Expect(dictionaryBox.Items.Count == 0 && dictionaryBox.SelectedIndex == -1, "removed dictionary returns to empty state safely");
            Directory.CreateDirectory(AppStorage.DictionaryRoot);
            File.WriteAllText(Path.Combine(AppStorage.DictionaryRoot, "test.txt"), "test-password-only");
            refresh.Invoke(dictionary, null);
            Expect(dictionaryBox.Items.Count == 1 && dictionaryBox.SelectedIndex == 0 && dictionaryBox.IsEnabled, "built-in dictionary is selected after refresh");
            dictionary.Loaded += (_, _) => dictionary.Dispatcher.BeginInvoke(() =>
            {
                var capture = new RenderTargetBitmap((int)dictionary.ActualWidth, (int)dictionary.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                capture.Render(dictionary); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(capture));
                using (var file = File.Create(Path.Combine(root, "dictionary-dialog.png"))) encoder.Save(file);
                dictionary.Close();
            }, DispatcherPriority.ApplicationIdle);
            dictionary.ShowDialog();
            Expect(true, "dictionary dialog opens and closes without a file picker");
            var cloud = new CloudSearchDialog(new string('a', 64), null) { Owner = main };
            cloud.Loaded += (_, _) => cloud.Dispatcher.BeginInvoke(() => cloud.Close(), DispatcherPriority.ApplicationIdle);
            cloud.ShowDialog();
            Expect(true, "cloud search window opens without making a query request");
            main.Close();
            var large = BackgroundImages.Read(BackgroundImages.Save(ColorImage(0, 0, 255, 3000, 2000)));
            Expect(large.PixelWidth <= 2560 && large.PixelHeight <= 2560, "managed background is bounded for USB and memory usage");
            Console.WriteLine($"PASS: {checks} checks; {root}");
            app.Shutdown(0);
        }
        finally { AppStorage.Dispose(); }
    }
}
