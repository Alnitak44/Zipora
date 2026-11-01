using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Threading;
using Zipora.Services;

internal static class Program
{
    static int checks;
    static string root = "";
    [STAThread]
    static int Main(string[] args)
    {
        root = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(root);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        int exit = 1;
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try { await Run(args[1]); Console.WriteLine($"PASS: {checks} checks"); exit = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
        return exit;
    }
    static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    static void Reject(Action action, string label)
    { try { action(); } catch (InvalidDataException) { Check(true, label); return; } throw new Exception(label); }
    static ArchiveEntry E(string path, bool dir = false, long size = 3) => new() { Path = path, IsDir = dir, Size = size };
    static async Task Run(string exe)
    {
        var entries = new[] { E("parent/data", true), E("parent/data/empty", true),
            E("parent/data/sub/a.txt"), E("parent/data/a.txt"), E("parent/data2/no.txt"), E("root.txt") };
        var many = Enumerable.Range(0, 20000).Select(i => E($"file-{i}.txt")).ToList();
        var timer = Stopwatch.StartNew();
        var manyPlan = ArchiveSelectionPlan.Create(many, many);
        Check(manyPlan.ExtractionPaths.Count == 20000 && timer.Elapsed < TimeSpan.FromSeconds(3),
            $"20k selected files planned in {timer.ElapsedMilliseconds}ms");
        var plan = ArchiveSelectionPlan.Create(entries, new[] { entries[0], entries[2], entries[5] });
        Check(plan.RootCount == 2 && plan.ExtractionPaths.Count == 3, "overlapping selection deduplicated");
        Check(plan.Items.Any(x => x.ExportPath == "data/empty" && x.IsDirectory), "empty directory preserved");
        Check(plan.Items.Any(x => x.ExportPath == "data/sub" && x.IsDirectory), "implicit parent preserved");
        Check(!plan.ExtractionPaths.Contains("parent/data2/no.txt"), "directory prefix boundary");
        Check(ArchiveSelectionPlan.Create(entries, ArchiveSelectionPlan.ArchiveRoots(entries)).RootCount == 2, "whole archive roots");
        Reject(() => ArchiveSelectionPlan.Create(new[] { E("a/f"), E("b/f") }, new[] { E("a/f"), E("b/f") }), "collision rejected");
        Reject(() => ArchiveSelectionPlan.Create(new[] { E("a"), E("A") }, new[] { E("a") }), "case collision rejected");
        var link = E("link"); link.IsLink = true;
        Reject(() => ArchiveSelectionPlan.Create(new[] { link }, new[] { link }), "link rejected");
        foreach (string bad in new[] { "../escape", "/absolute", "C:/a", "a/../b", "a:b", "NUL.txt", "a. ", "a//b", "a\nfile" })
            Reject(() => ArchiveSelectionPlan.ValidatePath(bad), "unsafe path " + bad.Replace("\n", "[newline]"));

        string input = Path.Combine(root, "input");
        Directory.CreateDirectory(Path.Combine(input, "data", "empty"));
        Directory.CreateDirectory(Path.Combine(input, "data", "sub"));
        File.WriteAllText(Path.Combine(input, "data", "sub", "中文.txt"), "nested", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(input, "data", "@file.txt"), "special", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(input, "outside.txt"), "not selected");
        string archive = Path.Combine(root, "fixture.zip");
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = input, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "a", "-tzip", archive, "." }) psi.ArgumentList.Add(arg);
        using (var process = Process.Start(psi)!) { await process.WaitForExitAsync(); Check(process.ExitCode == 0, "create ZIP fixture"); }
        var engine = new SevenZipEngine(exe);
        var listing = await Task.Run(() => engine.List(archive, null));
        Check(listing.Status == ArchiveListStatus.Ok, "list fixture");
        var actual = ArchiveSelectionPlan.Create(listing.Entries, new[] { E("data", true) });
        string raw = Path.Combine(root, "raw");
        var extracted = await engine.ExtractSelectedAsync(archive, null, raw, actual.ExtractionPaths);
        Check(extracted.Success, "exact selected extraction: " + extracted.Message);
        actual.ValidateExtractedFiles(raw);
        string ready = Path.Combine(root, "ready");
        // COM descriptors and lazy streams are exercised before ExportTo moves source files.
        int preparations = 0;
        var data = new ArchiveDragDataObject(actual, async () => { preparations++; await Task.Delay(20); return raw; });
        var desc = F(ArchiveDragDataObject.DescriptorFormat, TYMED.TYMED_HGLOBAL);
        data.GetData(ref desc, out var medium);
        try
        {
            IntPtr p = GlobalLock(medium.unionmember);
            try
            {
                Check(Marshal.ReadInt32(p) == actual.Items.Count, "FILEGROUPDESCRIPTOR count");
                Check(Marshal.PtrToStringUni(p + 4 + 72) == "data", "descriptor filename layout");
            }
            finally { GlobalUnlock(medium.unionmember); }
        }
        finally { ReleaseStgMedium(ref medium); }
        Check(preparations == 0, "enumeration never extracts");
        var imageMethod = typeof(Zipora.MainWindow).GetMethod("InitializeNativeDragImage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Check((bool)imageMethod.Invoke(null, new object[] { data, true })!, "Shell native drag image initialization");
        int index = actual.Items.ToList().FindIndex(x => !x.IsDirectory);
        for (int repeat = 0; repeat < 2; repeat++)
        {
            var content = F(ArchiveDragDataObject.ContentsFormat, TYMED.TYMED_ISTREAM, index);
            data.GetData(ref content, out medium);
            try
            {
                var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                try { stream.Stat(out var stat, 1); Check(stat.cbSize == actual.Items[index].Size, "native stream length"); }
                finally { Marshal.ReleaseComObject(stream); }
            }
            finally { ReleaseStgMedium(ref medium); }
        }
        Check(preparations == 1, "materialize once");
        string shellTarget = Path.Combine(root, "shell-copy");
        Directory.CreateDirectory(shellTarget);
        ShellCopy.Copy(data, shellTarget);
        Check(File.ReadAllText(Path.Combine(shellTarget, "data", "sub", "中文.txt")) == "nested", "Windows Shell copies virtual nested file");
        Check(Directory.Exists(Path.Combine(shellTarget, "data", "empty")), "Windows Shell copies empty folder");
        string asyncTarget = Path.Combine(root, "shell-async");
        Directory.CreateDirectory(asyncTarget);
        int deferred = 0;
        var asyncData = new ArchiveDragDataObject(actual, async () => { deferred++; await Task.Delay(150); return raw; });
        ShellCopy.Copy(asyncData, asyncTarget, true);
        if (asyncData.IsAsyncOperation) await asyncData.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Check(asyncData.Failure == null && deferred == 1, "Shell deferred extraction once");
        Check(File.ReadAllText(Path.Combine(asyncTarget, "data", "sub", "中文.txt")) == "nested", "Shell asynchronous drop completes");
        string onlyEmpty = Path.Combine(root, "shell-empty");
        Directory.CreateDirectory(onlyEmpty);
        var emptyPlan = ArchiveSelectionPlan.Create(new[] { E("blank", true) }, new[] { E("blank", true) });
        var emptyData = new ArchiveDragDataObject(emptyPlan, () => throw new Exception("Must not extract an empty folder"));
        ShellCopy.Copy(emptyData, onlyEmpty);
        Check(Directory.Exists(Path.Combine(onlyEmpty, "blank")), "Shell empty-only drop without extraction");
        data.StartOperation(IntPtr.Zero); Check(data.IsAsyncOperation, "async operation started");
        data.EndOperation(0, IntPtr.Zero, 1); Check((await data.Completion).Effect == 1, "async completion");
        actual.ExportTo(raw, ready);
        Check(File.ReadAllText(Path.Combine(ready, "data", "sub", "中文.txt")) == "nested", "nested content exact");
        Check(Directory.Exists(Path.Combine(ready, "data", "empty")), "empty directory exported");
        Check(!File.Exists(Path.Combine(ready, "outside.txt")), "unselected file absent");
        string empty = Path.Combine(root, "empty-selection");
        var emptyResult = await engine.ExtractSelectedAsync(archive, null, empty, Array.Empty<string>());
        Check(emptyResult.Success && !Directory.EnumerateFileSystemEntries(empty).Any(), "empty selection never extracts whole archive");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await engine.ExtractSelectedAsync(archive, null, Path.Combine(root, "cancelled"), actual.ExtractionPaths, ct: cancelled.Token); }
        catch (OperationCanceledException) { }
        Check(!Directory.Exists(Path.Combine(root, "cancelled")), "pre-cancel creates no output");
        var noSpace = await engine.ExtractSelectedAsync(archive, null, Path.Combine(root, "no-space"),
            actual.ExtractionPaths, spaceGuard: new(Path.Combine(root, "no-space"), long.MaxValue, true));
        Check(!noSpace.Success && noSpace.InsufficientSpace, "space guard stops before extraction");
        string encrypted = Path.Combine(root, "encrypted.7z");
        var encPsi = new ProcessStartInfo(exe) { WorkingDirectory = input, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "a", "-t7z", "-pRegression-only", encrypted, "." }) encPsi.ArgumentList.Add(arg);
        using (var p = Process.Start(encPsi)!) { await p.WaitForExitAsync(); Check(p.ExitCode == 0, "create encrypted fixture"); }
        var wrong = await engine.ExtractSelectedAsync(encrypted, "wrong", Path.Combine(root, "wrong"), actual.ExtractionPaths);
        Check(!wrong.Success && wrong.WrongPassword, "wrong password fails without prompt hang");
        var right = await engine.ExtractSelectedAsync(encrypted, "Regression-only", Path.Combine(root, "right"), actual.ExtractionPaths);
        Check(right.Success, "encrypted selection succeeds");
        string largeInput = Path.Combine(root, "large.bin");
        using (var f = File.Create(largeInput))
        {
            byte[] block = new byte[1024 * 1024];
            new Random(42).NextBytes(block);
            for (int n = 0; n < 64; n++) f.Write(block);
        }
        string largeArchive = Path.Combine(root, "large.zip");
        var largePsi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "a", "-tzip", "-mx=0", largeArchive, largeInput }) largePsi.ArgumentList.Add(arg);
        using (var p = Process.Start(largePsi)!) { await p.WaitForExitAsync(); Check(p.ExitCode == 0, "create large fixture"); }
        using var activeCancel = new CancellationTokenSource();
        string cancelOutput = Path.Combine(root, "active-cancel");
        var cancelledResult = await engine.ExtractSelectedAsync(largeArchive, null, cancelOutput, new[] { "large.bin" },
            new CancelOnProgress(activeCancel), activeCancel.Token, new(cancelOutput, 0, true));
        Check(cancelledResult.Cancelled && !cancelledResult.Success, "cancel extraction after progress begins");
        Check(!Directory.Exists(cancelOutput), "cancel waits for process exit and removes partial output");
        using var temp1 = new TemporaryWorkspace();
        string tmp1 = temp1.CreateDirectory("drag");
        using (var temp2 = new TemporaryWorkspace()) { Check(Directory.Exists(tmp1), "other active session preserved"); }
        temp1.Remove(tmp1); Check(!Directory.Exists(tmp1), "owned temporary cleanup");
    }
    static FORMATETC F(short id, TYMED medium, int index = -1) => new()
    { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = medium };
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM m);
    sealed class CancelOnProgress(CancellationTokenSource cancellation) : IProgress<double>
    { public void Report(double value) => cancellation.Cancel(); }
}
