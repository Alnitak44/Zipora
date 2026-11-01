using System.Windows;
using Zipora.Services;

namespace Zipora.Dialogs;

public partial class PortableVaultDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly bool _creating;
    public PortableVaultDialog()
    {
        InitializeComponent();
        _creating = !PasswordStore.PortableVault.Exists;
        DescriptionText.Text = _creating
            ? "设置至少 8 个字符的主密码，保护随 U 盘携带的常用密码。换电脑后使用同一个主密码解锁。"
            : "输入主密码解锁 U 盘上的密码库。不依赖当前电脑或 Windows 用户。";
        ConfirmPanel.Visibility = _creating ? Visibility.Visible : Visibility.Collapsed;
        UnlockButton.Content = _creating ? "创建密码库" : "解锁";
        Loaded += (_, _) => MasterBox.Focus();
        Closed += (_, _) => { MasterBox.Password = ""; ConfirmBox.Password = ""; };
        Backdrop.ApplyFromSetting(this);
    }
    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_creating)
            {
                if (MasterBox.Password != ConfirmBox.Password) { StatusText.Text = "两次输入的主密码不一致。"; return; }
                PasswordStore.PortableVault.Create(MasterBox.Password);
            }
            else PasswordStore.PortableVault.Unlock(MasterBox.Password);
            DialogResult = true;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    public static bool EnsureUnlocked(Window owner)
    {
        if (!AppStorage.IsPortable || !PasswordStore.PortableVault.IsLocked) return true;
        return new PortableVaultDialog { Owner = owner }.ShowDialog() == true;
    }
}
