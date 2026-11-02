using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace Zipora.Services;

/// <summary>
/// 本地云端限流（与服务端规则一致，先于网络请求拦截，fail-fast）：
///   - 查询每天（UTC+8）最多 10 次，贡献每天最多 20 次
///   - 任意两次云端请求之间至少间隔 3 秒
/// 状态持久化到 %APPDATA%\Zipora\ratelimit.json
/// </summary>
public class LocalRateLimiter
{
    private const int QueryDailyLimit = 10;
    private const int ContribDailyLimit = 20;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(3);

    private string _date = "";
    private int _fetchCount;
    private int _contribCount;
    private DateTime _lastRequestUtc;

    private static string Path
    {
        get
        {
            string dir = AppStorage.DataRoot;
            return System.IO.Path.Combine(dir, "ratelimit.json");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static LocalRateLimiter Load()
    {
        var limiter = new LocalRateLimiter();
        foreach (string path in new[] { Path, Path + ".bak" })
        {
          try
          {
            if (File.Exists(path))
            {
                byte[] bytes = File.ReadAllBytes(path);
                using var doc = JsonDocument.Parse(bytes);
                var root = doc.RootElement;
                if (root.TryGetProperty("date", out var d)) limiter._date = d.GetString() ?? "";
                if (root.TryGetProperty("fetchCount", out var f)) limiter._fetchCount = f.GetInt32();
                if (root.TryGetProperty("contribCount", out var c)) limiter._contribCount = c.GetInt32();
                if (root.TryGetProperty("lastRequestUtc", out var t)) limiter._lastRequestUtc = DateTime.FromBinary(t.GetInt64());
                break;
            }
          }
          catch { }
        }
        limiter.RollDate();
        return limiter;
    }

    /// <summary>当前 UTC+8 日期内、本地记录的剩余查询次数。</summary>
    public int QueryRemaining
    {
        get
        {
            RollDate();
            return Math.Max(0, QueryDailyLimit - _fetchCount);
        }
    }

    /// <summary>返回 (是否允许, 拒绝原因)。允许时已计入本次请求。</summary>
    public (bool ok, string message) TryQuery() => TryUse(ref _fetchCount, "查询", QueryDailyLimit);

    public (bool ok, string message) TryContribute() => TryUse(ref _contribCount, "贡献", ContribDailyLimit);

    private (bool ok, string message) TryUse(ref int counter, string label, int dailyLimit)
    {
        RollDate();

        var now = DateTime.UtcNow;
        if (_lastRequestUtc != default && now - _lastRequestUtc < MinInterval)
        {
            return (false, $"云端请求过于频繁，请稍候几秒再试。");
        }
        if (counter >= dailyLimit)
        {
            return (false, $"今日云端{label}次数已用完（{dailyLimit}次/天），明日恢复。");
        }

        var previousRequestUtc = _lastRequestUtc;
        counter++;
        _lastRequestUtc = now;
        if (!Save() && AppStorage.IsPortable)
        {
            counter--;
            _lastRequestUtc = previousRequestUtc;
            return (false, "U 盘配额记录保存失败，本次未请求云端。请检查连接与可用空间。");
        }
        return (true, "");
    }

    private void RollDate()
    {
        string d = Utc8Date();
        if (d != _date)
        {
            _date = d;
            _fetchCount = 0;
            _contribCount = 0;
            Save();
        }
    }

    private static string Utc8Date()
    {
        var utc8 = DateTime.UtcNow.AddHours(8);
        return $"{utc8:yyyy-MM-dd}";
    }

    private bool Save()
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(Path);
            if (dir != null) Directory.CreateDirectory(dir);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                date = _date,
                fetchCount = _fetchCount,
                contribCount = _contribCount,
                lastRequestUtc = _lastRequestUtc.ToBinary(),
            }, JsonOptions);
            AppStorage.WriteAtomic(Path, payload);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
