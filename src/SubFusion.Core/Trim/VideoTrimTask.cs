using SubFusion.Core.Media;

namespace SubFusion.Core.Trim;

/// <summary>
/// 视频裁剪任务的公共入口。与视频合并一样，所有核心任务通过明确的参数对象启动，
/// 返回带任务 ID、状态、实际切点、音画同步结论与诊断的结果对象。
///
/// 本类型不依赖任何 GUI 类型：UI 只需要构造 <see cref="VideoTrimOptions"/>、
/// 传入 <see cref="IProgress{T}"/> 回调与 <see cref="CancellationToken"/>。
/// </summary>
public sealed class VideoTrimTask
{
    private readonly FfmpegTools _tools;

    public VideoTrimTask(FfmpegTools tools) => _tools = tools;

    /// <summary>定位工具后创建任务（找不到 ffmpeg 时仍返回对象，执行时报错）。</summary>
    public static async Task<VideoTrimTask> CreateAsync(string? ffmpeg = null, string? ffprobe = null,
                                                        CancellationToken cancellationToken = default)
        => new(await FfmpegTools.ResolveAsync(ffmpeg, ffprobe, cancellationToken).ConfigureAwait(false));

    public FfmpegTools Tools => _tools;

    /// <summary>
    /// 只分析不执行：返回媒体信息、关键帧时间轴与源旋转角。
    /// <c>SourceRotation</c> 为 null 表示未能读取（不等于 0°）。
    /// </summary>
    public Task<(MediaBundle Bundle, KeyframeIndex Keyframes, int? SourceRotation)> AnalyzeAsync(
        string inputPath, CancellationToken cancellationToken = default)
        => new VideoTrimEngine(_tools).AnalyzeAsync(inputPath, cancellationToken);

    /// <summary>执行裁剪；进度经 <paramref name="progress"/> 回调，取消经 <paramref name="cancellationToken"/>。</summary>
    public Task<TrimResult> RunAsync(VideoTrimOptions options, IProgress<TrimProgress>? progress = null,
                                     CancellationToken cancellationToken = default)
    {
        var engine = new VideoTrimEngine(_tools, progress);
        return engine.RunAsync(options, cancellationToken);
    }
}
