using SubFusion.Core.Chapters;
using SubFusion.Core.Media;

namespace SubFusion.Core.Merge;

/// <summary>任务终态。</summary>
public enum MergeStatus
{
    Completed = 0,
    Failed,
    Cancelled,
}

/// <summary>一次进度回调的快照（不可变，可安全跨线程传递）。</summary>
public sealed record MergeProgress(
    MergeStage Stage,
    double Percent,
    string Message,
    int StepIndex = 0,
    int StepCount = 0,
    double Speed = 0,
    double? Eta = null);

/// <summary>
/// 视频合并任务的完整结果：状态、阶段、进度、输出路径、最终媒体信息、章节、
/// 失败原因与诊断日志。GUI 只需要渲染这个对象，不关心内部实现。
/// </summary>
public sealed class MergeResult
{
    public string TaskId { get; init; } = "";
    public MergeStatus Status { get; init; } = MergeStatus.Failed;
    public MergeStage Stage { get; init; } = MergeStage.Failed;
    public bool Ok => Status == MergeStatus.Completed;

    /// <summary>最终输出文件路径（成功时存在）。</summary>
    public string OutputPath { get; init; } = "";

    /// <summary>输出文件的最终探测结果。</summary>
    public MediaBundle? Output { get; init; }

    /// <summary>输出中实际存在的章节（未启用章节时为空）。</summary>
    public IReadOnlyList<ChapterEntry> Chapters { get; init; } = Array.Empty<ChapterEntry>();

    /// <summary>为什么选择重新编码 / 为什么不能无损拼接。</summary>
    public IReadOnlyList<MergeReason> Compatibility { get; init; } = Array.Empty<MergeReason>();

    public bool VideoCopied { get; init; }
    public bool AudioCopied { get; init; }
    public bool UsedReencode => !VideoCopied;

    /// <summary>成功时的提示信息（例如音轨策略、章节修正）。</summary>
    public IReadOnlyList<string> Notices { get; init; } = Array.Empty<string>();

    /// <summary>失败原因（面向用户）。</summary>
    public string Error { get; init; } = "";

    /// <summary>诊断日志（命令、ffmpeg 输出），失败时保留便于排查。</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public double ElapsedSeconds { get; init; }

    public string SummaryText => Ok
        ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "合并完成：{0}（{1}，{2} 个章节）",
            System.IO.Path.GetFileName(OutputPath), VideoCopied ? "无损拼接" : "统一重新编码", Chapters.Count)
        : (Status == MergeStatus.Cancelled ? "已取消" : "失败：" + Error);
}
