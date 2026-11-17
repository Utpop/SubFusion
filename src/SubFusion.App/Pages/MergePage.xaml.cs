using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SubFusion.App.ViewModels;

namespace SubFusion.App.Pages;

/// <summary>
/// 视频合并页。Code-behind 只做列表选中同步与生命周期收尾；
/// 合并策略、章节时间轴、ffmpeg 命令全部由 Core 决定，这里只展示。
/// </summary>
public sealed partial class MergePage : Page
{
    public VideoMergeViewModel ViewModel { get; }

    public MergePage()
    {
        ViewModel = App.Shell?.Merge ?? throw new InvalidOperationException("外壳尚未初始化");
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => ViewModel.SetSelection(Array.Empty<MergeInputViewModel>());

    private void InputsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ViewModel.SetSelection(InputsList.SelectedItems.OfType<MergeInputViewModel>());
}
