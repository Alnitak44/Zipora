using System.IO;
using System.Windows;

namespace Zipora.Services;

/// <summary>
/// 主题管理：Light / Dark 两套自建语义色板（Themes/Light.xaml、Themes/Dark.xaml）。
/// 切换时整表替换 App 资源里的主题字典（DynamicResource 自动刷新全部引用），
/// 同时同步 WPF-UI 的控件主题，并把用户选择持久化到 config.json。
/// </summary>
public static class ThemeManager
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    /// <summary>当前主题。</summary>
    public static string Current { get; private set; } = Light;

    /// <summary>当前挂载在 App 资源里的主题字典（切换时替换它）。</summary>
    private static ResourceDictionary? _activeTheme;

    /// <summary>启动时按已保存选择应用主题（不重复写配置）。</summary>
    public static void Init(string? saved)
    {
        Apply(saved == Dark ? Dark : Light, persist: false);
    }

    /// <summary>运行时切换主题：立即生效并保存用户选择。</summary>
    public static void Set(string theme)
    {
        Apply(theme == Dark ? Dark : Light, persist: true);
    }

    private static void Apply(string theme, bool persist)
    {
        Current = theme;
        var app = Application.Current;
        if (app == null) return;

        // 1) 替换自建语义色板（所有 DynamicResource 引用自动刷新）
        try
        {
            var dict = new ResourceDictionary
            {
                // 同程序集资源使用相对 URI；省略程序集名的绝对 pack URI 会在部分
                // 启动/发布路径下解析失败，导致切换主题后资源字典为空。
                Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative),
            };
            if (_activeTheme != null)
                app.Resources.MergedDictionaries.Remove(_activeTheme);
            app.Resources.MergedDictionaries.Insert(0, dict);
            _activeTheme = dict;
        }
        catch (Exception ex)
        {
            LogThemeError("主题字典加载失败", ex);
        }

        // 2) 同步 WPF-UI 控件主题（按钮/输入框/窗口 chrome 等），失败不影响自建色板
        try
        {
            // 颜色主题与窗口背景效果分别管理，但 WPF-UI 每次换色都会刷新窗口
            // chrome；因此必须把当前保存的背景模式一起传入，不能落回默认 Mica。
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                theme == Dark
                    ? Wpf.Ui.Appearance.ApplicationTheme.Dark
                    : Wpf.Ui.Appearance.ApplicationTheme.Light,
                Backdrop.ToWpfUiBackdropType());
        }
        catch { }

        // 换色会让 WPF-UI 重建窗口 chrome；重新写入当前 DWM 背景，避免深色窗口
        // 切到浅色后留下黑色标题栏，或把 Acrylic 恢复成默认 Mica。
        Backdrop.RefreshAll();

        // 3) 持久化用户选择
        if (persist)
        {
            try
            {
                var store = AuthStore.Load();
                store.Theme = theme;
                store.Save();
            }
            catch (Exception ex) { AppStorage.ReportSaveFailure(ex); }
        }
    }

    private static void LogThemeError(string stage, Exception ex)
    {
        try
        {
            AppStorage.Log($"{stage}: {ex}");
        }
        catch { }
    }
}
