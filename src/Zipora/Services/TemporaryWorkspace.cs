using System.IO;

namespace Zipora.Services;

/// <summary>One leased workspace per window; never collect another live instance.</summary>
public sealed class TemporaryWorkspace : IDisposable
{
    private readonly string _root;
    private readonly FileStream _lease;
    public TemporaryWorkspace()
    {
        string parent = AppStorage.TempRoot;
        Directory.CreateDirectory(parent);
        _root = Path.Combine(parent, "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _lease = new FileStream(Path.Combine(_root, ".lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        // Startup recovery only touches old, unlocked session directories from this implementation.
        foreach (string candidate in Directory.EnumerateDirectories(parent, "session-*"))
        {
            try
            {
                if (!AppStorage.IsPortable && Directory.GetLastWriteTimeUtc(candidate) >= DateTime.UtcNow.AddDays(-1)) continue;
                if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) continue;
                using (new FileStream(Path.Combine(candidate, ".lease"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                DeleteTree(candidate);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public string CreateDirectory(string purpose)
    {
        if (purpose is not ("drag" or "open" or "lists" or "create")) throw new ArgumentException("Unknown temporary purpose", nameof(purpose));
        string path = Path.Combine(_root, purpose + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    public void Remove(string path)
    {
        string full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), _root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Temporary directory is not owned by this workspace.");
        try { DeleteTree(full); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        _lease.Dispose();
        try { DeleteTree(_root); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(path); return; }
        foreach (string file in Directory.EnumerateFiles(path))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        foreach (string child in Directory.EnumerateDirectories(path)) DeleteTree(child);
        Directory.Delete(path);
    }
}
