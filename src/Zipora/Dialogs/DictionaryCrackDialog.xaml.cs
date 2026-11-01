using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Zipora.Services;

namespace Zipora.Dialogs;

/// <summary>
/// 字典密码破解窗口（免费功能，无需激活）：
/// 多线程并行跑 7z t 快速测试，命中即停；借鉴 7zrpw 的 2 秒超时加速技巧。
/// 由「工具 → 字典破解」打开，或密码框点「字典破解」预填压缩包后打开。
/// </summary>
public partial class DictionaryCrackDialog : Wpf.Ui.Controls.FluentWindow
{

    /// <summary>取主题语义刷（找不到时回退 fallback），避免硬编码颜色。</summary>
    private static System.Windows.Media.Brush ThemeBrush(string key, System.Windows.Media.Brush fallback)
    {
        return System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;
    }

    private readonly SevenZipEngine _engine;
    private CancellationTokenSource? _cts;

    private string? _archivePath;
    private List<string> _passwords = new();
    private long _tested;
    private long _total;
    private Stopwatch _stopwatch = new();
    private string _lastPass = "";
    private string? _foundPassword;

    /// <summary>命中并点击「用此密码解压」后返回 (压缩包路径, 密码)；直接关闭返回 null。</summary>
    public (string Archive, string Password)? CrackResult { get; private set; }

    private const string BrowseTag = "__browse__";

    /// <summary>字典下拉项：文件名 + 右侧大小。</summary>
    private sealed class DictItem
    {
        public string Name { get; }
        public string FilePath { get; }
        public string SizeText { get; }

        public DictItem(string filePath, bool showSize = true)
        {
            FilePath = filePath;
            Name = Path.GetFileName(filePath);
            SizeText = showSize ? FormatSize(new FileInfo(filePath).Length) : "";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double v = bytes;
            foreach (var unit in new[] { "KB", "MB", "GB", "TB" })
            {
                v /= 1024;
                if (v < 1024) return $"{v:0.#} {unit}";
            }
            return $"{v:0.#} PB";
        }
    }

    private sealed class BrowseDictItem
    {
        public string Name => "浏览其他字典文件…";
        public string SizeText => "";
        public string FilePath => BrowseTag;
    }

    public DictionaryCrackDialog(SevenZipEngine engine, string? prefillArchive = null)
    {
        InitializeComponent();
        Backdrop.ApplyFromSetting(this);
        _engine = engine;

        LoadBuiltinDictionaries();

        if (!string.IsNullOrEmpty(prefillArchive) && File.Exists(prefillArchive))
        {
            _archivePath = prefillArchive;
            ArchiveBox.Text = prefillArchive;
            // 自动识别同目录 passwd.txt 并置顶选中
            string? dir = Path.GetDirectoryName(prefillArchive);
            if (dir != null && File.Exists(Path.Combine(dir, "passwd.txt")))
            {
                var passItem = new DictItem(Path.Combine(dir, "passwd.txt"));
                DictBox.Items.Insert(0, passItem);
                DictBox.SelectedItem = passItem;
            }
        }
    }

    /// <summary>扫描程序目录 dictionaries\ 下的内置字典，展开列表可选（右侧显示大小）。</summary>
    private void LoadBuiltinDictionaries()
    {
        DictBox.Items.Clear();
        string dictDir = AppStorage.DictionaryRoot;
        try
        {
            if (Directory.Exists(dictDir))
            {
                foreach (var f in Directory.GetFiles(dictDir, "*.txt")
                             .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                {
                    DictBox.Items.Add(new DictItem(f));
                }
            }
        }
        catch { /* 目录不存在/不可读则只有「浏览其他」 */ }

        DictBox.Items.Add(new BrowseDictItem());
        if (DictBox.Items.Count > 0)
            DictBox.SelectedIndex = 0;
    }

    private void RefreshDict_Click(object sender, RoutedEventArgs e)
        => LoadBuiltinDictionaries();

    private void DictBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DictBox.SelectedItem is not BrowseDictItem) return;

        // 选了「浏览其他」→ 弹出文件选择
        var dlg = new OpenFileDialog
        {
            Title = "选择密码字典文件（每行一个密码）",
            Filter = "文本文件|*.txt;*.dic;*.dict|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            var custom = new DictItem(dlg.FileName);
            DictBox.Items.Insert(Math.Max(0, DictBox.Items.Count - 1), custom);
            DictBox.SelectedItem = custom;
        }
        else
        {
            // 取消 → 回退到上一项
            DictBox.SelectedIndex = Math.Max(0, DictBox.Items.Count - 2);
        }
    }

    /// <summary>当前选中的字典文件路径；选中的是「浏览其他」时返回 null。</summary>
    private string? SelectedDictPath()
    {
        if (DictBox.SelectedItem is DictItem item)
            return item.FilePath;
        return null;
    }

    private void PickArchive_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择要破解的加密压缩包",
            Filter = "压缩包|*.7z;*.zip;*.rar;*.tar;*.gz;*.tgz;*.bz2;*.xz;*.iso;*.cab;*.wim;*.zipx;*.jar;*.apk|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            _archivePath = dlg.FileName;
            ArchiveBox.Text = dlg.FileName;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ArchiveBox.Text) || !File.Exists(ArchiveBox.Text))
        {
            SetStatus("请先选择有效的压缩包。", error: true);
            return;
        }
        string? dictPath = SelectedDictPath();
        if (dictPath == null || !File.Exists(dictPath))
        {
            SetStatus("请先选择有效的字典文件（展开下拉列表选择内置字典，或浏览其他文件）。", error: true);
            return;
        }
        if (_cts != null) return;

        _archivePath = ArchiveBox.Text.Trim();
        _foundPassword = null;
        ResultText.Text = "正在读取字典…";
        ResultText.Foreground = ThemeBrush("ZiporaMutedBrush", System.Windows.Media.Brushes.DimGray);
        CopyButton.IsEnabled = false;
        UseButton.IsEnabled = false;

        int threads = (ThreadBox.SelectedIndex + 1) is int t and >= 1 and <= 16 ? t : 4;

        try
        {
            _passwords = File.ReadAllLines(dictPath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            SetStatus("读取字典失败：" + ex.Message, error: true);
            return;
        }

        _total = _passwords.Count;
        _tested = 0;
        _lastPass = "";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        PickArchiveButton.IsEnabled = false;
        RefreshDictButton.IsEnabled = false;
        Progress.Maximum = Math.Max(_total, 1);
        Progress.Value = 0;

        _stopwatch.Restart();
        var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        uiTimer.Tick += (_, _) => UpdateProgressUi();
        uiTimer.Start();

        try
        {
            // 先试空密码（未加密/空密码包）
            bool? empty = await Task.Run(() => _engine.TestPasswordFast(_archivePath, "", ct), ct);
            if (ct.IsCancellationRequested) return;
            if (empty == true)
            {
                _foundPassword = "";
                ResultText.Text = "🎉 该压缩包无需密码（空密码即通过）。";
            }
            else if (_total > 0)
            {
                int n = threads;
                var tasks = new List<Task>();
                for (int i = 0; i < n; i++)
                {
                    int idx = i;
                    var slice = _passwords.Where((_, k) => k % n == idx).ToList();
                    tasks.Add(Task.Run(() => RunSliceAsync(slice, ct), ct));
                }
                await Task.WhenAll(tasks);
            }

            if (_foundPassword != null)
            {
                _stopwatch.Stop();
                double speed = _stopwatch.Elapsed.TotalSeconds > 0
                    ? _tested / _stopwatch.Elapsed.TotalSeconds
                    : 0;
                ResultText.Text = $"🎉 破解成功！密码：<{_foundPassword}>　（用时 {_stopwatch.Elapsed.TotalSeconds:0.0}s，平均 {speed:0.0} 密码/秒）";
                ResultText.Foreground = ThemeBrush("ZiporaSuccessBrush", System.Windows.Media.Brushes.SeaGreen);
                CopyButton.IsEnabled = true;
                UseButton.IsEnabled = true;
                SaveFoundPassword();
            }
            else if (!ct.IsCancellationRequested)
            {
                _stopwatch.Stop();
                ResultText.Text = $"字典已全部尝试完毕（{_tested} 个），未找到正确密码。";
                ResultText.Foreground = ThemeBrush("ZiporaWarningBrush", System.Windows.Media.Brushes.Firebrick);
            }
        }
        finally
        {
            uiTimer.Stop();
            _cts.Dispose();
            _cts = null;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            PickArchiveButton.IsEnabled = true;
            RefreshDictButton.IsEnabled = true;
            if (ct.IsCancellationRequested && _foundPassword == null)
                SetStatus("已停止。", error: false);
        }
    }

    private void RunSliceAsync(List<string> slice, CancellationToken ct)
    {
        foreach (var pwd in slice)
        {
            ct.ThrowIfCancellationRequested();
            _lastPass = pwd;
            Interlocked.Increment(ref _tested);

            bool? ok = _engine.TestPasswordFast(_archivePath!, pwd, ct);
            if (ct.IsCancellationRequested) return;
            if (ok == true)
            {
                _foundPassword = pwd;
                _cts?.Cancel();
                return;
            }
        }
    }

    private void UpdateProgressUi()
    {
        if (_cts == null) return;
        long tested = Interlocked.Read(ref _tested);
        Progress.Value = Math.Min(tested, _total);
        double speed = _stopwatch.Elapsed.TotalSeconds > 0
            ? tested / _stopwatch.Elapsed.TotalSeconds
            : 0;
        double pct = _total > 0 ? tested * 100.0 / _total : 0;
        string cur = _lastPass.Length > 24 ? _lastPass[..24] + "…" : _lastPass;
        StatusText.Text = $"正在尝试密码… {tested}/{_total}（{pct:0.0}%）　速度 {speed:0.0} 密码/秒　当前: {cur}";
        StatusText.Foreground = ThemeBrush("ZiporaMutedBrush", System.Windows.Media.Brushes.DimGray);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
        => _cts?.Cancel();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_foundPassword == null) return;
        Clipboard.SetText(_foundPassword);
        SetStatus($"已复制密码到剪贴板。", error: false);
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (_foundPassword == null || _archivePath == null) return;
        CrackResult = (_archivePath, _foundPassword);
        DialogResult = true;
    }

    /// <summary>把命中的密码保存到压缩包同目录 passwd.txt（追加）。</summary>
    private void SaveFoundPassword()
    {
        try
        {
            if (_foundPassword == null || _archivePath == null) return;
            // Portable mode must not leave plaintext passwords beside an archive
            // on a borrowed computer. Users can explicitly add them to the vault.
            if (AppStorage.IsPortable) return;
            string passFile = Path.Combine(Path.GetDirectoryName(_archivePath) ?? "", "passwd.txt");
            File.AppendAllText(passFile, _foundPassword + Environment.NewLine);
        }
        catch { /* 保存失败不影响破解结果 */ }
    }

    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.Foreground = error
            ? ThemeBrush("ZiporaWarningBrush", System.Windows.Media.Brushes.OrangeRed)
            : (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush");
    }
}
