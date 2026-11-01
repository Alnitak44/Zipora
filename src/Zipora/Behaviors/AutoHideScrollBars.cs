using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Zipora.Behaviors;

/// <summary>
/// Scrollbar visuals stay out of the way until the user scrolls or approaches
/// the edge. The actual ScrollBar remains hit-testable, so its thumb can still
/// be grabbed for fast navigation.
/// </summary>
public static class AutoHideScrollBars
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(AutoHideScrollBars),
        new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static readonly Dictionary<FrameworkElement, State> States = new();

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement host) return;
        if ((bool)e.NewValue)
        {
            if (!States.ContainsKey(host)) States.Add(host, new State(host));
        }
        else if (States.Remove(host, out var state))
        {
            state.Dispose();
        }
    }

    private sealed class State : IDisposable
    {
        private readonly FrameworkElement _host;
        private readonly DispatcherTimer _hideTimer;
        private readonly List<ScrollBar> _bars = new();
        private bool _dragging;

        public State(FrameworkElement host)
        {
            _host = host;
            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1150) };
            _hideTimer.Tick += (_, _) => Hide();
            _host.Loaded += Host_Loaded;
            _host.Unloaded += Host_Unloaded;
            _host.PreviewMouseWheel += Host_PreviewMouseWheel;
            _host.PreviewMouseMove += Host_PreviewMouseMove;
            if (_host.IsLoaded) AttachBarsSoon();
        }

        private void Host_Loaded(object? sender, RoutedEventArgs e) => AttachBarsSoon();
        private void Host_Unloaded(object? sender, RoutedEventArgs e) => _hideTimer.Stop();

        private void AttachBarsSoon()
        {
            _host.Dispatcher.BeginInvoke(() =>
            {
                if (!_host.IsLoaded) return;
                foreach (var bar in FindVisualChildren<ScrollBar>(_host))
                {
                    if (_bars.Contains(bar)) continue;
                    _bars.Add(bar);
                    bar.Opacity = 0;
                    bar.MouseEnter += Bar_MouseEnter;
                    bar.MouseLeave += Bar_MouseLeave;
                    bar.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(Thumb_DragStarted));
                    bar.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Thumb_DragCompleted));
                }
            }, DispatcherPriority.Loaded);
        }

        private void Host_PreviewMouseWheel(object sender, MouseWheelEventArgs e) => Reveal();

        private void Host_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            var point = e.GetPosition(_host);
            // A wider approach area makes an invisible scrollbar discoverable.
            if (point.X >= _host.ActualWidth - 28 || point.Y >= _host.ActualHeight - 18)
                Reveal();
            else if (!_dragging)
                ScheduleHide();
        }

        private void Bar_MouseEnter(object sender, MouseEventArgs e) => Reveal();
        private void Bar_MouseLeave(object sender, MouseEventArgs e) { if (!_dragging) ScheduleHide(); }
        private void Thumb_DragStarted(object sender, DragStartedEventArgs e) { _dragging = true; Reveal(); }
        private void Thumb_DragCompleted(object sender, DragCompletedEventArgs e) { _dragging = false; ScheduleHide(); }

        private void Reveal()
        {
            foreach (var bar in _bars)
                if (bar.IsVisible) bar.Opacity = 1;
            _hideTimer.Stop();
            if (!_dragging) _hideTimer.Start();
        }

        private void ScheduleHide()
        {
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void Hide()
        {
            _hideTimer.Stop();
            if (_dragging) return;
            foreach (var bar in _bars)
                if (!bar.IsMouseOver) bar.Opacity = 0;
        }

        public void Dispose()
        {
            _hideTimer.Stop();
            _host.Loaded -= Host_Loaded;
            _host.Unloaded -= Host_Unloaded;
            _host.PreviewMouseWheel -= Host_PreviewMouseWheel;
            _host.PreviewMouseMove -= Host_PreviewMouseMove;
            foreach (var bar in _bars) bar.Opacity = 1;
            _bars.Clear();
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
}
