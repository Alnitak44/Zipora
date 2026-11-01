using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zipora.Services;

/// <summary>No machine-bound key, embedded key or plaintext password file.</summary>
public sealed class PortablePasswordVault : IDisposable
{
    private const int Iterations = 600_000;
    private readonly string _path;
    private byte[]? _key;
    private byte[]? _salt;
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("Zipora.PortableVault.v1");
    private sealed record Envelope(int Version, int Iterations, byte[] Salt, byte[] Nonce, byte[] Tag, byte[] Ciphertext);
    public PortablePasswordVault(string path) => _path = path;
    public bool Exists => File.Exists(_path) || File.Exists(_path + ".bak");
    public bool IsLocked => _key == null;
    public bool RecoveredBackup { get; private set; }

    public void Create(string masterPassword)
    {
        if (Exists) throw new InvalidOperationException("密码库已存在，请解锁，不能覆盖创建。");
        if (masterPassword.Length < 8 || masterPassword.Length > 1024) throw new ArgumentException("主密码长度需为 8～1024 个字符。");
        _salt = RandomNumberGenerator.GetBytes(16);
        _key = Derive(masterPassword, _salt);
        try { Save([]); } catch { Dispose(); throw; }
    }
    public void Unlock(string masterPassword)
    {
        Dispose();
        RecoveredBackup = false;
        Exception? error = null;
        foreach (string path in new[] { _path, _path + ".bak" })
        {
            try
            {
                var envelope = ReadEnvelope(path);
                byte[] key = Derive(masterPassword, envelope.Salt);
                try
                {
                    _ = Decrypt(envelope, key);
                    _key = key;
                    _salt = envelope.Salt;
                    RecoveredBackup = path != _path;
                    return;
                }
                catch { CryptographicOperations.ZeroMemory(key); throw; }
            }
            catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or InvalidDataException or ArgumentException)
            { error = ex; }
        }
        throw new CryptographicException("主密码不正确，或密码库已损坏。原文件未被修改。", error);
    }
    public IReadOnlyList<string> Load()
    {
        if (_key == null) throw new InvalidOperationException("请先解锁便携密码库。");
        return Decrypt(ReadEnvelope(RecoveredBackup ? _path + ".bak" : _path), _key);
    }
    public void Save(IEnumerable<string> passwords)
    {
        if (_key == null || _salt == null) throw new InvalidOperationException("请先解锁便携密码库。");
        var values = passwords.Take(11).ToArray();
        if (values.Length > 10 || values.Any(v => string.IsNullOrEmpty(v) || v.Length > 256))
            throw new ArgumentException("密码库最多保存 10 个密码，每个密码最多 256 个字符。");
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(values);
        try
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], cipher = new byte[plain.Length];
            using (var aes = new AesGcm(_key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Header);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, Iterations, _salt, nonce, tag, cipher));
            if (RecoveredBackup)
            {
                // Do not replace the only good backup with the corrupt primary.
                if (File.Exists(_path)) File.Move(_path, _path + ".corrupt", true);
            }
            AppStorage.WriteAtomic(_path, bytes);
            RecoveredBackup = false;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
    private static Envelope ReadEnvelope(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("密码库大小异常。");
        var e = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("密码库为空。");
        if (e.Version != 1 || e.Iterations != Iterations || e.Salt is not { Length: 16 }
            || e.Nonce is not { Length: 12 } || e.Tag is not { Length: 16 } || e.Ciphertext == null)
            throw new InvalidDataException("密码库格式无效或版本不受支持。");
        return e;
    }
    private static IReadOnlyList<string> Decrypt(Envelope e, byte[] key)
    {
        byte[] plain = new byte[e.Ciphertext.Length];
        try
        {
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(e.Nonce, e.Ciphertext, e.Tag, plain, Header);
            var values = JsonSerializer.Deserialize<string[]>(plain) ?? throw new InvalidDataException("密码库内容无效。");
            if (values.Length > 10 || values.Any(v => string.IsNullOrEmpty(v) || v.Length > 256))
                throw new InvalidDataException("密码库内容无效。");
            return values;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Dispose()
    {
        if (_key != null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
        _salt = null;
    }
}
