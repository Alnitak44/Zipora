using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

using Zipora.Services;

namespace Zipora.Dialogs;

public partial class CreditsDialog : Wpf.Ui.Controls.FluentWindow
{
    public CreditsDialog()
    {
        InitializeComponent();
        Backdrop.ApplyFromSetting(this);
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* 打不开浏览器不阻断 */ }
        e.Handled = true;
    }
}