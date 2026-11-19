using SubFusion.Core.Media;

namespace SubFusion.Core.Compress;

/// <summary>
/// 视频压制任务的公共入口。与其他核心任务一致：明确的参数对象进、结果对象出，
/// 不依赖任何 GUI 类型，UI 只负责构造参数、接收进度与渲染结果。
/// </summary>
public sealed class VideoCompressTask
{
    private readonly FfmpegTools _tools;

    public VideoCompressTask(FfmpegTools tools) => _tools = tools;

    /// <summary>定位工具后创建任务（找不到 ffmpeg 时仍返回对象，执行时报错）。</summary>
    public static async Task<VideoCompressTask> CreateAsync(string? ffmpeg = null, string? ffprobe = null,
                                                             CancellationToken cancellationToken = default)
        => new(await FfmpegTools.ResolveAsync(ffmpeg, ffprobe, cancellationToken).ConfigureAwait(false));

    public FfmpegTools Tools => _tools;

    /// <summary>只分析不执行：返回媒体信息与本机可用的编码器列表。</summary>
    public Task<(MediaBundle Bundle, IReadOnlyList<EncoderAvailability> Encoders)> AnalyzeAsync(
        string inputPath, CancellationToken cancellationToken = default)
        => new VideoCompressEngine(_tools).AnalyzeAsync(inputPath, cancellationToken);

    /// <summary>执行压制；进度经 <paramref name="progress"/> 回调，取消经 <paramref name="cancellationToken"/>。</summary>
    public Task<CompressResult> RunAsync(VideoCompressOptions options,
                                          IProgress<CompressProgress>? progress = null,
                                          CancellationToken cancellationToken = default)
        => new VideoCompressEngine(_tools, progress).RunAsync(options, cancellationToken);
}
