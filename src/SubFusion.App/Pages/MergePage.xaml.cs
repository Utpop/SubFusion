using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SubFusion.App.Services;
using SubFusion.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;

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

    /// <summary>拖拽起始行下标；-1 表示当前没有进行中的行内拖拽。</summary>
    private int _dragOriginIndex = -1;

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => ViewModel.SetSelection(Array.Empty<MergeInputViewModel>());

    private void InputsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ViewModel.SetSelection(InputsList.SelectedItems.OfType<MergeInputViewModel>());

    /// <summary>
    /// 行内拖拽开始。ListView 自己负责重排，这里只记录拖拽起点，
    /// 供 DragItemsCompleted 判断是否真的发生了位移。
    /// </summary>
    private void InputsList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragOriginIndex = e.Items.Count > 0
            ? InputsList.Items.IndexOf(e.Items[0])
            : -1;
        // 从行内部拖拽时不要同时被识别为外部文件投放。
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    /// <summary>
    /// 拖拽结束：ListView 已把集合重排，但合并顺序是 Core 的输入，
    /// 顺序变化会影响兼容性判定与章节时间轴，必须重新分析。
    /// </summary>
    private void InputsList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_dragOriginIndex < 0) return;
        _dragOriginIndex = -1;
        ViewModel.OnInputOrderChanged();
    }

    /// <summary>「用这组」按钮：把该组合设为当前合并内容。</summary>
    private void UseGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MergeGroupViewModel group })
            ViewModel.UseGroupCommand.Execute(group);
    }

    /// <summary>从资源管理器拖文件到列表：交给 ViewModel 追加，而不是被内部重排吞掉。</summary>
    private void InputsList_Drop(object sender, DragEventArgs e)
        => CrashGuard.FireAndForget(() => DropFilesAsync(e), "InputsList_Drop");

    private async Task DropFilesAsync(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            IReadOnlyList<Windows.Storage.IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            List<string> paths = items.Select(i => i.Path ?? "").Where(p => p.Length > 0).ToList();
            if (paths.Count == 0) return;
            // 自行处理，避免继续冒泡到外层 Root 造成重复添加。
            e.Handled = true;
            await ViewModel.AddPathsAsync(paths);
        }
        catch { /* 拖放数据异常时静默忽略，按钮添加仍可用 */ }
    }
}
