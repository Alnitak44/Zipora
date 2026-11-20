using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Zipora.Services;

/// <summary>
/// 统一动效策略：所有代码侧的过渡都从这里取值。
/// 时长刻意保持短促（120–220ms）并统一缓出收尾；遵循 Windows「在 Windows 中显示动画」
/// 设置，关闭时直接落到终态，不改变任何业务状态，也不阻拦操作。
/// </summary>
public static class Motion
{
    private sealed class State { public int Version; public bool Visible; }
    private static readonly ConditionalWeakTable<UIElement, State> States = new();

    /// <summary>仅回归测试使用：强制动画开/关，覆盖系统设置关闭动画的分支。</summary>
    private static bool? _forcedEnabled = null;

    /// <summary>系统是否允许界面动画（高对比度下自动关闭）。</summary>
    public static bool Enabled => _forcedEnabled ?? (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast);

    private static int Begin(UIElement element)
    {
        var state = States.GetOrCreateValue(element);
        return ++state.Version;
    }

    private static bool IsCurrent(UIElement element, int version)
        => States.TryGetValue(element, out var state) && state.Version == version;

    private static TranslateTransform EnsureShift(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform shift) return shift;
        shift = new TranslateTransform();
        element.RenderTransform = shift;
        return shift;
    }

    private static void ResetShift(FrameworkElement element)
    {
        if (element.RenderTransform is not TranslateTransform shift) return;
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.BeginAnimation(TranslateTransform.YProperty, null);
        shift.X = 0;
        shift.Y = 0;
    }

    /// <summary>直接落到终态，不播放动画（系统关闭动画、或需要同步显示时使用）。</summary>
    public static void Settle(FrameworkElement element)
    {
        Begin(element);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        ResetShift(element);
    }

    /// <summary>立即显示并取消尚未完成的淡出，防止旧回调稍后把元素重新折叠。</summary>
    public static void Show(FrameworkElement element)
    {
        Settle(element);
        element.Visibility = Visibility.Visible;
        States.GetOrCreateValue(element).Visible = true;
    }

    /// <summary>立即隐藏并取消尚未完成的动画。</summary>
    public static void Hide(FrameworkElement element)
    {
        Settle(element);
        element.Visibility = Visibility.Collapsed;
        States.GetOrCreateValue(element).Visible = false;
    }

    /// <summary>
    /// 淡入，并可从偏移处归位。默认从当前可见状态接续；
    /// <paramref name="restart"/> 用于「内容整体替换」的入场，总是从偏移与透明重新开始。
    /// </summary>
    public static void FadeIn(FrameworkElement element, double dx = 0, double dy = 0, int ms = 180,
        bool restart = false)
    {
        // 之前处于隐藏状态时一律从透明开始：调用方刚设过 Visibility 也不会丢掉入场。
        var state = States.GetOrCreateValue(element);
        bool enter = restart || !state.Visible;
        state.Visible = true;
        int version = ++state.Version; // 使尚未完成的上一次淡出回调失效
        double from = enter ? 0 : Math.Clamp(element.Opacity, 0, 1);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        element.Visibility = Visibility.Visible;

        if (!Enabled)
        {
            ResetShift(element);
            return;
        }

        var fade = new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        // 动画结束后移除时钟：基础值已经是 1/0，视觉不变，但不再让控件持有动画属性。
        fade.Completed += (_, _) =>
        {
            if (IsCurrent(element, version)) element.BeginAnimation(UIElement.OpacityProperty, null);
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);

        if (dx == 0 && dy == 0) return;
        var shift = EnsureShift(element);
        // 先读取当前可见值（可能来自上一次未完成的动画），再清空动画、把基准值归零，
        // 最后从该可见值补间到 0。FillBehavior.Stop 结束后回落到基准 0，不会弹回起点。
        double fromX = shift.X;
        double fromY = shift.Y;
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.BeginAnimation(TranslateTransform.YProperty, null);
        shift.X = 0;
        shift.Y = 0;
        if (dx != 0)
        {
            var slideX = new DoubleAnimation(enter ? dx : fromX, 0, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };
            slideX.Completed += (_, _) =>
            {
                if (IsCurrent(element, version)) shift.BeginAnimation(TranslateTransform.XProperty, null);
            };
            shift.BeginAnimation(TranslateTransform.XProperty, slideX);
        }
        if (dy == 0) return;
        var slideY = new DoubleAnimation(enter ? dy : fromY, 0, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        slideY.Completed += (_, _) =>
        {
            if (IsCurrent(element, version)) shift.BeginAnimation(TranslateTransform.YProperty, null);
        };
        shift.BeginAnimation(TranslateTransform.YProperty, slideY);
    }

    /// <summary>
    /// 淡出；可选择结束后隐藏。回调只在这是最后一次触发时执行，
    /// 因此快速「隐藏→显示」不会被旧动画把新内容重新折叠。
    /// </summary>
    public static void FadeOut(FrameworkElement element, int ms = 120,
        Action? completed = null, bool collapse = false)
    {
        int version = Begin(element);
        var state = States.GetOrCreateValue(element);
        if (element.Visibility != Visibility.Visible)
        {
            if (collapse) element.Visibility = Visibility.Collapsed;
            state.Visible = !collapse;
            completed?.Invoke();
            return;
        }

        double from = element.Opacity;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1; // 基础值保持可见，动画只负责当次呈现

        if (!Enabled || from <= 0.01)
        {
            if (collapse) element.Visibility = Visibility.Collapsed;
            element.Opacity = 1;
            state.Visible = !collapse;
            completed?.Invoke();
            return;
        }

        var fade = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd,
        };
        fade.Completed += (_, _) =>
        {
            if (!IsCurrent(element, version)) return;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            // 折叠后把基础值恢复到可见，下一次 FadeIn 不会从 0 之外的状态开始；
            // 若只是淡出但仍保留元素，则停在透明终态。
            element.Opacity = collapse ? 1 : 0;
            if (collapse) element.Visibility = Visibility.Collapsed;
            state.Visible = !collapse;
            completed?.Invoke();
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
