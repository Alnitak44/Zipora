using System.Windows;
using Wpf.Ui.Controls;

using Zipora.Services;

namespace Zipora.Dialogs;

public partial class PasswordDialog : FluentWindow
{
    private sealed record SavedPasswordItem(string Value, int Number)
    {
        public override string ToString() => Value;
    }

    /// <summary>取主题语义刷（找不到时回退 fallback），避免硬编码颜色。</summary>
    private static System.Windows.Media.Brush ThemeBrush(string key, System.Windows.Media.Brush fallback)
    {
        return System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;
    }


    public string? Password { get; private set; }

    /// <summary>用户点击了「字典破解」按钮（对话框将关闭，由主窗口接管破解流程）。</summary>
    public bool CrackRequested { get; private set; }

    public PasswordDialog(string archiveName, string infoText)
    {
        InitializeComponent();
        ArchiveNameText.Text = archiveName;
        InfoText.Text = infoText;

        PopulateSavedPasswords();
        Loaded += (_, _) => PasswordBox.Focus();

        Backdrop.ApplyFromSetting(this);
    }

    private void PopulateSavedPasswords()
    {
        SavedPasswordList.Items.Clear();
        var passwords = PasswordStore.Load();
        for (var i = 0; i < passwords.Count; i++)
            SavedPasswordList.Items.Add(new SavedPasswordItem(passwords[i], i + 1));
        SavedPasswordButton.IsEnabled = SavedPasswordList.Items.Count > 0 || PasswordStore.NeedsUnlock;
        SavedPasswordButton.ToolTip = SavedPasswordButton.IsEnabled
            ? (PasswordStore.NeedsUnlock ? "解锁便携密码库" : "选择已保存的常用密码")
            : "请先到“设置 → 密码管理”添加常用密码";
    }

    private void ToggleSavedPasswords_Click(object sender, RoutedEventArgs e)
    {
        if (!PortableVaultDialog.EnsureUnlocked(this)) return;
        PopulateSavedPasswords();
        SavedPasswordPopup.IsOpen = !SavedPasswordPopup.IsOpen;
    }

    private void SavedPasswordList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SavedPasswordList.SelectedItem is SavedPasswordItem item)
        {
            PasswordBox.Password = item.Value;
            SavedPasswordPopup.IsOpen = false;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordBox.Password))
        {
            SetStatus("请输入密码。", error: true);
            return;
        }
        Password = PasswordBox.Password;
        DialogResult = true;
    }

    private void CrackButton_Click(object sender, RoutedEventArgs e)
    {
        CrackRequested = true;
        Close();
    }


    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");
        StatusSpinner.Visibility = Visibility.Visible;
        StatusPanel.Visibility = Visibility.Visible;
    }

    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.Foreground = error
            ? ThemeBrush("ZiporaWarningBrush", System.Windows.Media.Brushes.OrangeRed)
            : (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");
        StatusSpinner.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;
    }
}
