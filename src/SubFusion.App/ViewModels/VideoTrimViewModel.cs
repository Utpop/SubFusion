using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubFusion.App.Services;
using SubFusion.Core;
using SubFusion.Core.Media;
using SubFusion.Core.Services;
using SubFusion.Core.Trim;

namespace SubFusion.App.ViewModels;

/// <summary>旋转下拉项：枚举值 + 中文标签（直接绑枚举会显示 Clockwise90 之类的英文名）。</summary>
public sealed record RotationOption(RotationMode Value, string Label);

/// <summary>裁剪方式下拉项。</summary>
public sealed record TrimModeOption(TrimMode Value, string Label, string Hint);

/// <summary>
/// 视频裁剪工作区的 ViewModel。
///
/// 与 Core 的边界（本页最关键的设计约束）：
/// - 只构造 <see cref="VideoTrimOptions"/> 并调用 <see cref="VideoTrimTask"/>；
/// - 关键帧吸附、旋转策略、音画同步判定全部由 Core 决定，这里只展示结论；
/// - 界面绝不自行猜测「切点会不会被吸附」——那是 Core 用真实关键帧算出来的。
///
/// 本页的核心诚实性要求：无损切割的切点只能落在关键帧上，
/// 用户拖到 12.34s 时实际可能从 12.00s 开始。因此每次
/// <see cref="StartInput"/> / <see cref="EndInput"/> 变化都要立刻重算并显示
/// 「实际会切哪里」，而不是等用户点了开始才告诉他时长不对。
/// </summary>
public sealed partial class VideoTrimViewModel : ObservableObject
{
    private readonly IUiServices _ui;
    private readonly AppSettings _settings;
    private readonly TaskCenter _tasks;

    private VideoTrimTask? _task;
    private TaskRecord? _record;
    private CancellationTokenSource? _runSource;
    private CancellationTokenSource? _runLinked;
    private CancellationTokenSource? _analyzeSource;

    private MediaBundle? _bundle;
    private KeyframeIndex _keyframes = new();
    private int? _sourceRotation;

    private bool _loading;
    private bool _outputNameUserEdited;

    public VideoTrimViewModel(IUiServices ui, AppSettings settings, TaskCenter tasks)
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
            try { return Path.GetFileName(InputPath); } catch { return InputPath; }
        }
    }

    [ObservableProperty] private bool analyzing;
    [ObservableProperty] private string analysisError = "";
    [ObservableProperty] private string mediaInfoText = "";
    [ObservableProperty] private string keyframeText = "";
    [ObservableProperty] private string rotationText = "";

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
        IReadOnlyList<string> paths = await _ui.PickFilesAsync("选择要裁剪的视频", extensions);
        string? path = paths.FirstOrDefault();
        if (!string.IsNullOrEmpty(path)) InputPath = path;
    }

    /// <summary>拖放入口：只接受第一个视频文件。</summary>
    public void AcceptDroppedFile(IReadOnlyList<string> paths)
    {
        string? video = paths.FirstOrDefault(p => Constants.VideoExts.Contains(Path.GetExtension(p)));
        if (video is not null) InputPath = video;
    }

    /// <summary>载入输入：探测媒体信息、扫描关键帧、读取源旋转角，并给出默认区间与输出名。</summary>
    private async Task LoadInputAsync()
    {
        if (InputPath.Length == 0)
        {
            _bundle = null;
            _keyframes = new KeyframeIndex();
            _sourceRotation = 0;
            MediaInfoText = "";
            KeyframeText = "";
            RotationText = "";
            AnalysisError = "";
            StartInput = "";
            EndInput = "";
            OutputName = "";
            OutputPath = "";
            return;
        }

        CancelAnalyze();
        var cts = new CancellationTokenSource();
        _analyzeSource = cts;
        Analyzing = true;
        AnalysisError = "";

        try
        {
            VideoTrimTask? task = await EnsureTaskAsync(cts.Token);
            if (task is null)
            {
                AnalysisError = "未找到 ffprobe，无法读取媒体信息。请在「设置 → 媒体工具」中指定路径。";
                return;
            }

            (MediaBundle bundle, KeyframeIndex keyframes, int? rotation) =
                await task.AnalyzeAsync(InputPath, cts.Token);
            if (cts.IsCancellationRequested) return;

            // 编码器探测与媒体分析共用同一个取消令牌：用户换文件时探测也应作废。
            await RefreshEncodersAsync(cts.Token);
            if (cts.IsCancellationRequested) return;

            _bundle = bundle;
            _keyframes = keyframes;
            _sourceRotation = rotation;

            _ui.RunOnUiThread(() =>
            {
                if (!bundle.Success)
                {
                    AnalysisError = "无法读取该文件：" + bundle.Error;
                    MediaInfoText = "";
                    return;
                }
                // 通知时间轴标尺刷新：重新分析同一文件且起止字符串未变时，
                // StartInput/EndInput 的 setter 不会触发，只有这些派生属性能驱动刷新。
                OnPropertyChanged(nameof(SourceDuration));
                OnPropertyChanged(nameof(KeyframeTimes));
                OnPropertyChanged(nameof(SnapToKeyframes));
                MediaInfoText = DescribeMedia(bundle);
                KeyframeText = keyframes.Describe();
                RotationText = rotation is null
                    ? "无法读取显示方向（ffprobe 未给出结果；竖屏素材可能丢失方向）"
                    : rotation == 0
                        ? "无显示方向（正常横屏素材）"
                        : "源文件自带 " + rotation + "° 显示方向（竖屏素材，裁剪时会原样保留）";

                // 输出默认落在源文件旁边：绝大多数场景是「就地截一段」，
                // 留空会让输出路径为空、开始按钮不可用，用户还得再点一次「浏览」。
                if (OutputDirectory.Length == 0)
                {
                    string? dir = null;
                    try { dir = Path.GetDirectoryName(InputPath); } catch { /* 路径异常时留空 */ }
                    if (!string.IsNullOrEmpty(dir)) OutputDirectory = dir;
                }

                // 默认裁剪整个文件：用户通常会先选文件再拖时间轴。
                StartInput = "00:00:00.000";
                EndInput = FormatTime(bundle.Duration);
                if (!_outputNameUserEdited) SetOutputName(SuggestOutputName(), userEdit: false);
                RefreshPlan();
                // 载入后先给一张首帧，空窗口会让人以为功能没生效
                RequestPreview(0);
            });
        }
        catch (OperationCanceledException) { /* 切换输入时的正常取消 */ }
        catch (Exception ex)
        {
            _ui.RunOnUiThread(() => AnalysisError = "分析失败：" + ex.Message);
        }
        finally
        {
            // 只有「当前这一代」才有权熄灭加载指示。
            // 连续切换输入时，上一代的 finally 会晚于下一代启动才执行；
            // 无条件写 Analyzing = false 会把新一代刚点亮的加载状态抹掉，
            // 界面于是显示「B 的文件名 + A 的参数」，开始按钮也已可用。
            if (ReferenceEquals(_analyzeSource, cts))
            {
                _analyzeSource = null;
                cts.Dispose();
                _ui.RunOnUiThread(() => Analyzing = false);
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

    private static string DescribeMedia(MediaBundle bundle)
    {
        VideoStreamInfo? v = bundle.PrimaryVideo;
        if (v is null) return "无视频流";
        string video = string.Format(CultureInfo.InvariantCulture,
            "{0} · {1}×{2} · {3:0.###}fps · {4}", v.Codec.ToUpperInvariant(), v.Width, v.Height,
            v.EffectiveFrameRate, v.PixFmt);
        AudioStreamInfo? a = bundle.PrimaryAudio;
        string audio = a is null ? "无音轨"
            : string.Format(CultureInfo.InvariantCulture, "{0} · {1} kHz · {2} ch",
                a.Codec.ToUpperInvariant(), a.SampleRate / 1000.0, a.Channels);
        string space = Formatting.FormatSize(bundle.Container.Size);
        return video + "\n" + audio + " · 时长 " + FormatTime(bundle.Duration) + " · " + space;
    }

    /// <summary>
    /// 探测本机可用的视频编码器，刷新下拉框。
    ///
    /// 必须在载入媒体之后、用户真正点「开始裁剪」之前完成：
    /// 硬件编码器的可用性只能靠真编码一次来判定，放在开始按钮里做会让用户
    /// 先等一次探测、然后才发现自己选的编码器不可用。
    /// 探测失败（没有 ffmpeg）时保持软件保底项，不影响无损裁剪。
    /// </summary>
    private async Task RefreshEncodersAsync(CancellationToken cancellationToken)
    {
        if (_encoders is not null) return;

        string ffmpeg = _settings.FfmpegPath;
        if (string.IsNullOrEmpty(ffmpeg)) return;

        try
        {
            _encoders = await VideoEncoder.ProbeAsync(ffmpeg, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { return; }

        if (cancellationToken.IsCancellationRequested) return;

        _ui.RunOnUiThread(() =>
        {
            var names = new List<string>();
            // 软件编码器保底：即使探测一个都没过，也不能让用户无编码器可选。
            names.Add("libx264");
            names.Add("libx265");
            foreach (EncoderAvailability e in _encoders)
            {
                if (!e.Usable) continue;
                if (e.Kind == EncoderKind.Software) continue;
                names.Add(e.Name);
            }

            Codecs = names;
            if (!names.Contains(Codec)) Codec = "libx264";

            int hardware = _encoders.Count(e => e.Usable && e.Kind != EncoderKind.Software);
            // 代码内部用行首缩进描述结论；这里只给用户一句话。
            EncoderHint = hardware > 0
                ? "检测到 " + hardware + " 个可用的显卡编码器：精确裁剪时可选用它们加速（无损裁剪本来就不编码）。"
                : "未检测到可用的显卡编码器，精确裁剪将使用 CPU 编码。";
        });
    }

    private async Task<VideoTrimTask?> EnsureTaskAsync(CancellationToken cancellationToken)
    {
        if (_task is not null) return _task;
        try
        {
            _task = await VideoTrimTask.CreateAsync(_settings.FfmpegPath, _settings.FfprobePath,
                                                    cancellationToken);
            return _task;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>设置里的工具路径变化后需要重建任务。</summary>
    public void InvalidateTools()
    {
        _task = null;
        if (InputPath.Length > 0) _ = LoadInputAsync();
    }

    #endregion

    #region 裁剪参数

    /// <summary>时间输入框。接受 12.5 / 00:00:12.500 / 1:02:03.4 三种写法。</summary>
    [ObservableProperty] private string startInput = "";
    [ObservableProperty] private string endInput = "";

    partial void OnStartInputChanged(string value) => RefreshPlan();
    partial void OnEndInputChanged(string value) => RefreshPlan();

    /// <summary>源文件总时长（供时间轴标定）。未加载时为 0。</summary>
    public double SourceDuration => _bundle?.Success == true ? _bundle.Duration : 0;

    /// <summary>关键帧时间点，供时间轴画刻度与吸附。</summary>
    public IReadOnlyList<double> KeyframeTimes => _keyframes.Ok ? _keyframes.Times : Array.Empty<double>();

    /// <summary>关键帧索引是否可用于吸附。</summary>
    public bool SnapToKeyframes => Mode == TrimMode.Copy && _keyframes.Ok && !_keyframes.AllIntra;

    /// <summary>
    /// 时间轴拖拽后的入口：把拖动结果写回两个输入框。
    /// 拖动优先，因此这里不判断「用户是否正在输入」——用户正在拖的时候不会同时在打字。
    /// </summary>
    public void ApplyRangeFromTimeline(double start, double end)
    {
        string s = FormatTime(start);
        string e = FormatTime(end);
        if (!string.Equals(StartInput, s, StringComparison.Ordinal)) StartInput = s;
        if (!string.Equals(EndInput, e, StringComparison.Ordinal)) EndInput = e;
        // 预览跟着起点走：用户拖动时最想确认的就是「这段的起点长什么样」。
        // RequestPreview 内部有节流，这里高频调用是安全的。
        RequestPreview(start);
    }

    /// <summary>当前解析出的区间；解析失败时返回 false。</summary>
    public bool TryGetRange(out double start, out double end)
    {
        start = 0;
        end = 0;
        return TrimTime.TryParse(StartInput, out start) && TrimTime.TryParse(EndInput, out end);
    }

    /// <summary>把区间设为整段（时间轴上的「全选」）。</summary>
    public void SelectAllRange()
    {
        if (SourceDuration <= 0) return;
        ApplyRangeFromTimeline(0, SourceDuration);
    }

    public IReadOnlyList<TrimModeOption> Modes { get; } = new[]
    {
        new TrimModeOption(TrimMode.Copy, "无损切割（不重新编码）",
            "仅复制码流，画质逐比特不变，速度最快。切点必须落在关键帧上，因此实际起点可能早于请求值。"),
        new TrimModeOption(TrimMode.Precise, "精确切割（重编码所选区间）",
            "按请求的时间点精确切割，仅重编码选中的片段。画质有轻微损失，耗时取决于片段长度。"),
    };

    [ObservableProperty] private TrimMode mode = TrimMode.Copy;
    /// <summary>与下拉框绑定的对象；变化时同步到 <see cref="Mode"/>。</summary>
    [ObservableProperty] private TrimModeOption? selectedMode;

    partial void OnModeChanged(TrimMode value)
    {
        SelectedMode = Modes.FirstOrDefault(m => m.Value == value) ?? Modes[0];
        OnPropertyChanged(nameof(IsPrecise));
        // SnapToKeyframes 依赖 Mode：不通知它，TrimPage 就收不到属性变更，
        // Timeline.SnapEnabled 会停在旧值——切到「精确切割」后拖动条仍在吸附关键帧，
        // 用户以为能精确到帧却拖不到位；反向切回无损模式则失去吸附。
        OnPropertyChanged(nameof(SnapToKeyframes));
        RefreshPlan();
    }

    partial void OnSelectedModeChanged(TrimModeOption? value)
    {
        if (value is not null && value.Value != Mode) Mode = value.Value;
    }

    /// <summary>true 表示精确模式：编码参数才有意义。</summary>
    public bool IsPrecise => Mode == TrimMode.Precise;

    [ObservableProperty] private RotationMode rotation = RotationMode.None;
    [ObservableProperty] private RotationOption? selectedRotation;

    public IReadOnlyList<RotationOption> Rotations { get; } = new[]
    {
        new RotationOption(RotationMode.None, "不旋转（保留原方向）"),
        new RotationOption(RotationMode.Clockwise90, "顺时针 90°"),
        new RotationOption(RotationMode.Half, "旋转 180°"),
        new RotationOption(RotationMode.CounterClockwise90, "逆时针 90°"),
    };

    partial void OnRotationChanged(RotationMode value)
    {
        SelectedRotation = Rotations.FirstOrDefault(r => r.Value == value) ?? Rotations[0];
        RefreshPlan();
    }

    partial void OnSelectedRotationChanged(RotationOption? value)
    {
        if (value is not null && value.Value != Rotation) Rotation = value.Value;
    }

    public IReadOnlyList<string> Containers { get; } = new[] { "mkv", "mp4" };
    [ObservableProperty] private string container = "mkv";

    partial void OnContainerChanged(string value)
    {
        SyncOutputExtension();
        RefreshPlan();
    }

    /// <summary>
    /// 可选的精确模式编码器。
    ///
    /// 候选来自两处：本机真实探测出的可用编码器（<see cref="VideoEncoder.ProbeAsync"/>），
    /// 加上软件保底项。硬件编码器必须先真的编码一次才能判定可用——
    /// 只看 ffmpeg -encoders 列表会把「有这个编码器但没有显卡」当成可用，
    /// 用户选完才在真正开始时失败。
    ///
    /// 无损模式（Copy）不编码，这一项在界面上是禁用的。
    /// </summary>
    [ObservableProperty] private IReadOnlyList<string> codecs = new[] { "libx264", "libx265" };

    [ObservableProperty] private IReadOnlyList<string> presets = DefaultPresets;

    private static readonly string[] DefaultPresets =
        { "ultrafast", "veryfast", "fast", "medium", "slow", "veryslow" };

    private static readonly string[] HardwareQualities = { "balanced", "speed", "quality" };

    /// <summary>编码器探测结果缓存：同一台机器一次会话内只需探测一次。</summary>
    private IReadOnlyList<EncoderAvailability>? _encoders;

    [ObservableProperty] private string encoderHint = "";

    [ObservableProperty] private string codec = "libx264";
    [ObservableProperty] private double crf = 20;
    [ObservableProperty] private string preset = "medium";

    partial void OnCodecChanged(string value)
    {
        // 软件是 x264 preset 名，硬件是 balanced/speed/quality——不能混用。
        bool hardware = VideoEncoder.IsHardware(value);
        Presets = hardware ? HardwareQualities : DefaultPresets;
        if (!Presets.Contains(Preset)) Preset = Presets[0];
        // 硬件编码器的"画质"是 QP，同一个数字比 CRF 更糊/更糊的换算由 Core 做，
        // 界面上只需如实说明，避免用户以为切到显卡后画质完全不变。
        EncoderHint = hardware
            ? "使用显卡编码：速度快很多，同画质下的体积通常比 CPU 编码略大。"
            : "";
        RefreshPlan();
    }

    [ObservableProperty] private bool verifyOutput = true;
    [ObservableProperty] private bool keepSubtitles;

    partial void OnVerifyOutputChanged(bool value) => RefreshPlan();

    #endregion

    #region 预览

    /// <summary>
    /// 预览帧（JPEG 字节）。用字节而不是 ImageSource 是为了让 ViewModel 不引用 WinUI：
    /// 页面把它包成 BitmapImage 再交给 Image 控件。
    /// </summary>
    [ObservableProperty] private byte[]? previewFrame;
    [ObservableProperty] private string previewCaption = "";
    [ObservableProperty] private bool previewLoading;

    private CancellationTokenSource? _previewSource;
    private double _previewPendingSeconds = -1;
    private bool _previewBusy;

    /// <summary>预览帧宽度（像素）。跟随时长轴刷新，太大反而拖慢解码。</summary>
    public int PreviewWidth { get; set; } = 480;

    /// <summary>
    /// 请求刷新预览（拖动时间轴时高频调用）。
    ///
    /// 必须节流：拖动每移动一像素就起一次 ffmpeg 会瞬间堆出上百个进程，
    /// 界面直接卡死。这里只保留「最后一次请求」，上一次还没解码完就记下新位置，
    /// 解码完再接着追——用户始终看到最终停下的位置，中间帧丢掉无所谓。
    /// </summary>
    public void RequestPreview(double seconds)
    {
        _previewPendingSeconds = seconds;
        if (_previewBusy) return;
        _ = PreviewLoopAsync();
    }

    private async Task PreviewLoopAsync()
    {
        _previewBusy = true;
        try
        {
            while (_previewPendingSeconds >= 0)
            {
                double seconds = _previewPendingSeconds;
                _previewPendingSeconds = -1;

                VideoTrimTask? task = _task;
                string ffmpeg = _settings.FfmpegPath;
                if (task is null && InputPath.Length > 0) task = await EnsureTaskAsync(CancellationToken.None);
                if (task is not null) ffmpeg = task.Tools.Ffmpeg;
                if (ffmpeg.Length == 0 || InputPath.Length == 0) return;

                PreviewLoading = true;
                _previewSource?.Cancel();
                var cts = new CancellationTokenSource();
                _previewSource = cts;

                byte[]? jpeg;
                try
                {
                    jpeg = await FrameGrabber.GrabJpegAsync(ffmpeg, InputPath, seconds, PreviewWidth,
                                                            cts.Token);
                }
                catch (OperationCanceledException) { continue; }
                catch { jpeg = null; }

                if (cts.IsCancellationRequested) continue;

                _ui.RunOnUiThread(() =>
                {
                    PreviewLoading = false;
                    if (jpeg is not null)
                    {
                        PreviewFrame = jpeg;
                        PreviewCaption = FormatTime(seconds);
                    }
                    else if (PreviewFrame is null)
                    {
                        PreviewCaption = "无法读取预览";
                    }
                });
            }
        }
        finally
        {
            _previewBusy = false;
            _previewSource?.Dispose();
            _previewSource = null;
        }
    }

    #endregion

    #region 计划（来自 Core 的真实计算）

    [ObservableProperty] private string planText = "";
    [ObservableProperty] private string planDetail = "";
    [ObservableProperty] private bool planSnaps;
    [ObservableProperty] private bool hasPlan;
    [ObservableProperty] private string planError = "";
    [ObservableProperty] private double previewDuration;
    [ObservableProperty] private bool canStart;

    /// <summary>
    /// 重算「实际会切哪里」。纯计算，不起子进程，因此可以在每次输入变化时立即调用。
    /// </summary>
    private void RefreshPlan()
    {
        PlanError = "";
        HasPlan = false;
        PlanSnaps = false;
        CanStart = false;

        if (_bundle is null || !_bundle.Success)
        {
            PlanText = "";
            PlanDetail = "";
            StartCommand.NotifyCanExecuteChanged();
            return;
        }

        if (!TryParseTime(StartInput, out double start) || !TryParseTime(EndInput, out double end))
        {
            PlanError = "时间格式无法识别：可以填 12.5、00:00:12.500 或 1:02:03.4";
            PlanText = "";
            PlanDetail = "";
            StartCommand.NotifyCanExecuteChanged();
            return;
        }

        double sourceDuration = _bundle.Duration;
        // 终点收敛走 Core 的统一实现：界面、Validate、命令构造三处必须是同一份结果，
        // 否则会出现「界面可以点开始、校验却报时长不足」这种自相矛盾。
        var planned = new VideoTrimOptions { StartSeconds = start, EndSeconds = end };
        double clampedEnd = planned.NormalizeRange(sourceDuration).End;
        if (start < 0 || clampedEnd <= start)
        {
            PlanError = "终点必须大于起点";
            PlanText = "";
            PlanDetail = "";
            StartCommand.NotifyCanExecuteChanged();
            return;
        }
        if (start >= sourceDuration)
        {
            PlanError = string.Format(CultureInfo.InvariantCulture,
                "起点超出文件时长（{0}）", FormatTime(sourceDuration));
            StartCommand.NotifyCanExecuteChanged();
            return;
        }

        // 无损模式下真实切点 = 关键帧吸附结果；精确模式就是请求值。
        // 这个数字必须来自 Core 的关键帧索引，界面不能自己猜。
        double actualStart = start;
        string snapNote = "";
        bool snaps = false;
        if (Mode == TrimMode.Copy && _keyframes.Ok && !_keyframes.AllIntra)
        {
            actualStart = _keyframes.SnapToKeyframe(start);
            snaps = Math.Abs(actualStart - start) > 1e-3;
            if (snaps)
                snapNote = string.Format(CultureInfo.InvariantCulture,
                    "起点前移至关键帧 {0}（多包含 {1:0.###} 秒画面）",
                    FormatTime(actualStart), start - actualStart);
        }

        double actualDuration = clampedEnd - actualStart;
        if (actualDuration < 0.05)
        {
            PlanError = "对齐至关键帧后剩余时长不足 0.05 秒，请将起点提前或终点延后";
            StartCommand.NotifyCanExecuteChanged();
            return;
        }

        // 未能读取方向时按 0° 参与预览计划（与引擎执行口径一致），
        // 真实执行会在 notices 里说明「未能读取源显示方向」。
        RotationPlan rotationPlan = RotationPlan.Create(_sourceRotation ?? 0, Rotation, Mode == TrimMode.Copy);

        HasPlan = true;
        PlanSnaps = snaps;
        PreviewDuration = actualDuration;
        PlanText = string.Format(CultureInfo.InvariantCulture,
            "实际输出 {0} → {1}，共 {2}",
            FormatTime(actualStart), FormatTime(clampedEnd), FormatTime(actualDuration));

        var parts = new List<string>();
        if (snapNote.Length > 0) parts.Add(snapNote);
        if (clampedEnd < end - 0.01)
            parts.Add("终点超出文件时长，已按 " + FormatTime(sourceDuration) + " 截止");
        parts.Add(rotationPlan.Describe());
        if (!_keyframes.Ok)
            parts.Add("未能扫描关键帧，实际切点由 ffmpeg 决定");
        else if (_keyframes.AllIntra)
            parts.Add("素材是全关键帧，无损切割不会多带画面");
        if (Mode == TrimMode.Copy)
            parts.Add("音频与视频一起搬运，不会被分开处理，因此不会出现音画不同步");
        else
            parts.Add("视频与音频一起重新编码，避免两条流对齐粒度不同造成错位");

        PlanDetail = string.Join("\n", parts);
        CanStart = !IsRunning;
        StartCommand.NotifyCanExecuteChanged();
    }

    // 解析与格式化都在 Core 里（TrimTime），这样自检能覆盖它；
    // 这里只做转发，避免两处各写一套导致行为分叉。
    /// <summary>解析用户输入的时间：纯秒数、mm:ss(.fff) 或 hh:mm:ss(.fff)。</summary>
    public static bool TryParseTime(string? text, out double seconds) => TrimTime.TryParse(text, out seconds);

    /// <summary>秒 → hh:mm:ss.fff，输入框里也用它表达默认值。</summary>
    public static string FormatTime(double seconds) => TrimTime.Format(seconds);

    #endregion

    #region 输出

    [ObservableProperty] private string outputDirectory = "";
    [ObservableProperty] private string outputName = "";
    [ObservableProperty] private string outputPath = "";

    partial void OnOutputDirectoryChanged(string value) { SyncOutputExtension(); StartCommand.NotifyCanExecuteChanged(); }
    partial void OnOutputNameChanged(string value)
    {
        if (!_loading) _outputNameUserEdited = value.Trim().Length > 0;
        SyncOutputExtension();
        StartCommand.NotifyCanExecuteChanged();
    }

    private string SuggestOutputName()
    {
        string stem;
        try { stem = Path.GetFileNameWithoutExtension(InputPath); }
        catch { stem = "clip"; }
        if (stem.Length == 0) stem = "clip";
        return stem + "_trim";
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

        string extension = Container == "mp4" ? ".mp4" : ".mkv";
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            string withoutExt = Path.HasExtension(name) ? name[..^Path.GetExtension(name).Length] : name;
            name = withoutExt + extension;
        }
        try
        {
            OutputPath = Path.Combine(dir, name);
            // 这里不能无条件清空 PlanError：用户是在「区间非法」导致 PlanError 有内容时
            // 才去改输出目录的，清掉它会让界面变回「无提示 + 开始按钮永久灰」。
            // 重新跑一次完整计划计算，让区间合法性重新决定 PlanError 与 CanStart。
            RefreshPlan();
        }
        catch (Exception ex)
        {
            // 路径拼不出来时按钮会直接变灰，界面却一个字都不说，用户只会以为程序坏了。
            // 把原因留在 PlanError 上，由计划区显示出来。
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
                                                    Container == "mp4" ? ".mp4" : ".mkv", OutputDirectory);
        if (string.IsNullOrEmpty(path)) return;
        OutputDirectory = Path.GetDirectoryName(path) ?? OutputDirectory;
        SetOutputName(Path.GetFileName(path), userEdit: true);
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

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    private bool CanStartCommand() => CanStart && !IsRunning && OutputPath.Length > 0;

    private VideoTrimOptions BuildOptions()
    {
        TryParseTime(StartInput, out double start);
        TryParseTime(EndInput, out double end);
        return new VideoTrimOptions
        {
            Input = InputPath,
            OutputPath = OutputPath,
            StartSeconds = start,
            EndSeconds = end,
            Mode = Mode,
            Rotation = Rotation,
            Container = Container,
            VerifyOutput = VerifyOutput,
            KeepSubtitles = KeepSubtitles,
            VideoCodec = Codec,
            Preset = Preset,
            Crf = (int)Crf,
        };
    }

    [RelayCommand(CanExecute = nameof(CanStartCommand))]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        VideoTrimOptions options = BuildOptions();
        List<string> errors = options.Validate();
        if (errors.Count > 0)
        {
            await _ui.ShowErrorAsync("参数不完整：\n\n" + string.Join("\n", errors.Select(e => "· " + e)));
            return;
        }

        VideoTrimTask? task = await EnsureTaskAsync(CancellationToken.None);
        if (task is null)
        {
            await _ui.ShowErrorAsync("未找到 ffmpeg / ffprobe，请在「设置 → 媒体工具」中指定路径。");
            return;
        }

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

        string strategy = Mode == TrimMode.Copy ? "无损切割" : "精确切割";
        _record = _tasks.Add(TaskKind.VideoTrim,
            $"视频裁剪 · {InputName}",
            $"{FormatTime(options.StartSeconds)} → {FormatTime(options.EndSeconds)} · {strategy}",
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
            r.Message = strategy;
        });

        _runSource = new CancellationTokenSource();
        _runLinked = CancellationTokenSource.CreateLinkedTokenSource(
            _runSource.Token, _record.Cancellation.Token);

        try
        {
            TrimResult result = await task.RunAsync(options, new Progress<TrimProgress>(OnProgress),
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
            ResultText = "裁剪失败：" + ex.Message;
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

    /// <summary>进度回调（任意线程）；合并到约 10 Hz 再刷 UI。</summary>
    private void OnProgress(TrimProgress progress) => _ui.RunOnUiThread(() => ApplyProgress(progress));

    private DateTime _lastFlush = DateTime.MinValue;
    // 终态定稿后不再接受迟到的进度回调，避免已入队的旧快照把"已完成/100%"回退。
    private bool _progressClosed;

    private void ApplyProgress(TrimProgress snapshot)
    {
        if (_progressClosed) return;
        // 终态不能因为节流被丢掉：最后一条「完成 / 100%」一旦漏掉，
        // 界面就永远停在前一条上，任务成功也会显示成卡住。
        bool terminal = snapshot.Stage is TrimStage.Completed or TrimStage.Failed or TrimStage.Cancelled
                        || ProgressPacer.IsTerminalPercent(snapshot.Percent);
        DateTime now = DateTime.UtcNow;
        if (!ProgressPacer.ShouldDeliver(now - _lastFlush, terminal)) return;
        _lastFlush = now;

        Progress = Math.Clamp(snapshot.Percent, 0, 100);
        StageText = DescribeStage(snapshot.Stage);
        ProgressDetail = snapshot.Message;
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

    private static string DescribeStage(TrimStage stage) => stage switch
    {
        TrimStage.Validating => "校验参数",
        TrimStage.Probing => "读取媒体信息",
        TrimStage.ScanningKeyframes => "扫描关键帧",
        TrimStage.Planning => "计算切点",
        TrimStage.Cutting => "裁剪中",
        TrimStage.Verifying => "校验输出",
        TrimStage.Completed => "已完成",
        TrimStage.Cancelled => "已取消",
        TrimStage.Failed => "失败",
        _ => "准备中",
    };

    private void ShowResult(TrimResult result)
    {
        _progressClosed = true;
        HasResult = true;
        ResultOk = result.Ok;
        if (result.Ok)
        {
            // 进度区在这里定稿：任务结束后不会再有进度回调来覆盖上一阶段的文字，
            // 不收尾就会停成「已完成 / 定位切点… / 62%」这种自相矛盾的画面。
            Progress = 100;
            ProgressText = "100%";
            ProgressDetail = result.SummaryText;
            SpeedText = "";
            EtaText = "";
            IsIndeterminate = false;
            StageText = "已完成";
            var lines = new List<string>
            {
                result.SummaryText,
                "输出：" + result.OutputPath,
                string.Format(CultureInfo.InvariantCulture, "用时 {0:0.#} 秒", result.ElapsedSeconds),
            };
            // 成功时不要把偏差数值写成「告警」。
            // 这个数字含 0.06~0.15 秒的编码/容器对齐偏差（AAC priming 等），
            // 属于测量噪声；一行带负号的数字会让人以为出了问题。
            // 因此只在确实接近容差时才提示，其余情况用一句结论代替。
            if (result.SyncVerified)
            {
                double absDrift = Math.Abs(result.SyncDrift);
                lines.Add(absDrift <= 0.05
                    ? "音画同步：已核实，无错位"
                    : string.Format(CultureInfo.InvariantCulture,
                        "音画同步：已核实，无错位（测量偏差 {0:+0.000;-0.000;0.000} 秒，" +
                        "属编码/容器对齐的正常范围）", result.SyncDrift));
            }
            lines.AddRange(result.Notices);
            ResultText = string.Join("\n", lines);
            FinishRecord(TaskState.Completed, result.SummaryText);
        }
        else
        {
            StageText = result.Status == TrimStatus.Cancelled ? "已取消" : "失败";
            ResultText = result.Status == TrimStatus.Cancelled
                ? "任务已取消。"
                : "裁剪失败：" + result.Error;
            FinishRecord(result.Status == TrimStatus.Cancelled ? TaskState.Cancelled : TaskState.Failed,
                         result.Status == TrimStatus.Cancelled ? "已取消" : result.Error);
        }
    }

    private void AppendDiagnostics(TrimResult result)
    {
        if (_record is null) return;
        TaskRecord record = _record;
        var lines = new List<string>
        {
            $"[方式] {(result.VideoCopied ? "无损切割（stream copy）" : "精确切割（重编码所选区间）")}",
        };
        if (result.Snapped)
            lines.Add($"[切点] 请求 {FormatTime(result.RequestedStart)} → 实际 {FormatTime(result.ActualStart)}（已吸附关键帧）");
        foreach (string note in result.Notices) lines.Add("[提示] " + note);
        lines.AddRange(result.Diagnostics);
        lines.Add("[结果] " + result.SummaryText);
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
        // 取消是协作式的：Core 要等 ffmpeg 进程退出才返回，期间仍会回推进度。
        // 不关掉进度通道，界面就会继续往前走，看起来像「取消无效」。
        _progressClosed = true;
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
        else _ui.ShowInfoAsync("输出目录尚不存在。");
    }

    #endregion

    #region 初始化

    public void Initialize()
    {
        _loading = true;
        try
        {
            VerifyOutput = _settings.TrimVerify;
            Container = _settings.TrimContainer;
            Codec = _settings.TrimCodec;
            Crf = _settings.TrimCrf;
            Preset = _settings.TrimPreset;
            Mode = _settings.TrimMode switch
            {
                "Precise" => TrimMode.Precise,
                _ => TrimMode.Copy,
            };
            Rotation = _settings.TrimRotation switch
            {
                "90" => RotationMode.Clockwise90,
                "180" => RotationMode.Half,
                "270" => RotationMode.CounterClockwise90,
                _ => RotationMode.None,
            };
            if (OutputDirectory.Length == 0) OutputDirectory = _settings.TrimOutputDir;
        }
        finally { _loading = false; }

        // 下拉框绑的是选项对象，不是枚举值本身。
        // 若默认值与字段初始值相同（例如都是 Copy），OnModeChanged 不会被触发，
        // SelectedMode 就会保持 null —— 下拉框看起来是空的，用户以为没得选。
        SelectedMode = Modes.FirstOrDefault(m => m.Value == Mode) ?? Modes[0];
        SelectedRotation = Rotations.FirstOrDefault(r => r.Value == Rotation) ?? Rotations[0];
        RefreshPlan();
    }

    public void ApplyToSettings()
    {
        _settings.TrimVerify = VerifyOutput;
        _settings.TrimContainer = Container;
        _settings.TrimCodec = Codec;
        _settings.TrimCrf = Crf;
        _settings.TrimPreset = Preset;
        _settings.TrimMode = Mode == TrimMode.Precise ? "Precise" : "Copy";
        _settings.TrimRotation = Rotation.ToDegrees().ToString(CultureInfo.InvariantCulture);
        if (OutputDirectory.Length > 0) _settings.TrimOutputDir = OutputDirectory;
    }

    /// <summary>窗口关闭时调用：取消分析与运行。</summary>
    public void Shutdown()
    {
        CancelAnalyze();
        try { _previewSource?.Cancel(); } catch (ObjectDisposedException) { /* 忽略 */ }
        if (_record is not null)
            try { _record.Cancellation.Cancel(); } catch (ObjectDisposedException) { /* 忽略 */ }
        try { _runSource?.Cancel(); } catch (ObjectDisposedException) { /* 忽略 */ }
        _task = null;
    }

    #endregion
}
