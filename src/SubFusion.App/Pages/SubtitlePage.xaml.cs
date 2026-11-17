using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SubFusion.App.Services;
using SubFusion.App.ViewModels;
using SubFusion.App.Views;

namespace SubFusion.App.Pages;

/// <summary>
/// 字幕合并页。Code-behind 只处理 WinUI 无法用 MVVM 表达的部分：
/// 列表选中同步、右键菜单定位、日志滚动注册。
/// </summary>
public sealed partial class SubtitlePage : Page
{
    public SubtitleViewModel ViewModel { get; }

    public SubtitlePage()
    {
        ViewModel = App.Shell?.Subtitle ?? throw new InvalidOperationException("外壳尚未初始化");
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.CurrentWindow is MainWindow window) window.RegisterLogScroll(LogScroll);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 页面离开时释放选中状态，避免 ViewModel 继续持有已移除的行。
        ViewModel.SetSelection(Array.Empty<SubtitleRowViewModel>());
    }

    private void RowsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ViewModel.SetSelection(RowsList.SelectedItems.OfType<SubtitleRowViewModel>());

    private void RowsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.EditSubtitlesCommand.CanExecute(null))
            ViewModel.EditSubtitlesCommand.Execute(null);
    }

    private void RowsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // 右键未选中的行先选中它（与资源管理器一致）
        if (e.OriginalSource is DependencyObject node)
            while (node is not null)
            {
                if (node is FrameworkElement element && element.DataContext is SubtitleRowViewModel row)
                {
                    if (!RowsList.SelectedItems.Contains(row)) RowsList.SelectedItem = row;
                    break;
                }
                node = VisualTreeHelper.GetParent(node);
            }

        if (RowsList.ContextFlyout is MenuFlyout flyout)
            flyout.ShowAt(RowsList, e.GetPosition(RowsList));
    }
}
