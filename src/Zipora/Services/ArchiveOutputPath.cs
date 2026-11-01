using System.IO;

namespace Zipora.Services;

/// <summary>Archive suffixes are replaced as a whole; the user's directory and name are retained.</summary>
public static class ArchiveOutputPath
{
    private static readonly string[] Extensions =
    {
        ".tar.bz2", ".tar.gz", ".tar.xz", ".7z", ".zip", ".exe", ".tar", ".wim",
        ".tgz", ".tbz2", ".txz", ".gz", ".bz2", ".xz", ".rar",
    };

    public static string WithExtension(string path, string extension)
    {
        path = path.Trim();
        if (path.Length == 0) return path;
        int start = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/')) + 1;
        string name = path[start..];
        // Leave an unfinished directory alone while the user is editing.
        if (name.Length == 0) return path;
        string? oldExtension = Extensions.FirstOrDefault(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        string stem = oldExtension == null ? name : name[..^oldExtension.Length];
        return path[..start] + stem + extension;
    }

    public static string FindAvailablePath(string path, string extension)
    {
        string normalized = WithExtension(path, extension);
        if (normalized.Length == 0 || Path.GetFileName(normalized).Length == 0)
            throw new ArgumentException("输出路径缺少压缩包文件名。", nameof(path));
        if (!File.Exists(normalized) && !Directory.Exists(normalized)) return normalized;
        string directory = Path.GetDirectoryName(normalized) ?? "";
        string name = Path.GetFileName(normalized)[..^extension.Length];
        int index = 1;
        string candidate;
        do { candidate = Path.Combine(directory, $"{name} ({index++}){extension}"); }
        while (File.Exists(candidate) || Directory.Exists(candidate));
        return candidate;
    }
}
