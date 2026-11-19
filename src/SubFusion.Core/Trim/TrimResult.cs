using SubFusion.Core.Media;

namespace SubFusion.Core.Trim;

/// <summary>一次进度回调的快照（不可变，可安全跨线程传递）。</summary>
public sealed record TrimProgress(
    TrimStage Stage,
    double Percent,
    string Message,
    double Speed = 0,
    double? Eta = null);

/// <summary>裁剪任务的终态。</summary>
public enum TrimStatus
{
    Completed = 0,
    Failed,
    Cancelled,
}

/// <summary>
/// 视频裁剪任务的完整结果：状态、阶段、实际切点、输出信息、音画同步结论与诊断日志。
/// GUI 只需要渲染这个对象，不关心内部实现。
/// </summary>
public sealed class TrimResult
{
    public string TaskId { get; init; } = "";
    public TrimStatus Status { get; init; } = TrimStatus.Failed;
    public TrimStage Stage { get; init; } = TrimStage.Failed;
    public bool Ok => Status == TrimStatus.Completed;

    /// <summary>最终输出文件路径（成功时存在）。</summary>
    public string OutputPath { get; init; } = "";

    /// <summary>输出文件的最终探测结果。</summary>
    public MediaBundle? Output { get; init; }

    /// <summary>用户请求的区间。</summary>
    public double RequestedStart { get; init; }
    public double RequestedEnd { get; init; }

    /// <summary>实际执行的区间；Copy 模式下起点可能比请求值更早（吸附到关键帧）。</summary>
    public double ActualStart { get; init; }
    public double ActualEnd { get; init; }

    /// <summary>true 表示起点被向前吸附到了关键帧。</summary>
    public bool Snapped { get; init; }

    /// <summary>输出实际时长（ffprobe 回读）。</summary>
    public double OutputDuration { get; init; }

    public bool VideoCopied { get; init; }
    public bool AudioCopied { get; init; }

    /// <summary>源文件的音画相对偏移（秒）：视频首包 - 音频首包。</summary>
    public double SourceSyncOffset { get; init; }
    /// <summary>输出的音画相对偏移（秒）。</summary>
    public double OutputSyncOffset { get; init; }
    /// <summary>音画同步偏差（秒）：输出偏移与源偏移之差，越小越好。</summary>
    public double SyncDrift { get; init; }
    /// <summary>true 表示已核实音画同步未被破坏。</summary>
    public bool SyncVerified { get; init; }

    /// <summary>成功时的提示信息。</summary>
    public IReadOnlyList<string> Notices { get; init; } = Array.Empty<string>();

    /// <summary>失败原因（面向用户）。</summary>
    public string Error { get; init; } = "";

    /// <summary>诊断日志（命令、ffmpeg 输出、校验结论）。</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public double ElapsedSeconds { get; init; }

    public string SummaryText
    {
        get
        {
            if (!Ok)
                return Status == TrimStatus.Cancelled ? "已取消" : "失败：" + Error;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "裁剪完成：{0}（{1:0.###}s，{2}）",
                System.IO.Path.GetFileName(OutputPath), OutputDuration,
                VideoCopied ? "无损切割" : "精确切割（重编码所选区间）");
        }
    }
}
