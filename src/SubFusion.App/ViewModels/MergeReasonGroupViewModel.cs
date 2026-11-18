using System.Collections.ObjectModel;
using SubFusion.Core.Merge;

namespace SubFusion.App.ViewModels;

/// <summary>
/// 「不满足无损拼接的参数」按修复代价分组后的展示投影。
///
/// 平铺一长串差异会让用户看不出重点：真正决定"有没有画质损失"的，
/// 是其中有没有 Reencode 档的差异，而不是差异有几条。
/// 这里只做分组呈现，判定完全来自 Core 的 <see cref="MergeReasonGroup"/>。
/// </summary>
public sealed class MergeReasonGroupViewModel
{
    public MergeReasonGroupViewModel(MergeReasonGroup group)
    {
        Title = group.Title;
        Intro = group.Intro;
        IsLossy = group.IsLossy;
        foreach (string detail in group.Details) Details.Add(detail);
        CountText = group.Details.Count + " 项";
    }

    /// <summary>分组标题："必须重新编码" / "可用无损重封装解决"。</summary>
    public string Title { get; }

    /// <summary>该组的说明文字（会重新编码 / 不会重新编码）。</summary>
    public string Intro { get; }

    /// <summary>true 表示这一组会导致视频重新编码，界面应使用警示色。</summary>
    public bool IsLossy { get; }

    public string CountText { get; }

    public ObservableCollection<string> Details { get; } = new();
}
