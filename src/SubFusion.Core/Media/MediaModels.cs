using System.Globalization;

namespace SubFusion.Core.Media;

/// <summary>
/// 一条视频流的完整 ffprobe 结果。所有字段都直接来自 ffprobe，
/// 缺失值使用空串 / 0 / null，调用方不需要再解析原始 JSON。
/// </summary>
public sealed class VideoStreamInfo
{
    public int Index { get; set; }
    public string Codec { get; set; } = "";
    public string Profile { get; set; } = "";
    public int Level { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>像素宽高比（sample_aspect_ratio），如 "1:1" / "40:33"。</summary>
    public string Sar { get; set; } = "";
    /// <summary>显示宽高比（display_aspect_ratio），如 "16:9"。</summary>
    public string Dar { get; set; } = "";
    public string PixFmt { get; set; } = "";

    /// <summary>平均帧率（avg_frame_rate，可能为 0）。</summary>
    public double AvgFrameRate { get; set; }
    /// <summary>实际帧率（r_frame_rate），用于判定能否直接拼接。</summary>
    public double RealFrameRate { get; set; }
    /// <summary>时间基准（time_base），如 "1/90000"。</summary>
    public string TimeBase { get; set; } = "";

    public string ColorSpace { get; set; } = "";
    public string ColorRange { get; set; } = "";
    public string ColorTransfer { get; set; } = "";
    public string ColorPrimaries { get; set; } = "";

    /// <summary>HDR 格式标记："" 表示 SDR；否则为 "PQ(HDR10)" / "HLG" / "DOVI" / "HDR10+"。</summary>
    public string HdrFormat { get; set; } = "";
    public bool IsHdr => HdrFormat.Length > 0;

    /// <summary>本视频流时长（秒）；容器时长缺失时用它。</summary>
    public double Duration { get; set; }
    public double StartTime { get; set; }
    /// <summary>帧数（nb_frames），不可靠时为空。</summary>
    public long? FrameCount { get; set; }
    public double BitRate { get; set; }

    public double EffectiveFrameRate => RealFrameRate > 0 ? RealFrameRate : AvgFrameRate;

    /// <summary>SAR 是否为方形像素（1:1 或缺省）。</summary>
    public bool HasSquarePixels
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Sar)) return true;
            if (Sar is "0:1" or "0:0") return true;   // ffprobe 用 0:1 表示未知，按方形像素处理
            string[] parts = Sar.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2) return false;
            return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
                   double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) &&
                   h > 0 && Math.Abs(w / h - 1.0) < 1e-9;
        }
    }

    public string SizeText => Width > 0 && Height > 0
        ? string.Format(CultureInfo.InvariantCulture, "{0}x{1}", Width, Height) : "";
}

/// <summary>一条音频流的 ffprobe 结果。</summary>
public sealed class AudioStreamInfo
{
    public int Index { get; set; }
    public string Codec { get; set; } = "";
    public string Profile { get; set; } = "";
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public string ChannelLayout { get; set; } = "";
    public double Duration { get; set; }
    public double StartTime { get; set; }
    public double BitRate { get; set; }
}

/// <summary>容器信息。</summary>
public sealed class ContainerInfo
{
    public string FormatName { get; set; } = "";
    public string FormatLongName { get; set; } = "";
    public double Duration { get; set; }
    public double StartTime { get; set; }
    public long Size { get; set; }
    public double BitRate { get; set; }
    public int ProbeScore { get; set; }

    /// <summary>容器族：mkv / mp4 / other。</summary>
    public string Family
    {
        get
        {
            string n = FormatName.ToLowerInvariant();
            if (n.Contains("matroska", StringComparison.Ordinal)) return "mkv";
            if (n.Contains("mp4", StringComparison.Ordinal) || n.Contains("mov", StringComparison.Ordinal)) return "mp4";
            return "other";
        }
    }
}

/// <summary>
/// 一个媒体文件的完整探测结果（视频流 + 音频流 + 容器）。
/// 上层业务只使用本模型，不直接接触 ffprobe 的原始 JSON。
/// </summary>
public sealed class MediaBundle
{
    public string Path { get; set; } = "";
    public bool Success { get; set; }
    public string Error { get; set; } = "";

    public List<VideoStreamInfo> Videos { get; } = new();
    public List<AudioStreamInfo> Audios { get; } = new();
    public int SubtitleStreamCount { get; set; }
    public ContainerInfo Container { get; set; } = new();

    public VideoStreamInfo? PrimaryVideo => Videos.Count > 0 ? Videos[0] : null;
    public AudioStreamInfo? PrimaryAudio => Audios.Count > 0 ? Audios[0] : null;
    public bool HasAudio => Audios.Count > 0;

    /// <summary>用于合并时间轴的片段时长：优先视频流时长，其次容器时长。</summary>
    public double Duration
    {
        get
        {
            VideoStreamInfo? v = PrimaryVideo;
            if (v is not null && v.Duration > 0) return v.Duration;
            return Container.Duration;
        }
    }

    public string DurationText => Duration > 0
        ? TimeSpan.FromSeconds(Duration).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
        : "--:--";

    public string Summary
    {
        get
        {
            if (!Success) return Path + "：" + (Error.Length > 0 ? Error : "无法读取");
            VideoStreamInfo? v = PrimaryVideo;
            string video = v is null ? "无视频流" :
                string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}fps {3}",
                    v.Codec, v.SizeText, Math.Round(v.EffectiveFrameRate, 3), v.PixFmt);
            return string.Format(CultureInfo.InvariantCulture, "{0} / {1} / {2} 音轨 / {3:0.###}s",
                video, HasAudio ? Audios[0].Codec : "无音频", Audios.Count, Duration);
        }
    }
}
