using System.Globalization;
using SubFusion.Core.Services;

namespace SubFusion.Core.Trim;

/// <summary>
/// 从视频里取单帧画面（预览用）。
///
/// 必须走二进制管道：ffmpeg 把 JPEG 写到 stdout，而 <see cref="ProcessUtil"/>
/// 用 UTF-8 解码文本流，直接读字符串会把图像字节破坏掉。所以这里单独开一个进程并按字节读。
/// </summary>
public static class FrameGrabber
{
    /// <summary>
    /// 取 <paramref name="seconds"/> 处的一帧，返回 JPEG 字节；失败返回 null。
    ///
    /// 用 <c>-ss</c> 放在 <c>-i</c> 之前做输入侧快速定位：预览要跟着拖动频繁刷新，
    /// 输出侧精确定位会解码整段前置内容，拖一下就要几百毫秒，交互会明显发滞。
    /// 代价是取到的帧可能落在目标时间之前（最多一个 GOP），对预览来说可以接受。
    /// </summary>
    public static async Task<byte[]?> GrabJpegAsync(string ffmpeg, string path, double seconds,
                                                    int width = 480,
                                                    CancellationToken cancellationToken = default,
                                                    int timeoutSeconds = 20)
    {
        if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(path)) return null;
        if (seconds < 0) seconds = 0;
        if (width <= 0) width = 480;

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", path,
            "-frames:v", "1",
            // 等比缩到指定宽度，再对齐到偶数：奇数宽高的 JPEG 在某些渲染路径上会失败。
            "-vf", string.Format(CultureInfo.InvariantCulture,
                "scale={0}:-2:flags=bilinear", width),
            "-f", "image2", "-c:v", "mjpeg", "-q:v", "4",
            "pipe:1",
        };

        var psi = ProcessUtil.MakeStartInfo(ffmpeg, args);
        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(psi)
                     ?? throw new InvalidOperationException("无法启动 ffmpeg");
        }
        catch { return null; }

        using (process)
        {
            try { process.StandardInput.Close(); } catch { /* stdin = DEVNULL */ }

            using CancellationTokenRegistration reg = cancellationToken.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            });

            var buffer = new MemoryStream();
            try
            {
                Task copy = process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
                Task drain = process.StandardError.ReadToEndAsync(CancellationToken.None);

                Task timeout = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), CancellationToken.None);
                Task finished = await Task.WhenAny(copy, timeout).ConfigureAwait(false);
                if (finished != copy)
                {
                    // 取帧是拖动时间轴时的高频操作：Kill 之后必须等进程真正退出，
                    // 否则残留的 ffmpeg 与管道句柄会随拖动次数线性累积。
                    try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                    try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2))
                        .ConfigureAwait(false); } catch { /* 忽略 */ }
                    Observe(copy);
                    Observe(drain);
                    return null;
                }

                await copy.ConfigureAwait(false);
                await Task.WhenAny(drain, Task.Delay(2000)).ConfigureAwait(false);
                Observe(drain);
                try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
            }
            catch (OperationCanceledException)
            {
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false); } catch { /* 忽略 */ }
                return null;
            }
            catch { return null; }

            return buffer.Length > 0 ? buffer.ToArray() : null;
        }
    }

    /// <summary>
    /// 观察被放弃的任务的异常。
    /// 取帧在拖动时会被高频取消，被 Kill 打断的管道读取/写出任务会以异常结束；
    /// 没人 await 它们就会变成未观察异常，重负载下不断累积。
    /// </summary>
    private static void Observe(Task task)
        => _ = task.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
}
