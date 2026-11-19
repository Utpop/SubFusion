using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubFusion.App.Services;
using SubFusion.Core;
using SubFusion.Core.Compress;
using SubFusion.Core.Media;
using SubFusion.Core.Services;
using SubFusion.Core.Trim;

namespace SubFusion.App.ViewModels;

/// <summary>编码格式下拉项。</summary>
public sealed record CompressCodecOption(VideoCodecKind Value, string Label, string Hint);

/// <summary>编码器偏好下拉项。</summary>
public sealed record CompressPreferenceOption(string Value, string Label, string Hint);

/// <summary>硬件后端下拉项。</summary>
public sealed record CompressBackendOption(EncoderKind Value, string Label);

/// <summary>码率控制下拉项。</summary>
public sealed record CompressRateOption(RateControl Value, string Label, string Hint);

/// <summary>分辨率处理下拉项。</summary>
public sealed record CompressResizeOption(CompressResize Value, string Label);

/// <summary>
/// 视频压制工作区。
///
/// 与 Core 的边界：本页只构造 VideoCompressOptions 并调用 VideoCompressTask；
/// 编码器选择、硬件可用性、命令构造、校验与落盘全部由 Core 决定，界面只展示结论。
///
/// 本页最重要的一条诚实性要求：界面上显示的硬件加速状态必须来自真实探测。
/// 只看 ffmpeg 的编码器列表会把「有这个编码器但没有对应显卡」当成可用——
/// 本机实测 h264_nvenc / h264_qsv / av1_amf 全都这样。因此每次载入素材都会真的
/// 初始化一次编码，把结论原样显示出来。
/// </summary>
public sealed partial class VideoCompressViewModel : ObservableObject
{
    private readonly IUiServices _ui;
    private readonly AppSettings _settings;
    private readonly TaskCenter _tasks;

    private VideoCompressTask? _task;
    private TaskRecord? _record;
    private CancellationTokenSource? _runSource;
    private CancellationTokenSource? _runLinked;
    private CancellationTokenSource? _analyzeSource;

    private MediaBundle? _bundle;
    private IReadOnlyList<EncoderAvailability> _encoders = Array.Empty<EncoderAvailability>();

    private bool _loading;
    private bool _outputNameUserEdited;
    private bool _progressClosed;

    public VideoCompressViewModel(IUiServices ui, AppSettings settings, TaskCenter tasks)
    {
        _ui = ui;
        _settings = settings;
        _tasks = tasks;
    }

    #region 输入

    [ObservableProperty] private string inputPath = "";

    public bool HasInput => InputPath.Length > 0;

    public string InputName
    {
        get
        {
            if (InputPath.Length == 0) return "尚未选择视频";
            try { return Path.GetFileName(InputPath); }
            catch { return InputPath; }
        }
    }

    [ObservableProperty] private bool analyzing;
    [ObservableProperty] private string analysisError = "";
    [ObservableProperty] private string mediaInfoText = "";

    partial void OnInputPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasInput));
        OnPropertyChanged(nameof(InputName));
        if (_loading) return;
        _ = LoadInputAsync();
    }

    [RelayCommand]
    private async Task ChooseInputAsync()
    {
        List<string> extensions = Constants.VideoExts.OrderBy(x => x, StringComparer.Ordinal).ToList();
        IReadOnlyList<string> paths = await _ui.PickFilesAsync("选择要压制的视频", extensions);
        string? path = paths.FirstOrDefault();
        if (!string.IsNullOrEmpty(path)) InputPath = path;
    }

    /// <summary>拖放入口：只接受第一个视频文件。</summary>
    public void AcceptDroppedFile(IReadOnlyList<string> paths)
    {
        string? video = paths.FirstOrDefault(p => Constants.VideoExts.Contains(Path.GetExtension(p)));
        if (video is not null) InputPath = video;
    }

    /// <summary>载入输入：探测媒体信息 + 真实探测本机可用编码器。</summary>
    private async Task LoadInputAsync()
    {
        if (InputPath.Length == 0)
        {
            _bundle = null;
            _encoders = Array.Empty<EncoderAvailability>();
            MediaInfoText = "";
            AnalysisError = "";
            EncoderSummary = "";
            EncoderWarning = "";
            SourceSizeText = "";
            EstimateText = "";
            OutputName = "";
            OutputPath = "";
            RefreshPlan();
            return;
        }

        CancelAnalyze();
        var cts = new CancellationTokenSource();
        _analyzeSource = cts;
        Analyzing = true;
        AnalysisError = "";

        try
        {
            VideoCompressTask? task = await EnsureTaskAsync(cts.Token);
            if (task is null)
            {
                AnalysisError = "未找到 ffmpeg / ffprobe，无法读取媒体信息。请在「设置 → 媒体工具」中指定路径。";
                return;
            }

            (MediaBundle bundle, IReadOnlyList<EncoderAvailability> encoders) =
                await task.AnalyzeAsync(InputPath, cts.Token);
            if (cts.IsCancellationRequested) return;

            _bundle = bundle;
            _encoders = encoders;

            _ui.RunOnUiThread(() =>
            {
                if (!bundle.Success)
                {
                    AnalysisError = "无法读取该文件：" + bundle.Error;
                    MediaInfoText = "";
                    return;
                }

                MediaInfoText = DescribeMedia(bundle);
                SourceSizeText = Formatting.FormatSize(bundle.Container.Size);
                RefreshEncoderStatus();

                if (OutputDirectory.Length == 0)
                {
                    string? dir = null;
                    try { dir = Path.GetDirectoryName(InputPath); } catch { /* 路径异常时留空 */ }
                    if (!string.IsNullOrEmpty(dir)) OutputDirectory = dir;
                }

                if (!_outputNameUserEdited) SetOutputName(SuggestOutputName(), userEdit: false);
                RefreshPlan();
            });
        }
        catch (OperationCanceledException) { /* 切换输入时的正常取消 */ }
        catch (Exception ex)
        {
            _ui.RunOnUiThread(() => AnalysisError = "分析失败：" + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_analyzeSource, cts))
            {
                _analyzeSource = null;
                cts.Dispose();
            }
            _ui.RunOnUiThread(() => Analyzing = false);
        }
    }

    private void CancelAnalyze()
    {
        CancellationTokenSource? previous = _analyzeSource;
        _analyzeSource = null;
        if (previous is null) return;
        try { previous.Cancel(); } catch (ObjectDisposedException) { /* 已释放 */ }
    }

    private static string DescribeMedia(MediaBundle bundle)
    {
        VideoStreamInfo? v = bundle.PrimaryVideo;
        if (v is null) return "无视频流";
        string video = string.Format(CultureInfo.InvariantCulture,
            "{0} · {1}×{2} · {3:0.###}fps · {4}",
            v.Codec.ToUpperInvariant(), v.Width, v.Height, v.EffectiveFrameRate, v.PixFmt);
        AudioStreamInfo? a = bundle.PrimaryAudio;
        string audio = a is null ? "无音轨"
            : string.Format(CultureInfo.InvariantCulture, "{0} · {1:0.#} kHz · {2} 声道",
                a.Codec.ToUpperInvariant(), a.SampleRate / 1000.0, a.Channels);
        string space = Formatting.FormatSize(bundle.Container.Size);
        string hdr = v.IsHdr ? " · HDR（" + v.HdrFormat + "）" : "";
        return video + hdr + "\n" + audio + " · 时长 " + TrimTime.Format(bundle.Duration) + " · " + space;
    }

    private async Task<VideoCompressTask?> EnsureTaskAsync(CancellationToken cancellationToken)
    {
        if (_task is not null) return _task;
        try
        {
            _task = await VideoCompressTask.CreateAsync(_settings.FfmpegPath, _settings.FfprobePath,
                                                        cancellationToken);
            return _task;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>设置里的工具路径变化后需要重建任务并重新探测编码器。</summary>
    public void InvalidateTools()
    {
        _task = null;
        _encoders = Array.Empty<EncoderAvailability>();
        if (InputPath.Length > 0) _ = LoadInputAsync();
    }

    #endregion

    #region 编码设置

    public IReadOnlyList<CompressCodecOption> CodecOptions { get; } = new[]
    {
        new CompressCodecOption(VideoCodecKind.H264, "H.264（AVC）",
            "兼容性最好：手机、电视、网页、几乎所有播放器都能放。同等画质下文件比 H.265 大一些。"),
        new CompressCodecOption(VideoCodecKind.Hevc, "H.265（HEVC）",
            "同等画质下文件通常小 30% 左右，代价是编码慢一点、老设备可能放不动。"),
    };

    [ObservableProperty] private CompressCodecOption? selectedCodec;
    [ObservableProperty] private VideoCodecKind codec = VideoCodecKind.H264;

    partial void OnSelectedCodecChanged(CompressCodecOption? value)
    {
        OnPropertyChanged(nameof(CodecHint));
        if (value is null || value.Value == Codec) return;
        Codec = value.Value;
    }

    /// <summary>当前编码格式的说明。单独暴露成字符串：x:Bind 的路径中出现 null 会直接抛异常，
    /// 因此界面不绑 SelectedCodec.Hint，而绑这个永远非空的投影。</summary>
    public string CodecHint => SelectedCodec?.Hint ?? CodecOptions[0].Hint;

    partial void OnCodecChanged(VideoCodecKind value)
    {
        CompressCodecOption? option = CodecOptions.FirstOrDefault(c => c.Value == value);
        if (option is not null && !ReferenceEquals(SelectedCodec, option)) SelectedCodec = option;
        RefreshEncoderStatus();
        RefreshPlan();
    }

    public IReadOnlyList<CompressPreferenceOption> PreferenceOptions { get; } = new[]
    {
        new CompressPreferenceOption("auto", "自动（有显卡就用显卡）",
            "能用显卡就用显卡，显卡不可用时自动改用 CPU，并在结果里说明原因。"),
        new CompressPreferenceOption("hardware", "只用显卡加速",
            "强制使用显卡编码。显卡不可用时直接失败，不会静默改用 CPU——适合需要稳定耗时预期的场景。"),
        new CompressPreferenceOption("software", "只用 CPU 编码",
            "画质/体积比最好，但速度取决于 CPU。显卡在跑别的任务时用它更稳。"),
    };

    [ObservableProperty] private CompressPreferenceOption? selectedPreference;
    [ObservableProperty] private string encoderPreference = "auto";

    partial void OnSelectedPreferenceChanged(CompressPreferenceOption? value)
    {
        OnPropertyChanged(nameof(PreferenceHint));
        if (value is null || value.Value == EncoderPreference) return;
        EncoderPreference = value.Value;
    }

    public string PreferenceHint => SelectedPreference?.Hint ?? PreferenceOptions[0].Hint;

    partial void OnEncoderPreferenceChanged(string value)
    {
        CompressPreferenceOption? option = PreferenceOptions.FirstOrDefault(o => o.Value == value);
        if (option is not null && !ReferenceEquals(SelectedPreference, option)) SelectedPreference = option;
        OnPropertyChanged(nameof(IsHardwareAllowed));
        OnPropertyChanged(nameof(IsSoftwareOnly));
        RefreshEncoderStatus();
        RefreshPlan();
    }

    /// <summary>true 表示允许用显卡（自动或强制）。</summary>
    public bool IsHardwareAllowed => EncoderPreference is "auto" or "hardware";

    /// <summary>true 表示只用 CPU：硬件相关控件应当变灰。</summary>
    public bool IsSoftwareOnly => EncoderPreference == "software";

    public IReadOnlyList<CompressBackendOption> BackendOptions { get; } = new[]
    {
        new CompressBackendOption(EncoderKind.Software, "不限（自动挑选）"),
        new CompressBackendOption(EncoderKind.Amf, "AMD（AMF）"),
        new CompressBackendOption(EncoderKind.Nvenc, "NVIDIA（NVENC）"),
        new CompressBackendOption(EncoderKind.Qsv, "Intel 核显（QSV）"),
    };

    [ObservableProperty] private CompressBackendOption? selectedBackend;
    /// <summary>限定后端："" 表示不限，其它值对应 EncoderKind 名称。</summary>
    [ObservableProperty] private string backend = "";

    partial void OnSelectedBackendChanged(CompressBackendOption? value)
    {
        if (value is null) return;
        string name = value.Value == EncoderKind.Software ? "" : value.Value.ToString();
        if (!string.Equals(Backend, name, StringComparison.Ordinal)) Backend = name;
    }

    partial void OnBackendChanged(string value)
    {
        CompressBackendOption? option = value.Length == 0
            ? BackendOptions[0]
            : BackendOptions.FirstOrDefault(o => o.Value.ToString() == value);
        if (option is not null && !ReferenceEquals(SelectedBackend, option)) SelectedBackend = option;
        RefreshEncoderStatus();
        RefreshPlan();
    }

    public IReadOnlyList<string> HardwareQualities { get; } = new[] { "speed", "balanced", "quality" };

    [ObservableProperty] private string hardwareQuality = "balanced";

    partial void OnHardwareQualityChanged(string value)
    {
        RefreshEncoderStatus();
        RefreshPlan();
    }

    public IReadOnlyList<CompressRateOption> RateOptions { get; } = new[]
    {
        new CompressRateOption(RateControl.Quality, "按画质（推荐）",
            "指定画质档，软件自己决定码率：画面复杂的片段给得多、静止画面给得少。文件大小不固定。"),
        new CompressRateOption(RateControl.Bitrate, "按码率",
            "指定目标码率，文件大小基本可预期，适合要严格控制体积的场景（例如上传有大小限制）。"),
    };

    [ObservableProperty] private CompressRateOption? selectedRate;
    [ObservableProperty] private RateControl rateControl = RateControl.Quality;

    partial void OnSelectedRateChanged(CompressRateOption? value)
    {
        OnPropertyChanged(nameof(RateHint));
        if (value is null || value.Value == RateControl) return;
        RateControl = value.Value;
    }

    public string RateHint => SelectedRate?.Hint ?? RateOptions[0].Hint;

    partial void OnRateControlChanged(RateControl value)
    {
        CompressRateOption? option = RateOptions.FirstOrDefault(o => o.Value == value);
        if (option is not null && !ReferenceEquals(SelectedRate, option)) SelectedRate = option;
        OnPropertyChanged(nameof(IsQualityMode));
        OnPropertyChanged(nameof(IsBitrateMode));
        RefreshPlan();
    }

    public bool IsQualityMode => RateControl == RateControl.Quality;
    public bool IsBitrateMode => RateControl == RateControl.Bitrate;

    [ObservableProperty] private double quality = 23;
    [ObservableProperty] private double bitrateKbps = 2500;

    partial void OnQualityChanged(double value) => RefreshPlan();
    partial void OnBitrateKbpsChanged(double value) => RefreshPlan();

    /// <summary>软件编码速度档。硬件编码不使用它（用「显卡编码质量档」），因此界面里会变灰。</summary>
    public IReadOnlyList<string> Presets { get; } =
        new[] { "ultrafast", "veryfast", "fast", "medium", "slow", "veryslow" };

    [ObservableProperty] private string preset = "medium";
    partial void OnPresetChanged(string value) => RefreshPlan();

    /// <summary>编码器实时结论（探测结果 + 本次会实际使用哪个）。</summary>
    [ObservableProperty] private string encoderSummary = "";
    [ObservableProperty] private string encoderWarning = "";
    [ObservableProperty] private bool hardwareReady;
    [ObservableProperty] private bool hardwareDecodeEnabled = true;
    [ObservableProperty] private string hardwareDecodeText = "";

    partial void OnHardwareDecodeEnabledChanged(bool value) => RefreshPlan();

    /// <summary>把真实探测结果整理成一句话，并在「要显卡但没有」时给出明确告警。</summary>
    private void RefreshEncoderStatus()
    {
        if (_encoders.Count == 0)
        {
            EncoderSummary = "";
            EncoderWarning = "";
            HardwareReady = false;
            HardwareDecodeText = "";
            return;
        }

        List<EncoderKind>? backends = ResolveBackends();
        EncoderAvailability? selected = HardwareEncoder.Select(_encoders, Codec, EncoderPreference, backends);

        List<EncoderAvailability> usableHardware = _encoders
            .Where(e => e.Usable && e.Kind != EncoderKind.Software).ToList();
        HardwareReady = usableHardware.Count > 0;

        var parts = new List<string>();
        foreach (EncoderAvailability entry in _encoders.Where(e => e.Kind != EncoderKind.Software))
            parts.Add((entry.Usable ? "可用 " : "不可用 ") + entry.Name +
                      (entry.Usable ? "" : "（" + ShortError(entry.Error) + "）"));
        foreach (EncoderAvailability entry in _encoders.Where(e => e.Kind == EncoderKind.Software))
            parts.Add((entry.Usable ? "可用 " : "不可用 ") + entry.Name + "（CPU）");

        string chosen = selected is null
            ? "当前设置下没有可用编码器"
            : "本次将使用：" + selected.Label + "（" + selected.Name + "）";
        EncoderSummary = chosen + "\n" + string.Join("\n", parts);

        EncoderWarning = selected switch
        {
            null => "当前编码格式 + 编码器偏好下没有任何可用编码器，请改用 CPU 软件编码或换一种格式。",
            { Kind: EncoderKind.Software } when EncoderPreference == "hardware"
                => "已选择「只用显卡加速」，但本机没有可用的硬件编码器，任务会直接失败。",
            { Kind: EncoderKind.Software } when EncoderPreference == "auto" && usableHardware.Count > 0
                => "本机有可用的硬件编码器，但当前「编码格式 / 显卡品牌」筛选把它们排除了，本次会用 CPU。",
            _ => "",
        };

        HardwareDecodeText = selected is { Kind: not EncoderKind.Software }
            ? "显卡解码：开启后由显卡解码源视频，减少 CPU 占用。个别素材硬解会失败，程序会自动退回软解重试。"
            : "显卡解码只在使用显卡编码时生效；当前用 CPU 编码，此选项不起作用。";
    }

    private static string ShortError(string error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "不可用";
        string text = error.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length > 60 ? text[..60] + "…" : text;
    }

    private List<EncoderKind>? ResolveBackends()
    {
        if (string.IsNullOrEmpty(Backend)) return null;
        return Enum.TryParse(Backend, out EncoderKind kind) && kind != EncoderKind.Software
            ? new List<EncoderKind> { kind }
            : null;
    }

    #endregion

    #region 画面与音频

    public IReadOnlyList<CompressResizeOption> ResizeOptions { get; } = new[]
    {
        new CompressResizeOption(CompressResize.None, "保持原分辨率"),
        new CompressResizeOption(CompressResize.LongEdge, "缩到指定长边"),
        new CompressResizeOption(CompressResize.Scale, "按整数倍缩小"),
    };

    [ObservableProperty] private CompressResizeOption? selectedResize;
    [ObservableProperty] private CompressResize resize = CompressResize.None;

    partial void OnSelectedResizeChanged(CompressResizeOption? value)
    {
        if (value is null || value.Value == Resize) return;
        Resize = value.Value;
    }

    partial void OnResizeChanged(CompressResize value)
    {
        CompressResizeOption? option = ResizeOptions.FirstOrDefault(o => o.Value == value);
        if (option is not null && !ReferenceEquals(SelectedResize, option)) SelectedResize = option;
        OnPropertyChanged(nameof(UsesLongEdge));
        OnPropertyChanged(nameof(UsesScaleFactor));
        RefreshPlan();
    }

    public bool UsesLongEdge => Resize == CompressResize.LongEdge;
    public bool UsesScaleFactor => Resize == CompressResize.Scale;

    /// <summary>
    /// 目标长边候选。用 double 而不是 int：ComboBox.SelectedItem 双向绑到 double 属性，
    /// 元素类型不一致时 WinUI 会在回写时抛类型转换异常。
    /// </summary>
    public IReadOnlyList<double> LongEdges { get; } = new[] { 2160d, 1440d, 1080d, 720d, 480d, 360d };

    [ObservableProperty] private double longEdge = 1080;
    [ObservableProperty] private double scaleFactor = 2;

    partial void OnLongEdgeChanged(double value) => RefreshPlan();
    partial void OnScaleFactorChanged(double value) => RefreshPlan();

    /// <summary>目标帧率；「保持原样」表示 0。</summary>
    public IReadOnlyList<string> FrameRates { get; } = new[] { "保持原样", "60", "50", "30", "25", "24" };

    [ObservableProperty] private string selectedFrameRate = "保持原样";

    partial void OnSelectedFrameRateChanged(string value)
    {
        FrameRate = value == "保持原样" ? 0
            : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps) ? fps : 0;
    }

    [ObservableProperty] private double frameRate;
    partial void OnFrameRateChanged(double value) => RefreshPlan();

    [ObservableProperty] private bool reencodeAudio = true;
    [ObservableProperty] private double audioBitrate = 192;

    partial void OnReencodeAudioChanged(bool value) => RefreshPlan();
    partial void OnAudioBitrateChanged(double value) => RefreshPlan();

    #endregion

    #region 输出

    public IReadOnlyList<string> Containers { get; } = new[] { "mp4", "mkv" };

    [ObservableProperty] private string container = "mp4";

    partial void OnContainerChanged(string value)
    {
        SyncOutputExtension();
        RefreshEncoderStatus();
        RefreshPlan();
    }

    [ObservableProperty] private string outputDirectory = "";
    [ObservableProperty] private string outputName = "";
    [ObservableProperty] private string outputPath = "";
    [ObservableProperty] private bool overwrite = true;
    [ObservableProperty] private bool verifyOutput = true;
    [ObservableProperty] private bool keepSubtitles;

    partial void OnOutputDirectoryChanged(string value) { SyncOutputExtension(); StartCommand.NotifyCanExecuteChanged(); }
    partial void OnOutputNameChanged(string value)
    {
        if (!_loading) _outputNameUserEdited = value.Trim().Length > 0;
        SyncOutputExtension();
        StartCommand.NotifyCanExecuteChanged();
    }
    partial void OnOverwriteChanged(bool value) => StartCommand.NotifyCanExecuteChanged();
    partial void OnVerifyOutputChanged(bool value) => RefreshPlan();
    partial void OnKeepSubtitlesChanged(bool value) => RefreshPlan();

    private string SuggestOutputName()
    {
        string stem;
        try { stem = Path.GetFileNameWithoutExtension(InputPath); }
        catch { stem = "video"; }
        if (stem.Length == 0) stem = "video";
        return stem + "_compressed";
    }

    /// <summary>仅供自动化验收挂钩调用：按用户手动改名的方式设置输出名。</summary>
    public void SetOutputNameForTest(string value) => SetOutputName(value, userEdit: true);

    private void SetOutputName(string value, bool userEdit)
    {
        _loading = !userEdit;
        try { OutputName = value; }
        finally { _loading = false; }
        if (!userEdit) _outputNameUserEdited = false;
    }

    private void SyncOutputExtension()
    {
        string dir = (OutputDirectory ?? "").Trim();
        string name = (OutputName ?? "").Trim();
        if (dir.Length == 0 || name.Length == 0) { OutputPath = ""; return; }

        string extension = Container == "mkv" ? ".mkv" : ".mp4";
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            string withoutExt = Path.HasExtension(name) ? name[..^Path.GetExtension(name).Length] : name;
            name = withoutExt + extension;
        }
        try
        {
            OutputPath = Path.Combine(dir, name);
            PlanError = "";
        }
        catch (Exception ex)
        {
            OutputPath = "";
            PlanError = "无法生成输出路径：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ChooseOutputDirectoryAsync()
    {
        string? folder = await _ui.PickFolderAsync("选择输出目录");
        if (string.IsNullOrEmpty(folder)) return;
        OutputDirectory = folder;
        if (OutputName.Trim().Length == 0) SetOutputName(SuggestOutputName(), userEdit: false);
    }

    [RelayCommand]
    private async Task ChooseOutputFileAsync()
    {
        string? path = await _ui.PickSaveVideoAsync("选择输出文件", SuggestOutputName(),
                                                    Container == "mkv" ? ".mkv" : ".mp4", OutputDirectory);
        if (string.IsNullOrEmpty(path)) return;
        OutputDirectory = Path.GetDirectoryName(path) ?? OutputDirectory;
        SetOutputName(Path.GetFileName(path), userEdit: true);
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        string dir = OutputPath.Length > 0 ? (Path.GetDirectoryName(OutputPath) ?? "") : OutputDirectory;
        if (dir.Length > 0 && Directory.Exists(dir)) _ui.OpenFolder(dir);
        else _ = _ui.ShowInfoAsync("输出目录还不存在。");
    }

    #endregion

    #region 计划与预估

    [ObservableProperty] private bool hasPlan;
    [ObservableProperty] private string planText = "";
    [ObservableProperty] private string planDetail = "";
    [ObservableProperty] private string planError = "";
    [ObservableProperty] private string sourceSizeText = "";
    [ObservableProperty] private string estimateText = "";

    /// <summary>
    /// 重新计算「本次会用什么编码器 / 缩到多大 / 体积大概多少」。
    /// 与引擎共用同一套 Core 判定（HardwareEncoder.Select），
    /// 界面不允许自己猜一个和引擎不同的结论。
    /// </summary>
    private void RefreshPlan()
    {
        if (_bundle is not { Success: true } bundle)
        {
            HasPlan = false;
            PlanText = "";
            PlanDetail = "";
            EstimateText = "";
            StartCommand.NotifyCanExecuteChanged();
            return;
        }

        VideoCompressOptions options = BuildOptions();

        List<EncoderKind>? backends = ResolveBackends();
        EncoderAvailability? encoder = HardwareEncoder.Select(_encoders, Codec, EncoderPreference, backends);
        if (encoder is null)
        {
            HasPlan = false;
            PlanError = "当前编码格式与编码器偏好下没有可用编码器，请改用 CPU 软件编码或换一种编码格式。";
            StartCommand.NotifyCanExecuteChanged();
            return;
        }

        // 「要显卡却只有 CPU」在 hardware 模式下必须拦住，不能让用户点了才开始才发现。
        if (EncoderPreference == "hardware" && encoder.Kind == EncoderKind.Software)
        {
            HasPlan = false;
            PlanError = "已选择「只用显卡加速」，但本机没有可用的硬件编码器。请换回「自动」或「只用 CPU」。";
            StartCommand.NotifyCanExecuteChanged();
            return;
        }

        List<string> audioErrors = VideoCompressOptions.ValidateAudioCopy(
            options.NormalizedContainer(), bundle.PrimaryAudio?.Codec ?? "", bundle.HasAudio);
        if (audioErrors.Count > 0 && !options.ReencodeAudio)
        {
            HasPlan = false;
            PlanError = string.Join("；", audioErrors);
            StartCommand.NotifyCanExecuteChanged();
            return;
        }

        PlanError = "";
        HasPlan = true;

        VideoStreamInfo? video = bundle.PrimaryVideo;
        string sizeText = video is null ? "?" : video.Width + "×" + video.Height;
        string targetSize = DescribeTargetSize(video);
        string rateText = RateControl == RateControl.Bitrate
            ? string.Format(CultureInfo.InvariantCulture, "{0} kbps", (int)BitrateKbps)
            : string.Format(CultureInfo.InvariantCulture, "画质档 {0}", (int)Quality);

        PlanText = string.Format(CultureInfo.InvariantCulture,
            "{0} · {1}{2} · {3}", encoder.Label, targetSize,
            targetSize == sizeText ? "" : "（原 " + sizeText + "）", rateText);

        var parts = new List<string>
        {
            "编码器 " + encoder.Name + "（" + encoder.Label + "）",
            RateControl == RateControl.Bitrate
                ? "目标码率 " + (int)BitrateKbps + " kbps"
                : encoder.Kind != EncoderKind.Software
                    ? "画质档 " + (int)Quality + "（硬件编码换算为 QP " + CompressCommandBuilder.HardwareQp((int)Quality) + "）"
                    : "画质档 CRF " + (int)Quality,
            "音轨 " + (ReencodeAudio
                ? "重编码 " + (int)AudioBitrate + " kbps"
                : "原样复制（不重编码）"),
        };
        if (FrameRate > 0 && video is not null)
        {
            double source = video.EffectiveFrameRate;
            parts.Add(source > 0 && FrameRate >= source - 0.01
                ? "帧率保持 " + source.ToString("0.###", CultureInfo.InvariantCulture) + " fps（不放大）"
                : "帧率转为 " + FrameRate.ToString("0.###", CultureInfo.InvariantCulture) + " fps");
        }
        if (encoder.Kind != EncoderKind.Software)
            parts.Add(HardwareDecodeEnabled ? "显卡解码：启用" : "显卡解码：关闭（用 CPU 解）");
        if (RateControl == RateControl.Quality)
            parts.Add("按画质档压缩时文件大小取决于画面内容，下方预估值仅供参考");
        PlanDetail = string.Join("\n", parts);

        EstimateText = DescribeEstimate(options, bundle, encoder);
        StartCommand.NotifyCanExecuteChanged();
    }

    private string DescribeTargetSize(VideoStreamInfo? video)
    {
        if (video is null) return "原分辨率";
        if (Resize == CompressResize.LongEdge) return LongEdge.ToString("0") + "p";
        if (Resize == CompressResize.Scale)
        {
            int divisor = Math.Max(2, (int)Math.Round(ScaleFactor));
            return string.Format(CultureInfo.InvariantCulture, "缩到 1/{0}（约 {1}×{2}）", divisor,
                CompressCommandBuilder.EvenUp(video.Width / divisor),
                CompressCommandBuilder.EvenUp(video.Height / divisor));
        }
        return video.Width + "×" + video.Height;
    }

    private string DescribeEstimate(VideoCompressOptions options, MediaBundle bundle,
                                    EncoderAvailability encoder)
    {
        double duration = bundle.Duration;
        if (duration <= 0) return "";
        long sourceBytes = bundle.Container.Size;
        string source = Formatting.FormatSize(sourceBytes);

        if (RateControl == RateControl.Bitrate)
        {
            long estimated = CompressCommandBuilder.EstimateBytes(options, duration);
            if (estimated <= 0) return "";
            return string.Format(CultureInfo.InvariantCulture,
                "预计输出约 {0}（源 {1}，{2:0.#} 分钟）",
                Formatting.FormatSize(estimated), source, duration / 60.0);
        }

        // 画质模式：用「源码率 × 画质系数」粗略估算。系数来自本机实测：
        // CRF 23 在 720p 上大致是源体积的 1/3~1/2，因此按画质档外推，只给区间不说死。
        double sourceBitrate = bundle.Container.BitRate;
        if (sourceBitrate <= 0 && sourceBytes > 0) sourceBitrate = sourceBytes * 8.0 / duration;
        if (sourceBitrate <= 0) return "";

        double factor = Quality switch
        {
            <= 18 => 0.75,
            <= 21 => 0.55,
            <= 24 => 0.40,
            <= 27 => 0.28,
            <= 30 => 0.20,
            _ => 0.14,
        };
        // 硬件编码在同画质档下体积通常比软件编码大 30%~60%，如实反映，避免用户以为切到显卡更省。
        if (encoder.Kind != EncoderKind.Software) factor *= 1.45;

        double expectedBitrate = sourceBitrate * factor;
        double bytes = expectedBitrate / 8.0 * duration;
        return string.Format(CultureInfo.InvariantCulture,
            "预计输出 {0} ~ {1}（源 {2}，{3:0.#} 分钟；按画质档压缩时体积随画面内容浮动）",
            Formatting.FormatSize((long)(bytes * 0.6)), Formatting.FormatSize((long)(bytes * 1.6)),
            source, duration / 60.0);
    }

    #endregion

    #region 执行

    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool canCancel;
    [ObservableProperty] private double progress;
    [ObservableProperty] private string stageText = "空闲";
    [ObservableProperty] private string progressDetail = "";
    [ObservableProperty] private string speedText = "";
    [ObservableProperty] private string etaText = "";
    [ObservableProperty] private bool hasProgressInfo;
    [ObservableProperty] private bool isIndeterminate;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string resultText = "";
    [ObservableProperty] private bool hasResult;
    [ObservableProperty] private bool resultOk;
    [ObservableProperty] private string activeEncoderText = "";

    public bool CanStart => !IsRunning && HasInput && OutputPath.Length > 0;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    private bool CanStartCommand() => CanStart;

    private VideoCompressOptions BuildOptions()
    {
        return new VideoCompressOptions
        {
            Input = InputPath,
            OutputPath = OutputPath,
            Codec = Codec,
            EncoderPreference = EncoderPreference,
            AllowedBackends = ResolveBackends() ?? new List<EncoderKind>(),
            HardwareQuality = HardwareQuality,
            RateControl = RateControl,
            Quality = (int)Math.Round(Quality),
            TargetBitrateKbps = (int)Math.Round(BitrateKbps),
            Preset = Preset,
            FrameRate = FrameRate,
            Resize = Resize,
            ResizeValue = Resize == CompressResize.Scale
                ? (int)Math.Round(ScaleFactor)
                : (int)Math.Round(LongEdge),
            Container = Container,
            Overwrite = Overwrite,
            VerifyOutput = VerifyOutput,
            KeepSubtitles = KeepSubtitles,
            ReencodeAudio = ReencodeAudio,
            AudioBitrateKbps = (int)Math.Round(AudioBitrate),
            HardwareDecode = HardwareDecodeEnabled,
        };
    }

    [RelayCommand(CanExecute = nameof(CanStartCommand))]
    private async Task StartAsync()
    {
        if (IsRunning) return;

        VideoCompressOptions options = BuildOptions();
        List<string> errors = options.Validate();
        if (errors.Count > 0)
        {
            await _ui.ShowErrorAsync("参数不完整：\n\n" + string.Join("\n", errors.Select(e => "· " + e)));
            return;
        }

        VideoCompressTask? task = await EnsureTaskAsync(CancellationToken.None);
        if (task is null)
        {
            await _ui.ShowErrorAsync("未找到 ffmpeg / ffprobe，请在「设置 → 媒体工具」中指定路径。");
            return;
        }

        // 用户每次真正开始压制时把当前选择落盘：下次打开还是这套参数。
        // 不在每次属性变化时写盘——拖滑杆会把配置文件刷爆（与其它页面的约定一致）。
        PersistSettings();

        IsRunning = true;
        CanCancel = true;
        _progressClosed = false;
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
        ActiveEncoderText = "";

        _record = _tasks.Add(TaskKind.VideoCompress,
            "视频压制 · " + InputName,
            BuildRecordSummary(options),
            options.OutputPath);

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
            r.Message = "压制中";
        });

        _runSource = new CancellationTokenSource();
        _runLinked = CancellationTokenSource.CreateLinkedTokenSource(
            _runSource.Token, _record.Cancellation.Token);

        try
        {
            CompressResult result = await task.RunAsync(options, new Progress<CompressProgress>(OnProgress),
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
            ResultText = "压制失败：" + ex.Message;
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

    private string BuildRecordSummary(VideoCompressOptions options)
    {
        string codec = options.Codec == VideoCodecKind.Hevc ? "H.265" : "H.264";
        string mode = options.EncoderPreference switch
        {
            "hardware" => "显卡",
            "software" => "CPU",
            _ => "自动",
        };
        string rate = options.RateControl == RateControl.Bitrate
            ? options.TargetBitrateKbps + " kbps"
            : "画质 " + options.Quality;
        return codec + " · " + mode + " · " + rate;
    }

    /// <summary>进度回调（任意线程）；合并到约 10 Hz 再刷 UI。</summary>
    private void OnProgress(CompressProgress progress) => _ui.RunOnUiThread(() => ApplyProgress(progress));

    private DateTime _lastFlush = DateTime.MinValue;

    private void ApplyProgress(CompressProgress snapshot)
    {
        if (_progressClosed) return;
        // 终态不能因为节流被丢掉：最后一条「完成 / 100%」一旦漏掉，
        // 界面就永远停在前一条上，任务成功也会显示成卡住。
        bool terminal = snapshot.Stage is CompressStage.Completed or CompressStage.Failed or CompressStage.Cancelled
                        || ProgressPacer.IsTerminalPercent(snapshot.Percent);
        DateTime now = DateTime.UtcNow;
        if (!ProgressPacer.ShouldDeliver(now - _lastFlush, terminal)) return;
        _lastFlush = now;

        Progress = Math.Clamp(snapshot.Percent, 0, 100);
        StageText = DescribeStage(snapshot.Stage);
        ProgressDetail = snapshot.Message;
        if (!string.IsNullOrEmpty(snapshot.Encoder)) ActiveEncoderText = snapshot.Encoder;
        SpeedText = snapshot.Speed > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.##}x", snapshot.Speed) : "";
        EtaText = snapshot.Eta is > 0 and < 86400
            ? "预计剩余 " + ((int)Math.Round(snapshot.Eta.Value)) + " 秒" : "";
        HasProgressInfo = true;
        IsIndeterminate = snapshot.Percent <= 0.01 && snapshot.Speed <= 0;
        ProgressText = string.Format(CultureInfo.InvariantCulture, "{0:0.#}%", snapshot.Percent);

        if (_record is not null)
        {
            TaskRecord record = _record;
            _tasks.Update(record, r =>
            {
                r.Progress = Math.Clamp(snapshot.Percent, 0, 100);
                r.Stage = DescribeStage(snapshot.Stage);
                r.Message = snapshot.Message;
            });
        }
    }

    private static string DescribeStage(CompressStage stage) => stage switch
    {
        CompressStage.Validating => "校验参数",
        CompressStage.Probing => "读取媒体信息",
        CompressStage.Planning => "选择编码器",
        CompressStage.Encoding => "压制中",
        CompressStage.Verifying => "校验输出",
        CompressStage.Moving => "写入输出",
        CompressStage.Completed => "已完成",
        CompressStage.Cancelled => "已取消",
        CompressStage.Failed => "失败",
        _ => "准备中",
    };

    private void ShowResult(CompressResult result)
    {
        _progressClosed = true;
        HasResult = true;
        ResultOk = result.Ok;
        if (result.Ok)
        {
            Progress = 100;
            ProgressText = "100%";
            ProgressDetail = result.SummaryText;
            SpeedText = "";
            EtaText = "";
            IsIndeterminate = false;
            StageText = "已完成";
            ActiveEncoderText = result.Encoder;

            var lines = new List<string>
            {
                result.SummaryText,
                "输出：" + result.OutputPath,
                string.Format(CultureInfo.InvariantCulture, "用时 {0:0.#} 秒", result.ElapsedSeconds),
                string.Format(CultureInfo.InvariantCulture, "编码器：{0}（{1}）",
                    result.Encoder, result.EncoderLabel),
            };
            if (result.HardwareEncoded)
                lines.Add(result.HardwareDecoded
                    ? "显卡加速：解码与编码都在显卡上完成"
                    : "显卡加速：仅编码走显卡（解码在 CPU 上）");
            if (result.OutputBitrateKbps > 0)
                lines.Add(string.Format(CultureInfo.InvariantCulture, "输出码率约 {0:0} kbps",
                    result.OutputBitrateKbps));
            lines.AddRange(result.Notices);
            ResultText = string.Join("\n", lines);
            FinishRecord(TaskState.Completed, result.SummaryText);
        }
        else
        {
            StageText = result.Status == CompressStatus.Cancelled ? "已取消" : "失败";
            ResultText = result.Status == CompressStatus.Cancelled
                ? "任务已取消。"
                : "压制失败：" + result.Error;
            FinishRecord(result.Status == CompressStatus.Cancelled ? TaskState.Cancelled : TaskState.Failed,
                         result.Status == CompressStatus.Cancelled ? "已取消" : result.Error);
        }
    }

    private void AppendDiagnostics(CompressResult result)
    {
        if (_record is null) return;
        TaskRecord record = _record;
        var lines = new List<string> { "[编码器] " + result.Encoder + "（" + result.EncoderLabel + "）" };
        lines.Add(result.HardwareEncoded
            ? "[加速] 硬件编码；解码 " + (result.HardwareDecoded ? "硬件" : "软件")
            : "[加速] 未使用硬件加速");
        lines.Add("[音轨] " + (result.AudioCopied ? "原样复制" : "重新编码"));
        foreach (string note in result.Notices) lines.Add("[提示] " + note);
        lines.AddRange(result.Diagnostics);
        lines.Add("[结果] " + result.SummaryText);
        if (!result.Ok) lines.Add("[错误] " + result.Error);
        lines.Add(string.Format(CultureInfo.InvariantCulture, "[耗时] {0:0.#} 秒", result.ElapsedSeconds));

        _tasks.Update(record, r =>
        {
            r.Detail = result.SummaryText;
            r.VerifyFailed = !result.Ok;
        });
        foreach (string line in lines) _tasks.AppendLog(record, line);
    }

    private void FinishRecord(TaskState state, string message)
    {
        if (_record is null || _record.State is TaskState.Completed or TaskState.Failed or TaskState.Cancelled) return;
        TaskRecord record = _record;
        _tasks.Update(record, r => r.Result = null);
        _tasks.Finish(record, state, message.Length == 0 ? record.Stage : message);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (!IsRunning) return;
        if (_record is not null)
        {
            try { _record.Cancellation.Cancel(); } catch (ObjectDisposedException) { /* 刚好收尾 */ }
            _tasks.Finish(_record, TaskState.Cancelled, "已取消");
        }
        try { _runSource?.Cancel(); } catch (ObjectDisposedException) { /* 刚好收尾 */ }
        // 取消是协作式的：Core 要等 ffmpeg 退出才返回，期间仍会回推进度。
        // 不关掉进度通道，界面就会继续往前走，看起来像「取消无效」。
        _progressClosed = true;
        IsIndeterminate = false;
        StageText = "正在取消…";
        ProgressDetail = "正在等待当前步骤安全退出…";
        SpeedText = "";
        EtaText = "";
        CanCancel = false;
    }

    #endregion

    #region 初始化

    public void Initialize()
    {
        _loading = true;
        try
        {
            VerifyOutput = _settings.CompressVerify;
            Container = _settings.CompressContainer;
            Codec = _settings.CompressCodec == "Hevc" ? VideoCodecKind.Hevc : VideoCodecKind.H264;
            EncoderPreference = _settings.CompressEncoder;
            Backend = _settings.CompressBackend;
            HardwareQuality = _settings.CompressHardwareQuality;
            Quality = _settings.CompressQuality;
            RateControl = _settings.CompressRateControl == "Bitrate" ? RateControl.Bitrate : RateControl.Quality;
            BitrateKbps = _settings.CompressBitrateKbps;
            Preset = _settings.CompressPreset;
            Resize = _settings.CompressResize switch
            {
                "LongEdge" => CompressResize.LongEdge,
                "Fit" => CompressResize.Fit,
                "Scale" => CompressResize.Scale,
                _ => CompressResize.None,
            };
            LongEdge = double.TryParse(_settings.CompressLongEdge, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double edge) ? edge : 1080;
            FrameRate = _settings.CompressFrameRate;
            SelectedFrameRate = FrameRate <= 0
                ? "保持原样"
                : FrameRate.ToString("0.###", CultureInfo.InvariantCulture);
            ReencodeAudio = _settings.CompressReencodeAudio;
            AudioBitrate = _settings.CompressAudioBitrate;
            HardwareDecodeEnabled = _settings.CompressHardwareDecode;
            if (OutputDirectory.Length == 0) OutputDirectory = _settings.CompressOutputDir;
        }
        finally
        {
            _loading = false;
        }

        // 下拉框的选中项由业务值反推（_loading 期间 setter 会跳过回写）。
        SelectedCodec = CodecOptions.FirstOrDefault(c => c.Value == Codec) ?? CodecOptions[0];
        SelectedPreference = PreferenceOptions.FirstOrDefault(o => o.Value == EncoderPreference) ?? PreferenceOptions[0];
        SelectedBackend = Backend.Length == 0
            ? BackendOptions[0]
            : BackendOptions.FirstOrDefault(o => o.Value.ToString() == Backend) ?? BackendOptions[0];
        SelectedRate = RateOptions.FirstOrDefault(o => o.Value == RateControl) ?? RateOptions[0];
        SelectedResize = ResizeOptions.FirstOrDefault(o => o.Value == Resize) ?? ResizeOptions[0];
        RefreshPlan();
    }

    /// <summary>设置页改动后把新的默认值同步进来。</summary>
    public void ApplyDefaults()
    {
        if (IsRunning) return;
        Quality = _settings.CompressQuality;
        BitrateKbps = _settings.CompressBitrateKbps;
        Preset = _settings.CompressPreset;
        HardwareQuality = _settings.CompressHardwareQuality;
        AudioBitrate = _settings.CompressAudioBitrate;
        RefreshPlan();
    }

    /// <summary>把当前页面的选择写回设置（用户改过才需要）。</summary>
    public void PersistSettings()
    {
        _settings.CompressContainer = Container;
        _settings.CompressCodec = Codec == VideoCodecKind.Hevc ? "Hevc" : "H264";
        _settings.CompressEncoder = EncoderPreference;
        _settings.CompressBackend = Backend;
        _settings.CompressHardwareQuality = HardwareQuality;
        _settings.CompressQuality = Quality;
        _settings.CompressRateControl = RateControl == RateControl.Bitrate ? "Bitrate" : "Quality";
        _settings.CompressBitrateKbps = BitrateKbps;
        _settings.CompressPreset = Preset;
        _settings.CompressResize = Resize switch
        {
            CompressResize.LongEdge => "LongEdge",
            CompressResize.Fit => "Fit",
            CompressResize.Scale => "Scale",
            _ => "None",
        };
        _settings.CompressLongEdge = ((int)LongEdge).ToString(CultureInfo.InvariantCulture);
        _settings.CompressFrameRate = FrameRate;
        _settings.CompressReencodeAudio = ReencodeAudio;
        _settings.CompressAudioBitrate = AudioBitrate;
        _settings.CompressHardwareDecode = HardwareDecodeEnabled;
        _settings.CompressVerify = VerifyOutput;
        _settings.CompressOutputDir = OutputDirectory;
    }

    #endregion
}
