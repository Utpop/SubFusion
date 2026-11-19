using System.Globalization;

namespace SubFusion.Core.Compress;

/// <summary>压制阶段，供 UI 展示。</summary>
public enum CompressStage
{
    Idle = 0,
    Validating,
    Probing,
    Planning,
    Encoding,
    Verifying,
    Moving,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>码率控制方式。</summary>
public enum RateControl
{
    /// <summary>按画质定码率（CRF / CQP），文件大小随内容变化。</summary>
    Quality = 0,

    /// <summary>按目标码率（VBR），文件大小可预期。</summary>
    Bitrate,
}

/// <summary>分辨率处理。</summary>
public enum CompressResize
{
    /// <summary>保持原样（只做偶数对齐）。</summary>
    None = 0,

    /// <summary>缩到目标长边，短边按原比例。</summary>
    LongEdge,

    /// <summary>缩放到固定宽高（保持比例并补边，不拉伸）。</summary>
    Fit,

    /// <summary>按整数倍缩小（等比），常用于把 4K 降到 1080p。</summary>
    Scale,
}

/// <summary>
/// 一次「视频压制」任务的全部参数。
///
/// 与裁剪/合并一样：引擎不依赖任何 GUI 类型，UI 只构造这个对象并收集进度与结果。
/// 所有字段都有明确默认值，默认值就是「最安全」的那一档（H.264 + 画质模式 + 保留音轨原样）。
/// </summary>
public sealed class VideoCompressOptions
{
    /// <summary>输入文件完整路径。</summary>
    public string Input { get; set; } = "";

    /// <summary>输出文件完整路径（含扩展名）。</summary>
    public string OutputPath { get; set; } = "";

    #region 视频

    /// <summary>目标视频编码格式。</summary>
    public VideoCodecKind Codec { get; set; } = VideoCodecKind.H264;

    /// <summary>
    /// 编码器偏好：<c>auto</c>（有显卡就用显卡）/ <c>hardware</c>（只用显卡）/ <c>software</c>（只用 CPU）。
    /// </summary>
    public string EncoderPreference { get; set; } = "auto";

    /// <summary>限定后端；空集合表示不限。界面用它让用户只选 AMD / NVIDIA / Intel / CPU。</summary>
    public List<EncoderKind> AllowedBackends { get; set; } = new();

    /// <summary>指定编码器名（如 h264_amf）；空串表示按偏好自动选。</summary>
    public string EncoderOverride { get; set; } = "";

    /// <summary>硬件编码质量档：quality / balanced / speed（只对 AMF/NVENC/QSV 有意义）。</summary>
    public string HardwareQuality { get; set; } = "balanced";

    /// <summary>码率控制方式。</summary>
    public RateControl RateControl { get; set; } = RateControl.Quality;

    /// <summary>软件编码的画质档（CRF，越小越清晰）。H.264 用 18~28，H.265 用 20~30。</summary>
    public int Quality { get; set; } = 23;

    /// <summary>目标码率（kbps），只在 <see cref="RateControl.Bitrate"/> 下使用。</summary>
    public int TargetBitrateKbps { get; set; } = 2500;

    /// <summary>软件编码速度档（ultrafast..veryslow）。硬件编码忽略此项，改用 <see cref="HardwareQuality"/>。</summary>
    public string Preset { get; set; } = "medium";

    /// <summary>目标帧率；0 表示保持原样。只降不升（超过源帧率时不放大）。</summary>
    public double FrameRate { get; set; }

    #endregion

    #region 缩放

    public CompressResize Resize { get; set; } = CompressResize.None;

    /// <summary>缩放的参照值：None 忽略；LongEdge 为长边像素；Fit 见 Width/Height；Scale 为倍数分母。</summary>
    public int ResizeValue { get; set; } = 1080;

    public int Width { get; set; }

    public int Height { get; set; }

    #endregion

    #region 音频

    /// <summary>音频是否重编码。false 时尝试原样复制（要求容器兼容，见 ValidateAudio）。</summary>
    public bool ReencodeAudio { get; set; } = true;

    public string AudioCodec { get; set; } = "aac";

    public int AudioBitrateKbps { get; set; } = 192;

    #endregion

    #region 其它

    /// <summary>输出容器：mkv / mp4。</summary>
    public string Container { get; set; } = "mp4";

    /// <summary>目标已存在时是否覆盖；false 时任务直接失败，不覆盖任何数据。</summary>
    public bool Overwrite { get; set; } = true;

    /// <summary>完成后校验输出（时长 / 流结构 / 首尾解码）。</summary>
    public bool VerifyOutput { get; set; } = true;

    /// <summary>是否保留源字幕轨。</summary>
    public bool KeepSubtitles { get; set; }

    /// <summary>
    /// 使用显卡解码（d3d11va）。实测在本机（RX 580）能让「解码 + AMF 编码」走显卡，
    /// 但缩放等滤镜仍会回落到内存，收益取决于源分辨率；默认开启，失败时引擎自动退回软解。
    /// </summary>
    public bool HardwareDecode { get; set; } = true;

    /// <summary>临时目录；null 表示与输出同目录（同卷改名，零额外空间）。</summary>
    public string? TempDirectory { get; set; }

    /// <summary>探测子进程超时（秒）。</summary>
    public int ProbeTimeoutSeconds { get; set; } = 60;

    #endregion

    /// <summary>规整容器名为 "mkv" / "mp4"。</summary>
    public string NormalizedContainer()
    {
        string c = (Container ?? "").Trim().TrimStart('.').ToLowerInvariant();
        return c.Length == 0 ? "mp4" : c;
    }

    /// <summary>参数自检；返回空列表表示可以执行。</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Input)) errors.Add("未选择输入文件");
        else if (!File.Exists(Input)) errors.Add("输入文件不存在：" + Input);

        if (string.IsNullOrWhiteSpace(OutputPath)) errors.Add("未指定输出路径");
        else
        {
            string full = SafeFullPath(OutputPath);
            if (!string.IsNullOrWhiteSpace(Input) &&
                string.Equals(SafeFullPath(Input), full, StringComparison.OrdinalIgnoreCase))
                errors.Add("输出路径不能与输入文件相同");
        }

        string container = NormalizedContainer();
        if (container is not ("mkv" or "mp4"))
            errors.Add("不支持的输出容器：" + Container);

        if (Quality is < 0 or > 51) errors.Add("画质档超出范围（0-51，越大越模糊）");
        if (RateControl == RateControl.Bitrate && TargetBitrateKbps is < 100 or > 200000)
            errors.Add("目标码率超出范围（100-200000 kbps）");
        if (ReencodeAudio && AudioBitrateKbps is < 32 or > 1024)
            errors.Add("音频码率超出范围（32-1024 kbps）");
        if (FrameRate < 0 || FrameRate > 240)
            errors.Add("目标帧率超出范围（0-240，0 表示保持原样）");

        if (Resize == CompressResize.LongEdge && ResizeValue is < 64 or > 8192)
            errors.Add("目标长边超出范围（64-8192）");
        if (Resize == CompressResize.Scale && ResizeValue is < 2 or > 8)
            errors.Add("缩小倍数超出范围（2-8）");
        if (Resize == CompressResize.Fit &&
            (Width is < 64 or > 8192 || Height is < 64 or > 8192))
            errors.Add("目标宽高超出范围（64-8192）");

        return errors;
    }

    /// <summary>
    /// 音频容器约束：mp4 装不下 Vorbis / Opus / FLAC / PCM 之外的多数「原样复制」组合时，
    /// ffmpeg 会以非 0 退出且错误信息难懂，这里提前给结论。
    /// </summary>
    public static List<string> ValidateAudioCopy(string container, string sourceAudioCodec, bool hasAudio)
    {
        var errors = new List<string>();
        if (!hasAudio || container != "mp4") return errors;
        string codec = (sourceAudioCodec ?? "").ToLowerInvariant();
        if (codec.Length == 0) return errors;
        string[] mp4Friendly = { "aac", "mp3", "ac3", "eac3", "alac", "opus", "flac" };
        if (!mp4Friendly.Contains(codec, StringComparer.Ordinal))
            errors.Add(string.Format(CultureInfo.InvariantCulture,
                "MP4 装不下原样的 {0} 音轨，请打开「重新编码音频」或改用 MKV 容器", codec));
        return errors;
    }

    public VideoCompressOptions Clone() => (VideoCompressOptions)MemberwiseClone();

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
