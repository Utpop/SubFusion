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

    private bool _loading;

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
    [ObservableProperty] private bool hasReasons;
    [ObservableProperty] private string analysisError = "";

    public ObservableCollection<string> Reasons { get; } = new();

    /// <summary>实际会采用的策略描述（无损拼接 / 统一重新编码 / 被禁止）。</summary>
    private void ApplyAnalysis(MergeCompatibility compatibility)
    {
        Analyzed = true;
        CanStreamCopy = compatibility.CanStreamCopy;
        Reasons.Clear();
        foreach (MergeReason reason in compatibility.Reasons) Reasons.Add(reason.Detail);
        HasReasons = Reasons.Count > 0;

        if (compatibility.CanStreamCopy)
        {
            StrategyText = "无损拼接（concat + stream copy）";
            StrategyDetail = "所有输入的编码、分辨率、帧率、色彩与音频参数一致，直接拼接，不重新编码。";
        }
        else if (!AllowReencode)
        {
            StrategyText = "无法合并：已禁止重新编码";
            StrategyDetail = "输入不满足无损拼接条件，且「允许重新编码」已关闭。";
        }
        else
        {
            StrategyText = "统一重新编码后拼接";
            StrategyDetail = "输入参数不一致，将按下方设置统一编码每个片段后再拼接。";
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
            return;
        }
        Analyzing = true;
    }

    /// <summary>输入或参数变化后重新分析（防抖 + 取消上一次），绝不在 UI 线程做探测。</summary>
    private async Task RefreshAnalysisAsync()
    {
        if (Inputs.Count == 0)
        {
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

    private void ApplyBundleInfo(List<MediaBundle> bundles)
    {
        for (int i = 0; i < Inputs.Count && i < bundles.Count; i++)
        {
            if (!string.Equals(Inputs[i].Path, bundles[i].Path, StringComparison.OrdinalIgnoreCase)) continue;
            Inputs[i].Apply(bundles[i]);
        }
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
        if (OutputName.Length == 0 && added.Count > 0)
            OutputName = SuggestOutputName();
        SyncOutputExtension();

        await ProbeInputsAsync(added);
        await RefreshAnalysisAsync();
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
            _ui.RunOnUiThread(() =>
            {
                for (int i = 0; i < items.Count && i < bundles.Count; i++) items[i].Apply(bundles[i]);
            });
        }
        catch (Exception ex)
        {
            foreach (MergeInputViewModel item in items) item.Fail("探测失败：" + ex.Message);
        }
    }

    private string SuggestOutputName()
    {
        if (Inputs.Count == 0) return "merged";
        string first = Inputs[0].DefaultChapterTitle;
        return Inputs.Count == 1 ? first + "_merged" : first + "_等" + Inputs.Count + "个视频";
    }

    public void SetSelection(IEnumerable<MergeInputViewModel> selected)
    {
        _selected = selected.Distinct().ToList();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
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
        OutputPath = "";
    }

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

    /// <summary>供视图拖放排序使用（不经过按钮）。</summary>
    public void MoveInput(int from, int to)
    {
        if (IsRunning) return;
        if (from < 0 || to < 0 || from >= Inputs.Count || to >= Inputs.Count || from == to) return;
        Inputs.Move(from, to);
        _ = RefreshAnalysisAsync();
    }

    #endregion

    #region 输出路径

    partial void OnOutputDirectoryChanged(string value)
    {
        OutputPath = CombineOutput();
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputNameChanged(string value)
    {
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
        if (OutputName.Length == 0) OutputName = SuggestOutputName();
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
    [ObservableProperty] private string resultText = "";
    [ObservableProperty] private bool hasResult;
    [ObservableProperty] private bool resultOk;

    partial void OnIsRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
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
        if (!CanStreamCopy && !AllowReencode)
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
        CanCancel = true;
        HasResult = false;
        ResultText = "";
        Progress = 0;
        StageText = "准备中…";
        ProgressDetail = "";

        _record = _tasks.Add(TaskKind.VideoMerge,
            $"视频合并 · {options.Inputs.Count} 段",
            $"{options.Inputs.Count} 段 · {(CanStreamCopy ? "无损拼接" : "统一重新编码")}",
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
    private void OnProgress(MergeProgress progress)
    {
        _pendingProgress = progress;
        if (_progressScheduled) return;
        _progressScheduled = true;
        _ui.RunOnUiThread(FlushProgress);
    }

    private void FlushProgress()
    {
        MergeProgress? snapshot = _pendingProgress;
        _pendingProgress = null;
        _progressScheduled = false;
        if (snapshot is null) return;

        DateTime now = DateTime.UtcNow;
        if ((now - _lastProgressFlush).TotalMilliseconds < 100) return;
        _lastProgressFlush = now;

        Progress = Math.Clamp(snapshot.Percent, 0, 100);
        StageText = DescribeStage(snapshot);
        ProgressDetail = snapshot.Message;

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

    private void ShowResult(MergeResult result)
    {
        HasResult = true;
        ResultOk = result.Ok;
        Progress = result.Ok ? 100 : Progress;
        StageText = result.Ok ? "已完成" : result.Status == MergeStatus.Cancelled ? "已取消" : "失败";
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
        StageText = "正在取消…";
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
