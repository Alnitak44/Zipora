using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zipora.Services;

internal static class Program
{
    private static int _checks;
    private const string Master = "test-only master 你好 2026";
    private static void Expect(bool value, string label)
    {
        if (!value) throw new Exception(label);
        _checks++;
        Console.WriteLine("PASS " + label);
    }
    private static bool Fails(Action action)
    {
        try { action(); return false; } catch { return true; }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--try-open")
        {
            try { AppStorage.Initialize(args[1]); return 3; }
            catch { return 7; }
            finally { AppStorage.Dispose(); }
        }
        if (args.Length != 2) { Console.Error.WriteLine("Supply a NEW output directory and 7z.exe path."); return 2; }
        string output = Path.GetFullPath(args[0]);
        if (Directory.Exists(output)) { Console.Error.WriteLine("Output already exists; refusing to overwrite."); return 2; }
        Directory.CreateDirectory(output);
        string root = Path.Combine(output, "USB original 便携 & test");
        string app = Path.Combine(root, "App");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(Path.Combine(app, "dictionaries"));
        File.WriteAllText(Path.Combine(app, "dictionaries", "common.txt"), "test-only-dictionary");
        File.WriteAllText(Path.Combine(root, "portable.json"), "{}");
        try
        {
            AppStorage.Initialize(app);
            Expect(AppStorage.IsPortable, "portable marker selects USB storage");
            Expect(AppStorage.DataRoot == Path.Combine(root, "Data"), "Data stays beside App");
            Expect(AppStorage.TempRoot == Path.Combine(root, "Data", "Temp"), "temporary content stays on USB");
            Expect(File.ReadAllText(Path.Combine(AppStorage.DictionaryRoot, "common.txt")) == "test-only-dictionary", "editable dictionaries seeded into Data, not left in program files");
            Expect(Path.GetTempPath().StartsWith(AppStorage.TempRoot, StringComparison.OrdinalIgnoreCase), "child process TEMP inherits USB path");
            var layout = AppStorage.Layout;
            string inside = Path.Combine(root, "子目录", "test.zip");
            Expect(layout.DecodePath(layout.EncodePath(inside)) == inside, "install-relative path round trip");
            var relocated = new StorageLayout(Path.Combine(output, "moved installation", "App"), true);
            Expect(relocated.DecodePath(layout.EncodePath(inside)) == Path.Combine(relocated.Root, "子目录", "test.zip"), "moving the installation preserves relative paths");
            Expect(relocated.DecodePath(layout.EncodePath(root)) == relocated.Root, "remembered installation root follows a folder move");
            var changedDrive = new StorageLayout(@"Q:\移动文件夹\App", true);
            string volumePath = Path.Combine(Path.GetPathRoot(root)!, "USB files", "归档");
            Expect(changedDrive.DecodePath(layout.EncodePath(volumePath)) == @"Q:\USB files\归档", "USB drive-letter change remaps same-volume directories");
            Expect(layout.EncodePath(@"Z:\host-only\private") == "", "other computer's local directory is not persisted");
            Expect(layout.DecodePath("app:../../outside") == "", "relative traversal rejected");
            Expect(layout.DecodePath("drive:../outside") == "", "volume traversal rejected");
            Expect(layout.DecodePath("app:C:/outside") == "", "rooted path injection rejected");
            Expect(!new StorageLayout(Path.Combine(output, "ordinary")).IsPortable, "ordinary version remains ordinary");
            Expect(new StorageLayout(Path.Combine(output, "no-marker", "App"), true).IsPortable, "dedicated build remains portable without marker");
            Expect(new StorageLayout(@"Q:\", true).DataRoot == @"Q:\Data", "installation at volume root preserves absolute paths");

            var store = AuthStore.Load();
            string identity = store.DeviceFp;
            Expect(identity.Length == 64 && AuthStore.Load().DeviceFp == identity, "portable cloud identity is stable across reloads");
            Expect(!store.AutoContributePassword, "portable defaults to no automatic password upload");
            store.Theme = "Dark";
            store.LastExtractDir = inside;
            store.Save();
            store.Theme = "Light";
            store.Save();
            string config = AppStorage.DataFile("config.json");
            Expect(File.Exists(config + ".bak"), "preferences retain one prior backup");
            File.WriteAllText(config, "{broken config");
            var recovered = AuthStore.Load();
            Expect(recovered.Theme == "Dark" && recovered.DeviceFp == identity, "corrupt config recovers previous preferences and identity");
            recovered.Save();
            Expect(File.Exists(config + ".corrupt") && AuthStore.Load().Theme == "Dark", "saving recovered config preserves corrupt original");

            string image = Path.Combine(output, "source image.bin");
            File.WriteAllBytes(image, Encoding.UTF8.GetBytes("test-only-image-content"));
            string imported = AppStorage.ImportBackground(image);
            File.Delete(image);
            Expect(File.Exists(imported) && StorageLayout.Within(imported, AppStorage.DataRoot), "background copied, independent of original file");
            recovered.CustomBackgroundImagePath = imported;
            recovered.Save();
            Expect(AuthStore.Load().CustomBackgroundImagePath == imported, "copied background preference round trip");

            var vault = PasswordStore.PortableVault;
            Expect(Fails(() => vault.Create("short")), "short master password rejected");
            vault.Create(Master);
            string[] secrets = ["distinct-Secret-123", "你好-测试密码"];
            vault.Save(secrets);
            string vaultPath = AppStorage.DataFile("passwords.vault");
            Expect(!File.ReadAllText(vaultPath).Contains(secrets[0]) && !File.ReadAllText(vaultPath).Contains(Master), "neither saved password nor master password is plaintext");
            Expect(vault.Load().SequenceEqual(secrets), "encrypted passwords round trip");
            var before = File.ReadAllBytes(vaultPath);
            vault.Save(secrets);
            Expect(!before.SequenceEqual(File.ReadAllBytes(vaultPath)), "fresh AES-GCM nonce on each save");
            Expect(Fails(() => vault.Save(Enumerable.Repeat("x", 11))), "vault capacity enforced");
            Expect(Fails(() => vault.Create(Master)), "existing vault cannot be overwritten by create");
            PasswordStore.Lock();
            Expect(PasswordStore.NeedsUnlock && PasswordStore.Load().Count == 0, "locked vault exposes no passwords");
            before = File.ReadAllBytes(vaultPath);
            Expect(Fails(() => vault.Unlock("incorrect password")) && vault.IsLocked, "incorrect master leaves vault locked");
            Expect(before.SequenceEqual(File.ReadAllBytes(vaultPath)), "incorrect master does not modify encrypted file");
            vault.Unlock(Master);
            Expect(PasswordStore.TryAdd("third saved password", out _), "password manager adds to portable encrypted store");
            Expect(!PasswordStore.TryAdd("third saved password", out _), "duplicates rejected");
            PasswordStore.Remove("third saved password");
            Expect(PasswordStore.Load().SequenceEqual(secrets), "password manager removes exact entry");

            string copyPath = Path.Combine(output, "another-PC", "passwords.vault");
            Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);
            File.Copy(vaultPath, copyPath);
            using (var copied = new PortablePasswordVault(copyPath))
            {
                copied.Unlock(Master);
                Expect(copied.Load().SequenceEqual(secrets), "copied vault unlocks with master and no machine key");
            }
            vault.Save(secrets); // ensure latest backup contains the same values
            File.WriteAllText(vaultPath, "{corrupt vault");
            vault.Unlock(Master);
            Expect(vault.RecoveredBackup && vault.Load().SequenceEqual(secrets), "corrupt vault primary recovers authenticated backup");
            vault.Save(secrets);
            Expect(File.Exists(vaultPath + ".corrupt") && vault.Load().SequenceEqual(secrets), "recovery does not overwrite good backup with corrupt bytes");
            before = File.ReadAllBytes(vaultPath);
            using (new FileStream(vaultPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Expect(Fails(() => vault.Save(secrets)), "write failure is surfaced instead of pretending to save");
            Expect(before.SequenceEqual(File.ReadAllBytes(vaultPath)), "failed save retains original vault");
            Expect(!Directory.EnumerateFiles(AppStorage.DataRoot, ".write-*").Any(), "failed atomic writes leave no pending files");

            var limiter = LocalRateLimiter.Load();
            Expect(limiter.QueryRemaining == 10 && limiter.TryQuery().ok, "daily cloud query allowance unchanged");
            Expect(LocalRateLimiter.Load().QueryRemaining == 9, "quota follows persisted portable data");
            Expect(!limiter.TryQuery().ok, "cloud interval guard unchanged");
            for (int i = 1; i < 10; i++)
            {
                string date = DateTime.UtcNow.AddHours(8).ToString("yyyy-MM-dd");
                AppStorage.WriteText(AppStorage.DataFile("ratelimit.json"), JsonSerializer.Serialize(new { date, fetchCount = i, contribCount = 0, lastRequestUtc = 0L }));
                Expect(LocalRateLimiter.Load().TryQuery().ok, "quota count " + (i + 1));
            }
            limiter = LocalRateLimiter.Load();
            Expect(limiter.QueryRemaining == 0 && !limiter.TryQuery().ok, "daily allowance cannot be exceeded");
            AppStorage.WriteText(AppStorage.DataFile("ratelimit.json"), JsonSerializer.Serialize(new { date = DateTime.UtcNow.AddHours(8).ToString("yyyy-MM-dd"), fetchCount = 0, contribCount = 0, lastRequestUtc = 0L }));
            limiter = LocalRateLimiter.Load();
            using (new FileStream(AppStorage.DataFile("ratelimit.json"), FileMode.Open, FileAccess.Read, FileShare.None))
                Expect(!limiter.TryQuery().ok && limiter.QueryRemaining == 10, "failed quota save blocks network permission without consuming an allowance");
            var updatePreferences = AuthStore.Load();
            updatePreferences.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            updatePreferences.Save();
            Expect(AuthStore.Load().LastUpdateCheckUtc == updatePreferences.LastUpdateCheckUtc &&
                File.Exists(AppStorage.DataFile("config.json")), "update-check timestamp stays in portable preferences");
            AppStorage.Log("test-only portable error");
            Expect(File.Exists(Path.Combine(AppStorage.LogRoot, "crash.log")), "application logs stay in Data");
            Expect(Fails(ContextMenuInstaller.Install) && Fails(ContextMenuInstaller.Uninstall), "both registry mutations blocked in portable mode");

            string stale = Path.Combine(AppStorage.TempRoot, "session-orphan");
            Directory.CreateDirectory(stale);
            File.WriteAllText(Path.Combine(stale, ".lease"), "");
            File.WriteAllText(Path.Combine(stale, "old-content"), "stale");
            string live;
            using (var workspace = new TemporaryWorkspace())
            {
                live = workspace.CreateDirectory("drag");
                Expect(StorageLayout.Within(live, AppStorage.TempRoot), "extraction workspace stays inside USB Data");
                Expect(!Directory.Exists(stale), "unlocked crash residue collected at next workspace creation");
                using (var second = new TemporaryWorkspace())
                    Expect(Directory.Exists(live), "another live workspace is not collected");
                Expect(Fails(() => workspace.Remove(output)), "cleanup cannot target unrelated user folder");
            }
            Expect(!Directory.Exists(live), "normal workspace disposal cleans extracted temporary content");

            string sentinel = Path.Combine(AppStorage.TempRoot, "system", "live-sentinel");
            File.WriteAllText(sentinel, "active");
            Expect(ChildOpen(app) == 7, "second process cannot acquire the same Data lease");
            Expect(File.Exists(sentinel), "rejected second process cannot clean the first process's cache");
            string blocked = Path.Combine(output, "write-blocked");
            Directory.CreateDirectory(Path.Combine(blocked, "App"));
            File.WriteAllText(Path.Combine(blocked, "portable.json"), "{}");
            File.WriteAllText(Path.Combine(blocked, "Data"), "blocked by a file");
            Expect(ChildOpen(Path.Combine(blocked, "App")) == 7, "unwritable data layout fails closed without host fallback");

            var engine = new SevenZipEngine(Path.GetFullPath(args[1]));
            string source = Path.Combine(output, "source.txt");
            File.WriteAllText(source, "portable engine fixture");
            string archive = Path.Combine(output, "fixture.zip");
            Expect(engine.CreateAsync([source], archive, "zip", null, 1, false).GetAwaiter().GetResult().Success, "real ZIP creation in portable session");
            string extracted = Path.Combine(output, "extracted");
            Expect(engine.ExtractSelectedAsync(archive, null, extracted, ["source.txt"]).GetAwaiter().GetResult().Success, "real selected extraction uses portable list-file workspace");
            Expect(File.ReadAllText(Path.Combine(extracted, "source.txt")) == "portable engine fixture", "real extraction bytes are correct");
            Expect(engine.CreateAsync([source], Path.Combine(output, "fixture.tar.gz"), "gzip", null, 1, false).GetAwaiter().GetResult().Success, "two-stage compression uses portable tar workspace");
            Expect(!Directory.EnumerateDirectories(AppStorage.TempRoot, "session-*").Any(), "engine temporary workspaces removed after completion");
            AppStorage.Dispose();
            Expect(PasswordStore.NeedsUnlock && !File.Exists(sentinel), "exit locks password vault and clears private cache");
            Expect(File.Exists(Path.Combine(extracted, "source.txt")), "exit never deletes user-selected extraction output");
            Console.WriteLine($"PASS: {_checks} portable assertions. Output: {output}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { AppStorage.Dispose(); }
    }
    private static int ChildOpen(string app)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--try-open");
        info.ArgumentList.Add(app);
        using var process = Process.Start(info)!;
        if (!process.WaitForExit(10_000)) { process.Kill(true); throw new Exception("Child lease test timed out."); }
        return process.ExitCode;
    }
}
