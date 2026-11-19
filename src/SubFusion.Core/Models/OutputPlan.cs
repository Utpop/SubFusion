namespace SubFusion.Core.Models;

/// <summary>build_command 生成的输出结构预期，供 verify_output 核对（原 plan dict）。</summary>
public sealed class OutputPlan
{
    public string Mode { get; set; } = "soft";
    public string Container { get; set; } = "mkv";
    public int ExpectedVideo { get; set; } = 1;
    public int ExpectedAudio { get; set; }
    /// <summary>期望的字幕轨数量（Python v1.1 语义，冻结基准逐项比对）。</summary>
    public int ExpectedSubs { get; set; }

    /// <summary>
    /// true 表示字幕轨来自「-map 0:s? 原样复制源字幕」（硬字幕但没有可烧录的轨）。
    /// 此时 <see cref="ExpectedSubs"/> 仍是 0（「没有新字幕轨」），但输出的实际字幕数
    /// 等于源文件的字幕数，校验必须换一个口径，否则必然误报「字幕轨数量不符」。
    /// </summary>
    public bool SubsFromSource { get; set; }

    /// <summary>源字幕数量是否可信（ffprobe 探测成功）。false 时无法核对数量。</summary>
    public bool SubsSourceKnown { get; set; } = true;
    public int BurnedSubs { get; set; }
    public string Vcodec { get; set; } = "copy";
    public string Acodec { get; set; } = "copy";
}

/// <summary>ffmpeg 能力探测结果（原 ffmpeg_capabilities 返回的 dict）。</summary>
public sealed class FfmpegCapabilities
{
    public bool Libass { get; set; }
    public bool X264 { get; set; }
    public bool X265 { get; set; }
    public bool Nvenc { get; set; }
}

/// <summary>输出校验结果（原 verify_output 的三元组）。</summary>
public sealed class VerifyResult
{
    /// <summary>是否真正执行过校验（ffprobe 不可用而跳过时为 false）。</summary>
    public bool Verified { get; set; }

    public bool Ok { get; set; }
    public List<string> Issues { get; set; } = new();
    public MediaInfo Detail { get; set; } = new();
}
