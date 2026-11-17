using System.Diagnostics;
using System.Text;

namespace SubFusion.Core.Services;

/// <summary>一次性进程捕获结果。</summary>
public sealed record ProcessCapture(int ExitCode, string Stdout, string Stderr, bool TimedOut);

/// <summary>
/// 子进程封装：对应原 core 里的 subprocess.run(...) 调用。
/// CREATE_NO_WINDOW → ProcessStartInfo.CreateNoWindow；
/// encoding="utf-8", errors="replace" → UTF8Encoding(false, false)；
/// timeout=N → 超时 Kill 并返回 TimedOut，由调用方按「探测失败」处理（与 Python 捕获 TimeoutExpired 一致）。
/// </summary>
public static class ProcessUtil
{
    public static readonly Encoding Utf8Replace = new UTF8Encoding(false, false);

    public static ProcessStartInfo MakeStartInfo(string exe, IEnumerable<string> args,
                                                 string? workingDirectory = null,
                                                 bool redirectStdin = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,          // CREATE_NO_WINDOW (0x08000000)
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdin,
            StandardOutputEncoding = Utf8Replace,
            StandardErrorEncoding = Utf8Replace,
        };
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;
        return psi;
    }

    /// <summary>
    /// 运行并捕获输出。Python 的 stderr=STDOUT 合并流在 .NET 无原生等价物，
    /// 调用方可把 Stdout + Stderr 拼接后做子串判断（ffmpeg -filters/-encoders 输出都写到 stdout）。
    /// </summary>
    public static async Task<ProcessCapture> RunAsync(string exe, IReadOnlyList<string> args,
                                                       int timeoutSeconds,
                                                       string? workingDirectory = null,
                                                       CancellationToken cancellationToken = default)
    {
        ProcessStartInfo psi = MakeStartInfo(exe, args, workingDirectory);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动进程");
        }
        catch (Exception ex)
        {
            return new ProcessCapture(-1, "", ex.Message, false);
        }

        using (process)
        {
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            // 即使 2 秒收尾窗口已过，也要在后台观察两条读取任务的异常，
            // 否则被 Kill 打断的管道读取会留下未观察的 Task 异常。
            Observe(stdoutTask);
            Observe(stderrTask);

            try
            {
                Task waitTask = process.WaitForExitAsync(cancellationToken);
                Observe(waitTask);
                Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
                Task winner = await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false);
                if (winner == timeoutTask)
                {
                    KillTree(process);
                    await SettleAsync(waitTask, stdoutTask, stderrTask).ConfigureAwait(false);
                    string partialOut, partialErr;
                    try { partialOut = await stdoutTask.ConfigureAwait(false); } catch { partialOut = ""; }
                    try { partialErr = await stderrTask.ConfigureAwait(false); } catch { partialErr = ""; }
                    return new ProcessCapture(-1, partialOut, partialErr, true);
                }
                // 进程退出后，两条管道读取任务可能还在收尾；不等它们完成就离开
                // using 会 Dispose 仍在读的流，超时子进程残留句柄会线性累积。
                await SettleAsync(waitTask, stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                await SettleAsync(null, stdoutTask, stderrTask).ConfigureAwait(false);
                return new ProcessCapture(-1, "", "", true);
            }
            catch (Exception ex)
            {
                KillTree(process);
                await SettleAsync(null, stdoutTask, stderrTask).ConfigureAwait(false);
                return new ProcessCapture(-1, "", ex.Message, false);
            }

            string stdout, stderr;
            try { stdout = await stdoutTask.ConfigureAwait(false); } catch { stdout = ""; }
            try { stderr = await stderrTask.ConfigureAwait(false); } catch { stderr = ""; }
            int code;
            try { code = process.ExitCode; } catch { code = -1; }
            return new ProcessCapture(code, stdout, stderr, false);
        }
    }

    /// <summary>
    /// 结束整个进程树。ffprobe/ffmpeg 被超时或取消打断时必须连子进程一起终止，
    /// 否则子进程仍握着 stdout/stderr 管道，读取任务永远完不成、句柄也不会释放。
    /// </summary>
    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
    }

    /// <summary>收尾：等进程退出与两条输出流读取结束，各最多等 2 秒，保证句柄释放。</summary>
    private static void Observe(Task task)
        => _ = task.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

    private static async Task SettleAsync(Task? waitTask, Task stdoutTask, Task stderrTask)
    {
        Task all = Task.WhenAll(waitTask ?? Task.CompletedTask, stdoutTask, stderrTask);
        await Task.WhenAny(all, Task.Delay(2000)).ConfigureAwait(false);
    }

    /// <summary>
    /// 等价于 shutil.which(name)：按 PATH 顺序查找可执行文件（Windows 下补 .exe / PATHEXT）。
    /// </summary>
    public static string? Which(string name)
    {
        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        string[] exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool hasExt = System.IO.Path.HasExtension(name);

        foreach (string dir in pathEnv.Split(System.IO.Path.PathSeparator,
                                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (!hasExt)
                {
                    foreach (string ext in exts)
                    {
                        string candidate = System.IO.Path.Combine(dir, name + ext.ToLowerInvariant());
                        if (File.Exists(candidate)) return System.IO.Path.GetFullPath(candidate);
                    }
                }
                string direct = System.IO.Path.Combine(dir, name);
                if (File.Exists(direct)) return System.IO.Path.GetFullPath(direct);
            }
            catch { /* 无权限或非法目录，跳过 */ }
        }
        return null;
    }
}
