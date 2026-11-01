using System.IO;
using Wpf.Ui.Controls;

namespace Zipora.Services;

/// <summary>
/// 文件/文件夹类型图标：全部映射为 Microsoft Fluent System Icons
/// （经 WPF-UI 内置的 FluentSystemIcons 字体渲染，自动跟随主题前景色）。
/// </summary>
public static class IconProvider
{
    /// <summary>按条目类型/扩展名返回 Fluent 图标符号（未命中回退默认文件图标）。</summary>
    public static SymbolRegular ForEntry(ArchiveEntry entry)
    {
        if (entry.IsDir) return SymbolRegular.Folder24;

        string ext = Path.GetExtension(entry.Path).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg"
                or ".ico" or ".heic" or ".tif" or ".tiff" => SymbolRegular.Image24,

            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm"
                or ".m4v" or ".rmvb" => SymbolRegular.Video24,

            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac" or ".wma"
                => SymbolRegular.MusicNote224,

            ".7z" or ".zip" or ".rar" or ".tar" or ".gz" or ".xz" or ".bz2" or ".zst"
                or ".tgz" or ".zipx" or ".jar" or ".apk" => SymbolRegular.Archive24,

            ".iso" or ".cab" or ".wim" or ".img" or ".vhd"
                => SymbolRegular.Record20,

            ".pdf" => SymbolRegular.DocumentPdf24,

            ".doc" or ".docx" or ".docm" or ".rtf" or ".odt"
                => SymbolRegular.TextFont24,

            ".xls" or ".xlsx" or ".xlsm" or ".csv" or ".ods"
                => SymbolRegular.Table24,

            ".ppt" or ".pptx" or ".pps" or ".odp"
                => SymbolRegular.SlideLayout24,

            ".txt" or ".log" or ".ini" or ".cfg" or ".conf" or ".nfo"
                => SymbolRegular.Document24,

            ".md" or ".markdown" => SymbolRegular.TextBulletListLtr20,

            ".json" => SymbolRegular.Braces20,

            ".js" or ".mjs" or ".cjs" => SymbolRegular.Javascript20,

            ".ts" or ".tsx" => SymbolRegular.Code24,

            ".html" or ".htm" or ".xml" => SymbolRegular.WindowDevEdit20,

            ".css" or ".scss" or ".sass" or ".less"
                => SymbolRegular.ColorBackground24,

            ".sql" => SymbolRegular.Database24,

            ".db" or ".sqlite" or ".sqlite3" or ".mdb"
                => SymbolRegular.DocumentDatabase24,

            ".exe" or ".msi" or ".bat" or ".cmd" or ".com" or ".ps1" or ".sh"
                => SymbolRegular.WindowConsole20,

            ".ttf" or ".otf" or ".woff" or ".woff2" or ".eot"
                => SymbolRegular.FontIncrease24,

            ".cs" or ".py" or ".java" or ".c" or ".cpp" or ".h" or ".go"
                or ".rs" or ".rb" or ".php" or ".kt" or ".swift"
                => SymbolRegular.Code24,

            _ => SymbolRegular.Document24,
        };
    }

    /// <summary>返回图标的人类可读描述（用于无障碍/悬浮提示）。</summary>
    public static string Describe(SymbolRegular symbol) => symbol switch
    {
        SymbolRegular.Folder24 => "文件夹",
        SymbolRegular.Image24 => "图片",
        SymbolRegular.Video24 => "视频",
        SymbolRegular.MusicNote224 => "音频",
        SymbolRegular.Archive24 => "压缩包",
        SymbolRegular.Record20 => "光盘镜像",
        SymbolRegular.DocumentPdf24 => "PDF 文档",
        SymbolRegular.TextFont24 => "Word 文档",
        SymbolRegular.Table24 => "Excel 表格",
        SymbolRegular.SlideLayout24 => "PPT 演示",
        SymbolRegular.Document24 => "文本文件",
        SymbolRegular.TextBulletListLtr20 => "Markdown 文档",
        SymbolRegular.Braces20 => "JSON 数据",
        SymbolRegular.Javascript20 => "JS 脚本",
        SymbolRegular.Code24 => "源代码",
        SymbolRegular.WindowDevEdit20 => "网页文件",
        SymbolRegular.ColorBackground24 => "样式文件",
        SymbolRegular.Database24 => "数据库",
        SymbolRegular.DocumentDatabase24 => "数据库文件",
        SymbolRegular.WindowConsole20 => "可执行程序",
        SymbolRegular.FontIncrease24 => "字体文件",
        _ => "文件",
    };
}
