using CommunityToolkit.Mvvm.ComponentModel;
using SubtitleMerger.Core.Models;
using SubtitleMerger.Core.Services;

namespace SubtitleMerger.App.ViewModels;

/// <summary>任务列表的一行（原 Treeview 行的 9 列 + 状态标签）。</summary>
public sealed class JobViewModel : ObservableObject
{
    public Job Model { get; }

    public JobViewModel(Job model) => Model = model;

    public string Uid => Model.Uid;
    public string Name => Model.Name;
    public string DurationText => Model.Duration.HasValue ? Formatting.FormatDuration(Model.Duration) : "-";
    public string Resolution => Model.Info.Width > 0 && Model.Info.Height > 0
        ? $"{Model.Info.Width}x{Model.Info.Height}" : "-";
    public string SubsText => Model.Subs.Count > 0
        ? string.Join("、", Model.Subs.Select(t => t.Label())) : "（无）";
    public string Speed => Model.Speed;
    public string Eta => Model.Eta;
    public string OutputName => string.IsNullOrEmpty(Model.Output)
        ? "" : Path.GetFileName(Model.Output);

    public string StateText
    {
        get
        {
            string state = Model.State;
            if (state == Job.StateRunning)
                return $"处理中 {Formatting.F0(Model.Progress)}%";
            if (state == Job.StateDone)
            {
                if (Model.VerifyOk == false) return "完成（校验异常）";
                if (Model.VerifyOk == true) return "完成（已校验）";
            }
            else if (state == Job.StateFailed && !string.IsNullOrEmpty(Model.Message))
                return "失败：" + Formatting.Truncate(Model.Message, 40);
            else if (state == Job.StateSkipped && !string.IsNullOrEmpty(Model.Message))
                return "跳过：" + Formatting.Truncate(Model.Message, 30);
            return state;
        }
    }

    public string ProgressText => Model.Progress != 0 ? $"{Formatting.F1(Model.Progress)}%" : "";

    public string StateTag => Model.State switch
    {
        Job.StateDone => Model.VerifyOk == false ? "warn" : "done",
        Job.StateRunning => "running",
        Job.StateFailed => "failed",
        Job.StateSkipped or Job.StateCancelled => "skipped",
        _ => "pending",
    };

    /// <summary>模型被后台任务修改后，通知所有绑定属性刷新。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(Resolution));
        OnPropertyChanged(nameof(SubsText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(Speed));
        OnPropertyChanged(nameof(Eta));
        OnPropertyChanged(nameof(OutputName));
        OnPropertyChanged(nameof(StateTag));
    }
}
