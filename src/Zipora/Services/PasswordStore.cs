using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Zipora.Services;

/// <summary>Stores up to ten user-selected archive passwords with Windows DPAPI.
/// The encrypted file can only be read by the same Windows user.</summary>
public static class PasswordStore
{
    public const int MaxPasswords = 10;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Zipora.PasswordStore.v1");

    private static string StorePath => AppStorage.DataFile("passwords.dat");
    private static PortablePasswordVault? _portableVault;
    public static PortablePasswordVault PortableVault => _portableVault ??= new(AppStorage.DataFile("passwords.vault"));
    public static bool NeedsUnlock => AppStorage.IsPortable && PortableVault.Exists && PortableVault.IsLocked;
    public static void Lock() => _portableVault?.Dispose();

    public static IReadOnlyList<string> Load()
    {
        if (AppStorage.IsPortable)
            return PortableVault.IsLocked ? [] : Normalize(PortableVault.Load());
        try
        {
            var encrypted = Convert.FromBase64String(File.ReadAllText(StorePath, Encoding.UTF8));
            var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var values = JsonSerializer.Deserialize<List<string>>(plain) ?? [];
            return Normalize(values);
        }
        catch
        {
            return [];
        }
    }

    public static bool TryAdd(string password, out string message)
    {
        if (string.IsNullOrEmpty(password))
        {
            message = "请输入密码。";
            return false;
        }
        if (password.Length > 256)
        {
            message = "密码不能超过 256 个字符。";
            return false;
        }

        var values = Load().ToList();
        if (values.Contains(password, StringComparer.Ordinal))
        {
            message = "该密码已在列表中。";
            return false;
        }
        if (values.Count >= MaxPasswords)
        {
            message = $"最多保存 {MaxPasswords} 个密码。";
            return false;
        }

        values.Add(password);
        try { Save(values); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        { message = "未保存：" + ex.Message; return false; }
        message = "已保存。";
        return true;
    }

    public static void Remove(string password)
    {
        var values = Load().Where(value => !string.Equals(value, password, StringComparison.Ordinal)).ToList();
        Save(values);
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> values)
        => values.Where(value => !string.IsNullOrEmpty(value) && value.Length <= 256)
            .Distinct(StringComparer.Ordinal).Take(MaxPasswords).ToArray();

    private static void Save(IEnumerable<string> values)
    {
        if (AppStorage.IsPortable) { PortableVault.Save(Normalize(values)); return; }
        var directory = Path.GetDirectoryName(StorePath);
        if (directory != null) Directory.CreateDirectory(directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(Normalize(values));
        var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        AppStorage.WriteText(StorePath, Convert.ToBase64String(encrypted));
    }

    // Explicit user action only; portable startup never reads host credentials.
    public static int ImportFromCurrentWindowsUser()
    {
        if (!AppStorage.IsPortable || PortableVault.IsLocked) throw new InvalidOperationException("请先解锁便携密码库。");
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zipora", "passwords.dat");
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(path)), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var imported = JsonSerializer.Deserialize<List<string>>(bytes) ?? [];
            var current = Load();
            var combined = current.Concat(Normalize(imported)).Distinct(StringComparer.Ordinal).ToArray();
            if (combined.Length > MaxPasswords) throw new InvalidOperationException("合并后超过 10 个密码，请先删除部分密码再导入。");
            Save(combined);
            return combined.Length - current.Count;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
