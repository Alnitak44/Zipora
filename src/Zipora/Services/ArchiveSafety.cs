using System.IO;

namespace Zipora.Services;

public sealed record ArchiveRiskAssessment(
    bool IsHighRisk,
    long ExpandedSize,
    long ArchiveSize,
    double ExpansionRatio,
    string Reason);

public sealed record ExtractionSpaceGuard(
    string TargetPath,
    long MinimumFreeBytes,
    bool DeletePartialOutput = false);

public sealed record ExtractionSpaceAssessment(
    bool CouldEvaluate,
    bool IsInsufficient,
    long RequiredBytes,
    long AvailableBytes,
    long ReserveBytes,
    bool HasUnknownSizes,
    ExtractionSpaceGuard? Guard);

/// <summary>只使用可信度足够高的组合规则做压缩炸弹提醒，并为所选内容解压估算磁盘空间。</summary>
public static class ArchiveSafety
{
    private const long GiB = 1024L * 1024 * 1024;
    private const long TiB = 1024L * GiB;

    public static ArchiveRiskAssessment AssessArchive(
        string archivePath, ArchiveInfo? info, IReadOnlyList<ArchiveEntry> entries)
    {
        var files = entries.Where(x => !x.IsDir).ToList();
        bool unknown = files.Any(x => x.Size < 0);
        long expanded = SumSaturated(files.Select(x => x.Size));
        long archiveSize = ResolveArchiveSize(archivePath, info);
        double ratio = !unknown && expanded > 0 && archiveSize > 0
            ? expanded / (double)archiveSize
            : 0;

        string reason = "";
        if (!unknown && expanded >= 100 * GiB && ratio >= 1_000)
            reason = "预计展开体积超过 100 GiB，且压缩比超过 1,000 倍";
        else if (!unknown && expanded >= TiB && ratio >= 100)
            reason = "预计展开体积超过 1 TiB，且压缩比超过 100 倍";
        else if (!unknown && expanded >= 10 * GiB && ratio >= 10_000)
            reason = "预计展开体积超过 10 GiB，且压缩比超过 10,000 倍";

        return new ArchiveRiskAssessment(reason.Length > 0, expanded, archiveSize, ratio, reason);
    }

    public static ExtractionSpaceAssessment AssessSelectionSpace(
        string targetPath,
        IReadOnlyList<ArchiveEntry> allEntries,
        IReadOnlyList<ArchiveEntry> selected)
    {
        var selectedFiles = ExpandSelection(allEntries, selected);
        bool unknown = selectedFiles.Any(x => x.Size < 0);
        long contentBytes = SumSaturated(selectedFiles.Select(x => x.Size));
        long percentOverhead = contentBytes / 100;
        long entryOverhead = MultiplySaturated(selectedFiles.Count, 4096);
        long overhead = Math.Max(percentOverhead, entryOverhead);
        long required = AddSaturated(contentBytes, overhead);

        try
        {
            string fullPath = Path.GetFullPath(targetPath);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                return new(false, false, required, 0, 0, unknown, null);

            var drive = new DriveInfo(root);
            long available = drive.AvailableFreeSpace;
            long twoPercent = drive.TotalSize / 50;
            long reserve = Math.Max(512L * 1024 * 1024, Math.Min(twoPercent, 5 * GiB));
            if (AppStorage.IsPortable && string.Equals(root, Path.GetPathRoot(AppStorage.Layout.Root), StringComparison.OrdinalIgnoreCase))
                reserve = Math.Clamp(drive.TotalSize / 100, 16L * 1024 * 1024, 256L * 1024 * 1024);
            long usable = Math.Max(0, available - reserve);
            // 大小未知时不做预判阻断；运行时仍以安全保留空间为硬下限。
            bool insufficient = !unknown && required > usable;
            return new(true, insufficient, required, available, reserve, unknown,
                new ExtractionSpaceGuard(fullPath, reserve));
        }
        catch
        {
            // 无法解析目标卷时不误拦截；调用方仍可按正常流程交给操作系统处理。
            return new(false, false, required, 0, 0, unknown, null);
        }
    }

    private static List<ArchiveEntry> ExpandSelection(
        IReadOnlyList<ArchiveEntry> allEntries, IReadOnlyList<ArchiveEntry> selected)
    {
        var exactFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new List<string>();
        foreach (var entry in selected)
        {
            string path = Normalize(entry.Path);
            if (entry.IsDir) directories.Add(path.TrimEnd('/') + "/");
            else exactFiles.Add(path);
        }

        var result = new List<ArchiveEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in allEntries)
        {
            if (entry.IsDir) continue;
            string path = Normalize(entry.Path);
            bool included = exactFiles.Contains(path) || directories.Any(dir => path.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
            if (included && seen.Add(path)) result.Add(entry);
        }
        return result;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static long ResolveArchiveSize(string archivePath, ArchiveInfo? info)
    {
        if (info?.TotalPhysicalSize >= 0) return info.TotalPhysicalSize;
        if (info?.PhysicalSize >= 0) return info.PhysicalSize;
        try { return new FileInfo(archivePath).Length; }
        catch { return -1; }
    }

    private static long SumSaturated(IEnumerable<long> values)
    {
        long total = 0;
        foreach (long value in values)
        {
            if (value <= 0) continue;
            total = AddSaturated(total, value);
        }
        return total;
    }

    private static long AddSaturated(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static long MultiplySaturated(int left, long right)
        => left > 0 && left > long.MaxValue / right ? long.MaxValue : left * right;
}
