using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Zipora.Services;

namespace Zipora.Dialogs;

public partial class ArchiveDetailsDialog : Wpf.Ui.Controls.FluentWindow
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff",
        ".svg", ".ico", ".heic", ".heif", ".avif", ".raw", ".dng",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v",
        ".mpeg", ".mpg", ".ts", ".mts", ".m2ts", ".3gp",
    };

    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".dll", ".com", ".scr", ".bat",
        ".cmd", ".ps1", ".vbs", ".js", ".jar", ".apk", ".app", ".sh",
    };

    private readonly string _archivePath;
    private readonly CancellationTokenSource _sha256Cts = new();

    public ArchiveDetailsDialog(
        string archivePath,
        ArchiveInfo? info,
        IReadOnlyList<ArchiveEntry> entries,
        bool encrypted)
    {
        InitializeComponent();
        Backdrop.ApplyFromSetting(this);
        _archivePath = archivePath;

        var files = entries.Where(x => !x.IsDir).ToList();
        long unpackedSize = SumSizes(files.Select(x => x.Size));
        long archiveSize = ResolveArchiveSize(archivePath, info);

        ArchiveNameText.Text = Path.GetFileName(archivePath);
        SizeText.Text = ArchiveEntry.FormatSize(archiveSize);
        FileCountText.Text = files.Count.ToString("N0");
        FolderCountText.Text = CountFolders(entries).ToString("N0");
        FormatText.Text = ResolveFormat(archivePath, info);
        MethodText.Text = ResolveCompressionMethods(info, files);
        RatioText.Text = unpackedSize > 0 && archiveSize >= 0
            ? $"{Math.Round(archiveSize * 100d / unpackedSize):0}%"
            : "—";
        EncryptionText.Text = ResolveEncryption(info, files, encrypted);
        int volumeCount = Math.Max(1, info?.VolumeCount ?? 1);
        VolumesText.Text = volumeCount > 1 ? $"是（{volumeCount:N0} 卷）" : "否";
        SolidText.Text = info?.Solid == true ? "是" : "否";
        CrcText.Text = files.Any(x => !string.IsNullOrWhiteSpace(x.Crc)) ? "✓" : "—";
        ImageCountText.Text = CountByExtension(files, ImageExtensions).ToString("N0");
        VideoCountText.Text = CountByExtension(files, VideoExtensions).ToString("N0");
        ProgramCountText.Text = CountByExtension(files, ProgramExtensions).ToString("N0");

        Closed += (_, _) => _sha256Cts.Cancel();
    }

    private async void ComputeSha256_Click(object sender, RoutedEventArgs e)
    {
        ComputeSha256Button.IsEnabled = false;
        ComputeSha256Button.Content = "正在计算…";
        Sha256Progress.Value = 0;
        Sha256Progress.Visibility = Visibility.Visible;

        try
        {
            var progress = new Progress<double>(value => Sha256Progress.Value = value);
            string hash = await HashHelper.ComputeSha256Async(_archivePath, progress, _sha256Cts.Token);
            ComputeSha256Button.Visibility = Visibility.Collapsed;
            Sha256Text.Text = hash;
            Sha256Text.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            // 关闭详情窗口即取消计算，无需再提示。
        }
        catch (Exception ex)
        {
            ComputeSha256Button.IsEnabled = true;
            ComputeSha256Button.Content = "[重试]";
            MessageBox.Show(this, "无法计算 SHA256：" + ex.Message, "Zipora",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Sha256Progress.Visibility = Visibility.Collapsed;
        }
    }

    private static long ResolveArchiveSize(string archivePath, ArchiveInfo? info)
    {
        if (info?.TotalPhysicalSize >= 0) return info.TotalPhysicalSize;
        if (info?.PhysicalSize >= 0) return info.PhysicalSize;
        try { return new FileInfo(archivePath).Length; }
        catch { return -1; }
    }

    private static string ResolveFormat(string archivePath, ArchiveInfo? info)
    {
        string format = info?.Format?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(format) && !format.Equals("Split", StringComparison.OrdinalIgnoreCase))
            return format;

        string name = Path.GetFileName(archivePath);
        name = Regex.Replace(name, @"\.\d{3}$", "", RegexOptions.CultureInvariant);
        string extension = Path.GetExtension(name).TrimStart('.');
        return string.IsNullOrWhiteSpace(extension) ? "—" : extension;
    }

    private static string ResolveCompressionMethods(ArchiveInfo? info, IReadOnlyList<ArchiveEntry> files)
    {
        var result = new List<string>();
        AddMethods(result, info?.Method);
        foreach (var entry in files) AddMethods(result, entry.Method);
        return result.Count == 0 ? "—" : string.Join(" / ", result.Take(3));
    }

    private static void AddMethods(List<string> result, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (string token in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string method = token.Split(':', 2)[0];
            if (method.Contains("AES", StringComparison.OrdinalIgnoreCase) ||
                method.Equals("ZipCrypto", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!result.Contains(method, StringComparer.OrdinalIgnoreCase)) result.Add(method);
        }
    }

    private static string ResolveEncryption(ArchiveInfo? info, IReadOnlyList<ArchiveEntry> files, bool encrypted)
    {
        if (!encrypted) return "否";
        string methods = string.Join(' ', new[] { info?.Method ?? "" }.Concat(files.Select(x => x.Method)));
        if (methods.Contains("7zAES", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(methods, @"AES[-_ ]?256", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "AES-256";
        if (methods.Contains("AES", StringComparison.OrdinalIgnoreCase)) return "AES";
        if (methods.Contains("ZipCrypto", StringComparison.OrdinalIgnoreCase)) return "ZipCrypto";
        return "是";
    }

    private static int CountFolders(IEnumerable<ArchiveEntry> entries)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            string path = entry.Path.Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(path)) continue;
            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int directoryParts = entry.IsDir ? parts.Length : parts.Length - 1;
            for (int i = 1; i <= directoryParts; i++)
                folders.Add(string.Join('/', parts.Take(i)));
        }
        return folders.Count;
    }

    private static int CountByExtension(IEnumerable<ArchiveEntry> entries, HashSet<string> extensions)
        => entries.Count(x => extensions.Contains(Path.GetExtension(x.Path)));

    private static long SumSizes(IEnumerable<long> sizes)
    {
        long total = 0;
        foreach (long size in sizes)
        {
            if (size <= 0) continue;
            total = total > long.MaxValue - size ? long.MaxValue : total + size;
        }
        return total;
    }
}
