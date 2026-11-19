using System.Globalization;
using SubFusion.Core.Media;

namespace SubFusion.Core.Trim;

/// <summary>
/// 裁剪命令构造。命令与执行分离：这里只产出参数列表，便于自检直接断言命令形状，
/// 也便于在失败时把完整命令写进诊断日志。
/// </summary>
public static class TrimCommandBuilder
{
    /// <summary>
    /// 构造裁剪命令。
    /// </summary>
    /// <param name="options">任务参数。</param>
    /// <param name="bundle">源文件的探测结果（决定有无音轨、有无字幕、源旋转角）。</param>
    /// <param name="actualStart">实际起点（Copy 模式已吸附到关键帧；精确模式等于请求值）。</param>
    /// <param name="rotation">旋转结论。</param>
    /// <param name="ffmpeg">ffmpeg 可执行文件路径。</param>
    public static List<string> Build(VideoTrimOptions options, MediaBundle bundle,
                                     double actualStart, RotationPlan rotation, string ffmpeg = "ffmpeg",
                                     string? renderPath = null)
    {
        // 终点必须与引擎一致收敛到文件实际时长；否则 -t 与进度分母(actualDuration)不符，
        // 进度会冲过 100% 或提前结束，诊断时长也与实际命令矛盾。
        // 收敛规则统一走 VideoTrimOptions.NormalizeRange——三处各算一次迟早会分叉。
        double requestedEnd = options.NormalizeRange(bundle.Duration).End;
        double duration = Math.Max(0.05, requestedEnd - actualStart);
        bool copying = options.CopiesStreams;
        bool toMp4 = options.NormalizedContainer() == "mp4";

        var cmd = new List<string>
        {
            // -progress pipe:1 是 FfmpegRunner 唯一的进度来源；没有它 UI 会一直停在上一句文字上。
            ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1",
        };

        // 旋转元数据必须在 -i 之前给出：它作用于输入侧的显示矩阵，
        // 实测放在 -i 之后会被忽略（输出没有 DisplayMatrix）。
        if (rotation.Strategy == RotationStrategy.Metadata)
            cmd.AddRange(new[] { "-display_rotation", rotation.TargetRotation.ToString(CultureInfo.InvariantCulture) });

        // -ss 放在 -i 之前是「输入侧快速定位」：只解析到关键帧附近，大文件上快一个数量级。
        // 代价是切点会吸附到关键帧——这正是 KeyframeIndex 提前算出来、并在 UI 上如实展示的原因。
        cmd.AddRange(new[] { "-ss", Format(actualStart), "-i", bundle.Path });

        // 流选择：视频必选；音频与字幕按源文件实际存在的轨道映射，用 ? 保证缺失时不失败。
        cmd.AddRange(new[] { "-map", "0:v:0" });
        if (bundle.HasAudio) cmd.AddRange(new[] { "-map", "0:a:0" });
        // mp4 装不下 PGS/VobSub 这类图形字幕：-map 之后 ffmpeg 会以非 0 退出，
        // 用户看到的只是「切割失败」。这里提前拦下并说清楚原因，
        // 由引擎在 Validate 阶段之外给出可读提示（见 VideoTrimEngine 的字幕容器检查）。
        if (options.KeepSubtitles && !(toMp4 && HasBitmapSubtitles(bundle)))
            cmd.AddRange(new[] { "-map", "0:s?" });

        if (copying)
        {
            cmd.AddRange(new[] { "-c", "copy" });
        }
        else
        {
            cmd.AddRange(new[] { "-c:v", options.VideoCodec, "-preset", options.Preset,
                                 "-crf", options.Crf.ToString(CultureInfo.InvariantCulture) });

            // 旋转在精确模式下用 transpose 烘焙进像素。两种写法不能混用：
            // 既写 display_rotation 又做 transpose 会让播放器转两次。
            string? filter = TransposeFilter(rotation.Requested);
            if (filter is not null) cmd.AddRange(new[] { "-vf", filter });

            // 音频跟视频一起重编码。实测「视频重编码 + 音频 copy」会产生约 1.4 秒的错位：
            // 两条流按各自的粒度定位，退出码却是 0。**这是本功能最容易踩的坑**。
            if (bundle.HasAudio)
                cmd.AddRange(new[] { "-c:a", options.AudioCodec,
                                     "-b:a", options.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k" });
        }

        // -t 而不是 -to：-to 是「输入的绝对结束时间」，与 -ss 一起用时行为随 -ss 的位置变化
        // （实测 -ss 在 -i 之前时，-to 会被当成相对时长从而截错）。用 -t 表达时长没有歧义。
        cmd.AddRange(new[] { "-t", Format(duration) });

        // 源文件可能带负时间戳（TS 抓流常见）。mp4 无法表示负时间戳，
        // 不加这一项时音频起点会被整体前移，造成音画不同步且不报错。
        cmd.AddRange(new[] { "-avoid_negative_ts", "make_zero" });

        // 章节不能原样搬：其时间轴仍对应整片，裁掉前面后全部错位。
        cmd.AddRange(new[] { "-map_chapters", "-1" });

        if (toMp4) cmd.AddRange(new[] { "-movflags", "+faststart", "-f", "mp4" });
        else cmd.AddRange(new[] { "-f", "matroska" });

        // 默认落到 options.OutputPath（自检与「只看命令形状」的调用方要的就是它）；
        // 引擎执行时传入本任务专属的渲染临时文件，校验通过后才改名到最终路径。
        cmd.Add(string.IsNullOrEmpty(renderPath) ? options.OutputPath : renderPath!);
        return cmd;
    }

    /// <summary>
    /// 源文件是否带图形字幕（PGS / VobSub）。mp4 容器无法承载，只能转成 mov_text
    /// 或干脆不复制；直接 -map 会让整条命令以非 0 退出。
    /// </summary>
    public static bool HasBitmapSubtitles(MediaBundle bundle) => bundle.BitmapSubtitleCount > 0;

    /// <summary>
    /// 顺时针旋转 → transpose 滤镜参数。
    /// 1 = 顺时针 90°，2 = 逆时针 90°，=3 = 转 180°。
    /// 注意 transpose 会交换宽高，偶数对齐由编码器的 yuv420p 要求决定，
    /// 因此只在结果尺寸为偶数时才安全——奇数尺寸源于源文件本身，这里不额外补边。
    /// </summary>
    public static string? TransposeFilter(RotationMode mode) => mode switch
    {
        RotationMode.Clockwise90 => "transpose=1",
        RotationMode.CounterClockwise90 => "transpose=2",
        RotationMode.Half => "transpose=2,transpose=2",
        _ => null,
    };

    /// <summary>秒 → ffmpeg 接受的定点小数（微秒精度足够，且不会写出科学计数法）。</summary>
    private static string Format(double seconds)
        => seconds.ToString("0.######", CultureInfo.InvariantCulture);
}
