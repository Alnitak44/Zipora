using System.IO;

namespace Zipora.Services;

public sealed record ExtractionItem(string ArchivePath, string ExportPath, bool IsDirectory, long Size);

/// <summary>An immutable selection: shared by drag/drop and explicit extraction.</summary>
public sealed class ArchiveSelectionPlan
{
    public IReadOnlyList<ExtractionItem> Items { get; }
    public IReadOnlyList<string> ExtractionPaths { get; }
    public int RootCount => Items.Count(x => !x.ExportPath.Contains('/'));

    private ArchiveSelectionPlan(List<ExtractionItem> items, List<string> paths)
        => (Items, ExtractionPaths) = (items.AsReadOnly(), paths.AsReadOnly());

    public static IReadOnlyList<ArchiveEntry> ArchiveRoots(IEnumerable<ArchiveEntry> entries)
        => entries.GroupBy(x => ValidatePath(x.Path).Split('/')[0], StringComparer.OrdinalIgnoreCase)
            .Select(group => new ArchiveEntry { Path = group.Key,
                IsDir = group.Any(x => x.IsDir || ValidatePath(x.Path).Contains('/')) }).ToList();

    public static ArchiveSelectionPlan Create(IReadOnlyList<ArchiveEntry> entries,
        IReadOnlyList<ArchiveEntry> selected, string? wrapper = null)
    {
        if (selected.Count == 0) throw new InvalidOperationException("请先选择要解压的内容。");
        var all = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            string path = ValidatePath(entry.Path);
            if (!all.TryAdd(path, entry))
                throw new InvalidDataException($"压缩包有重复或仅大小写不同的路径：{path}");
        }
        var candidates = selected.Select(x => (Path: ValidatePath(x.Path), x.IsDir))
            .DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Path, x => x, StringComparer.OrdinalIgnoreCase);
        bool HasSelectedParent(string path)
        {
            int slash = path.LastIndexOf('/');
            while (slash >= 0)
            {
                string parent = path[..slash];
                if (candidates.TryGetValue(parent, out var item) && item.IsDir) return true;
                slash = parent.LastIndexOf('/');
            }
            return false;
        }
        var roots = candidates.Values.Where(x => !HasSelectedParent(x.Path))
            .ToDictionary(x => x.Path, x => x, StringComparer.OrdinalIgnoreCase);
        string prefix = wrapper == null ? "" : ValidatePath(wrapper) + "/";
        var output = new Dictionary<string, ExtractionItem>(StringComparer.OrdinalIgnoreCase);
        var extraction = new HashSet<string>(StringComparer.Ordinal);

        void Add(string source, string destination, bool directory, long size)
        {
            if (output.TryGetValue(destination, out var existing))
            {
                if (existing.ArchivePath != source || existing.IsDirectory != directory)
                    throw new InvalidDataException($"所选内容包含同名目标：{destination}");
                return;
            }
            output.Add(destination, new(source, destination, directory, size));
        }

        if (wrapper != null) Add("", prefix.TrimEnd('/'), true, 0);
        foreach (var root in roots.Values)
        {
            if (!root.IsDir && !all.ContainsKey(root.Path))
                throw new InvalidDataException($"所选文件已不在压缩包目录中：{root.Path}");
            if (root.IsDir) Add(root.Path, prefix + root.Path[(root.Path.LastIndexOf('/') + 1)..], true, 0);
        }
        // Linear in entry count (times path depth), even for tens of thousands of selected roots.
        foreach (var (path, entry) in all)
        {
            string ancestor = path;
            while (!roots.ContainsKey(ancestor))
            {
                int slash = ancestor.LastIndexOf('/');
                if (slash < 0) { ancestor = ""; break; }
                ancestor = ancestor[..slash];
            }
            if (ancestor.Length == 0) continue;
            var root = roots[ancestor];
            if (ancestor != path && !root.IsDir) continue;
            if (entry.IsLink) throw new InvalidDataException($"暂不支持解压链接条目：{path}");
            string destination = prefix + root.Path[(root.Path.LastIndexOf('/') + 1)..];
            string exported = destination + path[root.Path.Length..];
            Add(path, exported, entry.IsDir, entry.Size);
            if (!entry.IsDir) extraction.Add(entry.Path);
            int parentSlash = exported.LastIndexOf('/');
            string parentSource = path;
            while (parentSlash >= destination.Length)
            {
                parentSource = parentSource[..parentSource.LastIndexOf('/')];
                Add(parentSource, exported[..parentSlash], true, 0);
                parentSlash = exported.LastIndexOf('/', parentSlash - 1);
            }
        }
        return new(output.Values.OrderBy(x => x.ExportPath.Count(c => c == '/'))
            .ThenByDescending(x => x.IsDirectory).ThenBy(x => x.ExportPath, StringComparer.Ordinal).ToList(),
            extraction.ToList());
    }

    public static string ValidatePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || Path.IsPathRooted(normalized))
            throw new InvalidDataException($"压缩包路径无效：{path}");
        normalized = normalized.TrimEnd('/');
        foreach (string segment in normalized.Split('/'))
        {
            string device = segment.Split('.')[0].ToUpperInvariant();
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
                device is "CON" or "PRN" or "AUX" or "NUL" ||
                (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && char.IsDigit(device[3])))
                throw new InvalidDataException($"无法安全解压此 Windows 路径：{path}");
        }
        return normalized;
    }

    public static string ResolveInside(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Directory.Exists(fullRoot) && (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("目标目录不能是链接。");
        string path = Path.GetFullPath(Path.Combine(fullRoot, ValidatePath(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("解压路径越出目标目录。");
        for (string? current = path; current != null && current.Length >= fullRoot.Length; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("解压路径包含链接，已停止操作。");
        return path;
    }

    public void ValidateExtractedFiles(string rawRoot)
    {
        foreach (var item in Items.Where(x => !x.IsDirectory))
        {
            string path = ResolveInside(rawRoot, item.ArchivePath);
            if (!File.Exists(path)) throw new IOException($"未能完整提取：{item.ExportPath}");
            if (item.Size >= 0 && new FileInfo(path).Length != item.Size)
                throw new IOException($"提取后的大小与目录记录不符：{item.ExportPath}");
        }
    }

    public void ExportTo(string rawRoot, string exportRoot)
    {
        ValidateExtractedFiles(rawRoot);
        Directory.CreateDirectory(exportRoot);
        foreach (var item in Items)
        {
            string target = ResolveInside(exportRoot, item.ExportPath);
            if (item.IsDirectory) Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(ResolveInside(rawRoot, item.ArchivePath), target);
            }
        }
    }
}
