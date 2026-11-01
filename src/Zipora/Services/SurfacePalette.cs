using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace Zipora.Services;

/// <summary>One native backdrop, with translucent tint layers rather than nested opaque cards.</summary>
public static class SurfacePalette
{
    private sealed class State { public (bool Dark, bool Glass, bool Image, bool Contrast)? Value; }
    private static readonly ConditionalWeakTable<Window, State> States = new();

    public static void Apply(Window window, bool imageBackground = false)
    {
        bool dark = ThemeManager.Current == ThemeManager.Dark;
        bool contrast = SystemParameters.HighContrast;
        bool glass = !contrast && (Backdrop.Current == Backdrop.Acrylic || imageBackground);
        bool dialog = window is not MainWindow;
        var value = (dark, glass, imageBackground, contrast);
        var state = States.GetOrCreateValue(window);
        if (state.Value == value) return;
        state.Value = value;

        void Set(string key, string light, string night)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : light));
            brush.Freeze();
            window.Resources[key] = brush;
        }

        if (contrast)
        {
            foreach (string key in new[] { "ZiporaCardBackground", "ZiporaContentBackground",
                "ZiporaToolbarBackground", "ZiporaSidebarBackground", "ZiporaBreadcrumbBackground",
                "ZiporaOverlayBackground", "ZiporaFlyoutBackground", "ZiporaEmptyBadgeBackground" })
                window.Resources[key] = SystemColors.WindowBrush;
            window.Resources["ZiporaCardBorder"] = SystemColors.WindowTextBrush;
            window.Resources["ZiporaDividerBrush"] = SystemColors.WindowTextBrush;
            window.Resources["ZiporaTextPrimary"] = SystemColors.WindowTextBrush;
            window.Resources["ZiporaTextSecondary"] = SystemColors.WindowTextBrush;
            window.Resources["ZiporaTextTertiary"] = SystemColors.WindowTextBrush;
            return;
        }
        // Remove high-contrast text overrides so DynamicResource resolves the theme again.
        foreach (string key in new[] { "ZiporaTextPrimary", "ZiporaTextSecondary", "ZiporaTextTertiary" })
            window.Resources.Remove(key);

        if (glass)
        {
            // A photo needs stronger tinting than the already blurred system material.
            Set("ZiporaCardBackground", imageBackground ? "#CBFAFBFD" : dialog ? "#B8FFFFFF" : "#64FFFFFF",
                imageBackground ? "#D0212630" : dialog ? "#B8262D38" : "#602B303A");
            Set("ZiporaContentBackground", imageBackground ? "#C0FAFBFD" : "#50FFFFFF",
                imageBackground ? "#CC1D222B" : "#38202630");
            Set("ZiporaToolbarBackground", imageBackground ? "#D4FFFFFF" : "#70FFFFFF",
                imageBackground ? "#D42B303A" : "#642C323D");
            Set("ZiporaSidebarBackground", "#14FFFFFF", "#180C1018");
            Set("ZiporaBreadcrumbBackground", "#16FFFFFF", "#142A303A");
            Set("ZiporaOverlayBackground", imageBackground ? "#C4FFFFFF" : "#3DFFFFFF",
                imageBackground ? "#CC252B35" : "#30242A34");
            Set("ZiporaCardBorder", "#88FFFFFF", "#22FFFFFF");
            Set("ZiporaDividerBrush", "#160C2138", "#18FFFFFF");
            Set("ZiporaEmptyBadgeBackground", "#66FFFFFF", "#502F3947");
        }
        else
        {
            Set("ZiporaCardBackground", "#FAFBFD", "#292D34");
            Set("ZiporaContentBackground", "#FAFBFD", "#242830");
            Set("ZiporaToolbarBackground", "#F9FAFC", "#292E37");
            Set("ZiporaSidebarBackground", "#F2F4F7", "#20242C");
            Set("ZiporaBreadcrumbBackground", "#F7F9FC", "#272C34");
            Set("ZiporaOverlayBackground", "#F2F4F7", "#242931");
            Set("ZiporaCardBorder", "#E0E5EC", "#3C434E");
            Set("ZiporaDividerBrush", "#E2E7EE", "#363D48");
            Set("ZiporaEmptyBadgeBackground", "#EDF3FC", "#303B4B");
        }
        // Popup windows don't have the parent's DWM blur, so keep their surfaces legible.
        Set("ZiporaFlyoutBackground", "#FAFBFD", "#2B3039");
    }
}
