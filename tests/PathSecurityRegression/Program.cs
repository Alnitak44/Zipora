using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Zipora.Services;

internal static class Program
{
    static int checks;
    static string root = "";
    static void Check(bool value, string name)
    { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static ArchiveEntry E(string path, bool dir = false, bool link = false) => new() { Path = path, IsDir = dir, IsLink = link };
    static void Reject(Action action, string name)
    { try { action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception(name); }
    static string MakeZip(string name, params (string Path, bool Link)[] entries)
    {
        string path = Path.Combine(root, name + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = zip.CreateEntry(item.Path);
            if (item.Link) entry.ExternalAttributes = unchecked((int)0xa1ff0000);
            using var writer = new StreamWriter(entry.Open());
            if (!item.Path.EndsWith('/') && !item.Path.EndsWith('\\')) writer.Write(item.Link ? "../outside" : "fixture content");
        }
        return path;
    }

    static async Task<int> Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Supply a NEW output directory and 7z.exe."); return 2; }
        root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root)) throw new Exception("Refusing to overwrite a test directory.");
        Directory.CreateDirectory(Path.Combine(root, "App"));
        File.WriteAllText(Path.Combine(root, "portable.json"), "{}");
        AppStorage.Initialize(Path.Combine(root, "App"));
        try { await Run(Path.GetFullPath(args[1])); Console.WriteLine($"PASS: {checks} checks"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { AppStorage.Dispose(); }
    }

    static async Task Run(string exe)
    {
        var engine = new SevenZipEngine(exe);
        var preferences = AuthStore.Load();
        Check(preferences.ZipSlipProtectionEnabled && engine.ZipSlipProtectionEnabled, "path protection defaults to enabled");
        preferences.Save();
        string config = AppStorage.DataFile("config.json");
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(config))!.AsObject();
        legacy.Remove("zipSlipProtection");
        File.WriteAllText(config, legacy.ToJsonString());
        Check(AuthStore.Load().ZipSlipProtectionEnabled, "legacy settings default to enabled");
        preferences = AuthStore.Load(); preferences.ZipSlipProtectionEnabled = false; preferences.ArchiveBombProtectionEnabled = true; preferences.Save();
        Check(!AuthStore.Load().ZipSlipProtectionEnabled && AuthStore.Load().ArchiveBombProtectionEnabled, "disabled path setting persists independently");
        preferences = AuthStore.Load(); preferences.ZipSlipProtectionEnabled = true; preferences.Save();
        Check(AuthStore.Load().ZipSlipProtectionEnabled, "re-enabled setting persists");
        var badNames = new[] { "../escape.txt", "a/../../escape.txt", "a\\..\\escape.txt", "/escape.txt", "C:/escape.txt",
            "C:relative.txt", "//server/share/escape.txt", "\\\\?\\C:\\escape.txt", "a.txt:stream", "NUL.txt", "CON .txt", "COM¹.txt",
            "LPT²", "CONIN$", "a. /file", "a//b", "a/./b", "trailing.", "a\nname.txt", "a\u0001.txt" };
        foreach (string name in badNames)
            Check(!ArchivePathSecurity.Inspect(new[] { E(name) }).IsSafe, "reject name " + name.Replace('\n', '_'));
        Check(ArchivePathSecurity.Inspect(new[] { E("中文 文件/emoji 😀.txt"), E("@literal.txt"), E("目录/", true) }).IsSafe, "normal Unicode names");
        Check(!ArchivePathSecurity.Inspect(new[] { E("x.txt"), E("X.TXT") }).IsSafe, "case alias rejected");
        Check(!ArchivePathSecurity.Inspect(new[] { E("data"), E("data/a.txt") }).IsSafe, "file parent conflict");
        Check(!ArchivePathSecurity.Inspect(new[] { E("link", link: true) }).IsSafe, "link metadata rejected");
        var bounded = ArchivePathSecurity.Inspect(Enumerable.Range(0, 500).Select(i => E($"../{i}")));
        Check(bounded.RiskCount == 500 && bounded.Issues.Count == 200 && bounded.Describe().Contains("300"), "bounded report preserves total risk count");
        Reject(() => ArchiveSelectionPlan.ResolveInside(root, "../outside"), "containment boundary");

        int index = 0;
        foreach (string name in badNames.Where(x => !x.Contains('\u0001')))
        {
            string zip = MakeZip("unsafe-" + index, ("safe.txt", false), (name, false));
            // Use the raw-directory inspector too, independent of 7z's treatment of invalid names.
            var rawReport = ArchivePathSecurity.InspectArchive(zip, ArchiveListResult.Ok(new[] { E("safe.txt") }, new ArchiveInfo { Format = "zip" }));
            Check(!rawReport.IsSafe, "raw ZIP name rejected " + index);
            string output = Path.Combine(root, "blocked-" + index++);
            var result = await engine.ExtractAsync(zip, null, output);
            Check(!result.Success && !Directory.Exists(output), "whole extraction blocked before writes");
            result = await engine.ExtractSelectedAsync(zip, null, output, new[] { "safe.txt" });
            Check(!result.Success && !Directory.Exists(output), "selection cannot bypass dangerous archive");
        }
        foreach (var contents in new[] {
            new[] { ("data", true), ("data/a.txt", false) },
            new[] { ("same.txt", false), ("SAME.TXT", false) },
            new[] { ("data", false), ("data/a.txt", false) } })
        {
            string zip = MakeZip("structure-" + index++, contents);
            string output = Path.Combine(root, "structure-output-" + index);
            var result = await engine.ExtractAsync(zip, null, output);
            Check(!result.Success && !Directory.Exists(output), "link/collision/parent conflict blocked");
        }
        foreach (var type in new[] { TarEntryType.SymbolicLink, TarEntryType.HardLink, TarEntryType.Fifo })
        {
            string tar = Path.Combine(root, type + ".tar");
            using (var file = File.Create(tar))
            using (var writer = new TarWriter(file))
            {
                var entry = new PaxTarEntry(type, "danger");
                if (type != TarEntryType.Fifo) entry.LinkName = "../outside";
                writer.WriteEntry(entry);
            }
            string output = Path.Combine(root, type + "-output");
            var result = await engine.ExtractAsync(tar, null, output);
            Check(!result.Success && !Directory.Exists(output), "TAR special entry blocked: " + type);
        }
        string safe = MakeZip("safe", ("data/", false), ("data/empty/", false), ("data/中文.txt", false), ("other.txt", false));
        var safeList = engine.List(safe, null);
        Check(ArchivePathSecurity.InspectArchive(safe, safeList).IsSafe, "real ZIP scan succeeds");
        string windowsZip = MakeZip("windows-separators", ("data\\", false), ("data\\file.txt", false));
        Check(ArchivePathSecurity.InspectArchive(windowsZip, engine.List(windowsZip, null)).IsSafe,
            "ZIP folder entries with Windows separators stay valid");
        string whole = Path.Combine(root, "whole");
        var normal = await engine.ExtractAsync(safe, null, whole);
        Check(normal.Success && File.ReadAllText(Path.Combine(whole, "data", "中文.txt")) == "fixture content" &&
            Directory.Exists(Path.Combine(whole, "data", "empty")), "safe whole extraction preserves files and empty folders: " + normal.Message);
        string selected = Path.Combine(root, "selected");
        var selection = await engine.ExtractSelectedAsync(safe, null, selected, new[] { "data/中文.txt" });
        Check(selection.Success && !File.Exists(Path.Combine(selected, "other.txt")), "safe selected extraction");
        var overwrite = await engine.ExtractAsync(safe, null, whole);
        Check(!overwrite.Success && overwrite.BlockedByPathProtection && File.ReadAllText(Path.Combine(whole, "other.txt")) == "fixture content", "existing files not overwritten");
        engine.ZipSlipProtectionEnabled = false;
        var withoutExtraScan = await engine.ExtractAsync(safe, null, whole);
        Check(withoutExtraScan.Success, "disabled protection skips the extra engine preflight");
        engine.ZipSlipProtectionEnabled = true;
        var restored = await engine.ExtractAsync(safe, null, whole);
        Check(!restored.Success && restored.BlockedByPathProtection, "re-enabled engine protection applies immediately");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            var result = await engine.ExtractAsync(safe, null, Path.Combine(root, "canceled"), ct: canceled.Token);
            Check(result.Cancelled && !Directory.Exists(Path.Combine(root, "canceled")), "canceled preflight creates no output");
        }
        string missing = Path.Combine(root, "missing");
        var missingResult = await engine.ExtractSelectedAsync(safe, null, missing, new[] { "gone.txt" });
        Check(!missingResult.Success && !Directory.Exists(missing), "stale selection fails closed");

        // Directory junctions require no Developer Mode or symbolic-link privilege.
        string outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel"), "unchanged");
        string junction = Path.Combine(root, "junction");
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "/c", "mklink", "/J", junction, outside }) psi.ArgumentList.Add(arg);
        using (var p = Process.Start(psi)!) { await p.WaitForExitAsync(); Check(p.ExitCode == 0, "create junction fixture"); }
        try
        {
            Reject(() => ArchiveSelectionPlan.ResolveInside(Path.Combine(junction, "new"), "file.txt"), "junction above output root blocked");
            var result = await engine.ExtractAsync(safe, null, Path.Combine(junction, "output"));
            Check(!result.Success && result.BlockedByPathProtection && !Directory.Exists(Path.Combine(outside, "output")), "engine blocks junction before creating target");
            Check(File.ReadAllText(Path.Combine(outside, "sentinel")) == "unchanged", "outside sentinel unchanged");
        }
        finally { Directory.Delete(junction); }

        string hardlink = Path.Combine(whole, "other.txt");
        File.Delete(hardlink);
        Check(CreateHardLink(hardlink, Path.Combine(outside, "sentinel"), IntPtr.Zero), "create hardlink fixture");
        var hardResult = await engine.ExtractAsync(safe, null, whole);
        Check(!hardResult.Success && File.ReadAllText(Path.Combine(outside, "sentinel")) == "unchanged", "existing hardlink target unchanged");
        string large = MakeZip("bounded-names", Enumerable.Range(0, 12000).Select(i => ($"folder/file-{i}.txt", false)).ToArray());
        var watch = Stopwatch.StartNew();
        var largeList = engine.List(large, null);
        Check(ArchivePathSecurity.InspectArchive(large, largeList).IsSafe && watch.Elapsed < TimeSpan.FromSeconds(10), "12k-entry scan completes without quadratic planning");
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr security);
}
