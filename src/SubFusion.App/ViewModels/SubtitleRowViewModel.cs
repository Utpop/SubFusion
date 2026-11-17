using CommunityToolkit.Mvvm.ComponentModel;
using SubFusion.Core.Models;
using SubFusion.Core.Services;

namespace SubFusion.App.ViewModels;

/// <summary>
/// 字幕合并工作区里的一行：一个视频 + 它匹配到的字幕轨 + 处理状态。
/// 只做展示投影，不持有业务逻辑；<see cref="Model"/> 是 Core 的子任务对象。
/// </summary>
public sealed partial class SubtitleRowViewModel : ObservableObject
{
    public SubtitleRowViewModel(Job model) => Model = model;

    public Job Model { get; }

    public string Uid => Model.Uid;
    public string Name => Model.Name;
    public string FullPath => Model.Video;

    public string DurationText => Model.Duration.HasValue
        ? Formatting.FormatDuration(Model.Duration) : "-";

    public string Resolution => Model.Info.Width > 0 && Model.Info.Height > 0
        ? $"{Model.Info.Width}x{Model.Info.Height}" : "-";

    public string SubsText => Model.Subs.Count > 0
        ? string.Join("、", Model.Subs.Select(t => t.Label())) : "（无字幕）";

    public int SubCount => Model.Subs.Count;
    public bool HasSubs => Model.Subs.Count > 0;

    public string Speed => Model.Speed;
    public string Eta => Model.Eta;

    public string OutputName => string.IsNullOrEmpty(Model.Output)
        ? "" : Path.GetFileName(Model.Output);

    public string StateText
    {
        get
        {
            string state = Model.State;
            if (state == Job.StateRunning) return $"处理中 {Formatting.F0(Model.Progress)}%";
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
    public double Progress => Model.Progress;

    /// <summary>done / warn / running / failed / skipped / pending —— 供状态色转换器使用。</summary>
    public string StateTag => Model.State switch
    {
        Job.StateDone => Model.VerifyOk == false ? "warn" : "done",
        Job.StateRunning => "running",
        Job.StateFailed => "failed",
        Job.StateSkipped or Job.StateCancelled => "skipped",
        _ => "pending",
    };

    public string StateGlyph => StateTag switch
    {
        "done" => "\uE73E",
        "warn" => "\uE7BA",
        "running" => "\uE895",
        "failed" => "\uEA39",
        "skipped" => "\uE711",
        _ => "\uE823",
    };

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(FullPath));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(Resolution));
        OnPropertyChanged(nameof(SubsText));
        OnPropertyChanged(nameof(SubCount));
        OnPropertyChanged(nameof(HasSubs));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(Speed));
        OnPropertyChanged(nameof(Eta));
        OnPropertyChanged(nameof(OutputName));
        OnPropertyChanged(nameof(StateTag));
        OnPropertyChanged(nameof(StateGlyph));
    }
}
