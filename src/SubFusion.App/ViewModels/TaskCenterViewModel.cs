using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubFusion.App.Services;

namespace SubFusion.App.ViewModels;

/// <summary>任务中心的筛选条件（只筛选真实存在的状态，不虚构"暂停"等）。</summary>
public enum TaskFilter
{
    All,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// 任务中心页。数据全部来自 <see cref="TaskCenter"/>；
/// 页内只做筛选、选择与对真实状态可执行的操作（取消 / 清理 / 打开输出目录 / 查看诊断）。
/// </summary>
public sealed partial class TaskCenterViewModel : ObservableObject
{
    private readonly TaskCenter _tasks;
    private readonly IUiServices _ui;

    public TaskCenterViewModel(TaskCenter tasks, IUiServices ui)
    {
        _tasks = tasks;
        _ui = ui;
        _tasks.Changed += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<TaskRecord> Filtered { get; } = new();

    [ObservableProperty] private TaskFilter filter = TaskFilter.All;
    [ObservableProperty] private TaskRecord? selected;
    [ObservableProperty] private bool hasSelection;
    [ObservableProperty] private bool showEmptyState = true;
    [ObservableProperty] private string emptyText = "还没有任务";
    [ObservableProperty] private string countText = "";
    [ObservableProperty] private bool hasRunning;
    [ObservableProperty] private int runningCount;
    [ObservableProperty] private int completedCount;
    [ObservableProperty] private int failedCount;

    /// <summary>
    /// 下拉选项。直接绑枚举会让下拉显示 All / Running 之类的英文名，
    /// 因此每个值配一个中文标签，由 <see cref="FilterOptions"/> 提供给界面。
    /// </summary>
    public sealed record FilterOption(TaskFilter Value, string Label);

    public IReadOnlyList<FilterOption> FilterOptions { get; } = new[]
    {
        new FilterOption(TaskFilter.All, "全部"),
        new FilterOption(TaskFilter.Running, "运行中"),
        new FilterOption(TaskFilter.Completed, "已完成"),
        new FilterOption(TaskFilter.Failed, "失败"),
        new FilterOption(TaskFilter.Cancelled, "已取消"),
    };

    public void Refresh()
    {
        RunningCount = _tasks.RunningCount;
        CompletedCount = _tasks.CompletedCount;
        FailedCount = _tasks.FailedCount;
        HasRunning = RunningCount > 0;

        IEnumerable<TaskRecord> source = _tasks.Tasks;
        source = Filter switch
        {
            TaskFilter.Running => source.Where(t => t.State == TaskState.Running),
            TaskFilter.Completed => source.Where(t => t.State == TaskState.Completed),
            TaskFilter.Failed => source.Where(t => t.State == TaskState.Failed),
            TaskFilter.Cancelled => source.Where(t => t.State == TaskState.Cancelled),
            _ => source,
        };

        var wanted = source.ToList();
        // 增量同步，避免每次状态变化都整体重建列表（会丢失选择与滚动位置）
        for (int i = Filtered.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Filtered[i])) Filtered.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            int existing = Filtered.IndexOf(wanted[i]);
            if (existing < 0) Filtered.Insert(i, wanted[i]);
            else if (existing != i) Filtered.Move(existing, i);
        }

        ShowEmptyState = Filtered.Count == 0;
        EmptyText = Filter switch
        {
            TaskFilter.Running => "当前没有运行中的任务",
            TaskFilter.Completed => "还没有完成的任务",
            TaskFilter.Failed => "没有失败的任务",
            TaskFilter.Cancelled => "没有已取消的任务",
            _ => "还没有任务。在「字幕合并」或「视频合并」页开始一个任务后，这里会显示它的实时状态。",
        };
        CountText = $"{Filtered.Count} 条";
        if (Selected is not null && !Filtered.Contains(Selected)) Selected = null;
        HasSelection = Selected is not null;
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedInput));
        OnPropertyChanged(nameof(SelectedMessage));
        OnPropertyChanged(nameof(SelectedDetail));
        OnPropertyChanged(nameof(SelectedOutput));
        OnPropertyChanged(nameof(SelectedKind));
        OnPropertyChanged(nameof(SelectedState));
        OnPropertyChanged(nameof(SelectedElapsed));
        OnPropertyChanged(nameof(SelectedStateBrushKey));
        OnPropertyChanged(nameof(SelectedLog));
        CancelSelectedCommand.NotifyCanExecuteChanged();
        OpenOutputCommand.NotifyCanExecuteChanged();
    }

    partial void OnFilterChanged(TaskFilter value) => Refresh();

    partial void OnSelectedChanged(TaskRecord? value)
    {
        HasSelection = value is not null;

        // 详情区的绑定目标是 Selected 的属性，null 传播到普通 Binding 只是取不到值，
        // 但为保险起见把详情投影成一组可绑定字符串，未选中时为空串而不是 null。
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedInput));
        OnPropertyChanged(nameof(SelectedMessage));
        OnPropertyChanged(nameof(SelectedDetail));
        OnPropertyChanged(nameof(SelectedOutput));
        OnPropertyChanged(nameof(SelectedKind));
        OnPropertyChanged(nameof(SelectedState));
        OnPropertyChanged(nameof(SelectedElapsed));
        OnPropertyChanged(nameof(SelectedStateBrushKey));
        OnPropertyChanged(nameof(SelectedLog));

        CancelSelectedCommand.NotifyCanExecuteChanged();
        OpenOutputCommand.NotifyCanExecuteChanged();
    }

    /// <summary>详情区展示用的投影（未选中时全部为空，XAML 不需要处理 null）。</summary>
    public string SelectedTitle => Selected?.Title ?? "";
    public string SelectedInput => Selected?.InputSummary ?? "";
    public string SelectedMessage => Selected?.Message ?? "";
    public string SelectedDetail => Selected?.Detail ?? "";
    public string SelectedOutput => Selected?.OutputPath ?? "";
    public string SelectedKind => Selected?.KindText ?? "";
    public string SelectedState => Selected?.StateText ?? "";
    public string SelectedElapsed => Selected?.ElapsedText ?? "";
    public object SelectedStateBrushKey => Selected?.State ?? TaskState.Queued;
    public System.Collections.ObjectModel.ObservableCollection<string>? SelectedLog => Selected?.Log;

    public void SetSelection(IEnumerable<TaskRecord> selection)
        => Selected = selection.FirstOrDefault();

    private bool CanActOnSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void CancelSelected()
    {
        if (Selected is null || !Selected.CanCancel)
        {
            _ui.ShowInfoAsync("该任务已经结束，无法取消。");
            return;
        }
        _tasks.Cancel(Selected);
    }

    [RelayCommand]
    private void CancelAll() => _tasks.CancelAll();

    [RelayCommand]
    private void ClearFinished() => _tasks.ClearFinished();

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void OpenOutput()
    {
        if (Selected is null) return;
        string path = Selected.OutputPath;
        if (string.IsNullOrEmpty(path))
        {
            _ui.ShowInfoAsync("该任务没有输出路径。");
            return;
        }
        string folder = Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? "");
        if (folder.Length > 0 && Directory.Exists(folder)) _ui.OpenFolder(folder);
        else _ui.ShowInfoAsync("输出目录还不存在。");
    }

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        if (Selected is null) return;
        string text = string.Join(Environment.NewLine, Selected.Log);
        if (text.Length == 0)
        {
            await _ui.ShowInfoAsync("该任务没有诊断日志。");
            return;
        }
        await _ui.SetClipboardTextAsync(text);
        await _ui.ShowInfoAsync("诊断日志已复制到剪贴板。");
    }
}
