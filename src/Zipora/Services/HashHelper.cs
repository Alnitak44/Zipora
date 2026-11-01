using System.IO;
using System.Security.Cryptography;

namespace Zipora.Services;

public static class HashHelper
{
    /// <summary>
    /// 流式计算文件 MD5（小写十六进制），支持进度与取消。
    /// </summary>
    public static async Task<string> ComputeMd5Async(
        string filePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        long total = fs.Length;
        using var md5 = MD5.Create();
        byte[] buffer = new byte[1024 * 1024];
        long readTotal = 0;
        int n;
        while ((n = await fs.ReadAsync(buffer, ct)) > 0)
        {
            md5.TransformBlock(buffer, 0, n, null, 0);
            readTotal += n;
            if (total > 0) progress?.Report(readTotal * 100.0 / total);
        }
        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(md5.Hash!);
    }

    /// <summary>流式计算文件 SHA256（大写十六进制），支持进度与取消。</summary>
    public static async Task<string> ComputeSha256Async(
        string filePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        long total = fs.Length;
        using var sha256 = SHA256.Create();
        byte[] buffer = new byte[1024 * 1024];
        long readTotal = 0;
        int n;
        while ((n = await fs.ReadAsync(buffer, ct)) > 0)
        {
            sha256.TransformBlock(buffer, 0, n, null, 0);
            readTotal += n;
            if (total > 0) progress?.Report(readTotal * 100.0 / total);
        }
        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha256.Hash!);
    }
}
