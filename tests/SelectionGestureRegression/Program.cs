using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Zipora.Services;

internal static class Program
{
    private static int _checks;
    private static Zipora.MainWindow _window = null!;
    private static ListView _list = null!;
    private static ObservableCollection<ArchiveEntry> _entries = null!;
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        Application? app = null;
        try
        {
            string root = Path.GetFullPath(args.Length > 0 ? args[0] :
                $"../../../../../../builds/regression/full/selection-{DateTime.Now:yyyyMMdd-HHmmss}");
            if (Directory.Exists(root)) throw new InvalidOperationException("Use a new output directory for isolated storage.");
            Directory.CreateDirectory(Path.Combine(root, "App"));
            File.WriteAllText(Path.Combine(root, "portable.json"), "{}");
            AppStorage.Initialize(Path.Combine(root, "App"));
            var preferences = AuthStore.Load();
            preferences.DeviceFp = new string('a', 64);
            preferences.UpdateFrequency = UpdateFrequency.Never;
            preferences.WindowBackdrop = "Solid";
            preferences.Save();

            // A bare Application loads the production theme without invoking
            // Zipora.App.OnStartup (which would initialize portable storage twice).
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var xml = XDocument.Load(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../../src/Zipora/App.xaml")));
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var resources = xml.Root!.Element(ns + "Application.Resources")!.Elements().Single();
            resources.SetAttributeValue(XNamespace.Xmlns + "ui", "http://schemas.lepo.co/wpfui/2022/xaml");
            resources.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
            app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString().Replace(
                "Source=\"Themes/Zipora.xaml\"", "Source=\"/Zipora;component/Themes/Zipora.xaml\""));
            ThemeManager.Init("Light");

            Expect(File.Exists(Path.Combine(AppContext.BaseDirectory, "7zip", "7z.exe")),
                "test output contains engine; constructor will not show a missing-engine prompt");
            _window = new Zipora.MainWindow
            {
                Width = 1100, Height = 800, Left = -12000, Top = -12000,
                ShowActivated = false, ShowInTaskbar = false,
            };
            // A capture transition can synchronously route the *physical*
            // mouse position/button state. Suppress those uncontrolled move
            // events in this fixture; all gesture moves below are supplied
            // explicitly to the production method. Capture-loss events remain
            // enabled and are exercised with a real Mouse.Capture transition.
            _window.PreviewMouseMove += (_, e) => e.Handled = true;
            app.MainWindow = _window;
            _list = (ListView)_window.FindName("FileList");
            _entries = Field<ObservableCollection<ArchiveEntry>>("_visibleEntries");
            foreach (int index in Enumerable.Range(0, 3000))
                _entries.Add(new ArchiveEntry { Path = (index % 5) switch
                {
                    0 => $"中文文件-{index:D5}.txt", 1 => $"📂-emoji-{index:D5}.txt",
                    2 => $"sample-{index:D5}.txt", 3 => $"一段很长的文件名称-{index:D5}-" + new string('W', 100) + ".txt",
                    _ => $"sample-{index:D5}.zip",
                }, Size = 10 });
            Invoke("ShowEmptyState", false);
            _window.Show();
            _window.UpdateLayout();
            _window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            Expect(_list.ActualHeight > 200 && _list.ActualWidth > 200, "production list is laid out");
            Expect(RowBounds(2).Height > 0, "real row geometry available");
            double height = RowBounds(0).Height;
            Expect(Enumerable.Range(0, 10).All(i => Math.Abs(RowBounds(i).Height - height) < .01),
                "Chinese, emoji and long non-wrapping names retain uniform row geometry");
            // All following checks run synchronously: no real pointer input is
            // injected and the timer does not consume the user's physical mouse.
            CheckPendingClick();
            CheckControlToggle();
            CheckAdditiveModifiers(ModifierKeys.Shift);
            CheckAdditiveModifiers(ModifierKeys.Control | ModifierKeys.Shift);
            CheckPlainBlankSelection();
            CheckLateControlAndCaptureLoss();
            CheckCancellationInputs();
            CheckVirtualizedScrolling();
            Invoke("ResetPointerGesture");
            Expect(!Field<bool>("_dragOutStarted"), "selection tests never start OLE extraction");
            Console.WriteLine($"PASS: {_checks} selection gesture checks; storage: {root}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error is TargetInvocationException { InnerException: { } inner } ? inner : error);
            return 1;
        }
        finally
        {
            if (_window != null)
            {
                Invoke("ResetPointerGesture");
                _window.Close();
            }
            app?.Shutdown();
            AppStorage.Dispose();
        }
    }

    private static void CheckPendingClick()
    {
        Select(0, 2, 9);
        var start = RowPoint(2);
        Invoke("PrepareMarquee", start, ModifierKeys.Control, true);
        Expect(Field<bool>("_marqueePending") && !Field<bool>("_marqueeActive"), "Ctrl item starts pending, no rectangle yet");
        Selected(0, 2, 9);
        // The production PreviewMouseDown intentionally leaves native Ctrl click
        // selection/focus/Shift-anchor logic intact. Model that native toggle.
        _list.SelectedItems.Remove(_entries[2]);
        Expect(!Move(new Point(start.X + SystemParameters.MinimumHorizontalDragDistance / 2, start.Y), true, ModifierKeys.Control),
            "sub-threshold movement does not claim native click");
        Expect(!Field<bool>("_marqueeActive"), "small click jitter does not show rectangle");
        Move(start, false, ModifierKeys.Control);
        Selected(0, 9);
        Expect(!Field<bool>("_marqueePending") && !Field<bool>("_marqueeActive"), "Ctrl click release ends pending state");
        Expect(!OwnsCapture, "Ctrl click release leaves no mouse capture");
    }

    private static void CheckControlToggle()
    {
        Select(0, 2, 9);
        var start = RowPoint(2);
        Invoke("PrepareMarquee", start, ModifierKeys.Control, true);
        _list.SelectedItems.Remove(_entries[2]); // native down toggle must not become the drag baseline
        var target = RowPoint(4, start.X + 80);
        Expect(Move(target, true, ModifierKeys.Control), "Ctrl drag crosses threshold and claims selection");
        Expect(Field<bool>("_marqueeActive") && !Field<bool>("_dragCandidate"), "Ctrl marquee suppresses extraction candidate");
        Selected(0, 3, 4, 9);
        Move(target, true, ModifierKeys.Control);
        Selected(0, 3, 4, 9); // repeated motion cannot toggle again
        Move(RowPoint(3, start.X + 80), true, ModifierKeys.Control);
        Selected(0, 3, 9);
        Move(target, true, ModifierKeys.None);
        Selected(0, 3, 4, 9);
        Expect(Field<bool>("_marqueeActive") && !Field<bool>("_dragOutStarted"), "releasing Ctrl mid-drag remains a marquee");
        Invoke("CancelMarquee");
        Selected(0, 2, 9);
        Expect(!Field<bool>("_marqueeActive") && !OwnsCapture, "cancel restores baseline and releases capture");

        Invoke("PrepareMarquee", RowPoint(6), ModifierKeys.Control, true);
        _list.SelectedItems.Add(_entries[6]); // native Ctrl down on an unselected row
        Move(RowPoint(7, 180), true, ModifierKeys.Control);
        Selected(0, 2, 6, 7, 9);
        Move(RowPoint(7, 180), false, ModifierKeys.None);
        Selected(0, 2, 6, 7, 9);
        Expect(!Field<bool>("_marqueeActive") && !OwnsCapture, "mouse release commits and clears rectangle capture");
    }

    private static void CheckAdditiveModifiers(ModifierKeys modifiers)
    {
        Select(0, 2, 9);
        Invoke("PrepareMarquee", RowPoint(2), modifiers, false);
        Move(RowPoint(4, 180), true, modifiers);
        Selected(0, 2, 3, 4, 9);
        Move(RowPoint(3, 180), true, ModifierKeys.None);
        Selected(0, 2, 3, 9);
        Invoke("CancelMarquee");
        Selected(0, 2, 9);
    }

    private static void CheckPlainBlankSelection()
    {
        Select(0, 2, 9);
        var start = RowPoint(5);
        Invoke("PrepareMarquee", start, ModifierKeys.None, false);
        Selected();
        Expect(!Field<bool>("_marqueeActive"), "plain blank down clears selection without showing a zero-area rectangle");
        Move(RowPoint(7, 180), true, ModifierKeys.None);
        Selected(5, 6, 7);
        Invoke("CancelMarquee");
        Selected(0, 2, 9);
        Invoke("PrepareMarquee", start, ModifierKeys.None, false);
        Move(start, false, ModifierKeys.None);
        Selected();
        Expect(!OwnsCapture, "blank click releases capture");
    }

    private static void CheckLateControlAndCaptureLoss()
    {
        Select(0, 2);
        SetField("_mouseDownPos", RowPoint(2));
        SetField("_dragCandidate", true);
        Expect(Move(RowPoint(4, 180), true, ModifierKeys.Control), "Ctrl pressed after down converts candidate into marquee");
        Selected(0, 3, 4);
        Expect(!Field<bool>("_dragCandidate") && !Field<bool>("_dragOutStarted"), "late Ctrl does not start drag extraction");
        // Actual routed capture-loss event runs the production cleanup handler.
        Mouse.Capture(null);
        Selected(0, 2);
        Expect(!Field<bool>("_marqueeActive") && !Field<bool>("_marqueePending"), "capture loss cancels and restores");
    }

    private static void CheckVirtualizedScrolling()
    {
        Select();
        var viewer = Descendants(_list).OfType<ScrollViewer>().First();
        viewer.ScrollToTop();
        _window.UpdateLayout();
        Expect(viewer.ScrollableHeight > 1000, "large list supports pixel scrolling");
        Expect(Descendants(_list).OfType<ListViewItem>().Count() < 3000, "selection leaves row virtualization enabled");
        var start = RowPoint(2);
        Invoke("PrepareMarquee", start, ModifierKeys.Control, true);
        var viewport = (Rect)Invoke("GetMarqueeViewport")!;
        var below = new Point(start.X + 80, viewport.Bottom + 30);
        Move(RowPoint(4, start.X + 80), true, ModifierKeys.Control);
        if (SystemParameters.WheelScrollLines != 0)
        {
            var wheelPoint = RowPoint(4, start.X + 80);
            Invoke("ScrollMarqueeWheel", wheelPoint, -120);
            Expect(viewer.VerticalOffset > 0, "wheel scrolls while marquee owns capture");
            Expect(_list.SelectedItems.Cast<ArchiveEntry>().Select(_entries.IndexOf).Max() > 4,
                "wheel scrolling extends the content-coordinate selection");
            Invoke("ScrollMarqueeWheel", wheelPoint, 120);
            Expect(viewer.VerticalOffset == 0, "opposite wheel restores scroll offset");
            Move(RowPoint(4, start.X + 80), true, ModifierKeys.None);
            Selected(2, 3, 4);
        }
        for (int i = 0; i < 32; i++) Invoke("ScrollMarqueeAt", below);
        Expect(viewer.VerticalOffset > 300, "bottom-edge marquee auto-scrolls");
        var selected = _list.SelectedItems.Cast<ArchiveEntry>().Select(_entries.IndexOf).OrderBy(i => i).ToArray();
        Expect(selected.Length > 15 && selected[0] == 2, "offscreen starting rows remain in the marquee");
        Expect(selected.SequenceEqual(Enumerable.Range(2, selected[^1] - 1)), "virtualized marquee contains every row without gaps");
        Expect(_list.ItemContainerGenerator.ContainerFromIndex(2) == null || !RowBounds(2).IntersectsWith(viewport),
            "anchor is offscreen while selection remains stable");
        // Move back up with the same gesture. Rows below the shrunken rectangle
        // must be removed even though their containers are now virtualized.
        var above = new Point(start.X + 80, viewport.Top - 30);
        for (int i = 0; i < 48 && viewer.VerticalOffset > 0; i++) Invoke("ScrollMarqueeAt", above);
        Expect(viewer.VerticalOffset == 0, "top-edge marquee scrolls back to the original position");
        Move(RowPoint(4, start.X + 80), true, ModifierKeys.None);
        Selected(2, 3, 4);
        Invoke("ResetPointerGesture");
        Expect(!OwnsCapture && !Field<bool>("_marqueeActive"), "scrolling marquee cleanup releases capture");
        Expect(!Field<DispatcherTimer>("_marqueeScrollTimer").IsEnabled, "completed marquee stops its autoscroll timer");
        Expect(((UIElement)_window.FindName("MarqueeCanvas")).Visibility == Visibility.Collapsed,
            "completed marquee hides its overlay");
        Expect(Descendants(_list).OfType<ListViewItem>().Count() < 3000, "large list remains virtualized after selection");
    }

    private static void CheckCancellationInputs()
    {
        foreach (string input in new[] { "Escape", "Deactivated", "Disabled" })
        {
            Select(0, 2, 9);
            Invoke("PrepareMarquee", RowPoint(2), ModifierKeys.Control, true);
            _list.SelectedItems.Remove(_entries[2]);
            Move(RowPoint(4, 180), true, ModifierKeys.Control);
            Selected(0, 3, 4, 9);
            switch (input)
            {
                case "Escape":
                    var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(_window),
                        Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    Invoke("FileList_PreviewKeyDown", _list, key);
                    Expect(key.Handled, "Escape is handled by the production key handler");
                    break;
                case "Deactivated":
                    Invoke("OnDeactivated", EventArgs.Empty);
                    break;
                case "Disabled":
                    _list.IsEnabled = false;
                    _list.IsEnabled = true;
                    break;
            }
            Selected(0, 2, 9);
            Expect(!Field<bool>("_marqueeActive") && !Field<bool>("_marqueePending") && !OwnsCapture,
                $"{input} cancels selection and releases every gesture capture");
        }
    }

    private static void Select(params int[] indices)
    {
        Invoke("ResetPointerGesture");
        var viewer = Descendants(_list).OfType<ScrollViewer>().First();
        viewer.ScrollToTop(); _window.UpdateLayout();
        _list.SelectedItems.Clear();
        foreach (int index in indices) _list.SelectedItems.Add(_entries[index]);
    }

    private static void Selected(params int[] expected)
    {
        var actual = _list.SelectedItems.Cast<ArchiveEntry>().Select(_entries.IndexOf).OrderBy(i => i).ToArray();
        Expect(actual.SequenceEqual(expected.OrderBy(i => i)), $"selection is [{string.Join(',', expected)}], actual [{string.Join(',', actual)}]");
    }

    private static Point RowPoint(int index, double x = 100)
    {
        var bounds = RowBounds(index);
        if (bounds.IsEmpty) throw new Exception($"Test row {index} is not realized");
        return new Point(Math.Clamp(x, bounds.Left + 20, bounds.Right - 20), bounds.Top + bounds.Height / 2);
    }

    private static Rect RowBounds(int index) => (Rect)Invoke("GetItemBounds", index)!;
    private static bool Move(Point point, bool down, ModifierKeys modifiers) => (bool)Invoke("TryUpdateMarquee", point, down, modifiers)!;
    private static bool OwnsCapture => ReferenceEquals(Mouse.Captured, _list) ||
        ReferenceEquals(Mouse.Captured, _window.FindName("FileListCard"));
    private static object? Invoke(string name, params object[] args) => typeof(Zipora.MainWindow)
        .GetMethod(name, PrivateInstance)!.Invoke(_window, args);
    private static T Field<T>(string name) => (T)typeof(Zipora.MainWindow).GetField(name, PrivateInstance)!.GetValue(_window)!;
    private static void SetField(string name, object value) => typeof(Zipora.MainWindow).GetField(name, PrivateInstance)!.SetValue(_window, value);
    private static void Expect(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }
}
