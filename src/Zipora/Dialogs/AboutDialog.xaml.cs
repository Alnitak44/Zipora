using System.Diagnostics;
using System.IO;
using System.Windows;

using Zipora.Services;

namespace Zipora.Dialogs;

public partial class AboutDialog : Wpf.Ui.Controls.FluentWindow
{
    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = $"版本 {AppInfo.Version}";
        Backdrop.ApplyFromSetting(this);
    }

    private void License_Click(object sender, RoutedEventArgs e)
    {
        string licensePath = Path.Combine(AppContext.BaseDirectory, "7zip", "License.txt");
        if (!File.Exists(licensePath))
        {
            MessageBox.Show(this, "未找到随程序附带的 7-Zip 许可证文件。", "Zipora",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(licensePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法打开许可证文件：" + ex.Message, "Zipora",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
