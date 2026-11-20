using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Runtime.CompilerServices;
using Zipora.Services;

namespace Zipora.Behaviors;

/// <summary>
/// 让鼠标滚轮以短促缓动落到目标偏移，而不是每次整段瞬跳。
/// 连续同向滚动累加目标（不丢增量），反向或停止时从当前可见偏移接续；
/// 到达即停。系统关闭动画或按住修饰键时退回原生行为。
/// 可挂在 ScrollViewer 上，也可挂在 ListView 等宿主上（自动找到内部滚动区域）。
/// </summary>
public static class SmoothWheelScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothWheelScroll),
        new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    /// <summary>
    /// 仅供动画使用的中间偏移；变化时同步到真正的 ScrollViewer。
    /// 它不是用户可见状态的来源，真正的状态始终是 ScrollViewer.VerticalOffset。
    /// </summary>
    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.RegisterAttached(
        "AnimatedOffset", typeof(double), typeof(SmoothWheelScroll),
        new PropertyMetadata(0.0, (d, e) =>
        {
            if (d is ScrollViewer viewer) viewer.ScrollToVerticalOffset((double)e.NewValue);
        }));

    private sealed class WheelState
    {
        public double PendingTarget;
        public int Direction;
        public int Version;
        public bool Animating;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, WheelState> States = new();

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement host) return;
        host.PreviewMouseWheel -= Host_PreviewMouseWheel;
        host.PreviewMouseDown -= Host_PreviewMouseDown;
        if (!(bool)e.NewValue) return;
        host.PreviewMouseWheel += Host_PreviewMouseWheel;
        host.PreviewMouseDown += Host_PreviewMouseDown;
    }

    private static ScrollViewer? ResolveScroller(FrameworkElement host)
        => host as ScrollViewer ?? FindDescendant<ScrollViewer>(host);

    /// <summary>供按钮触发的平滑滚动（如设置页目录跳转），同样受系统动画开关控制。</summary>
    public static void AnimateTo(ScrollViewer viewer, double target, int ms = 240)
    {
        target = Math.Clamp(target, 0, viewer.ScrollableHeight);
        double current = viewer.VerticalOffset;
        viewer.BeginAnimation(AnimatedOffsetProperty, null);
        viewer.SetValue(AnimatedOffsetProperty, current);
        if (!Motion.Enabled || Math.Abs(target - current) < 0.5)
        {
            viewer.ScrollToVerticalOffset(target);
            return;
        }
        var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) => viewer.SetValue(AnimatedOffsetProperty, target);
        viewer.BeginAnimation(AnimatedOffsetProperty, animation);
    }

    private static void Host_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        var viewer = sender is FrameworkElement host ? ResolveScroller(host) : null;
        if (viewer == null) return;
        if (!Motion.Enabled || Keyboard.Modifiers != ModifierKeys.None) return;
        if (viewer.ScrollableHeight <= 0 || e.Delta == 0) return;

        // 与 WPF 原生一致：一行约 16 DIP × 系统滚轮行数（默认 3）；
        // 系统设为「一次滚动一屏」时 WheelScrollLines 返回 -1。
        int lines = SystemParameters.WheelScrollLines;
        double step = lines > 0 ? 16 * lines : Math.Max(1, viewer.ViewportHeight * 0.9);
        double notches = e.Delta / 120.0; // 触摸板可能给出非整格增量，按比例换算
        int direction = Math.Sign(e.Delta);

        if (!States.TryGetValue(viewer, out var state))
        {
            state = new WheelState();
            States.Add(viewer, state);
        }

        // 同向连续滚动在尚未到达的目标上继续累加，快速滚轮不会丢失行程；
        // 反向时丢弃未走完的目标，从当前可见位置重新起算。
        bool continuing = state.Version != 0 && state.Direction == direction &&
            (state.Animating || Math.Abs(viewer.VerticalOffset - state.PendingTarget) < 1.5);
        double origin = continuing
            ? state.PendingTarget
            : viewer.VerticalOffset;
        double target = Math.Clamp(origin - notches * step, 0, viewer.ScrollableHeight);
        if (Math.Abs(target - viewer.VerticalOffset) < 0.5) return;

        e.Handled = true;
        state.PendingTarget = target;
        state.Direction = direction;
        state.Version++;
        state.Animating = true;
        int version = state.Version;

        double current = viewer.VerticalOffset;
        viewer.BeginAnimation(AnimatedOffsetProperty, null);
        viewer.SetValue(AnimatedOffsetProperty, current);
        var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) =>
        {
            if (!States.TryGetValue(viewer, out var live) || live.Version != version) return;
            live.Animating = false;
            viewer.BeginAnimation(AnimatedOffsetProperty, null);
            viewer.SetValue(AnimatedOffsetProperty, target);
        };
        viewer.BeginAnimation(AnimatedOffsetProperty, animation);
    }

    /// <summary>用户抓住滚动条或拖动滑块时立即让位，避免动画和手动拖动互相抢。</summary>
    private static void Host_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var viewer = sender is FrameworkElement host ? ResolveScroller(host) : null;
        if (viewer == null) return;
        if (FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) == null) return;
        // 先把当前动画值固化为基准值再移除动画，否则会跳回本次滚动的起点。
        double live = (double)viewer.GetValue(AnimatedOffsetProperty);
        viewer.BeginAnimation(AnimatedOffsetProperty, null);
        viewer.SetValue(AnimatedOffsetProperty, live);
        if (States.TryGetValue(viewer, out var state))
        {
            state.Version++;
            state.Animating = false;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : null;
        }
        return null;
    }
}
