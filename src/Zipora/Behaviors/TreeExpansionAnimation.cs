using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Zipora.Services;

namespace Zipora.Behaviors;

/// <summary>
/// 目录树展开/收起按真实高度滚动展开，而不是整块瞬现。
/// 高度取自展开后的实际布局，因此不压缩文字、不改变缩进；
/// 系统关闭界面动画时直接落到终态。
/// </summary>
public static class TreeExpansionAnimation
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(TreeExpansionAnimation),
        new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private sealed class State { public int Version; }

    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    private static int Next(FrameworkElement host)
    {
        var state = States.GetOrCreateValue(host);
        return ++state.Version;
    }

    private static bool IsCurrent(FrameworkElement host, int version)
        => States.TryGetValue(host, out var state) && state.Version == version;

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeViewItem item) return;
        item.Expanded -= Item_Expanded;
        item.Collapsed -= Item_Collapsed;
        if (!(bool)e.NewValue) return;
        item.Expanded += Item_Expanded;
        item.Collapsed += Item_Collapsed;
    }

    /// <summary>模板里的子项容器；未应用模板时返回 null，直接走默认行为。</summary>
    private static FrameworkElement? ResolveHost(TreeViewItem item)
        => item.Template?.FindName("ItemsHost", item) as FrameworkElement;

    private static void Item_Expanded(object sender, RoutedEventArgs e)
    {
        // Expanded 会从子项冒泡上来，只处理自己这一次展开。
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        if (sender is not TreeViewItem item) return;
        if (ResolveHost(item) is not { } host) return;

        int version = Next(host);
        host.ClipToBounds = true;
        host.BeginAnimation(FrameworkElement.HeightProperty, null);
        host.BeginAnimation(UIElement.OpacityProperty, null);
        host.Opacity = 1;
        host.Visibility = Visibility.Visible;
        host.Height = double.NaN;

        // 先让布局算出展开后的自然高度，再用它作为动画终点。
        item.UpdateLayout();
        double target = host.ActualHeight > 0.5 ? host.ActualHeight : host.DesiredSize.Height;
        if (!Motion.Enabled || target < 0.5)
        {
            host.Height = double.NaN;
            return;
        }

        host.Height = 0;
        var grow = new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        grow.Completed += (_, _) =>
        {
            if (!IsCurrent(host, version)) return;
            host.BeginAnimation(FrameworkElement.HeightProperty, null);
            // 动画结束回到自动高度，后续增删子项仍能正常排版。
            host.Height = double.NaN;
        };
        host.BeginAnimation(FrameworkElement.HeightProperty, grow);

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        host.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static void Item_Collapsed(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        if (sender is not TreeViewItem item) return;
        if (ResolveHost(item) is not { } host) return;

        int version = Next(host);
        // 模板触发器会把容器直接折叠，这里改回可见，先播完收起动画。
        host.Visibility = Visibility.Visible;
        host.BeginAnimation(UIElement.OpacityProperty, null);
        host.Opacity = 1;
        double current = host.ActualHeight;
        host.BeginAnimation(FrameworkElement.HeightProperty, null);

        if (!Motion.Enabled || current < 0.5)
        {
            host.Height = double.NaN;
            host.Visibility = Visibility.Collapsed;
            return;
        }

        var shrink = new DoubleAnimation(current, 0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        shrink.Completed += (_, _) =>
        {
            if (!IsCurrent(host, version)) return;
            host.BeginAnimation(FrameworkElement.HeightProperty, null);
            host.Height = double.NaN;
            host.Visibility = Visibility.Collapsed;
        };
        host.BeginAnimation(FrameworkElement.HeightProperty, shrink);

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        host.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
