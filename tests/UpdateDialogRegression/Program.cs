using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Zipora.Dialogs;
using Zipora.Services;

internal static class Program
{
    static int checks;
    static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static int MainThread(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root)) throw new IOException("Use a new test output directory.");
        Directory.CreateDirectory(Path.Combine(root, "App")); File.WriteAllText(Path.Combine(root, "portable.json"), "{}");
        AppStorage.Initialize(Path.Combine(root, "App"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var xml = XDocument.Load(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Zipora/App.xaml")));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = xml.Root!.Element(ns + "Application.Resources")!.Elements().Single();
        resources.SetAttributeValue(XNamespace.Xmlns + "ui", "http://schemas.lepo.co/wpfui/2022/xaml");
        resources.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString().Replace("Source=\"Themes/Zipora.xaml\"", "Source=\"/Zipora;component/Themes/Zipora.xaml\""));
        try
        {
            const string markdown = "## 更新内容\n\n- **加粗**、`Zipora.exe` 和 *强调*\n- [发行页面](https://github.com/Alnitak44/Zipora/releases)\n\n1. 第一步\n2. 第二步\n\n> 保留 Data\n\n```text\nliteral **code**\n```\n\n[拒绝](javascript:alert(1)) ![示例](https://invalid.example/image.png) <Button>仅文字</Button>";
            var parsed = ReleaseNotesMarkdown.Render(markdown);
            var plain = new TextRange(parsed.ContentStart, parsed.ContentEnd).Text;
            Check(plain.Contains("更新内容") && !plain.Contains("## ") && !plain.Contains("**加粗**"), "headings and bold markup rendered");
            var paragraphs = Paragraphs(parsed.Blocks).ToArray();
            var inlines = paragraphs.SelectMany(x => Inlines(x.Inlines)).ToArray();
            Check(inlines.OfType<Bold>().Any() && inlines.OfType<Italic>().Any(), "bold and emphasis produce native inline formatting");
            Check(inlines.OfType<Run>().Any(x => x.Text == "Zipora.exe" && x.FontFamily.Source == "Consolas"), "inline code is monospaced without backticks");
            Check(parsed.Blocks.OfType<System.Windows.Documents.List>().Count() == 2, "bullet and numbered lists rendered");
            Check(inlines.OfType<Hyperlink>().Count() == 1, "only safe link becomes clickable");
            Check(plain.Contains("literal **code**") && plain.Contains("[图片：示例]") && plain.Contains("<Button>仅文字</Button>"), "code is literal; HTML and images are never executed or loaded");
            foreach (string uri in new[] { "file:///C:/Windows/a.exe", "javascript:alert(1)", "data:text/html,a", "shell:AppsFolder" })
                Check(!ReleaseNotesMarkdown.IsSafeLink(uri, out _), "unsafe link scheme rejected");
            var empty = ReleaseNotesMarkdown.Render("");
            Check(new TextRange(empty.ContentStart, empty.ContentEnd).Text.Contains("未提供"), "missing notes display a placeholder");
            var malformed = ReleaseNotesMarkdown.Render("未闭合 **加粗 [链接](https://example.com\n\n普通文本");
            Check(new TextRange(malformed.ContentStart, malformed.ContentEnd).Text.Contains("普通文本"), "malformed Markdown remains readable");
            var watch = Stopwatch.StartNew();
            ReleaseNotesMarkdown.Render(new string('[', 80_000) + "](https://invalid");
            Check(watch.Elapsed < TimeSpan.FromSeconds(5), "malformed long link parsing is bounded");
            string notes = string.Join("\n", Enumerable.Range(1, 180).Select(i => $"- **变更 {i}**：密码管理、拖拽解压与更新窗口，文件 `Zipora-v99.0.0.exe`。")) + "\n\n日志末尾验收标记";
            string displayNotes = args.Length > 1 ? File.ReadAllText(args[1]) + "\n\n" + notes : markdown + "\n\n" + notes;
            using (var json = JsonDocument.Parse(JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = "v99.0.0",
                html_url = "https://github.com/Alnitak44/Zipora/releases/tag/v99.0.0", body = notes, assets = Array.Empty<object>() })))
            {
                var release = UpdateChecker.ParseRelease(json.RootElement, true)!;
                Check(release.Notes == notes && release.ReleaseUrl.EndsWith("/v99.0.0"), "notes beyond 1800 characters are preserved with release-page URL");
            }
            Check(ReleaseNotesMarkdown.Limit(new string('x', 120_000)).Length < 100_100, "oversized release bodies have a resource limit and a full-notes fallback");
            foreach (string theme in new[] { "Light", "Dark" })
            {
                ThemeManager.Init(theme); Backdrop.Init("Solid");
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/Zipora;component/Themes/{theme}.xaml", UriKind.Relative) });
                foreach (var scenario in new[] { ("normal", new Rect(0, 0, 1200, 900)), ("small", new Rect(0, 0, 640, 480)),
                    ("narrow", new Rect(0, 0, 480, 360)), ("secondary", new Rect(-1280, 0, 1280, 720)) })
                {
                    var dialog = new UpdateDialog(new UpdateInfo("99.0.0", displayNotes, "https://github.com/download"))
                        { ShowActivated = false, ShowInTaskbar = false };
                    dialog.Show(); dialog.UpdateLayout();
                    typeof(UpdateDialog).GetMethod("FitToWorkArea", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, new object[] { scenario.Item2 });
                    dialog.UpdateLayout(); dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); dialog.UpdateLayout();
                    var viewer = (FlowDocumentScrollViewer)dialog.FindName("NotesViewer");
                    var button = (FrameworkElement)dialog.FindName("DownloadButton");
                    var buttonBottom = button.TranslatePoint(new Point(0, button.ActualHeight), dialog);
                    Check(dialog.ActualHeight <= scenario.Item2.Height - 31 && dialog.ActualWidth <= scenario.Item2.Width - 31,
                        $"{theme}/{scenario.Item1}: constrained to work area");
                    Check(dialog.Left >= scenario.Item2.Left && dialog.Top >= scenario.Item2.Top && dialog.Left + dialog.ActualWidth <= scenario.Item2.Right,
                        $"{theme}/{scenario.Item1}: stays inside monitor bounds");
                    Check(buttonBottom.Y <= dialog.ActualHeight && viewer.ActualHeight > 0, $"{theme}/{scenario.Item1}: footer visible and notes have space");
                    var scroll = FindScroll(viewer);
                    Check(scroll != null && scroll.ScrollableHeight > 0, $"{theme}/{scenario.Item1}: long notes scroll");
                    if (scenario.Item1 is "normal" or "small") Capture(dialog, Path.Combine(root, $"{theme}-{scenario.Item1}.png"));
                    scroll!.ScrollToEnd(); dialog.UpdateLayout();
                    Check(scroll.VerticalOffset > 0 && scroll.VerticalOffset >= scroll.ScrollableHeight - 2, $"{theme}/{scenario.Item1}: can reach final notes");
                    Check(new TextRange(viewer.Document.ContentStart, viewer.Document.ContentEnd).Text.Contains("日志末尾验收标记"), "last entry remains in the document");
                    dialog.Close();
                }
            }
            Console.WriteLine($"PASS: {checks} update dialog checks; {root}"); return 0;
        }
        finally { app.Shutdown(); AppStorage.Dispose(); }
    }
    [STAThread] static int Main(string[] args)
    { try { return MainThread(args); } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; } }
    static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        { if (block is Paragraph p) yield return p; else if (block is System.Windows.Documents.List list)
                foreach (var item in list.ListItems) foreach (var child in Paragraphs(item.Blocks)) yield return child; }
    }
    static IEnumerable<Inline> Inlines(InlineCollection collection)
    { foreach (var inline in collection) { yield return inline; if (inline is Span span) foreach (var child in Inlines(span.Inlines)) yield return child; } }
    static ScrollViewer? FindScroll(DependencyObject root)
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroll) return scroll; var found = FindScroll(child); if (found != null) return found; } return null; }
    static void Capture(Window window, string path)
    { var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); png.Save(stream); }
}
