using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using SubFusion.App.Services;
using SubFusion.App.ViewModels;

namespace SubFusion.App.Pages;

/// <summary>
/// 视频裁剪页。Code-behind 只做三件事：
/// 拖放接入、把拖动结果同步给 ViewModel、把 ViewModel 的区间同步回拖动条。
/// 切点吸附、旋转策略、音画同步校验全部由 Core 决定，这里只展示。
/// </summary>
public sealed partial class TrimPage : Page
{
    public VideoTrimViewModel ViewModel { get; }

    /// <summary>
    /// 正在由代码回写输入框。
    /// 防止「拖动 → 写输入框 → TextChanged → 又写拖动条 → 又触发拖动」的回环。
    /// </summary>
    private bool _syncing;

    public TrimPage()
    {
        ViewModel = App.Shell?.Trim ?? throw new InvalidOperationException("外壳尚未初始化");
        InitializeComponent();
        Loaded += OnLoaded;

        Timeline.RangeChanged += OnRangeChanged;
        Timeline.RangeCommitted += OnRangeCommitted;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SyncTimelineFromViewModel();

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

    /// <summary>
    /// 输入框或文件变化后，把区间回写到拖动条。
    /// 输入框里可能是半截内容（例如正在输入 "00:00:1"），解析失败时不覆盖拖动条，
    /// 否则用户每敲一个字拖动条都会跳。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VideoTrimViewModel.StartInput) or nameof(VideoTrimViewModel.EndInput))
            SyncTimelineFromViewModel();
        else if (e.PropertyName is nameof(VideoTrimViewModel.KeyframeTimes) or
                                    nameof(VideoTrimViewModel.SourceDuration) or
                                    nameof(VideoTrimViewModel.SnapToKeyframes))
            SyncTimelineFromViewModel();
    }

    private void SyncTimelineFromViewModel()
    {
        if (_syncing) return;
        double duration = ViewModel.SourceDuration;
        if (duration <= 0)
        {
            Timeline.SetRange(0, 0);
            return;
        }

        Timeline.SnapEnabled = ViewModel.SnapToKeyframes;
        Timeline.Configure(duration, ViewModel.KeyframeTimes);

        if (ViewModel.TryGetRange(out double start, out double end) && end > start)
            Timeline.SetRange(start, end);
        else
            Timeline.SetRange(0, duration);
    }

    /// <summary>拖动中：连续回写输入框，预计结果与吸附说明因此实时更新。</summary>
    private void OnRangeChanged(object? sender, (double Start, double End) e)
    {
        if (_syncing) return;
        _syncing = true;
        try { ViewModel.ApplyRangeFromTimeline(e.Start, e.End); }
        finally { _syncing = false; }
    }

    /// <summary>松手：再同步一次，确保最终值（含吸附）与输入框完全一致。</summary>
    private void OnRangeCommitted(object? sender, (double Start, double End) e)
    {
        _syncing = true;
        try { ViewModel.ApplyRangeFromTimeline(e.Start, e.End); }
        finally { _syncing = false; }
    }

    /// <summary>手输时间后立刻把拖动条挪到对应位置（输入合法时）。</summary>
    private void TimeInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        double duration = ViewModel.SourceDuration;
        if (duration <= 0) return;
        if (!ViewModel.TryGetRange(out double start, out double end) || end <= start) return;

        _syncing = true;
        try { Timeline.SetRange(start, end); }
        finally { _syncing = false; }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectAllRange();
        SyncTimelineFromViewModel();
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
        e.Handled = true;
    }

    private void Page_Drop(object sender, DragEventArgs e)
        => CrashGuard.FireAndForget(() => DropAsync(e), "TrimPage_Drop");

    private async Task DropAsync(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            IReadOnlyList<Windows.Storage.IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            List<string> paths = items.Select(i => i.Path ?? "").Where(p => p.Length > 0).ToList();
            if (paths.Count == 0) return;
            e.Handled = true;
            ViewModel.AcceptDroppedFile(paths);
        }
        catch { /* 拖放数据异常时静默忽略，按钮选择仍可用 */ }
    }
}
