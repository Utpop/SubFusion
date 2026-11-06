using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SubtitleMerger.App.Services;
using SubtitleMerger.App.ViewModels;
using SubtitleMerger.Core;
using SubtitleMerger.Core.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace SubtitleMerger.App.Views;

/// <summary>
/// 主窗口：XAML 视图 + WinUI 原生平台能力实现（IUiServices）。
/// 所有业务状态放在 MainViewModel；此处只负责窗口外壳、拖放、选择同步与原生对话框。
/// </summary>
public sealed partial class MainWindow : Window, IUiServices
{
    private readonly DispatcherQueue _dispatcher;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private bool _allowClose;

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _hwnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        Title = $"{Constants.AppName} v{Constants.AppVersion}";

        // 默认窗口调大：任务表共 9 列，1480 宽下基本不必横向滚动。
        // 受屏幕工作区与最小尺寸 1120x800 双重约束，避免在较小屏幕上超出可视范围。
        RectInt32 workArea = DisplayArea
            .GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Clamp(1480, 1120, Math.Max(1120, workArea.Width - 40));
        int height = Math.Clamp(980, 800, Math.Max(800, workArea.Height - 40));
        _appWindow.Resize(new SizeInt32(width, height));
        _appWindow.Move(new PointInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2));

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            try { _appWindow.SetIcon(iconPath); } catch { /* 图标不可用不影响运行 */ }
        }

        // 原 root.minsize(1120, 800)：WinUI 3 Window 无该属性，用 WM_GETMINMAXINFO 子类化补齐
        NativeWindowHelper.SetMinimumSize(_hwnd, 1120, 800);

        ViewModel = new MainViewModel(this);
        Root.DataContext = ViewModel;
        _appWindow.Closing += OnAppWindowClosing;

        ViewModel.Initialize();
    }

    #region IUiServices —— 原生文件 / 文件夹 / 保存对话框

    private static PickerLocationId SuggestedLocation() => PickerLocationId.DocumentsLibrary;

    private void InitPicker(object picker) => InitializeWithWindow.Initialize(picker, _hwnd);

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title,
                                                            IReadOnlyList<string> extensions,
                                                            bool allowAll = true)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = SuggestedLocation(),
            SettingsIdentifier = "SubtitleMerger.Files",
            CommitButtonText = title,
        };
        InitPicker(picker);
        foreach (string ext in extensions) picker.FileTypeFilter.Add(ext);
        if (allowAll) picker.FileTypeFilter.Add("*");
        IReadOnlyList<Windows.Storage.StorageFile> files = await picker.PickMultipleFilesAsync();
        return files.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var picker = new FolderPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = SuggestedLocation(),
            SettingsIdentifier = "SubtitleMerger.Folders",
            CommitButtonText = title,
        };
        InitPicker(picker);
        picker.FileTypeFilter.Add("*");
        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>
    /// 原 filedialog.askopenfilename 支持 initialdir 任意路径；
    /// WinUI 3 原生 FileOpenPicker 只暴露 PickerLocationId 枚举，无法指定任意目录。
    /// 这里由 Shell 通过 SettingsIdentifier 记忆上次目录，initialDir 仅保留在配置中供其它用途。
    /// </summary>
    public async Task<string?> PickExecutableAsync(string title, string? initialDir)
    {
        _ = initialDir;
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            SettingsIdentifier = "SubtitleMerger.Executable",
            CommitButtonText = title,
        };
        InitPicker(picker);
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add("*");
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickSaveCsvAsync(string suggestedName, string initialDir)
    {
        _ = initialDir;
        var picker = new FileSavePicker
        {
            SuggestedFileName = suggestedName,
            DefaultFileExtension = ".csv",
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SettingsIdentifier = "SubtitleMerger.Report",
        };
        InitPicker(picker);
        picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
        Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private ContentDialog MakeDialog(string message, string primary, string? close)
        => new()
        {
            Title = Constants.AppName,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };

    public async Task ShowInfoAsync(string message)
        => await MakeDialog(message, "确定", null).ShowAsync();

    public async Task ShowErrorAsync(string message)
        => await MakeDialog(message, "确定", null).ShowAsync();

    public async Task<bool> AskYesNoAsync(string message)
    {
        ContentDialogResult result = await MakeDialog(message, "是", "否").ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task<List<SubTrack>?> EditSubtitlesAsync(string video,
                                                          IReadOnlyList<string> candidates,
                                                          IReadOnlyList<string> allSubs,
                                                          IReadOnlyList<SubTrack> current)
    {
        List<string> subExts = Constants.SubExts.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var dialog = new SubtitleTrackDialog(
            video, candidates, allSubs, current,
            () => PickFilesAsync("选择字幕文件", subExts))
        {
            XamlRoot = Root.XamlRoot,
        };
        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? dialog.Result : null;
    }

    public void OpenFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch { /* 原 _open_path 同样吞掉所有异常 */ }
    }

    public void ScrollLogToEnd()
    {
        // 原 txt_log.see("end")：绑定更新后先完成布局，再把视口滚到最底部
        LogScroll.UpdateLayout();
        LogScroll.ChangeView(null, double.MaxValue, null, disableAnimation: true);
    }

    public void RunOnUiThread(Action action)
    {
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    #endregion

    #region 原生拖放（替代原 ctypes WM_DROPFILES 钩子）

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "添加视频 / 字幕 / 文件夹";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
    }

    private void Root_DragEnter(object sender, DragEventArgs e)
        => DropOutline.Stroke = new SolidColorBrush(Color.FromArgb(255, 15, 108, 189));

    private void Root_DragLeave(object sender, DragEventArgs e)
        => DropOutline.Stroke = new SolidColorBrush(Color.FromArgb(255, 156, 184, 216));

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        Root_DragLeave(sender, e);
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            IReadOnlyList<Windows.Storage.IStorageItem> items =
                await e.DataView.GetStorageItemsAsync();
            List<string> paths = items.Select(i => i.Path ?? "")
                .Where(p => p.Length > 0).ToList();
            await ViewModel.AddPathsAsync(paths);
        }
        catch { /* 拖放数据异常时静默忽略，按钮添加仍可用 */ }
    }

    #endregion

    #region 任务表交互

    private void JobsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ViewModel.SetSelection(JobsList.SelectedItems.OfType<JobViewModel>());

    private async void JobsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        => await ViewModel.EditSubtitlesAsync();

    private void JobsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // 与 Tk 的 tree.identify_row + selection_set 等价：右键未选中的行先选中
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node is not null)
        {
            if (node is FrameworkElement fe && fe.DataContext is JobViewModel row)
            {
                if (!JobsList.SelectedItems.Contains(row)) JobsList.SelectedItem = row;
                break;
            }
            node = VisualTreeHelper.GetParent(node);
        }

        var flyout = new MenuFlyout();
        flyout.Items.Add(MakeMenuItem("设置字幕轨…",
            () => { _ = ViewModel.EditSubtitlesAsync(); }));
        flyout.Items.Add(MakeMenuItem("移除选中", () => ViewModel.RemoveSelectedCommand.Execute(null)));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(MakeMenuItem("打开视频所在文件夹",
            () => ViewModel.OpenVideoFolderCommand.Execute(null)));
        flyout.Items.Add(MakeMenuItem("打开输出文件",
            () => ViewModel.OpenOutputFileCommand.Execute(null)));
        flyout.ShowAt(JobsList, e.GetPosition(JobsList));
    }

    private static MenuFlyoutItem MakeMenuItem(string text, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => onClick();
        return item;
    }

    #endregion

    #region 关闭确认

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        bool confirmed = await ViewModel.ConfirmCloseAsync();
        if (!confirmed) return;
        _allowClose = true;
        Close();
    }

    #endregion
}
