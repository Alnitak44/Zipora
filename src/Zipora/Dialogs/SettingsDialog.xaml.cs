using System.Diagnostics;
using System.Windows;
using Wpf.Ui.Controls;
using Zipora.Services;

namespace Zipora.Dialogs;

/// <summary>
/// 设置窗口：目前包含「系统右键菜单」开关。
/// 勾选/取消勾选通过提权子进程（runas + 命令行参数）写入或清理注册表，完成后刷新状态。
/// </summary>
public partial class SettingsDialog : FluentWindow
{

    /// <summary>取主题语义刷（找不到时回退 fallback），避免硬编码颜色。</summary>
    private static System.Windows.Media.Brush ThemeBrush(string key, System.Windows.Media.Brush fallback)
    {
        return System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;
    }

    private bool _busy;
    private bool _protectionPromptOpen;
    private bool _suppressEvents; // 初始化/刷新状态时不触发注册表操作

    public SettingsDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshState();
            RefreshThemeState();
            RefreshBackdropState();
            RefreshBackgroundImageState();
            RefreshUpdateState();
            RefreshContributeState();
            RefreshArchiveBombProtectionState();
            RefreshZipSlipProtectionState();
        };
        Backdrop.ApplyFromSetting(this);
        if (AppStorage.IsPortable)
        {
            PortableInfoCard.Visibility = Visibility.Visible;
            PortableInfoText.Text = $"数据目录：{AppStorage.DataRoot}\n设置、密码库、背景图与临时文件均在此目录。退出后再安全弹出 U 盘；不要在运行中拔盘。右键菜单集成在便携版中禁用。FAT32 单文件上限约 4 GiB，大文件请分卷或使用 exFAT / NTFS。";
        }
    }

    private void NavigateToTheme_Click(object sender, RoutedEventArgs e)
        => ScrollToSection(ThemeSection);

    private void NavigateToFunction_Click(object sender, RoutedEventArgs e)
        => ScrollToSection(FunctionSection);

    private void ScrollToSection(FrameworkElement section)
    {
        // Explicitly calculate the anchor's position inside the ScrollViewer.
        // BringIntoView is unreliable here because the content is a StackPanel
        // and the requested item can already be partly visible.
        var point = section.TransformToAncestor(SettingsScrollViewer)
            .Transform(new Point(0, 0));
        SettingsScrollViewer.ScrollToVerticalOffset(
            Math.Max(0, SettingsScrollViewer.VerticalOffset + point.Y - 2));
    }

    // ---------- 外观主题（浅色 / 深色） ----------

    private void RefreshThemeState()
    {
        _suppressEvents = true;
        if (Services.ThemeManager.Current == Services.ThemeManager.Dark)
            DarkThemeRadio.IsChecked = true;
        else
            LightThemeRadio.IsChecked = true;
        _suppressEvents = false;
    }

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        string theme = DarkThemeRadio.IsChecked == true
            ? Services.ThemeManager.Dark
            : Services.ThemeManager.Light;
        // 立即全局切换（DynamicResource 自动刷新），ThemeManager 内部持久化
        Services.ThemeManager.Set(theme);
        if (Application.Current.MainWindow is Zipora.MainWindow main)
            main.RefreshCustomBackgroundAppearance();
    }

    // ---------- 窗口背景效果（Mica / Acrylic / 纯色） ----------

    private void RefreshBackdropState()
    {
        _suppressEvents = true;
        switch (Backdrop.Current)
        {
            case Backdrop.Acrylic: AcrylicRadio.IsChecked = true; break;
            case Backdrop.Solid: SolidRadio.IsChecked = true; break;
            default: MicaRadio.IsChecked = true; break;
        }
        _suppressEvents = false;
    }

    // ---------- 自定义背景图 ----------

    private void RefreshBackgroundImageState()
    {
        var store = AuthStore.Load();
        BackgroundImagePathBox.Text = store.CustomBackgroundImagePath;
        ClearBackgroundImageButton.IsEnabled = !string.IsNullOrWhiteSpace(store.CustomBackgroundImagePath);
    }

    private void ChooseBackgroundImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择主窗口背景图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;

        SaveBackgroundImage(dialog.FileName);
    }

    private void ClearBackgroundImage_Click(object sender, RoutedEventArgs e)
    {
        SaveBackgroundImage(string.Empty);
    }

    private void SaveBackgroundImage(string imagePath)
    {
        try
        {
            string saved = "";
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                var image = BackgroundImages.Read(imagePath);
                if (BackgroundImages.NeedsCrop(image.PixelWidth, image.PixelHeight))
                {
                    var crop = new BackgroundCropDialog(image) { Owner = this };
                    if (crop.ShowDialog() != true || crop.Result == null) return;
                    image = crop.Result;
                }
                saved = BackgroundImages.Save(image);
            }
            var store = AuthStore.Load();
            store.CustomBackgroundImagePath = saved;
            store.Save();
            if (Application.Current.MainWindow is Zipora.MainWindow main && !main.SetCustomBackground(saved))
                new NoticeDialog("请重新启动 Zipora", "背景图已保存，但当前窗口刷新失败。请完成正在进行的操作后关闭并重新打开 Zipora。", NoticeKind.Warning) { Owner = this }.ShowDialog();
            RefreshBackgroundImageState();
        }
        catch (Exception ex)
        {
            new NoticeDialog("背景图设置失败", "无法保存或加载图片，请检查图片格式、磁盘空间和写保护。原设置不会因裁切取消而改变。\n\n" + ex.Message, NoticeKind.Warning) { Owner = this }.ShowDialog();
        }
    }

    private void RefreshUpdateState()
    {
        _suppressEvents = true;
        UpdateFrequencyBox.SelectedIndex = (int)AuthStore.Load().UpdateFrequency;
        _suppressEvents = false;
    }
    private void UpdateFrequency_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressEvents || UpdateFrequencyBox.SelectedIndex < 0) return;
        try
        {
            var store = AuthStore.Load();
            store.UpdateFrequency = (UpdateFrequency)UpdateFrequencyBox.SelectedIndex;
            store.Save();
        }
        catch (Exception ex) { AppStorage.ReportSaveFailure(ex); }
    }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesButton.Content = "正在检查…";
        try { await UpdateChecker.CheckWithPromptAsync(this); }
        finally { CheckUpdatesButton.IsEnabled = true; CheckUpdatesButton.Content = "立即检查"; }
    }

    // ---------- 密码上传（解压后 / 压缩后） ----------

    private void RefreshContributeState()
    {
        _suppressEvents = true;
        var store = AuthStore.Load();
        ExtractUploadBox.IsChecked = store.AutoContributePassword;
        CompressUploadBox.IsChecked = store.CompressContributePassword;
        _suppressEvents = false;
    }

    private void Contribute_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var store = AuthStore.Load();
        store.AutoContributePassword = ExtractUploadBox.IsChecked == true;
        store.CompressContributePassword = CompressUploadBox.IsChecked == true;
        store.Save();
    }

    private void ManagePasswords_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PasswordManagerDialog { Owner = this };
        dialog.ShowDialog();
    }

    private void RefreshArchiveBombProtectionState()
    {
        _suppressEvents = true;
        ArchiveBombProtectionBox.IsChecked = AuthStore.Load().ArchiveBombProtectionEnabled;
        _suppressEvents = false;
    }

    private void RefreshZipSlipProtectionState()
    {
        SetZipSlipProtectionToggle(AuthStore.Load().ZipSlipProtectionEnabled);
    }

    private async void ZipSlipProtection_Changed(object sender, RoutedEventArgs e)
        => await ChangeProtectionAsync(zipSlip: true);

    private async void ArchiveBombProtection_Changed(object sender, RoutedEventArgs e)
        => await ChangeProtectionAsync(zipSlip: false);

    private async Task ChangeProtectionAsync(bool zipSlip)
    {
        if (_suppressEvents) return;
        var toggle = zipSlip ? ZipSlipProtectionBox : ArchiveBombProtectionBox;
        if (_protectionPromptOpen) { SetProtectionToggle(zipSlip, true); return; }
        if (toggle.IsChecked == true) { SaveProtectionPreference(true, zipSlip); return; }

        _protectionPromptOpen = true;
        try
        {
            // Leave the ToggleSwitch event before restoring state and showing a modal.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            SetProtectionToggle(zipSlip, true);
            if (!IsVisible) { SaveProtectionPreference(true, zipSlip); return; }
            var dialog = new NoticeDialog(zipSlip ? "关闭 Zip Slip 防护？" : "关闭压缩炸弹防护？",
                zipSlip
                    ? "关闭后，打开压缩包时不再自动检测危险路径和链接，提取时也会跳过额外安全预检。恶意压缩包可能把文件写入目标目录之外或覆盖其他文件。"
                    : "关闭后，Zipora 将不再提醒异常膨胀的压缩包，也不会在所选内容解压前阻止磁盘空间耗尽。恶意压缩包可能导致磁盘写满或系统失去响应。",
                NoticeKind.Warning, "仍然关闭", "保持开启", primaryIsDanger: true) { Owner = this };
            bool enabled = dialog.ShowDialog() != true;
            SetProtectionToggle(zipSlip, enabled);
            SaveProtectionPreference(enabled, zipSlip);
        }
        finally { _protectionPromptOpen = false; }
    }

    private void SetProtectionToggle(bool zipSlip, bool enabled)
    {
        _suppressEvents = true;
        (zipSlip ? ZipSlipProtectionBox : ArchiveBombProtectionBox).IsChecked = enabled;
        _suppressEvents = false;
    }

    private void SetZipSlipProtectionToggle(bool enabled) => SetProtectionToggle(true, enabled);

    private static void SaveProtectionPreference(bool enabled, bool zipSlip)
    {
        // Apply the confirmed choice in memory even when removable storage becomes
        // read-only/disconnected. A failed write must not crash an async UI handler.
        if (Application.Current.MainWindow is Zipora.MainWindow main)
        {
            if (zipSlip) main.SetZipSlipProtection(enabled);
            else main.SetArchiveBombProtection(enabled);
        }
        try
        {
            var store = AuthStore.Load();
            if (zipSlip) store.ZipSlipProtectionEnabled = enabled;
            else store.ArchiveBombProtectionEnabled = enabled;
            store.Save();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppStorage.Log("Protection preference save failed: " + ex);
            if (AppStorage.IsPortable) AppStorage.ReportSaveFailure(ex);
            else System.Windows.MessageBox.Show("设置已在本次会话生效，但未能保存。请检查目录权限和可用空间。\n\n" + ex.Message,
                "设置未保存", System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void Backdrop_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;

        string mode = MicaRadio.IsChecked == true ? Backdrop.Mica
            : AcrylicRadio.IsChecked == true ? Backdrop.Acrylic
            : Backdrop.Solid;

        // 立即应用到所有窗口（含本设置窗口与主窗口），并持久化
        Backdrop.Set(mode);
        try
        {
            var store = AuthStore.Load();
            store.WindowBackdrop = mode;
            store.Save();
        }
        catch (Exception ex)
        {
            // 持久化失败不影响本次会话效果
            AppStorage.ReportSaveFailure(ex);
        }
    }

    private void RefreshState()
    {
        _suppressEvents = true;
        ContextMenuBox.IsChecked = !AppStorage.IsPortable && ContextMenuInstaller.IsInstalled();
        ContextMenuBox.IsEnabled = !AppStorage.IsPortable;
        // Keep the tooltip owner enabled: the disabled ToggleSwitch's template can
        // remove its content from hit testing, so ShowOnDisabled alone is insufficient.
        ContextMenuCard.ToolTip = AppStorage.IsPortable ? "Portable版无法使用此功能" : null;
        _suppressEvents = false;
        OpStatusPanel.Visibility = Visibility.Collapsed;
    }

    private async void ContextMenuBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_busy || _suppressEvents || AppStorage.IsPortable) return;
        _busy = true;
        ContextMenuBox.IsEnabled = false;

        try
        {
            bool install = ContextMenuBox.IsChecked == true;
            bool ok = await LaunchElevatedAsync(install ? "--install-contextmenu" : "--uninstall-contextmenu");
            if (ok)
            {
                OpStatusIcon.Symbol = SymbolRegular.CheckmarkCircle20;
                OpStatusIcon.Foreground = (System.Windows.Media.Brush)FindResource("SystemFillColorSuccessBrush");
                OpStatusText.Text = install ? "已启用右键菜单。" : "已移除右键菜单。";
            }
            else
            {
                OpStatusIcon.Symbol = SymbolRegular.Warning20;
                OpStatusIcon.Foreground = ThemeBrush("ZiporaWarningBrush", System.Windows.Media.Brushes.OrangeRed);
                OpStatusText.Text = "操作未完成（可能未授权 UAC）。";
            }
            OpStatusPanel.Visibility = Visibility.Visible;

            // 以注册表实际状态为准（用户可能在 UAC 弹窗点了取消）
            ContextMenuBox.IsChecked = ContextMenuInstaller.IsInstalled();
        }
        finally
        {
            _busy = false;
            ContextMenuBox.IsEnabled = true;
        }
    }

    /// <summary>以管理员身份运行本程序执行注册表操作，等待完成。</summary>
    private static Task<bool> LaunchElevatedAsync(string arg)
    {
        string exe = Environment.ProcessPath
            ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Zipora.exe");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arg,
                UseShellExecute = true,
                Verb = "runas",
            };
            var proc = Process.Start(psi);
            if (proc == null) return Task.FromResult(false);
            return Task.Run(() => { proc.WaitForExit(); return proc.ExitCode == 0; });
        }
        catch
        {
            // UAC 被取消
            return Task.FromResult(false);
        }
    }
}
