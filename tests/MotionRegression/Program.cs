using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using System.IO;
using System.Reflection;
using Zipora.Behaviors;
using Zipora.Services;

/// <summary>
/// 动效回归：只验证「动画结束后落到正确终态」和「快速反向不会留下错误状态」，
/// 不把某一帧的观感当作通过依据。实际手感仍需人工确认。
/// CI 等无桌面会话环境下系统会关闭界面动画，此时 Motion 直接落终态，断言同样成立。
/// </summary>
internal static class Program
{
    private static int _checks;
    private static int _skipped;

    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new Zipora.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            ThemeManager.Init("Light");
            ApplyMotionOverride();
            Console.WriteLine($"motion animations enabled: {Motion.Enabled}");

            CheckFadeInSettles();
            CheckFadeOutCollapses();
            CheckInterruptedFadeDoesNotRehide();
            CheckFadeInAfterShow();
            CheckSmoothWheelScroll();
            CheckFileRowStateWashes();
            CheckTreeExpansion();

            var note = _skipped == 0 ? string.Empty : $"; {_skipped} frame checks skipped (system animations off)";
            Console.WriteLine($"PASS: {_checks} motion checks{note}");
            app.Shutdown();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    /// <summary>
    /// 设置 ZIPORA_MOTION_FORCE=on/off 可在同一台机器上分别覆盖「动画开启」和
    /// 「系统关闭动画」两条分支；不设置时跟随系统设置（CI 默认即关闭）。
    /// </summary>
    private static void ApplyMotionOverride()
    {
        var value = Environment.GetEnvironmentVariable("ZIPORA_MOTION_FORCE");
        if (string.IsNullOrWhiteSpace(value)) return;
        bool enabled = value.Equals("on", StringComparison.OrdinalIgnoreCase) || value == "1";
        var field = typeof(Motion).GetField("_forcedEnabled", BindingFlags.NonPublic | BindingFlags.Static);
        if (field is null) throw new Exception("Motion override field missing");
        field.SetValue(null, (bool?)enabled);
    }

    private static void CheckFadeInSettles()
    {
        var element = new Border { Width = 100, Height = 40, Opacity = 0 };
        var host = Host(element);
        Motion.FadeIn(element, dx: 12, dy: 8, ms: 80, restart: true);
        Pump(320);
        Check(element.Opacity > 0.999, "fade-in ends fully visible");
        Check(element.Visibility == Visibility.Visible, "fade-in leaves element visible");
        // 系统关闭界面动画时 RenderTransform 保持 Transform.Identity（MatrixTransform），
        // 因此读取矩阵偏移，兼容「有动画」和「直接落终态」两种环境。
        var shift = element.RenderTransform.Value;
        Check(Math.Abs(shift.OffsetX) < 0.01 && Math.Abs(shift.OffsetY) < 0.01,
            "fade-in settles at zero offset (no snap-back)");
        Check(!element.HasAnimatedProperties, "fade-in releases its animations");
        host.Close();
    }

    private static void CheckFadeOutCollapses()
    {
        var element = new Border { Width = 100, Height = 40 };
        var host = Host(element);
        Motion.FadeOut(element, 80, collapse: true);
        Pump(320);
        Check(element.Visibility == Visibility.Collapsed, "fade-out collapse hides element");
        Check(element.Opacity > 0.999, "collapsed element keeps a visible base opacity for next show");
        Motion.Show(element);
        Check(element.Visibility == Visibility.Visible && element.Opacity > 0.999, "show restores a collapsed element");
        host.Close();
    }

    private static void CheckInterruptedFadeDoesNotRehide()
    {
        var element = new Border { Width = 100, Height = 40 };
        var host = Host(element);
        Motion.FadeOut(element, 400, collapse: true);
        Pump(60);
        Motion.FadeIn(element, ms: 80);
        Pump(600); // 超过旧淡出动画的总时长
        Check(element.Visibility == Visibility.Visible, "a superseded fade-out must not collapse the element later");
        Check(element.Opacity > 0.999, "re-shown element is fully visible");
        host.Close();
    }

    /// <summary>调用方先把元素设为 Visible 再淡入时，入场动画不能因为基础不透明度为 1 而消失。</summary>
    private static void CheckFadeInAfterShow()
    {
        var element = new Border { Width = 100, Height = 40, Visibility = Visibility.Collapsed };
        var host = Host(element);
        element.Visibility = Visibility.Visible;
        Motion.FadeIn(element, dy: 4, ms: 200);
        if (Motion.Enabled)
        {
            Pump(40);
            Check(element.Opacity < 0.95, "fade-in still starts transparent when the caller pre-shows the element");
        }
        else
        {
            _skipped++;
        }
        Pump(400);
        Check(element.Opacity > 0.999, "pre-shown element still settles fully visible");
        host.Close();
    }

    private static void CheckSmoothWheelScroll()
    {
        var panel = new StackPanel();
        for (int i = 0; i < 200; i++) panel.Children.Add(new TextBlock { Text = $"row {i}", Height = 20 });
        var viewer = new ScrollViewer
        {
            Height = 300, Width = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
        };
        SmoothWheelScroll.SetEnabled(viewer, true);
        var host = Host(viewer);
        Pump(120);
        viewer.UpdateLayout();
        Check(viewer.ScrollableHeight > 0, "test scroller has scrollable content");

        if (Motion.Enabled)
        {
            double start = viewer.VerticalOffset;
            viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            Pump(300);
            Check(viewer.VerticalOffset > start, "wheel down moves the viewport");
            double afterOne = viewer.VerticalOffset;

            // 连续反向：动画途中再次滚动，最终应从当前可见位置接续，而不是排队或回跳。
            viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            Pump(40);
            viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
            { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            Pump(400);
            Check(viewer.VerticalOffset < afterOne + 60, "rapid reverse scroll continues from the visible offset");

            // 键盘/拖动滚动条等外部滚动后，下一次滚轮必须从当前可见位置继续，不能回到旧目标。
            viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * 0.5);
            Pump(60);
            double external = viewer.VerticalOffset;
            viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            Pump(300);
            Check(viewer.VerticalOffset > external + 1, "wheel continues from an externally changed offset");

            // 连续向下直到边界，不得超出可滚动范围。
            for (int i = 0; i < 200; i++)
            {
                viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                Pump(8);
            }
            Pump(500);
            Check(Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) < 1.5,
                "repeated wheel reaches the bottom without overshoot");
        }
        else
        {
            // 系统关闭动画时不接管滚轮，交给原生滚动，因此这里没有可断言的动画帧。
            _skipped += 4;
        }
        host.Close();
    }

    /// <summary>文件列表行的悬浮/选中底纹必须在动画结束后真正可见，且文字位置不变。</summary>
    private static void CheckFileRowStateWashes()
    {
        var app = Application.Current;

        var view = new GridView { AllowsColumnReorder = false };
        view.Columns.Add(new GridViewColumn { Header = "名称", Width = 300 });
        var list = new ListView
        {
            Style = (Style)app.FindResource("FileListView"), View = view,
            ItemsSource = Enumerable.Range(0, 20).Select(i => $"sample-{i}.txt").ToArray(),
        };
        var host = Host(list);
        Pump(120);
        list.UpdateLayout();

        var row = list.ItemContainerGenerator.ContainerFromIndex(3) as ListViewItem;
        Check(row != null, "row container realized");
        if (row is null) throw new Exception("row container not realized");
        var text = FindText(row);
        double textLeft = text.TranslatePoint(new Point(0, 0), row).X;

        list.SelectedIndex = 3;
        Pump(320);
        var selectWash = (Border)row.Template.FindName("SelectWash", row);
        var indicator = (Border)row.Template.FindName("SelectionIndicator", row);
        Check(selectWash.Opacity > 0.99, "selected row wash is fully visible after the transition");
        Check(indicator.Opacity > 0.99, "selection indicator is fully visible after the transition");
        Check(Math.Abs(text.TranslatePoint(new Point(0, 0), row).X - textLeft) < 0.01,
            "selection does not shift row text horizontally");

        list.SelectedIndex = -1;
        Pump(320);
        Check(selectWash.Opacity < 0.01 && indicator.Opacity < 0.01, "clearing selection fades the wash out");
        Check(Math.Abs(text.TranslatePoint(new Point(0, 0), row).X - textLeft) < 0.01,
            "clearing selection does not shift row text horizontally");
        host.Close();

        // 初始选中（例如恢复上次选择）发生在模板首次应用之前，不能依赖 EnterActions 才显示底纹。
        var restoredView = new GridView { AllowsColumnReorder = false };
        restoredView.Columns.Add(new GridViewColumn { Header = "名称", Width = 300 });
        var restored = new ListView
        {
            Style = (Style)app.FindResource("FileListView"), View = restoredView,
            ItemsSource = Enumerable.Range(0, 20).Select(i => $"restored-{i}.txt").ToArray(),
            SelectedIndex = 0,
        };
        var restoredHost = Host(restored);
        Pump(240);
        restored.UpdateLayout();
        var restoredRow = (ListViewItem)restored.ItemContainerGenerator.ContainerFromIndex(0)!;
        var restoredWash = (Border)restoredRow.Template.FindName("SelectWash", restoredRow);
        Check(restoredWash.Opacity > 0.99, "row selected before template load shows its wash");
        restoredHost.Close();
    }

    private static TextBlock FindText(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text) return text;
            var nested = FindTextOrDefault(child);
            if (nested != null) return nested;
        }
        throw new Exception("no text found in row");
    }

    /// <summary>目录树必须按真实高度展开，收起后子项容器真正折叠。</summary>
    private static void CheckTreeExpansion()
    {
        var style = (Style)Application.Current.FindResource("ArchiveTreeItem");
        var root = new TreeViewItem { Header = "root" };
        root.Items.Add(new TreeViewItem { Header = "child-0" });
        root.Items.Add(new TreeViewItem { Header = "child-1" });
        root.Items.Add(new TreeViewItem { Header = "child-2" });
        var tree = new TreeView { ItemContainerStyle = style };
        tree.Items.Add(root);
        var host = Host(tree);
        Pump(160);
        tree.UpdateLayout();

        var item = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0)!;
        Check(item.Visibility == Visibility.Visible, "tree root container realized");
        var hostPresenter = (FrameworkElement)item.Template.FindName("ItemsHost", item);
        Check(hostPresenter.Visibility == Visibility.Collapsed, "collapsed tree node hides its children");

        item.IsExpanded = true;
        double expandedHeight;
        if (Motion.Enabled)
        {
            Pump(60);
            Check(hostPresenter.Visibility == Visibility.Visible, "expanding tree node shows its children");
            double midHeight = hostPresenter.ActualHeight;
            Pump(320);
            Check(hostPresenter.ActualHeight > 20, "expanded tree children receive real layout height");
            expandedHeight = hostPresenter.ActualHeight;
            Check(midHeight > 0 && midHeight < expandedHeight - 1, "tree expands gradually instead of snapping open");

            item.IsExpanded = false;
            Pump(40);
            Check(hostPresenter.Visibility == Visibility.Visible, "collapse animation keeps children visible while shrinking");
            Check(hostPresenter.ActualHeight < expandedHeight, "tree children shrink before collapsing");
            Pump(360);
            Check(hostPresenter.Visibility == Visibility.Collapsed, "collapsed tree node hides children after the animation");
        }
        else
        {
            // 系统关闭动画时直接落终态，只验证展开/收起后的布局结果。
            _skipped += 3;
            Pump(320);
            Check(hostPresenter.ActualHeight > 20, "expanded tree children receive real layout height");
            expandedHeight = hostPresenter.ActualHeight;
            item.IsExpanded = false;
            Pump(360);
            Check(hostPresenter.Visibility == Visibility.Collapsed, "collapsed tree node hides children after the animation");
        }
        item.IsExpanded = true;
        Pump(320);
        Check(hostPresenter.ActualHeight > expandedHeight - 1, "re-expanding restores the full height");
        host.Close();
    }

    private static TextBlock? FindTextOrDefault(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text) return text;
            if (FindTextOrDefault(child) is { } nested) return nested;
        }
        return null;
    }

    private static Window Host(UIElement content)
    {
        var window = new Window
        {
            Width = 360, Height = 400, ShowInTaskbar = false, ShowActivated = false,
            Left = -12000, Top = -12000, WindowStyle = WindowStyle.None,
            Content = new Border { Padding = new Thickness(8), Child = content },
        };
        window.Show();
        window.UpdateLayout();
        Pump(60);
        return window;
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }
}
