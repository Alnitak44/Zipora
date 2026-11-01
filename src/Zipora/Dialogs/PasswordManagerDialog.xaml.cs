using System.Windows;
using Wpf.Ui.Controls;
using Zipora.Services;

namespace Zipora.Dialogs;

public partial class PasswordManagerDialog : FluentWindow
{
    private sealed record PasswordEntry(string Value, int Number)
    {
    }

    public PasswordManagerDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!PortableVaultDialog.EnsureUnlocked(this)) { Close(); return; }
            ImportLocalButton.Visibility = LockVaultButton.Visibility = AppStorage.IsPortable ? Visibility.Visible : Visibility.Collapsed;
            RefreshList();
        };
        Backdrop.ApplyFromSetting(this);
    }

    private void RefreshList()
    {
        var passwords = PasswordStore.Load();
        PasswordList.Items.Clear();
        for (var i = 0; i < passwords.Count; i++) PasswordList.Items.Add(new PasswordEntry(passwords[i], i + 1));
        CountText.Text = $"已保存 {passwords.Count} / {PasswordStore.MaxPasswords} 个密码。" +
            (AppStorage.IsPortable ? "主密码加密，随 U 盘携带。" : "密码使用当前 Windows 用户的加密保护。");
        if (AppStorage.IsPortable && PasswordStore.PortableVault.RecoveredBackup)
            StatusText.Text = "主文件无法读取，已打开上次备份；最近一次修改可能未包含在备份中。";
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordStore.TryAdd(NewPasswordBox.Text, out var message))
        {
            NewPasswordBox.Text = "";
            RefreshList();
        }
        StatusText.Text = message;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordList.SelectedItem is not PasswordEntry entry)
        {
            StatusText.Text = "请选择要删除的密码。";
            return;
        }
        try { PasswordStore.Remove(entry.Value); }
        catch (Exception ex) { StatusText.Text = "未删除：" + ex.Message; return; }
        RefreshList();
        StatusText.Text = "已删除。";
    }

    private void LockVault_Click(object sender, RoutedEventArgs e) { PasswordStore.Lock(); Close(); }
    private void ImportLocal_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this, "将此 Windows 用户在普通版保存的密码复制到便携密码库？原密码库不会被修改。",
            "导入本机密码", System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes) return;
        try { int count = PasswordStore.ImportFromCurrentWindowsUser(); RefreshList(); StatusText.Text = $"已导入 {count} 个密码。"; }
        catch (Exception ex) { StatusText.Text = "未导入：" + ex.Message; }
    }
}
