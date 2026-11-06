using System.Diagnostics;
using System.Globalization;

namespace SubtitleMerger.Core.Services;

/// <summary>解析 ffmpeg `-progress pipe:1` 输出（原 core.ProgressState）。</summary>
public sealed class ProgressState
{
    public double Position { get; private set; }
    public double Speed { get; private set; }

    public void Feed(string? line)
    {
        line = (line ?? "").Trim();
        if (line.Length == 0) return;
        int eq = line.IndexOf('=');
        if (eq < 0) return;
        string key = line[..eq];
        string value = line[(eq + 1)..].Trim();

        switch (key)
        {
            case "out_time_us":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us))
                    Position = us / 1000000.0;
                break;
            case "out_time_ms":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms))
                    Position = ms / 1000.0;
                break;
            case "out_time":
            {
                string[] parts = value.Split(':');
                if (parts.Length == 3 &&
                    int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) &&
                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
                    Position = h * 3600 + m * 60 + s;
                break;
            }
            case "speed":
                string speedText = value.TrimEnd('x');
                Speed = double.TryParse(speedText, NumberStyles.Float, CultureInfo.InvariantCulture, out double sp)
                    ? sp : 0.0;
                break;
        }
    }

    public double Percent(double? duration)
        => duration is > 0 ? Math.Min(100.0, Position / duration.Value * 100.0) : 0.0;

    public double? Eta(double? duration)
        => Speed > 0 && duration is > 0 && duration.Value > Position
            ? (duration.Value - Position) / Speed
            : null;
}

/// <summary>原 core.run_ffmpeg：启动子进程、解析进度、收集 stderr、支持随时停止。</summary>
public static class FfmpegRunner
{
    public static async Task<(int Code, string Error)> RunFfmpegAsync(
        IReadOnlyList<string> cmd,
        double? duration,
        Action<double, double, double?>? onProgress,
        CancellationToken cancelToken,
        List<string>? logLines = null,
        int maxLog = 40,
        string? cwd = null)
    {
        ProcessStartInfo psi = ProcessUtil.MakeStartInfo(cmd[0], cmd.Skip(1), cwd, redirectStdin: true);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ffmpeg");
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }

        using (process)
        {
            try { process.StandardInput.Close(); } catch { /* stdin = DEVNULL */ }

            var stderrLines = new List<string>();
            var gate = new object();

            Task reader = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                    {
                        lock (gate)
                        {
                            stderrLines.Add(line);
                            if (stderrLines.Count > maxLog) stderrLines.RemoveAt(0);
                        }
                    }
                }
                catch { /* 读取失败不影响主流程 */ }
            });

            // 立刻终止 ffmpeg（等价 Python 在读到取消标记时 proc.terminate()），
            // 避免 ffmpeg 长时间无输出时「停止」按钮卡住。
            using CancellationTokenRegistration stopRegistration = cancelToken.Register(() =>
            {
                try { process.Kill(entireProcessTree: false); } catch { /* 已退出 */ }
            });

            var state = new ProgressState();
            double lastEmit = 0.0;   // Python: last_emit = 0.0，第一条进度行立即回调

            try
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    if (cancelToken.IsCancellationRequested)
                    {
                        try { process.Kill(entireProcessTree: false); } catch { /* 忽略 */ }
                        break;
                    }
                    state.Feed(line);
                    double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                    if (onProgress is not null && (now - lastEmit) > 0.25)
                    {
                        lastEmit = now;
                        onProgress(state.Percent(duration), state.Speed, state.Eta(duration));
                    }
                }
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
                try { process.Kill(entireProcessTree: false); } catch { /* 忽略 */ }
                try { await process.WaitForExitAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
            }
            finally
            {
                try { process.StandardOutput.Close(); } catch { /* 忽略 */ }
                stopRegistration.Unregister();
                await Task.WhenAny(reader, Task.Delay(5000)).ConfigureAwait(false);  // reader.join(timeout=5)
            }

            List<string> snapshot;
            lock (gate) snapshot = stderrLines.ToList();
            logLines?.AddRange(snapshot);
            string errorText = string.Join("\n", snapshot.TakeLast(8)).Trim();
            return (process.ExitCode, errorText);
        }
    }
}
