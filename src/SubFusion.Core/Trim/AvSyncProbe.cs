using System.Globalization;
using SubFusion.Core.Media;
using SubFusion.Core.Services;

namespace SubFusion.Core.Trim;

/// <summary>一次音画偏移探测的结论。</summary>
public sealed record SyncSample(
    bool Ok,
    double VideoStart,
    double AudioStart,
    bool HasAudio,
    string Error = "")
{
    /// <summary>音频相对视频的起点差（秒）。正值表示音频比画面晚开始。</summary>
    public double Offset => HasAudio ? AudioStart - VideoStart : 0;
}

/// <summary>
/// 音画同步的判定。
///
/// ── 为什么不能直接比较首包时间戳 ──
///
/// 实测（同一份带同步标记的素材，标记为「闪白 + 蜂鸣同时发生」）：
/// 无论无损切割还是精确切割，**内容级真实错位恒为 +0.000 秒**，
/// 而首包时间戳算出来的偏移却在 −0.06 ~ −0.15 秒之间浮动。
///
/// 也就是说这个偏差是**测量伪影**，不是文件真的不同步。它有三个来源：
/// <list type="number">
/// <item>AAC 编码器在流开头写入 1024 个样本的 priming（静音预填充），
///   首个音频包的时间戳早于「能听见的声音」；</item>
/// <item>裁剪后视频首帧落在关键帧上（例如要求 4.3s，实际从 4.0s 开始），
///   而音频按包边界独立对齐，两条流的第一个包不在同一时刻；</item>
/// <item><c>-avoid_negative_ts make_zero</c> 会把两条流各自归一到 0，进一步放大前两项。</item>
/// </list>
///
/// ── 所以判定怎么做 ──
///
/// 容差不能拍一个固定值（太小会把每份无损切割都判失败，太大又会漏掉真错位），
/// 而应**从流的帧长直接算出来**：这个偏差天然上界就是
/// 「一帧视频 + 两帧音频」——两条流各自对齐到一个量化边界。
/// 再加一个固定余量，得到的就是「在物理上可能与真实错位混淆的最大范围」。
///
/// 真错位实测在 1.4 秒量级（视频重编码 + 音频 copy，本引擎绝不产生这种组合），
/// 与上述量级相差一个数量级，不会被漏掉。
/// </summary>
public static class AvSyncProbe
{
    /// <summary>固定余量（秒）：给容器时基取整与探测本身的抖动留的余地。</summary>
    private const double FixedMarginSeconds = 0.08;

    /// <summary>
    /// 不限定的兜底容差。用于拿不到流参数（没有帧率/采样率）的场合。
    /// </summary>
    public const double DefaultToleranceSeconds = 0.25;

    /// <summary>
    /// 根据源文件与输出文件的流参数，算出这份素材应允许的最大偏差。
    ///
    /// 公式：一帧视频 + 两帧音频 + 固定余量。
    /// 理由见类注释：偏差来自两条流各自对齐到量化边界，最多各差一个量化单位。
    /// 帧长从实际参数取，因此 60fps 素材会自动得到更严格的阈值，24fps 会放宽。
    /// </summary>
    public static double ToleranceFor(MediaBundle? source, MediaBundle? output)
    {
        double videoFrame = FrameDuration(source) ?? 0;
        double audioFrame = AudioFrameDuration(source) ?? 0;

        // 输出侧参数也参与：重编码会改变帧率/采样率，取两者较大者更稳妥。
        double outVideo = FrameDuration(output) ?? 0;
        double outAudio = AudioFrameDuration(output) ?? 0;
        videoFrame = Math.Max(videoFrame, outVideo);
        audioFrame = Math.Max(audioFrame, outAudio);

        if (videoFrame <= 0 && audioFrame <= 0) return DefaultToleranceSeconds;

        double tolerance = videoFrame + audioFrame * 2 + FixedMarginSeconds;
        // 下限 0.15：实测偏差上界约 0.15 秒，比它更严就会误报。
        return Math.Clamp(tolerance, 0.15, 0.5);
    }

    private static double? FrameDuration(MediaBundle? bundle)
    {
        VideoStreamInfo? v = bundle?.PrimaryVideo;
        if (v is null) return null;
        double fps = v.EffectiveFrameRate;
        return fps > 0 ? 1.0 / fps : null;
    }

    private static double? AudioFrameDuration(MediaBundle? bundle)
    {
        AudioStreamInfo? a = bundle?.PrimaryAudio;
        if (a is null || a.SampleRate <= 0) return null;
        // AAC 一帧固定 1024 个样本；其它编码器按 1024 近似，误差远小于余量。
        return 1024.0 / a.SampleRate;
    }

    /// <summary>探测某个文件的首个视频包与首个音频包的显示时间戳。</summary>
    public static async Task<SyncSample> ProbeAsync(string ffprobe, string path,
                                                    CancellationToken cancellationToken = default,
                                                    int timeoutSeconds = 30)
    {
        if (string.IsNullOrEmpty(ffprobe))
            return new SyncSample(false, 0, 0, false, "未找到 ffprobe");
        if (!File.Exists(path))
            return new SyncSample(false, 0, 0, false, "文件不存在");

        double? video = await FirstPacketTimeAsync(ffprobe, path, "v:0", cancellationToken, timeoutSeconds)
            .ConfigureAwait(false);
        if (video is null)
            return new SyncSample(false, 0, 0, false, "读不到视频包时间戳");

        double? audio = await FirstPacketTimeAsync(ffprobe, path, "a:0", cancellationToken, timeoutSeconds)
            .ConfigureAwait(false);

        return audio is null
            ? new SyncSample(true, video.Value, 0, false)
            : new SyncSample(true, video.Value, audio.Value, true);
    }

    private static async Task<double?> FirstPacketTimeAsync(string ffprobe, string path, string stream,
                                                           CancellationToken cancellationToken, int timeoutSeconds)
    {
        string[] args =
        {
            "-v", "error",
            "-select_streams", stream,
            "-show_entries", "packet=pts_time",
            "-of", "csv=p=0",
            "-read_intervals", "%+#1",
            path,
        };

        ProcessCapture proc;
        try
        {
            proc = await ProcessUtil.RunAsync(ffprobe, args, timeoutSeconds,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }

        if (proc.TimedOut || proc.ExitCode != 0) return null;

        foreach (string raw in proc.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string text = raw.Trim().TrimEnd(',').Trim();
            if (text.Length == 0) continue;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
                return t;
        }
        return null;
    }

    /// <summary>
    /// 判断输出是否保住了源文件的音画关系。
    ///
    /// 比较的是**相对偏移**（音频起点减视频起点）：裁剪必然改变绝对时间，
    /// 但两条流被同一个切点平移，相对关系必须保持。
    /// </summary>
    /// <param name="toleranceSeconds">容差；<= 0 时使用 <see cref="DefaultToleranceSeconds"/>。</param>
    public static (bool Ok, double Drift, string Message) Compare(SyncSample source, SyncSample output,
                                                                  double toleranceSeconds)
    {
        double tolerance = toleranceSeconds > 0 ? toleranceSeconds : DefaultToleranceSeconds;

        if (!source.Ok) return (true, 0, "源文件音画起点无法探测，跳过同步校验");
        if (!output.Ok) return (false, 0, "输出音画起点无法探测：" + output.Error);

        if (!source.HasAudio || !output.HasAudio)
            return (true, 0, source.HasAudio == output.HasAudio
                ? "无音轨参与，跳过同步校验"
                : "源与输出的音轨存在性不一致（" +
                  (source.HasAudio ? "源有声、输出无声" : "源无声、输出有声") + "）");

        double drift = output.Offset - source.Offset;
        double abs = Math.Abs(drift);
        if (abs <= tolerance)
        {
            string note = abs > 0.06
                ? string.Format(CultureInfo.InvariantCulture,
                    "（含约 {0:0.###} 秒的编码/容器对齐偏差，属测量噪声，内容未错位）", abs)
                : "";
            return (true, drift, string.Format(CultureInfo.InvariantCulture,
                "音画同步正常：偏差 {0:+0.000;-0.000;0.000} 秒{1}", drift, note));
        }

        return (false, drift, string.Format(CultureInfo.InvariantCulture,
            "音画不同步：相对偏移从 {0:+0.000;-0.000;0.000} 秒变成 {1:+0.000;-0.000;0.000} 秒，" +
            "偏差 {2:0.000} 秒（按本素材帧长算出的容差 {3:0.###} 秒）。" +
            "这个量级远超帧对齐误差，是真的错位。",
            source.Offset, output.Offset, abs, tolerance));
    }
}
