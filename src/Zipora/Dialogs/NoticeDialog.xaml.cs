using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Zipora.Services;

namespace Zipora.Dialogs;

public enum NoticeKind { Info, Success, Warning, Error, Update }

public partial class NoticeDialog : FluentWindow
{
    /// <summary>
    /// 全应用统一的提示框入口。所有曾经用 MessageBox 的地方都应走这里，
    /// 保证深色主题、背景效果、圆角与按钮样式一致。
    /// </summary>
    public static bool Show(string title, string message, NoticeKind kind, Window? owner = null)
        => Present(title, message, kind, "确定", null, false, owner);

    /// <summary>两按钮确认框；返回 true 表示点击了主按钮。</summary>
    public static bool Confirm(string title, string message, NoticeKind kind,
        string primaryText, string secondaryText, bool primaryIsDanger = false, Window? owner = null)
        => Present(title, message, kind, primaryText, secondaryText, primaryIsDanger, owner);

    private static bool Present(string title, string message, NoticeKind kind,
        string primaryText, string? secondaryText, bool primaryIsDanger, Window? owner)
    {
        try
        {
            var app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
                return app.Dispatcher.Invoke(() => Present(title, message, kind, primaryText, secondaryText, primaryIsDanger, owner));
            var dialog = new NoticeDialog(title, message, kind, primaryText, secondaryText, primaryIsDanger);
            var host = ResolveOwner(owner);
            if (host != null) dialog.Owner = host;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return dialog.ShowDialog() == true;
        }
        catch (Exception ex)
        {
            // 出错路径本身不能再抛异常（否则会陷入异常循环）。
            try { AppStorage.Log("NoticeDialog failed: " + ex); } catch { }
            var buttons = secondaryText == null ? System.Windows.MessageBoxButton.OK : System.Windows.MessageBoxButton.YesNo;
            var image = kind switch
            {
                NoticeKind.Error => System.Windows.MessageBoxImage.Error,
                NoticeKind.Warning => System.Windows.MessageBoxImage.Warning,
                NoticeKind.Success => System.Windows.MessageBoxImage.Information,
                _ => System.Windows.MessageBoxImage.Information,
            };
            return System.Windows.MessageBox.Show(message, title, buttons, image) != System.Windows.MessageBoxResult.No;
        }
    }

    private static Window? ResolveOwner(Window? explicitOwner)
    {
        if (explicitOwner is { IsVisible: true }) return explicitOwner;
        var app = Application.Current;
        if (app == null) return null;
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
            ?? (app.MainWindow is { IsVisible: true } main ? main : null);
    }

    public NoticeDialog(string title, string message, NoticeKind kind,
        string primaryText = "确定", string? secondaryText = null,
        bool primaryIsDanger = false)
    {
        InitializeComponent();
        MessageScroller.MaxHeight = Math.Min(360, Math.Max(80, SystemParameters.WorkArea.Height * 0.45));
        Title = title;
        DialogTitle.Title = title;
        HeadlineText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;
        PrimaryButton.IsDefault = !primaryIsDanger;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            DialogResult = false;
            e.Handled = true;
        };
        if (primaryIsDanger)
        {
            PrimaryButton.Appearance = ControlAppearance.Danger;
            PrimaryButton.IsDefault = false;
        }
        if (!string.IsNullOrWhiteSpace(secondaryText))
        {
            SecondaryButton.Content = secondaryText;
            SecondaryButton.Visibility = Visibility.Visible;
            if (primaryIsDanger) SecondaryButton.IsDefault = true;
        }

        NoticeIcon.Symbol = kind switch
        {
            NoticeKind.Success => SymbolRegular.CheckmarkCircle20,
            NoticeKind.Warning => SymbolRegular.Warning20,
            NoticeKind.Error => SymbolRegular.DismissCircle20,
            NoticeKind.Update => SymbolRegular.ArrowSync20,
            _ => SymbolRegular.Info20,
        };
        NoticeIcon.Foreground = kind switch
        {
            NoticeKind.Success => Brushes.SeaGreen,
            NoticeKind.Warning => Brushes.DarkOrange,
            NoticeKind.Error => Brushes.IndianRed,
            NoticeKind.Update => Brushes.DodgerBlue,
            _ => Brushes.DodgerBlue,
        };
        Backdrop.ApplyFromSetting(this);
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Secondary_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
