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

    /// <summary>
    /// 云端查询回调（由主窗口注入）：参数为 (取消令牌, 状态上报)。
    /// 返回云端可用密码；返回 null 表示云端没有。
    /// </summary>
    public Func<CancellationToken, Action<string>, Task<string?>>? CloudPasswordFinder { get; set; }

    public string? Password { get; private set; }

    /// <summary>用户点击了「字典破解」按钮（对话框将关闭，由主窗口接管破解流程）。</summary>
    public bool CrackRequested { get; private set; }

    public PasswordDialog(string archiveName, string infoText, bool cloudConfigured)
    {
        InitializeComponent();
        ArchiveNameText.Text = archiveName;
        InfoText.Text = infoText;

        CloudButton.Visibility = cloudConfigured ? Visibility.Visible : Visibility.Collapsed;
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

    private async void CloudButton_Click(object sender, RoutedEventArgs e)
    {
        if (CloudPasswordFinder == null) return;

        CloudButton.IsEnabled = false;
        ShowStatus("正在查询云端…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            string? found = await CloudPasswordFinder(cts.Token, s => ShowStatus(s));
            if (found != null)
            {
                Password = found;
                DialogResult = true;
            }
            else
            {
                SetStatus("云端没有这个文件的可用密码，请手动输入。", error: false);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("云端查询已取消/超时。", error: true);
        }
        catch (Exception ex)
        {
            SetStatus("云端查询失败：" + ex.Message, error: true);
        }
        finally
        {
            CloudButton.IsEnabled = true;
        }
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
