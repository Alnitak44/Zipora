using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

using Zipora.Services;

namespace Zipora.Dialogs;

public partial class CreateArchiveDialog : Wpf.Ui.Controls.FluentWindow
{
    private sealed record SavedPasswordItem(string Value, int Number)
    {
        public override string ToString() => Value;
    }

    /// <summary>显示名 → (7z 内部格式, 默认扩展名, 是否支持密码)。</summary>
    private static readonly (string Name, string Format, string Ext, bool Pwd)[] Formats =
    {
        ("7z（默认，压缩率高）", "7z", ".7z", true),
        ("zip（兼容性好）", "zip", ".zip", true),
        ("exe 自解压（7z SFX）", "sfx", ".exe", true),
        ("tar（无压缩）", "tar", ".tar", false),
        ("tar.gz（gzip 压缩）", "gzip", ".tar.gz", false),
        ("tar.bz2（bzip2 压缩）", "bzip2", ".tar.bz2", false),
        ("tar.xz（xz 压缩）", "xz", ".tar.xz", false),
        ("wim（Windows 映像）", "wim", ".wim", false),
    };

    public ObservableCollection<string> Sources { get; } = new();

    public string? OutFile { get; private set; }
    public string Format { get; private set; } = "7z";
    public int Level { get; private set; } = 5;
    public string? Password { get; private set; }
    public bool EncryptHeaders { get; private set; }
    /// <summary>每卷字节数；null 表示不分卷。</summary>
    public long? VolumeSizeBytes { get; private set; }



    private const string DefaultFileName = "新建压缩";

    public CreateArchiveDialog()
    {
        InitializeComponent();
        Backdrop.ApplyFromSetting(this);
        SourceList.ItemsSource = Sources;



        foreach (var f in Formats)
        {
            var item = new ComboBoxItem { Content = f.Name, Tag = f };
            if (f.Format == "7z") item.IsSelected = true;
            FormatBox.Items.Add(item);
        }
        PopulateSavedPasswords();

        // 默认输出到桌面，文件名「新建压缩」，效仿 Bandizip
        OutBox.Text = DefaultOutPath();
        Loaded += (_, _) => SelectDefaultOutputName();
    }

    private void PopulateSavedPasswords()
    {
        SavedPasswordList.Items.Clear();
        var passwords = PasswordStore.Load();
        for (var i = 0; i < passwords.Count; i++)
            SavedPasswordList.Items.Add(new SavedPasswordItem(passwords[i], i + 1));
        SavedPasswordButton.IsEnabled = CurrentFormat.Pwd && (SavedPasswordList.Items.Count > 0 || PasswordStore.NeedsUnlock);
        SavedPasswordButton.ToolTip = SavedPasswordButton.IsEnabled
            ? (PasswordStore.NeedsUnlock ? "解锁便携密码库" : "选择已保存的常用密码")
            : "请先到“设置 → 密码管理”添加常用密码";
    }

    private void ToggleSavedPasswords_Click(object sender, RoutedEventArgs e)
    {
        if (!PortableVaultDialog.EnsureUnlocked(this)) return;
        PopulateSavedPasswords();
        SavedPasswordPopup.IsOpen = !SavedPasswordPopup.IsOpen;
    }

    private void SavedPasswordList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SavedPasswordList.SelectedItem is not SavedPasswordItem item) return;
        PasswordBox.Password = item.Value;
        ConfirmBox.Password = item.Value;
        SavedPasswordPopup.IsOpen = false;
    }

    /// <summary>打开窗口时只选中文件名，保留输出目录和扩展名方便直接改名。</summary>
    private void SelectDefaultOutputName()
    {
        string path = OutBox.Text;
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.Length == 0) return;
        int start = path.LastIndexOf(name, StringComparison.Ordinal);
        if (start < 0) return;
        OutBox.Focus();
        OutBox.Select(start, name.Length);
    }

    private string DefaultOutPath()
        => Path.Combine(AppStorage.IsPortable ? AppStorage.Layout.Root : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            DefaultFileName + CurrentFormat.Ext);

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        { }

    private void EncryptArchiveCheck_Changed(object sender, RoutedEventArgs e)
        => UpdateEncryptionControls();

    private void SplitArchiveCheck_Changed(object sender, RoutedEventArgs e)
        => UpdateVolumeControls();

    private void UpdateEncryptionControls()
    {
        bool encrypting = CurrentFormat.Pwd && EncryptArchiveCheck.IsChecked == true;
        EncryptionPanel.Visibility = encrypting ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.IsEnabled = encrypting;
        ConfirmBox.IsEnabled = encrypting;
        SavedPasswordButton.IsEnabled = encrypting && (SavedPasswordList.Items.Count > 0 || PasswordStore.NeedsUnlock);
        EncryptHeadersCheck.IsEnabled = encrypting && (CurrentFormat.Format == "7z" || CurrentFormat.Format == "sfx");
        if (!encrypting)
        {
            PasswordBox.Password = "";
            ConfirmBox.Password = "";
            EncryptHeadersCheck.IsChecked = false;
            SavedPasswordPopup.IsOpen = false;
        }
    }

    private void UpdateVolumeControls()
    {
        bool volumeSupported = CurrentFormat.Format != "sfx";
        bool splitting = volumeSupported && SplitArchiveCheck.IsChecked == true;
        VolumeOptionsPanel.Visibility = splitting ? Visibility.Visible : Visibility.Collapsed;
        VolumePanel.IsEnabled = splitting;
        if (!splitting) VolumeSizeBox.Text = "";
    }


    private (string Name, string Format, string Ext, bool Pwd) CurrentFormat
    {
        get
        {
            if (FormatBox.SelectedItem is ComboBoxItem item
                && item.Tag is ValueTuple<string, string, string, bool> t)
            {
                return t;
            }
            return Formats[0];
        }
    }

    private void FormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var f = CurrentFormat;
        bool pwdOk = f.Pwd;

        if (!pwdOk)
        {
            EncryptArchiveCheck.IsChecked = false;
        }
        EncryptArchiveCheck.IsEnabled = pwdOk;
        UpdateEncryptionControls();

        // SFX 必须是单一可执行文件，不能拆成独立分卷。
        bool volumeSupported = f.Format != "sfx";
        SplitArchiveCheck.IsEnabled = volumeSupported;
        if (!volumeSupported) SplitArchiveCheck.IsChecked = false;
        VolumeHintText.Text = volumeSupported
            ? "生成 archive.7z.001、.002…；解压时请选择 .001 文件。"
            : "自解压格式必须是单个 EXE，不能使用分卷。";
        UpdateVolumeControls();

        // Renaming or browsing must not disable suffix synchronization.
        OutBox.Text = ArchiveOutputPath.WithExtension(OutBox.Text, f.Ext);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var f = CurrentFormat;
        var dlg = new SaveFileDialog
        {
            Title = "选择压缩包保存位置",
            Filter = $"{f.Name}|*{f.Ext}|所有文件|*.*",
            AddExtension = true,
            DefaultExt = f.Ext,
        };
        if (dlg.ShowDialog(this) == true)
            OutBox.Text = ArchiveOutputPath.WithExtension(dlg.FileName, f.Ext);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择要压缩的文件（可多选）",
            Multiselect = true,
            Filter = "所有文件|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            foreach (var f in dlg.FileNames)
                if (!Sources.Contains(f)) Sources.Add(f);
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择要压缩的文件夹" };
        if (dlg.ShowDialog(this) == true)
            if (!Sources.Contains(dlg.FolderName)) Sources.Add(dlg.FolderName);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = SourceList.SelectedItems.Cast<string>().ToList();
        foreach (var s in selected) Sources.Remove(s);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        string outFile = OutBox.Text.Trim();
        if (outFile.Length == 0)
        {
            SetStatus("请选择压缩包保存位置。");
            return;
        }
        if (Path.GetFileName(outFile).Length == 0)
        {
            SetStatus("请在输出位置填写压缩包文件名。");
            return;
        }
        if (Sources.Count == 0)
        {
            SetStatus("请至少添加一个文件或文件夹。");
            return;
        }

        var f = CurrentFormat;
        bool encrypting = EncryptArchiveCheck.IsChecked == true;
        string pwd = encrypting ? PasswordBox.Password : "";
        if (encrypting && pwd != ConfirmBox.Password)
        {
            SetStatus("两次输入的密码不一致。");
            return;
        }

        long? volumeSizeBytes = null;
        string volumeText = VolumeSizeBox.Text.Trim();
        if (volumeText.Length > 0)
        {
            if (!decimal.TryParse(volumeText, out var amount) || amount <= 0)
            {
                SetStatus("分卷大小请输入大于 0 的数字。");
                return;
            }
            decimal multiplier = VolumeUnitBox.SelectedIndex == 1 ? 1024m * 1024m * 1024m : 1024m * 1024m;
            decimal bytes = amount * multiplier;
            if (bytes > long.MaxValue || bytes < 1024m * 1024m)
            {
                SetStatus("分卷大小应在 1 MB 到 8 EB 之间。");
                return;
            }
            volumeSizeBytes = decimal.ToInt64(decimal.Truncate(bytes));
        }

        // Share the same suffix rules with format changes, including .tar.gz / .tar.bz2.
        outFile = ArchiveOutputPath.FindAvailablePath(outFile, f.Ext);
        OutBox.Text = outFile; // 回显最终路径，用户创建前可见

        OutFile = outFile;
        Format = f.Format;
        Level = LevelBox.SelectedIndex switch
        {
            0 => 0,
            1 => 1,
            2 => 3,
            3 => 5,
            4 => 7,
            5 => 9,
            _ => 5,
        };
        Password = pwd.Length > 0 ? pwd : null;
        EncryptHeaders = EncryptHeadersCheck.IsChecked == true && Password != null;
        VolumeSizeBytes = volumeSizeBytes;
        DialogResult = true;
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusPanel.Visibility = Visibility.Visible;
    }
}
