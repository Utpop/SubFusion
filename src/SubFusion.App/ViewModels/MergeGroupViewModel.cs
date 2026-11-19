using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SubFusion.App.ViewModels;

/// <summary>
/// 「可无损拼接的组合」的一行展示投影。
/// 只承载 Core 的分组结果，不含任何判定逻辑——能不能拼由 CompatibilityAnalyzer 决定。
/// </summary>
public sealed partial class MergeGroupViewModel : ObservableObject
{
    /// <summary>组标题，如 "组合 1"。</summary>
    [ObservableProperty] private string title = "";

    /// <summary>该组共同的参数摘要，说明它们为什么能拼在一起。</summary>
    [ObservableProperty] private string signature = "";

    /// <summary>如 "3 个 · 合计 00:42:15"。</summary>
    [ObservableProperty] private string countText = "";

    /// <summary>该组的处理方式："直接拼接" 或 "重封装后拼接"，两者都不重编码。</summary>
    [ObservableProperty] private string kindText = "";

    /// <summary>
    /// 为什么这一组需要先重封装（为空表示可直接拼接）。
    /// 卡片上只写"重封装后拼接"而不写原因时，用户无法判断这是不是自己想要的。
    /// </summary>
    [ObservableProperty] private string reasonText = "";

    /// <summary>组内文件名，按合并顺序排列。</summary>
    public ObservableCollection<string> Files { get; } = new();

    /// <summary>
    /// 组内文件名的一行展示。Files 变化后必须显式通知：
    /// x:Bind 默认 OneTime，计算属性不会因为集合内容变化而刷新，
    /// 结果就是卡片上只有「组合 N / 3 个」而看不到任何文件名。
    /// </summary>
    public string FileListText => string.Join(" · ", Files);

    public void NotifyFilesChanged() => OnPropertyChanged(nameof(FileListText));
}
