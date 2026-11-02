using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using System.Windows.Threading;
using Zipora.Dialogs;
using Zipora.Services;
using Zipora.Services.KeyFetch;
using Microsoft.Win32;
using System.Linq;

namespace Zipora;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>KeyFetch 云端服务地址（固定，不提供手动配置）。</summary>
    private const string KeyFetchBaseUrl = "https://KeyFetch.site";

    private readonly SevenZipEngine _engine;
    private readonly ObservableCollection<ArchiveEntry> _entries = new();
    private readonly ObservableCollection<ArchiveEntry> _visibleEntries = new();
    private readonly ObservableCollection<ArchiveTreeNode> _treeRoots = new();
    private readonly Dictionary<string, ArchiveEntry> _directoryEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly AuthStore _authStore;
    private readonly LocalRateLimiter _rateLimiter;
    private readonly string _deviceFp;

    private IKeyFetchClient _keyFetch;

    private string? _archivePath;
    private ArchiveInfo? _archiveInfo;
    private string? _md5;
    private long _fileSize;
    private string? _currentPassword;
    private bool _passwordFromUser;   // 密码来自用户手动输入（解压成功后有资格贡献云端）
    private bool _needsPassword;
    private bool _headerEncrypted;    // 文件名也加密，未拿到密码前列不出目录
    private bool _archiveRiskPromptShown;
    private int _lastProgressPct = -1;
    private string _currentDirectory = "";

    /// <summary>字典破解命中的密码，ResolvePasswordAsync 消费后清空。</summary>
    private string? _prefillPassword;

    /// <summary>当前密码来源：true 表示字典破解命中（用于状态文案）。</summary>
    private bool _fromCrack;

    private static readonly string DesktopDir =
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    private static readonly string[] ArchiveExtensions =
    {
        ".7z", ".zip", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst",
        ".lz", ".lzma", ".iso", ".cab", ".wim", ".zipx", ".jar", ".apk", ".001",
    };

    private CancellationTokenSource? _busyCts;

    /// <summary>窗口标题基础部分（带版本号）。</summary>
    private static string BaseTitle => $"Zipora v{AppInfo.Version}";

    // 拖出解压：仅无修饰键、从条目开始并离开窗口后触发，避免与多选手势抢操作。
    private Point _mouseDownPos;
    private bool _dragCandidate;
    private bool _dragThresholdReached;
    private bool _dragOutStarted;
    private bool _clickPending;   // 多选集合内的普通单击：松开时塌缩为单选
    private ArchiveEntry? _mouseDownItem;

    // 目录树拖出解压：普通点击/展开不触发，只有超过系统拖拽阈值并离开窗口才开始。
    private Point _treeMouseDownPos;
    private bool _treeDragCandidate;
    private ArchiveTreeNode? _treeMouseDownNode;

    private bool _dragActive;
    private bool _closeWhenIdle;
    private readonly TemporaryWorkspace _temporary = new();

    // 橡皮筋框选（从列表空白处左键拖动，效仿资源管理器）
    private bool _marqueeActive;
    private Point _marqueeStart;
    private readonly HashSet<object> _marqueeInitialSelection = new();
    private ModifierKeys _marqueeModifiers;

    // Skeleton 占位（读取目录期间的骨架行数据）
    private readonly object[] _skeletonRows = new object[12];

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? archiveToOpen)
    {
        InitializeComponent();
        FileList.ItemsSource = _visibleEntries;
        DirectoryTree.ItemsSource = _treeRoots;

        _engine = new SevenZipEngine(Path.Combine(AppContext.BaseDirectory, "7zip", "7z.exe"));
        _authStore = AuthStore.Load();
        _rateLimiter = LocalRateLimiter.Load();
        _deviceFp = _authStore.DeviceFp;
        _keyFetch = CreateKeyFetchClient();
        Title = BaseTitle;
        TitleBar.Title = BaseTitle;

        // 窗口背景效果（Mica / Acrylic / 纯色），设置页可切换
        Backdrop.Init(_authStore.WindowBackdrop);
        Backdrop.ApplyFromSetting(this);
        ApplyCustomBackground(_authStore.CustomBackgroundImagePath);

        if (!_engine.IsAvailable)
        {
            MessageBox.Show(this,
                "找不到 7-Zip 引擎（7zip\\7z.exe）。\n请确认程序目录下存在 7zip 文件夹，解压功能将不可用。",
                "Zipora", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        ShowEmptyState(true);

        if (archiveToOpen != null)
        {
            string? cliTarget = App.CliExtractTarget;
            if (cliTarget == null)
            {
                Loaded += async (_, _) => await OpenArchiveAsync(archiveToOpen);
            }
            else
            {
                // 右键子菜单触发：打开压缩包后自动解压到对应位置
                Loaded += async (_, _) =>
                {
                    await OpenArchiveAsync(archiveToOpen);
                    await RunCliExtractAsync(archiveToOpen, cliTarget);
                    App.CliExtractTarget = null;
                };
            }
        }
        else if (App.CliCreateFiles.Count > 0)
        {
            // 右键「新建压缩包」触发：打开压缩对话框并预填选中的文件/文件夹
            var files = App.CliCreateFiles.ToList();
            App.CliCreateFiles.Clear();
            Loaded += (_, _) => OpenCreateWithFiles(files);
        }

        Loaded += async (_, _) => await UpdateChecker.CheckWithPromptAsync(this, automatic: true);
    }

    // ---------- 空态 / 骨架屏状态管理 ----------

    /// <summary>由设置页调用：立即应用或清除主窗口的自定义背景图。</summary>
    public bool SetCustomBackground(string? imagePath)
    {
        _authStore.CustomBackgroundImagePath = imagePath ?? "";
        ApplyCustomBackground(_authStore.CustomBackgroundImagePath);
        return string.IsNullOrWhiteSpace(imagePath) || HasCustomBackground;
    }

    /// <summary>由设置页调用：立即更新本窗口使用的压缩炸弹防护状态。</summary>
    public void SetArchiveBombProtection(bool enabled)
    {
        _authStore.ArchiveBombProtectionEnabled = enabled;
    }

    /// <summary>主题切换后重新计算玻璃卡片的色调。</summary>
    public void RefreshCustomBackgroundAppearance()
    {
        ApplyCustomBackground(_authStore.CustomBackgroundImagePath);
    }

    private void ApplyCustomBackground(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            CustomBackgroundImage.Source = null;
            CustomBackgroundImage.Visibility = Visibility.Collapsed;
            ClearGlassCards();
            return;
        }

        try
        {
            var bitmap = BackgroundImages.Read(imagePath);

            CustomBackgroundImage.Source = bitmap;
            CustomBackgroundImage.Visibility = Visibility.Visible;
            ApplyGlassCards();
        }
        catch
        {
            // 图片损坏或格式不支持时回退到普通窗口，不阻断启动。
            CustomBackgroundImage.Source = null;
            CustomBackgroundImage.Visibility = Visibility.Collapsed;
            ClearGlassCards();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        if (_busyCts != null || _dragActive)
        {
            e.Cancel = true;
            _closeWhenIdle = true;
            _busyCts?.Cancel();
            Status("正在停止操作，完成清理后关闭…");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _temporary.Dispose();
        base.OnClosed(e);
    }

    internal bool HasCustomBackground => CustomBackgroundImage.Visibility == Visibility.Visible;

    private void ApplyGlassCards() => SurfacePalette.Apply(this, HasCustomBackground);

    private void ClearGlassCards() => SurfacePalette.Apply(this, false);

    // ---------- 压缩包目录浏览 ----------

    private void InitializeArchiveBrowser()
    {
        if (_archivePath == null) return;

        _treeRoots.Clear();
        _directoryEntries.Clear();
        _currentDirectory = "";

        var root = new ArchiveTreeNode(Path.GetFileName(_archivePath), "") { IsExpanded = true };
        _treeRoots.Add(root);
        var nodes = new Dictionary<string, ArchiveTreeNode>(StringComparer.OrdinalIgnoreCase);

        ArchiveTreeNode EnsureDirectory(string directory)
        {
            directory = NormalizeArchivePath(directory);
            if (directory.Length == 0) return root;
            if (nodes.TryGetValue(directory, out var existing)) return existing;

            string parent = GetParentDirectory(directory);
            var parentNode = EnsureDirectory(parent);
            var node = new ArchiveTreeNode(GetArchiveLeafName(directory), directory);
            nodes.Add(directory, node);
            parentNode.Children.Add(node);
            if (!_directoryEntries.ContainsKey(directory))
                _directoryEntries.Add(directory, new ArchiveEntry { Path = directory, IsDir = true });
            return node;
        }

        foreach (var entry in _entries)
        {
            string path = NormalizeArchivePath(entry.Path);
            if (path.Length == 0) continue;

            string directory = entry.IsDir ? path : GetParentDirectory(path);
            EnsureDirectory(directory);
            if (entry.IsDir)
                _directoryEntries[path] = entry;
        }

        SetArchiveBrowserVisible(true);
        NavigateArchiveDirectory("");
    }

    private void SetArchiveBrowserVisible(bool visible)
    {
        ArchiveNavigator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CurrentDirectoryBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        FileList.Margin = visible ? new Thickness(228, 36, 0, 2) : new Thickness(0, 2, 0, 0);
        MarqueeCanvas.Margin = visible ? new Thickness(228, 36, 0, 2) : new Thickness(0, 2, 0, 0);
        if (CustomBackgroundImage.Visibility == Visibility.Visible)
            ApplyGlassCards();
    }

    private void DirectoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ArchiveTreeNode node)
            NavigateArchiveDirectory(node.FullPath);
    }

    private void DirectoryTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_busyCts != null || FindVisualAncestor<ToggleButton>(e.OriginalSource as DependencyObject) != null) return;
        var item = FindVisualAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not ArchiveTreeNode node) { ResetTreeDragGesture(); return; }
        _treeMouseDownPos = e.GetPosition(DirectoryTree);
        _treeMouseDownNode = node;
        _treeDragCandidate = Keyboard.Modifiers == ModifierKeys.None;
        // Do not capture here: TreeView still needs its native selection/expander handling.
    }

    private async void DirectoryTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_treeDragCandidate || _treeMouseDownNode == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { ResetTreeDragGesture(); return; }
        var pos = e.GetPosition(DirectoryTree);
        if (Math.Abs(pos.X - _treeMouseDownPos.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _treeMouseDownPos.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var node = _treeMouseDownNode;
        ResetTreeDragGesture();
        e.Handled = true;
        if (node.FullPath.Length == 0)
        {
            var roots = ArchiveSelectionPlan.ArchiveRoots(_entries);
            await StartArchiveDragAsync(roots, Path.GetFileNameWithoutExtension(_archivePath) ?? "压缩包");
        }
        else await StartArchiveDragAsync(new[] { new ArchiveEntry { Path = node.FullPath, IsDir = true } });
    }

    private void DirectoryTree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => ResetTreeDragGesture();

    private void ResetTreeDragGesture()
    {
        _treeDragCandidate = false;
        _treeMouseDownNode = null;
        if (Mouse.Captured == DirectoryTree) Mouse.Capture(null);
    }

    private List<ArchiveEntry> GetFilesUnderDirectory(string directory)
    {
        string normalized = NormalizeArchivePath(directory).TrimEnd('/');
        string prefix = normalized.Length == 0 ? "" : normalized + "/";
        var files = _entries
            .Where(entry => !entry.IsDir &&
                (prefix.Length == 0 || NormalizeArchivePath(entry.Path)
                    .StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // 空文件夹没有文件条目时仍允许拖出；7-Zip 会按目录条目创建它。
        if (files.Count == 0 && normalized.Length > 0 && _directoryEntries.TryGetValue(normalized, out var folder))
            files.Add(folder);
        return files;
    }

    private void NavigateUp_Click(object sender, RoutedEventArgs e)
        => NavigateArchiveDirectory(GetParentDirectory(_currentDirectory));

    private void NavigateArchiveDirectory(string directory)
    {
        _currentDirectory = NormalizeArchivePath(directory);
        _visibleEntries.Clear();

        foreach (var folder in _directoryEntries.Values
                     .Where(x => GetParentDirectory(NormalizeArchivePath(x.Path)) == _currentDirectory)
                     .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            _visibleEntries.Add(folder);
        }
        foreach (var file in _entries
                     .Where(x => !x.IsDir && GetParentDirectory(NormalizeArchivePath(x.Path)) == _currentDirectory)
                     .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            _visibleEntries.Add(file);
        }

        CurrentDirectoryText.Text = _currentDirectory.Length == 0
            ? "压缩包根目录"
            : "压缩包根目录  ›  " + _currentDirectory.Replace("/", "  ›  ");
        NavigateUpButton.IsEnabled = _currentDirectory.Length > 0;
        FileList.SelectedItems.Clear();
    }

    private static string NormalizeArchivePath(string path)
        => path.Replace('\\', '/').Trim('/');

    private static string GetParentDirectory(string path)
    {
        path = NormalizeArchivePath(path);
        int slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private static string GetArchiveLeafName(string path)
    {
        path = NormalizeArchivePath(path);
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>空态卡片显示/隐藏（列表有内容时隐藏）。</summary>
    private void ShowEmptyState(bool show)
    {
        EmptyState.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FileList.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>进入骨架屏状态：显示 12 行流光占位，隐藏空态与真实列表。</summary>
    private void ShowSkeleton()
    {
        ShowEmptyState(false);
        FileList.Visibility = Visibility.Collapsed;
        SkeletonOverlay.ItemsSource = _skeletonRows;
        SkeletonOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>退出骨架屏状态：恢复列表与空态判断。</summary>
    private void HideSkeleton()
    {
        SkeletonOverlay.Visibility = Visibility.Collapsed;
        FileList.Visibility = Visibility.Visible;
        ShowEmptyState(_archivePath == null && _visibleEntries.Count == 0);
    }

    /// <summary>列表条目变化后刷新空态可见性（带淡入动画）。</summary>
    private void RefreshListVisualState(bool animateIn)
    {
        ShowEmptyState(_archivePath == null && _visibleEntries.Count == 0 && SkeletonOverlay.Visibility != Visibility.Visible);
        if (animateIn && _visibleEntries.Count > 0)
        {
            // 列表卡片内容淡入 + 轻微上移，营造内容「就位」感
            FileList.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.25)));
            var tf = new TranslateTransform();
            FileList.RenderTransform = tf;
            tf.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, TimeSpan.FromSeconds(0.25))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private async void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ResetPointerGesture();
        if (_busyCts != null) return;
        if (_visibleEntries.Count == 0) return; // 双击发生在空态/列头时不处理
        // 不依赖 SelectedItem：预览双击发生时，WPF 的选择状态可能尚未完成更新。
        if (GetItemAt(e.GetPosition(FileList)) is not ArchiveEntry entry) return;

        if (_archivePath == null) return;
        if (entry.IsDir)
        {
            NavigateArchiveDirectory(entry.Path);
            e.Handled = true;
            return;
        }
        // 压缩包模式：双击文件直接打开（提取到临时目录 → 系统默认程序运行）
        e.Handled = true;
        await OpenEntryFromArchiveAsync(entry);
    }

    // ---------- 打开/运行压缩包内条目（效仿 Bandizip 双击直接运行） ----------

    /// <summary>右键菜单「打开」。</summary>
    private async void CtxOpen_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not ArchiveEntry entry) return;

        if (_archivePath == null) return;
        if (entry.IsDir)
        {
            NavigateArchiveDirectory(entry.Path);
            return;
        }
        await OpenEntryFromArchiveAsync(entry);
    }

    /// <summary>提取压缩包内条目到临时目录并用系统默认程序打开。</summary>
    private async Task OpenEntryFromArchiveAsync(ArchiveEntry entry)
    {
        if (_busyCts != null || _archivePath == null) return;
        if (!await EnsurePasswordForArchiveAsync()) return;

        string archive = _archivePath;
        string pwd = _currentPassword ?? "";
        ArchiveSelectionPlan plan;
        try { plan = ArchiveSelectionPlan.Create(_entries.ToList(), new[] { entry }); }
        catch (Exception ex) { ShowNotice("无法打开", ex.Message, NoticeKind.Warning); return; }
        string tmpDir = _temporary.CreateDirectory("open");
        if (!TryPrepareExtractionSpace(tmpDir, new[] { entry }, true, out var guard, cleanupOnStop: true))
        { _temporary.Remove(tmpDir); return; }

        bool ok = false;
        string failMsg = "";
        await RunBusyAsync(async ct =>
        {
            Status($"正在提取 {entry.Path} 到临时目录…");
            var result = await _engine.ExtractSelectedAsync(archive, pwd, tmpDir, plan.ExtractionPaths, new Progress<double>(p => SetProgress(p, "提取")), ct, guard);
            HideProgress();
            ok = result.Success && !result.Cancelled;
            failMsg = result.Message;
        });

        if (!ok)
        {
            _temporary.Remove(tmpDir);
            if (failMsg.Length > 0) Status(failMsg);
            return;
        }

        if (_closeWhenIdle) return;
        string full = ArchiveSelectionPlan.ResolveInside(tmpDir, entry.Path);
        if (!File.Exists(full))
        {
            Status("提取失败：目标文件不存在。");
            return;
        }

        // 可执行程序：从压缩包临时目录直接运行可能缺少依赖文件 → 提示
        string ext = Path.GetExtension(full).ToLowerInvariant();
        if (ext is ".exe" or ".bat" or ".cmd" or ".com" or ".msi")
        {
            var r = MessageBox.Show(this,
                "该文件是从压缩包内直接提取到临时目录运行的。\n\n" +
                "它可能需要同目录下的其他文件（DLL、配置等）才能正常运行，" +
                "直接从压缩包运行可能报错或无法启动。\n\n建议先「解压到…」后再运行。是否仍要打开？",
                "Zipora — 运行提醒", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes)
            {
                Status("已取消打开该程序。");
                return;
            }
        }

        try
        {
            OpenExternalFile(full);
            Status($"已打开 {Path.GetFileName(full)}（来自压缩包临时目录）。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开文件失败：" + ex.Message, "Zipora",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>用系统默认程序打开外部文件（ShellExecute）。</summary>
    private static void OpenExternalFile(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
        {
            UseShellExecute = true,
        });
    }

    // ---------- 打开压缩包 ----------

    private void FileList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        { _busyCts?.Cancel(); ResetPointerGesture(); e.Handled = true; return; }
        if (_busyCts != null) return;
        if (e.Key == Key.Back || (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt))
        { NavigateArchiveDirectory(GetParentDirectory(_currentDirectory)); e.Handled = true; }
        else if (e.Key == Key.Enter && FileList.SelectedItems.Count == 1)
        { CtxOpen_Click(sender, e); e.Handled = true; }
    }

    private async void OpenArchive_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "打开压缩包",
            Filter = "压缩包（含分卷首卷）|*.7z;*.zip;*.rar;*.tar;*.gz;*.tgz;*.bz2;*.xz;*.zst;*.lz;*.lzma;*.iso;*.cab;*.wim;*.zipx;*.jar;*.apk;*.001|所有文件|*.*",
        };
        if (_archivePath != null)
        {
            string? dir = Path.GetDirectoryName(_archivePath);
            if (dir != null && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) == true)
            await OpenArchiveAsync(dlg.FileName);
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_busyCts != null || _dragActive) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] files
            && files.Length > 0)
        {
            // 拖出解压产生的临时提取文件拖回窗口时，不作为压缩包打开
            string dropRoot = AppStorage.TempRoot + Path.DirectorySeparatorChar;
            if (files.All(f => f.StartsWith(dropRoot, StringComparison.OrdinalIgnoreCase)))
                return;
            await OpenArchiveAsync(files[0]);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _busyCts == null && !_dragActive && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_busyCts != null || _dragActive || _archivePath == null) return;
        int count = FileList.SelectedItems.Count;
        if (count == 0) UpdateArchiveSummaryStatus();
        else Status($"已选择 {count} 项 · 拖到目标文件夹即可解压");
    }

    private async Task StartArchiveDragAsync(IReadOnlyList<ArchiveEntry> selected, string? wrapper = null)
    {
        if (_archivePath == null || _busyCts != null || _dragActive || selected.Count == 0) return;
        // Capture all identity before entering the native modal loop.
        string archive = _archivePath;
        ArchiveSelectionPlan plan;
        try { plan = ArchiveSelectionPlan.Create(_entries.ToList(), selected, wrapper); }
        catch (Exception ex) { ShowNotice("无法拖放这些项目", ex.Message, NoticeKind.Warning); return; }
        if (!await EnsurePasswordForArchiveAsync()) return;
        // A password dialog consumes the original mouse gesture; do not start a phantom drop.
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        { Status("密码已准备好，请拖动选中的内容。"); return; }
        string password = _currentPassword ?? "";
        string? rawRoot = null;
        Task<string>? extraction = null;
        ArchiveDragDataObject? data = null;
        using var cancellation = new CancellationTokenSource();
        _busyCts = cancellation;
        _dragActive = true;
        SetWorkEnabled(false);
        CancelButton.Visibility = Visibility.Visible;
        try
        {
            async Task<string> Prepare()
            {
                cancellation.Token.ThrowIfCancellationRequested();
                rawRoot = _temporary.CreateDirectory("drag");
                if (!TryPrepareExtractionSpace(rawRoot, selected, true, out var guard, cleanupOnStop: true))
                    throw new OperationCanceledException("临时空间不足，已取消拖放。", cancellation.Token);
                Status($"正在提取 {plan.RootCount} 项，请保持目标窗口打开…");
                Progress.Visibility = Visibility.Visible;
                Progress.IsIndeterminate = true;
                _lastProgressPct = -1;
                var progress = new Progress<double>(p => SetProgress(p, "解压"));
                var result = await _engine.ExtractSelectedAsync(archive, password, rawRoot,
                    plan.ExtractionPaths, progress, cancellation.Token, guard);
                if (result.Cancelled) throw new OperationCanceledException(cancellation.Token);
                if (!result.Success) throw new IOException(result.Message);
                cancellation.Token.ThrowIfCancellationRequested();
                plan.ValidateExtractedFiles(rawRoot);
                Progress.IsIndeterminate = true;
                Status("内容已提取，正在交给目标文件夹…");
                return rawRoot;
            }

            data = new ArchiveDragDataObject(plan, () => extraction ??= Prepare(), cancellation.Token);
            bool nativeImage = InitializeNativeDragImage(data, selected.All(x => x.IsDir));
            ShowDragPreview(selected);
            Status($"正在拖动 {plan.RootCount} 项 · 松开复制 · Esc 取消");
            var source = new ArchiveDropSource(cancellation.Token, effect =>
            {
                // Explorer renders the Shell image; elsewhere keep a click-through fallback.
                DragPreviewPopup.IsOpen = !nativeImage || effect == 0;
                UpdateDragPreviewPosition();
            });
            int result = ArchiveDropSource.DoDragDrop(data, source, 1, out uint effect);
            DragPreviewPopup.IsOpen = false;
            if (data.IsAsyncOperation)
            {
                Status("资源管理器正在接收内容，可点击取消停止提取…");
                var completion = await data.Completion.WaitAsync(cancellation.Token);
                result = completion.Result;
                effect = completion.Effect;
            }
            if (data.Failure != null) throw data.Failure;
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            Status(cancellation.IsCancellationRequested || result == 0x40101
                ? "拖放已取消。"
                : (effect & 1) != 0 ? "已交给目标文件夹；复制结果请查看资源管理器。"
                : "目标未接收内容。请拖到资源管理器文件夹，或使用「解压到…」。");
        }
        catch (OperationCanceledException) { Status("拖放已取消。"); }
        catch (Exception ex)
        {
            Status("拖放未完成：" + ex.Message);
            ShowNotice("拖放未完成", ex.Message + "\n\n也可使用工具栏「解压到…」选择目标目录。", NoticeKind.Warning);
        }
        finally
        {
            cancellation.Cancel();
            // Ensure 7-Zip has really exited before deleting its output.
            if (extraction != null) { try { await extraction; } catch { } }
            DragPreviewPopup.IsOpen = false;
            if (rawRoot != null) _temporary.Remove(rawRoot);
            _busyCts = null;
            _dragActive = false;
            HideProgress();
            CancelButton.Visibility = Visibility.Collapsed;
            SetWorkEnabled(true);
            ResetPointerGesture();
            ResetTreeDragGesture();
            if (_closeWhenIdle) Close();
        }
    }

    private void SetWorkEnabled(bool enabled)
    {
        ToolbarCard.IsEnabled = enabled;
        FileListCard.IsEnabled = enabled;
        // StatusCard remains enabled so Cancel is usable.
    }

    // ---------- 拖出解压 + 框选（选中条目拖出窗口即解压到目标位置） ----------

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Preview 事件也会经过 ListView 本身；若起点是滚动条/滑块，绝不能把它当作
        // 列表空白区来启动框选，否则拖动滑块会被框选逻辑抢走。
        if (_busyCts != null || e.ClickCount > 1 || IsInsideScrollBar(e.OriginalSource as DependencyObject) ||
            FindVisualAncestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject) != null)
        {
            ResetPointerGesture();
            return;
        }

        var pos = e.GetPosition(FileList);
        _mouseDownPos = pos;
        _mouseDownItem = null;
        _clickPending = false;
        _dragThresholdReached = false;
        _dragOutStarted = false;

        var item = GetItemAt(pos);
        if (item != null)
        {
            ModifierKeys modifiers = Keyboard.Modifiers;
            bool inMultiSelection =
                FileList.SelectedItems.Count > 1 && FileList.SelectedItems.Contains(item);

            // Ctrl / Shift 始终由 ListView 原生处理：切换单项或扩展范围。
            if (modifiers == ModifierKeys.None && inMultiSelection)
            {
                // 普通按在多选集合内：先保留集合供可能的拖出；若只是单击再塌缩。
                _dragCandidate = true;
                _clickPending = true;
                _mouseDownItem = item;
                _marqueeActive = false;
                HideMarquee();
                e.Handled = true;
            }
            else
            {
                // 无修饰键可开始拖出；带 Ctrl/Shift 的手势只服务于选择。
                _dragCandidate = modifiers == ModifierKeys.None;
                _marqueeActive = false;
                HideMarquee();

                // Native selection preserves keyboard focus and Shift range anchors.
            }
        }
        else
        {
            // 空白处按住拖动 = 框选；普通单击空白 = 取消选择。
            _dragCandidate = false;
            BeginMarquee(pos);
            // 选择完全由 BeginMarquee 按修饰键规则处理，阻止 ListView 再次改写它。
            e.Handled = true;
        }
    }

    private async void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_marqueeActive)
        {
            UpdateMarquee(e.GetPosition(FileList));
            return;
        }

        if (!_dragCandidate) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetPointerGesture();
            return;
        }
        var pos = e.GetPosition(FileList);
        if (!_dragThresholdReached &&
            Math.Abs(pos.X - _mouseDownPos.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _mouseDownPos.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragThresholdReached = true;


        // Native dragging starts at the normal threshold; extraction is deferred until requested.
        _clickPending = false;
        _dragCandidate = false;
        _dragOutStarted = true;
        if (Mouse.Captured == FileList) Mouse.Capture(null);
        e.Handled = true;
        await StartArchiveDragAsync(FileList.SelectedItems.Cast<ArchiveEntry>().ToList());
    }

    private void FileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_marqueeActive)
        {
            _marqueeActive = false;
            HideMarquee();
            if (Mouse.Captured == FileList) Mouse.Capture(null);
            e.Handled = true;
        }

        // 多选集合内的普通单击（未移动）→ Windows 资源管理器式塌缩为单选。
        if (_clickPending && !_dragThresholdReached && _mouseDownItem != null)
        {
            FileList.SelectedItems.Clear();
            FileList.SelectedItems.Add(_mouseDownItem);
            e.Handled = true;
        }

        if (!_dragOutStarted) ResetPointerGesture();
    }

    private void BeginMarquee(Point start)
    {
        _marqueeActive = true;
        _marqueeStart = start;
        _marqueeModifiers = Keyboard.Modifiers;
        _marqueeInitialSelection.Clear();
        foreach (var item in FileList.SelectedItems) _marqueeInitialSelection.Add(item);

        // 普通空白点击立即取消选择；Ctrl/Shift 框选保留原有集合作基准。
        if (!_marqueeModifiers.HasFlag(ModifierKeys.Control) &&
            !_marqueeModifiers.HasFlag(ModifierKeys.Shift))
        {
            FileList.SelectedItems.Clear();
        }

        MarqueeCanvas.Visibility = Visibility.Visible;
        Mouse.Capture(FileList);
        UpdateMarquee(start);
    }

    private void UpdateMarquee(Point currentPoint)
    {
        double x = Math.Min(_marqueeStart.X, currentPoint.X);
        double y = Math.Min(_marqueeStart.Y, currentPoint.Y);
        double w = Math.Abs(currentPoint.X - _marqueeStart.X);
        double h = Math.Abs(currentPoint.Y - _marqueeStart.Y);
        var band = new Rect(x, y, w, h);

        MarqueeBox.Width = w;
        MarqueeBox.Height = h;
        Canvas.SetLeft(MarqueeBox, x);
        Canvas.SetTop(MarqueeBox, y);

        // 资源管理器行为：普通框选替换；Shift 追加；Ctrl 对框住的项切换选中状态。
        bool toggle = _marqueeModifiers.HasFlag(ModifierKeys.Control);
        bool append = _marqueeModifiers.HasFlag(ModifierKeys.Shift);
        var newSel = new HashSet<object>();
        if (toggle || append)
        {
            foreach (var it in _marqueeInitialSelection) newSel.Add(it);
        }

        int count = FileList.Items.Count;
        for (int i = 0; i < count; i++)
        {
            var r = GetItemBounds(i);
            if (r.IsEmpty) continue;
            if (!band.IntersectsWith(r) && !band.Contains(r) && !r.Contains(band)) continue;

            object listItem = FileList.Items[i];
            if (toggle && _marqueeInitialSelection.Contains(listItem))
                newSel.Remove(listItem);
            else
                newSel.Add(listItem);
        }

        var currentSelection = new HashSet<object>(FileList.SelectedItems.Cast<object>());
        if (!newSel.SetEquals(currentSelection))
        {
            FileList.SelectedItems.Clear();
            foreach (var it in newSel) FileList.SelectedItems.Add(it);
        }
    }

    private void HideMarquee() => MarqueeCanvas.Visibility = Visibility.Collapsed;

    private bool IsPointerOutsideWindow(Point position) =>
        position.X < 0 || position.Y < 0 || position.X > ActualWidth || position.Y > ActualHeight;

    private void ResetPointerGesture()
    {
        _marqueeActive = false;
        HideMarquee();
        _dragCandidate = false;
        _dragThresholdReached = false;
        _clickPending = false;
        _mouseDownItem = null;
        if (Mouse.Captured == FileList) Mouse.Capture(null);
    }

    private Rect GetItemBounds(int index)
    {
        var container = FileList.ItemContainerGenerator.ContainerFromIndex(index) as ListViewItem;
        if (container == null || !container.IsVisible) return Rect.Empty;
        var tl = container.TranslatePoint(new Point(0, 0), FileList);
        return new Rect(tl, new Size(container.ActualWidth, container.ActualHeight));
    }

    private ArchiveEntry? GetItemAt(Point p)
    {
        var hit = FileList.InputHitTest(p) as DependencyObject;
        return FindVisualAncestor<ListViewItem>(hit)?.DataContext as ArchiveEntry;
    }

    private static bool IsInsideScrollBar(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is ScrollBar) return true;
            try { element = VisualTreeHelper.GetParent(element); }
            catch (InvalidOperationException) { return false; }
        }
        return false;
    }

    private void ShowDragPreview(IReadOnlyList<ArchiveEntry> selected)
    {
        DragPreviewIcon.Symbol = selected.All(x => x.IsDir)
            ? Wpf.Ui.Controls.SymbolRegular.Folder20 : Wpf.Ui.Controls.SymbolRegular.Document20;
        DragPreviewPopup.IsOpen = true;
        UpdateDragPreviewPosition();
    }

    private void UpdateDragPreviewPosition()
    {
        if (!DragPreviewPopup.IsOpen || !GetCursorPos(out var point)) return;
        if (PresentationSource.FromVisual(DragPreviewPopup.Child) is not System.Windows.Interop.HwndSource source) return;
        // Physical screen coordinates track the pointer across monitors and DPI scales.
        var style = GetWindowLongPtr(source.Handle, -20);
        SetWindowLongPtr(source.Handle, -20, new IntPtr(style.ToInt64() | 0x20 | 0x08000000));
        SetWindowPos(source.Handle, new IntPtr(-1), point.X + 18, point.Y + 20, 0, 0, 0x0011);
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    /// <summary>为跨应用拖放提供 Shell 原生拖放图像，让文件/文件夹图标随鼠标移动。</summary>
    private static bool InitializeNativeDragImage(ComDataObject data, bool isFolder)
    {
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            bitmap = CreateDragGlyphBitmap(isFolder);
            if (bitmap == IntPtr.Zero) return false;
            var dragImage = new ShDragImage
            {
                Size = new NativeSize { Width = 32, Height = 32 },
                Offset = new NativePoint { X = 10, Y = 10 },
                Bitmap = bitmap,
                ColorKey = unchecked((int)0xFFFFFFFF),
            };
            var helper = (IDragSourceHelper)new DragDropHelper();
            try { helper.InitializeFromBitmap(ref dragImage, data); }
            finally { Marshal.ReleaseComObject(helper); }
            return true;
        }
        catch
        {
            return false; // A failure in visual enhancement must not break extraction.
        }
        finally
        {
            DeleteObject(bitmap);
        }
    }

    /// <summary>创建带透明背景的 32px 文件夹/文件图标位图，供 Shell 拖放使用。</summary>
    private static IntPtr CreateDragGlyphBitmap(bool isFolder)
    {
        const int size = 32;
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = size,
                Height = -size, // 顶部朝下，便于按屏幕坐标绘制
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            },
        };
        IntPtr bitmap = CreateDibSection(IntPtr.Zero, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;

        var pixels = new int[size * size];
        int color = isFolder ? unchecked((int)0xFFFFC84A) : unchecked((int)0xFF4F93E8);
        int outline = isFolder ? unchecked((int)0xFFD99B20) : unchecked((int)0xFF236CC4);
        int left = 4, right = 28, top = isFolder ? 10 : 3, bottom = 27;
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
                pixels[y * size + x] = x == left || x == right || y == top || y == bottom ? outline : color;

        if (isFolder)
        {
            for (int y = 6; y < 11; y++)
                for (int x = 6; x < 16; x++) pixels[y * size + x] = outline;
        }
        else
        {
            for (int y = 9; y < 11; y++)
                for (int x = 8; x < 24; x++) pixels[y * size + x] = unchecked((int)0xFFFFFFFF);
            for (int y = 15; y < 17; y++)
                for (int x = 8; x < 24; x++) pixels[y * size + x] = unchecked((int)0xFFFFFFFF);
        }
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        return bitmap;
    }

    [ComImport, Guid("DE5BF786-477A-11D2-839D-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDragSourceHelper
    {
        void InitializeFromBitmap(ref ShDragImage dragImage, [MarshalAs(UnmanagedType.Interface)] ComDataObject dataObject);
        void InitializeFromWindow(IntPtr window, ref NativePoint point, [MarshalAs(UnmanagedType.Interface)] ComDataObject dataObject);
    }

    [ComImport, Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
    private class DragDropHelper;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width; public int Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShDragImage
    {
        public NativeSize Size;
        public NativePoint Offset;
        public IntPtr Bitmap;
        public int ColorKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }

    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection", SetLastError = true)]
    private static extern IntPtr CreateDibSection(IntPtr deviceContext, ref BitmapInfo bitmapInfo,
        uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    private static string GetUniqueDirectoryPath(string parent, string name)
    {
        string candidate = Path.Combine(parent, name);
        for (int index = 2; Directory.Exists(candidate) || File.Exists(candidate); index++)
            candidate = Path.Combine(parent, $"{name} ({index})");
        return candidate;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T match) return match;
            element = element is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(element);
        }
        return null;
    }



    private Task OpenArchiveAsync(string path) => RunBusyAsync(async ct =>
    {
        ResetArchiveState();
        _archivePath = path;
        Title = $"{BaseTitle} — {Path.GetFileName(path)}";
        TitleBar.Title = $"{BaseTitle} — {Path.GetFileName(path)}";
        ShowSkeleton();

        Status("正在计算文件 MD5…");
        _lastProgressPct = -1;
        _md5 = await HashHelper.ComputeMd5Async(path,
            new Progress<double>(p => SetProgress(p, "MD5")), ct);
        HideProgress();
        _fileSize = new FileInfo(path).Length;
        Status($"MD5  {_md5}　（{ArchiveEntry.FormatSize(_fileSize)}）");

        Status("正在读取压缩包目录…");
        var list = await Task.Run(() => _engine.List(path, null, ct), ct);

        switch (list.Status)
        {
            case ArchiveListStatus.Ok:
                foreach (var en in list.Entries) _entries.Add(en);
                _archiveInfo = list.Info;
                _headerEncrypted = false;
                _needsPassword = list.Entries.Any(x => x.Encrypted);
                break;

            case ArchiveListStatus.WrongPassword:
                _headerEncrypted = true;
                _needsPassword = true;
                break;

            default:
                HideSkeleton();
                MessageBox.Show(this, list.ErrorMessage, "Zipora",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ReturnAfterOpenFailure();
                return;
        }

        // 未加密文件名时可以立即建立目录树；加密文件名需等待密码后的 relist。
        if (!_headerEncrypted)
            InitializeArchiveBrowser();

        if (!_headerEncrypted && !ConfirmHighRiskArchive())
        {
            HideSkeleton();
            ReturnAfterOpenFailure("已取消打开高风险压缩包。");
            return;
        }

        if (!_needsPassword)
        {
            HideSkeleton();
            UpdateArchiveSummaryStatus();
            RefreshListVisualState(animateIn: true);
            DetailsButton.Visibility = Visibility.Visible;
            IntegrityButton.Visibility = Visibility.Visible;
            ExtractButton.Visibility = Visibility.Visible;
            return;
        }

        Status(_headerEncrypted ? "压缩包已加密（文件名也加密）。" : "压缩包内容已加密。");

        // 密码对话框是用户交互，不应继续显示读取中的骨架占位；深色主题下会尤其显得杂乱。
        HideSkeleton();

        string? pwd = await ResolvePasswordAsync(ct);
        if (pwd == null)
        {
            HideSkeleton();
            if (_headerEncrypted)
            {
                ReturnAfterOpenFailure("未提供密码，已放弃打开该压缩包。");
            }
            else
            {
                Status("未提供密码，加密内容无法解压。");
                DetailsButton.Visibility = Visibility.Visible;
                IntegrityButton.Visibility = Visibility.Visible;
            ExtractButton.Visibility = Visibility.Visible;
            }
            return;
        }

        _currentPassword = pwd;

        if (_headerEncrypted)
        {
            Status("正在用密码读取文件列表…");
            var relist = await Task.Run(() => _engine.List(path, pwd, ct), ct);
            if (relist.Status != ArchiveListStatus.Ok)
            {
                HideSkeleton();
                MessageBox.Show(this, "密码验证通过，但读取文件列表失败：" + relist.ErrorMessage,
                    "Zipora", MessageBoxButton.OK, MessageBoxImage.Error);
                ReturnAfterOpenFailure();
                return;
            }
            foreach (var en in relist.Entries) _entries.Add(en);
            _archiveInfo = relist.Info;
            _headerEncrypted = false;
            if (!ConfirmHighRiskArchive())
            {
                HideSkeleton();
                ReturnAfterOpenFailure("已取消打开高风险压缩包。");
                return;
            }
            InitializeArchiveBrowser();
        }

        HideSkeleton();
        RefreshListVisualState(animateIn: true);
        DetailsButton.Visibility = Visibility.Visible;
        IntegrityButton.Visibility = Visibility.Visible;
            ExtractButton.Visibility = Visibility.Visible;
        string source = _fromCrack ? "字典破解" : _passwordFromUser ? "手动输入" : "来自云端";
        _fromCrack = false;
        UpdateArchiveSummaryStatus($"密码已就绪（{source}）");
    });

    // ---------- 密码解析：手动输入（云端查询仅由用户点按钮触发） ----------

    private async Task<string?> ResolvePasswordAsync(CancellationToken ct)
    {
        // 字典破解命中：直接用破解密码（消费后清空）。破解获得的密码同样参与解压后的贡献上传。
        if (_prefillPassword != null)
        {
            string cracked = _prefillPassword;
            _prefillPassword = null;
            Status("正在用破解到的密码验证…");
            var pre = await Task.Run(() => _engine.TestPassword(_archivePath!, cracked, ct), ct);
            if (pre == PasswordTestResult.Ok)
            {
                _passwordFromUser = true; // 用户主动破解获得，验证正确即允许贡献上传
                _fromCrack = true;
                if (_md5 != null) ContributePasswordInBackground(cracked, _md5, _fileSize);
                return cracked;
            }
        }

        string info = "请手动输入密码；如需云端查询，可点击「云端查询」按钮。";

        while (true)
        {
            var dlg = new PasswordDialog(
                Path.GetFileName(_archivePath!), info,
                cloudConfigured: _keyFetch.IsConfigured)
            { Owner = this };

            dlg.CloudPasswordFinder = (token, report) => FetchCloudPasswordAsync(token, report);

            string pwd;
            if (dlg.ShowDialog() != true)
            {
                // 用户点了「字典破解」：打开破解窗口，命中后回填密码继续验证
                if (dlg.CrackRequested)
                {
                    var crack = await OpenDictionaryCrackAsync(_archivePath);
                    if (crack == null)
                    {
                        info = "字典破解未成功，请手动输入密码。";
                        continue;
                    }
                    pwd = crack.Value.Password;
                    _passwordFromUser = true;
                    _fromCrack = true;
                }
                else
                {
                    return null;
                }
            }
            else
            {
                pwd = dlg.Password!;
            }

            Status("正在验证密码…");
            var test = await Task.Run(() => _engine.TestPassword(_archivePath!, pwd, ct), ct);
            switch (test)
            {
                case PasswordTestResult.Ok:
                    _passwordFromUser = true;
                    if (_md5 != null) ContributePasswordInBackground(pwd, _md5, _fileSize);
                    return pwd;

                case PasswordTestResult.WrongPassword:
                    MessageBox.Show(this, "密码错误，请重试。", "Zipora",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    info = "上一次输入的密码不正确，请重试。";
                    continue;

                default:
                    MessageBox.Show(this, "验证密码时出错，压缩包可能已损坏。", "Zipora",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    continue;
            }
        }
    }

    /// <summary>
    /// 确保当前已打开压缩包的密码可用：需要密码且未缓存时弹标准密码框（含云端查询/字典破解），
    /// 验证正确后缓存到 _currentPassword。返回 true 表示密码已就绪（或无需密码）。
    /// </summary>
    private async Task<bool> EnsurePasswordForArchiveAsync(CancellationToken ct = default)
    {
        if (_archivePath == null) return false;
        if (!_needsPassword || _currentPassword != null) return true;

        string? pwd = await ResolvePasswordAsync(ct);
        if (pwd == null) return false;
        _currentPassword = pwd;
        return true;
    }

    /// <summary>密码验证正确后在后台匿名上传云端（不阻塞界面、不显示状态提醒）。</summary>
    private void ContributePasswordInBackground(string pwd, string md5, long size)
    {
        if (!AuthStore.Load().AutoContributePassword) return;
        if (!_keyFetch.IsConfigured) return;
        if (string.IsNullOrEmpty(md5)) return;

        var (ok, _) = _rateLimiter.TryContribute();
        if (!ok) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _keyFetch.ContributeAsync(md5, size, pwd, CancellationToken.None);
            }
            catch
            {
                // 后台上传失败静默，不打扰用户
            }
        });
    }

    /// <summary>向云端查询密码并验证；成功返回密码，失败/无结果返回 null。</summary>
    private async Task<string?> FetchCloudPasswordAsync(CancellationToken ct, Action<string> report)
    {
        if (_md5 == null || _archivePath == null)
            return null;
        if (!_keyFetch.IsConfigured) return null;

        // 本地限流：每日 10 次查询 + 最小请求间隔，超限不发请求
        var (ok, limitMsg) = _rateLimiter.TryQuery();
        if (!ok)
        {
            report(limitMsg);
            return null;
        }

        report("正在向云端查询密码…");
        FetchResult r;
        try
        {
            r = await _keyFetch.FetchPasswordAsync(_md5, _fileSize, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            report($"云端查询失败：{ex.Message}");
            return null;
        }

        switch (r.Status)
        {
            case FetchStatus.Ok:
                report("云端找到密码，正在验证…");
                var test = await Task.Run(() => _engine.TestPassword(_archivePath, r.Password!, ct), ct);
                if (test == PasswordTestResult.Ok)
                {
                    report("云端密码验证成功。");
                    return r.Password;
                }
                report("云端返回的密码不正确。");
                return null;

            case FetchStatus.NotFound:
                report("云端未收录该文件。");
                return null;

            case FetchStatus.QuotaExceeded:
                report(r.Message ?? "云端今日查询额度已用完，明日恢复。");
                return null;

            default:
                report(r.Message ?? "云端查询失败。");
                return null;
        }
    }

    private void CloudSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_busyCts != null)
        {
            MessageBox.Show(this, "正在处理任务，请稍候。", "Zipora",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new CloudSearchDialog(_deviceFp, _md5) { Owner = this };
        dlg.ShowDialog();
    }

    private void Credits_Click(object sender, RoutedEventArgs e)
    {
        new CreditsDialog { Owner = this }.ShowDialog();
    }

    // ---------- 新建压缩包 ----------

    /// <summary>右键「新建压缩包」触发：用选中文件打开压缩对话框。</summary>
    private async void OpenCreateWithFiles(IReadOnlyList<string> files)
    {
        await CreateArchiveWithSourcesAsync(files);
    }

    private async void CreateArchive_Click(object sender, RoutedEventArgs e)
    {
        await CreateArchiveWithSourcesAsync(null);
    }

    /// <summary>打开新建压缩包对话框并执行创建；preselect 非空时预填来源（右键「新建压缩包」）。</summary>
    private async Task CreateArchiveWithSourcesAsync(IReadOnlyList<string>? preselect)
    {
        if (_busyCts != null) return;

        var dlg = new CreateArchiveDialog(_keyFetch.IsConfigured) { Owner = this };
        if (preselect != null)
            foreach (var f in preselect)
                if (!dlg.Sources.Contains(f)) dlg.Sources.Add(f);
        if (dlg.ShowDialog() != true) return;

        string outFile = dlg.OutFile!;
        bool ok = false;

        await RunBusyAsync(async ct =>
        {
            Status($"正在创建压缩包「{Path.GetFileName(outFile)}」…");
            _lastProgressPct = -1;
            var progress = new Progress<double>(p => SetProgress(p, "压缩"));

            var result = await _engine.CreateAsync(
                dlg.Sources.ToList(), outFile, dlg.Format, dlg.Password, dlg.Level, dlg.EncryptHeaders,
                dlg.VolumeSizeBytes,
                progress, ct);
            HideProgress();

            if (result.Cancelled)
            {
                Status("创建已取消。");
                return;
            }
            if (!result.Success)
            {
                MessageBox.Show(this, result.Message, "Zipora",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Status("创建失败。");
                return;
            }
            ok = true;
            Status(result.Message + " 位置：" + (dlg.VolumeSizeBytes.HasValue ? outFile + ".001" : outFile));
        });

        // 创建成功后仍用 7-Zip 回读验证一次密码；仅验证通过才允许上传。
        // 这样服务端收到的每条自动贡献都已在本机对应压缩包上实际验证。
        if (ok && dlg.UploadToCloud && dlg.Password != null && _keyFetch.IsConfigured)
        {
            string outPath = dlg.VolumeSizeBytes.HasValue ? outFile + ".001" : outFile;
            string pwd = dlg.Password;
            _ = Task.Run(async () =>
            {
                try
                {
                    var verification = _engine.TestPassword(outPath, pwd, CancellationToken.None);
                    if (verification != PasswordTestResult.Ok) return;

                    var (limitOk, _) = _rateLimiter.TryContribute();
                    if (!limitOk) return;

                    string md5 = await HashHelper.ComputeMd5Async(outPath, null, CancellationToken.None);
                    long size = new FileInfo(outPath).Length;
                    await _keyFetch.ContributeAsync(md5, size, pwd, CancellationToken.None);
                }
                catch
                {
                    // 后台上传失败静默，不打扰用户
                }
            });
        }

        // 询问放在 busy 区外，避免点「是」时被任务占用拦截
        if (ok &&
            MessageBox.Show(this, "压缩包创建完成。\n\n是否立即打开查看？", "Zipora",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            await OpenArchiveAsync(dlg.VolumeSizeBytes.HasValue ? outFile + ".001" : outFile);
        }
    }

    // ---------- 解压 ----------

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_archivePath == null || _busyCts != null) return;
        if (!await EnsurePasswordForArchiveAsync()) return;

        string archiveName = Path.GetFileName(_archivePath);
        string nameNoExt = Path.GetFileNameWithoutExtension(_archivePath);

        var dlg = new OpenFolderDialog
        {
            Title = "选择解压位置（将在所选文件夹内创建「" + nameNoExt + "」子文件夹）",
        };
        if (Directory.Exists(_authStore.LastExtractDir))
            dlg.InitialDirectory = _authStore.LastExtractDir;
        else
        {
            string? parent = Path.GetDirectoryName(_archivePath);
            if (parent != null && Directory.Exists(parent)) dlg.InitialDirectory = parent;
        }
        if (dlg.ShowDialog(this) != true) return;

        _authStore.LastExtractDir = dlg.FolderName;
        var latestPreferences = AuthStore.Load();
        latestPreferences.LastExtractDir = _authStore.LastExtractDir;
        latestPreferences.Save();

        await ExtractSelectionToRootAsync(dlg.FolderName, ArchiveSelectionPlan.ArchiveRoots(_entries));
    }

    // ---------- 列表右键：解压到 当前目录 / 桌面 / 指定目录 ----------

    private void FileList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // 右键落在列表条目上才弹菜单；列标题行/滚动条/空白处不弹
        var pos = Mouse.GetPosition(FileList);
        if (GetItemAt(pos) == null)
        {
            e.Handled = true;
            return;
        }

        var clicked = GetItemAt(pos);
        if (clicked != null && !FileList.SelectedItems.Contains(clicked))
        { FileList.SelectedItems.Clear(); FileList.SelectedItems.Add(clicked); }
        bool hasSelection = FileList.SelectedItems.Count > 0 && _busyCts == null;
        CtxOpen.IsEnabled = hasSelection && FileList.SelectedItems.Count == 1;
        CtxExtractCurrent.IsEnabled = hasSelection;
        CtxExtractDesktop.IsEnabled = hasSelection;
        CtxExtractPick.IsEnabled = hasSelection;
    }

    private async void CtxExtractCurrent_Click(object sender, RoutedEventArgs e)
    {
        string root = _archivePath != null ? Path.GetDirectoryName(_archivePath) ?? DesktopDir : DesktopDir;
        await ExtractToRootAsync(root);
    }

    private async void CtxExtractDesktop_Click(object sender, RoutedEventArgs e)
    {
        await ExtractToRootAsync(DesktopDir);
    }

    private async void CtxExtractPick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择解压位置（将在所选文件夹内创建「压缩包名」子文件夹）",
        };
        if (Directory.Exists(_authStore.LastExtractDir))
            dlg.InitialDirectory = _authStore.LastExtractDir;
        else if (_archivePath != null)
        {
            string? parent = Path.GetDirectoryName(_archivePath);
            if (parent != null && Directory.Exists(parent)) dlg.InitialDirectory = parent;
        }
        if (dlg.ShowDialog(this) != true) return;
        _authStore.LastExtractDir = dlg.FolderName;
        var latestPreferences = AuthStore.Load();
        latestPreferences.LastExtractDir = _authStore.LastExtractDir;
        latestPreferences.Save();
        await ExtractToRootAsync(dlg.FolderName);
    }

    /// <summary>右键解压：把选中的压缩包内条目解压到目标目录（「压缩包名」子文件夹）。</summary>
    private Task ExtractToRootAsync(string rootDir)
        => ExtractSelectionToRootAsync(rootDir, FileList.SelectedItems.Cast<ArchiveEntry>().ToList());

    private async Task ExtractSelectionToRootAsync(string rootDir, IReadOnlyList<ArchiveEntry> selected)
    {
        if (_busyCts != null || _archivePath == null || selected.Count == 0) return;
        string archive = _archivePath;
        ArchiveSelectionPlan plan;
        try { plan = ArchiveSelectionPlan.Create(_entries.ToList(), selected); }
        catch (Exception ex) { ShowNotice("无法解压", ex.Message, NoticeKind.Warning); return; }
        string staging = Path.Combine(rootDir, ".zipora-extract-" + Guid.NewGuid().ToString("N"));
        if (!TryPrepareExtractionSpace(staging, selected, true, out var guard, cleanupOnStop: true)) return;
        if (!await EnsurePasswordForArchiveAsync()) return;
        string password = _currentPassword ?? "";
        await RunBusyAsync(async ct =>
        {
            string raw = Path.Combine(staging, "raw");
            string ready = Path.Combine(staging, "ready");
            try
            {
                Status("正在解压所选内容…");
                Progress.Visibility = Visibility.Visible;
                Progress.IsIndeterminate = true;
                _lastProgressPct = -1;
                var result = await _engine.ExtractSelectedAsync(archive, password, raw,
                    plan.ExtractionPaths, new Progress<double>(p => SetProgress(p, "解压")), ct, guard);
                if (result.Cancelled) { Status("解压已取消，未生成目标文件夹。"); return; }
                if (!result.Success) throw new IOException(result.Message);
                ct.ThrowIfCancellationRequested();
                await Task.Run(() => plan.ExportTo(raw, ready), ct);
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileNameWithoutExtension(archive);
                string target = Path.Combine(rootDir, name);
                int suffix = 2;
                while (Path.Exists(target)) target = Path.Combine(rootDir, $"{name} ({suffix++})");
                Directory.Move(ready, target);
                Status($"解压完成。位置：{target}");
            }
            finally
            {
                // This is a fresh staging tree owned exclusively by this operation.
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                catch { Status("临时目录未能清理，请检查：" + staging); }
                HideProgress();
            }
        });
    }

    /// <summary>右键子菜单命令行解压（--extract-here/desktop/pick）：整包解压到对应位置。</summary>
    private async Task RunCliExtractAsync(string archive, string targetKind)
    {
        string root;
        switch (targetKind)
        {
            case App.CliTargetKind.Here:
                root = Path.GetDirectoryName(archive) ?? DesktopDir;
                break;
            case App.CliTargetKind.Desktop:
                root = DesktopDir;
                break;
            default: // Pick：弹选择框
                var dlg = new OpenFolderDialog
                {
                    Title = "选择解压位置（将在所选文件夹内创建「压缩包名」子文件夹）",
                };
                if (dlg.ShowDialog(this) != true)
                {
                    Status("已取消解压。");
                    return;
                }
                root = dlg.FolderName;
                break;
        }

        await ExtractSelectionToRootAsync(root, ArchiveSelectionPlan.ArchiveRoots(_entries));
    }

    /// <summary>打开设置窗口（含系统右键菜单开关）。</summary>
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        new Dialogs.SettingsDialog { Owner = this }.ShowDialog();
    }

    /// <summary>显示当前压缩包的格式、结构、安全信息与内容分类统计。</summary>
    private void ArchiveDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_archivePath == null) return;
        bool encrypted = _needsPassword || _headerEncrypted || _entries.Any(x => x.Encrypted);
        new ArchiveDetailsDialog(_archivePath, _archiveInfo, _entries.ToList(), encrypted)
        {
            Owner = this,
        }.ShowDialog();
    }

    /// <summary>使用 7-Zip 的 t 命令全量读取数据并校验 CRC，不会解压或改动原压缩包。</summary>
    private async void TestArchiveIntegrity_Click(object sender, RoutedEventArgs e)
    {
        if (_busyCts != null) return;
        if (_archivePath == null)
        {
            ShowNotice("尚未打开压缩包", "请先打开一个压缩包，再进行完整性校验。", NoticeKind.Info);
            return;
        }

        if (!await EnsurePasswordForArchiveAsync()) return;

        string archive = _archivePath;
        string password = _currentPassword ?? "";
        string archiveName = Path.GetFileName(archive);

        await RunBusyAsync(async ct =>
        {
            Status($"正在校验「{archiveName}」的完整性…");
            _lastProgressPct = -1;
            var progress = new Progress<double>(p => SetProgress(p, "校验"));
            var result = await _engine.TestArchiveAsync(archive, password, progress, ct);
            HideProgress();

            switch (result.Status)
            {
                case ArchiveTestStatus.Passed:
                    Status("压缩包完整性校验通过。");
                    ShowNotice("完整性校验通过", "已完成全量数据与 CRC 校验，压缩包没有发现错误。", NoticeKind.Success);
                    break;
                case ArchiveTestStatus.Warning:
                    Status("完整性校验完成，但发现警告。");
                    ShowNotice("完整性校验发现警告", result.Message, NoticeKind.Warning);
                    break;
                case ArchiveTestStatus.Cancelled:
                    Status("完整性校验已取消。");
                    break;
                default:
                    Status("完整性校验失败。");
                    ShowNotice("完整性校验失败", result.Message, NoticeKind.Error);
                    break;
            }
        });
    }

    // ---------- 更新检测 ----------

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        Status("正在检测更新…");
        var info = await UpdateChecker.CheckAsync();
        if (info == null)
        {
            Status("检查更新失败（网络异常或服务器不可用）。");
            ShowNotice("检查更新失败", "网络异常或更新服务器暂不可用，请稍后再试。", NoticeKind.Warning);
            return;
        }
        if (info.IsNewer)
        {
            PromptUpdate(info);
        }
        else
        {
            Status($"当前已是最新版本 v{AppInfo.Version}。");
            ShowNotice("已是最新版本", $"当前使用的是 Zipora v{AppInfo.Version}。", NoticeKind.Success);
        }
    }

    /// <summary>提示有新版本，用户确认后打开官网下载页。</summary>
    private void PromptUpdate(UpdateInfo info)
    {
        string notes = string.IsNullOrWhiteSpace(info.Notes) ? "" : $"\n\n更新说明：{info.Notes}";
        var dialog = new NoticeDialog("发现新版本",
            $"发现 Zipora v{info.LatestVersion}，当前版本为 v{AppInfo.Version}。{notes}\n\n是否前往下载页面？",
            NoticeKind.Update, "前往下载", "暂不") { Owner = this };
        if (dialog.ShowDialog() == true)
            UpdateChecker.OpenDownloadPage(info.DownloadUrl);
    }

    private void ShowNotice(string title, string message, NoticeKind kind)
        => new NoticeDialog(title, message, kind) { Owner = this }.ShowDialog();

    private bool ConfirmHighRiskArchive()
    {
        if (!_authStore.ArchiveBombProtectionEnabled || _archiveRiskPromptShown || _archivePath == null)
            return true;

        ArchiveRiskAssessment risk = ArchiveSafety.AssessArchive(_archivePath, _archiveInfo, _entries.ToList());
        if (!risk.IsHighRisk) return true;

        _archiveRiskPromptShown = true;
        HideSkeleton();
        string message =
            $"{risk.Reason}。\n\n" +
            $"压缩包大小：{ArchiveEntry.FormatSize(risk.ArchiveSize)}\n" +
            $"预计展开：{ArchiveEntry.FormatSize(risk.ExpandedSize)}\n" +
            $"压缩比：{risk.ExpansionRatio:N0} 倍\n\n" +
            "继续处理可能耗尽磁盘空间并导致系统失去响应。";
        var dialog = new NoticeDialog(
            "检测到极高风险压缩包", message, NoticeKind.Warning,
            "仍然打开", "取消打开", primaryIsDanger: true)
        { Owner = this };
        return dialog.ShowDialog() == true;
    }

    private bool TryPrepareExtractionSpace(
        string targetPath,
        IReadOnlyList<ArchiveEntry> selected,
        bool showNotification,
        out ExtractionSpaceGuard? guard,
        bool cleanupOnStop = false)
    {
        guard = null;
        if (!_authStore.ArchiveBombProtectionEnabled) return true;

        ExtractionSpaceAssessment assessment = ArchiveSafety.AssessSelectionSpace(
            targetPath, _entries.ToList(), selected);
        guard = assessment.Guard;
        if (guard != null && cleanupOnStop)
            guard = guard with { DeletePartialOutput = true };
        if (!assessment.IsInsufficient) return true;

        if (showNotification)
        {
            long usable = Math.Max(0, assessment.AvailableBytes - assessment.ReserveBytes);
            long shortage = Math.Max(0, assessment.RequiredBytes - usable);
            bool temporary = StorageLayout.Within(targetPath, AppStorage.TempRoot);
            string location = temporary ? "临时解压磁盘" : "目标磁盘";
            ShowNotice(
                temporary ? "临时解压空间不足" : "磁盘空间不足，已取消解压",
                $"{location}不足以安全完成所选内容的解压。\n\n" +
                $"预计需要：{ArchiveEntry.FormatSize(assessment.RequiredBytes)}\n" +
                $"当前可用：{ArchiveEntry.FormatSize(assessment.AvailableBytes)}\n" +
                $"安全保留：{ArchiveEntry.FormatSize(assessment.ReserveBytes)}\n" +
                $"至少还需：{ArchiveEntry.FormatSize(shortage)}",
                NoticeKind.Warning);
        }
        return false;
    }

    private void ShowRuntimeSpaceStoppedNotice(bool temporary)
    {
        ShowNotice(
            temporary ? "临时磁盘空间接近安全下限" : "磁盘空间接近安全下限",
            temporary
                ? "准备拖放内容时，临时磁盘的剩余空间降至安全下限。Zipora 已终止解压并清理本次创建的临时内容。"
                : "解压过程中，目标磁盘的剩余空间降至安全下限。Zipora 已终止解压并清理本次创建的暂存目录。",
            NoticeKind.Warning);
    }

    // ---------- 关于 / 退出 ----------

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutDialog { Owner = this }.ShowDialog();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>「更多」按钮：在按钮下方弹出菜单（右键同样可弹）。</summary>
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ButtonBase btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _busyCts?.Cancel();

    /// <summary>打开字典破解窗口（免费功能，无需激活）；命中并确认后返回 (压缩包, 密码)。</summary>
    private Task<(string Archive, string Password)?> OpenDictionaryCrackAsync(string? prefillArchive)
    {
        var dlg = new DictionaryCrackDialog(_engine, prefillArchive) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.CrackResult != null)
            return Task.FromResult<(string, string)?>(dlg.CrackResult);
        return Task.FromResult<(string, string)?>(null);
    }

    private async void DictionaryCrack_Click(object sender, RoutedEventArgs e)
    {
        var result = await OpenDictionaryCrackAsync(_archivePath);
        if (result != null)
        {
            _prefillPassword = result.Value.Password;
            await OpenArchiveAsync(result.Value.Archive);
        }
    }

    // ---------- 通用辅助 ----------

    private IKeyFetchClient CreateKeyFetchClient()
    {
        try
        {
            return new HttpKeyFetchClient(KeyFetchBaseUrl, _deviceFp);
        }
        catch (ArgumentException)
        {
            return NullKeyFetchClient.Instance;
        }
    }

    private void ResetArchiveState()
    {
        _archivePath = null;
        _archiveInfo = null;
        _md5 = null;
        _fileSize = 0;
        _currentPassword = null;
        _passwordFromUser = false;
        _needsPassword = false;
        _headerEncrypted = false;
        _archiveRiskPromptShown = false;
        _entries.Clear();
        _visibleEntries.Clear();
        _treeRoots.Clear();
        _directoryEntries.Clear();
        _currentDirectory = "";
        SetArchiveBrowserVisible(false);
        DetailsButton.Visibility = Visibility.Collapsed;
        IntegrityButton.Visibility = Visibility.Collapsed;
        ExtractButton.Visibility = Visibility.Collapsed;
        Title = BaseTitle;
        TitleBar.Title = BaseTitle;
    }

    /// <summary>打开压缩包失败/放弃后：若来自目录浏览则回到该目录，否则空白。</summary>
    private void ReturnAfterOpenFailure(string? status = null)
    {
        ResetArchiveState();
        ShowEmptyState(true);
        if (status != null) Status(status);
    }

    private void Status(string text) => StatusText.Text = text;

    /// <summary>显示当前压缩包摘要：项目数、原始大小和是否加密。</summary>
    private void UpdateArchiveSummaryStatus(string? prefix = null)
    {
        long totalSize = 0;
        foreach (var entry in _entries)
        {
            if (entry.IsDir || entry.Size <= 0) continue;
            totalSize = totalSize > long.MaxValue - entry.Size
                ? long.MaxValue
                : totalSize + entry.Size;
        }

        bool encrypted = _needsPassword || _headerEncrypted || _entries.Any(x => x.Encrypted);
        string summary = $"共 {_entries.Count} 项，{ArchiveEntry.FormatSize(totalSize)}，{(encrypted ? "已加密" : "未加密")}";
        Status(prefix is null ? summary : $"{prefix}。{summary}");
    }

    private void SetProgress(double pct, string label)
    {
        Progress.IsIndeterminate = false;
        int i = Math.Clamp((int)Math.Floor(pct), 0, 100);
        if (i == _lastProgressPct) return;
        _lastProgressPct = i;

        Progress.Visibility = Visibility.Visible;
        ProgressText.Visibility = Visibility.Visible;
        Progress.Value = i;
        ProgressText.Text = $"{label} {i}%";
    }

    private void HideProgress()
    {
        Progress.IsIndeterminate = false;
        Progress.Visibility = Visibility.Collapsed;
        ProgressText.Visibility = Visibility.Collapsed;
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> work)
    {
        if (_busyCts != null)
        {
            MessageBox.Show(this, "正在处理上一个任务，请稍候。", "Zipora",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _busyCts = new CancellationTokenSource();
        SetWorkEnabled(false);
        CancelButton.Visibility = Visibility.Visible;
        try
        {
            await work(_busyCts.Token);
        }
        catch (OperationCanceledException)
        {
            Status("已取消。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "发生错误：" + ex.Message, "Zipora",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Status("发生错误。");
        }
        finally
        {
            HideProgress();
            SetWorkEnabled(true);
            CancelButton.Visibility = Visibility.Collapsed;
            _busyCts.Dispose();
            _busyCts = null;
            if (_closeWhenIdle) Close();
            HideSkeleton(); // 兜底：任何路径都不残留骨架屏
        }
    }
}
