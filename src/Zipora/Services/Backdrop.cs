using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Zipora.Services;

/// <summary>
/// 窗口背景效果：Mica（云母）/ Acrylic（亚克力毛玻璃）/ Solid（纯色）。
/// Win11 22H2+ 通过 DWMWA_SYSTEMBACKDROP_TYPE（DWM API）实现；
/// 更旧的系统回退到 SetWindowCompositionAttribute 亚克力模糊。
/// 选择持久化在 AuthStore.WindowBackdrop，对所有窗口统一生效、可随时切换。
/// </summary>
public static class Backdrop
{
    public const string Mica = "Mica";
    public const string Acrylic = "Acrylic";
    public const string Solid = "Solid";

    /// <summary>当前生效的模式（启动时从配置读取，切换时更新）。</summary>
    public static string Current { get; private set; } = Mica;

    /// <summary>把当前保存模式转换为 WPF-UI 所需的窗口背景类型。</summary>
    public static Wpf.Ui.Controls.WindowBackdropType ToWpfUiBackdropType() => Current switch
    {
        Acrylic => Wpf.Ui.Controls.WindowBackdropType.Acrylic,
        Solid => Wpf.Ui.Controls.WindowBackdropType.None,
        _ => Wpf.Ui.Controls.WindowBackdropType.Mica,
    };

    // ---------- Win32 互操作 ----------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private const int DwmwaSystemBackdropType = 38; // Win11 22H2+
    private const int DwmsbtNone = 1;               // 纯色（系统默认）
    private const int DwmsbtMica = 2;               // 主窗口 Mica
    private const int DwmsbtAcrylic = 3;            // 弹层 Acrylic（毛玻璃）

    private const int WcaAccentPolicy = 19;         // Win10 亚克力回退
    private const int AccentEnableAcrylic = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor; // 毛玻璃底色（含透明度）
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    /// <summary>启动时初始化当前模式（不触发应用）。</summary>
    public static void Init(string? saved)
    {
        Current = saved switch
        {
            Mica or Acrylic or Solid => saved,
            _ => Mica,
        };
    }

    /// <summary>切换模式：立即应用到当前所有已打开的 Zipora 窗口。</summary>
    public static void Set(string mode)
    {
        Init(mode);

        // WPF-UI 负责窗口客户区和 chrome 的原生渲染；同步更新它的全局背景
        // 类型，确保切换当下与之后新开的窗口都使用同一个效果。
        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                ThemeManager.Current == ThemeManager.Dark
                    ? Wpf.Ui.Appearance.ApplicationTheme.Dark
                    : Wpf.Ui.Appearance.ApplicationTheme.Light,
                ToWpfUiBackdropType());
        }
        catch
        {
            // DWM 应用仍可作为 WPF-UI 更新失败时的回退。
        }

        RefreshAll();
    }

    /// <summary>主题色板变化后重新同步所有已打开窗口的 DWM 背景。</summary>
    public static void RefreshAll()
    {
        foreach (Window w in Application.Current.Windows)
            ApplyAfterWpfUi(w);
    }

    /// <summary>把窗口挂到当前模式。
    /// 在 SourceInitialized、Loaded 和首帧渲染后各应用一次，确保覆盖 WPF-UI
    /// 可能在窗口初始化期间补写的默认 Mica。</summary>
    public static void ApplyFromSetting(Window window)
    {
        SurfacePalette.Apply(window, window is MainWindow main && main.HasCustomBackground);
        // 必须在 FluentWindow 创建原生 chrome 之前设置；显示后再改这个属性会
        // 触发 WPF-UI 的 Freezable 继承上下文异常。运行时切换则由 DWM 兜底。
        if (!window.IsLoaded && window is Wpf.Ui.Controls.FluentWindow fluentWindow)
            fluentWindow.WindowBackdropType = ToWpfUiBackdropType();

        window.SourceInitialized += (_, _) => ApplyCore(window);
        window.Loaded += (_, _) => ApplyAfterWpfUi(window);
        window.ContentRendered += (_, _) => ApplyAfterWpfUi(window);
        if (window.IsLoaded) ApplyCore(window);
    }

    private static void ApplyAfterWpfUi(Window window)
    {
        ApplyCore(window);
        // ApplicationThemeManager 可能在当前消息循环稍后重建 chrome；最后再覆盖一次，
        // 避免 Acrylic 被异步恢复成默认 Mica。
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => ApplyCore(window)));
    }

    private static void ApplyCore(Window window)
    {
        SurfacePalette.Apply(window, window is MainWindow main && main.HasCustomBackground);
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (SystemParameters.HighContrast)
        {
            TryDwm(hwnd, DwmsbtNone);
            window.Background = SystemColors.WindowBrush;
            return;
        }

        // 这里不再改 FluentWindow.WindowBackdropType；它只在窗口显示前设置一次。
        // 显示后由 DWM 属性 + Background 接管，避免 WPF-UI 重建 chrome 时崩溃。

        switch (Current)
        {
            case Mica:
                // Mica 保持较高不透明度，避免深色模式被系统白色底冲成灰色。
                if (TryDwm(hwnd, DwmsbtMica))
                    window.Background = CreateBackdropBrush(0xF0);
                else
                    window.SetResourceReference(Window.BackgroundProperty, "ZiporaWindowBackground");
                break;

            case Acrylic:
                // Preserve translucency for DWM; a thin tint stabilizes contrast.
                // Dialogs use a stronger veil so the owner's text doesn't compete.
                if (TryDwm(hwnd, DwmsbtAcrylic))
                {
                    window.Background = CreateBackdropBrush(window is MainWindow ? (byte)0x28 : (byte)0x80);
                }
                else if (ApplyLegacyAcrylic(hwnd))
                {
                    window.Background = CreateBackdropBrush(window is MainWindow ? (byte)0x28 : (byte)0x80);
                }
                else
                {
                    window.SetResourceReference(Window.BackgroundProperty, "ZiporaWindowBackground");
                }
                break;

            default: // Solid：关掉系统背景，恢复主题纯色
                TryDwm(hwnd, DwmsbtNone);
                window.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
                break;
        }
    }

    private static SolidColorBrush CreateBackdropBrush(byte alpha)
    {
        bool dark = ThemeManager.Current == ThemeManager.Dark;
        Color color = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private static bool TryDwm(IntPtr hwnd, int backdropType)
    {
        try
        {
            int v = backdropType;
            return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref v, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Win10 式亚克力模糊（AccentPolicy），仅在不支持 DWMWA_SYSTEMBACKDROP_TYPE 时使用。</summary>
    private static bool ApplyLegacyAcrylic(IntPtr hwnd)
    {
        try
        {
            bool dark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()
                == Wpf.Ui.Appearance.ApplicationTheme.Dark;
            var accent = new AccentPolicy
            {
                AccentState = AccentEnableAcrylic,
                GradientColor = dark ? 0xCC202020u : 0xCCF3F3F3u,
            };
            int size = Marshal.SizeOf(accent);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = Marshal.AllocHGlobal(size),
                SizeOfData = size,
            };
            try
            {
                Marshal.StructureToPtr(accent, data.Data, false);
                return SetWindowCompositionAttribute(hwnd, ref data) == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(data.Data);
            }
        }
        catch
        {
            // 旧系统也不支持时保持纯色，不影响功能
            return false;
        }
    }
}
