using System.Diagnostics;
using System.Text;

namespace SubtitleMerger.Core.Services;

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

            try
            {
                Task waitTask = process.WaitForExitAsync(cancellationToken);
                Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
                Task winner = await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false);
                if (winner == timeoutTask)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                    await Task.WhenAny(waitTask, Task.Delay(2000)).ConfigureAwait(false);
                    return new ProcessCapture(-1, "", "", true);
                }
                await waitTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                return new ProcessCapture(-1, "", "", true);
            }
            catch (Exception ex)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                return new ProcessCapture(-1, "", ex.Message, false);
            }

            string stdout, stderr;
            try { stdout = await stdoutTask.ConfigureAwait(false); } catch { stdout = ""; }
            try { stderr = await stderrTask.ConfigureAwait(false); } catch { stderr = ""; }
            return new ProcessCapture(process.ExitCode, stdout, stderr, false);
        }
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
