using System.IO;
using System.Text;

namespace Zipora.Services;

/// <summary>All application-owned writes have one explicit storage policy.</summary>
public sealed class StorageLayout
{
    public string Root { get; }
    public string AppDirectory { get; }
    public bool IsPortable { get; }
    public string DataRoot { get; }
    public string LogRoot => IsPortable ? Path.Combine(DataRoot, "Logs")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zipora");
    public string TempRoot => IsPortable ? Path.Combine(DataRoot, "Temp") : Path.Combine(Path.GetTempPath(), "Zipora");

    public StorageLayout(string baseDirectory, bool portableBuild = false)
    {
        string app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        AppDirectory = app;
        string parent = Path.GetDirectoryName(app) ?? app;
        bool appSubfolder = string.Equals(Path.GetFileName(app), "App", StringComparison.OrdinalIgnoreCase);
        Root = appSubfolder && (portableBuild || File.Exists(Path.Combine(parent, "portable.json"))) ? parent : app;
        IsPortable = portableBuild || File.Exists(Path.Combine(Root, "portable.json"));
        DataRoot = IsPortable ? Path.Combine(Root, "Data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zipora");
    }

    public string EncodePath(string path)
    {
        if (!IsPortable || string.IsNullOrWhiteSpace(path)) return path;
        string full = Path.GetFullPath(path);
        if (Within(full, Root) || string.Equals(full, Root, StringComparison.OrdinalIgnoreCase))
            return "app:" + Path.GetRelativePath(Root, full).Replace('\\', '/');
        string volume = Path.GetPathRoot(Root)!;
        if (Within(full, volume) || string.Equals(full, volume, StringComparison.OrdinalIgnoreCase))
            return "drive:" + Path.GetRelativePath(volume, full).Replace('\\', '/');
        // Do not reuse another computer's remembered local output directory.
        return "";
    }

    public string DecodePath(string path)
    {
        if (!IsPortable || string.IsNullOrWhiteSpace(path)) return path;
        string? anchor = path.StartsWith("app:", StringComparison.Ordinal) ? Root
            : path.StartsWith("drive:", StringComparison.Ordinal) ? Path.GetPathRoot(Root) : null;
        if (anchor == null) return "";
        string relative = path[(path.IndexOf(':') + 1)..];
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(part => part == "..")) return "";
        string full = Path.GetFullPath(Path.Combine(anchor, relative));
        return Within(full, anchor) || string.Equals(full, anchor, StringComparison.OrdinalIgnoreCase) ? full : "";
    }

    public static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

public static class AppStorage
{
    public static event Action<string>? SaveFailed;
    public static void ReportSaveFailure(Exception error)
    {
        if (IsPortable) SaveFailed?.Invoke("设置仅在本次会话生效，未能保存到 U 盘。请检查连接、写保护与可用空间。\n\n" + error.Message);
    }
#if PORTABLE
    private const bool PortableBuild = true;
#else
    private const bool PortableBuild = false;
#endif
    private static StorageLayout? _layout;
    private static FileStream? _instanceLease;
    public static StorageLayout Layout => _layout ??= new StorageLayout(AppContext.BaseDirectory, PortableBuild);
    public static bool IsPortable => Layout.IsPortable;
    public static string DataRoot => Layout.DataRoot;
    public static string TempRoot => Layout.TempRoot;
    public static string DataFile(string name) => Path.Combine(DataRoot, name);
    public static string LogRoot => Layout.LogRoot;
    public static string DictionaryRoot => IsPortable ? Path.Combine(DataRoot, "Dictionaries") : Path.Combine(Layout.AppDirectory, "dictionaries");

    // Initialization happens before stores/windows; the explicit base also permits
    // isolated regression executables without touching the user's actual AppData.
    public static void Initialize(string? baseDirectory = null)
    {
        if (baseDirectory != null)
        {
            if (_layout != null) throw new InvalidOperationException("Storage is already initialized.");
            _layout = new StorageLayout(baseDirectory, PortableBuild);
        }
        if (!IsPortable) return;
        Directory.CreateDirectory(DataRoot);
        if ((File.GetAttributes(DataRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Data 目录不能是符号链接或目录联接。");
        foreach (string child in new[] { "Temp", "Logs", "Assets", "Dictionaries" })
        {
            string directory = Path.Combine(DataRoot, child);
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("便携数据子目录不能是符号链接或目录联接：" + child);
        }
        try
        {
            _instanceLease = new FileStream(DataFile(".instance.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) { throw new IOException("便携版已在运行，或 U 盘不可写。请先关闭同一目录中的 Zipora。", ex); }
        // Fail closed: never silently redirect USB data to the host computer.
        string probe = DataFile(".write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.WriteByte(1); file.Flush(true); }
            File.Delete(probe);
            Directory.CreateDirectory(TempRoot);
            Directory.CreateDirectory(Path.Combine(TempRoot, "system"));
            Environment.SetEnvironmentVariable("TEMP", Path.Combine(TempRoot, "system"));
            Environment.SetEnvironmentVariable("TMP", Path.Combine(TempRoot, "system"));
            // A crashed process cannot retain an open lease. Only our private temp
            // tree is collected, never an archive or a user-chosen output directory.
            foreach (string name in new[] { "lists", "create", "system" })
                CleanPrivateDirectory(Path.Combine(TempRoot, name));
            foreach (string file in Directory.EnumerateFiles(DataRoot, ".write-*"))
                try { File.Delete(file); } catch { }
            string seedMarker = Path.Combine(DictionaryRoot, ".initialized");
            if (!File.Exists(seedMarker))
            {
                Directory.CreateDirectory(DictionaryRoot);
                string defaults = Path.Combine(Layout.AppDirectory, "dictionaries");
                if (Directory.Exists(defaults))
                    foreach (string source in Directory.EnumerateFiles(defaults, "*.txt"))
                    {
                        string target = Path.Combine(DictionaryRoot, Path.GetFileName(source));
                        if (!File.Exists(target)) WriteAtomic(target, File.ReadAllBytes(source));
                    }
                WriteText(seedMarker, "1");
            }
        }
        catch { Dispose(); throw; }
    }

    public static void Dispose()
    {
        PasswordStore.Lock();
        if (IsPortable && _instanceLease != null)
            foreach (string name in new[] { "lists", "create", "system" })
                CleanPrivateDirectory(Path.Combine(TempRoot, name));
        _instanceLease?.Dispose();
        _instanceLease = null;
    }

    public static void CleanPrivateDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
            foreach (string file in Directory.EnumerateFiles(path)) File.Delete(file);
            foreach (string child in Directory.EnumerateDirectories(path))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                CleanPrivateDirectory(child);
                try { Directory.Delete(child); } catch { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void WriteAtomic(string path, byte[] bytes)
    {
        string directory = Path.GetDirectoryName(path)!;
        if (IsPortable)
        {
            if (!StorageLayout.Within(path, DataRoot)) throw new IOException("便携数据写入必须位于 Data 目录内。");
            for (string? current = directory; current != null && (StorageLayout.Within(current, DataRoot) || current == DataRoot); current = Path.GetDirectoryName(current))
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("便携数据路径不能经过目录联接或符号链接。");
        }
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(path))
            {
                // Rename-overwrite works on NTFS/exFAT/FAT32; no File.Replace
                // assumption. Keep one previous version for recovery.
                string backupTemp = temporary + ".bak";
                try { File.Copy(path, backupTemp); File.Move(backupTemp, path + ".bak", true); }
                finally { if (File.Exists(backupTemp)) File.Delete(backupTemp); }
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void WriteText(string path, string text) => WriteAtomic(path, Encoding.UTF8.GetBytes(text));
    public static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(LogRoot);
            string path = Path.Combine(LogRoot, "crash.log");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {text}{Environment.NewLine}");
        }
        catch { }
    }
    public static string ImportBackground(string path)
    {
        if (!IsPortable || string.IsNullOrWhiteSpace(path)) return path;
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new IOException("背景图片不能超过 32 MiB。请先缩小图片。");
        string target = Path.Combine(DataRoot, "Assets", "background.img");
        if (!string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase))
            WriteAtomic(target, File.ReadAllBytes(path));
        return target;
    }
}
