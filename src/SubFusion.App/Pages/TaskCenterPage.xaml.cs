using Microsoft.UI.Xaml.Controls;
using SubFusion.App.Services;
using SubFusion.App.ViewModels;

namespace SubFusion.App.Pages;

/// <summary>任务中心：统一展示字幕合并与视频合并的真实任务状态。</summary>
public sealed partial class TaskCenterPage : Page
{
    public TaskCenterViewModel ViewModel { get; }

    public TaskCenterPage()
    {
        ViewModel = App.Shell?.TaskCenterPage ?? throw new InvalidOperationException("外壳尚未初始化");
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Refresh();
    }

    private void TasksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ViewModel.SetSelection(TasksList.SelectedItems.OfType<TaskRecord>());

    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterBox.SelectedItem is TaskFilter filter) ViewModel.Filter = filter;
    }
}
