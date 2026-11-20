using System.IO;

namespace Zipora.Services;

/// <summary>字典文件中的一条候选密码及其原始行号（1 基，按文件真实行号）。</summary>
public sealed record DictionaryEntry(int Line, string Password);

/// <summary>
/// 把字典文本转换成待测序列：去空行、按 Ordinal 去重、从指定行开始。
/// 去重针对整个文件，避免“起始行”之前出现过的密码被重复测试。
/// </summary>
public static class DictionaryPlan
{
    public static List<DictionaryEntry> Build(IReadOnlyList<string> lines, int startLine)
    {
        if (startLine < 1) throw new ArgumentOutOfRangeException(nameof(startLine));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var plan = new List<DictionaryEntry>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            string password = lines[i].Trim();
            if (password.Length == 0 || !seen.Add(password)) continue;
            if (i + 1 < startLine) continue;
            plan.Add(new DictionaryEntry(i + 1, password));
        }
        return plan;
    }
}
