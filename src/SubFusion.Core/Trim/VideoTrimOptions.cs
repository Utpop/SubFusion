using System.Globalization;

namespace SubFusion.Core.Trim;

/// <summary>裁剪方式。</summary>
public enum TrimMode
{
    /// <summary>只做码流复制（-c copy），不重编码；切点必须落在关键帧上，画质逐比特不变。</summary>
    Copy = 0,

    /// <summary>重编码所选区间，切点精确到帧。只重编码这一小段，不是整片。</summary>
    Precise,
}

/// <summary>裁剪阶段，供 UI 展示。</summary>
public enum TrimStage
{
    Idle = 0,
    Validating,
    Probing,
    ScanningKeyframes,
    Planning,
    Cutting,
    Verifying,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// 一次视频裁剪任务的全部参数。所有字段都有明确默认值，UI 只需要收集用户改动过的部分。
/// 引擎不依赖任何 GUI 类型；<see cref="Validate"/> 在真正执行前给出可读的参数错误。
/// </summary>
public sealed class VideoTrimOptions
{
    /// <summary>输入文件完整路径。</summary>
    public string Input { get; set; } = "";

    /// <summary>输出文件完整路径（含扩展名）。</summary>
    public string OutputPath { get; set; } = "";

    /// <summary>用户请求的起点（秒）。Copy 模式下会向前吸附到关键帧。</summary>
    public double StartSeconds { get; set; }

    /// <summary>用户请求的终点（秒）。两种模式下终点都按请求值截断（尾部无需关键帧）。</summary>
    public double EndSeconds { get; set; }

    public TrimMode Mode { get; set; } = TrimMode.Copy;

    /// <summary>
    /// 旋转方向。None 表示不改动——但源文件自带的显示方向会被原样保留，
    /// 否则手机竖拍素材裁完会从竖屏变成横躺。
    /// </summary>
    public RotationMode Rotation { get; set; } = RotationMode.None;

    /// <summary>输出容器：mkv / mp4。默认 mkv——mp4 无法表示负时间戳，会造成音轨起点错位。</summary>
    public string Container { get; set; } = "mkv";

    /// <summary>目标已存在时是否覆盖；false 时任务直接失败，不覆盖任何数据。</summary>
    public bool Overwrite { get; set; } = true;

    /// <summary>裁剪完成后校验输出（时长 / 流结构 / 音画同步 / 首尾解码）。</summary>
    public bool VerifyOutput { get; set; } = true;

    /// <summary>是否保留字幕轨。默认关闭：字幕时间轴不会跟着重写，保留反而容易错位。</summary>
    public bool KeepSubtitles { get; set; }

    /// <summary>精确模式的视频编码器。</summary>
    public string VideoCodec { get; set; } = "libx264";
    public string Preset { get; set; } = "medium";
    public int Crf { get; set; } = 20;

    /// <summary>精确模式的音频编码器与码率。</summary>
    public string AudioCodec { get; set; } = "aac";
    public int AudioBitrateKbps { get; set; } = 192;

    /// <summary>临时目录；null 表示使用系统临时目录下每个任务独立的子目录。</summary>
    public string? TempDirectory { get; set; }

    /// <summary>探测与转码子进程超时（秒）。</summary>
    public int ProbeTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// 音画偏移容差（秒）。0 表示**按素材帧长自动计算**（推荐，也是默认）：
    /// 容差 = 一帧视频 + 两帧音频 + 固定余量，见 <see cref="AvSyncProbe.ToleranceFor"/>。
    ///
    /// 为什么默认不用固定值：实测首包偏移本身带 0.06~0.15 秒的编码/容器对齐偏差，
    /// 而这段偏差的物理上界恰好是「两条流各差一个量化单位」。
    /// 用帧长算出来的阈值既不会误报无损切割，也不会漏掉 1.4 秒级的真错位。
    /// 显式填正数可以覆盖自动计算。
    /// </summary>
    public double SyncToleranceSeconds { get; set; }

    /// <summary>true 表示全程只做码流复制。</summary>
    public bool CopiesStreams => Mode == TrimMode.Copy;

    /// <summary>把用户可能输入的小写/带点容器名规整为 "mkv" / "mp4"。</summary>
    public string NormalizedContainer()
    {
        string c = (Container ?? "").Trim().TrimStart('.').ToLowerInvariant();
        return c.Length == 0 ? "mkv" : c;
    }

    /// <summary>
    /// 把请求区间收敛到「文件实际时长」内。引擎、命令构造与参数自检必须用同一份结果，
    /// 否则会出现「界面允许开始、Validate 却报错」或「-t 与进度分母不一致」。
    /// </summary>
    /// <param name="sourceDuration">输入文件时长（秒）；&lt;= 0 表示未知，按请求值原样返回。</param>
    public (double Start, double End) NormalizeRange(double sourceDuration)
    {
        double end = sourceDuration > 0 ? Math.Min(EndSeconds, sourceDuration) : EndSeconds;
        return (StartSeconds, end);
    }

    /// <summary>参数自检；返回空列表表示可以执行。</summary>
    /// <param name="sourceDuration">
    /// 已知的输入时长（秒）。给出时按「收敛后的终点」判定时长下限，与引擎保持一致；
    /// 0 表示尚不知道（参数还没探测），只用请求值做基础校验。
    /// </param>
    public List<string> Validate(double sourceDuration = 0)
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

        // 终点先与文件实际时长收敛，再做判定：不收敛就会出现「用户填 70s、文件只有 60s，
        // 界面拦截说时长足够，引擎却按 60s 截断并再次失败」这种两套结论。
        (double normalizedStart, double normalizedEnd) = NormalizeRange(sourceDuration);
        if (normalizedStart < 0) errors.Add("起点不能为负数");
        if (normalizedEnd <= normalizedStart)
            errors.Add(string.Format(CultureInfo.InvariantCulture,
                "终点必须大于起点（当前 {0:0.###}s → {1:0.###}s）", normalizedStart, normalizedEnd));
        else if (normalizedEnd - normalizedStart < 0.1)
            errors.Add("裁剪时长至少需要 0.1 秒");

        string container = NormalizedContainer();
        if (container is not ("mkv" or "mp4"))
            errors.Add("不支持的输出容器：" + Container);

        if (Mode == TrimMode.Copy)
        {
            // Copy 模式没有可调编码参数；这里只提示容器约束。
        }
        else
        {
            if ((VideoCodec ?? "").Trim().Length == 0) errors.Add("精确模式必须指定视频编码器");
            if (Crf < 0 || Crf > 51) errors.Add("CRF 超出范围（0-51）");
            if (AudioBitrateKbps is < 32 or > 1024) errors.Add("音频码率超出范围（32-1024 kbps）");
        }
        return errors;
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    public VideoTrimOptions Clone() => (VideoTrimOptions)MemberwiseClone();
}
