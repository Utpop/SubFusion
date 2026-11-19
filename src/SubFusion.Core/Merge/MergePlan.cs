using System.Globalization;
using SubFusion.Core.Chapters;
using SubFusion.Core.Media;
using SubFusion.Core.Models;

namespace SubFusion.Core.Merge;

/// <summary>
/// 一次合并的实际执行方案：由用户选项 + 输入探测结果推导出统一输出参数，
/// 并负责生成「统一重编码」与「concat 拼接」两条 ffmpeg 命令。
/// </summary>
public sealed class MergePlan
{
    public bool VideoCopy { get; private init; }
    public bool AudioCopy { get; private init; }
    public string ContainerExtension { get; private init; } = ".mkv";

    public int TargetWidth { get; private init; }
    public int TargetHeight { get; private init; }
    public double TargetFrameRate { get; private init; }
    public FitMode Fit { get; private init; } = FitMode.Contain;

    public string VideoCodec { get; private init; } = "libx264";
    public string VideoCodecOutputName { get; private init; } = "h264";

    /// <summary>
    /// 重编码后要写回的像素宽高比。空表示按方形像素处理。
    /// 老片常见非方形像素（如 136:135），重编码若被压成 1:1，
    /// 输出的 SAR 会与未重编码的段不一致，下次合并又会触发一次无谓的重编码。
    /// 这里保留第一段输入的 SAR，让重编码段与保留段保持同一显示比例。
    /// </summary>
    public string TargetSar { get; private init; } = "";
    public int Crf { get; private init; } = 20;

    /// <summary>老编码（mpeg4/mpeg2video）的质量档，由 CRF 折算：CRF 20 ≈ q2，越高越差。</summary>
    public int QScale => Math.Clamp(Crf - 18, 2, 10);
    public string Preset { get; private init; } = "medium";
    public string PixelFormat { get; private init; } = "yuv420p";
    public bool TonemapToSdr { get; private init; }

    public string AudioCodec { get; private init; } = "aac";
    public int AudioBitrateKbps { get; private init; } = 192;
    public int SampleRate { get; private init; } = 48000;
    public string ChannelLayout { get; private init; } = "stereo";

    public int ExpectedVideoStreams { get; private init; } = 1;
    public int ExpectedAudioStreams { get; private init; }
    public List<string> Notes { get; } = new();

    public static MergePlan Create(VideoMergeOptions options, IReadOnlyList<MediaBundle> bundles, bool streamCopy,
                                   FfmpegCapabilities? capabilities = null)
    {
        string container = options.NormalizedContainer();
        string extension = container == "mp4" ? ".mp4" : ".mkv";

        List<AudioStreamInfo> audios = bundles
            .SelectMany(b => b.Audios.Take(1)).ToList();
        List<VideoStreamInfo> videos = bundles
            .Select(b => b.PrimaryVideo).Where(v => v is not null).Select(v => v!).ToList();

        int expectedAudio = audios.Count > 0 ? 1 : 0;
        // 纯音频输入没有视频流，不能对空集合调用 Max；引擎会给出「输入文件没有视频流」的明确失败。
        int targetW = options.TargetWidth > 0 ? options.TargetWidth : (videos.Count > 0 ? videos.Max(v => v.Width) : 1920);
        int targetH = options.TargetHeight > 0 ? options.TargetHeight : (videos.Count > 0 ? videos.Max(v => v.Height) : 1080);
        double targetFps = options.TargetFrameRate > 0
            ? options.TargetFrameRate
            : (videos.Count > 0 ? Math.Round(videos.Max(v => v.EffectiveFrameRate), 3) : 30);
        // yuv420p 要求宽高为偶数，目标尺寸在此统一消毒。
        bool evenAdjustedW = targetW > 0 && (targetW & 1) != 0;
        bool evenAdjustedH = targetH > 0 && (targetH & 1) != 0;
        if (evenAdjustedW) targetW--;
        if (evenAdjustedH) targetH--;

        int sampleRate = options.SampleRate > 0
            ? options.SampleRate
            : Math.Min(48000, audios.Count == 0 ? 48000 : audios.Max(a => a.SampleRate));
        if (sampleRate <= 0) sampleRate = 48000;
        int maxChannels = audios.Count == 0 ? 2 : audios.Max(a => a.Channels);
        string layout = options.ChannelLayout.Length > 0
            ? options.ChannelLayout
            : maxChannels >= 6 ? "5.1" : maxChannels >= 2 ? "stereo" : "mono";

        bool audioCopy = streamCopy && options.Audio != AudioStrategy.Reencode && audios.Count > 0;

        // 用户没指定时，按输入编码选择同族编码器：
        // 同一批素材重编码后仍保持同一编码，输出才能与未重编码的段一致，
        // 也避免把 MPEG-4 Part 2 之类老编码悄悄换成 H.264 造成混编。
        string sourceCodec = videos.Count > 0 ? videos[0].Codec : "";
        string codec = options.VideoCodec.Length > 0
            ? options.VideoCodec
            : string.Equals(sourceCodec, "hevc", StringComparison.OrdinalIgnoreCase) ? "libx265"
            : string.Equals(sourceCodec, "mpeg4", StringComparison.OrdinalIgnoreCase) ? "mpeg4"
            : string.Equals(sourceCodec, "mpeg2video", StringComparison.OrdinalIgnoreCase) ? "mpeg2video"
            : "libx264";
        string outputCodecName = codec.Contains("265", StringComparison.Ordinal) ||
                                 codec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ? "hevc"
            : codec is "mpeg4" or "mpeg2video" ? codec : "h264";
        // 老编码不认 -crf/-preset，走固定质量档（qscale），下面按此分支拼参数。
        bool legacyCodec = codec is "mpeg4" or "mpeg2video";

        // 能力回退：ffmpeg 未编译对应编码器时退回 libx264，避免生成必然失败的命令行。
        bool fallbackX265 = false, fallbackNvenc = false;
        if (capabilities is not null)
        {
            if (codec.Contains("265", StringComparison.Ordinal) && !capabilities.X265)
            {
                codec = "libx264";
                outputCodecName = "h264";
                fallbackX265 = true;
            }
            else if (codec.Contains("nvenc", StringComparison.OrdinalIgnoreCase) && !capabilities.Nvenc)
            {
                codec = "libx264";
                outputCodecName = "h264";
                fallbackNvenc = true;
            }
        }

        string pixFmt = options.PixelFormat.Length > 0
            ? options.PixelFormat
            : (videos.Any(v => v.PixFmt.Contains("10", StringComparison.Ordinal) || v.IsHdr)
                ? "yuv420p10le" : "yuv420p");

        // 只有在重编码（streamCopy=false）且输入 SAR 非方形时才需要写回 SAR。
        string targetSar = "";
        if (!streamCopy && videos.Count > 0)
        {
            string sourceSar = videos[0].Sar;
            if (sourceSar.Length > 0 && sourceSar is not ("0:1" or "1:1")) targetSar = sourceSar;
        }

        bool hdrMixed = videos.Select(v => v.HdrFormat).Distinct(StringComparer.Ordinal).Count() > 1;
        bool tonemap = hdrMixed && videos.Any(v => v.IsHdr) && videos.Any(v => !v.IsHdr);

        string audioCodec = options.AudioCodec.Length > 0 ? options.AudioCodec : "aac";

        var created = new MergePlan
        {
            VideoCopy = streamCopy,
            AudioCopy = audioCopy,
            ContainerExtension = extension,
            TargetWidth = targetW,
            TargetHeight = targetH,
            TargetFrameRate = targetFps,
            Fit = options.Fit,
            VideoCodec = codec,
            VideoCodecOutputName = outputCodecName,
            Crf = options.Crf,
            Preset = options.Preset,
            PixelFormat = pixFmt,
            TargetSar = targetSar,
            TonemapToSdr = tonemap,
            AudioCodec = audioCodec,
            AudioBitrateKbps = options.AudioBitrateKbps,
            SampleRate = sampleRate,
            ChannelLayout = layout,
            ExpectedVideoStreams = 1,
            ExpectedAudioStreams = expectedAudio,
        };

        if (evenAdjustedW || evenAdjustedH)
            created.Notes.Add("目标分辨率已调整为偶数：" + targetW + "x" + targetH);
        if (fallbackX265) created.Notes.Add("ffmpeg 不支持 libx265，已回退到 libx264");
        if (fallbackNvenc) created.Notes.Add("ffmpeg 不支持 nvenc，已回退到 libx264");
        if (!streamCopy)
            created.Notes.Add("统一输出参数：" + created.Describe());
        if (tonemap)
            created.Notes.Add("检测到 HDR/SDR 混合输入，已启用 HDR→SDR 色调映射（zscale + tonemap），" +
                              "不是只改色彩标记");
        if (audios.Count == 0)
            created.Notes.Add("所有输入都没有音轨，输出同样不含音轨");
        if (bundles.Any(b => b.Audios.Count > 1))
            created.Notes.Add("多音轨输入只取第一条音轨");
        return created;
    }

    /// <summary>
    /// 为「只重编码少数派」创建方案：统一到保留段的参数，而不是全部输入的最大值。
    ///
    /// 与 <see cref="Create"/> 的差别只有一点、但很关键：目标参数取自**保留段**。
    /// 若仍按全部输入取最大分辨率/最高帧率，重编码出来的片段就与保留段对不上，
    /// 拼接结果依然是坏的。这里把保留段当作基准，少数派向它靠拢。
    /// </summary>
    public static MergePlan CreateForPartial(VideoMergeOptions options,
                                             IReadOnlyList<MediaBundle> referenceBundles,
                                             IReadOnlyList<MediaBundle> allBundles,
                                             FfmpegCapabilities? capabilities = null)
    {
        // 先用保留段当作「全部输入」构造一遍：目标分辨率/帧率/采样率/编码器都会收敛到保留段。
        // streamCopy 传 false，因为这里要拿到完整的重编码参数。
        MergePlan referencePlan = Create(options, referenceBundles, streamCopy: false, capabilities);

        // HDR / 位深必须按全部输入判定，不能只看保留段：
        // 若多数派是 SDR 而重编码的少数派是 HDR，referencePlan 会得到 SDR 的
        // yuv420p + TonemapToSdr=false，但 ColorMetadataFilter 仍给少数派打 bt2020/PQ 标记，
        // 产出 8bit 却标 HDR 的非法流。用 allBundles 重算这两个字段。
        MergePlan allPlan = Create(options, allBundles, streamCopy: false, capabilities);

        // 章节与预期流数量仍按全部输入计算；其余编码参数保持不变。
        var plan = new MergePlan
        {
            VideoCopy = false,
            // 音频跟着视频一起重编码，避免「视频重编码、音频 copy」在采样率不一致时错位。
            AudioCopy = false,
            ContainerExtension = referencePlan.ContainerExtension,
            TargetWidth = referencePlan.TargetWidth,
            TargetHeight = referencePlan.TargetHeight,
            TargetFrameRate = referencePlan.TargetFrameRate,
            Fit = referencePlan.Fit,
            VideoCodec = referencePlan.VideoCodec,
            VideoCodecOutputName = referencePlan.VideoCodecOutputName,
            Crf = referencePlan.Crf,
            Preset = referencePlan.Preset,
            PixelFormat = allPlan.PixelFormat,
            TargetSar = referencePlan.TargetSar,
            TonemapToSdr = allPlan.TonemapToSdr,
            AudioCodec = referencePlan.AudioCodec,
            AudioBitrateKbps = referencePlan.AudioBitrateKbps,
            SampleRate = referencePlan.SampleRate,
            ChannelLayout = referencePlan.ChannelLayout,
            ExpectedVideoStreams = 1,
            ExpectedAudioStreams = allBundles.Any(b => b.HasAudio) ? 1 : 0,
        };
        plan.Notes.AddRange(referencePlan.Notes);
        plan.Notes.Add("仅重编码偏离多数派的片段，其余片段保持原样（不重新编码）");
        return plan;
    }

    public string Describe()
    {
        var parts = new List<string>();
        if (VideoCopy)
        {
            parts.Add("视频：直接复制（无损拼接）");
        }
        else
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture,
                "视频：{0} CRF {1} / {2} / {3}x{4} / {5:0.###}fps / {6} / 适配 {7}",
                VideoCodec, Crf, Preset, TargetWidth, TargetHeight, TargetFrameRate, PixelFormat, Fit));
            if (TonemapToSdr) parts.Add("HDR→SDR 色调映射");
        }
        parts.Add(AudioCopy
            ? "音频：直接复制"
            : string.Format(CultureInfo.InvariantCulture, "音频：{0} {1} kbps / {2} Hz / {3}",
                AudioCodec, AudioBitrateKbps, SampleRate, ChannelLayout));
        parts.Add("容器：" + ContainerExtension.TrimStart('.'));
        return string.Join("；", parts);
    }

    /// <summary>
    /// 把单个输入重封装为统一时间基准的中间片段（全程 -c copy，画质零损失）。
    /// 仅用于「差异全部落在时间轴参数上」的情形：帧率保持原样（各段的帧间隔由码流本身决定），
    /// 只把时基统一到足够细的刻度，concat 时就不会产生非单调 DTS 与错位时长。
    /// </summary>
    public List<string> BuildRemuxCommand(MediaBundle bundle, string outputPath, string ffmpeg = "ffmpeg")
    {
        // 时间基准取 1/1000 的整数倍且能被常见帧率整除：1/90000 对 24/25/30/29.97 都能精确表达。
        const int trackTimescale = 90000;
        return new List<string>
        {
            ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1",
            "-i", bundle.Path,
            "-map", "0:v:0",
        }
        // 走到重封装路径时各输入的音频轨数量必然一致（数量不一致会被判为需要重编码），
        // 因此按本段是否真有音轨决定映射，不会映射出不存在的流。
        .Concat(bundle.HasAudio && ExpectedAudioStreams > 0 ? new[] { "-map", "0:a:0" } : Array.Empty<string>())
        .Concat(new[]
        {
            "-c", "copy",
            "-video_track_timescale", trackTimescale.ToString(CultureInfo.InvariantCulture),
            // 统一封装到 matroska 中间文件，避免 mp4 的 edit list 在拼接时引入额外偏移。
            "-f", "matroska", outputPath,
        })
        .ToList();
    }

    /// <summary>
    /// 把任意一个已存在的媒体文件重封装为统一时基的 matroska（全程 -c copy）。
    ///
    /// 与 <see cref="BuildRemuxCommand"/> 的差别：那个需要 <see cref="MediaBundle"/> 来判断音轨，
    /// 这个用可选的流选择符 <c>-map 0:a:0?</c>，没有音轨时也不会失败。
    /// 用途是「只重编码少数派」时把保留段也统一到同一容器与时基——
    /// 重编码产出的片段必然是 mkv/1:1000，而原文件可能是 mp4/1:12288，
    /// 不统一这两项，拼接结果依然不可用（实测会被兼容性判定直接拦下）。
    /// 注意：这一步是纯复制，视频码流逐比特不变，不属于「重新编码」。
    /// </summary>
    public List<string> BuildRemuxCommandForFile(string inputPath, string outputPath, string ffmpeg = "ffmpeg")
    {
        const int trackTimescale = 90000;
        return new List<string>
        {
            ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1",
            "-i", inputPath,
            "-map", "0:v:0",
            // 问号表示"存在才映射"：部分输入没有音轨时不会因此失败。
            "-map", "0:a:0?",
            "-c", "copy",
            "-video_track_timescale", trackTimescale.ToString(CultureInfo.InvariantCulture),
            "-f", "matroska", outputPath,
        };
    }

    /// <summary>
    /// 统一重新编码单个输入为中间片段。分辨率适配在滤镜链内完成：
    /// Contain=补边、Cover=裁切、Stretch=直接拉伸，均先按 SAR 还原方形像素，避免画面变形。
    /// 无音轨的输入用 anullsrc 补静音，保证每段音视频时长一致。
    /// </summary>
    public List<string> BuildSegmentCommand(MediaBundle bundle, string outputPath, string ffmpeg = "ffmpeg")
    {
        var cmd = new List<string>
        {
            // -progress pipe:1 是 FfmpegRunner 唯一的进度来源：没有它，ffmpeg 的 stdout 永不产出，
            // 上层只能等到进程退出才拿到结果，UI 会一直停在上一阶段的文字上，看起来像卡死。
            ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1",
            "-i", bundle.Path,
        };
        if (!bundle.HasAudio)
        {
            cmd.AddRange(new[] { "-f", "lavfi", "-i" });
            cmd.Add(string.Format(CultureInfo.InvariantCulture,
                "anullsrc=channel_layout={0}:sample_rate={1}", ChannelLayout, SampleRate));
        }

        double duration = bundle.Duration > 0 ? bundle.Duration : 1.0;
        cmd.AddRange(new[] { "-map", "0:v:0" });
        cmd.AddRange(new[] { "-map", bundle.HasAudio ? "0:a:0" : "1:a:0" });

        string chain = BuildVideoFilter(bundle);
        cmd.AddRange(new[] { "-vf", chain });
        cmd.AddRange(new[] { "-c:v", VideoCodec, "-pix_fmt", PixelFormat });
        if (VideoCodec is "mpeg4" or "mpeg2video")
        {
            // MPEG-Part2 系编码器没有 -crf/-preset，用 qscale 表达同等质量档（2 最好，31 最差）。
            cmd.AddRange(new[] { "-q:v", QScale.ToString(CultureInfo.InvariantCulture) });
        }
        else
        {
            cmd.AddRange(new[] { "-preset", Preset });
            // libx265 不接受 -crf（那是 x264/nvenc 的选项），必须走 -x265-params
            if (VideoCodec.Contains("265", StringComparison.Ordinal))
                cmd.AddRange(new[] { "-x265-params", "crf=" + Crf.ToString(CultureInfo.InvariantCulture) });
            else
                cmd.AddRange(new[] { "-crf", Crf.ToString(CultureInfo.InvariantCulture) });
        }
        if (TargetFrameRate > 0)
            cmd.AddRange(new[] { "-r", TargetFrameRate.ToString("0.###", CultureInfo.InvariantCulture) });
        // 老编码的 SAR 由码流头携带，仅靠 setsar 滤镜不够，必须同时写容器侧的 -aspect，
        // 否则输出会被 ffprobe 报成约分后的近似值（如 100:99），与保留段对不上。
        if (TargetSar.Length > 0)
            cmd.AddRange(new[] { "-aspect", TargetSar });


        cmd.AddRange(new[] { "-c:a", AudioCodec,
            "-b:a", (AudioBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k"),
            "-ac", ChannelCount(ChannelLayout).ToString(CultureInfo.InvariantCulture),
            "-ar", SampleRate.ToString(CultureInfo.InvariantCulture) });
        if (!bundle.HasAudio)
            cmd.AddRange(new[] { "-shortest" });

        cmd.AddRange(new[] { "-t", duration.ToString("0.###", CultureInfo.InvariantCulture) });
        cmd.AddRange(new[] { "-f", "matroska", outputPath });
        return cmd;
    }

    private string BuildVideoFilter(MediaBundle bundle)
    {
        // 先消毒奇偶尺寸与 SAR，再按适配模式缩放，最后统一像素格式。
        if (TonemapToSdr && bundle.PrimaryVideo is { IsHdr: true })
            return BuildTonemapChain(BuildGeometryChain(), bundle);

        var parts = new List<string>
        {
            "setsar=1",
            string.Format(CultureInfo.InvariantCulture, "scale={0}:{1}:force_original_aspect_ratio={2}",
                TargetWidth, TargetHeight, Fit == FitMode.Contain ? "decrease" : "increase"),
        };
        if (Fit == FitMode.Contain)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture,
                "pad={0}:{1}:(ow-iw)/2:(oh-ih)/2:color=black", TargetWidth, TargetHeight));
        }
        else if (Fit == FitMode.Cover)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture,
                "crop={0}:{1}", TargetWidth, TargetHeight));
        }
        // Stretch：scale 的 force_original_aspect_ratio=increase 后需要精确拉伸回目标尺寸
        if (Fit == FitMode.Stretch)
            parts.Add(string.Format(CultureInfo.InvariantCulture, "scale={0}:{1}", TargetWidth, TargetHeight));

        // 最终统一像素格式前截断到偶数尺寸，保证 yuv420p 编码不会因奇数宽高失败。
        if (Is420()) parts.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        // 有目标 SAR 时写回原像素宽高比，否则按方形像素输出。
        parts.Add(TargetSar.Length > 0 ? "setsar=" + TargetSar.Replace(':', '/') : "setsar=1");
        parts.Add("format=" + PixelFormat);
        parts.Add(ColorMetadataFilter(bundle));
        return string.Join(",", parts);
    }

    /// <summary>不含色彩处理的几何链（缩放/补边/裁切），供色调映射前置使用。</summary>
    private string BuildGeometryChain()
    {
        var parts = new List<string>
        {
            "setsar=1",
            string.Format(CultureInfo.InvariantCulture, "scale={0}:{1}:force_original_aspect_ratio={2}",
                TargetWidth, TargetHeight, Fit == FitMode.Contain ? "decrease" : "increase"),
        };
        if (Is420()) parts.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        if (Fit == FitMode.Contain)
            parts.Add(string.Format(CultureInfo.InvariantCulture,
                "pad={0}:{1}:(ow-iw)/2:(oh-ih)/2:color=black", TargetWidth, TargetHeight));
        else if (Fit == FitMode.Cover)
            parts.Add(string.Format(CultureInfo.InvariantCulture, "crop={0}:{1}", TargetWidth, TargetHeight));
        if (Fit == FitMode.Stretch)
            parts.Add(string.Format(CultureInfo.InvariantCulture, "scale={0}:{1}", TargetWidth, TargetHeight));
        parts.Add("setsar=1");
        return string.Join(",", parts);
    }

    /// <summary>
    /// 用滤镜内的 setparams 写入色彩标记。
    /// 注意：编码器的 -colorspace/-color_trc/-color_primaries 在 mp4/mkv 封装下不一定写进码流，
    /// setparams 是可靠的写法——这一点对「HDR 不能被伪装成 SDR、SDR 也不能被误标成 HDR」很关键。
    /// </summary>
    private string ColorMetadataFilter(MediaBundle bundle)
    {
        if (TonemapToSdr && bundle.PrimaryVideo is { IsHdr: true })
            return "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv";
        if (bundle.PrimaryVideo is { IsHdr: true })
            return "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc:range=tv";
        return "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv";
    }

    /// <summary>
    /// HDR→SDR 的真正色调映射：线性化 → 色调映射 → 回到 BT.709 传递函数。
    /// 只改标记而不改像素，会造成「看起来发灰/发白」的假 HDR 转换，这里不做那种事。
    /// </summary>
    private string BuildTonemapChain(string geometry, MediaBundle bundle) =>
        geometry + ",format=gbrpf32le,zscale=t=linear:npl=100:p=bt709," +
        "tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:p=bt709:r=tv," +
        "format=" + PixelFormat + ",setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv";

    /// <summary>拼接命令：concat demuxer + 全程 stream copy；有章节时同时挂 ffmetadata。</summary>
    public List<string> BuildConcatCommand(string listPath, string workDirectory, string outputPath,
                                          string ffmpeg = "ffmpeg", string? metaPath = null)
    {
        var cmd = new List<string>
        {
            // 同样需要 -progress：拼接阶段同样由 FfmpegRunner 读取进度，否则进度条在大文件上完全不动。
            ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1",
            "-f", "concat", "-safe", "0", "-i", listPath,
        };
        bool withChapters = !string.IsNullOrEmpty(metaPath);
        if (withChapters)
        {
            // 章节来自外部 ffmetadata 输入（第 1 号输入），只搬运章节目录，不改码流。
            cmd.AddRange(new[] { "-f", "ffmetadata", "-i", metaPath! });
        }

        cmd.AddRange(new[] { "-map", "0:v:0" });
        if (ExpectedAudioStreams > 0) cmd.AddRange(new[] { "-map", "0:a:0" });
        if (withChapters)
            cmd.AddRange(new[] { "-map_metadata", "0", "-map_chapters", "1", "-c", "copy" });
        else
            cmd.AddRange(new[] { "-map_metadata", "0", "-map_chapters", "-1", "-c", "copy" });
        cmd.AddRange(ContainerExtension == ".mp4"
            ? new[] { "-movflags", "+faststart", "-f", "mp4" }
            : new[] { "-f", "matroska" });
        cmd.Add(outputPath);
        return cmd;
    }

    /// <summary>4:2:0 像素格式要求宽高为偶数；先消毒输入尺寸再补边/裁切。</summary>
    private bool Is420() => PixelFormat.StartsWith("yuv420p", StringComparison.Ordinal);

    private static int ChannelCount(string layout) => layout switch
    {
        "mono" => 1,
        "stereo" => 2,
        "5.1" => 6,
        "7.1" => 8,
        _ => 2,
    };
}
