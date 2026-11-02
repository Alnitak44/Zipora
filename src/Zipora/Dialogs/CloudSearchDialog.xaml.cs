using System.Windows;
using Wpf.Ui.Controls;
using Zipora.Services;
using Zipora.Services.KeyFetch;

namespace Zipora.Dialogs;

/// <summary>Public cloud password search. The service is available without an activation code.</summary>
public partial class CloudSearchDialog : FluentWindow
{
    private readonly IKeyFetchClient _client;
    private string? _lastPassword;

    public CloudSearchDialog(string deviceFp, string? initialMd5)
    {
        InitializeComponent();
        _client = CreateClient(deviceFp);
        if (!string.IsNullOrEmpty(initialMd5)) Md5Box.Text = initialMd5;
        Loaded += (_, _) => RefreshQuotaText();
        Backdrop.ApplyFromSetting(this);
    }

    private static IKeyFetchClient CreateClient(string deviceFp)
    {
        try { return new HttpKeyFetchClient("https://KeyFetch.site", deviceFp); }
        catch (ArgumentException) { return NullKeyFetchClient.Instance; }
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        var md5 = Md5Box.Text.Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(md5, "^[a-f0-9]{32}$"))
        {
            SetResult("请输入 32 位十六进制 MD5，或点击“选择文件”自动计算。", true);
            return;
        }
        await SearchAsync(md5);
    }

    private async void PickFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择要查询的文件（自动计算 MD5）", Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        PickFileButton.IsEnabled = false;
        SearchButton.IsEnabled = false;
        SetSearching(true);
        SetResult("正在计算文件 MD5…", false);
        try
        {
            var md5 = await HashHelper.ComputeMd5Async(dialog.FileName, null, CancellationToken.None);
            Md5Box.Text = md5;
            SetSearching(false);
            await SearchAsync(md5);
        }
        catch (Exception ex)
        {
            SetSearching(false);
            SetResult("计算 MD5 失败：" + ex.Message, true);
        }
        finally
        {
            PickFileButton.IsEnabled = true;
            SearchButton.IsEnabled = true;
        }
    }

    private async Task SearchAsync(string md5)
    {
        var limiter = LocalRateLimiter.Load();
        var (ok, limitMessage) = limiter.TryQuery();
        RefreshQuotaText(limiter);
        if (!ok) { SetResult(limitMessage, true); return; }
        SearchButton.IsEnabled = false;
        SetSearching(true);
        SetResult("正在向云端搜索…", false);
        try
        {
            var result = await _client.FetchPasswordAsync(md5, 0);
            switch (result.Status)
            {
                case FetchStatus.Ok:
                    _lastPassword = result.Password;
                    SetResult($"云端找到密码：{result.Password}", false);
                    CopyButton.IsEnabled = true;
                    break;
                case FetchStatus.NotFound:
                    SetResult("云端未收录该文件。", false);
                    CopyButton.IsEnabled = false;
                    break;
                case FetchStatus.QuotaExceeded:
                    SetResult(result.Message ?? "今日查询额度已用完，明日恢复。", true);
                    CopyButton.IsEnabled = false;
                    break;
                default:
                    SetResult(result.Message ?? "搜索失败。", true);
                    CopyButton.IsEnabled = false;
                    break;
            }
        }
        catch (Exception ex) { SetResult("搜索失败：" + ex.Message, true); }
        finally { SetSearching(false); SearchButton.IsEnabled = true; }
    }

    private void SetSearching(bool searching) => ResultSpinner.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshQuotaText(LocalRateLimiter? limiter = null)
    {
        limiter ??= LocalRateLimiter.Load();
        QuotaText.Text = $"今日剩余查询配额：{limiter.QueryRemaining} / 10";
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastPassword == null) return;
        Clipboard.SetText(_lastPassword);
        SetResult("已复制密码到剪贴板。", false);
    }

    private void SetResult(string text, bool error)
    {
        ResultText.Text = text;
        ResultText.Foreground = error
            ? (System.Windows.Media.Brush)FindResource("ZiporaWarningBrush")
            : (System.Windows.Media.Brush)FindResource("SystemFillColorSuccessBrush");
        ResultPanel.Visibility = Visibility.Visible;
    }
}
