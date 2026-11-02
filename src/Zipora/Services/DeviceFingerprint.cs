using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace Zipora.Services;

/// <summary>
/// 设备指纹：CPU 序列号 + 主板序列号 + 系统盘卷标序列号 → SHA256（64位十六进制）。
/// 任一组件获取失败或为空时使用固定占位符 "NA"，保证任何一次启动指纹都稳定一致
/// （组件缺失会导致指纹变化 → 该设备会获得新的本地配额桶）。
/// </summary>
public static class DeviceFingerprint
{
    private const string Placeholder = "NA";

    public static string Compute()
    {
        string cpu = Collect("SELECT ProcessorId FROM Win32_Processor", "ProcessorId");
        string board = Collect("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber");
        string disk = Collect("SELECT VolumeSerialNumber FROM Win32_LogicalDisk WHERE DeviceID='C:'", "VolumeSerialNumber");

        // 极端情况：WMI 完全不可用（如精简系统）→ 用机器名+用户名兜底，同样保证稳定
        if (cpu == Placeholder && board == Placeholder && disk == Placeholder)
        {
            cpu = Environment.MachineName;
            board = Environment.UserName;
            disk = Placeholder;
        }

        string joined = $"{cpu}|{board}|{disk}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>查询单个 WMI 字段；失败或为空返回固定占位符（保证指纹跨启动稳定）。</summary>
    private static string Collect(string query, string field)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    var v = obj[field]?.ToString();
                    if (!string.IsNullOrWhiteSpace(v))
                        return v.Trim();
                }
            }
        }
        catch
        {
            // WMI 偶发失败（系统繁忙/受限）不改变指纹组成
        }
        return Placeholder;
    }
}
