using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Zipora.Services;

/// <summary>
/// 压缩包内的一条目录记录（来自 7z l -slt）。
/// </summary>
public class ArchiveEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; } = -1;
    /// <summary>-1 表示未知（部分格式没有打包后大小）。</summary>
    public long PackedSize { get; set; } = -1;
    public bool IsDir { get; set; }
    public bool IsLink { get; set; }
    public bool Encrypted { get; set; }
    public string Method { get; set; } = "";
    public string Crc { get; set; } = "";

    /// <summary>本地浏览模式下为文件/文件夹的完整路径；压缩包模式下为 null。</summary>
    public string? SourcePath { get; set; }

    /// <summary>Fluent 图标符号（Microsoft Fluent System Icons）。</summary>
    public SymbolRegular TypeIcon => IconProvider.ForEntry(this);

    /// <summary>图标悬浮提示（文件类型描述）。</summary>
    public string TypeIconText => IconProvider.Describe(TypeIcon);

    /// <summary>加密标记列的可见性（锁定图标）。</summary>
    public Visibility EncryptedIconVisibility => Encrypted ? Visibility.Visible : Visibility.Collapsed;

    public string SizeText => IsDir ? "" : FormatSize(Size);
    public string PackedSizeText => (IsDir || PackedSize < 0) ? "" : FormatSize(PackedSize);
    public string EncryptedText => Encrypted ? "已加密" : "";

    /// <summary>当前目录视图中的显示名称；压缩包路径仍保留在 Path 以供解压引擎使用。</summary>
    public string DisplayName
    {
        get
        {
            string value = Path.TrimEnd('\\', '/');
            int separator = Math.Max(value.LastIndexOf('\\'), value.LastIndexOf('/'));
            return separator >= 0 ? value[(separator + 1)..] : value;
        }
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes < 0) return "";
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
