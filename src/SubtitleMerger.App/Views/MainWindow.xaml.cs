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
using Windows.Foundation;
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

    /// <summary>主视图模型。在构造函数末尾赋值；此前的原生回调需用 ViewModelOrNull 访问。</summary>
    public MainViewModel ViewModel { get; }

    private MainViewModel? ViewModelOrNull => ViewModel;

    public MainWindow()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _hwnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // 程序默认以管理员权限运行（app.manifest 的 requireAdministrator）。
        // 提升后 UIPI 会丢弃普通权限资源管理器发来的拖放消息，导致拖放完全没反应；
        // 这里先放行相关消息，再挂上 WM_DROPFILES 兜底，两条路都通。
        DragDropUnblocker.Apply(_hwnd);

        Title = $"{Constants.AppName} v{Constants.AppVersion}";

        // 1.1：按工作区比例定初始尺寸，而不是写死 1480x980。
        // 1080p + 125% 缩放时工作区只有约 1600x816 DIP，旧逻辑会先撑到工作区再被裁，
        // 底部按钮栏随之跑出屏幕。现在默认取工作区的 92%，并保证 9 列任务表不压缩
        // （最小 1000，列总宽 1176 时任务表内部横向滚动，而不是把整页内容挤掉）。
        RectInt32 workArea = DisplayArea
            .GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Clamp((int)(workArea.Width * 0.92), 1000, workArea.Width - 24);
        // 高度留足 88 px 的任务栏/边框余量：否则窗口贴满工作区，内容被系统边距挤出一条竖向滚动条
        int height = Math.Clamp((int)(workArea.Height * 0.92), 640, workArea.Height - 88);
        width = Math.Min(width, Math.Max(1000, workArea.Width - 24));
        height = Math.Min(height, Math.Max(640, workArea.Height - 88));
        _appWindow.Resize(new SizeInt32(width, height));
        _appWindow.Move(new PointInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2)));

        // 工作区本身就不大（例如 1080p + 150% 缩放）时直接最大化，保证所有区域都可见
        if (workArea.Height < 900 || workArea.Width < 1280)
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();
        }

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            try { _appWindow.SetIcon(iconPath); } catch { /* 图标不可用不影响运行 */ }
        }

        // 原 root.minsize(1120, 800)：WinUI 3 Window 无该属性，用 WM_GETMINMAXINFO 子类化补齐。
        // 1.1：最小尺寸改为不超过工作区（否则小屏上窗口被强制放大，底部按钮栏直接出屏），
        // 且高度下限放到 640 DIP —— 这一档下任务表与设置页都能靠内部滚动完整操作。
        NativeWindowHelper.SetMinimumSize(_hwnd,
            (int)Math.Min(1120, Math.Max(900, workArea.Width - 24)),
            (int)Math.Min(800, Math.Max(600, workArea.Height - 24)));

        // 拖放兜底：管理员运行时 OLE 拖放可能被 UIPI 拦掉，用经典 WM_DROPFILES 补一条通路。
        // 回调里再取 ViewModel（此处构造顺序上它还没赋值）。
        NativeWindowHelper.SetDropHandler(_hwnd, paths =>
        {
            MainViewModel? vm = ViewModelOrNull;
            if (vm is null) return;
            CrashGuard.FireAndForget(() => vm.AddPathsAsync(paths), "DropFiles");
        });

        ViewModel = new MainViewModel(this);
        Root.DataContext = ViewModel;
        _appWindow.Closing += OnAppWindowClosing;

        // 布局自检（--layout-report，可选再加 --resize WxH 指定窗口像素尺寸）：
        // 逐个切换设置分页并把每页内容的实际需求尺寸写进临时文件，
        // 便于验证「所有界面元素都能显示全、没有被裁掉」。
        if (Environment.GetCommandLineArgs().Any(a => a == "--layout-report"))
        {
            string[] argv = Environment.GetCommandLineArgs();
            int sizeArg = Array.IndexOf(argv, "--resize");
            if (sizeArg >= 0 && sizeArg + 1 < argv.Length &&
                argv[sizeArg + 1].Split('x') is [var wS, var hS] &&
                int.TryParse(wS, out int rw) && int.TryParse(hS, out int rh))
            {
                _appWindow.Resize(new SizeInt32(rw, rh));
            }

            Root.Loaded += async (_, _) =>
            {
                await Task.Delay(800);
                var sb = new System.Text.StringBuilder();
                double scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
                sb.AppendLine($"clientDIP={Root.ActualWidth:F0}x{Root.ActualHeight:F0} scale={scale:F2} " +
                              $"rows=[{Root.RowDefinitions[0].ActualHeight:F0},{Root.RowDefinitions[1].ActualHeight:F0}," +
                              $"{Root.RowDefinitions[2].ActualHeight:F0},{Root.RowDefinitions[3].ActualHeight:F0}," +
                              $"{Root.RowDefinitions[4].ActualHeight:F0},{Root.RowDefinitions[5].ActualHeight:F0}]");

                for (int i = 0; i < SettingsPivot.Items.Count; i++)
                {
                    SettingsPivot.SelectedIndex = i;
                    await Task.Delay(350);
                    Root.UpdateLayout();
                    var item = (PivotItem)SettingsPivot.Items[i];
                    string detail = "";
                    if (item.Content is ScrollViewer sv && sv.Content is FrameworkElement inner)
                    {
                        sv.UpdateLayout();
                        inner.Measure(new Size(sv.ViewportWidth, double.PositiveInfinity));
                        detail = $" content={inner.DesiredSize.Height:F0} viewport={sv.ViewportHeight:F0}" +
                                 $" hScroll={sv.ScrollableHeight:F0}";
                    }
                    sb.AppendLine($"tab[{i}] {item.Header}: item={item.ActualHeight:F0}{detail}");
                }
                SettingsPivot.SelectedIndex = 0;

                var bottom = new List<string>();
                foreach (FrameworkElement fe in new FrameworkElement[] { JobsList, SettingsPivot })
                    bottom.Add($"{fe.Name}={fe.ActualHeight:F0}");
                sb.AppendLine(string.Join(" ", bottom));
                sb.AppendLine($"buttonsRow={Root.RowDefinitions[5].ActualHeight:F0} windowPX={_appWindow.Size.Width}x{_appWindow.Size.Height}");
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "subtitle_merger_layout.txt"), sb.ToString());
            };
        }

        // 复现/自检钩子（--dir <文件夹>）：走与拖放完全相同的 AddPathsAsync 路径把文件加进来。
        // 仅用于自动化复现与验收，正常双击启动不会触发。
        string[] argv2 = Environment.GetCommandLineArgs();
        int dirArg = Array.IndexOf(argv2, "--dir");
        if (dirArg >= 0 && dirArg + 1 < argv2.Length)
            _ = LoadFolderForTestAsync(argv2[dirArg + 1]);

        // 自检钩子（--dialog-report）：直接打开字幕轨设置对话框并量测实际尺寸，
        // 用于验证「对话框宽度被 ContentDialogMaxWidth 主题资源压窄」这类无法目测的布局问题。
        if (argv2.Contains("--dialog-report"))
            _ = ReportDialogAsync();

        ViewModel.Initialize();
    }

    private async Task ReportDialogAsync()
    {
        await Task.Delay(4000);
        var sb = new System.Text.StringBuilder();
        try
        {
            sb.AppendLine($"window={_appWindow.Size.Width}x{_appWindow.Size.Height} " +
                          $"scale={Root.XamlRoot?.RasterizationScale:F2} " +
                          $"root={Root.ActualWidth:F0}x{Root.ActualHeight:F0}");

            var dialog = new SubtitleTrackDialog(
                @"D:\BANGUMI\样例视频.mkv",
                new[] { @"D:\BANGUMI\样例视频.chs.ass", @"D:\BANGUMI\样例视频.eng.srt" },
                new[] { @"D:\BANGUMI\样例视频.chs.ass" },
                new List<SubTrack>(),
                () => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()))
            {
                XamlRoot = Root.XamlRoot,
            };
            Task<ContentDialogResult> showing = dialog.ShowAsync().AsTask();
            await Task.Delay(1200);
            dialog.UpdateLayout();
            sb.AppendLine($"dialog={dialog.ActualWidth:F0}x{dialog.ActualHeight:F0}");
            if (dialog.Content is FrameworkElement content)
            {
                content.UpdateLayout();
                sb.AppendLine($"content={content.ActualWidth:F0}x{content.ActualHeight:F0} " +
                              $"desired={content.DesiredSize.Width:F0}x{content.DesiredSize.Height:F0}");
            }
            object? maxRes = Application.Current.Resources["ContentDialogMaxWidth"];
            object? minRes = Application.Current.Resources["ContentDialogMinWidth"];
            sb.AppendLine($"ContentDialogMaxWidth={maxRes} ContentDialogMinWidth={minRes}");

            // 截图辅助（--dialog-shot <路径>）：先把对话框留在屏幕上，供外部截图后再关闭
            string[] argv = Environment.GetCommandLineArgs();
            int shotArg = Array.IndexOf(argv, "--dialog-shot");
            if (shotArg >= 0 && shotArg + 1 < argv.Length)
            {
                string shotPath = argv[shotArg + 1];
                await Task.Delay(3000);
                try
                {
                    string temp = Path.Combine(Path.GetDirectoryName(shotPath) ?? ".", "dlg_ready.flag");
                    File.WriteAllText(temp, DateTime.Now.ToString("O"));
                }
                catch { /* 忽略 */ }
                await Task.Delay(6000);
            }

            dialog.Hide();
            await showing;
        }
        catch (Exception ex)
        {
            sb.AppendLine("dialog-report error: " + ex);
        }
        finally
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "subtitle_merger_dialog.txt"), sb.ToString());
            Environment.Exit(0);
        }
    }

    private async Task LoadFolderForTestAsync(string folder)
    {
        await Task.Delay(1200);
        try { await ViewModel.AddPathsAsync(new[] { folder }); }
        catch (Exception ex) { App.LogTest("--dir 失败：" + ex); }
    }

    #region IUiServices —— 原生文件 / 文件夹 / 保存对话框

    private static PickerLocationId SuggestedLocation() => PickerLocationId.DocumentsLibrary;

    private void InitPicker(object picker) => InitializeWithWindow.Initialize(picker, _hwnd);

    /// <summary>
    /// WinRT 的 FileOpenPicker / FolderPicker / FileSavePicker 在**管理员（提升）进程**里不可用：
    /// 它们依赖未提升的 Shell 代理，调用会以 E_ACCESSDENIED 失败；在 WinUI 的 async void
    /// 命令里该异常会直接逃逸成 fail-fast（事件日志 0xc000027b），表现为「点一下按钮程序就没了」。
    /// 因此提升运行时统一改走经典 Win32 对话框。
    /// </summary>
    private static bool UseClassicDialogs => ClassicDialogs.IsElevated();

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title,
                                                            IReadOnlyList<string> extensions,
                                                            bool allowAll = true)
    {
        if (UseClassicDialogs)
            return ClassicDialogs.OpenFiles(_hwnd, title, extensions, allowAll);

        try
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
        catch (Exception ex) when (IsPickerAccessDenied(ex))
        {
            Log("文件选择器不可用（E_ACCESSDENIED），已切换到系统对话框");
            return ClassicDialogs.OpenFiles(_hwnd, title, extensions, allowAll);
        }
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (UseClassicDialogs)
            return ClassicDialogs.PickFolder(_hwnd, title);

        try
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
        catch (Exception ex) when (IsPickerAccessDenied(ex))
        {
            Log("文件夹选择器不可用（E_ACCESSDENIED），已切换到系统对话框");
            return ClassicDialogs.PickFolder(_hwnd, title);
        }
    }

    /// <summary>
    /// 原 filedialog.askopenfilename 支持 initialdir 任意路径；
    /// WinUI 3 原生 FileOpenPicker 只暴露 PickerLocationId 枚举，无法指定任意目录。
    /// 这里由 Shell 通过 SettingsIdentifier 记忆上次目录，initialDir 仅保留在配置中供其它用途。
    /// </summary>
    public async Task<string?> PickExecutableAsync(string title, string? initialDir)
    {
        _ = initialDir;
        if (UseClassicDialogs)
            return ClassicDialogs.OpenFiles(_hwnd, title, new[] { ".exe" }, allowAll: true).FirstOrDefault();

        try
        {
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
        catch (Exception ex) when (IsPickerAccessDenied(ex))
        {
            Log("文件选择器不可用（E_ACCESSDENIED），已切换到系统对话框");
            return ClassicDialogs.OpenFiles(_hwnd, title, new[] { ".exe" }, allowAll: true).FirstOrDefault();
        }
    }

    public async Task<string?> PickSaveCsvAsync(string suggestedName, string initialDir)
    {
        _ = initialDir;
        if (UseClassicDialogs)
            return ClassicDialogs.SaveFile(_hwnd, "导出报告", suggestedName, ".csv");

        try
        {
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
        catch (Exception ex) when (IsPickerAccessDenied(ex))
        {
            Log("保存对话框不可用（E_ACCESSDENIED），已切换到系统对话框");
            return ClassicDialogs.SaveFile(_hwnd, "导出报告", suggestedName, ".csv");
        }
    }

    /// <summary>判断是不是「提升进程里 picker 被拒绝」这一类异常（HRESULT 0x80070005）。</summary>
    private static bool IsPickerAccessDenied(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.UnauthorizedAccessException) return true;
            if (e.HResult == unchecked((int)0x80070005)) return true;   // E_ACCESSDENIED
            if (e is System.Runtime.InteropServices.COMException com &&
                com.HResult == unchecked((int)0x80070005)) return true;
        }
        return false;
    }

    private void Log(string text) => ViewModel?.Log(text);

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

    private DateTime _lastLogScroll = DateTime.MinValue;

    public void ScrollLogToEnd()
    {
        // 原 txt_log.see("end")：绑定更新后先完成布局，再把视口滚到最底部。
        // 重负载时日志可能每秒更新几十次，强制 UpdateLayout + ChangeView 会把 UI 线程拖垮，
        // 这里限流到最多每 120 ms 跟随一次（人工阅读也看不出差别）。
        DateTime now = DateTime.UtcNow;
        if ((now - _lastLogScroll).TotalMilliseconds < 120) return;
        _lastLogScroll = now;
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

    private void Root_Drop(object sender, DragEventArgs e)
        => CrashGuard.FireAndForget(() => DropAsync(e), "Root_Drop");

    private async Task DropAsync(DragEventArgs e)
    {
        Root_DragLeave(null!, e);
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

    private void JobsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        => CrashGuard.FireAndForget(() => ViewModel.EditSubtitlesAsync(), "JobsList_DoubleTapped");

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

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        => CrashGuard.FireAndForget(() => ClosingAsync(args), "OnAppWindowClosing");

    private async Task ClosingAsync(AppWindowClosingEventArgs args)
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
