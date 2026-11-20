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
            NoticeDialog.Show("未找到许可证", "未找到随程序附带的 7-Zip 许可证文件。",
                NoticeKind.Info, this);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(licensePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            NoticeDialog.Show("无法打开许可证文件", ex.Message, NoticeKind.Error, this);
        }
    }
}
