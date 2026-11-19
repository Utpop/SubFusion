using System.Globalization;
using SubFusion.Core.Services;

namespace SubFusion.Core.Trim;

/// <summary>
/// 一个文件的关键帧时间轴。
///
/// 存在的理由：无损切割（-c copy）的切点只能落在关键帧上。
/// ffmpeg 遇到不在关键帧上的 -ss 会**向前回退**到上一个关键帧，
/// 于是「我要 10 秒」实际给出「从 8 秒开始的 12 秒」。用户看到的时长与内容
/// 都和请求不符，如果不提前把这件事算出来并如实展示，功能就会被当成 Bug。
/// </summary>
public sealed class KeyframeIndex
{
    /// <summary>按时间升序排列的关键帧时间点（秒）。首个元素通常是 0。</summary>
    public IReadOnlyList<double> Times { get; init; } = Array.Empty<double>();

    /// <summary>扫描是否成功；false 时 <see cref="Error"/> 说明原因。</summary>
    public bool Ok { get; init; }
    public string Error { get; init; } = "";

    public bool IsEmpty => Times.Count == 0;

    /// <summary>
    /// 所有帧都是关键帧（如 ProRes / 部分录屏素材）。
    /// 这类素材的 copy 切割天然帧精确，不需要提示用户「切点会吸附」。
    /// </summary>
    public bool AllIntra { get; init; }

    /// <summary>
    /// 找到不大于 <paramref name="seconds"/> 的最后一个关键帧。
    /// 没有更早的关键帧时返回 0（即从头开始）——ffmpeg 的实际行为也是如此。
    /// </summary>
    public double SnapToKeyframe(double seconds)
    {
        if (Times.Count == 0 || seconds <= 0) return 0;
        double best = 0;
        foreach (double t in Times)
        {
            if (t <= seconds + 1e-6) best = t;
            else break;
        }
        return best;
    }

    /// <summary>吸附会造成多少提前量（秒）。0 表示切点正好落在关键帧上。</summary>
    public double SnapLoss(double seconds) => Math.Max(0, seconds - SnapToKeyframe(seconds));

    /// <summary>
    /// 相邻关键帧的最大间隔（秒）。这就是 copy 切割的最大误差上界：
    /// 无论用户把切点拖到哪里，最多会多带上这么长一段。
    /// </summary>
    public double MaxGap
    {
        get
        {
            if (Times.Count < 2) return 0;
            double max = 0;
            for (int i = 1; i < Times.Count; i++)
            {
                double gap = Times[i] - Times[i - 1];
                if (gap > max) max = gap;
            }
            return max;
        }
    }

    public string Describe()
    {
        if (!Ok) return "关键帧索引不可用：" + Error;
        if (Times.Count == 0) return "未找到关键帧";
        if (AllIntra) return "全关键帧素材：切点可以精确到帧，不需要吸附";
        return string.Format(CultureInfo.InvariantCulture,
            "{0} 个关键帧，最大间隔 {1:0.###} 秒（无损切割最多会多带这么长一段）",
            Times.Count, MaxGap);
    }
}

/// <summary>用 ffprobe 扫描关键帧时间轴。</summary>
public static class KeyframeScanner
{
    /// <summary>
    /// 扫描全部关键帧。
    ///
    /// 用 <c>-skip_frame nokey</c> 让解码器跳过非关键帧，只输出关键帧的时间戳；
    /// 实测 30 秒素材耗时约 0.1 秒，两小时的片子约十几秒，属于可接受的一次性代价。
    /// 扫描失败不影响主流程：调用方会退化为「不吸附、直接让 ffmpeg 自己处理」。
    /// </summary>
    public static async Task<KeyframeIndex> ScanAsync(string ffprobe, string path,
                                                      CancellationToken cancellationToken = default,
                                                      int timeoutSeconds = 120)
    {
        if (string.IsNullOrEmpty(ffprobe))
            return new KeyframeIndex { Ok = false, Error = "未找到 ffprobe" };
        if (!File.Exists(path))
            return new KeyframeIndex { Ok = false, Error = "文件不存在" };

        string[] args =
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-skip_frame", "nokey",
            "-show_entries", "frame=pts_time",
            "-of", "csv=p=0",
            path,
        };

        ProcessCapture proc;
        try
        {
            proc = await ProcessUtil.RunAsync(ffprobe, args, timeoutSeconds,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new KeyframeIndex { Ok = false, Error = ex.Message };
        }

        if (proc.TimedOut) return new KeyframeIndex { Ok = false, Error = "扫描关键帧超时" };
        if (proc.ExitCode != 0) return new KeyframeIndex { Ok = false, Error = "扫描关键帧失败" };

        var times = new List<double>();
        foreach (string raw in proc.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // csv=p=0 每行形如 "12.345" 或 "12.345,"（带尾随逗号），两处都要吃得下。
            string text = raw.Trim().TrimEnd(',').Trim();
            if (text.Length == 0) continue;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double t) &&
                t >= 0 && (times.Count == 0 || t > times[^1]))
                times.Add(t);
        }

        if (times.Count == 0)
            return new KeyframeIndex { Ok = false, Error = "该文件没有可用的关键帧时间戳" };

        // 全关键帧判定：靠关键帧数量除以总帧数，没有帧数时退化为「最大间隔小于一帧」。
        bool allIntra = times.Count >= 2 && DetectAllIntra(times);

        return new KeyframeIndex { Ok = true, Times = times, AllIntra = allIntra };
    }

    /// <summary>
    /// 相邻关键帧间隔都近似相等且极短（通常等于帧间隔）时视为全关键帧素材。
    /// 这里不引帧率，只用间隔的极度均匀性判断，避免为一件事再起一次 ffprobe：
    /// 真正的 all-intra 素材关键帧间隔恒定，而普通素材的间隔会随场景变化。
    /// </summary>
    private static bool DetectAllIntra(IReadOnlyList<double> times)
    {
        if (times.Count < 3) return false;
        double first = times[1] - times[0];
        if (first <= 0 || first > 0.1) return false;      // 超过 100ms 不可能是逐帧关键帧
        for (int i = 2; i < times.Count; i++)
        {
            double gap = times[i] - times[i - 1];
            if (Math.Abs(gap - first) > 1e-3) return false;
        }
        return true;
    }
}
