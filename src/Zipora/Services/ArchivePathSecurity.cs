using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Text;

namespace Zipora.Services;

public sealed record ArchivePathIssue(string Path, string Reason);

public sealed record ArchivePathReport(int CheckedEntries, int RiskCount, IReadOnlyList<ArchivePathIssue> Issues)
{
    public bool IsSafe => RiskCount == 0;

    public string Describe()
    {
        if (IsSafe)
            return $"已检查 {CheckedEntries:N0} 个条目，未发现危险路径或链接。\n\n解压时仍会重新检查压缩包与实际目标路径。此检查不包含病毒扫描或文件内容完整性校验。";
        var text = new StringBuilder($"已检查 {CheckedEntries:N0} 个条目，发现 {RiskCount:N0} 项风险。为防止文件写到目标目录之外或覆盖异常目标，已阻止解压。\n");
        foreach (var issue in Issues)
            text.Append($"\n• {DisplayPath(issue.Path)}\n  {issue.Reason}\n");
        if (RiskCount > Issues.Count) text.Append($"\n其余 {RiskCount - Issues.Count:N0} 项未展开显示。");
        return text.ToString();
    }

    public void ThrowIfUnsafe()
    {
        if (!IsSafe) throw new InvalidDataException("Zip Slip 安全防护：\n" + Describe());
    }

    private static string DisplayPath(string value) => string.Concat(value.Take(512)
        .Select(c => char.IsControl(c) || c is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069'
            ? $"\\u{(int)c:x4}" : c.ToString())) + (value.Length > 512 ? "…" : "");
}

/// <summary>Always-on Windows extraction policy, shared by inspection, planning and the engine.</summary>
public static class ArchivePathSecurity
{
    public static string ValidatePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || Path.IsPathRooted(normalized))
            throw new InvalidDataException("绝对路径或空路径");
        normalized = normalized.TrimEnd('/');
        foreach (string segment in normalized.Split('/'))
        {
            string device = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (segment is "." or "..") throw new InvalidDataException("包含 . 或 .. 路径穿越片段");
            if (segment.Contains(':')) throw new InvalidDataException("包含盘符或 NTFS 备用数据流");
            if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => char.IsControl(c) || "<>\"|?*".Contains(c)))
                throw new InvalidDataException("包含空片段、无效字符或 Windows 路径别名");
            if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) &&
                    (char.IsDigit(device[3]) || "¹²³".Contains(device[3]))))
                throw new InvalidDataException("包含 Windows 保留设备名");
        }
        return normalized;
    }

    public static ArchivePathReport Inspect(IEnumerable<ArchiveEntry> entries, CancellationToken ct = default)
    {
        int count = 0, risks = 0;
        var issues = new List<ArchivePathIssue>();
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string reason)
        {
            risks++;
            if (issues.Count < 200) issues.Add(new(path, reason));
        }
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            count++;
            string path;
            try { path = ValidatePath(entry.Path); }
            catch (InvalidDataException ex) { Add(entry.Path, ex.Message); continue; }
            if (entry.IsLink) Add(path, "符号链接、硬链接或重解析条目，可能重定向写入位置");
            if (!paths.TryAdd(path, entry.IsDir)) Add(path, "重复路径或仅大小写不同的目标路径");
        }
        foreach (var path in paths.Keys)
        {
            ct.ThrowIfCancellationRequested();
            int slash = path.LastIndexOf('/');
            while (slash >= 0)
            {
                if (paths.TryGetValue(path[..slash], out bool directory) && !directory)
                { Add(path, "父路径同时被声明为文件，存在文件/目录冲突"); break; }
                slash = path.LastIndexOf('/', slash - 1);
            }
        }
        return new(count, risks, issues.AsReadOnly());
    }

    // ZIP's raw central-directory names are checked as well: a line-oriented 7z listing
    // cannot faithfully represent filenames containing CR/LF. No payload is decompressed.
    public static ArchivePathReport InspectArchive(string archive, ArchiveListResult listing,
        CancellationToken ct = default)
    {
        var listed = Inspect(listing.Entries, ct);
        if (string.Equals(listing.Info?.Format, "tar", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(archive);
            using var reader = new TarReader(file);
            var entries = new List<ArchiveEntry>();
            TarEntry? item;
            while ((item = reader.GetNextEntry(copyData: false)) != null)
            {
                ct.ThrowIfCancellationRequested();
                if (item.EntryType == TarEntryType.GlobalExtendedAttributes) continue;
                entries.Add(new ArchiveEntry { Path = item.Name, IsDir = item.EntryType == TarEntryType.Directory,
                    IsLink = item.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory) });
            }
            var raw = Inspect(entries, ct);
            return raw.IsSafe ? listed : raw;
        }
        if (!string.Equals(listing.Info?.Format, "zip", StringComparison.OrdinalIgnoreCase)) return listed;
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            var entries = new List<ArchiveEntry>(zip.Entries.Count);
            foreach (var item in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                int unixType = (item.ExternalAttributes >> 16) & 0xf000;
                entries.Add(new ArchiveEntry { Path = item.FullName,
                    IsDir = item.FullName.EndsWith('/') || item.FullName.EndsWith('\\') || unixType == 0x4000 ||
                        (item.ExternalAttributes & (int)FileAttributes.Directory) != 0,
                    IsLink = unixType == 0xa000 || (item.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 });
            }
            var raw = Inspect(entries, ct);
            return raw.IsSafe ? listed : raw;
        }
        catch (InvalidDataException)
        {
            // Some 7z-supported ZIP variants (e.g. split archives) cannot be opened by
            // System.IO.Compression. Fail closed rather than claim a partial scan is safe.
            return new(listed.CheckedEntries, listed.RiskCount + 1,
                listed.Issues.Concat(new[] { new ArchivePathIssue("ZIP 目录", "无法完整验证原始 ZIP 文件名，已阻止解压") }).Take(200).ToList());
        }
    }

    public static void EnsureNoReparseAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Zip Slip 安全防护：目标路径包含符号链接或目录联接，已阻止操作。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static void ValidateOutputTree(string root, CancellationToken ct = default)
    {
        EnsureNoReparseAncestors(root);
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.Count > 0)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directories.Pop()))
            {
                ct.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Zip Slip 安全防护：提取结果包含链接，已停止交付文件。");
                ValidatePath(Path.GetRelativePath(root, path));
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(path);
            }
        }
    }
}
