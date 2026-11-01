using System.Reflection;

namespace Zipora.Services;

/// <summary>应用元信息（版本号统一来源）。</summary>
public static class AppInfo
{
    /// <summary>如 1.2.0。读取程序集版本（由 csproj 的 &lt;Version&gt; 生成）。</summary>
    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
