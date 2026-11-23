using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubFusion.App.Services;
using SubFusion.Core;
using SubFusion.Core.Media;
using SubFusion.Core.Chapters;
using SubFusion.Core.Merge;
using SubFusion.Core.Services;

namespace SubFusion.App.ViewModels;

/// <summary>
/// 视频合并工作区的一行输入。媒体信息来自 Core 的探测结果，不在 GUI 侧做任何推断。
/// </summary>
public sealed partial class MergeInputViewModel : ObservableObject
{
    public MergeInputViewModel(string path) => Path = path;

    public string Path { get; }

    public string FileName
    {
        get
        {
            try { return System.IO.Path.GetFileName(Path); }
            catch { return Path; }
        }
    }

    [ObservableProperty] private bool probing = true;
    [ObservableProperty] private bool probed;
    [ObservableProperty] private string error = "";

    [ObservableProperty] private string resolution = "-";
    [ObservableProperty] private string durationText = "-";
    [ObservableProperty] private string codecText = "-";
    [ObservableProperty] private string frameRateText = "-";
    [ObservableProperty] private string pixelFormat = "-";
    [ObservableProperty] private string audioText = "-";
    [ObservableProperty] private string hdrText = "SDR";
    [ObservableProperty] private bool isHdr;

    /// <summary>用户可编辑的章节标题（默认取文件名去扩展名，与 Core 的默认行为一致）。</summary>
    [ObservableProperty] private string chapterTitle = "";

    /// <summary>用户是否编辑过章节标题（含清空）。为 true 时重新分析不再覆盖。</summary>
    public bool ChapterTitleEdited { get; private set; }

    /// <summary>只由初始化/默认逻辑调用：填入默认标题且不标记为“用户已编辑”。</summary>
    public void SetDefaultChapterTitle()
    {
        ChapterTitle = DefaultChapterTitle;
        ChapterTitleEdited = false;
    }

    partial void OnChapterTitleChanged(string value)
    {
        // x:Bind TwoWay 的用户编辑会走到这里；代码里设置默认值时用 SetDefaultChapterTitle 复位标记。
        ChapterTitleEdited = true;
    }

    public string DefaultChapterTitle
    {
        get
        {
            try { return System.IO.Path.GetFileNameWithoutExtension(Path); }
            catch { return Path; }
        }
    }

    public bool Failed => Error.Length > 0;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(Failed));

    public void Apply(MediaBundle bundle)
    {
        Probed = bundle.Success;
        Probing = false;
        if (!bundle.Success)
        {
            Error = bundle.Error;
            return;
        }
        Error = "";
        VideoStreamInfo? v = bundle.PrimaryVideo;
        Resolution = v is null ? "无视频流" : $"{v.Width}×{v.Height}";
        DurationText = bundle.DurationText;
        CodecText = v is null ? "-"
            : (v.Codec + (v.Profile.Length > 0 ? $" ({v.Profile})" : "")).ToUpperInvariant();
        FrameRateText = v is null ? "-"
            : v.EffectiveFrameRate.ToString("0.###", CultureInfo.InvariantCulture) + " fps";
        PixelFormat = v is null ? "-" : v.PixFmt;
        AudioStreamInfo? a = bundle.PrimaryAudio;
        AudioText = a is null ? "无音轨"
            : $"{a.Codec.ToUpperInvariant()} · {a.SampleRate / 1000.0:0.#} kHz · {a.Channels} ch";
        IsHdr = v?.IsHdr == true;
        HdrText = v is null ? "-" : (v.IsHdr ? v.HdrFormat : "SDR");
        // 只有用户从未编辑过时才补默认标题：用户主动清空过则保持为空。
        if (!ChapterTitleEdited && ChapterTitle.Length == 0) ChapterTitle = DefaultChapterTitle;
    }

    public void Fail(string message)
    {
        Probing = false;
        Probed = false;
        Error = message;
    }
}

/// <summary>
/// 视频合并工作区的 ViewModel。
///
/// 与 Core 的边界（本页最关键的设计约束）：
/// - 只构造 <see cref="VideoMergeOptions"/> 并调用 <see cref="VideoMergeTask"/>；
/// - 兼容性结论来自 <see cref="CompatibilityAnalyzer"/>（经 <c>AnalyzeAsync</c>），
///   GUI 不自行判断"能不能无损拼接"；
/// - 章节时间轴、ffmpeg 命令、输出校验全部在 Core 内完成，这里只展示结果。
///
/// 线程：<see cref="VideoMergeTask.RunAsync"/> 的进度回调来自任意线程，
/// 全部经 <see cref="IUiServices.RunOnUiThread"/> 投递；高频进度做了合并（见 <see cref="OnProgress"/>）。
/// </summary>
public sealed partial class VideoMergeViewModel : ObservableObject
{
    private readonly IUiServices _ui;
    private readonly AppSettings _settings;
    private readonly TaskCenter _tasks;

    private VideoMergeTask? _task;
    private TaskRecord? _record;
    private CancellationTokenSource? _runSource;
    private CancellationTokenSource? _runLinked; // links _runSource + record.Cancellation
    private CancellationTokenSource? _analyzeSource;
    private List<MergeInputViewModel> _selected = new();

    private DateTime _lastProgressFlush = DateTime.MinValue;
    private MergeProgress? _pendingProgress;
    private bool _progressScheduled;
    /// <summary>
    /// 任务已结束、进度区已定稿。此后到达的任何进度回调都必须丢弃。
    ///
    /// 这不是防御性写法，而是修一个实测到的界面故障：
    /// 引擎最后报的一条（「输出已就位… 99.9%」）可能还在 DispatcherQueue 里排队，
    /// 而 ShowResult 已经先把界面定稿成「已完成 / 100%」。
    /// 两条回调的先后一颠倒，排队中的旧快照就会把定稿覆盖回去——
    /// 于是文件明明已经写好，界面却停在 99.9% 那句话上。
    /// </summary>
    private bool _progressClosed;

    private bool _loading;

    /// <summary>用户是否手动改过输出名；改过就不再自动覆盖。</summary>
    private bool _outputNameUserEdited;

    /// <summary>正在由代码写入输出名（自动生成），此时不应视为用户编辑。</summary>
    private bool _suppressOutputNameEdit;

    public VideoMergeViewModel(IUiServices ui, AppSettings settings, TaskCenter tasks)
    {
        _ui = ui;
        _settings = settings;
        _tasks = tasks;
        Inputs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasInputs));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(InputCountText));
            AddInputsCommand.NotifyCanExecuteChanged();
        };
    }

    #region 集合

    public ObservableCollection<MergeInputViewModel> Inputs { get; } = new();

    public bool HasInputs => Inputs.Count > 0;
    public bool ShowEmptyState => Inputs.Count == 0;
    public string InputCountText => Inputs.Count == 0 ? "尚未添加视频" : $"{Inputs.Count} 个视频";

    public IReadOnlyList<string> Containers { get; } = new[] { "mkv", "mp4" };
    public IReadOnlyList<string> Codecs { get; } = new[] { "", "libx264", "libx265" };
    public IReadOnlyList<string> Presets { get; } =
        new[] { "ultrafast", "veryfast", "fast", "medium", "slow", "veryslow" };
    public IReadOnlyList<string> Fits { get; } = new[] { "Contain", "Cover", "Stretch" };
    public IReadOnlyList<string> AudioStrategies { get; } = new[] { "Auto", "Reencode", "Copy" };

    public IReadOnlyList<(string Key, string Label)> FitLabels { get; } = new[]
    {
        ("Contain", "Contain（完整显示·补边）"),
        ("Cover", "Cover（填满画面·裁切）"),
        ("Stretch", "Stretch（拉伸到目标分辨率）"),
    };

    #endregion

    #region 选项

    [ObservableProperty] private string container = "mkv";
    [ObservableProperty] private string codec = "";
    [ObservableProperty] private double crf = 20;
    [ObservableProperty] private string preset = "medium";
    [ObservableProperty] private string fit = "Contain";
    [ObservableProperty] private string audioStrategyText = "Auto";
    [ObservableProperty] private bool allowReencode = true;
    [ObservableProperty] private bool autoChapters = true;
    [ObservableProperty] private bool verifyOutput = true;
    [ObservableProperty] private bool overwrite = true;

    /// <summary>
    /// 校验通过后删除源文件（不可逆）。默认关闭。
    /// 勾选它会自动强制开启「校验输出」——没有校验就没有删除的前提。
    /// </summary>
    [ObservableProperty] private bool deleteSource;

    /// <summary>
    /// 只重编码偏离多数派的片段，其余保留原样。默认开启。
    /// 好处是同样能合并成功，但保留的段逐比特不变，耗时与画质都明显更好。
    /// </summary>
    [ObservableProperty] private bool partialReencode = true;

    /// <summary>
    /// 无损拼接失败时自动改用重新编码重试。默认关闭：
    /// 参数一致时 concat 极少失败，真失败通常是文件本身有问题，
    /// 自动重编码只会把「立刻报错」变成「跑很久仍失败」。
    /// </summary>
    [ObservableProperty] private bool autoFallbackOnFailure;

    /// <summary>
    /// 输入尾部被截断时，保留每个文件真正读得出来的部分继续合并。默认开启。
    ///
    /// 下载中断/录制崩溃留下的文件，容器头仍声明完整时长，数据却在中途断了。
    /// 直接拼接的结果是「时长不符 + 结尾无法解码」，报错指向输出、看着像合并做坏了。
    /// 开启后按可读部分收尾，并在结果里说明每个文件少了多少秒。
    /// </summary>
    [ObservableProperty] private bool salvageTruncatedInputs = true;

    [ObservableProperty] private string outputPath = "";
    [ObservableProperty] private string outputDirectory = "";
    [ObservableProperty] private string outputName = "";

    /// <summary>输出容器为 mkv 时才允许章节（与 <see cref="VideoMergeOptions.ChaptersEnabled"/> 一致）。</summary>
    public bool ChaptersAvailable => string.Equals(Container, "mkv", StringComparison.OrdinalIgnoreCase);
    public bool ChaptersEnabled => ChaptersAvailable && AutoChapters;

    partial void OnContainerChanged(string value)
    {
        OnPropertyChanged(nameof(ChaptersAvailable));
        OnPropertyChanged(nameof(ChaptersEnabled));
        SyncOutputExtension();
        if (!_loading) _ = RefreshAnalysisAsync();
    }

    /// <summary>关闭校验时同步取消删源，避免出现"未校验却要删源"的危险组合。</summary>
    partial void OnVerifyOutputChanged(bool value)
    {
        if (!value && DeleteSource) DeleteSource = false;
    }

    partial void OnAutoChaptersChanged(bool value)
    {
        OnPropertyChanged(nameof(ChaptersEnabled));
        if (!_loading) _ = RefreshAnalysisAsync();
    }

    partial void OnAllowReencodeChanged(bool value) { if (!_loading) _ = RefreshAnalysisAsync(); }
    partial void OnFitChanged(string value) { if (!_loading) _ = RefreshAnalysisAsync(); }
    partial void OnCodecChanged(string value) { if (!_loading) _ = RefreshAnalysisAsync(); }
    partial void OnAudioStrategyTextChanged(string value) { if (!_loading) _ = RefreshAnalysisAsync(); }

    #endregion

    #region 分析结果（全部来自 Core）

    [ObservableProperty] private bool analyzing;
    [ObservableProperty] private bool analyzed;
    [ObservableProperty] private bool canStreamCopy;
    [ObservableProperty] private string strategyText = "添加视频后自动分析";
    [ObservableProperty] private string strategyDetail = "";
    [ObservableProperty] private bool canRemuxOnly;
    [ObservableProperty] private bool hasReasons;
    /// <summary>差异提示的标题：随策略变化，不能写死成「会导致统一重新编码」。</summary>
    [ObservableProperty] private string reasonTitle = "";
    [ObservableProperty] private string reasonIntro = "";
    /// <summary>true 表示必须重编码（用警示色）；false 表示只是会重封装（用提示色）。</summary>
    [ObservableProperty] private bool reasonsAreBlocking;
    [ObservableProperty] private string analysisError = "";

    public ObservableCollection<string> Reasons { get; } = new();

    /// <summary>按修复代价分组后的差异（警示色表示会重编码）。</summary>
    public ObservableCollection<MergeReasonGroupViewModel> ReasonGroups { get; } = new();

    /// <summary>实际会采用的策略描述（无损拼接 / 统一重新编码 / 被禁止）。</summary>
    private void ApplyAnalysis(MergeCompatibility compatibility)
    {
        Analyzed = true;
        CanStreamCopy = compatibility.CanStreamCopy;
        CanRemuxOnly = compatibility.CanRemuxOnly;
        Reasons.Clear();
        foreach (MergeReason reason in compatibility.Reasons) Reasons.Add(reason.Detail);
        ReasonGroups.Clear();
        // 分组只在"需要重编码"或"需要重封装"时才有意义；
        // 完全一致时 Reasons 为空，分组自然也只有一个空的集合。
        foreach (MergeReasonGroup group in compatibility.GroupedReasons)
            ReasonGroups.Add(new MergeReasonGroupViewModel(group));
        HasReasons = Reasons.Count > 0;

        if (compatibility.CanStreamCopy)
        {
            StrategyText = "无损拼接（concat + stream copy）";
            StrategyDetail = "所有输入的编码、分辨率、帧率、色彩与音频参数一致，直接拼接，不重新编码。";
        }
        else if (compatibility.CanRemuxOnly)
        {
            StrategyText = "无损重封装后拼接";
            StrategyDetail = "差异只在时间轴（时间基准/帧率），重封装对齐即可拼接，画质不受任何损失。";
            ReasonTitle = "需要先对齐时间轴";
            ReasonIntro = "下列差异会用无损重封装消除，视频不会被重新编码：";
            ReasonsAreBlocking = false;
        }
        else if (!AllowReencode)
        {
            StrategyText = "无法合并：已禁止重新编码";
            StrategyDetail = "输入不满足无损拼接条件，且「允许重新编码」已关闭。";
            ReasonTitle = "不满足无损拼接的参数";
            ReasonIntro = "下列差异无法用重封装消除，且「允许重新编码」已关闭：";
            ReasonsAreBlocking = true;
        }
        else
        {
            StrategyText = "统一重新编码后拼接";
            StrategyDetail = "输入参数不一致，将按下方设置统一编码每个片段后再拼接。";
            ReasonTitle = "不满足无损拼接的参数";
            ReasonIntro = "下列差异会导致统一重新编码：";
            ReasonsAreBlocking = true;
        }
    }

    private void MarkAnalysisStale()
    {
        if (Inputs.Count == 0)
        {
            Analyzed = false;
            StrategyText = "添加视频后自动分析";
            StrategyDetail = "";
            Reasons.Clear();
            HasReasons = false;
            ReasonTitle = "";
            ReasonIntro = "";
            // 列表被清空时也必须熄灭加载指示：此前 Analyzing 已被置 true 的话，
            // 这里不回落就会让页面上的 ProgressRing 一直转下去（永远等不到结果）。
            Analyzing = false;
            return;
        }
        Analyzing = true;
    }

    /// <summary>输入或参数变化后重新分析（防抖 + 取消上一次），绝不在 UI 线程做探测。</summary>
    private async Task RefreshAnalysisAsync()
    {
        if (Inputs.Count == 0)
        {
            // 先取消在飞的分析再退：否则旧分析返回后仍会走 ApplyBundleInfo/ApplyAnalysis，
            // 把「无损拼接」之类的结论写回一个已经没有任何输入的页面。
            CancelAnalyze();
            MarkAnalysisStale();
            return;
        }

        CancelAnalyze();
        var cts = new CancellationTokenSource();
        _analyzeSource = cts;
        MarkAnalysisStale();
        AnalysisError = "";

        try
        {
            VideoMergeTask? task = await EnsureTaskAsync(cts.Token);
            if (task is null)
            {
                AnalysisError = "未找到 ffprobe，无法分析输入文件。请在设置中指定媒体工具路径。";
                Analyzing = false;
                return;
            }

            VideoMergeOptions options = BuildOptions();
            (MergeCompatibility compatibility, List<MediaBundle> bundles) =
                await task.AnalyzeAsync(options, cts.Token);
            if (cts.IsCancellationRequested) return;

            _ui.RunOnUiThread(() =>
            {
                ApplyBundleInfo(bundles);
                ApplyAnalysis(compatibility);
                Analyzing = false;
            });
        }
        catch (OperationCanceledException) { /* 参数连续变更时的正常取消 */ }
        catch (Exception ex)
        {
            _ui.RunOnUiThread(() =>
            {
                Analyzing = false;
                AnalysisError = "分析失败：" + ex.Message;
            });
        }
        finally
        {
            if (ReferenceEquals(_analyzeSource, cts))
            {
                _analyzeSource = null;
                cts.Dispose();
            }
        }
    }

    private void CancelAnalyze()
    {
        CancellationTokenSource? previous = _analyzeSource;
        _analyzeSource = null;
        if (previous is null) return;
        try { previous.Cancel(); } catch (ObjectDisposedException) { /* 已释放 */ }
    }

    /// <summary>
    /// 把探测结果写回对应的输入行。
    /// 必须按路径匹配，不能按下标：<see cref="VideoMergeTask.ProbeAsync"/> 是并发探测，
    /// 返回顺序不保证与输入顺序一致，按下标会在顺序变化时把整批媒体信息贴错行。
    /// </summary>
    private void ApplyBundleInfo(List<MediaBundle> bundles)
    {
        var byPath = new Dictionary<string, MediaBundle>(StringComparer.OrdinalIgnoreCase);
        foreach (MediaBundle bundle in bundles)
            if (!string.IsNullOrEmpty(bundle.Path)) byPath[bundle.Path] = bundle;

        foreach (MergeInputViewModel input in Inputs)
            if (byPath.TryGetValue(input.Path, out MediaBundle? bundle)) input.Apply(bundle);
    }

    private async Task<VideoMergeTask?> EnsureTaskAsync(CancellationToken cancellationToken)
    {
        if (_task is not null) return _task;
        try
        {
            _task = await VideoMergeTask.CreateAsync(_settings.FfmpegPath, _settings.FfprobePath,
                                                     cancellationToken);
            return _task;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    /// <summary>设置里的工具路径变化后需要重建任务（否则仍指向旧路径）。</summary>
    public void InvalidateTools()
    {
        _task = null;
        _ = RefreshAnalysisAsync();
    }

    #endregion

    #region 初始化

    public void Initialize()
    {
        _loading = true;
        Container = _settings.MergeContainer;
        Codec = _settings.MergeCodec;
        Crf = _settings.MergeCrf;
        Preset = _settings.MergePreset;
        Fit = Fits.Contains(_settings.MergeFit) ? _settings.MergeFit : "Contain";
        AudioStrategyText = AudioStrategies.Contains(_settings.MergeAudio) ? _settings.MergeAudio : "Auto";
        AllowReencode = _settings.MergeAllowReencode;
        AutoChapters = _settings.MergeChapters;
        VerifyOutput = _settings.MergeVerify;
        DeleteSource = _settings.MergeDeleteSource && VerifyOutput;
        PartialReencode = _settings.MergePartialReencode;
        AutoFallbackOnFailure = _settings.MergeAutoFallback;
        SalvageTruncatedInputs = _settings.MergeSalvage;
        _loading = false;
        OnPropertyChanged(nameof(ChaptersAvailable));
        OnPropertyChanged(nameof(ChaptersEnabled));
        if (OutputDirectory.Length == 0) OutputDirectory = _settings.MergeOutputDir;
        SyncOutputExtension();
    }

    public void ApplyToSettings()
    {
        _settings.MergeContainer = Container;
        _settings.MergeCodec = Codec;
        _settings.MergeCrf = Crf;
        _settings.MergePreset = Preset;
        _settings.MergeFit = Fit;
        _settings.MergeAudio = AudioStrategyText;
        _settings.MergeAllowReencode = AllowReencode;
        _settings.MergeChapters = AutoChapters;
        _settings.MergeVerify = VerifyOutput;
        _settings.MergeDeleteSource = DeleteSource;
        _settings.MergePartialReencode = PartialReencode;
        _settings.MergeAutoFallback = AutoFallbackOnFailure;
        _settings.MergeSalvage = SalvageTruncatedInputs;
        if (OutputDirectory.Length > 0) _settings.MergeOutputDir = OutputDirectory;
    }

    #endregion

    #region 输入管理

    [RelayCommand]
    private async Task AddInputsAsync()
    {
        List<string> extensions = Constants.VideoExts.OrderBy(x => x, StringComparer.Ordinal).ToList();
        IReadOnlyList<string> paths = await _ui.PickFilesAsync("选择要合并的视频", extensions);
        await AddPathsAsync(paths);
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        string? folder = await _ui.PickFolderAsync("选择包含视频的文件夹");
        if (!string.IsNullOrEmpty(folder)) await AddPathsAsync(new[] { folder });
    }

    /// <summary>拖放与文件对话框共用入口；保持用户添加顺序（合并顺序即列表顺序）。</summary>
    public async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        if (IsRunning)
        {
            await _ui.ShowInfoAsync("正在合并中，请先取消当前任务再添加文件。");
            return;
        }
        if (paths.Count == 0) return;

        CollectResult collected = FileCollector.Collect(paths, recursive: false);
        List<string> videos = collected.Videos;
        if (videos.Count == 0)
        {
            await _ui.ShowErrorAsync("没有找到可合并的视频文件。");
            return;
        }

        var known = new HashSet<string>(Inputs.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        var added = new List<MergeInputViewModel>();
        foreach (string path in videos)
        {
            if (!known.Add(path)) continue;
            var item = new MergeInputViewModel(path) { ChapterTitle = "" };
            item.SetDefaultChapterTitle();
            Inputs.Add(item);
            added.Add(item);
        }

        if (added.Count == 0)
        {
            await _ui.ShowInfoAsync("这些视频已经在列表里了。");
            return;
        }

        if (OutputDirectory.Length == 0)
        {
            string? dir = Path.GetDirectoryName(added[0].Path);
            if (!string.IsNullOrEmpty(dir)) OutputDirectory = dir;
        }
        // 未手动改过名字时，按当前首段重新生成：清空后再拖入新视频也能得到新名字。
        // 默认按名称自然排序（第2集 < 第10集），用户拖拽或上移/下移后以手动顺序为准。
        SortInputsByName();

        if (!_outputNameUserEdited && OutputName.Length == 0 && Inputs.Count > 0)
            SetOutputName(SuggestOutputName(), userEdit: false);
        SyncOutputExtension();

        await ProbeInputsAsync(Inputs.ToList());
        await RefreshAnalysisAsync();

        // 已有多个文件时顺手算一次分组，用户不必再点一次按钮就能看到哪些能无损拼。
        if (Inputs.Count > 1 && !IsRunning) await GroupByCompatibilityAsync(showDialog: false);
    }

    /// <summary>
    /// 按文件名自然排序（"第2集" 排在 "第10集" 之前）。
    /// 排序只调整顺序，不移除任何项；合并顺序即列表顺序，因此排完要重新分析。
    /// </summary>
    private void SortInputsByName()
    {
        if (Inputs.Count < 2) return;
        List<MergeInputViewModel> ordered = Inputs
            .OrderBy(i => Path.GetFileName(i.Path), NaturalComparer.Instance)
            .ToList();
        for (int target = 0; target < ordered.Count; target++)
        {
            int current = Inputs.IndexOf(ordered[target]);
            if (current != target) Inputs.Move(current, target);
        }
    }

    [RelayCommand]
    private void SortByName()
    {
        if (IsRunning || Inputs.Count < 2) return;
        SortInputsByName();
        if (!_outputNameUserEdited) SetOutputName(SuggestOutputName(), userEdit: false);
        SyncOutputExtension();
        _ = RefreshAnalysisAsync();
    }

    #endregion

    #region 无损分组

    [ObservableProperty] private bool grouping;
    [ObservableProperty] private string groupSummary = "";
    [ObservableProperty] private bool hasGroups;

    /// <summary>最近一次分组结果。导出文件夹时要用它的原始顺序与组合，而不是界面投影。</summary>
    private MergeGroupingResult? _lastGrouping;

    /// <summary>导出进行中的状态与进度。</summary>
    [ObservableProperty] private bool exportingGroups;
    [ObservableProperty] private double exportProgress;
    [ObservableProperty] private string exportDetail = "";

    /// <summary>「可无损拼接的组合」结果，供界面直接渲染。</summary>
    public ObservableCollection<MergeGroupViewModel> Groups { get; } = new();

    [RelayCommand]
    private async Task DetectGroupsAsync() => await GroupByCompatibilityAsync(showDialog: true);

    /// <summary>
    /// 一键识别可无损拼接的组合。
    /// 所有判定都来自 Core 的 CompatibilityAnalyzer，界面不自行推测，
    /// 保证「这里显示能拼」与「引擎实际会走 copy 拼接」是同一个标准。
    /// </summary>
    private async Task GroupByCompatibilityAsync(bool showDialog)
    {
        if (IsRunning || Grouping || Inputs.Count < 2) return;
        Grouping = true;
        try
        {
            VideoMergeTask? task = await EnsureTaskAsync(CancellationToken.None);
            if (task is null)
            {
                if (showDialog) await _ui.ShowErrorAsync("未找到 ffprobe，无法分析媒体参数。");
                return;
            }

            List<MediaBundle> bundles = await task.ProbeAsync(Inputs.Select(i => i.Path).ToList());
            MergeGroupingResult result = MergeGrouping.Analyze(bundles);
            _lastGrouping = result;

            _ui.RunOnUiThread(() =>
            {
                // RunOnUiThread 的实现会吞掉 lambda 内的异常（只写崩溃日志），
                // 因此 Grouping 必须在这里 finally 复位：漏掉一次就是按钮永久失效 +
                // 进度圈永转，用户除了重启没有别的恢复手段。
                try
                {
                    Groups.Clear();
                    int index = 1;
                    foreach (MergeGroup group in result.Groups.Where(g => g.Items.Count > 1))
                    {
                        var vm = new MergeGroupViewModel
                        {
                            Title = "组合 " + index++,
                            Signature = group.Signature,
                            KindText = group.KindText,
                            ReasonText = group.ReasonText,
                            CountText = group.Items.Count + " 个 · 合计 " + group.TotalDurationText,
                        };
                        foreach (MediaBundle b in group.Items) vm.Files.Add(Path.GetFileName(b.Path));
                        vm.NotifyFilesChanged();
                        Groups.Add(vm);
                    }

                    HasGroups = Groups.Count > 0;
                    GroupSummary = BuildGroupSummary(result);
                }
                finally
                {
                    Grouping = false;
                }
            });
        }
        catch (Exception ex)
        {
            Grouping = false;
            GroupSummary = "分组失败：" + ex.Message;
            if (showDialog) await _ui.ShowErrorAsync(GroupSummary);
        }
    }

    /// <summary>
    /// 最近一次分组结果是否仍然对应当前输入列表。
    /// 逐项比对「分组里出现的文件」与当前输入：既比数量也比成员，
    /// 任何一个方向变了都要求重新识别。
    /// </summary>
    private bool GroupingMatchesInputs()
    {
        if (_lastGrouping is null) return false;
        var grouped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in _lastGrouping.Groups.SelectMany(g => g.Items.Select(i => i.Path)))
            grouped.Add(path);
        var current = new HashSet<string>(
            Inputs.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        return grouped.SetEquals(current);
    }

    private string BuildGroupSummary(MergeGroupingResult result)
    {
        int total = Inputs.Count;
        if (Groups.Count == 0)
            return "未能找出可无损拼接的组合：这些文件的编码参数互不相同，合并时需要重编码。";

        int mergeable = result.GroupedCount;
        var parts = new List<string>();
        if (result.DirectGroups > 0) parts.Add(result.DirectGroups + " 组可直接拼接");
        if (result.RemuxGroups > 0) parts.Add(result.RemuxGroups + " 组需重封装对齐");

        string text = "找到 " + Groups.Count + " 组可无损拼接的组合（" + string.Join("，", parts) + "），"
                    + "覆盖 " + mergeable + " / " + total + " 个文件。";
        int leftover = total - mergeable;
        if (leftover > 0)
            text += " 其余 " + leftover + " 个无法与任何组合无损拼接，合并它们需要重编码。";
        return text;
    }

    /// <summary>
    /// 把识别出来的无损组合各分配到一个独立文件夹，方便逐个检查。
    ///
    /// 设计上的三条约束：
    /// <list type="number">
    /// <item>只<b>复制</b>，绝不移动或删除源文件——这是检查用的副本，原库必须保持原样；</item>
    /// <item>先出计划再动手：文件夹名、总大小、空间是否够，全部在开始复制之前算清楚并显示；</item>
    /// <item>目标位置已有同名目录时自动改名，绝不复用别人的文件夹。</item>
    /// </list>
    /// </summary>
    [RelayCommand]
    private async Task ExportGroupFoldersAsync()
    {
        if (IsRunning || Grouping || ExportingGroups) return;
        if (_lastGrouping is null || Groups.Count == 0)
        {
            await _ui.ShowInfoAsync("请先点「识别无损组合」，有结果后才能分配文件夹。");
            return;
        }

        // 分组结果必须与当前输入列表一致。
        // 用户改了输入却不重新识别时，导出的会是上一批文件的组合——
        // 那等于把过期结论当成事实写进文件夹里。
        // 注意不能只比数量、也不能只比一个方向：新增文件和拖拽重排都能让
        // 数量相等或变多，却让 _lastGrouping 里的路径与当前列表完全对不上，
        // 而导出会对这些路径执行真正的 File.Move。
        if (!GroupingMatchesInputs())
        {
            await _ui.ShowInfoAsync("输入列表已经变化，请重新点「识别无损组合」再导出。");
            return;
        }

        string? target = await _ui.PickFolderAsync("选择导出位置（每个组合会在里面建一个文件夹）");
        if (string.IsNullOrEmpty(target)) return;

        GroupFolderPlan plan;
        try
        {
            plan = GroupFolderPlanner.Build(_lastGrouping, target);
        }
        catch (Exception ex)
        {
            await _ui.ShowErrorAsync("无法生成导出计划：" + ex.Message);
            return;
        }

        if (plan.Folders.Count == 0)
        {
            await _ui.ShowInfoAsync("没有可导出的组合。");
            return;
        }
        if (!string.IsNullOrEmpty(plan.SpaceProblem))
        {
            await _ui.ShowErrorAsync(plan.SpaceProblem);
            return;
        }

        // 动手之前把「要做什么、代价是什么、能不能撤回」一次说清。
        // 移动是不可逆操作，这里多写一句代价说明，比事后解释有用。
        string confirm =
            $"将把 {plan.Folders.Count} 个组合移动到各自的文件夹：" +
            $"{plan.FileCount} 个文件，合计 {plan.TotalSizeText}。\n\n" +
            $"位置：{plan.TargetRoot}\n\n" +
            (plan.HasCrossVolumeMove
                ? $"其中有 {FileSizeText(plan.CrossVolumeBytes)} 需要跨磁盘复制搬运（会慢一些，" +
                  "复制并核对完成后才会删除原文件；中途失败原文件不受影响）。\n\n"
                : "目标位置与素材在同一磁盘，移动只是改名，瞬时完成。\n\n") +
            "注意：这是移动，不是复制——原位置将不再保留这些文件。继续吗？";
        if (!await _ui.AskYesNoAsync(confirm)) return;

        ExportingGroups = true;
        ExportProgress = 0;
        ExportDetail = "准备复制…";
        try
        {
            var progress = new Progress<GroupFolderProgress>(p => _ui.RunOnUiThread(() =>
            {
                ExportProgress = Math.Clamp(p.Percent, 0, 100);
                ExportDetail = string.Format(CultureInfo.InvariantCulture,
                    "组合 {0}/{1}：{2}", Math.Min(p.FolderIndex + 1, p.FolderCount), p.FolderCount,
                    string.IsNullOrEmpty(p.FileName) ? p.FolderName : p.FileName);
            }));

            GroupFolderResult result = await GroupFolderExporter.ExecuteAsync(plan, progress);
            if (!result.Ok)
            {
                await _ui.ShowErrorAsync(result.Error);
                return;
            }

            // 移动后输入列表里那些路径已经失效，必须重新探测，
            // 否则界面上会留着一串「文件不存在」的旧行。
            await RefreshAfterMoveAsync(result);

            string message = result.SummaryText;
            if (result.Notices.Count > 0)
                message += "\n\n需要注意：\n· " + string.Join("\n· ", result.Notices.Take(10));
            await _ui.ShowInfoAsync(message);
            try { _ui.OpenFolder(result.TargetRoot); } catch { /* 打不开文件夹不算失败 */ }
        }
        catch (Exception ex)
        {
            await _ui.ShowErrorAsync("导出失败：" + ex.Message);
        }
        finally
        {
            ExportingGroups = false;
            ExportProgress = 0;
            ExportDetail = "";
        }
    }

    /// <summary>
    /// 移动完成后同步界面状态。
    ///
    /// 从输入列表里移除已经搬走的文件，并让分组结果失效——
    /// 分组是按旧路径算出来的，路径变了就不该再被当作有效结论使用。
    /// 这一步不做的话，界面会留着一串指向不存在文件的行。
    /// </summary>
    private async Task RefreshAfterMoveAsync(GroupFolderResult result)
    {
        var moved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_lastGrouping is not null)
            foreach (MergeGroup group in _lastGrouping.Groups)
                foreach (MediaBundle bundle in group.Items)
                    moved.Add(bundle.Path);

        _lastGrouping = null;
        Groups.Clear();
        HasGroups = false;
        GroupSummary = result.SummaryText;

        for (int i = Inputs.Count - 1; i >= 0; i--)
            if (!File.Exists(Inputs[i].Path)) Inputs.RemoveAt(i);

        _selected.Clear();
        await RefreshAnalysisAsync();
    }

    /// <summary>把字节数写成可读文本。</summary>
    private static string FileSizeText(long bytes) => Formatting.FormatSize(bytes);

    /// <summary>把某一组设为当前合并内容：只保留该组文件，顺序按组内顺序。</summary>
    [RelayCommand]
    private void UseGroup(MergeGroupViewModel? group)
    {
        if (group is null || IsRunning) return;
        List<MergeInputViewModel> keep = new();
        foreach (string name in group.Files)
        {
            MergeInputViewModel? hit = Inputs.FirstOrDefault(
                i => string.Equals(Path.GetFileName(i.Path), name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) keep.Add(hit);
        }
        if (keep.Count < 2) return;

        Inputs.Clear();
        foreach (MergeInputViewModel item in keep) Inputs.Add(item);
        _selected.Clear();
        if (!_outputNameUserEdited) SetOutputName(SuggestOutputName(), userEdit: false);
        SyncOutputExtension();
        _ = RefreshAnalysisAsync();
    }

    private async Task ProbeInputsAsync(IReadOnlyList<MergeInputViewModel> items)
    {
        if (items.Count == 0) return;
        VideoMergeTask? task = await EnsureTaskAsync(CancellationToken.None);
        if (task is null)
        {
            foreach (MergeInputViewModel item in items)
                item.Fail("未找到 ffprobe，无法读取媒体信息");
            return;
        }
        try
        {
            List<MediaBundle> bundles = await task.ProbeAsync(items.Select(i => i.Path).ToList());
            var byPath = new Dictionary<string, MediaBundle>(StringComparer.OrdinalIgnoreCase);
            foreach (MediaBundle bundle in bundles)
                if (!string.IsNullOrEmpty(bundle.Path)) byPath[bundle.Path] = bundle;
            _ui.RunOnUiThread(() =>
            {
                foreach (MergeInputViewModel item in items)
                    if (byPath.TryGetValue(item.Path, out MediaBundle? bundle)) item.Apply(bundle);
                    else item.Fail("探测未返回该文件的信息");
            });
        }
        catch (Exception ex)
        {
            foreach (MergeInputViewModel item in items) item.Fail("探测失败：" + ex.Message);
        }
    }

    /// <summary>由代码写入输出名并抑制"用户编辑"标记，避免自动生成的名字被当成手动输入。</summary>
    private void SetOutputName(string value, bool userEdit)
    {
        _suppressOutputNameEdit = !userEdit;
        try { OutputName = value; }
        finally { _suppressOutputNameEdit = false; }
        if (!userEdit) _outputNameUserEdited = false;
    }

    private string SuggestOutputName()
    {
        if (Inputs.Count == 0) return "merged";
        string first = Inputs[0].DefaultChapterTitle;
        return Inputs.Count == 1 ? first + "_merged" : first + "_等" + Inputs.Count + "个视频";
    }

    /// <summary>
    /// 列表顺序被拖拽改变后调用。合并顺序即 Core 的输入顺序，
    /// 会直接影响兼容性判定与章节时间轴，必须重新分析。
    /// </summary>
    /// <summary>
    /// 按界面实际顺序重排 <see cref="Inputs"/>。
    ///
    /// 拖拽排序后必须调用：ListView 的内部重排只改容器，不会保证回写到绑定的集合；
    /// 而 <see cref="Inputs"/> 才是合并顺序的唯一来源。不同步的话，
    /// 视觉上新顺序会维持到下一次布局，然后被集合的真实顺序弹回去——用户看到的就是「拖了没反应」。
    ///
    /// 只接受「与当前集合元素完全相同、但顺序不同」的排列；
    /// 长度或成员不一致说明调用方传错了数据，直接忽略而不是冒险打乱列表。
    /// </summary>
    public void SyncOrderFrom(IReadOnlyList<MergeInputViewModel> order)
    {
        if (IsRunning || order.Count != Inputs.Count) return;
        if (order.Distinct().Count() != order.Count) return;   // 有重复项，来源不可信

        List<MergeInputViewModel> current = Inputs.ToList();
        if (SubFusion.Core.Services.ListReorder.IsSameOrder(current, order)) return;

        // 搬移序列由 Core 计算：这段逻辑有自检覆盖，排序是合并顺序的唯一来源，不能靠肉眼核对。
        foreach (SubFusion.Core.Services.MoveStep step in
                 SubFusion.Core.Services.ListReorder.ComputeMoves(current, order))
            Inputs.Move(step.From, step.To);
        OnInputOrderChanged();
    }

    public void OnInputOrderChanged()
    {
        if (IsRunning) return;
        // 用户没有手动改过名字时，让输出名跟上新的首段，避免名字与顺序脱节。
        if (!_outputNameUserEdited) SetOutputName(SuggestOutputName(), userEdit: false);
        SyncOutputExtension();
        _ = RefreshAnalysisAsync();
    }

    public void SetSelection(IEnumerable<MergeInputViewModel> selected)
    {
        _selected = selected.Distinct().ToList();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        MoveToTopCommand.NotifyCanExecuteChanged();
        MoveToBottomCommand.NotifyCanExecuteChanged();
    }

    private bool HasSelection() => _selected.Count > 0 && !IsRunning;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveSelected()
    {
        if (IsRunning) return;
        foreach (MergeInputViewModel item in _selected.ToList()) Inputs.Remove(item);
        _selected.Clear();
        _ = RefreshAnalysisAsync();
    }

    [RelayCommand]
    private void ClearInputs()
    {
        if (IsRunning) return;
        Inputs.Clear();
        _selected.Clear();
        Analyzed = false;
        Reasons.Clear();
        HasReasons = false;
        StrategyText = "添加视频后自动分析";
        StrategyDetail = "";
        // 输出名必须一起清空。否则下次拖入新视频时 "OutputName.Length == 0" 不成立，
        // 会继续沿用上一批视频生成的名字（表现为"输出文件名不会重新写入"）。
        SetOutputName("", userEdit: false);
        OutputPath = "";
        _outputNameUserEdited = false;
    }

    /// <summary>
    /// 置顶：把选中项整体移到最前，保持它们之间的相对顺序。
    /// 多选时必须整体搬运而不是逐个上移——逐个上移会把选中项的顺序反过来，
    /// 而列表顺序就是合并顺序，反过来等于改掉了用户看到的结果。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMoveToTop))]
    private void MoveToTop()
    {
        if (IsRunning || _selected.Count == 0) return;
        List<MergeInputViewModel> moving = Inputs.Where(_selected.Contains).ToList();
        if (moving.Count == 0) return;
        // 已经在最前面时不做任何改动，避免触发无意义的重分析。
        if (Inputs.Take(moving.Count).SequenceEqual(moving)) return;
        MoveSelectedTo(moving, 0);
    }

    /// <summary>置底：把选中项整体移到最后，保持相对顺序。</summary>
    [RelayCommand(CanExecute = nameof(CanMoveToBottom))]
    private void MoveToBottom()
    {
        if (IsRunning || _selected.Count == 0) return;
        List<MergeInputViewModel> moving = Inputs.Where(_selected.Contains).ToList();
        if (moving.Count == 0) return;
        if (Inputs.Skip(Inputs.Count - moving.Count).SequenceEqual(moving)) return;
        MoveSelectedTo(moving, Inputs.Count - moving.Count);
    }

    /// <summary>把一组条目依次搬到 <paramref name="startIndex"/> 起的位置，保持组内顺序。</summary>
    private void MoveSelectedTo(List<MergeInputViewModel> moving, int startIndex)
    {
        // 先算出目标顺序，再交给 Core 求搬移序列：与拖拽排序走同一条逻辑，
        // 两处若各写一套，迟早会出现「拖拽结果」和「按钮结果」不一致。
        var target = Inputs.ToList();
        target.RemoveAll(moving.Contains);
        target.InsertRange(Math.Clamp(startIndex, 0, target.Count), moving);
        SyncOrderFrom(target);
    }

    private bool CanMoveToTop() => HasSelection() && Inputs.IndexOf(_selected[0]) > 0;

    private bool CanMoveToBottom() => HasSelection() && _selected.Count > 0 &&
                                      Inputs.IndexOf(_selected[^1]) < Inputs.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        if (_selected.Count == 0) return;
        int index = Inputs.IndexOf(_selected[0]);
        if (index <= 0) return;
        Inputs.Move(index, index - 1);
        _ = RefreshAnalysisAsync();
    }

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        if (_selected.Count == 0) return;
        int index = Inputs.IndexOf(_selected[0]);
        if (index < 0 || index >= Inputs.Count - 1) return;
        Inputs.Move(index, index + 1);
        _ = RefreshAnalysisAsync();
    }

    private bool CanMoveUp() => HasSelection() && _selected.Count > 0 && Inputs.IndexOf(_selected[0]) > 0;
    private bool CanMoveDown() => HasSelection() && _selected.Count > 0 &&
                                  Inputs.IndexOf(_selected[0]) is var i && i >= 0 && i < Inputs.Count - 1;

    #endregion

    #region 输出路径

    partial void OnOutputDirectoryChanged(string value)
    {
        OutputPath = CombineOutput();
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputNameChanged(string value)
    {
        // 只有用户真正编辑（非初始化/自动生成/清空）才标记为手动改名。
        if (!_loading && !_suppressOutputNameEdit) _outputNameUserEdited = value.Trim().Length > 0;
        OutputPath = CombineOutput();
        StartCommand.NotifyCanExecuteChanged();
    }

    private string CombineOutput()
    {
        string dir = OutputDirectory?.Trim() ?? "";
        string name = OutputName?.Trim() ?? "";
        if (dir.Length == 0 || name.Length == 0) return "";
        string extension = ChaptersAvailable ? ".mkv" : ".mp4";
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            string withoutExt = Path.HasExtension(name)
                ? name[..^Path.GetExtension(name).Length] : name;
            name = withoutExt + extension;
        }
        try { return Path.Combine(dir, name); }
        catch { return ""; }
    }

    private void SyncOutputExtension()
    {
        string next = CombineOutput();
        if (!string.Equals(OutputPath, next, StringComparison.Ordinal)) OutputPath = next;
    }

    [RelayCommand]
    private async Task ChooseOutputDirectoryAsync()
    {
        string? folder = await _ui.PickFolderAsync("选择输出目录");
        if (string.IsNullOrEmpty(folder)) return;
        OutputDirectory = folder;
        if (OutputName.Length == 0) SetOutputName(SuggestOutputName(), userEdit: false);
    }

    [RelayCommand]
    private async Task ChooseOutputFileAsync()
    {
        string? path = await _ui.PickSaveVideoAsync("选择输出文件", SuggestOutputName(),
                                                    ChaptersAvailable ? ".mkv" : ".mp4",
                                                    OutputDirectory);
        if (string.IsNullOrEmpty(path)) return;
        OutputDirectory = Path.GetDirectoryName(path) ?? OutputDirectory;
        OutputName = Path.GetFileName(path);
    }

    #endregion

    #region 执行

    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool canCancel;
    [ObservableProperty] private double progress;
    [ObservableProperty] private string stageText = "空闲";
    [ObservableProperty] private string progressDetail = "";

    /// <summary>当前处理速度（如 "2.4x"）；未知时为空串。</summary>
    [ObservableProperty] private string speedText = "";
    /// <summary>预计剩余时间（如 "约 3 分 20 秒"）；无法估算时说明原因。</summary>
    [ObservableProperty] private string etaText = "";
    /// <summary>是否已经有可显示的进度信息（决定进度区是否出现在界面）。</summary>
    [ObservableProperty] private bool hasProgressInfo;
    /// <summary>进度条是否处于"不确定"状态（刚开始、还没有可信百分比时）。</summary>
    [ObservableProperty] private bool isIndeterminate;
    /// <summary>形如 "42% · 第 12 秒 / 共 6 分 10 秒" 的位置说明。</summary>
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string resultText = "";
    [ObservableProperty] private bool hasResult;
    [ObservableProperty] private bool resultOk;

    partial void OnIsRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        MoveToTopCommand.NotifyCanExecuteChanged();
        MoveToBottomCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() => !IsRunning && Inputs.Count > 0 && OutputPath.Length > 0;

    private VideoMergeOptions BuildOptions()
        => new()
        {
            Inputs = Inputs.Select(i => i.Path).ToList(),
            OutputPath = OutputPath,
            Container = Container,
            AllowReencode = AllowReencode,
            VideoCodec = Codec,
            Crf = (int)Crf,
            Preset = Preset,
            Fit = Fit switch
            {
                "Cover" => FitMode.Cover,
                "Stretch" => FitMode.Stretch,
                _ => FitMode.Contain,
            },
            Audio = AudioStrategyText switch
            {
                "Reencode" => SubFusion.Core.Merge.AudioStrategy.Reencode,
                "Copy" => SubFusion.Core.Merge.AudioStrategy.Copy,
                _ => SubFusion.Core.Merge.AudioStrategy.Auto,
            },
            AutoChapters = ChaptersEnabled,
            ChapterTitles = ChaptersEnabled
                ? Inputs.Select(i => string.IsNullOrWhiteSpace(i.ChapterTitle) ? i.DefaultChapterTitle : i.ChapterTitle.Trim()).ToList()
                : new List<string>(),
            Overwrite = Overwrite,
            VerifyOutput = VerifyOutput,
            PartialReencode = PartialReencode,
            AutoFallbackOnFailure = AutoFallbackOnFailure,
            SalvageTruncatedInputs = SalvageTruncatedInputs,
            // 删源必须先证明输出完好，因此这里强制与校验联动。
            DeleteSourceAfterVerify = DeleteSource && VerifyOutput,
        };

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        VideoMergeOptions options = BuildOptions();
        List<string> errors = options.Validate();
        if (errors.Count > 0)
        {
            await _ui.ShowErrorAsync("参数不完整：\n\n" + string.Join("\n", errors.Select(e => "· " + e)));
            return;
        }
        // 仅时间轴差异可以无损重封装，不需要用户开启「允许重新编码」。
        if (!CanStreamCopy && !CanRemuxOnly && !AllowReencode)
        {
            await _ui.ShowErrorAsync("输入参数不一致，且「允许重新编码」已关闭，无法合并。\n" +
                                     "请开启该选项，或调整输入的编码参数。");
            return;
        }

        VideoMergeTask? task = await EnsureTaskAsync(CancellationToken.None);
        if (task is null)
        {
            await _ui.ShowErrorAsync("未找到 ffmpeg / ffprobe，请在「设置 → 媒体工具」中指定路径。");
            return;
        }

        IsRunning = true;
        _progressClosed = false;
        _trailingScheduled = false;
        _pendingProgress = null;
        _progressScheduled = false;
        CanCancel = true;
        HasResult = false;
        ResultText = "";
        Progress = 0;
        StageText = "准备中…";
        ProgressDetail = "";
        HasProgressInfo = false;
        SpeedText = "";
        EtaText = "";
        ProgressText = "";
        IsIndeterminate = true;

        _record = _tasks.Add(TaskKind.VideoMerge,
            $"视频合并 · {options.Inputs.Count} 段",
            $"{options.Inputs.Count} 段 · {(CanStreamCopy ? "无损拼接" : CanRemuxOnly ? "无损重封装" : "统一重新编码")}",
            options.OutputPath);

        // 任务中心可能已在排队/准备阶段取消：先检查，避免把已取消任务交给 Core。
        if (_record.Cancellation.IsCancellationRequested)
        {
            _tasks.Finish(_record, TaskState.Cancelled, "已取消");
            StageText = "已取消";
            ResultText = "任务已取消。";
            HasResult = true;
            ResultOk = false;
            IsRunning = false;
            CanCancel = false;
            _record = null;
            return;
        }

        _tasks.Update(_record, r =>
        {
            r.State = TaskState.Running;
            r.Stage = "准备中";
            r.Message = StrategyText;
        });

        _runSource = new CancellationTokenSource();
        // 关键：把任务记录的取消令牌与本次执行令牌联动，任务中心 Cancel 才能真正终止 Core。
        _runLinked = CancellationTokenSource.CreateLinkedTokenSource(
            _runSource.Token, _record.Cancellation.Token);

        try
        {
            MergeResult result = await task.RunAsync(options, new Progress<MergeProgress>(OnProgress),
                                                     _runLinked.Token);
            AppendDiagnostics(result);
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            StageText = "已取消";
            ResultText = "任务已取消。";
            HasResult = true;
            ResultOk = false;
            FinishRecord(TaskState.Cancelled, "已取消");
        }
        catch (Exception ex)
        {
            StageText = "失败";
            ResultText = "合并失败：" + ex.Message;
            HasResult = true;
            ResultOk = false;
            FinishRecord(TaskState.Failed, ResultText);
        }
        finally
        {
            IsRunning = false;
            CanCancel = false;
            _runLinked?.Dispose();
            _runLinked = null;
            _runSource?.Dispose();
            _runSource = null;
            _record = null;
        }
    }

    /// <summary>
    /// 进度回调（任意线程）。Core 每几百毫秒回调一次；这里再合并到约 10 Hz，
    /// 避免高码率任务把 UI 线程刷爆——只保留最后一次快照，由 UI 线程统一采纳。
    /// </summary>
    /// <summary>
    /// 安排一次延迟刷新，把「因为太快被节流挡下」的进度补发出去。
    /// 只排一次，且不会晚于节流间隔——既保证最后一条一定能显示，
    /// 也保证不会因为频繁排队把 UI 线程拖住。
    /// </summary>
    private void ScheduleTrailingFlush(TimeSpan waited)
    {
        if (_trailingScheduled) return;
        _trailingScheduled = true;
        double delayMs = Math.Max(1, ProgressPacer.MinInterval.TotalMilliseconds - waited.TotalMilliseconds);
        _ = Task.Delay(TimeSpan.FromMilliseconds(delayMs)).ContinueWith(_ =>
        {
            _ui.RunOnUiThread(() =>
            {
                _trailingScheduled = false;
                FlushProgress();
            });
        }, TaskScheduler.Default);
    }

    private bool _trailingScheduled;

    private void OnProgress(MergeProgress progress)
    {
        if (_progressClosed) return;
        _pendingProgress = progress;
        if (_progressScheduled) return;
        _progressScheduled = true;
        _ui.RunOnUiThread(FlushProgress);
    }

    private void FlushProgress()
    {
        // 任务已定稿：排队中的旧快照一律丢弃，避免把「已完成 / 100%」覆盖回 99.9%。
        if (_progressClosed) { _pendingProgress = null; _progressScheduled = false; return; }

        MergeProgress? snapshot = _pendingProgress;
        _pendingProgress = null;
        _progressScheduled = false;
        if (snapshot is null) return;

        // 节流不能把终态一起丢掉。
        //
        // 这里曾经是「不到 100ms 就 return」，而被丢掉的那一条恰好经常是最后一条
        // （「合并完成 / 100%」）：任务其实已经成功结束、文件也已经写好，
        // 界面却永远停在前一条「写入输出文件… 99%」上。
        // 从用户角度这就是卡死——而且是成功之后才卡死的那种，最让人困惑。
        // 现在终态无条件送达，普通进度照旧节流。
        bool terminal = snapshot.Stage is MergeStage.Completed or MergeStage.Failed or MergeStage.Cancelled
                        || ProgressPacer.IsTerminalPercent(snapshot.Percent);
        DateTime now = DateTime.UtcNow;
        if (!ProgressPacer.ShouldDeliver(now - _lastProgressFlush, terminal))
        {
            // 尾随补发：不直接丢弃，而是留到间隔过去之后再送。
            //
            // 早期写法是「不到 100ms 就 return」，被丢掉的那一条如果正好是最后一条
            // 有意义的进度，界面就永远停在更早的文案上。
            // 现在把快照留下并安排一次延迟刷新，既保住节流，也不丢信息。
            if (_pendingProgress is null) _pendingProgress = snapshot;
            ScheduleTrailingFlush(now - _lastProgressFlush);
            return;
        }
        _lastProgressFlush = now;

        Progress = Math.Clamp(snapshot.Percent, 0, 100);
        StageText = DescribeStage(snapshot);
        ProgressDetail = snapshot.Message;

        // 速度与预计时间：Core 已经把这些数据传上来，之前只是没显示。
        // 重编码动辄几十分钟，没有「还要多久」会让人不知道程序是不是卡住了。
        SpeedText = snapshot.Speed > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.##}x", snapshot.Speed)
            : "";
        EtaText = FormatEta(snapshot);
        HasProgressInfo = true;
        // 只有在「进度确实为 0、且当前这一步没有速度数据」时才用不确定态。
        // 重编码进行中速度为 0 说明 ffmpeg 还没吐第一批 progress 行，
        // 此时显示确定态会让人盯着一个不动的百分比，和卡死没有区别。
        IsIndeterminate = snapshot.Percent <= 0.01 && snapshot.Speed <= 0;
        // 收尾/搬运阶段用一位小数：99.0 与 99.7 在整数显示下完全一样，
        // 而用户正是靠这个数字判断"到底在动还是卡了"。
        ProgressText = snapshot.Percent > 98 && snapshot.Percent < 100
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.0}%", snapshot.Percent)
            : string.Format(CultureInfo.InvariantCulture, "{0:0.#}%", snapshot.Percent);

        // 阶段之间（探测 / 核实 / 待命）本来就没有百分比，
        // 这里明说"正在准备"而不是让数字停在原处冒充进度。
        // 例外是 Completed：跨盘搬运输出时它是真有百分比的（99→99.9），
        // 再补一句「此阶段无进度百分比」就自相矛盾了。
        if (snapshot.Speed <= 0 && snapshot.Percent < 99 &&
            snapshot.Stage is MergeStage.Merging or MergeStage.Verifying or MergeStage.Probing)
            ProgressDetail = string.IsNullOrEmpty(snapshot.Message)
                ? "正在处理（此阶段无进度百分比）…"
                : snapshot.Message + "（此阶段无进度百分比）";

        if (_record is not null)
        {
            TaskRecord record = _record;
            _tasks.Update(record, r =>
            {
                r.Progress = Math.Clamp(snapshot.Percent, 0, 100);
                r.Stage = DescribeStage(snapshot);
                r.Message = snapshot.Message;
            });
        }
    }

    /// <summary>
    /// 把剩余秒数整理成人话。
    /// 优先用 Core 算出的 ETA（它知道当前这一段还剩多少）；拿不到时按整体百分比线性推算，
    /// 两者都没有就明说"无法估算"，而不是显示一个假装精确的数字。
    /// </summary>
    private static string FormatEta(MergeProgress progress)
    {
        // 收尾阶段（写章节/校验/搬运输出）本来就没有速度数据，也不该有。
        // 此时硬凑一个「计算中…」只会让人以为还在算，实际进度是确定的。
        if (progress.Stage is MergeStage.WritingChapters or MergeStage.Verifying or MergeStage.Completed)
            return "";
        if (progress.Eta is > 0 and < 86400)
            return "预计剩余 " + FormatDuration(progress.Eta.Value);
        if (progress.Percent >= 100) return "";
        return progress.Percent > 2 ? "预计剩余 计算中…" : "";
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60) return Math.Max(1, (int)Math.Round(seconds)) + " 秒";
        if (seconds < 3600)
        {
            int m = (int)(seconds / 60);
            int s = (int)Math.Round(seconds - m * 60);
            if (s == 60) { m++; s = 0; }
            return s == 0 ? m + " 分钟" : $"{m} 分 {s} 秒";
        }
        int h = (int)(seconds / 3600);
        int rm = (int)Math.Round((seconds - h * 3600) / 60);
        return rm == 0 ? h + " 小时" : $"{h} 小时 {rm} 分";
    }

    private static string DescribeStage(MergeProgress progress)
    {
        string stage = progress.Stage switch
        {
            MergeStage.Validating => "校验参数",
            MergeStage.Probing => "读取媒体信息",
            MergeStage.Planning => "生成合并方案",
            MergeStage.Merging => "合并中",
            MergeStage.WritingChapters => "写入章节",
            MergeStage.Verifying => "校验输出",
            MergeStage.Completed => "已完成",
            MergeStage.Cancelled => "已取消",
            MergeStage.Failed => "失败",
            _ => "准备中",
        };
        if (progress.StepCount > 1 && progress.StepIndex > 0)
            return $"{stage}（第 {progress.StepIndex}/{progress.StepCount} 段）";
        return stage;
    }

    /// <summary>
    /// 任务结束时的进度区定稿。
    ///
    /// 必须显式改写这几项，不能只设 <see cref="Progress"/>：
    /// 它们是上一阶段留下的快照，任务结束后不会再有任何进度回调来覆盖它们。
    /// 少改一项，界面就会定格在「已完成 / 写入输出文件… / 99% / 预计剩余 计算中…」
    /// 这种自相矛盾的画面上——文件明明已经写好了。
    /// </summary>
    private void SettleProgress(bool ok, bool cancelled, string detail)
    {
        _progressClosed = true;
        _pendingProgress = null;
        IsIndeterminate = false;
        HasProgressInfo = true;
        SpeedText = "";
        EtaText = "";
        ProgressDetail = detail;
        if (ok)
        {
            Progress = 100;
            ProgressText = "100%";
        }
        else
        {
            // 失败/取消时停在真实进度上，但把「还要多久」这类预测清掉：
            // 任务已经结束，再说"预计剩余"就是错的。
            ProgressText = string.Format(CultureInfo.InvariantCulture, "{0:0.#}%", Progress);
        }
        _ = cancelled;
    }

    private void ShowResult(MergeResult result)
    {
        HasResult = true;
        ResultOk = result.Ok;
        Progress = result.Ok ? 100 : Progress;
        StageText = result.Ok ? "已完成" : result.Status == MergeStatus.Cancelled ? "已取消" : "失败";
        SettleProgress(result.Ok, result.Status == MergeStatus.Cancelled,
            result.Ok
                ? $"{result.SummaryText}"
                : result.Status == MergeStatus.Cancelled ? "任务已取消" : "任务失败");
        ResultText = result.Ok
            ? $"{result.SummaryText}\n输出：{result.OutputPath}\n用时 {result.ElapsedSeconds:0.#} 秒" +
              (result.Notices.Count > 0 ? "\n" + string.Join("\n", result.Notices) : "")
            : (result.Status == MergeStatus.Cancelled ? "任务已取消。" : "合并失败：" + result.Error);

        if (result.Ok)
        {
            // 完成后刷新输入信息（输出已存在，章节标题可能被 Core 校正过）
            if (OutputDirectory.Length > 0) _ = RefreshAnalysisAsync();
            FinishRecord(TaskState.Completed, result.SummaryText);
        }
        else
        {
            FinishRecord(result.Status == MergeStatus.Cancelled ? TaskState.Cancelled : TaskState.Failed,
                         result.Ok ? result.SummaryText : result.Error);
        }
    }

    /// <summary>
    /// 把 Core 返回的诊断日志（ffmpeg 命令、输出、章节校验）写进任务中心，
    /// 任务中心因此能展示真实诊断，而不是只有一句状态文本。
    /// </summary>
    private void AppendDiagnostics(MergeResult result)
    {
        if (_record is null) return;
        TaskRecord record = _record;
        var lines = new List<string>
        {
            $"[策略] {(result.VideoCopied ? "无损拼接（stream copy）" : "统一重新编码")}" +
            $"{(result.AudioCopied ? " · 音频复制" : " · 音频重编码")}",
        };
        if (result.Compatibility.Count > 0)
            foreach (MergeReason reason in result.Compatibility) lines.Add("[不可无损] " + reason.Detail);
        foreach (string note in result.Notices) lines.Add("[提示] " + note);
        foreach (ChapterEntry chapter in result.Chapters)
            lines.Add($"[章节] {chapter.StartText} → {chapter.EndText}  {chapter.Title}");
        lines.AddRange(result.Diagnostics);
        lines.Add($"[结果] {result.SummaryText}");
        if (!result.Ok) lines.Add("[错误] " + result.Error);
        lines.Add($"[耗时] {result.ElapsedSeconds:0.#} 秒");

        _tasks.Update(record, r =>
        {
            r.Detail = result.SummaryText;
            r.VerifyFailed = !result.Ok;
        });
        foreach (string line in lines) _tasks.AppendLog(record, line);
    }

    private void FinishRecord(TaskState state, string message)
    {
        if (_record is null) return;
        // 取消路径会 finish 两次：Cancel() 里先把记录切到「已取消」，
        // 随后 Core 抛 OperationCanceledException 又会走到这里。
        // 没有终态守卫时第二次会重写 FinishedAt，任务中心的「用时」被凭空拉长。
        // 裁剪页与压制页早已有同样的守卫，这里补齐以保持一致。
        if (_record.State is TaskState.Completed or TaskState.Failed or TaskState.Cancelled) return;
        TaskRecord record = _record;
        _tasks.Update(record, r => r.Result = null);
        _tasks.Finish(record, state,
            state == TaskState.Completed ? message : (message.Length == 0 ? record.Stage : message));
    }

    [RelayCommand]
    private void Cancel()
    {
        if (!IsRunning) return;
        // 先取消任务记录（它会通过 linked token 触发 Core），再取消本地令牌兜底。
        if (_record is not null)
        {
            try { _record.Cancellation.Cancel(); } catch (ObjectDisposedException) { /* 刚好收尾 */ }
            // 立即把任务中心状态切到已取消，不等 Core 慢任务返回。
            _tasks.Finish(_record, TaskState.Cancelled, "已取消");
        }
        try { _runSource?.Cancel(); } catch (ObjectDisposedException) { /* 刚好收尾 */ }
        // 取消是协作式的：Core 要等 ffmpeg 退出才返回，这期间它仍会回推进度。
        // 不在这里关掉进度通道，界面就会继续往前跑，甚至把 StageText 写回「合并中…」，
        // 用户看到的是「点了取消却还在动」。这里定格在当前进度上。
        _progressClosed = true;
        _pendingProgress = null;
        IsIndeterminate = false;
        StageText = "正在取消…";
        ProgressDetail = "正在等待当前步骤安全退出…";
        SpeedText = "";
        EtaText = "";
        CanCancel = false;
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        string dir = OutputPath.Length > 0 ? (Path.GetDirectoryName(OutputPath) ?? "") : OutputDirectory;
        if (dir.Length > 0 && Directory.Exists(dir)) _ui.OpenFolder(dir);
        else _ui.ShowInfoAsync("输出目录还不存在。");
    }

    #endregion

    /// <summary>页面离开或窗口关闭时调用：取消分析、取消运行。</summary>
    public void Shutdown()
    {
        CancelAnalyze();
        if (_record is not null)
            try { _record.Cancellation.Cancel(); } catch (ObjectDisposedException) { /* 忽略 */ }
        try { _runSource?.Cancel(); } catch (ObjectDisposedException) { /* 忽略 */ }
        _task = null;
    }
}
