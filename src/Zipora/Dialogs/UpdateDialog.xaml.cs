using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Zipora.Services;

namespace Zipora.Dialogs;

public partial class UpdateDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly UpdateInfo _info;

    public UpdateDialog(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        VersionText.Text = $"Zipora v{info.LatestVersion}";
        CurrentVersionText.Text = $"当前版本 v{AppInfo.Version} · 发现新的正式发行版";
        UpgradeHint.Text = AppStorage.IsPortable ? "Portable 升级请保留整个 Data 文件夹。" : "运行新版安装包覆盖升级，设置和密码库将保留。";
        NotesViewer.Document = ReleaseNotesMarkdown.Render(info.Notes);
        Backdrop.ApplyFromSetting(this);
        Loaded += (_, _) => FitToWorkArea(CurrentWorkArea());
    }

    private void FitToWorkArea(Rect workArea)
    {
        // A fixed window, with only the notes row allowed to scroll. Override
        // FluentWindow's defaults even on a small display or at high DPI.
        MaxWidth = Math.Max(1, workArea.Width - 32);
        MaxHeight = Math.Max(1, workArea.Height - 32);
        Width = Math.Min(620, MaxWidth); Height = Math.Min(620, MaxHeight);
        Left = Math.Clamp(Left, workArea.Left + 16, Math.Max(workArea.Left + 16, workArea.Right - Width - 16));
        Top = Math.Clamp(Top, workArea.Top + 16, Math.Max(workArea.Top + 16, workArea.Bottom - Height - 16));
    }

    private Rect CurrentWorkArea()
    {
        var source = PresentationSource.FromVisual(this);
        var monitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) && source?.CompositionTarget != null)
        {
            var transform = source.CompositionTarget.TransformFromDevice;
            return new Rect(transform.Transform(new Point(info.Work.Left, info.Work.Top)),
                transform.Transform(new Point(info.Work.Right, info.Work.Bottom)));
        }
        return SystemParameters.WorkArea;
    }

    private void Download_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void ReleasePage_Click(object sender, RoutedEventArgs e) => UpdateChecker.OpenDownloadPage(_info.ReleaseUrl);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
