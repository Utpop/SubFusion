using System.Globalization;

namespace SubFusion.Core.Merge;

/// <summary>分辨率适配模式。</summary>
public enum FitMode
{
    /// <summary>保持比例、完整显示，必要时补边（默认）。</summary>
    Contain,
    /// <summary>保持比例、填满画面，必要时裁切。</summary>
    Cover,
    /// <summary>不保持比例，直接拉伸到目标分辨率。</summary>
    Stretch,
}

/// <summary>音频统一策略。</summary>
public enum AudioStrategy
{
    /// <summary>参数一致时直接复制（推荐）。</summary>
    Auto = 0,
    /// <summary>强制统一重新编码。</summary>
    Reencode,
    /// <summary>强制直接复制（参数不一致时由兼容性分析判定为不可行）。</summary>
    Copy,
}

/// <summary>合并过程中的阶段，供 UI 展示。</summary>
public enum MergeStage
{
    Idle = 0,
    Validating,
    Probing,
    Planning,
    Merging,
    WritingChapters,
    Verifying,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// 一次视频合并任务的全部参数。所有字段都有明确默认值，UI 只需要收集用户改动过的部分。
/// 引擎不依赖任何 GUI 类型；<see cref="Validate"/> 在真正执行前给出可读的参数错误。
/// </summary>
public sealed class VideoMergeOptions
{
    /// <summary>输入文件列表，顺序即最终合并顺序。</summary>
    public List<string> Inputs { get; set; } = new();

    /// <summary>输出文件完整路径（含扩展名）。</summary>
    public string OutputPath { get; set; } = "";

    /// <summary>输出容器：mkv / mp4。默认 mkv（章节只依赖容器能力，mkv 支持最好）。</summary>
    public string Container { get; set; } = "mkv";

    /// <summary>输入不兼容时是否允许自动重新编码；false 时直接报错说明原因。</summary>
    public bool AllowReencode { get; set; } = true;

    /// <summary>统一编码器："" = 按输入自动选择（h264 保持 h264、hevc 保持 hevc）；也可显式填 libx264 / libx265。</summary>
    public string VideoCodec { get; set; } = "";
    public int Crf { get; set; } = 20;
    public string Preset { get; set; } = "medium";
    /// <summary>重新编码时的像素格式；"" = 自动（8bit→yuv420p，10bit/HDR→yuv420p10le）。</summary>
    public string PixelFormat { get; set; } = "";

    /// <summary>目标分辨率；0 表示按输入自动取最大宽高。</summary>
    public int TargetWidth { get; set; }
    public int TargetHeight { get; set; }
    public FitMode Fit { get; set; } = FitMode.Contain;

    /// <summary>目标帧率；0 表示按输入自动取最高帧率。</summary>
    public double TargetFrameRate { get; set; }

    public AudioStrategy Audio { get; set; } = AudioStrategy.Auto;
    /// <summary>重新编码音频时使用的编码器；"" = 按容器自动（mkv→aac，mp4→aac）。</summary>
    public string AudioCodec { get; set; } = "";
    public int AudioBitrateKbps { get; set; } = 192;
    /// <summary>目标采样率；0 = 自动（取输入最高，最高 48000）。</summary>
    public int SampleRate { get; set; }
    /// <summary>目标声道布局；"" = 自动（取输入最多声道，最高 5.1）。</summary>
    public string ChannelLayout { get; set; } = "";

    /// <summary>输出为 MKV 时默认生成自动章节（每个输入一个章节）。</summary>
    public bool AutoChapters { get; set; } = true;
    /// <summary>自定义章节标题，按合并顺序一一对应；不足时用文件名补齐。</summary>
    public List<string> ChapterTitles { get; set; } = new();

    /// <summary>目标已存在时是否覆盖；false 时任务直接失败，不覆盖任何数据。</summary>
    public bool Overwrite { get; set; } = true;

    /// <summary>合并完成后校验输出（结构/时长/章节/解码完整性）。</summary>
    public bool VerifyOutput { get; set; } = true;

    /// <summary>
    /// 只重编码少数派：当一批输入里有大部分彼此兼容、少数几个不兼容时，
    /// 保留兼容的那批原样，只把少数几个重编码到它们的参数。
    /// 默认开启——同样能成功合并，但保留段是逐比特不变的，耗时与画质都明显更好。
    /// 重编码后会重新探测并核实，核实不通过则自动退回全量重编码，所以开着不影响正确性。
    /// </summary>
    public bool PartialReencode { get; set; } = true;

    /// <summary>
    /// 无损拼接失败时自动改用重新编码重试。
    ///
    /// 默认**关闭**：参数全部一致时 concat 极少失败，一旦失败通常是文件本身有问题，
    /// 自动重编码只会把「明显报错」变成「跑了很久最后仍失败」。
    /// 需要「无论如何都要出片」的场景可以显式打开。
    /// </summary>
    public bool AutoFallbackOnFailure { get; set; }

    /// <summary>
    /// 确认输出完好后删除源文件。
    /// 这是不可逆操作，只有在 <see cref="VerifyOutput"/> 为 true 且全片解码校验通过时才允许执行；
    /// 引擎会在删除前额外做一次「全片解码 + 时长比对」，而不只是开头 1 秒 / 结尾 2 秒的抽样。
    /// </summary>
    public bool DeleteSourceAfterVerify { get; set; }

    /// <summary>
    /// 输入尾部被截断时，自动只保留每个文件真正读得出来的部分，而不是直接失败。
    ///
    /// 为什么默认开启：下载中断、录制崩溃留下的 mp4/mkv 很常见，
    /// 它们的容器头仍然声明完整时长，但数据在中途就断了。
    /// 直接把这种文件交给 concat，产出的输出会「时长不符 + 结尾无法解码」，
    /// 报错看起来像是合并做坏了，实际是素材本身残缺。
    /// 保留可读部分后合并可以正常完成，并明确告知少了多少秒。
    /// 关闭时引擎会在探测阶段就失败并指名哪个文件被截断。
    /// </summary>
    public bool SalvageTruncatedInputs { get; set; } = true;

    /// <summary>临时目录；null 表示每个任务使用独立临时目录（禁止任务间共享）。</summary>
    public string? TempDirectory { get; set; }

    /// <summary>探测与转码子进程超时（秒）。</summary>
    public int ProbeTimeoutSeconds { get; set; } = 60;

    public bool ChaptersEnabled => AutoChapters && NormalizedContainer() == "mkv";

    /// <summary>把用户可能输入的小写/带点容器名规整为 "mkv" / "mp4"。</summary>
    public string NormalizedContainer()
    {
        string c = (Container ?? "").Trim().TrimStart('.').ToLowerInvariant();
        return c.Length == 0 ? "mkv" : c;
    }

    /// <summary>参数自检；返回空列表表示可以执行。</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (Inputs.Count < 1) errors.Add("至少需要一个输入文件");
        if (string.IsNullOrWhiteSpace(OutputPath)) errors.Add("未指定输出路径");
        else
        {
            string full = SafeFullPath(OutputPath);
            foreach (string input in Inputs)
            {
                if (string.IsNullOrWhiteSpace(input)) continue;
                if (string.Equals(SafeFullPath(input), full, StringComparison.OrdinalIgnoreCase))
                    errors.Add("输出路径不能与输入文件相同：" + Path.GetFileName(input));
            }
        }
        if (Inputs.Any(string.IsNullOrWhiteSpace)) errors.Add("输入列表中存在空路径");
        // 删源是不可逆操作，必须同时开启输出校验，否则无法证明输出完好。
        if (DeleteSourceAfterVerify && !VerifyOutput)
            errors.Add("删除源文件必须在开启「校验输出」的前提下进行");
        string container = NormalizedContainer();
        if (container is not ("mkv" or "mp4"))
            errors.Add("不支持的输出容器：" + Container);
        if (TargetWidth < 0 || TargetHeight < 0) errors.Add("目标分辨率不能为负数");
        if (TargetWidth > 0 && TargetHeight == 0 || TargetHeight > 0 && TargetWidth == 0)
            errors.Add("目标分辨率必须同时给出宽和高");
        // yuv420p 要求偶数尺寸；提前告知用户，实际方案里也会自动减 1 消毒并在滤镜链里截断对齐。
        if (TargetWidth > 0 && (TargetWidth & 1) != 0)
            errors.Add("目标宽度必须为偶数（yuv420p 限制），当前：" + TargetWidth.ToString(CultureInfo.InvariantCulture));
        if (TargetHeight > 0 && (TargetHeight & 1) != 0)
            errors.Add("目标高度必须为偶数（yuv420p 限制），当前：" + TargetHeight.ToString(CultureInfo.InvariantCulture));
        // CRF 上限取决于编码器：x264 0-51，x265 0-63；自动模式按 x264 的严格上限校验。
        string codecName = (VideoCodec ?? "").Trim();
        int maxCrf = codecName.Contains("265", StringComparison.OrdinalIgnoreCase) ||
                     codecName.Contains("hevc", StringComparison.OrdinalIgnoreCase) ? 63 : 51;
        if (Crf < 0 || Crf > maxCrf)
            errors.Add(string.Format(CultureInfo.InvariantCulture, "CRF 超出范围（0-{0}，当前编码器：{1}）",
                maxCrf, codecName.Length == 0 ? "自动" : codecName));
        if (AudioBitrateKbps is < 32 or > 1024) errors.Add("音频码率超出范围（32-1024 kbps）");
        if (SampleRate is not (0 or 8000 or 11025 or 16000 or 22050 or 32000 or 44100 or 48000 or 96000))
            errors.Add("不支持的采样率：" + SampleRate.ToString(CultureInfo.InvariantCulture));
        if (codecName.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            errors.Add("暂不支持 nvenc 编码器（CRF/preset 无法正确映射），请改用 libx264 或 libx265");
        // 自动章节仅对 MKV 生效：非 MKV 容器由 ChaptersEnabled 静默关闭，
        // 不作为参数错误（兼容既有 mp4 合并行为）。
        return errors;
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    public VideoMergeOptions Clone()
    {
        var copy = (VideoMergeOptions)MemberwiseClone();
        copy.Inputs = new List<string>(Inputs);
        copy.ChapterTitles = new List<string>(ChapterTitles);
        return copy;
    }
}
