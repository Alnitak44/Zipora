using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace Zipora.Services;

public enum ArchiveListStatus { Ok, WrongPassword, Error }

public record ArchiveListResult(
    ArchiveListStatus Status,
    IReadOnlyList<ArchiveEntry> Entries,
    string ErrorMessage,
    ArchiveInfo? Info = null)
{
    public static ArchiveListResult Ok(IReadOnlyList<ArchiveEntry> entries, ArchiveInfo info) =>
        new(ArchiveListStatus.Ok, entries, "", info);
}

public enum PasswordTestResult { Ok, WrongPassword, Error }

public record ExtractResult(bool Success, bool WrongPassword, bool Cancelled, string Message)
{
    public bool InsufficientSpace { get; init; }
    public bool BlockedByPathProtection { get; init; }
}

public enum ArchiveTestStatus { Passed, Warning, WrongPassword, Failed, Cancelled }

public record ArchiveTestResult(ArchiveTestStatus Status, string Message);

/// <summary>
/// 官方 7z.exe 命令行的薄封装：列目录 / 试密码 / 解压（含选中项）/ 创建压缩包（含多格式与自解压）。
/// 所有调用均后台执行、无窗口、UTF-8 输出、绝不进入交互模式（始终显式传 -p）。
/// </summary>
public partial class SevenZipEngine
{
    /// <summary>自动尝试云端候选密码的上限，避免大压缩包反复验证耗时过久。</summary>
    public const int MaxAutoTryCandidates = 10;

    private readonly string _exePath;

    public SevenZipEngine(string exePath)
    {
        _exePath = exePath;
    }

    public bool IsAvailable => File.Exists(_exePath);
    public bool ZipSlipProtectionEnabled { get; set; } = true;

    [GeneratedRegex(@"^\s*(\d{1,3})%")]
    private static partial Regex ProgressRegex();

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>
    /// 列出压缩包内容。password 传 null 时用占位密码（不会弹出交互）。
    /// 若头部加密且密码不对，返回 WrongPassword（此时拿不到文件清单）。
    /// </summary>
    public ArchiveListResult List(string archive, string? password, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "l", "-slt", "-sccUTF-8", "-y",
            password is null ? "-p__ZiporaProbe__" : "-p" + password,
            "--", archive,
        };

        var (exitCode, stdout, stderr) = RunToCompletion(args, ct);

        if (exitCode == 0)
        {
            var parsed = ParseTechnicalListing(stdout);
            return ArchiveListResult.Ok(parsed.Entries, parsed.Info);
        }
        if (ContainsWrongPassword(stdout, stderr))
        {
            return new ArchiveListResult(ArchiveListStatus.WrongPassword, Array.Empty<ArchiveEntry>(), "需要正确的密码才能列出内容（文件名已加密）。");
        }
        return new ArchiveListResult(ArchiveListStatus.Error, Array.Empty<ArchiveEntry>(),
            ComposeError("无法读取压缩包", exitCode, stdout, stderr));
    }

    /// <summary>验证密码是否正确（执行 7z t 全量校验，大文件较慢但最可靠）。</summary>
    public PasswordTestResult TestPassword(string archive, string password, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "t", "-sccUTF-8", "-y", "-p" + password, "--", archive,
        };
        var (exitCode, stdout, stderr) = RunToCompletion(args, ct);
        if (exitCode == 0) return PasswordTestResult.Ok;
        if (ContainsWrongPassword(stdout, stderr)) return PasswordTestResult.WrongPassword;
        return PasswordTestResult.Error;
    }

    /// <summary>
    /// 全量验证压缩包数据与 CRC，不写出任何文件。password 传 null/空时显式使用空密码，
    /// 避免 7-Zip 进入交互提示。
    /// </summary>
    public async Task<ArchiveTestResult> TestArchiveAsync(
        string archive, string? password,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "t", "-bsp1", "-sccUTF-8", "-y", "-p" + (password ?? ""), "--", archive,
        };

        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        object outputLock = new();
        int lastPercent = -1;

        using var process = new Process { StartInfo = CreateStartInfo(args) };
        try
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (outputLock) stdoutLines.Add(e.Data);

                var match = ProgressRegex().Match(e.Data);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int percent) && percent > lastPercent)
                {
                    lastPercent = percent;
                    progress?.Report(percent);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (outputLock) stderrLines.Add(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { }
            }))
            {
                await process.WaitForExitAsync(ct);
            }
            process.WaitForExit(); // 冲刷异步输出缓冲

            string stdout;
            string stderr;
            lock (outputLock)
            {
                stdout = string.Join("\n", stdoutLines);
                stderr = string.Join("\n", stderrLines);
            }

            if (process.ExitCode == 0)
                return new ArchiveTestResult(ArchiveTestStatus.Passed, "压缩包完整性校验通过。");
            if (ContainsWrongPassword(stdout, stderr))
                return new ArchiveTestResult(ArchiveTestStatus.WrongPassword, "密码错误，无法完成完整性校验。");
            if (process.ExitCode == 1)
                return new ArchiveTestResult(ArchiveTestStatus.Warning,
                    "校验完成，但 7-Zip 报告了警告；请检查压缩包及其分卷文件是否完整。");

            return new ArchiveTestResult(ArchiveTestStatus.Failed,
                ComposeError("完整性校验失败", process.ExitCode, stdout, stderr));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return new ArchiveTestResult(ArchiveTestStatus.Cancelled, "完整性校验已取消。");
        }
        catch (Exception ex)
        {
            return new ArchiveTestResult(ArchiveTestStatus.Failed, "完整性校验失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 快速密码测试（字典破解用，借鉴 7zrpw 的加速技巧）：
    /// 密码错误时 7z 会立即输出错误标记；密码正确时 7z 会持续做完整性校验（大文件很慢）。
    /// 因此：出现错误标记 → 判错；2 秒内无错误标记 → 判对并终止进程。
    /// 返回 null 表示被外部取消。
    /// </summary>
    public bool? TestPasswordFast(string archive, string password, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "t", "-sccUTF-8", "-y", "-p" + password, "--", archive,
        };

        using var process = new Process { StartInfo = CreateStartInfo(args) };
        var output = new StringBuilder();
        object sync = new();
        bool wrongMarked = false;

        void OnData(string? data)
        {
            if (data is null) return;
            lock (sync)
            {
                output.AppendLine(data);
                foreach (var marker in FastWrongMarkers)
                {
                    if (data.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        wrongMarked = true;
                        break;
                    }
                }
            }
        }

        process.OutputDataReceived += (_, e) => OnData(e.Data);
        process.ErrorDataReceived += (_, e) => OnData(e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int FastTimeoutMs = 2000;
        try
        {
            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    return null;
                }
                lock (sync)
                {
                    if (wrongMarked)
                    {
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                        return false;
                    }
                }
                if (sw.ElapsedMilliseconds >= FastTimeoutMs)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    return true;
                }
                if (process.HasExited)
                {
                    if (process.ExitCode == 0) return true;
                    lock (sync)
                    {
                        string s = output.ToString();
                        foreach (var marker in FastWrongMarkers)
                        {
                            if (s.Contains(marker, StringComparison.OrdinalIgnoreCase)) return false;
                        }
                    }
                    return true; // 非密码错误的一般失败（如坏包），按 7zrpw 惯例视为命中以便人工确认
                }
                Thread.Sleep(40);
            }
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
    }

    /// <summary>快速测试的错误标记（出现任一即判密码错误）。</summary>
    private static readonly string[] FastWrongMarkers =
    {
        "Cannot open encrypted archive",
        "Data Error in encrypted file",
        "ERROR:",
        "Can't open as archive",
        "Wrong password",
        "Archives with Errors",
    };

    /// <summary>解压整个压缩包到 outDir（会自动创建）。progress 上报 0-100。</summary>
    public Task<ExtractResult> ExtractAsync(
        string archive, string? password, string outDir,
        IProgress<double>? progress = null, CancellationToken ct = default,
        ExtractionSpaceGuard? spaceGuard = null)
        => ExtractGuardedAsync(archive, password, outDir, null, progress, ct, spaceGuard);

    private async Task<ExtractResult> ExtractGuardedAsync(
        string archive, string? password, string outDir, IReadOnlyList<string>? entryPaths,
        IProgress<double>? progress, CancellationToken ct, ExtractionSpaceGuard? spaceGuard)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!ZipSlipProtectionEnabled)
                return entryPaths == null
                    ? await ExtractWholeCoreAsync(archive, password, outDir, progress, ct, spaceGuard)
                    : await ExtractSelectedCoreAsync(archive, password, outDir, entryPaths, progress, ct, spaceGuard);
            ArchivePathSecurity.EnsureNoReparseAncestors(outDir);
            if (entryPaths?.Count == 0)
            {
                Directory.CreateDirectory(outDir);
                return new ExtractResult(true, false, false, "空目录已准备。");
            }
            // Prevent replacement between listing and extraction. Do not cache an old UI listing.
            using var lease = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
            var listing = await Task.Run(() => List(archive, password, ct), ct);
            if (listing.Status != ArchiveListStatus.Ok)
                return new ExtractResult(false, listing.Status == ArchiveListStatus.WrongPassword, false,
                    "安全预检未完成：" + listing.ErrorMessage);
            await Task.Run(() => ArchivePathSecurity.InspectArchive(archive, listing, ct).ThrowIfUnsafe(), ct);
            await Task.Run(() =>
            {
                var requested = entryPaths?.Select(ArchiveSelectionPlan.ValidatePath)
                    .ToHashSet(StringComparer.Ordinal);
                var available = listing.Entries.Select(x => ArchiveSelectionPlan.ValidatePath(x.Path)).ToHashSet(StringComparer.Ordinal);
                if (requested != null && requested.Any(path => !available.Contains(path)))
                    throw new InvalidDataException("所选条目已发生变化，请重新打开压缩包。");
                foreach (var entry in listing.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    string source = ArchiveSelectionPlan.ValidatePath(entry.Path);
                    if (requested != null)
                    {
                        string candidate = source;
                        while (!requested.Contains(candidate))
                        {
                            int slash = candidate.LastIndexOf('/');
                            if (slash < 0) { candidate = ""; break; }
                            candidate = candidate[..slash];
                        }
                        if (candidate.Length == 0) continue;
                    }
                    string target = ArchiveSelectionPlan.ResolveInside(outDir, entry.Path);
                    // Do not overwrite an existing hard link (which has no ReparsePoint attribute).
                    // UI extraction already uses fresh staging directories.
                    if (File.Exists(target))
                        throw new InvalidDataException("目标文件已经存在，请解压到新的文件夹，防止覆盖硬链接目标。");
                }
            }, ct);
            var result = entryPaths == null
                ? await ExtractWholeCoreAsync(archive, password, outDir, progress, ct, spaceGuard)
                : await ExtractSelectedCoreAsync(archive, password, outDir, entryPaths, progress, ct, spaceGuard);
            if (result.Success) await Task.Run(() => ArchivePathSecurity.ValidateOutputTree(outDir, ct), ct);
            return result;
        }
        catch (OperationCanceledException) { return new ExtractResult(false, false, true, "已取消。"); }
        catch (InvalidDataException ex)
        {
            return new ExtractResult(false, false, false, "Zip Slip 安全防护已阻止解压：\n" + ex.Message)
                { BlockedByPathProtection = true };
        }
        catch (Exception ex) { return new ExtractResult(false, false, false, "安全预检或解压失败：" + ex.Message); }
    }

    private Task<ExtractResult> ExtractWholeCoreAsync(
        string archive, string? password, string outDir,
        IProgress<double>? progress, CancellationToken ct, ExtractionSpaceGuard? spaceGuard)
    {
        var args = new List<string>
        {
            "x", "-bsp1", "-sccUTF-8", "-y", "-p" + (password ?? ""),
            "-o" + outDir,
            "--", archive,
        };
        return ExtractCoreAsync(args, outDir, progress, ct, spaceGuard);
    }

    /// <summary>只解压选中的若干条目（保留包内相对路径）。-spd 关闭通配符，避免文件名里的特殊字符被当通配符。</summary>
    public Task<ExtractResult> ExtractSelectedAsync(
        string archive, string? password, string outDir,
        IReadOnlyList<string> entryPaths,
        IProgress<double>? progress = null, CancellationToken ct = default,
        ExtractionSpaceGuard? spaceGuard = null)
        => ExtractGuardedAsync(archive, password, outDir, entryPaths, progress, ct, spaceGuard);

    private async Task<ExtractResult> ExtractSelectedCoreAsync(
        string archive, string? password, string outDir, IReadOnlyList<string> entryPaths,
        IProgress<double>? progress, CancellationToken ct, ExtractionSpaceGuard? spaceGuard)
    {
        var args = new List<string>
        {
            "x", "-bsp1", "-sccUTF-8", "-y", "-spd", "-p" + (password ?? ""),
            "-o" + outDir,
            "--", archive,
        };
        if (entryPaths.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(outDir);
            return new ExtractResult(true, false, false, "空目录已准备。");
        }
        foreach (string path in entryPaths) ArchiveSelectionPlan.ValidatePath(path);
        using var workspace = new TemporaryWorkspace();
        string? listFile = null;
        try
        {
            string listRoot = workspace.CreateDirectory("lists");
            Directory.CreateDirectory(listRoot);
            listFile = Path.Combine(listRoot, Guid.NewGuid().ToString("N") + ".txt");
            await File.WriteAllLinesAsync(listFile, entryPaths, new UTF8Encoding(false), ct);
            args.Insert(2, "-scsUTF-8");
            // -- also disables @listfile parsing in 7-Zip; include filter must precede it.
            args.Insert(2, "-i@" + listFile);
            return await ExtractCoreAsync(args, outDir, progress, ct, spaceGuard);
        }
        finally
        {
            if (listFile != null)
            {
                try { File.Delete(listFile); } catch { }
            }
        }
    }

    // ---- 创建压缩包 ----

    /// <summary>
    /// 创建压缩包（压缩功能不需要任何云端凭证）。
    /// format: "7z" | "zip" | "tar" | "gzip" | "bzip2" | "xz" | "wim" | "sfx"
    /// gzip/bzip2/xz 不支持多文件与加密：自动先打 tar 再压缩（产物为 .tar.gz 等）。
    /// sfx 为 7z 自解压 exe（需要同目录 7z.sfx 模块）。
    /// password 传 null/空表示不加密；level 0-9；volumeSizeBytes 指定后输出 .001/.002… 分卷。
    /// </summary>
    public async Task<ExtractResult> CreateAsync(
        IReadOnlyList<string> sources, string outFile, string format,
        string? password, int level, bool encryptHeaders,
        long? volumeSizeBytes = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // gzip/bzip2/xz：两步（tar → 压缩），不支持密码
        if (format is "gzip" or "bzip2" or "xz")
        {
            using var workspace = new TemporaryWorkspace();
            string tmpTar = Path.Combine(workspace.CreateDirectory("create"), "content.tar");
            try
            {
                var r1 = await RunCreateCoreAsync(sources, tmpTar, "tar", null, level, false, null, progress, ct);
                if (!r1.Success) return r1;
                return await RunCreateCoreAsync(new[] { tmpTar }, outFile, format, null, level, false, volumeSizeBytes, progress, ct);
            }
            finally
            {
                try { if (File.Exists(tmpTar)) File.Delete(tmpTar); } catch { }
            }
        }

        return await RunCreateCoreAsync(sources, outFile, format, password, level, encryptHeaders, volumeSizeBytes, progress, ct);
    }

    private async Task<ExtractResult> RunCreateCoreAsync(
        IReadOnlyList<string> sources, string outFile, string format,
        string? password, int level, bool encryptHeaders,
        long? volumeSizeBytes,
        IProgress<double>? progress, CancellationToken ct)
    {
        var args = new List<string> { "a", "-y", "-bsp1", "-sccUTF-8" };

        if (format == "sfx")
        {
            args.Add("-t7z");
            args.Add("-sfx7z.sfx");
        }
        else
        {
            args.Add("-t" + format);
        }
        args.Add("-mx" + Math.Clamp(level, 0, 9));
        if (volumeSizeBytes is long volumeSize && volumeSize > 0)
            args.Add("-v" + volumeSize + "b");

        if (!string.IsNullOrEmpty(password))
        {
            args.Add("-p" + password);
            if (encryptHeaders && (format == "7z" || format == "sfx"))
                args.Add("-mhe=on"); // 仅 7z 系列支持加密文件名
        }

        args.Add("--");
        args.Add(outFile);
        foreach (var s in sources) args.Add(s);

        var errorLines = new List<string>();
        object errorLock = new();
        int lastPercent = -1;

        using var process = new Process { StartInfo = CreateStartInfo(args) };
        try
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var m = ProgressRegex().Match(e.Data);
                if (m.Success)
                {
                    int pct = int.Parse(m.Groups[1].Value);
                    if (pct > lastPercent)
                    {
                        lastPercent = pct;
                        progress?.Report(pct);
                    }
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (errorLock) errorLines.Add(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { }
            }))
            {
                await process.WaitForExitAsync(ct);
            }
            process.WaitForExit();

            int code = process.ExitCode;
            if (code == 0 || code == 1)
                return new ExtractResult(true, false, false, code == 1 ? "创建完成（有警告）。" : "创建完成。");

            return new ExtractResult(false, false, false,
                ComposeError("创建压缩包失败", code, "", string.Join("\n", errorLines)));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            return new ExtractResult(false, false, true, "已取消。");
        }
        catch (Exception ex)
        {
            return new ExtractResult(false, false, false, "创建压缩包失败：" + ex.Message);
        }
    }

    // ---- 解压公共核心 ----

    private async Task<ExtractResult> ExtractCoreAsync(
        IReadOnlyList<string> args, string outDir,
        IProgress<double>? progress, CancellationToken ct,
        ExtractionSpaceGuard? spaceGuard)
    {
        bool createdDir = !Directory.Exists(outDir);
        try
        {
            Directory.CreateDirectory(outDir);
        }
        catch (Exception ex)
        {
            return new ExtractResult(false, false, false, "无法创建目标目录：" + ex.Message);
        }

        var errorLines = new List<string>();
        object errorLock = new();
        int lastPercent = -1;

        using var process = new Process { StartInfo = CreateStartInfo(args) };
        try
        {
            // 预检与启动之间磁盘状态仍可能变化，因此在创建子进程前再核对一次。
            if (spaceGuard != null && IsBelowFreeSpaceLimit(spaceGuard))
            {
                CleanupFailedOutput(outDir, createdDir,
                    recursive: spaceGuard.DeletePartialOutput);
                return new ExtractResult(false, false, false,
                    "磁盘剩余空间已接近安全下限，已终止解压。")
                { InsufficientSpace = true };
            }

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var m = ProgressRegex().Match(e.Data);
                if (m.Success)
                {
                    int pct = int.Parse(m.Groups[1].Value);
                    if (pct > lastPercent)
                    {
                        lastPercent = pct;
                        progress?.Report(pct);
                    }
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (errorLock) errorLines.Add(e.Data);
            };

            ct.ThrowIfCancellationRequested();
            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            bool stoppedForSpace = false;
            using (ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* 进程可能已退出 */ }
            }))
            {
                Task waitTask = process.WaitForExitAsync(ct);
                while (!waitTask.IsCompleted)
                {
                    Task completed = await Task.WhenAny(waitTask, Task.Delay(250, ct));
                    if (completed == waitTask) break;
                    if (spaceGuard != null && IsBelowFreeSpaceLimit(spaceGuard))
                    {
                        stoppedForSpace = true;
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                        break;
                    }
                }
                if (!stoppedForSpace) await waitTask;
            }
            process.WaitForExit(); // 冲刷异步输出缓冲

            if (stoppedForSpace)
            {
                CleanupFailedOutput(outDir, createdDir,
                    recursive: spaceGuard?.DeletePartialOutput == true);
                return new ExtractResult(false, false, false,
                    "磁盘剩余空间已接近安全下限，已终止解压。")
                { InsufficientSpace = true };
            }

            int code = process.ExitCode;
            if (code == 0)
            {
                ct.ThrowIfCancellationRequested();
                return new ExtractResult(true, false, false, "解压完成。");
            }

            if (ContainsWrongPassword("", errorLines))
                return new ExtractResult(false, true, false, "密码错误。");

            string msg = ComposeError("解压失败", code, "", string.Join("\n", errorLines));
            CleanupFailedOutput(outDir, createdDir,
                recursive: spaceGuard?.DeletePartialOutput == true);
            return new ExtractResult(false, false, false, msg);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            CleanupFailedOutput(outDir, createdDir,
                recursive: spaceGuard?.DeletePartialOutput == true);
            return new ExtractResult(false, false, true, "已取消。");
        }
        catch (Exception ex)
        {
            CleanupFailedOutput(outDir, createdDir,
                recursive: spaceGuard?.DeletePartialOutput == true);
            return new ExtractResult(false, false, false, "解压失败：" + ex.Message);
        }
    }

    // ---- 内部工具 ----

    private (int ExitCode, string Stdout, string Stderr) RunToCompletion(
        IReadOnlyList<string> args, CancellationToken ct)
    {
        using var process = new Process { StartInfo = CreateStartInfo(args) };
        var stdout = new List<string>();
        var stderr = new List<string>();
        object sync = new();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sync) stdout.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sync) stderr.Add(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using (ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { }
            }))
            {
                process.WaitForExitAsync(ct).GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        process.WaitForExit();

        lock (sync)
            return (process.ExitCode, string.Join("\n", stdout), string.Join("\n", stderr));
    }

    private static bool ContainsWrongPassword(string stdout, IEnumerable<string> errorLines)
        => ContainsWrongPassword(stdout, string.Join("\n", errorLines));

    private static bool ContainsWrongPassword(string stdout, string stderr)
    {
        // 7-Zip 控制台输出恒为英文：wrong password 的各种变体
        return stdout.Contains("wrong password", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("wrong password", StringComparison.OrdinalIgnoreCase);
    }

    private static string ComposeError(string prefix, int exitCode, string stdout, string stderr)
    {
        string detail = ExtractErrorLine(stderr) ?? ExtractErrorLine(stdout) ?? $"退出码 {exitCode}";
        return $"{prefix}：{detail}";
    }

    private static string? ExtractErrorLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) && t.Length > 6)
                return t[6..].Trim();
        }
        return null;
    }

    private static void CleanupFailedOutput(string outDir, bool createdDir)
        => CleanupFailedOutput(outDir, createdDir, recursive: false);

    private static void CleanupFailedOutput(string outDir, bool createdDir, bool recursive)
    {
        // 只有当目录是我们创建的时候才清理，避免误删用户已有内容
        if (!createdDir) return;
        try
        {
            if (recursive && Directory.Exists(outDir))
            {
                Directory.Delete(outDir, recursive: true);
            }
            else if (Directory.Exists(outDir) &&
                     !Directory.EnumerateFileSystemEntries(outDir).Any())
            {
                Directory.Delete(outDir);
            }
        }
        catch { /* 清理失败不影响主流程 */ }
    }

    private static bool IsBelowFreeSpaceLimit(ExtractionSpaceGuard guard)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(guard.TargetPath));
            return !string.IsNullOrWhiteSpace(root) &&
                   new DriveInfo(root).AvailableFreeSpace <= guard.MinimumFreeBytes;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析 7z l -slt 的压缩包属性与条目技术清单。</summary>
    private static (List<ArchiveEntry> Entries, ArchiveInfo Info) ParseTechnicalListing(string stdout)
    {
        var entries = new List<ArchiveEntry>();
        ArchiveEntry? current = null;
        bool sawAny = false;
        bool inEntries = false;
        string format = "";
        long physicalSize = -1;
        long totalPhysicalSize = -1;
        string archiveMethod = "";
        bool? solid = null;
        int volumeCount = 1;

        foreach (var rawLine in stdout.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (line.StartsWith("----------", StringComparison.Ordinal))
            {
                inEntries = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                if (inEntries && current != null && sawAny)
                {
                    entries.Add(current);
                    current = null;
                    sawAny = false;
                }
                continue;
            }

            int idx = line.IndexOf(" = ", StringComparison.Ordinal);
            if (idx <= 0) continue;
            string key = line[..idx].Trim();
            string value = key == "Path" ? line[(idx + 3)..] : line[(idx + 3)..].Trim();

            if (!inEntries)
            {
                switch (key)
                {
                    case "Type":
                        format = value;
                        break;
                    case "Physical Size":
                        physicalSize = ParseSize(value);
                        break;
                    case "Total Physical Size":
                        totalPhysicalSize = ParseSize(value);
                        break;
                    case "Method":
                        archiveMethod = value;
                        break;
                    case "Solid":
                        solid = value == "+" ? true : value == "-" ? false : null;
                        break;
                    case "Volumes":
                        if (int.TryParse(value, out int count) && count > 0)
                            volumeCount = count;
                        break;
                }
                continue;
            }

            if (key == "Path")
            {
                if (current != null && sawAny) entries.Add(current);
                current = new ArchiveEntry { Path = value };
                sawAny = true;
                continue;
            }
            if (current == null) continue;

            switch (key)
            {
                case "Size":
                    current.Size = ParseSize(value);
                    sawAny = true;
                    break;
                case "Packed Size":
                    current.PackedSize = ParseSize(value);
                    sawAny = true;
                    break;
                case "Folder":
                    current.IsDir = value == "+";
                    sawAny = true;
                    break;
                case "Attributes":
                    // 7z 格式通常只输出 Attributes = D，不额外输出 Folder 字段。
                    if (value.StartsWith('D')) current.IsDir = true;
                    if (value.Contains("lrwx", StringComparison.Ordinal) ||
                        (uint.TryParse(value, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out uint attributes) &&
                         (attributes & (uint)FileAttributes.ReparsePoint) != 0)) current.IsLink = true;
                    sawAny = true;
                    break;
                case "Encrypted":
                    current.Encrypted = value == "+";
                    sawAny = true;
                    break;
                case "Symbolic Link":
                case "Hard Link":
                    current.IsLink |= !string.IsNullOrEmpty(value);
                    break;
                case "Method":
                    current.Method = value;
                    sawAny = true;
                    break;
                case "CRC":
                    current.Crc = value;
                    sawAny = true;
                    break;
            }
        }
        if (current != null && sawAny) entries.Add(current);

        return (entries, new ArchiveInfo
        {
            Format = format,
            PhysicalSize = physicalSize,
            TotalPhysicalSize = totalPhysicalSize,
            Method = archiveMethod,
            Solid = solid,
            VolumeCount = volumeCount,
        });
    }

    private static long ParseSize(string value)
        => long.TryParse(value, out long v) ? v : -1;
}
