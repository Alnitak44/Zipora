using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Zipora.Dialogs;
namespace Zipora.Services;

public enum UpdateFrequency { EveryLaunch, Daily, Weekly, Fortnightly, Monthly, Never }
public record UpdateInfo(string LatestVersion, string Notes, string DownloadUrl) { public bool IsNewer { get; init; } }
public static class UpdateChecker
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static bool IsDue(UpdateFrequency frequency, DateTimeOffset? last, DateTimeOffset now) => frequency switch
    {
        UpdateFrequency.Never => false,
        UpdateFrequency.EveryLaunch => true,
        _ when last == null || last > now => true,
        UpdateFrequency.Monthly => now >= last.Value.AddMonths(1),
        _ => now - last.Value >= TimeSpan.FromDays(frequency == UpdateFrequency.Weekly ? 7 : frequency == UpdateFrequency.Fortnightly ? 14 : 1)
    };
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Alnitak44/Zipora/releases/latest");
            request.Headers.UserAgent.ParseAdd("Zipora/" + AppInfo.Version);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            return ParseRelease(doc.RootElement, AppStorage.IsPortable);
        }
        catch { return null; }
        finally { Gate.Release(); }
    }
    public static UpdateInfo? ParseRelease(JsonElement root, bool portable)
    {
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
            string latest = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
            if (!Version.TryParse(latest, out _)) return null;
            string url = root.GetProperty("html_url").GetString() ?? "https://github.com/Alnitak44/Zipora/releases/latest";
            string? fallbackZip = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (!portable && name.StartsWith("Zipora-", StringComparison.OrdinalIgnoreCase)
                    && name.Contains("Setup", StringComparison.OrdinalIgnoreCase) && name.Contains("x64", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                { url = asset.GetProperty("browser_download_url").GetString() ?? url; fallbackZip = null; break; }
                if (name.StartsWith("Zipora-", StringComparison.OrdinalIgnoreCase) && name.Contains("x64", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && name.Contains("portable", StringComparison.OrdinalIgnoreCase) == portable)
                    fallbackZip ??= asset.GetProperty("browser_download_url").GetString();
            }
            url = fallbackZip ?? url;
            string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            return new UpdateInfo(latest, notes.Length > 1800 ? notes[..1800] + "…" : notes, url) { IsNewer = CompareVersions(latest, AppInfo.Version) > 0 };
    }
    public static async Task CheckWithPromptAsync(Window owner, bool automatic = false)
    {
        var store = AuthStore.Load();
        if (automatic && !IsDue(store.UpdateFrequency, store.LastUpdateCheckUtc, DateTimeOffset.UtcNow)) return;
        store.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        try { store.Save(); } catch (Exception ex) { AppStorage.ReportSaveFailure(ex); }
        var info = await CheckAsync();
        if (!owner.IsVisible) return;
        if (info is { IsNewer: true })
        {
            var dialog = new NoticeDialog("发现新版本", $"Zipora v{info.LatestVersion}，当前 v{AppInfo.Version}。\n\n{info.Notes}\n\n常规版运行安装包覆盖升级；Portable 升级请保留 Data。", NoticeKind.Update, "前往下载", "稍后") { Owner = owner };
            if (dialog.ShowDialog() == true) OpenDownloadPage(info.DownloadUrl);
        }
        else if (!automatic)
            new NoticeDialog(info == null ? "检查更新失败" : "已是最新版本", info == null ? "无法连接 GitHub，可能是网络异常或请求受限，请稍后重试。" : $"当前版本 v{AppInfo.Version}，暂无更新的正式发行版。", info == null ? NoticeKind.Warning : NoticeKind.Success) { Owner = owner }.ShowDialog();
    }
    public static void OpenDownloadPage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com") return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
    public static int CompareVersions(string a, string b)
    {
        var left = a.Split('.').Select(int.Parse).ToArray(); var right = b.Split('.').Select(int.Parse).ToArray();
        for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
        { int c = (i < left.Length ? left[i] : 0).CompareTo(i < right.Length ? right[i] : 0); if (c != 0) return c; }
        return 0;
    }
}
