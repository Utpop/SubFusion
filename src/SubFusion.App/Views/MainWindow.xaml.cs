using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SubFusion.App.Services;
using SubFusion.App.ViewModels;
using SubFusion.Core;
using SubFusion.Core.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SubFusion.App.Views;

/// <summary>
/// 应用外壳窗口：标题栏、NavigationView 导航、状态栏、Mica 背景，并实现 <see cref="IUiServices"/>。
///
/// 这里只放"视图生命周期 + WinUI 无法用 MVVM 表达的平台交互"，
/// 业务状态全部在 <see cref="ShellViewModel"/> 及其页面 ViewModel 中。
/// </summary>
public sealed partial class MainWindow : Window, IUiServices
{
    private readonly DispatcherQueue _dispatcher;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private bool _allowClose;
    private ScrollViewer? _logScroll;

    // GUI 线程回调队列：TryEnqueue 失败时记录，关闭时排空，避免丢回调。
    private readonly Queue<Action> _uiQueue = new();
    private readonly object _uiQueueGate = new();
    private bool _drainingUiQueue;

    // 全局只允许一个 ContentDialog 处于显示状态：WinUI 并发 ShowAsync 会直接崩溃。
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    public ShellViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _hwnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        ConfigureWindow();

        var settings = AppSettings.Load();
        settings.Read();
        var tasks = new TaskCenter(this);
        ViewModel = new ShellViewModel(this, settings, tasks);
        // 页面在本构造函数内就会被创建，必须先把外壳登记到 App，再开始导航。
        App.ActiveShell = ViewModel;

        // 拖放兜底：以管理员运行时 OLE 拖放可能被 UIPI 拦掉，用经典 WM_DROPFILES 补一条通路。
        DragDropUnblocker.Apply(_hwnd);
        NativeWindowHelper.SetDropHandler(_hwnd, paths =>
            CrashGuard.FireAndForget(() => RouteDropAsync(paths), "DropFiles"));

        ViewModel.ThemeChanged += (_, theme) => ApplyTheme(theme);
        ViewModel.NavigationRequested += (_, key) => NavigateTo(key);
        ApplyTheme(ViewModel.Theme);

        Root.DataContext = ViewModel;
        _appWindow.Closing += OnAppWindowClosing;

        string[] argv = Environment.GetCommandLineArgs();
        string initialPage = "subtitle";
        int pageArg = Array.IndexOf(argv, "--page");
        if (pageArg >= 0 && pageArg + 1 < argv.Length) initialPage = argv[pageArg + 1];

        ViewModel.Initialize(initialPage);
        SyncNavSelection(initialPage);

        // --smoke 计时器由 App.OnLaunched 统一启动（此处不再重复注册）。
        if (argv.Contains("--ui-report", StringComparer.OrdinalIgnoreCase)) _ = RunUiReportAsync();
        int mergeArg = Array.IndexOf(argv, "--merge-e2e");
        if (mergeArg >= 0 && mergeArg + 1 < argv.Length) _ = RunMergeE2EAsync(argv[mergeArg + 1]);
        int subArg = Array.IndexOf(argv, "--subtitle-e2e");
        if (subArg >= 0 && subArg + 1 < argv.Length) _ = RunSubtitleE2EAsync(argv[subArg + 1]);

        // 验收/复现钩子（--dir <文件夹>）：走与拖放完全相同的入口把文件加进来。
        int dirArg = Array.IndexOf(argv, "--dir");
        if (dirArg >= 0 && dirArg + 1 < argv.Length)
            _ = LoadFolderForTestAsync(argv[dirArg + 1]);
    }

    /// <summary>把窗口配置（尺寸、标题、Mica、最小尺寸、图标）集中在一处。</summary>
    private void ConfigureWindow()
    {
        Title = ViewModelTitle;

        // Mica 是 WinUI 3 原生的长期表面材质；不支持时（旧系统/远程桌面）自动回退到纯色，
        // 因此不需要额外判断，直接设置即可。
        SystemBackdrop = new MicaBackdrop
        {
            Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt,
        };

        // 自绘标题栏：把整个标题区域交给应用，同时保留系统 caption 按钮。
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindowTitleBar bar = _appWindow.TitleBar;
            bar.ExtendsContentIntoTitleBar = true;
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
            bar.ButtonForegroundColor = null;
            bar.ButtonHoverBackgroundColor = null;
        }

        // 初始尺寸用「DIP 目标值 × 缩放比例」换算成物理像素：
        // 之前按工作区 90% 算，在 2250x1296 的屏幕上会开成 2025x1166，几乎等于最大化，
        // 用户还得手动缩回来。默认给一个舒适的窗口尺寸，并保证不超过工作区。
        RectInt32 workArea = DisplayArea
            .GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = GetRasterizationScale();
        int width = Math.Min((int)(DefaultWidthDip * scale), Math.Max(800, workArea.Width - 24));
        int height = Math.Min((int)(DefaultHeightDip * scale), Math.Max(560, workArea.Height - 48));
        _appWindow.Resize(new SizeInt32(width, height));
        _appWindow.Move(new PointInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2)));

        // 只有工作区本身就装不下这个尺寸时才最大化（例如 1366x768 + 150% 缩放）。
        if (workArea.Width < width || workArea.Height < height)
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        }

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            try { _appWindow.SetIcon(iconPath); } catch { /* 图标不可用不影响运行 */ }
        }

        // 最小尺寸不超过工作区，避免小屏把窗口强行撑出屏幕。
        NativeWindowHelper.SetMinimumSize(_hwnd,
            (int)Math.Min(960, Math.Max(720, workArea.Width - 24)),
            (int)Math.Min(640, Math.Max(520, workArea.Height - 24)));
    }

    /// <summary>默认窗口尺寸（DIP，会按当前 DPI 换算成物理像素）。</summary>
    private const double DefaultWidthDip = 1180;
    private const double DefaultHeightDip = 780;

    /// <summary>当前显示器的缩放比例；取不到时按 1.0 处理。</summary>
    private double GetRasterizationScale()
    {
        try
        {
            uint dpi = GetDpiForWindow(_hwnd);
            if (dpi == 0) return 1.0;
            return dpi / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private string ViewModelTitle => $"{Constants.AppName} v{Constants.AppVersion}";

    private void ApplyTheme(string theme)
    {
        ThemeService.Apply(Root, theme);
        // 标题栏按钮颜色跟随主题（自绘标题栏不会再自动反色）
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            bool dark = Root.ActualTheme == ElementTheme.Dark;
            AppWindowTitleBar bar = _appWindow.TitleBar;
            bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonHoverBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(24, 255, 255, 255)
                : Windows.UI.Color.FromArgb(24, 0, 0, 0);
            bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonPressedBackgroundColor = dark
                ? Windows.UI.Color.FromArgb(40, 255, 255, 255)
                : Windows.UI.Color.FromArgb(40, 0, 0, 0);
            bar.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
        }
    }

    /// <summary>
    /// 界面自检钩子（--ui-report）：依次切换五个页面与三套主题，
    /// 把每个页面的实际渲染尺寸、是否出现裁切、以及主题是否真正生效写进 %TEMP%\subfusion_ui.txt。
    /// 用于在无法人工目测时给出客观的运行时证据；正常启动不触发。
    /// </summary>
    private async Task RunUiReportAsync()
    {
        await Task.Delay(1200);

        // 可选 --resize WxH：验证窄窗口下的布局（导航折叠、内容不裁切）
        string[] args0 = Environment.GetCommandLineArgs();
        int resizeArg = Array.IndexOf(args0, "--resize");
        if (resizeArg >= 0 && resizeArg + 1 < args0.Length &&
            args0[resizeArg + 1].Split('x') is [var ws, var hs] &&
            int.TryParse(ws, out int rw) && int.TryParse(hs, out int rh))
        {
            _appWindow.Resize(new SizeInt32(rw, rh));
            await Task.Delay(1200);
        }

        var report = new System.Text.StringBuilder();
        try
        {
            report.AppendLine("window=" + _appWindow.Size.Width + "x" + _appWindow.Size.Height +
                              " scale=" + (Root.XamlRoot?.RasterizationScale ?? 1).ToString("0.##"));
            report.AppendLine("nav=" + Nav.PaneDisplayMode + " openPane=" + Nav.IsPaneOpen);

            string[] tags = { "subtitle", "merge", "tasks", "settings" };
            foreach (string tag in tags)
            {
                ViewModel.NavigateCommand.Execute(tag);
                Nav.SelectedItem = FindNavItem(tag);

                // 等页面真正完成首次布局（Loaded + 一帧），再量实际尺寸
                FrameworkElement? page = ContentFrame.Content as FrameworkElement;
                for (int i = 0; i < 20 && (page is null || page.ActualWidth <= 1); i++)
                {
                    await Task.Delay(100);
                    Root.UpdateLayout();
                    page = ContentFrame.Content as FrameworkElement;
                }
                if (page is null)
                {
                    report.AppendLine($"page[{tag}] = <null>");
                    continue;
                }

                double w = page.ActualWidth, h = page.ActualHeight;
                bool hasZero = w <= 1 || h <= 1;
                string overflow = "ok";
                if (page is Microsoft.UI.Xaml.Controls.Page p && p.Content is FrameworkElement inner)
                {
                    inner.UpdateLayout();
                    double needW = inner.DesiredSize.Width;
                    if (needW > w + 1) overflow = $"contentWanted={needW:0} > frame={w:0}";
                }
                report.AppendLine($"page[{tag}] {page.GetType().Name} {w:0}x{h:0} " +
                                  (hasZero ? "ZERO-SIZE" : "visible") + " " + overflow +
                                  $" frame={ContentFrame.ActualWidth:0}x{ContentFrame.ActualHeight:0}");
            }

            // 任务中心：未选中任务时，详情区与空状态必须互斥，不能同时占位（否则文字重叠）
            ViewModel.NavigateCommand.Execute("tasks");
            Nav.SelectedItem = FindNavItem("tasks");
            for (int i = 0; i < 20 && ContentFrame.Content is not Pages.TaskCenterPage; i++) await Task.Delay(100);
            await Task.Delay(500);
            if (ContentFrame.Content is Pages.TaskCenterPage tasksPage)
            {
                tasksPage.UpdateLayout();
                var hasSel = tasksPage.ViewModel.HasSelection;
                FrameworkElement? detail = tasksPage.FindName("DetailPanel") as FrameworkElement;
                FrameworkElement? empty = tasksPage.FindName("DetailEmptyState") as FrameworkElement;
                string vis(FrameworkElement? e) =>
                    e is null ? "missing" : (e.Visibility == Visibility.Visible
                        ? $"visible({e.ActualHeight:0}px)" : "collapsed");
                bool bothVisible = detail?.Visibility == Visibility.Visible &&
                                   empty?.Visibility == Visibility.Visible;
                report.AppendLine($"tasks[hasSelection={hasSel}] detail={vis(detail)} empty={vis(empty)} " +
                                  $"overlap={(bothVisible ? "YES-BUG" : "no")}");
            }

            foreach (string theme in new[] { "light", "dark", "system" })
            {
                ThemeService.Apply(Root, theme);
                await Task.Delay(500);
                Root.UpdateLayout();
                var brushes = new List<string>();
                foreach (string key in new[] { "StatusSuccessBrush", "StatusDangerBrush" })
                {
                    if (Application.Current.Resources.TryGetValue(key, out object? value) &&
                        value is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
                        brushes.Add(key + "=" + brush.Color.A + "," + brush.Color.R + "," +
                                    brush.Color.G + "," + brush.Color.B);
                    else brushes.Add(key + "=<missing>");
                }
                report.AppendLine($"theme[{theme}] applied={Root.RequestedTheme} actual={Root.ActualTheme} " +
                                  string.Join(" ", brushes));
            }

            // 主题恢复成用户配置
            ThemeService.Apply(Root, ViewModel.Theme);
            report.AppendLine("restored=" + ViewModel.Theme);
            report.AppendLine("REDIRECTS=" + ContentFrame.BackStack.Count);
        }
        catch (Exception ex)
        {
            report.AppendLine("UI-REPORT-ERROR: " + ex);
        }
        finally
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "subfusion_ui.txt"), report.ToString());
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// 视频合并端到端自检（--merge-e2e &lt;目录&gt;）：真实走一遍
    /// 「添加输入 → Core 探测/兼容性分析 → 执行合并 → 校验输出」，把结论写进 %TEMP%\subfusion_merge_e2e.txt。
    /// 与用户点按钮走的是同一条命令路径（VideoMergeViewModel.StartCommand）。
    /// </summary>
    private async Task RunMergeE2EAsync(string dir)
    {
        var report = new System.Text.StringBuilder();
        try
        {
            await Task.Delay(1500);
            ViewModel.NavigateCommand.Execute("merge");
            await Task.Delay(800);

            List<string> inputs = Directory.GetFiles(dir, "*.mp4").OrderBy(f => f, StringComparer.Ordinal).ToList();
            report.AppendLine("inputs=" + inputs.Count + " -> " + string.Join(", ", inputs.Select(Path.GetFileName)));
            if (inputs.Count == 0) { report.AppendLine("NO-INPUTS"); return; }

            await ViewModel.Merge.AddPathsAsync(inputs);
            await Task.Delay(4000);

            ViewModel.Merge.OutputDirectory = dir;
            ViewModel.Merge.OutputName = "merged_out";
            await Task.Delay(1500);
            report.AppendLine("inputsListed=" + ViewModel.Merge.Inputs.Count);
            report.AppendLine("strategy=" + ViewModel.Merge.StrategyText);
            report.AppendLine("canStreamCopy=" + ViewModel.Merge.CanStreamCopy);
            report.AppendLine("reasons=" + ViewModel.Merge.Reasons.Count);
            report.AppendLine("chaptersEnabled=" + ViewModel.Merge.ChaptersEnabled);
            report.AppendLine("outputPath=" + ViewModel.Merge.OutputPath);
            report.AppendLine("firstResolution=" + ViewModel.Merge.Inputs[0].Resolution +
                              " codec=" + ViewModel.Merge.Inputs[0].CodecText +
                              " dur=" + ViewModel.Merge.Inputs[0].DurationText);

            ViewModel.Merge.StartCommand.Execute(null);
            for (int i = 0; i < 240 && ViewModel.Merge.IsRunning; i++) await Task.Delay(500);
            await Task.Delay(1500);

            report.AppendLine("stage=" + ViewModel.Merge.StageText);
            report.AppendLine("progress=" + ViewModel.Merge.Progress.ToString("0.#"));
            report.AppendLine("resultOk=" + ViewModel.Merge.ResultOk);
            report.AppendLine("result=" + ViewModel.Merge.ResultText.Replace("\n", " | "));
            string output = ViewModel.Merge.OutputPath;
            report.AppendLine("outputExists=" + File.Exists(output));
            if (File.Exists(output))
                report.AppendLine("outputSize=" + new FileInfo(output).Length);
            else
                report.AppendLine("dirListing=" + string.Join(",", Directory.GetFiles(dir).Select(Path.GetFileName)));

            // 任务中心必须反映这次真实执行（状态、进度、输出路径、诊断日志）
            var taskCenter = ViewModel.TaskCenterPage;
            taskCenter.Refresh();
            report.AppendLine("taskCount=" + taskCenter.Filtered.Count);
            if (taskCenter.Filtered.Count > 0)
            {
                TaskRecord first = taskCenter.Filtered[0];
                taskCenter.SetSelection(new[] { first });
                report.AppendLine("taskKind=" + first.KindText);
                report.AppendLine("taskState=" + first.StateText);
                report.AppendLine("taskProgress=" + first.Progress.ToString("0.#"));
                report.AppendLine("taskOutput=" + first.OutputPath);
                report.AppendLine("taskLogLines=" + first.Log.Count);
                report.AppendLine("detailTitle=" + taskCenter.SelectedTitle);
                report.AppendLine("detailState=" + taskCenter.SelectedState);
            }
            report.AppendLine("shellTaskSummary=" + ViewModel.TaskSummary +
                              " toolReady=" + ViewModel.ToolReady);
        }
        catch (Exception ex)
        {
            report.AppendLine("MERGE-E2E-ERROR: " + ex);
        }
        finally
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "subfusion_merge_e2e.txt"), report.ToString());
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// 字幕合并端到端自检（--subtitle-e2e &lt;目录&gt;）：添加视频+字幕 → 开始处理 → 校验输出。
    /// 走的是页面上的 StartCommand，与用户操作完全一致。
    /// </summary>
    private async Task RunSubtitleE2EAsync(string dir)
    {
        var report = new System.Text.StringBuilder();
        try
        {
            await Task.Delay(1500);
            ViewModel.NavigateCommand.Execute("subtitle");
            await Task.Delay(800);

            List<string> inputs = Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal).ToList();
            report.AppendLine("inputs=" + string.Join(", ", inputs.Select(Path.GetFileName)));

            await ViewModel.Subtitle.AddPathsAsync(inputs);
            await Task.Delay(4000);

            ViewModel.Subtitle.OutputDir = dir;
            await Task.Delay(500);
            report.AppendLine("rows=" + ViewModel.Subtitle.Rows.Count);
            report.AppendLine("firstSubs=" + (ViewModel.Subtitle.Rows.Count > 0 ? ViewModel.Subtitle.Rows[0].SubsText : "-"));
            report.AppendLine("countText=" + ViewModel.Subtitle.CountText);

            ViewModel.Subtitle.StartCommand.Execute(null);
            for (int i = 0; i < 240 && ViewModel.Subtitle.IsRunning; i++) await Task.Delay(500);
            await Task.Delay(1500);

            report.AppendLine("status=" + ViewModel.Subtitle.StatusText);
            report.AppendLine("logTail=" + string.Join(" | ", ViewModel.Subtitle.LogLines.TakeLast(6)));
            report.AppendLine("dirListing=" + string.Join(",", Directory.GetFiles(dir).Select(Path.GetFileName)));
        }
        catch (Exception ex)
        {
            report.AppendLine("SUBTITLE-E2E-ERROR: " + ex);
        }
        finally
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "subfusion_subtitle_e2e.txt"), report.ToString());
            Environment.Exit(0);
        }
    }

    private NavigationViewItem? FindNavItem(string tag)
    {
        foreach (object item in Nav.MenuItems.Concat(Nav.FooterMenuItems))
            if (item is NavigationViewItem entry && (entry.Tag as string) == tag) return entry;
        return null;
    }

    private async Task LoadFolderForTestAsync(string folder)
    {
        await Task.Delay(1200);
        try { await RouteDropAsync(new[] { folder }); }
        catch (Exception ex) { App.LogTest("--dir 失败：" + ex); }
    }

    /// <summary>拖放/命令行导入的统一路由：当前在哪个工作区就交给哪个 ViewModel。</summary>
    private async Task RouteDropAsync(IReadOnlyList<string> paths)
    {
        if (ViewModel.SelectedTag == "merge") await ViewModel.Merge.AddPathsAsync(paths);
        else await ViewModel.Subtitle.AddPathsAsync(paths);
    }

    #region 导航

    /// <summary>把页面 key 映射为页面类型并在 Frame 中导航（同一个 key 不重复导航）。</summary>
    private void NavigateTo(string key)
    {
        Type? target = key switch
        {
            "subtitle" => typeof(Pages.SubtitlePage),
            "merge" => typeof(Pages.MergePage),
            "tasks" => typeof(Pages.TaskCenterPage),
            "settings" => typeof(Pages.SettingsPage),
            _ => typeof(Pages.SubtitlePage),
        };
        if (ContentFrame.CurrentSourcePageType == target) return;
        ContentFrame.Navigate(target, null,
                              new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag) ViewModel.NavigateCommand.Execute(tag);
    }

    private void SyncNavSelection(string tag)
    {
        foreach (object item in Nav.MenuItems.Concat(Nav.FooterMenuItems))
        {
            if (item is NavigationViewItem entry && (entry.Tag as string) == tag)
            {
                Nav.SelectedItem = entry;
                break;
            }
        }
        UpdateBackButton();
    }

    private void UpdateBackButton()
        => Nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;

    #endregion

    #region IUiServices —— 文件 / 文件夹 / 保存对话框

    /// <summary>
    /// WinRT 的 FileOpenPicker / FolderPicker / FileSavePicker 在**管理员（提升）进程**里不可用：
    /// 它们依赖未提升的 Shell 代理，调用会以 E_ACCESSDENIED 失败；在 async void 命令里
    /// 该异常会直接逃逸成 fail-fast（事件日志 0xc000027b）。因此提升运行时改走经典 Win32 对话框。
    /// </summary>
    private static bool UseClassicDialogs => ClassicDialogs.IsElevated();

    private void InitPicker(object picker) => InitializeWithWindow.Initialize(picker, _hwnd);

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
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
                SettingsIdentifier = "SubFusion.Files",
                CommitButtonText = title,
            };
            InitPicker(picker);
            foreach (string extension in extensions) picker.FileTypeFilter.Add(extension);
            if (allowAll) picker.FileTypeFilter.Add("*");
            IReadOnlyList<Windows.Storage.StorageFile> files = await picker.PickMultipleFilesAsync();
            return files.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        }
        catch (Exception ex) when (IsPickerAccessDenied(ex))
        {
            return ClassicDialogs.OpenFiles(_hwnd, title, extensions, allowAll);
        }
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (UseClassicDialogs) return ClassicDialogs.PickFolder(_hwnd, title);
        try
        {
            var picker = new FolderPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                SettingsIdentifier = "SubFusion.Folders",
                CommitButtonText = title,
            };
            InitPicker(picker);
            picker.FileTypeFilter.Add("*");
            Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex) when (IsPickerAccessDenied(ex))
        {
            return ClassicDialogs.PickFolder(_hwnd, title);
        }
    }

    public async Task<string?> PickExecutableAsync(string title, string? initialDir)
    {
        _ = initialDir;
        if (UseClassicDialogs)
            return ClassicDialogs.OpenSingleFile(_hwnd, title, new[] { ".exe" });
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                SettingsIdentifier = "SubFusion.Executable",
                CommitButtonText = title,
            };
            InitPicker(picker);
            picker.FileTypeFilter.Add(".exe"); // 可执行文件只允许 .exe，不显示“所有文件”
            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex) when (IsPickerAccessDenied(ex))
        {
            return ClassicDialogs.OpenSingleFile(_hwnd, title, new[] { ".exe" });
        }
    }

    public async Task<string?> PickSaveCsvAsync(string suggestedName, string initialDir)
    {
        _ = initialDir;
        if (UseClassicDialogs) return ClassicDialogs.SaveFile(_hwnd, "导出报告", suggestedName, ".csv");
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = suggestedName,
                DefaultFileExtension = ".csv",
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SettingsIdentifier = "SubFusion.Report",
            };
            InitPicker(picker);
            picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
            Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
        catch (Exception ex) when (IsPickerAccessDenied(ex))
        {
            return ClassicDialogs.SaveFile(_hwnd, "导出报告", suggestedName, ".csv");
        }
    }

    public async Task<string?> PickSaveVideoAsync(string title, string suggestedName, string extension,
                                                  string? initialDir)
    {
        _ = initialDir;
        if (UseClassicDialogs)
            return ClassicDialogs.SaveFile(_hwnd, title, suggestedName, extension);
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = suggestedName,
                DefaultFileExtension = extension,
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
                SettingsIdentifier = "SubFusion.MergeOutput",
            };
            InitPicker(picker);
            picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(),
                                       new List<string> { extension });
            Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
        catch (Exception ex) when (IsPickerAccessDenied(ex))
        {
            return ClassicDialogs.SaveFile(_hwnd, title, suggestedName, extension);
        }
    }

    private static bool IsPickerAccessDenied(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is UnauthorizedAccessException) return true;
            if (e.HResult == unchecked((int)0x80070005)) return true;
            if (e is System.Runtime.InteropServices.COMException com &&
                com.HResult == unchecked((int)0x80070005)) return true;
        }
        return false;
    }

    #endregion

    #region IUiServices —— 对话框 / 剪贴板 / 资源管理器 / 线程

    private ContentDialog MakeDialog(string title, string message, string primary, string? close)
        => new()
        {
            Title = title,
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

    /// <summary>
    /// 串行显示任意 ContentDialog。WinUI 同一 XamlRoot 上并发 ShowAsync 会抛异常并崩溃，
    /// 因此所有对话框（含自定义编辑框）统一经此信号量排队；异常不会逃逸到 async void 消息循环。
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogGuardedAsync(ContentDialog dialog)
    {
        await _dialogGate.WaitAsync();
        try { return await dialog.ShowAsync(); }
        catch (Exception ex)
        {
            CrashGuard.Write("ContentDialog.ShowAsync", ex);
            return ContentDialogResult.None;
        }
        finally { _dialogGate.Release(); }
    }

    public async Task ShowInfoAsync(string message)
        => await ShowDialogGuardedAsync(MakeDialog("提示", message, "确定", null));

    public async Task ShowErrorAsync(string message)
        => await ShowDialogGuardedAsync(MakeDialog("出错了", message, "确定", null));

    public async Task<bool> AskYesNoAsync(string message)
    {
        ContentDialogResult result = await ShowDialogGuardedAsync(MakeDialog("请确认", message, "是", "否"));
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
        ContentDialogResult result = await ShowDialogGuardedAsync(dialog);
        return result == ContentDialogResult.Primary ? dialog.Result : null;
    }

    public Task SetClipboardTextAsync(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        try { Clipboard.SetContent(package); }
        catch { /* 剪贴板被其它进程占用时忽略 */ }
        return Task.CompletedTask;
    }

    public void OpenFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch { /* 打开失败不应影响任务 */ }
    }

    private DateTime _lastLogScroll = DateTime.MinValue;

    /// <summary>日志滚动注册：由 LogView 加载后把内部 ScrollViewer 交给外壳。</summary>
    public void RegisterLogScroll(ScrollViewer scroll) => _logScroll = scroll;

    public void ScrollLogToEnd()
    {
        if (_logScroll is null) return;
        // 高频日志合并到最多每 120 ms 跟随一次，人眼无差别但不会拖垮 UI 线程。
        DateTime now = DateTime.UtcNow;
        if ((now - _lastLogScroll).TotalMilliseconds < 120) return;
        _lastLogScroll = now;
        _logScroll.UpdateLayout();
        _logScroll.ChangeView(null, double.MaxValue, null, disableAnimation: true);
    }

    /// <summary>供 App 在窗口外投递 UI 回调（返回是否成功入队）。</summary>
    internal bool TryEnqueue(Action action) => _dispatcher.TryEnqueue(() => action());

    public void RunOnUiThread(Action action)
    {
        if (_dispatcher.HasThreadAccess) { action(); return; }
        // TryEnqueue 返回 false 表示队列已关闭：把回调暂存，交由关闭前 DrainUiQueue 执行，
        // 而不是静默丢失（丢回调会让任务状态停留在实际状态之前）。
        bool queued = _dispatcher.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { CrashGuard.Write("RunOnUiThread", ex); }
        });
        if (!queued)
        {
            CrashGuard.Write("RunOnUiThread", new InvalidOperationException("DispatcherQueue.TryEnqueue failed"));
            lock (_uiQueueGate) _uiQueue.Enqueue(action);
        }
    }

    /// <summary>窗口关闭前排空未能入队的回调，避免状态丢失。</summary>
    internal void DrainUiQueue()
    {
        lock (_uiQueueGate)
        {
            if (_drainingUiQueue) return;
            _drainingUiQueue = true;
            try
            {
                while (_uiQueue.Count > 0)
                {
                    Action action = _uiQueue.Dequeue();
                    try { action(); }
                    catch (Exception ex) { CrashGuard.Write("DrainUiQueue", ex); }
                }
            }
            finally { _drainingUiQueue = false; }
        }
    }

    #endregion

    #region 原生拖放

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            // 不含文件 = 列表内部的拖拽排序。这里必须显式给出 Move：
            // AcceptedOperation 默认是 None，根元素返回 None 会让 ListView 的内部重排
            // 在 drag-over 阶段就被拒绝，表现就是「鼠标拖动没有任何反应」。
            // 之前这里直接 return，正是拖拽排序失效的原因。
            e.AcceptedOperation = DataPackageOperation.Move;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "添加视频 / 字幕 / 文件夹";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
    }

    private void Root_DragEnter(object sender, DragEventArgs e) { }

    private void Root_DragLeave(object sender, DragEventArgs e) { }

    private void Root_Drop(object sender, DragEventArgs e)
        => CrashGuard.FireAndForget(() => DropAsync(e), "Root_Drop");

    private async Task DropAsync(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            IReadOnlyList<Windows.Storage.IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            List<string> paths = items.Select(i => i.Path ?? "").Where(p => p.Length > 0).ToList();
            await RouteDropAsync(paths);
        }
        catch { /* 拖放数据异常时静默忽略，按钮添加仍可用 */ }
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
        // 确认之后的所有 UI 变更显式回到 UI 线程执行，避免由后台续帧触发。
        _dispatcher.TryEnqueue(() =>
        {
            _allowClose = true;
            DrainUiQueue();
            NativeWindowHelper.RemoveWindow(_hwnd);
            App.ClearWindow();
            Close();
        });
    }

    #endregion
}
