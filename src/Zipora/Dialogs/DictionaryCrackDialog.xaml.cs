using System.Diagnostics;
using System.IO;
using System.ComponentModel;
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
    private List<DictionaryEntry> _passwords = new();
    private int _runOffset;
    private int _runCount;
    private int _cursor;
    private int _totalLines;
    private long _tested;
    private long _total;
    private Stopwatch _stopwatch = new();
    private string _lastPass = "";
    private int _lastLine = 1;
    private string? _foundPassword;
    private int _resumeLine;

    /// <summary>命中并点击「用此密码解压」后返回 (压缩包路径, 密码)；直接关闭返回 null。</summary>
    public (string Archive, string Password)? CrackResult { get; private set; }

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
                RefreshDictionaryState();
            }
        }
        RestoreProgressForSelection();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }

    /// <summary>扫描程序目录 dictionaries\ 下的内置字典，展开列表可选（右侧显示大小）。</summary>
    private void LoadBuiltinDictionaries()
    {
        string? previous = SelectedDictPath();
        DictBox.Items.Clear();
        try
        {
            if (Directory.Exists(AppStorage.DictionaryRoot))
                foreach (var path in Directory.GetFiles(AppStorage.DictionaryRoot, "*.txt")
                             .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    try { DictBox.Items.Add(new DictItem(path)); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            if (previous != null && File.Exists(previous) &&
                !DictBox.Items.OfType<DictItem>().Any(item => string.Equals(item.FilePath, previous, StringComparison.OrdinalIgnoreCase)))
                DictBox.Items.Add(new DictItem(previous));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        DictBox.SelectedItem = DictBox.Items.OfType<DictItem>().FirstOrDefault(item =>
            string.Equals(item.FilePath, previous, StringComparison.OrdinalIgnoreCase));
        if (DictBox.SelectedItem == null && DictBox.Items.Count > 0) DictBox.SelectedIndex = 0;
        RefreshDictionaryState();
        RestoreProgressForSelection();
    }

    private void RefreshDictionaryState()
    {
        DictBox.IsEnabled = _cts == null && DictBox.Items.Count > 0;
        DictHint.Visibility = DictBox.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshDict_Click(object sender, RoutedEventArgs e) => LoadBuiltinDictionaries();

    private void BrowseDict_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择密码字典文件（每行一个密码）",
                Filter = "文本文件|*.txt;*.dic;*.dict|所有文件|*.*",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(this) == true) SelectDictionaryFile(dialog.FileName);
        }
        catch (Exception ex) { SetStatus("选择字典失败：" + ex.Message, error: true); }
    }

    private void SelectDictionaryFile(string path)
    {
        var item = DictBox.Items.OfType<DictItem>().FirstOrDefault(existing =>
            string.Equals(existing.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (item == null) { item = new DictItem(path); DictBox.Items.Add(item); }
        DictBox.SelectedItem = item;
        RefreshDictionaryState();
        RestoreProgressForSelection();
    }

    private void DictBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RestoreProgressForSelection();

    /// <summary>当前字典路径；没有字典时返回 null，不自动弹文件选择器。</summary>
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
            RestoreProgressForSelection();
        }
    }

    /// <summary>读取当前压缩包 + 字典对应的断点位置，恢复“起始行”和「继续」按钮。</summary>
    private void RestoreProgressForSelection()
    {
        if (_cts != null) return;
        string? archive = ArchiveBox.Text?.Trim();
        string? dict = SelectedDictPath();
        if (string.IsNullOrEmpty(archive) || dict == null)
        {
            _resumeLine = 0;
            UpdateStopContinueButton();
            return;
        }
        int? line = CrackProgressStore.Load(archive, dict);
        if (line is >= 1)
        {
            _resumeLine = line.Value;
            StartLineBox.Text = line.Value.ToString();
            UpdateStopContinueButton();
            SetStatus($"上次停在第 {line.Value} 行，可点「继续」接着跑。", error: false);
        }
        else
        {
            _resumeLine = 0;
            UpdateStopContinueButton();
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartCoreAsync();

    /// <summary>运行中显示「中止」，空闲时显示「继续」；两种状态共用一个按钮。</summary>
    private void UpdateStopContinueButton()
    {
        StopContinueButton.Content = "继续";
        StopContinueButton.IsEnabled = _resumeLine > 0;
        StopContinueButton.ToolTip = _resumeLine > 0
            ? $"从第 {_resumeLine} 行继续"
            : "当前没有可续跑的位置";
    }

    private async void StopContinue_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null)
        {
            _cts.Cancel();
            return;
        }
        if (_resumeLine <= 0)
        {
            SetStatus("没有可继续的位置，请先开始一次破解。", error: true);
            return;
        }
        StartLineBox.Text = _resumeLine.ToString();
        await StartCoreAsync();
    }

    private int GetThreadCount()
    {
        if (ThreadBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Content?.ToString(), out int n) && n is >= 1 and <= 16)
            return n;
        return 4;
    }

    private bool TryGetStartLine(out int startLine)
    {
        string text = StartLineBox.Text.Trim();
        if (text.Length == 0) { startLine = 1; StartLineBox.Text = "1"; return true; }
        if (!int.TryParse(text, out startLine) || startLine < 1)
        {
            SetStatus("起始行需要是大于 0 的整数。", error: true);
            StartLineBox.Focus();
            StartLineBox.SelectAll();
            return false;
        }
        return true;
    }

    private async Task StartCoreAsync()
    {
        if (_cts != null) return;
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
        if (!TryGetStartLine(out int startLine)) return;

        _archivePath = ArchiveBox.Text.Trim();
        _foundPassword = null;
        ResultText.Text = "正在读取字典…";
        ResultText.Foreground = ThemeBrush("ZiporaMutedBrush", System.Windows.Media.Brushes.DimGray);
        CopyButton.IsEnabled = false;
        UseButton.IsEnabled = false;

        string[] raw;
        try { raw = File.ReadAllLines(dictPath); }
        catch (Exception ex) { SetStatus("读取字典失败：" + ex.Message, error: true); return; }
        if (raw.Length == 0) { SetStatus("字典文件为空。", error: true); return; }
        if (startLine > raw.Length)
        {
            SetStatus($"起始行超出字典范围（共 {raw.Length} 行）。", error: true);
            return;
        }

        List<DictionaryEntry> entries = DictionaryPlan.Build(raw, startLine);
        if (entries.Count == 0)
        {
            SetStatus($"第 {startLine} 行之后没有可测试的密码。", error: true);
            return;
        }

        _passwords = entries;
        _runOffset = 0;
        _runCount = entries.Count;
        _totalLines = raw.Length;
        _total = _runCount;
        _tested = 0;
        _cursor = 0;
        _lastPass = "";
        _lastLine = entries[0].Line;
        _resumeLine = 0;
        int threads = GetThreadCount();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        StartButton.IsEnabled = false;
        StopContinueButton.Content = "中止";
        StopContinueButton.IsEnabled = true;
        StopContinueButton.ToolTip = "中止本次破解并保留当前位置";
        PickArchiveButton.IsEnabled = false;
        RefreshDictButton.IsEnabled = false;
        BrowseDictButton.IsEnabled = false;
        DictBox.IsEnabled = false;
        ThreadBox.IsEnabled = false;
        StartLineBox.IsEnabled = false;
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
            if (empty == true)
            {
                _foundPassword = "";
                ResultText.Text = "🎉 该压缩包无需密码（空密码即通过）。";
            }
            else if (_runCount > 0 && !ct.IsCancellationRequested)
            {
                var tasks = new List<Task>();
                for (int i = 0; i < threads; i++)
                    tasks.Add(Task.Run(() => RunWorker(_runOffset, _runCount, ct), ct));
                try { await Task.WhenAll(tasks); }
                catch (OperationCanceledException)
                {
                    // 某个线程命中后取消其余线程，或用户点了「中止」。
                    // 结果统一在下面判断，不能让取消异常跳过成功分支。
                }
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
                CrackProgressStore.Clear(_archivePath, dictPath);
            }
            else if (!ct.IsCancellationRequested)
            {
                _stopwatch.Stop();
                ResultText.Text = $"字典已全部尝试完毕（本次 {_tested} 个），未找到正确密码。";
                ResultText.Foreground = ThemeBrush("ZiporaWarningBrush", System.Windows.Media.Brushes.Firebrick);
                CrackProgressStore.Clear(_archivePath, dictPath);
            }
        }
        catch (OperationCanceledException)
        {
            // 空密码探测阶段被取消（中止或关窗）：按正常结束处理，
            // 由 finally 统一恢复控件并写入续跑位置，避免 async void 事件里冒出未处理异常。
        }
        catch (Exception ex)
        {
            _stopwatch.Stop();
            ResultText.Text = "破解过程出错，请重试。";
            ResultText.Foreground = ThemeBrush("ZiporaWarningBrush", System.Windows.Media.Brushes.Firebrick);
            SetStatus("字典破解失败：" + ex.Message, error: true);
        }
        finally
        {
            uiTimer.Stop();
            _cts.Dispose();
            _cts = null;
            StartButton.IsEnabled = true;
            PickArchiveButton.IsEnabled = true;
            RefreshDictButton.IsEnabled = true;
            BrowseDictButton.IsEnabled = true;
            ThreadBox.IsEnabled = true;
            StartLineBox.IsEnabled = true;
            RefreshDictionaryState();
            if (ct.IsCancellationRequested && _foundPassword == null)
            {
                int claimed = Volatile.Read(ref _cursor);
                int safeSlot = Math.Max(0, Math.Min(claimed - threads, _runCount));
                if (safeSlot < _runCount)
                {
                    int resume = _passwords[_runOffset + safeSlot].Line;
                    _resumeLine = resume;
                    StartLineBox.Text = resume.ToString();
                    CrackProgressStore.Save(_archivePath, dictPath, resume);
                    ResultText.Text = $"已中止。可从第 {resume} 行继续。";
                    ResultText.Foreground = ThemeBrush("ZiporaMutedBrush", System.Windows.Media.Brushes.DimGray);
                    SetStatus($"已中止：本次测试 {_tested} 个，已保留位置，可从第 {resume} 行继续。", error: false);
                }
                else
                {
                    SetStatus($"已中止：字典已全部尝试（{_tested} 个）。", error: false);
                }
            }
            UpdateStopContinueButton();
        }
    }

    /// <summary>共享游标分发：每个线程取下一个待测条目，中止后游标前的条目均已被领取。</summary>
    private void RunWorker(int offset, int count, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int slot = Interlocked.Increment(ref _cursor) - 1;
            if (slot >= count) return;

            DictionaryEntry entry = _passwords[offset + slot];
            _lastPass = entry.Password;
            _lastLine = entry.Line;

            bool? ok = _engine.TestPasswordFast(_archivePath!, entry.Password, ct);
            if (ct.IsCancellationRequested) return;
            Interlocked.Increment(ref _tested);
            if (ok == true)
            {
                _foundPassword = entry.Password;
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
        StatusText.Text = $"正在尝试密码… 第 {_lastLine} 行 / 共 {_totalLines:N0} 行　" +
                          $"已测 {tested:N0}/{_total:N0}（{pct:0.0}%）　速度 {speed:0.0} 密码/秒　当前: {cur}";
        StatusText.Foreground = ThemeBrush("ZiporaMutedBrush", System.Windows.Media.Brushes.DimGray);
    }

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

