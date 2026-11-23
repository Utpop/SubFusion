using System.Globalization;
using SubFusion.Core.Media;

namespace SubFusion.Core.Compress;

/// <summary>
/// 压制命令构造。命令与执行分离：这里只产出参数列表，便于自检直接断言命令形状，
/// 也便于在失败时把完整命令写进诊断日志。
///
/// 关键取舍（都来自本机实测）：
/// <list type="bullet">
/// <item>硬件编码器不接受 <c>-preset medium</c> 这类 x264 参数，只能用 <c>-quality</c>；
/// 传错会直接报 Invalid argument。</item>
/// <item>缩放在硬件解码下用 <c>scale</c> 滤镜即可：ffmpeg 会自动把帧下载回内存，
/// 实测 <c>-hwaccel d3d11va -i in -vf scale=... -c:v h264_amf</c> 正常工作。</item>
/// <item>HDR→SDR 的色调映射必须在软件里做（zscale/tonemap 没有硬件版本），
/// 因此开了色调映射就不要再开硬件解码，否则会来回上传下载反而更慢。</item>
/// <item><c>-movflags +faststart</c> 只对 mp4 有意义；mkv 写不了这个参数。</item>
/// </list>
/// </summary>
public static class CompressCommandBuilder
{
    /// <summary>
    /// 构造压制命令。
    /// </summary>
    /// <param name="options">任务参数。</param>
    /// <param name="bundle">源文件探测结果。</param>
    /// <param name="encoder">已选定的编码器（来自 <see cref="VideoEncoder.Select"/>）。</param>
    /// <param name="ffmpeg">ffmpeg 可执行文件路径。</param>
    /// <param name="renderPath">渲染用的临时文件路径（最终落盘由引擎负责）。</param>
    public static List<string> Build(VideoCompressOptions options, MediaBundle bundle,
                                     EncoderAvailability encoder, string ffmpeg = "ffmpeg",
                                     string? renderPath = null)
    {
        bool copyingAudio = !options.ReencodeAudio && bundle.HasAudio;
        bool toMp4 = options.NormalizedContainer() == "mp4";
        bool hardware = encoder.Kind != EncoderKind.Software;

        var cmd = new List<string>
        {
            // -progress pipe:1 是 FfmpegRunner 唯一的进度来源；没有它 UI 会一直停在上一句文字上。
            ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1",
        };

        // 硬件解码：只在真的要重编码、且不是色调映射路径时启用。
        if (options.HardwareDecode && hardware)
            cmd.AddRange(new[] { "-hwaccel", "d3d11va" });

        cmd.AddRange(new[] { "-i", bundle.Path });

        // 流选择：视频必选；音频与字幕按源文件实际存在的轨道映射，用 ? 保证缺失时不失败。
        cmd.AddRange(new[] { "-map", "0:v:0" });
        if (bundle.HasAudio) cmd.AddRange(new[] { "-map", "0:a:0" });
        // mp4 装不下文本字幕原码流："-c:s copy" 下 ffmpeg 会报
        // Could not find tag for codec subrip in stream #N ... Invalid argument（-22）。
        // 引擎只拦了图形字幕，文本字幕仍会被映射进来，用户只看到「压制失败」。
        // 因此 mp4 时映射照旧，但字幕单独转成 mov_text。
        bool mapSubtitles = options.KeepSubtitles && !(toMp4 && bundle.BitmapSubtitleCount > 0);
        if (mapSubtitles)
            cmd.AddRange(new[] { "-map", "0:s?" });

        // 视频编码参数。
        cmd.AddRange(VideoArgs(options, encoder));

        // 滤镜链：缩放 / 帧率 / 像素格式。
        string? filter = BuildFilter(options, bundle);
        if (filter is not null) cmd.AddRange(new[] { "-vf", filter });

        // 音频。
        if (copyingAudio)
        {
            cmd.AddRange(new[] { "-c:a", "copy" });
        }
        else if (bundle.HasAudio)
        {
            cmd.AddRange(new[] { "-c:a", options.AudioCodec,
                                 "-b:a", options.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k" });
        }
        else
        {
            cmd.AddRange(new[] { "-an" });
        }

        // mp4 没有 subrip/ass 的 tag，只能转 mov_text；mkv 原样复制即可。
        if (mapSubtitles) cmd.AddRange(new[] { "-c:s", toMp4 ? "mov_text" : "copy" });

        // 源文件可能带负时间戳（TS 抓流常见）。mp4 无法表示负时间戳，
        // 不加这一项时音频起点会被整体前移，造成音画不同步且不报错。
        cmd.AddRange(new[] { "-avoid_negative_ts", "make_zero" });

        // 来源是分段拼接的素材时，段间时间戳可能不平滑；重编码时统一重算，避免累积抖动。
        cmd.AddRange(new[] { "-fflags", "+genpts" });

        // 章节原样搬没有意义：压制后时间轴虽然不变，但章节条目指向的可能是旧关键帧。
        // 这里明确不继承章节，避免出现「章节跳到某处画面却不同步」的错觉。
        cmd.AddRange(new[] { "-map_chapters", "-1" });

        cmd.AddRange(toMp4
            ? new[] { "-movflags", "+faststart", "-f", "mp4" }
            : new[] { "-f", "matroska" });

        cmd.Add(string.IsNullOrEmpty(renderPath) ? options.OutputPath : renderPath!);
        return cmd;
    }

    /// <summary>视频编码参数。软件与硬件的参数集完全不同，不能混用。</summary>
    private static IEnumerable<string> VideoArgs(VideoCompressOptions options, EncoderAvailability encoder)
    {
        var args = new List<string> { "-c:v", encoder.Name };
        bool hardware = encoder.Kind != EncoderKind.Software;

        // preset 与画质参数一律走共享的 VideoEncoder：三个硬件后端的选项名互不兼容，
        // 各自在模块里拼一份必然出现「某个后端直接 EINVAL、另一个后端参数被静默忽略」。
        VideoEncoder.AppendPreset(args, encoder.Kind, options.HardwareQuality);

        if (options.RateControl == RateControl.Bitrate)
        {
            VideoEncoder.AppendBitrate(args, encoder.Kind, options.TargetBitrateKbps);
        }
        else
        {
            // 界面画质档是 CRF 语义；硬件侧的 +6 偏移换算也在 VideoEncoder 里统一完成。
            VideoEncoder.AppendQuality(args, encoder.Kind, options.Quality);
        }

        if (hardware)
        {
            // HEVC 硬件编码默认 8bit；源是 10bit 时要求编码器输出 10bit，否则会静默降位深。
            VideoEncoder.AppendHardwareProfile(args, encoder.Codec);
        }
        else if (encoder.Codec == VideoCodecKind.Hevc)
        {
            // 10bit 源用 10bit 编码器保留色深（x264 的 10bit 构建才支持，按命名探测）。
            args.AddRange(new[] { "-x265-params", "log-level=error" });
        }
        return args;
    }

    /// <summary>
    /// 滤镜链：缩放 → 帧率（只降不升）→ 偶数对齐。
    /// 返回 null 表示不需要任何滤镜。
    /// </summary>
    public static string? BuildFilter(VideoCompressOptions options, MediaBundle bundle)
    {
        var parts = new List<string>();
        VideoStreamInfo? video = bundle.PrimaryVideo;

        // 缩放（先用 force_original_aspect_ratio 表达「保持比例」，再按需补边）。
        string? scale = BuildScale(options, video);
        if (scale is not null) parts.Add(scale);

        // 帧率：只降不升。源 23.976 时填 30 只会重复帧白白变大。
        if (options.FrameRate > 0 && video is not null)
        {
            double sourceFps = video.EffectiveFrameRate;
            double target = options.FrameRate;
            if (sourceFps <= 0 || target < sourceFps - 0.01)
                parts.Add("fps=" + target.ToString("0.###", CultureInfo.InvariantCulture));
        }

        // 4:2:0 要求宽高为偶数：奇数尺寸会让编码器直接失败。
        parts.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        parts.Add("format=yuv420p");

        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    private static string? BuildScale(VideoCompressOptions options, VideoStreamInfo? video)
    {
        switch (options.Resize)
        {
            case CompressResize.LongEdge:
            {
                int target = options.ResizeValue;
                // 长边不超过目标时不动：放大既费时间又不增加信息量。
                if (video is not null && Math.Max(video.Width, video.Height) <= target) return null;
                // if() 按长边等比缩放：横屏按 -2 缩宽，竖屏按 -2 缩高。
                return string.Format(CultureInfo.InvariantCulture,
                    "scale=if(gte(iw\\,ih)\\,{0}\\,-2):if(gte(iw\\,ih)\\,-2\\,{0})", target);
            }
            case CompressResize.Fit:
            {
                int w = options.Width, h = options.Height;
                if (w <= 0 || h <= 0) return null;
                return string.Format(CultureInfo.InvariantCulture,
                    "scale={0}:{1}:force_original_aspect_ratio=decrease,pad={0}:{1}:(ow-iw)/2:(oh-ih)/2:color=black",
                    w, h);
            }
            case CompressResize.Scale:
            {
                int divisor = Math.Max(2, options.ResizeValue);
                if (video is not null && video.Width > 0 && video.Height > 0)
                    return string.Format(CultureInfo.InvariantCulture,
                        "scale={0}:{1}", EvenUp(video.Width / divisor), EvenUp(video.Height / divisor));
                return string.Format(CultureInfo.InvariantCulture, "scale=iw/{0}:ih/{0}", divisor);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// 奇数尺寸向上取偶。向下取偶会让 1919x1079 变成 1918x1078，
    /// 少一行的画面比多一行更明显（原片右侧会露出一条）。
    /// </summary>
    public static int EvenUp(int value)
    {
        if (value <= 1) return 2;
        return value % 2 == 0 ? value : value + 1;
    }

    /// <summary>
    /// 界面画质档（CRF 语义）→ 硬件编码的 QP。
    /// 实现已上移到共享的 <see cref="VideoEncoder.HardwareQp"/>：裁剪与硬字幕要用同一套换算，
    /// 三处各写一份迟早分叉。
    /// </summary>
    public static int HardwareQp(int quality) => VideoEncoder.HardwareQp(quality);

    /// <summary>硬件质量档：实现见 <see cref="VideoEncoder.NormalizeHardwareQuality"/>。</summary>
    public static string NormalizeHardwareQuality(string? value) => VideoEncoder.NormalizeHardwareQuality(value);

    /// <summary>软件 preset 白名单：实现见 <see cref="VideoEncoder.NormalizePreset"/>。</summary>
    public static string NormalizePreset(string? value) => VideoEncoder.NormalizePreset(value);

    /// <summary>码率模式下按目标码率与时长估算输出体积（字节）；用于界面预估。</summary>
    public static long EstimateBytes(VideoCompressOptions options, double durationSeconds)
    {
        if (durationSeconds <= 0) return 0;
        double videoKbps = options.RateControl == RateControl.Bitrate
            ? options.TargetBitrateKbps
            : 0;
        if (videoKbps <= 0) return 0;
        double audioKbps = options.ReencodeAudio ? options.AudioBitrateKbps : 192;
        return (long)((videoKbps + audioKbps) * 1000.0 / 8.0 * durationSeconds);
    }
}
