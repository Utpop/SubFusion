namespace SubFusion.Core.Models;

/// <summary>一个视频任务（原 core.Job）。状态常量沿用 Python 版中文字符串。</summary>
public sealed class Job
{
    public const string StatePending = "待处理";
    public const string StateRunning = "处理中";
    public const string StateDone = "完成";
    public const string StateFailed = "失败";
    public const string StateSkipped = "跳过";
    public const string StateCancelled = "已取消";

    public string Video { get; set; }
    public List<SubTrack> Subs { get; set; }
    public string Output { get; set; }
    public double? Duration { get; set; }
    public MediaInfo Info { get; set; }
    public string State { get; set; } = StatePending;
    public double Progress { get; set; }
    public string Speed { get; set; } = "";
    public string Eta { get; set; } = "";
    public string Message { get; set; } = "";
    public double? StartedAt { get; set; }
    public double? FinishedAt { get; set; }
    public long? Size { get; set; }

    /// <summary>null=未校验 / true=通过 / false=有异常。</summary>
    public bool? VerifyOk { get; set; }
    public List<string> VerifyIssues { get; set; } = new();

    /// <summary>
    /// 本次确实合并进去的原始字幕路径（不是临时副本）。仅这些文件允许在
    /// 「替换源文件」成功后被删除；由 RunJobAsync 在准备轨道时记录。
    /// </summary>
    public List<string> MergedSubtitleSources { get; set; } = new();


    /// <summary>UI 行标识；原 Python 版在 App.rematch 中动态附加 job.uid。</summary>
    public string Uid { get; set; } = "";
    /// <summary>用户是否手工编辑过字幕轨；原 Python 版在 App 中动态附加。</summary>
    public bool UserEdited { get; set; }

    public string Name => System.IO.Path.GetFileName(Video);

    public Job(string video, IEnumerable<SubTrack>? subs = null, string output = "",
               double? duration = null, MediaInfo? info = null)
    {
        Video = video;
        Subs = subs?.ToList() ?? new List<SubTrack>();
        Output = output;
        Duration = duration;
        Info = info ?? new MediaInfo();
    }
}
