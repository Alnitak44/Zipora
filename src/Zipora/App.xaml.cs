using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Zipora.Services;

namespace Zipora;

public partial class App : Application
{
    /// <summary>命令行解压目标（右键子菜单触发），null 表示正常启动。</summary>
    internal static string? CliExtractTarget;

    /// <summary>右键「新建压缩包」传入的待压缩文件列表（可为空）。</summary>
    internal static System.Collections.Generic.List<string> CliCreateFiles = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppStorage.SaveFailed += message => MessageBox.Show(message, "便携版设置未保存", MessageBoxButton.OK, MessageBoxImage.Warning);

        try { AppStorage.Initialize(); }
        catch (Exception ex)
        {
            MessageBox.Show("无法启动便携版。请检查 U 盘是否连接、可写、空间充足，且同一目录未运行另一个实例。\n\n" + ex.Message,
                "Zipora Portable", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        // 启动时必须先恢复用户保存的主题和背景效果。
        // 过去这里固定传 Mica，导致已保存的 Acrylic/Solid 会在窗口创建前被全局默认值覆盖。
        try
        {
            var store = AuthStore.Load();
            Backdrop.Init(store.WindowBackdrop);
            ThemeManager.Init(store.Theme);

            // WPF-UI 需要拿到正确的初始背景类型，否则 FluentWindow 的客户区会
            // 退化成纯白/纯黑。它与 Backdrop 使用同一份已保存设置，而不是默认 Mica。
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                ThemeManager.Current == Services.ThemeManager.Dark
                    ? Wpf.Ui.Appearance.ApplicationTheme.Dark
                    : Wpf.Ui.Appearance.ApplicationTheme.Light,
                Backdrop.ToWpfUiBackdropType(),
                true);
        }
        catch { }

        var args = e.Args;

        // 右键菜单安装/卸载：由主程序「设置」页以管理员权限提权调用，执行后退出
        if (args.Any(a => a.Equals("--install-contextmenu", System.StringComparison.OrdinalIgnoreCase)))
        {
            RunContextMenuOp(install: true);
            return;
        }
        if (args.Any(a => a.Equals("--uninstall-contextmenu", System.StringComparison.OrdinalIgnoreCase)))
        {
            RunContextMenuOp(install: false);
            return;
        }

        // 右键子菜单解压目标（--extract-here / --extract-desktop / --extract-pick）
        string? target = null;
        if (args.Any(a => a.Equals("--extract-here", System.StringComparison.OrdinalIgnoreCase)))
            target = CliTargetKind.Here;
        else if (args.Any(a => a.Equals("--extract-desktop", System.StringComparison.OrdinalIgnoreCase)))
            target = CliTargetKind.Desktop;
        else if (args.Any(a => a.Equals("--extract-pick", System.StringComparison.OrdinalIgnoreCase)))
            target = CliTargetKind.Pick;
        CliExtractTarget = target;

        // 右键「新建压缩包」：--create 后的参数均为选中的文件/文件夹
        int createIdx = -1;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--create", System.StringComparison.OrdinalIgnoreCase)) { createIdx = i; break; }
        }
        if (createIdx >= 0)
        {
            for (int i = createIdx + 1; i < args.Length; i++)
            {
                if (args[i].Length > 0) CliCreateFiles.Add(args[i]);
            }
        }

        // 支持命令行参数直接打开压缩包：Zipora.exe <archive>（右键子菜单同样携带该参数）
        // 注意：--create 模式下文件列表是待压缩内容，不当作压缩包打开
        string? archive = createIdx < 0 ? args.FirstOrDefault(a => File.Exists(a)) : null;
        InstallationLifetime.Register();
        var window = new MainWindow(archive);
        if (AppStorage.IsPortable) window.Title = "Zipora Portable";
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppStorage.Dispose();
        base.OnExit(e);
    }

    /// <summary>命令行解压目标的种类标记。</summary>
    internal static class CliTargetKind
    {
        public const string Here = "here";
        public const string Desktop = "desktop";
        public const string Pick = "pick";
    }

    private void RunContextMenuOp(bool install)
    {
        try
        {
            if (install) ContextMenuInstaller.Install();
            else ContextMenuInstaller.Uninstall();
            MessageBox.Show(install
                    ? "已启用 Zipora Explorer 菜单：右键文件或文件夹 →「Zipora」子菜单。"
                    : "已从系统右键菜单移除。",
                "Zipora", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("操作失败：" + ex.Message, "Zipora",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (AppStorage.IsPortable && e.Exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("操作未完成，数据可能未保存。请检查 U 盘连接、写保护和剩余空间；不要继续拔插正在使用的 U 盘。\n\n" + e.Exception.Message,
                "便携版存储不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
            return;
        }
        // 不要吞掉未处理异常：此前将 e.Handled 设为 true 会让关闭窗口失败后
        // 留在一个已损坏的 Dispatcher 中，用户只能不断看到错误弹窗。
        // 先记录完整堆栈，随后让 WPF 按默认行为退出进程。
        try
        {
            AppStorage.Log("Unhandled dispatcher exception: " + e.Exception);
        }
        catch
        {
            // 记录失败不应掩盖原始异常。
        }

        MessageBox.Show("发生未处理的错误，Zipora 将退出。\n\n" + e.Exception.Message,
            "Zipora", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
