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
    public int Crf { get; private init; } = 20;
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
        if (options.Audio == AudioStrategy.Copy && streamCopy) audioCopy = audios.Count > 0;

        string codec = options.VideoCodec.Length > 0
            ? options.VideoCodec
            : videos.Count > 0 && string.Equals(videos[0].Codec, "hevc", StringComparison.OrdinalIgnoreCase)
                ? "libx265" : "libx264";
        string outputCodecName = codec.Contains("265", StringComparison.Ordinal) ||
                                 codec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ? "hevc" : "h264";

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
    /// 统一重新编码单个输入为中间片段。分辨率适配在滤镜链内完成：
    /// Contain=补边、Cover=裁切、Stretch=直接拉伸，均先按 SAR 还原方形像素，避免画面变形。
    /// 无音轨的输入用 anullsrc 补静音，保证每段音视频时长一致。
    /// </summary>
    public List<string> BuildSegmentCommand(MediaBundle bundle, string outputPath, string ffmpeg = "ffmpeg")
    {
        var cmd = new List<string>
        {
            ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
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
        cmd.AddRange(new[] { "-c:v", VideoCodec, "-preset", Preset, "-pix_fmt", PixelFormat });
        // libx265 不接受 -crf（那是 x264/nvenc 的选项），必须走 -x265-params
        if (VideoCodec.Contains("265", StringComparison.Ordinal))
            cmd.AddRange(new[] { "-x265-params", "crf=" + Crf.ToString(CultureInfo.InvariantCulture) });
        else
            cmd.AddRange(new[] { "-crf", Crf.ToString(CultureInfo.InvariantCulture) });
        if (TargetFrameRate > 0)
            cmd.AddRange(new[] { "-r", TargetFrameRate.ToString("0.###", CultureInfo.InvariantCulture) });


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
        parts.Add("setsar=1");
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
            ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
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
