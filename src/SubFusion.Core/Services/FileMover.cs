using System.Diagnostics;
using System.Globalization;

namespace SubFusion.Core.Services;

/// <summary>
/// 把临时输出搬到最终路径，并在这段时间里持续报告进度。
///
/// 为什么不能直接用 <c>File.Move</c>：
/// 同一卷内它是改名（微秒级），跨卷时它会退化成「整文件复制 + 删除源」，
/// 而且这期间不报告任何进度。几十 GB 的输出落在 99% 上静止几分钟，
/// 在用户眼里就是卡死——实际上磁盘灯在狂闪。
/// 这里把两种情况显式分开：同卷改名仍然瞬间完成，跨卷复制则按字节推进进度。
/// </summary>
public static class FileMover
{
    /// <summary>复制时每次读写的大小。4 MB 对机械盘和 SSD 都是合适的折中。</summary>
    private const int BufferBytes = 4 * 1024 * 1024;

    /// <summary>两个路径是否在同一卷（同一盘符/挂载点）。</summary>
    public static bool SameVolume(string a, string b)
    {
        try
        {
            string rootA = Path.GetPathRoot(Path.GetFullPath(a)) ?? "";
            string rootB = Path.GetPathRoot(Path.GetFullPath(b)) ?? "";
            return string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;   // 判断不了就按跨卷处理：慢一点但不会错
        }
    }

    /// <summary>
    /// 移动到目标路径；<paramref name="onProgress"/> 收到 0–1 的完成比例。
    /// 同卷时直接改名并只回调一次 1.0。
    /// </summary>
    public static async Task MoveAsync(string source, string destination, Action<double>? onProgress,
                                       CancellationToken cancellationToken = default)
    {
        if (SameVolume(source, destination))
        {
            File.Move(source, destination, overwrite: true);
            onProgress?.Invoke(1.0);
            return;
        }

        await CopyWithProgressAsync(source, destination, onProgress, cancellationToken).ConfigureAwait(false);
        try { File.Delete(source); }
        catch (Exception ex)
        {
            // 删不掉源文件不影响输出可用，但「以为已移动、实际源还在」必须让调用方知道，
            // 否则用户会在两个位置各看到一份几十 GB 的文件而毫无提示。
            throw new IOException("输出已就位，但源文件删除失败（数据未丢失）：" + ex.Message, ex);
        }
    }

    /// <summary>
    /// 带进度的复制。公开是为了能被自检直接覆盖：
    /// 跨卷复制只有在真有第二个盘时才会走到，而这段代码恰恰是「卡在 99%」的关键路径，
    /// 不能因为测试机只有一块盘就没人验证过。
    /// 中途取消或出错会删掉半成品——一份看起来完整的残缺输出比没有输出更危险。
    /// </summary>
    public static async Task CopyWithProgressAsync(string source, string destination, Action<double>? onProgress,
                                                   CancellationToken cancellationToken)
    {
        long total = new FileInfo(source).Length;
        long copied = 0;
        double lastReport = -1;

        try
        {
            await using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                BufferBytes, FileOptions.SequentialScan))
            await using (FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                                                 BufferBytes, FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[BufferBytes];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = await input.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken)
                        .ConfigureAwait(false);
                    if (read <= 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;

                    // 只在比例真的变了才回调：几十 GB 会产生上千次回调，
                    // 全部投递到 UI 线程只会把进度条拖慢。
                    double ratio = total > 0 ? Math.Min(1.0, (double)copied / total) : 1.0;
                    if (ratio - lastReport >= 0.01)
                    {
                        lastReport = ratio;
                        // 进度回调可能来自已关闭窗口的 Dispatcher；它抛异常绝不能落入下面的
                        // catch，否则一份已经复制完整的输出会被当成半成品删掉。
                        try { onProgress?.Invoke(ratio); } catch { /* 只影响进度显示 */ }
                    }
                }
            }
            try { onProgress?.Invoke(1.0); } catch { /* 只影响进度显示 */ }
        }
        catch
        {
            // 半成品不能留在用户目录里：它看起来像一份完整的输出。
            try { File.Delete(destination); } catch { /* 忽略 */ }
            throw;
        }
    }

    /// <summary>把字节数写成可读文本，用于进度提示。</summary>
    public static string Describe(long bytes)
        => Formatting.FormatSize(bytes);
}
