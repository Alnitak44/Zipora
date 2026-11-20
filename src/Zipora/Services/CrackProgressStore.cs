using System.IO;
using System.Text.Json;

namespace Zipora.Services;

/// <summary>
/// 按「压缩包 + 字典」记住中止时的续跑行号。
/// 字典长度或修改时间变化即视为旧断点失效，避免行号错位后漏测。
/// </summary>
public static class CrackProgressStore
{
    private sealed class Item
    {
        public string Archive { get; set; } = "";
        public string Dictionary { get; set; } = "";
        public long Length { get; set; }
        public long Ticks { get; set; }
        public int NextLine { get; set; }
    }

    private const int MaxEntries = 20;

    private static string FilePath => AppStorage.DataFile("crack-progress.json");

    public static int? Load(string archive, string dictionary)
    {
        try
        {
            var info = new FileInfo(dictionary);
            var item = Read().FirstOrDefault(x =>
                string.Equals(x.Archive, archive, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Dictionary, dictionary, StringComparison.OrdinalIgnoreCase) &&
                x.Length == info.Length && x.Ticks == info.LastWriteTimeUtc.Ticks);
            return item?.NextLine;
        }
        catch { return null; }
    }

    public static void Save(string archive, string dictionary, int nextLine)
    {
        try
        {
            var info = new FileInfo(dictionary);
            var items = Read();
            items.RemoveAll(x =>
                string.Equals(x.Archive, archive, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Dictionary, dictionary, StringComparison.OrdinalIgnoreCase));
            items.Add(new Item
            {
                Archive = archive,
                Dictionary = dictionary,
                Length = info.Length,
                Ticks = info.LastWriteTimeUtc.Ticks,
                NextLine = nextLine,
            });
            if (items.Count > MaxEntries) items.RemoveRange(0, items.Count - MaxEntries);
            AppStorage.WriteText(FilePath, JsonSerializer.Serialize(items));
        }
        catch { }
    }

    public static void Clear(string archive, string dictionary)
    {
        try
        {
            var items = Read();
            int removed = items.RemoveAll(x =>
                string.Equals(x.Archive, archive, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Dictionary, dictionary, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) AppStorage.WriteText(FilePath, JsonSerializer.Serialize(items));
        }
        catch { }
    }

    private static List<Item> Read()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path)) return new List<Item>();
            return JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(path)) ?? new List<Item>();
        }
        catch { return new List<Item>(); }
    }
}
