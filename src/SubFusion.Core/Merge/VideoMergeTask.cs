using SubFusion.Core.Media;

namespace SubFusion.Core.Merge;

/// <summary>
/// 视频合并任务的公共入口。所有核心任务都通过明确的参数对象启动，
/// 返回带任务 ID、状态、阶段、进度、输出信息、章节与诊断的结果对象。
///
/// 本类型不依赖任何 GUI 类型：UI 只需要构造 <see cref="VideoMergeOptions"/>、
/// 传入 <see cref="IProgress{T}"/> 回调与 <see cref="CancellationToken"/>。
/// </summary>
public sealed class VideoMergeTask
{
    private readonly FfmpegTools _tools;

    public VideoMergeTask(FfmpegTools tools) => _tools = tools;

    /// <summary>定位工具后创建任务（找不到 ffmpeg 时仍返回对象，执行时报错）。</summary>
    public static async Task<VideoMergeTask> CreateAsync(string? ffmpeg = null, string? ffprobe = null,
                                                         CancellationToken cancellationToken = default)
        => new(await FfmpegTools.ResolveAsync(ffmpeg, ffprobe, cancellationToken).ConfigureAwait(false));

    public FfmpegTools Tools => _tools;

    /// <summary>只分析不执行：返回兼容性结论与每个输入探测到的媒体信息。</summary>
    public async Task<(MergeCompatibility Compatibility, List<MediaBundle> Bundles)> AnalyzeAsync(
        VideoMergeOptions options, CancellationToken cancellationToken = default)
        => await new VideoMergeEngine(_tools).AnalyzeAsync(options, cancellationToken).ConfigureAwait(false);

    /// <summary>执行合并；进度经 <paramref name="progress"/> 回调，取消经 <paramref name="cancellationToken"/>。</summary>
    public Task<MergeResult> RunAsync(VideoMergeOptions options, IProgress<MergeProgress>? progress = null,
                                      CancellationToken cancellationToken = default)
    {
        var engine = new VideoMergeEngine(_tools, progress);
        return engine.MergeAsync(options, cancellationToken);
    }

    /// <summary>纯媒体探测入口（供 UI 展示输入信息，不触发合并）。</summary>
    public Task<List<MediaBundle>> ProbeAsync(IReadOnlyList<string> paths,
                                              CancellationToken cancellationToken = default,
                                              int timeoutSeconds = 60)
    {
        if (!_tools.ProbeAvailable)
            return Task.FromResult(paths.Select(p => new MediaBundle { Path = p, Error = "未找到 ffprobe" }).ToList());
        return MediaInspector.InspectManyAsync(_tools.Ffprobe!, paths, cancellationToken, 4, timeoutSeconds);
    }
}
