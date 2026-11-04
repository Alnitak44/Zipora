using System;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Zipora.Services;

namespace Zipora.Dialogs;

public enum NoticeKind { Info, Success, Warning, Error, Update }

public partial class NoticeDialog : FluentWindow
{
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
