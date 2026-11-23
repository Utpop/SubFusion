using SubFusion.Core.Models;
using SubFusion.Core.Services;

namespace SubFusion.Core.Media;

/// <summary>
/// 媒体工具门面：定位 ffmpeg / ffprobe、查询能力与版本。
/// 字幕业务与视频合并业务共用这一处，不各自解析工具路径。
/// </summary>
public sealed class FfmpegTools
{
    public string Ffmpeg { get; private init; } = "";
    public string? Ffprobe { get; private init; }
    public FfmpegCapabilities Capabilities { get; private init; } = new();
    public string Version { get; private init; } = "";

    public bool Available => Ffmpeg.Length > 0;
    public bool ProbeAvailable => !string.IsNullOrEmpty(Ffprobe);
    /// <summary>工具定位/能力探测失败原因；空串表示无错误。</summary>
    public string AvailabilityError { get; private init; } = "";

    /// <summary>定位工具并探测能力；任何一步失败都不抛异常，由调用方判断 Available。</summary>
    public static async Task<FfmpegTools> ResolveAsync(string? ffmpeg = null, string? ffprobe = null,
                                                       CancellationToken cancellationToken = default)
    {
        string resolvedFfmpeg = !string.IsNullOrEmpty(ffmpeg) ? ffmpeg! : (ToolLocator.FindFfmpeg() ?? "");
        string? resolvedFfprobe = !string.IsNullOrEmpty(ffprobe) ? ffprobe : ToolLocator.FindFfprobe();
        // 取消时也要带上 Ffmpeg 路径。
        // 三个引擎都是「先查 Available，再判断取消」：Ffmpeg 留空会让 Available=false，
        // 用户点取消后看到的却是「未找到 ffmpeg，无法合并视频」这种假故障。
        if (cancellationToken.IsCancellationRequested)
            return new FfmpegTools { Ffmpeg = resolvedFfmpeg, Ffprobe = resolvedFfprobe, AvailabilityError = "已取消" };
        if (resolvedFfmpeg.Length == 0)
            return new FfmpegTools { Ffprobe = resolvedFfprobe, AvailabilityError = "未找到 ffmpeg" };

        FfmpegCapabilities caps;
        string version;
        string availabilityError = "";
        try
        {
            // ToolLocator 的探测方法本身不接受 token；用带 token 的 Task.Run 包装，取消时立即返回。
            caps = await Task.Run(() => ToolLocator.FfmpegCapabilitiesAsync(resolvedFfmpeg), cancellationToken)
                .ConfigureAwait(false);
            version = await Task.Run(() => ToolLocator.FfmpegVersionAsync(resolvedFfmpeg), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 遵守"不抛异常"契约：取消也返回带 AvailabilityError 的结果对象。
            // 同样必须带上 Ffmpeg，否则调用方会把「已取消」判成「未找到 ffmpeg」。
            return new FfmpegTools { Ffmpeg = resolvedFfmpeg, Ffprobe = resolvedFfprobe, AvailabilityError = "已取消" };
        }
        catch (Exception ex)
        {
            caps = new FfmpegCapabilities();
            version = "";
            availabilityError = "能力探测失败：" + ex.Message;
        }
        return new FfmpegTools
        {
            Ffmpeg = resolvedFfmpeg,
            Ffprobe = resolvedFfprobe,
            Capabilities = caps,
            Version = version,
            AvailabilityError = availabilityError,
        };
    }
}
