using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SubFusion.App.Services;

namespace SubFusion.App.Services;

/// <summary>任务类别。</summary>
public enum TaskKind
{
    SubtitleMerge,
    VideoMerge,
    VideoTrim,
}

/// <summary>任务的五个真实状态（与 Core 的报告一致，不虚构暂停/续传）。</summary>
public enum TaskState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// 任务中心里的一条记录。它是 UI 侧的状态快照，不是 Core 的模型：
/// 字幕批处理一次任务对应多行 <c>Job</c>，视频合并一次任务对应一个 <c>MergeResult</c>，
/// 两者都用同一个 <see cref="TaskRecord"/> 呈现，因此任务中心可以统一展示、取消与查看诊断。
/// </summary>
public sealed partial class TaskRecord : ObservableObject
{
    public TaskRecord(string id, TaskKind kind, string title, string inputSummary, string outputPath)
    {
        Id = id;
        Kind = kind;
        Title = title;
        InputSummary = inputSummary;
        OutputPath = outputPath;
        CreatedAt = DateTimeOffset.Now;
        Cancellation = new CancellationTokenSource();
    }

    public string Id { get; }
    public TaskKind Kind { get; }
    public string Title { get; private set; }
    public string InputSummary { get; private set; }
    public string OutputPath { get; private set; }
    public DateTimeOffset CreatedAt { get; }

    /// <summary>取消令牌；任务结束后由 <see cref="TaskCenter"/> 负责释放。</summary>
    public CancellationTokenSource Cancellation { get; }

    [ObservableProperty] private TaskState state = TaskState.Queued;
    [ObservableProperty] private string stage = "等待中";
    [ObservableProperty] private double progress;
    [ObservableProperty] private string message = "";
    [ObservableProperty] private string detail = "";
    [ObservableProperty] private string speed = "";
    [ObservableProperty] private string eta = "";
    [ObservableProperty] private DateTimeOffset? finishedAt;
    [ObservableProperty] private bool verifyFailed;

    /// <summary>诊断日志（ffmpeg 命令、错误输出）。受 <see cref="TaskCenter.MaxLogLines"/> 限制。</summary>
    public ObservableCollection<string> Log { get; } = new();

    /// <summary>结构化结果：字幕任务为 BatchSummary，视频任务为 MergeResult。</summary>
    [ObservableProperty] private object? result;

    public string KindText => Kind switch
    {
        TaskKind.SubtitleMerge => "字幕合并",
        TaskKind.VideoMerge => "视频合并",
        _ => "视频裁剪",
    };

    public string KindGlyph => Kind switch
    {
        TaskKind.SubtitleMerge => "\uE8AC",
        TaskKind.VideoMerge => "\uE714",
        _ => "\uE8B9",
    };

    public string StateText => State switch
    {
        TaskState.Queued => "等待",
        TaskState.Running => "运行中",
        TaskState.Completed => VerifyFailed ? "完成（有告警）" : "已完成",
        TaskState.Failed => "失败",
        _ => "已取消",
    };

    public string StateGlyph => State switch
    {
        TaskState.Queued => "\uE823",
        TaskState.Running => "\uE895",
        TaskState.Completed => VerifyFailed ? "\uE7BA" : "\uE73E",
        TaskState.Failed => "\uEA39",
        _ => "\uE711",
    };

    public bool CanCancel => State is TaskState.Queued or TaskState.Running;

    public bool IsFinished => State is TaskState.Completed or TaskState.Failed or TaskState.Cancelled;

    public string FinishedText => FinishedAt is { } f ? f.ToString("HH:mm:ss") : "-";

    public string ElapsedText
    {
        get
        {
            DateTimeOffset end = FinishedAt ?? DateTimeOffset.Now;
            TimeSpan span = end - CreatedAt;
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes}:{span.Seconds:00}";
        }
    }

    public string ProgressText => State switch
    {
        TaskState.Queued => "—",
        TaskState.Completed => "100%",
        _ => $"{Progress:0.#}%",
    };

    public void RefreshDerived()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateGlyph));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(FinishedText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(KindGlyph));
    }

}

/// <summary>
/// 任务中心：唯一的任务状态来源。工作台、任务中心页、以及两个业务页都从这里读，
/// 因此不存在「页面之间各存一份状态」的重复实现。
///
/// 线程约定：<see cref="Add"/> 与所有 <c>Update*</c> 都可在任意线程调用，
/// 内部统一投递到 UI 线程后再改集合，保证 ObservableCollection 只在 UI 线程被修改。
/// </summary>
public sealed class TaskCenter : ObservableObject
{
    public const int MaxLogLines = 2000;
    private const int MaxRecords = 200;

    private readonly IUiServices _ui;

    public TaskCenter(IUiServices ui) => _ui = ui;

    public ObservableCollection<TaskRecord> Tasks { get; } = new();

    /// <summary>任一记录的状态/进度变化后触发；页面用它刷新统计与派生列表。</summary>
    public event EventHandler? Changed;

    public int RunningCount => Tasks.Count(t => t.State == TaskState.Running);
    public int CompletedCount => Tasks.Count(t => t.State == TaskState.Completed);
    public int FailedCount => Tasks.Count(t => t.State == TaskState.Failed);
    public int TotalCount => Tasks.Count;

    public TaskRecord? Current => Tasks.FirstOrDefault(t => t.State == TaskState.Running)
                                  ?? Tasks.FirstOrDefault(t => t.State == TaskState.Queued);

    /// <summary>新建任务记录（可在任意线程调用，返回的记录已加入集合）。</summary>
    public TaskRecord Add(TaskKind kind, string title, string inputSummary, string outputPath)
    {
        var record = new TaskRecord("t" + Guid.NewGuid().ToString("N")[..8], kind, title,
                                    inputSummary, outputPath);
        _ui.RunOnUiThread(() =>
        {
            Tasks.Insert(0, record);
            Trim();
            record.RefreshDerived();
            RaiseChanged();
        });
        return record;
    }

    public void Update(TaskRecord record, Action<TaskRecord> mutate)
        => _ui.RunOnUiThread(() =>
        {
            TaskState before = record.State;
            mutate(record);
            record.RefreshDerived();
            // 进度/文案变化由记录自身的 ObservableProperty 通知处理，任务中心聚合统计
            // 只在真正改变任务状态时重算，避免高频进度刷新筛选列表与统计。
            if (record.State != before) RaiseChanged();
        });

    /// <summary>追加诊断日志；超过上限后丢弃最早的行，避免长任务把内存吃满。</summary>
    public void AppendLog(TaskRecord record, string line)
        => _ui.RunOnUiThread(() =>
        {
            record.Log.Add(line);
            while (record.Log.Count > MaxLogLines) record.Log.RemoveAt(0);
        });

    public void Finish(TaskRecord record, TaskState state, string message)
        => _ui.RunOnUiThread(() =>
        {
            record.State = state;
            record.Message = message;
            record.FinishedAt = DateTimeOffset.Now;
            if (state == TaskState.Completed) record.Progress = 100;
            record.RefreshDerived();
            RaiseChanged();
        });

    /// <summary>取消任务（不支持的场景由调用方在此之前拦截）。</summary>
    public void Cancel(TaskRecord record)
        => _ui.RunOnUiThread(() =>
        {
            if (!record.CanCancel) return;
            try { record.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* 任务刚好收尾 */ }
            record.Message = "正在取消…";
            record.RefreshDerived();
            RaiseChanged();
        });

    public void CancelAll()
    {
        foreach (TaskRecord record in Tasks.Where(t => t.CanCancel).ToList()) Cancel(record);
    }

    public void ClearFinished()
        => _ui.RunOnUiThread(() =>
        {
            foreach (TaskRecord record in Tasks.Where(t => t.IsFinished).ToList())
            {
                Tasks.Remove(record);
                record.Cancellation.Dispose();
            }
            RaiseChanged();
        });

    private void Trim()
    {
        bool removed = false;
        while (Tasks.Count > MaxRecords)
        {
            TaskRecord oldest = Tasks[^1];
            if (!oldest.IsFinished) break;      // 运行中的任务不淘汰
            Tasks.RemoveAt(Tasks.Count - 1);
            oldest.Cancellation.Dispose();
            removed = true;
        }
        // 淘汰会让「当前选中项」失效，必须通知出去：否则详情区仍绑着一条已释放取消令牌的
        // 记录，用户点「取消」只会命中 ObjectDisposedException 的静默分支，
        // 界面停在「正在取消…」而永远不生效。
        if (removed) RaiseChanged();
    }

    private DateTime _lastChangedRaise = DateTime.MinValue;

    internal void RaiseChanged(bool throttle = false)
    {
        if (throttle && (DateTime.UtcNow - _lastChangedRaise).TotalMilliseconds < 200) return;
        _lastChangedRaise = DateTime.UtcNow;
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(Current));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
