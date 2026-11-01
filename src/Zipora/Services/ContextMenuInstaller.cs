using System.IO;
using Microsoft.Win32;

namespace Zipora.Services;

/// <summary>
/// 注册 Zipora 原生 Explorer Command Handler。COM DLL 独立于 WPF：Explorer 只加载
/// Zipora.ShellExtension.dll，点击命令后才通过命令行启动 Zipora.exe。
/// 一个 AllFilesystemObjects\shell\Zipora 关联覆盖文件和文件夹，子菜单由
/// IExplorerCommand::EnumSubCommands 提供，因而不会注册多个顶级 verb。
/// </summary>
public static class ContextMenuInstaller
{
    internal const string ExplorerCommandClsid = "{B53761D2-18A8-4C23-B6A6-948C2FBE8F4A}";
    private const string CommandKeyPath = @"AllFilesystemObjects\shell\Zipora";
    private const string ComKeyPath = @"CLSID\{B53761D2-18A8-4C23-B6A6-948C2FBE8F4A}";

    private static readonly string[] ArchiveExtensions =
    {
        ".7z", ".zip", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst",
        ".lz", ".lzma", ".iso", ".cab", ".wim", ".zipx", ".jar", ".apk",
    };

    private static string ExtensionDllPath => Path.Combine(AppContext.BaseDirectory, "Zipora.ShellExtension.dll");
    private static string IconPath => Path.Combine(AppContext.BaseDirectory, "Zipora.ico");
    private static string IconLocation => $"\"{IconPath}\",0";

    public static void Install()
    {
        if (AppStorage.IsPortable) throw new InvalidOperationException("便携版不注册系统右键菜单，请使用拖入窗口或“打开”。");
        if (!File.Exists(ExtensionDllPath))
            throw new FileNotFoundException("未找到 Zipora Explorer Shell Extension。请先将 Zipora.ShellExtension.dll 部署到 Zipora.exe 同目录。", ExtensionDllPath);

        // Explorer is 64-bit on supported x64 Windows, so the registering WPF
        // executable and the extension DLL must both be x64.
        using (RegistryKey? inproc = Registry.ClassesRoot.CreateSubKey($@"{ComKeyPath}\InprocServer32"))
        {
            inproc?.SetValue("", ExtensionDllPath);
            inproc?.SetValue("ThreadingModel", "Apartment");
        }
        using (RegistryKey? command = Registry.ClassesRoot.CreateSubKey(CommandKeyPath))
        {
            command?.SetValue("MUIVerb", "Zipora");
            // 顶级项目的图标不能只依赖 ExplorerCommandHandler；显式注册 Icon，
            // 同时为旧版 Explorer 和 Windows 11 右键菜单提供稳定的图标位置。
            command?.SetValue("Icon", IconLocation);
            command?.SetValue("ExplorerCommandHandler", ExplorerCommandClsid);
        }

        // Old registrations would otherwise leave extra top-level entries.
        RemoveLegacyVerbs();
        NativeMethods.RefreshShell();
    }

    public static void Uninstall()
    {
        if (AppStorage.IsPortable) throw new InvalidOperationException("便携版不修改本机注册表，请使用普通版管理系统右键菜单。");
        Registry.ClassesRoot.DeleteSubKeyTree(CommandKeyPath, throwOnMissingSubKey: false);
        Registry.ClassesRoot.DeleteSubKeyTree(ComKeyPath, throwOnMissingSubKey: false);
        RemoveLegacyVerbs();
        NativeMethods.RefreshShell();
    }

    private static void RemoveLegacyVerbs()
    {
        foreach (string ext in ArchiveExtensions)
        {
            foreach (string name in new[]
            {
                "ZiporaOpen", "ZiporaExtractHere", "Zipora", "ZiporaExtractDesktop", "ZiporaExtractPick",
                "KeyUnpackOpen", "KeyUnpackExtractHere", "KeyUnpack", "KeyUnpackExtractDesktop", "KeyUnpackExtractPick",
            })
                Registry.ClassesRoot.DeleteSubKeyTree($@"{ext}\shell\{name}", throwOnMissingSubKey: false);
        }
        Registry.ClassesRoot.DeleteSubKeyTree(@"*\shell\ZiporaCreate", throwOnMissingSubKey: false);
        Registry.ClassesRoot.DeleteSubKeyTree(@"*\shell\KeyUnpackCreate", throwOnMissingSubKey: false);
        foreach (string name in new[]
        {
            "Zipora.Open", "Zipora.ExtractHere", "Zipora.ExtractDesktop", "Zipora.ExtractPick",
            "KeyUnpack.Open", "KeyUnpack.ExtractHere", "KeyUnpack.ExtractDesktop", "KeyUnpack.ExtractPick",
        })
            Registry.ClassesRoot.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
    }

    /// <summary>仅当命令关联、COM 注册和实际 DLL 都存在时才视为已安装。</summary>
    public static bool IsInstalled()
    {
        using RegistryKey? command = Registry.ClassesRoot.OpenSubKey(CommandKeyPath);
        using RegistryKey? inproc = Registry.ClassesRoot.OpenSubKey($@"{ComKeyPath}\InprocServer32");
        return command?.GetValue("ExplorerCommandHandler") is string clsid
            && clsid.Equals(ExplorerCommandClsid, StringComparison.OrdinalIgnoreCase)
            && inproc?.GetValue("") is string dll
            && File.Exists(dll);
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);

    public static void RefreshShell() => SHChangeNotify(0x8000000 /*SHCNE_ASSOCCHANGED*/, 0, 0, 0);
}
