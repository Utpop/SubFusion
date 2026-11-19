using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubFusion.App.Services;
using SubFusion.Core;
using SubFusion.Core.Models;
using SubFusion.Core.Services;

namespace SubFusion.App.ViewModels;

/// <summary>
/// 应用外壳 ViewModel：持有五个页面 ViewModel、导航状态、主题以及全局状态栏。
///
/// 只做编排：具体业务全部在页面 ViewModel 里，Shell 不碰任务执行细节。
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IUiServices _ui;
    private readonly AppSettings _settings;
    private readonly TaskCenter _tasks;
    private readonly SemaphoreSlim _detectGate = new(1, 1);
    private CancellationTokenSource? _detectCancel;
    private Task? _detectTask;
    private string? _lastMergeFfmpeg;
    private string? _lastMergeFfprobe;

    public ShellViewModel(IUiServices ui, AppSettings settings, TaskCenter tasks)
    {
        _ui = ui;
        _settings = settings;
        _tasks = tasks;

        Subtitle = new SubtitleViewModel(ui, settings, tasks);
        Merge = new VideoMergeViewModel(ui, settings, tasks);
        Trim = new VideoTrimViewModel(ui, settings, tasks);
        TaskCenterPage = new TaskCenterViewModel(tasks, ui);
        Settings = new SettingsViewModel(settings, ui);

        Settings.Changed += (_, _) => OnSettingsChanged();
        Settings.ToolsChanged += (_, _) => _ = RecheckToolsAsync();
        _tasks.Changed += (_, _) => RefreshStatusBar();

        Theme = settings.Theme;
    }

    #region 页面

    public SubtitleViewModel Subtitle { get; }
    public VideoMergeViewModel Merge { get; }
    public VideoTrimViewModel Trim { get; }
    public TaskCenterViewModel TaskCenterPage { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private string selectedTag = "subtitle";
    [ObservableProperty] private string header = "工作台";

    #endregion

    #region 主题

    /// <summary>主题名（system / light / dark）。变化时由窗口把 ElementTheme 应用到根元素。</summary>
    [ObservableProperty] private string theme = "system";

    /// <summary>主题需要应用时触发（参数为主题名）。</summary>
    public event EventHandler<string>? ThemeChanged;

    partial void OnThemeChanged(string value)
    {
        // 主题可能来自设置页（此时 _settings.Theme 已被写过）或 Shell 自己：
        // 两种来源都必须广播 ThemeChanged，否则设置页切换主题不会应用到可视树。
        if (!string.Equals(_settings.Theme, value, StringComparison.Ordinal)) _settings.Theme = value;
        ThemeChanged?.Invoke(this, value);
    }

    #endregion

    #region 状态栏

    [ObservableProperty] private string statusText = "就绪";
    [ObservableProperty] private string taskSummary = "没有任务";
    [ObservableProperty] private string toolText = "正在检测 ffmpeg…";
    [ObservableProperty] private bool toolReady;

    /// <summary>
    /// 刷新状态栏文字。进度条不在状态栏：它属于各工作区的内容区（页面内已有），
    /// 两个地方各放一条只会互相干扰。
    /// </summary>
    private void RefreshStatusBar()
    {
        TaskSummary = _tasks.TotalCount == 0
            ? "没有任务"
            : $"运行 {_tasks.RunningCount} · 完成 {_tasks.CompletedCount} · 失败 {_tasks.FailedCount}";
    }

    #endregion

    #region 导航

    /// <summary>
    /// 页面 key → 标题栏文字。说明性文字只放在页面正文顶部，
    /// 标题栏只显示页面名，避免同一条信息出现两次、也不和系统 caption 按钮抢空间。
    /// </summary>
    private static readonly Dictionary<string, string> PageTitles = new()
    {
        ["subtitle"] = "字幕合并",
        ["merge"] = "视频合并",
        ["trim"] = "视频裁剪",
        ["tasks"] = "任务中心",
        ["settings"] = "设置",
    };

    /// <summary>页面切换时触发（参数为页面 key）；外壳窗口订阅后执行 Frame 导航。</summary>
    public event EventHandler<string>? NavigationRequested;

    [RelayCommand]
    private void Navigate(string? tag)
    {
        string key = (tag ?? "subtitle").ToLowerInvariant();
        if (!PageTitles.ContainsKey(key)) key = "subtitle";
        SelectedTag = key;
        Header = PageTitles[key];
        if (key == "tasks") TaskCenterPage.Refresh();
        NavigationRequested?.Invoke(this, key);
    }

    #endregion

    #region 初始化 / 设置联动

    public void Initialize(string initialTag = "subtitle")
    {
        Subtitle.Initialize();
        Merge.Initialize();
        Trim.Initialize();
        RefreshStatusBar();
        Navigate(initialTag);
        _ = RequestDetectToolsAsync();
    }

    private async Task RequestDetectToolsAsync()
    {
        // 这里只取消、不释放上一次的 CTS：旧任务可能正停在 WaitAsync /
        // ThrowIfCancellationRequested 上握着它的 Token，立刻 Dispose 会让它抛
        // ObjectDisposedException 并变成 UnobservedTaskException——表现是工具检测静默中止、
        // 状态栏永远停在「正在检测 ffmpeg…」。释放统一交给持有它的那个任务收尾。
        CancellationTokenSource? previousSource = _detectCancel;
        if (previousSource is not null)
        {
            try { previousSource.Cancel(); } catch (ObjectDisposedException) { /* 已释放 */ }
        }
        _detectCancel = new CancellationTokenSource();
        CancellationToken token = _detectCancel.Token;
        Task previousTask = _detectTask ?? Task.CompletedTask;
        _detectTask = RunDetectSerializedAsync(previousTask, previousSource, token);
        try { await _detectTask; }
        catch (OperationCanceledException) { /* cancelled by newer request */ }
    }

    private async Task RunDetectSerializedAsync(Task previous, CancellationTokenSource? previousSource,
                                                CancellationToken token)
    {
        // 这里不能 ConfigureAwait(false)：后续 DetectToolsCoreAsync 会写 ToolText / ToolReady /
        // Settings.ToolStatusText 这些可观察属性，它们必须在 UI 线程赋值，否则绑定收不到通知。
        try { await previous; } catch { /* 上一次探测的失败不影响本次 */ }
        try
        {
            await _detectGate.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            // 等锁期间就被新请求取代：这一代什么也没做，直接收尾。
            try { previousSource?.Dispose(); } catch { /* 忽略 */ }
            return;
        }

        try
        {
            token.ThrowIfCancellationRequested();
            await DetectToolsCoreAsync(token);
        }
        finally
        {
            _detectGate.Release();
            // 上一代此刻一定已经离开锁区，可以安全释放。
            try { previousSource?.Dispose(); } catch { /* 忽略 */ }
        }
    }

    private async Task DetectToolsCoreAsync(CancellationToken token)
    {
        string? configured = _settings.FfmpegPath;
        string? ffmpeg = !string.IsNullOrEmpty(configured) && File.Exists(configured)
            ? configured
            : ToolLocator.FindFfmpeg();
        string? ffprobe = !string.IsNullOrEmpty(_settings.FfprobePath) && File.Exists(_settings.FfprobePath)
            ? _settings.FfprobePath
            : ToolLocator.FindFfprobe();

        // Setters persist automatically; only write when value actually changes.
        if (!string.IsNullOrEmpty(ffmpeg) && ffmpeg != _settings.FfmpegPath)
            _settings.FfmpegPath = ffmpeg;
        if (!string.IsNullOrEmpty(ffprobe) && ffprobe != _settings.FfprobePath)
            _settings.FfprobePath = ffprobe;
        token.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(ffmpeg))
        {
            ToolText = "ffmpeg 未找到";
            ToolReady = false;
            StatusText = "未找到 ffmpeg：请在「设置 → 媒体工具」中指定路径";
            Subtitle.ApplyToolStatus("未找到 ffmpeg", false);
            Settings.ApplyToolStatus("未找到 ffmpeg", false, "");
            return; // tool paths unchanged; no need to rebuild merge task
        }

        FfmpegCapabilities capabilities = await ToolLocator.FfmpegCapabilitiesAsync(ffmpeg);
        token.ThrowIfCancellationRequested();
        string version = await ToolLocator.FfmpegVersionAsync(ffmpeg);
        token.ThrowIfCancellationRequested();
        string versionShort = string.IsNullOrEmpty(version)
            ? "ffmpeg"
            : version.Split(" Copyright")[0];

        ToolReady = true;
        ToolText = "ffmpeg 已就绪";
        StatusText = "就绪";
        Subtitle.ApplyToolStatus(versionShort, true);
        Subtitle.ApplyCapabilities(capabilities);
        // Rebuild Core task only when the tool paths actually changed.
        if (ffmpeg != _lastMergeFfmpeg || ffprobe != _lastMergeFfprobe)
        {
            _lastMergeFfmpeg = ffmpeg;
            _lastMergeFfprobe = ffprobe;
            Merge.InvalidateTools();
            Trim.InvalidateTools();
        }

        var missing = new List<string>();
        if (!capabilities.Libass) missing.Add("libass（硬字幕）");
        if (!capabilities.X264) missing.Add("libx264（重新编码）");
        string capabilityText = missing.Count == 0
            ? "ffmpeg 具备 libass 与 libx264，软字幕与硬字幕都可正常处理。"
            : "当前 ffmpeg 缺少：" + string.Join("、", missing);
        Settings.ApplyToolStatus(string.IsNullOrEmpty(ffprobe) ? "找到 ffmpeg，但未找到 ffprobe"
                                                                : "ffmpeg 与 ffprobe 均已找到",
                                 !string.IsNullOrEmpty(ffprobe), capabilityText);
    }

    /// <summary>
    /// 设置页改了默认值：同步主题与两个工作区的默认参数。
    /// 这里<不>重新定位外部工具——那会在每次改参数时都起一次 ffmpeg 子进程，
    /// 工具路径变化走 <see cref="SettingsViewModel.ToolsChanged"/> 单独处理。
    /// </summary>
    private void OnSettingsChanged()
    {
        Theme = _settings.Theme;
        Subtitle.ReloadFromSettings();
        Merge.Initialize();
        Trim.Initialize();
    }

    [RelayCommand]
    private async Task RecheckToolsAsync()
    {
        Settings.Detecting = true;
        try
        {
            await RequestDetectToolsAsync();
        }
        finally
        {
            Settings.Detecting = false;
        }
    }

    #endregion

    #region 退出

    /// <summary>窗口关闭前调用；返回 false 表示用户取消退出。</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!await Subtitle.ConfirmCloseAsync()) return false;
        Merge.Shutdown();
        Trim.Shutdown();
        Subtitle.Shutdown();
        Subtitle.ApplyToSettings();
        Merge.ApplyToSettings();
        Trim.ApplyToSettings();
        _settings.Save();
        return true;
    }

    #endregion
}
