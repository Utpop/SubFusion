using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubtitleMerger.App.Services;
using SubtitleMerger.Core;
using SubtitleMerger.Core.Models;
using SubtitleMerger.Core.Services;

namespace SubtitleMerger.App.ViewModels;

/// <summary>
/// 主界面状态与全部交互逻辑（对应原 App 类的非绘制部分）。
/// 采用轻 MVVM：状态/命令在此，WinUI 原生控件交互经 IUiServices 注入，
/// 后台批量任务经 IBatchEvents 回调并统一投递回 UI 线程。
/// </summary>
public partial class MainViewModel : ObservableObject, IBatchEvents
{
    private readonly IUiServices _ui;
    private readonly AppConfig _cfg;

    private readonly List<string> _videos = new();
    private readonly List<string> _subs = new();
    private readonly List<Job> _jobs = new();
    private readonly Dictionary<string, JobViewModel> _rows = new(StringComparer.Ordinal);
    private List<JobViewModel> _selected = new();

    /// <summary>日志最多保留的行数（重负载下防止字符串无限增长）。</summary>
    private const int MaxLogLines = 4000;

    private readonly object _pendingLogGate = new();
    private readonly Queue<string> _pendingLog = new();
    private bool _logFlushScheduled;
    private int _logLineCount;

    private readonly HashSet<string> _dirtyRows = new(StringComparer.Ordinal);
    private bool _progressFlushScheduled;
    // 日志镜像默认关闭：仅当设置环境变量 SUBMERGE_LOG=1 时写 %TEMP%\subtitle_merger_gui.log。
    // （曾用于排查管理员权限崩溃；若常开会在批量处理时每条日志都同步写盘，明显拖慢界面。）
    private static readonly bool _mirrorLog =
        Environment.GetEnvironmentVariable("SUBMERGE_LOG") == "1";
    private static readonly object _logMirrorGate = new();

    private int _uidSeq;
    private string? _ffmpeg;
    private string? _ffprobe;
    private FfmpegCapabilities _caps = new();
    private bool _capsChecked;
    private string _lastDir = "";
    private string _lastOutputDir = "";
    /// <summary>最近一次据以同步输出目录的源目录；用于判断「更换目录」是否真的发生。</summary>
    private string _syncSourceDir = "";
    private long _probeGeneration;
    private readonly HashSet<CancellationTokenSource> _probeSources = new();
    private CancellationTokenSource? _cancel;
    private Task? _batchTask;
    private string? _tempDir;
    private Options _options = new();
    private int _doneCount;
    private bool _loadingConfig;
    private bool _closing;

    public MainViewModel(IUiServices ui)
    {
        _ui = ui;
        _cfg = AppConfig.Load();
        _ffmpeg = FirstNonEmpty(_cfg.Get("ffmpeg"), ToolLocator.FindFfmpeg());
        _ffprobe = FirstNonEmpty(_cfg.Get("ffprobe"), ToolLocator.FindFfprobe());
    }

    private static string? FirstNonEmpty(string? configured, string? located)
        => !string.IsNullOrEmpty(configured) ? configured : located;

    private static void SafeCancel(CancellationTokenSource? cts)
    {
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* 任务刚收尾并已释放 */ }
    }

    #region 可绑定集合与选项

    public ObservableCollection<JobViewModel> Jobs { get; } = new();

    /// <summary>
    /// 日志全文（对应原 tk.Text 的内容）。原版日志控件虽为 disabled 但仍可选中复制，
    /// 因此这里保留整段文本而不是只读列表，视图用 IsTextSelectionEnabled 的 TextBlock 呈现。
    /// </summary>
    [ObservableProperty] private string logText = "";

    public IReadOnlyList<string> Containers { get; } = new[] { "mkv", "mp4", "same" };
    public IReadOnlyList<string> Modes { get; } = new[] { "soft", "hard" };
    public IReadOnlyList<string> Vcodecs { get; } = new[] { "copy", "libx264", "libx265", "h264_nvenc" };
    public IReadOnlyList<string> Acodecs { get; } = new[] { "copy", "aac" };
    public IReadOnlyList<string> Presets { get; } =
        new[] { "ultrafast", "veryfast", "fast", "medium", "slow", "veryslow" };

    [ObservableProperty] private string container = "mkv";
    [ObservableProperty] private string mode = "soft";
    [ObservableProperty] private string vcodec = "copy";
    [ObservableProperty] private string acodec = "copy";
    [ObservableProperty] private double crf = 20;
    [ObservableProperty] private string preset = "medium";
    [ObservableProperty] private double workers = 1;
    [ObservableProperty] private string suffix = "";
    [ObservableProperty] private string outputDir = "";

    /// <summary>
    /// 持久开关（默认打开）：输出目录始终与当前源文件的目录保持一致。
    /// 开启时，添加/拖入文件导致源目录变化会自动改写 OutputDir；关闭后由用户自行指定。
    /// 状态写入配置 sync_outdir，下次启动仍生效。
    /// </summary>
    [ObservableProperty] private bool syncOutputDir = true;
    [ObservableProperty] private bool keepExistingSubs = true;
    [ObservableProperty] private bool keepMetadata = true;
    [ObservableProperty] private bool overwrite = true;
    [ObservableProperty] private bool verify = true;

    /// <summary>合并并通过校验后删除原始视频与已合并字幕，输出沿用原文件名（不加 _sub）。</summary>
    [ObservableProperty] private bool replaceSource;
    [ObservableProperty] private bool convertEncoding = true;
    [ObservableProperty] private bool recursive = true;
    [ObservableProperty] private double threshold = Constants.DefaultThreshold;
    [ObservableProperty] private bool hardMulti = true;

    [ObservableProperty] private string statusText = "就绪";
    [ObservableProperty] private string countText = "尚未添加文件";
    [ObservableProperty] private string hintText = "";
    [ObservableProperty] private bool hintVisible;
    [ObservableProperty] private string ffmpegPillText = "正在检测 ffmpeg…";
    [ObservableProperty] private string ffmpegPillColor = "#8A8A8A";
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool canStop;
    [ObservableProperty] private double overallProgress;
    [ObservableProperty] private double overallMaximum = 100;

    public bool CanStart => !IsRunning;
    public string WindowTitle => $"{Constants.AppName} v{Constants.AppVersion}";

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanStart));

    // 原版仅在用户通过下拉框选择时触发这些回调（加载配置时不会触发），故用 _loadingConfig 保护
    partial void OnContainerChanged(string value) { if (!_loadingConfig) UpdateHint(); }
    partial void OnModeChanged(string value)
    {
        if (_loadingConfig) return;
        if (value == "hard" && Vcodec == "copy") Vcodec = "libx264";
        UpdateHint();
    }
    partial void OnVcodecChanged(string value) { if (!_loadingConfig) UpdateHint(); }

    // 打开「输出目录同源同步」时立即把输出目录对齐到当前源目录；关闭开关不改动输出目录
    partial void OnSyncOutputDirChanged(bool value)
    {
        if (_loadingConfig || !value) return;
        SyncOutputDirTo(CurrentSourceDir(), force: true);
    }

    #endregion

    #region 初始化 / 配置

    public void Initialize()
    {
        LoadConfigToUi();
        UpdateToolLabels();
        _ = CheckToolsAsync();
    }

    private void LoadConfigToUi()
    {
        _loadingConfig = true;
        if (_cfg.Get("container") is { Length: > 0 } c) Container = c;
        if (_cfg.Get("mode") is { Length: > 0 } m) Mode = m;
        if (_cfg.Get("vcodec") is { Length: > 0 } v) Vcodec = v;
        if (_cfg.Get("acodec") is { Length: > 0 } a) Acodec = a;
        if (_cfg.Get("preset") is { Length: > 0 } p) Preset = p;
        if (_cfg.Get("suffix") is { } s) Suffix = s;
        if (_cfg.Get("outdir") is { } od) OutputDir = od;
        // 新开关键：缺省（旧配置无此键）时保持默认「打开」，仅当用户显式写下 false 才关闭
        if (_cfg.Has("sync_outdir") && _cfg.GetBool("sync_outdir") is bool sod) SyncOutputDir = sod;
        if (double.TryParse(_cfg.Get("crf"), NumberStyles.Float, CultureInfo.InvariantCulture, out double crf)) Crf = crf;
        if (double.TryParse(_cfg.Get("workers"), NumberStyles.Float, CultureInfo.InvariantCulture, out double w)) Workers = w;
        if (double.TryParse(_cfg.Get("threshold"), NumberStyles.Float, CultureInfo.InvariantCulture, out double t)) Threshold = t;
        if (_cfg.Has("keep_existing_subs") && _cfg.GetBool("keep_existing_subs") is bool kes) KeepExistingSubs = kes;
        if (_cfg.Has("keep_metadata") && _cfg.GetBool("keep_metadata") is bool km) KeepMetadata = km;
        if (_cfg.Has("convert_encoding") && _cfg.GetBool("convert_encoding") is bool ce) ConvertEncoding = ce;
        if (_cfg.Has("hard_multi") && _cfg.GetBool("hard_multi") is bool hm) HardMulti = hm;
        if (_cfg.Has("verify") && _cfg.GetBool("verify") is bool vf) Verify = vf;
        if (_cfg.Has("replace_source") && _cfg.GetBool("replace_source") is bool rs) ReplaceSource = rs;
        if (_cfg.Has("recursive") && _cfg.GetBool("recursive") is bool rc) Recursive = rc;
        _lastDir = _cfg.Get("last_dir") ??
                   Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _loadingConfig = false;
        UpdateHint();
    }

    private void SaveConfig()
    {
        // 与原 Python 版一致：crf / workers / threshold 以字符串写入，保证旧配置可直接互读
        _cfg.Set("ffmpeg", _ffmpeg ?? "");
        _cfg.Set("ffprobe", _ffprobe ?? "");
        _cfg.Set("container", Container);
        _cfg.Set("mode", Mode);
        _cfg.Set("vcodec", Vcodec);
        _cfg.Set("acodec", Acodec);
        _cfg.Set("crf", ((int)Crf).ToString(CultureInfo.InvariantCulture));
        _cfg.Set("preset", Preset);
        _cfg.Set("workers", ((int)Workers).ToString(CultureInfo.InvariantCulture));
        _cfg.Set("suffix", Suffix);
        _cfg.Set("outdir", OutputDir);
        _cfg.Set("sync_outdir", SyncOutputDir);
        _cfg.Set("keep_existing_subs", KeepExistingSubs);
        _cfg.Set("keep_metadata", KeepMetadata);
        _cfg.Set("convert_encoding", ConvertEncoding);
        _cfg.Set("hard_multi", HardMulti);
        _cfg.Set("verify", Verify);
        _cfg.Set("replace_source", ReplaceSource);
        _cfg.Set("threshold", Threshold.ToString("0.##", CultureInfo.InvariantCulture));
        _cfg.Set("recursive", Recursive);
        _cfg.Set("last_dir", _lastDir);
        _cfg.Save();
    }

    private void UpdateToolLabels()
    {
        if (string.IsNullOrEmpty(_ffmpeg))
        {
            FfmpegPillText = "ffmpeg 未找到";
            FfmpegPillColor = "#C42B1C";
            return;
        }
        string baseDir = ToolLocator.BundledDir();
        string? toolDir = Path.GetDirectoryName(_ffmpeg);
        bool bundled = !string.IsNullOrEmpty(toolDir) &&
                       string.Equals(Path.GetFullPath(toolDir), Path.GetFullPath(baseDir),
                                     StringComparison.OrdinalIgnoreCase);
        FfmpegPillText = bundled ? "ffmpeg 内置版本" : "ffmpeg 已就绪";
        FfmpegPillColor = "#0E700E";
    }

    private async Task CheckToolsAsync()
    {
        FfmpegCapabilities caps = await ToolLocator.FfmpegCapabilitiesAsync(_ffmpeg);
        string version = await ToolLocator.FfmpegVersionAsync(_ffmpeg);
        _caps = caps;
        _capsChecked = true;
        if (!string.IsNullOrEmpty(version))
            Log("ffmpeg：" + version.Split(" Copyright")[0]);
        var missing = new List<string>();
        if (!_caps.Libass) missing.Add("libass");
        if (!_caps.X264) missing.Add("x264");
        if (missing.Count > 0) Log("注意：ffmpeg 缺少 " + string.Join("、", missing));
        UpdateHint();
    }

    #endregion

    #region 工具定位 / 提示

    [RelayCommand]
    private async Task ChooseFfmpeg()
    {
        string? initial = string.IsNullOrEmpty(_ffmpeg)
            ? "C:/" : (Path.GetDirectoryName(_ffmpeg) ?? "C:/");
        string? path = await _ui.PickExecutableAsync("选择 ffmpeg.exe", initial);
        if (string.IsNullOrEmpty(path)) return;
        _ffmpeg = path;
        string dir = Path.GetDirectoryName(path) ?? "";
        string guess = Path.Combine(dir, "ffprobe.exe");
        if (File.Exists(guess)) _ffprobe = guess;
        SaveConfig();
        UpdateToolLabels();
        Log($"已设置 ffmpeg：{path}");
        await CheckToolsAsync();
    }

    private void UpdateHint()
    {
        var hints = new List<string>();
        if (Container == "mp4")
            hints.Add("MP4 不支持图形字幕（SUP），这类字幕会被跳过；文本字幕将转为 mov_text（ASS 样式可能丢失）。");
        if (Mode == "hard")
        {
            hints.Add("硬字幕会重新编码视频，速度较慢；中文字幕依赖系统字体，若出现方块字请改用软字幕。");
            if (_capsChecked && !_caps.Libass)
                hints.Add("当前 ffmpeg 缺少 libass，硬字幕可能失败。");
        }
        if (Vcodec == "copy" && Container == "mp4")
            hints.Add("源视频为非 H.264/HEVC 编码时，MP4 可能需要重新编码。");
        HintText = string.Join(" ", hints);
        HintVisible = hints.Count > 0;
    }

    #endregion

    #region 添加 / 移除 / 匹配

    [RelayCommand]
    private async Task AddVideos()
    {
        List<string> exts = Constants.VideoExts.OrderBy(x => x, StringComparer.Ordinal).ToList();
        IReadOnlyList<string> paths = await _ui.PickFilesAsync("选择视频文件", exts);
        await AddPathsAsync(paths);
    }

    [RelayCommand]
    private async Task AddSubtitles()
    {
        List<string> exts = Constants.SubExts.OrderBy(x => x, StringComparer.Ordinal).ToList();
        IReadOnlyList<string> paths = await _ui.PickFilesAsync("选择字幕文件", exts);
        await AddPathsAsync(paths);
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        string? folder = await _ui.PickFolderAsync("选择文件夹");
        if (!string.IsNullOrEmpty(folder)) await AddPathsAsync(new[] { folder });
    }

    /// <summary>文件选择器与原生拖放的共用入口（原 App.add_paths）。</summary>
    public async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        if (IsRunning)
        {
            await _ui.ShowInfoAsync("正在处理中，请先停止再添加文件。");
            return;
        }
        if (paths.Count == 0) return;

        CollectResult collected = FileCollector.Collect(paths, Recursive);
        if (collected.Videos.Count == 0 && collected.Subtitles.Count == 0)
        {
            await _ui.ShowInfoAsync("没有找到视频或字幕文件。");
            return;
        }

        string addedDir;
        if (Directory.Exists(paths[0])) addedDir = paths[0];
        else if (File.Exists(paths[0])) addedDir = Path.GetDirectoryName(paths[0]) ?? _lastDir;
        else addedDir = _lastDir;
        _lastDir = addedDir;
        // 持久开关：本次添加的目录与上次不同时，自动把输出目录同步过去
        SyncOutputDirTo(addedDir);

        // 用 HashSet 去重：旧写法每个新文件都要和已存在的全部文件线性比较，
        // 反复「添加文件夹」时会退化成 O(n²)。
        var knownVideos = new HashSet<string>(_videos, StringComparer.Ordinal);
        var knownSubs = new HashSet<string>(_subs, StringComparer.Ordinal);
        List<string> newVideos = collected.Videos.Where(knownVideos.Add).ToList();
        List<string> newSubs = collected.Subtitles.Where(knownSubs.Add).ToList();
        _videos.AddRange(newVideos);
        _subs.AddRange(newSubs);
        Log($"添加：视频 {newVideos.Count} 个，字幕 {newSubs.Count} 个");
        Rematch();
    }

    public void SetSelection(IEnumerable<JobViewModel> selected)
        => _selected = selected.Distinct().ToList();

    [RelayCommand]
    private void RemoveSelected()
    {
        if (IsRunning) return;
        foreach (JobViewModel row in _selected.ToList())
        {
            Job job = row.Model;
            _videos.Remove(job.Video);
            _jobs.Remove(job);
            _rows.Remove(job.Uid);
            Jobs.Remove(row);
        }
        _selected.Clear();
        CountText = $"视频 {_videos.Count} 个 / 字幕 {_subs.Count} 个";
    }

    [RelayCommand]
    private void ClearAll()
    {
        if (IsRunning) return;
        _videos.Clear();
        _subs.Clear();
        _jobs.Clear();
        _rows.Clear();
        _selected.Clear();
        Jobs.Clear();
        _syncSourceDir = "";
        CountText = "未添加文件";
        OverallProgress = 0;
        StatusText = "已清空";
    }

    [RelayCommand]
    private void Rematch()
    {
        if (IsRunning) return;
        double threshold = Threshold;
        if (double.IsNaN(threshold) || threshold < 0.4 || threshold > 1.0)
        {
            threshold = Constants.DefaultThreshold;
            Threshold = threshold;
        }

        MatchResult matched = SubtitleMatcher.Match(_videos, _subs, threshold);
        var oldByVideo = new Dictionary<string, Job>(StringComparer.Ordinal);
        foreach (Job j in _jobs) oldByVideo[j.Video] = j;

        _jobs.Clear();
        _rows.Clear();
        Jobs.Clear();
        _selected.Clear();

        foreach (string video in _videos)
        {
            oldByVideo.TryGetValue(video, out Job? old);
            string uid = old?.Uid ?? NextUid();
            var job = new Job(video, new List<SubTrack>(), "",
                              old?.Duration, old?.Info ?? new MediaInfo())
            {
                Uid = uid,
                UserEdited = old?.UserEdited ?? false,
            };

            List<string> autoPaths = matched.Mapping.TryGetValue(video, out var list)
                ? list : new List<string>();
            if (job.UserEdited && old is not null)
            {
                var tracks = new List<SubTrack>();
                var autoSet = new HashSet<string>(autoPaths, StringComparer.Ordinal);
                foreach (SubTrack track in old.Subs)
                    if (!autoSet.Contains(track.Path)) tracks.Add(track.Clone());
                foreach (string path in autoPaths)
                    if (!tracks.Any(t => string.Equals(t.Path, path, StringComparison.Ordinal)))
                        tracks.Add(MakeTrack(path));
                job.Subs = tracks;
            }
            else
            {
                job.Subs = autoPaths.Select(MakeTrack).ToList();
                for (int i = 0; i < job.Subs.Count; i++) job.Subs[i].Default = i == 0;
            }

            _jobs.Add(job);
            var row = new JobViewModel(job);
            _rows[uid] = row;
            Jobs.Add(row);
        }

        int matchedCount = matched.Mapping.Values.Sum(l => l.Count);
        CountText = $"视频 {_videos.Count} 个 / 字幕 {_subs.Count} 个 / 已匹配 {matchedCount} 个 / 未匹配 {matched.Unmatched.Count} 个";
        StatusText = $"匹配完成，共 {_videos.Count} 个视频";
        if (matchedCount > 0) Log($"自动匹配：{matchedCount} 份字幕已挂载");
        if (matched.FuzzyMatches.Count > 0)
            Log($"有 {matched.FuzzyMatches.Count} 份字幕只能模糊对上（可能是同集的不同语言或版本），未自动挂载：" +
                string.Join("、", matched.FuzzyMatches.Take(5).Select(f => Path.GetFileName(f.Subtitle)))
                + (matched.FuzzyMatches.Count > 5 ? " 等" : ""));
        if (matched.Unmatched.Count > 0 && _videos.Count > 0)
            Log($"未匹配的字幕 {matched.Unmatched.Count} 个（可双击视频行手动指定）");
        _ = ProbeJobsAsync();
    }

    private static SubTrack MakeTrack(string path)
    {
        string kind = Constants.BitmapSubExts.Contains(Path.GetExtension(path).ToLowerInvariant())
            ? "bitmap" : "text";
        // 1.1：先认 .chs / _en / [cht] 这类明确标记，没有再按字符集猜
        return new SubTrack(path, lang: NameNormalizer.DetectTypedLang(path), kind: kind);
    }

    private string NextUid() => "j" + (++_uidSeq).ToString(CultureInfo.InvariantCulture);

    #endregion

    #region 探测 / 字幕校验

    private async Task ProbeJobsAsync(bool onlyMissing = true)
    {
        if (string.IsNullOrEmpty(_ffprobe) || _jobs.Count == 0) return;
        List<Job> targets = _jobs.Where(j => !onlyMissing || j.Duration is null || !j.Info.Probed).ToList();
        if (targets.Count == 0) return;

        // 只取消上一轮：它仍在收尾，必须等自己的 finally 才能 Dispose。
        // 复制一份再 Cancel，避免回调线程在锁内与集合修改交错。
        CancellationTokenSource[] oldSources;
        lock (_probeSources) oldSources = _probeSources.ToArray();
        foreach (CancellationTokenSource old in oldSources) SafeCancel(old);
        var cts = new CancellationTokenSource();
        lock (_probeSources) _probeSources.Add(cts);
        long generation = ++_probeGeneration;

        // 同一个视频被重复添加时 ToDictionary 会直接抛异常并让 Rematch 的 fire-and-forget
        // 任务失败；去重后行为与 Python 版「按路径字典收集」一致。
        var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Job target in targets) byPath[target.Video] = target.Uid;
        try
        {
            await MediaProbe.ProbeManyAsync(_ffprobe, byPath.Keys, Constants.ProbeWorkers,
                (path, info) =>
                {
                    if (generation != _probeGeneration) return;
                    if (!byPath.TryGetValue(path, out string? uid)) return;
                    _ui.RunOnUiThread(() => OnProbe(uid, info));
                }, cts.Token);
        }
        finally
        {
            lock (_probeSources) _probeSources.Remove(cts);
            cts.Dispose();
        }
    }

    private void OnProbe(string uid, MediaInfo info)
    {
        if (!_rows.TryGetValue(uid, out JobViewModel? row)) return;
        Job job = row.Model;
        job.Info = info;
        job.Duration = info.Duration ?? job.Duration;
        row.Refresh();
    }

    /// <summary>处理前同步补齐媒体信息：字幕轨序号、进度百分比都依赖它。</summary>
    private async Task EnsureProbedAsync(IReadOnlyList<Job> jobs)
    {
        List<string> missing = jobs.Where(j => !j.Info.Probed).Select(j => j.Video).ToList();
        if (missing.Count == 0 || string.IsNullOrEmpty(_ffprobe)) return;
        StatusText = $"正在读取媒体信息（{missing.Count} 个）…";
        Dictionary<string, MediaInfo> results = await MediaProbe.ProbeManyAsync(
            _ffprobe, missing, Constants.ProbeWorkers);
        foreach (Job job in jobs)
        {
            if (!results.TryGetValue(job.Video, out MediaInfo? info)) continue;
            job.Info = info;
            job.Duration = info.Duration ?? job.Duration;
            if (_rows.TryGetValue(job.Uid, out JobViewModel? row)) row.Refresh();
        }
    }

    /// <summary>剔除 ffmpeg 无法识别的字幕（空文件、乱码、损坏的图形字幕），其余照常处理。</summary>
    private async Task DropInvalidSubsAsync()
    {
        if (string.IsNullOrEmpty(_ffprobe)) return;
        string probe = _ffprobe;
        var paths = _jobs.SelectMany(j => j.Subs).Where(t => t.Enabled)
            .Select(t => t.Path).Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count == 0) return;

        StatusText = $"正在校验字幕文件（{paths.Count} 个）…";
        // 有界并发：旧写法一次性为每个字幕创建 Task，几千个大字幕文件会同时
        // 打开 FileStream 并各起一个超时定时器，句柄/内存一起膨胀。
        CancellationToken validationToken = _cancel?.Token ?? CancellationToken.None;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Constants.ProbeWorkers,
            CancellationToken = validationToken,
        };
        var bag = new System.Collections.Concurrent.ConcurrentBag<(string Path, bool Ok, string Detail)>();
        try
        {
            await Parallel.ForEachAsync(paths, options, async (path, ct) =>
            {
                (bool Ok, string Detail) result;
                try
                {
                    result = await MediaProbe.ProbeSubtitleAsync(probe, path).WaitAsync(TimeSpan.FromSeconds(25), ct);

                    // 有些 .ass/.ssa 只是缺了 [Script Info] 段头，ffprobe 会报 Invalid data，
                    // 但补上段头就能正常识别。这种情况判为可用，转换阶段会自动修复，
                    // 不能因为文件本身的小瑕疵把整条字幕丢掉（否则整批任务全部「没有可用字幕轨」）。
                    if (!result.Ok && EncodingService.NeedsScriptInfoRepair(path))
                    {
                        Log($"[修复] {Path.GetFileName(path)}：缺少 [Script Info] 段头，已自动补全后继续");
                        result = (true, "");
                    }
                }
                catch
                {
                    result = (true, "");
                }
                bag.Add((path, result.Ok, result.Detail));
            });
        }
        catch (OperationCanceledException)
        {
            return;   // 校验被取消（用户点了停止/关闭窗口），按原样继续
        }
        var results = bag.ToList();
        var bad = results.Where(r => !r.Ok)
            .GroupBy(r => r.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Detail, StringComparer.Ordinal);
        if (bad.Count == 0) return;

        foreach (Job job in _jobs)
        {
            var kept = new List<SubTrack>();
            foreach (SubTrack track in job.Subs)
            {
                if (bad.TryGetValue(track.Path, out string? reason))
                    Log($"[跳过] {job.Name}：字幕 {track.Name} 无法识别（{reason}）");
                else kept.Add(track);
            }
            job.Subs = kept;
            if (_rows.TryGetValue(job.Uid, out JobViewModel? row)) row.Refresh();
        }
        Log($"已剔除 {bad.Count} 个无效字幕文件，其余照常处理");
    }

    #endregion

    #region 字幕轨编辑

    public async Task EditSubtitlesAsync()
    {
        if (IsRunning || _selected.Count == 0) return;
        JobViewModel row = _selected.First();
        Job job = row.Model;
        string directory = Path.GetDirectoryName(job.Video) ?? "";
        List<string> candidates = _subs
            .Where(s => string.Equals(Path.GetDirectoryName(s) ?? "", directory,
                                      StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) candidates = _subs;

        List<SubTrack>? result = await _ui.EditSubtitlesAsync(job.Video, candidates, _subs, job.Subs);
        if (result is null) return;
        job.Subs = result;
        job.UserEdited = true;
        row.Refresh();
        StatusText = $"已更新 {job.Name} 的字幕轨";
    }

    [RelayCommand]
    private Task EditSubtitles() => EditSubtitlesAsync();

    #endregion

    #region 开始 / 停止

    private Options? CollectOptions()
    {
        var options = new Options
        {
            Container = Container,
            Mode = Mode,
            Vcodec = Vcodec,
            Acodec = Acodec,
            KeepExistingSubs = KeepExistingSubs,
            KeepMetadata = KeepMetadata,
            ConvertEncoding = ConvertEncoding,
            HardMulti = HardMulti,
            Verify = Verify,
            ReplaceSource = ReplaceSource,
            Suffix = Suffix?.Trim() ?? "",
            Overwrite = Overwrite,
            OutputDir = OutputDir?.Trim() ?? "",
            Crf = (int)Crf,
            Preset = Preset,
            Workers = Math.Clamp((int)Workers, 1, 4),
        };
        if (options.OutputDir.Length == 0) return null;
        if (options.Mode == "hard" && options.Vcodec == "copy")
        {
            Log("提示：硬字幕需要重新编码视频，已强制使用 libx264。");
            options.Vcodec = "libx264";
        }
        return options;
    }

    [RelayCommand]
    private async Task Start()
    {
        if (IsRunning) return;
        if (string.IsNullOrEmpty(_ffmpeg))
        {
            await _ui.ShowErrorAsync("未找到 ffmpeg，请先手动指定。");
            return;
        }
        string ffmpeg = _ffmpeg;
        if (_jobs.Count == 0)
        {
            await _ui.ShowInfoAsync("请先添加视频文件。");
            return;
        }

        Options? options = CollectOptions();
        if (options is null)
        {
            // 原 Python 版此处还有一段不可达的「请先选择输出目录」提示（_collect_options 已提前返回 None）；
            // C# 版把它变为可达，用户能明确知道缺什么。详见交互差异说明。
            await _ui.ShowInfoAsync("请先选择输出目录。");
            return;
        }
        _options = options;

        // 破坏性选项：执行前必须再确认一次（默认关闭，勾了才问）
        if (options.ReplaceSource)
        {
            bool go = await _ui.AskYesNoAsync(
                "已勾选「合并成功后替换原文件」。\n\n" +
                "该选项会在合并完成且校验通过后，删除原始视频与已合并的字幕文件，" +
                "输出沿用原视频文件名（不再保留 _sub 副本）。\n\n" +
                "删除后无法恢复，确定继续吗？");
            if (!go) return;
        }

        if (options.Mode == "hard" && _capsChecked && !_caps.Libass)
        {
            bool go = await _ui.AskYesNoAsync(
                "当前 ffmpeg 未编译 libass，硬字幕（烧录）可能无法工作。\n仍要继续吗？");
            if (!go) return;
        }

        // 先建本轮取消源：字幕有效性校验也要响应「停止 / 关闭」，
        // 不能沿用上一轮探测的令牌（那时 _cancel 尚未创建）。
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        CancellationToken batchToken = _cancel.Token;

        try
        {
            await EnsureProbedAsync(_jobs);
            await DropInvalidSubsAsync();
        }
        catch (Exception ex)
        {
            await _ui.ShowErrorAsync($"处理前检查失败：{ex.Message}");
            _cancel.Dispose(); _cancel = null;
            return;
        }
        if (batchToken.IsCancellationRequested) { _cancel.Dispose(); _cancel = null; return; }

        try { Directory.CreateDirectory(options.OutputDir); }
        catch (Exception ex)
        {
            await _ui.ShowErrorAsync($"无法创建输出目录：{ex.Message}");
            _cancel.Dispose(); _cancel = null;
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), "submerge_" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(_tempDir); }
        catch (Exception ex)
        {
            await _ui.ShowErrorAsync($"无法创建临时目录：{ex.Message}");
            _cancel.Dispose(); _cancel = null;
            return;
        }
        IsRunning = true;
        CanStop = true;
        OverallMaximum = _jobs.Count;
        OverallProgress = 0;
        _doneCount = 0;
        StatusText = $"开始处理 {_jobs.Count} 个视频...";

        var processor = new BatchProcessor(this);
        _batchTask = processor.RunAsync(new BatchRequest
        {
            Options = options,
            Ffmpeg = ffmpeg,
            Ffprobe = _ffprobe,
            Jobs = _jobs.ToList(),
            TempDir = _tempDir,
        }, batchToken);
        try
        {
            await _batchTask;
        }
        finally
        {
            // AllDone 是投递到 UI 队列的回调，可能晚于本方法返回才执行；
            // 资源收尾放到这里才是确定性的（关闭窗口时也不会漏）。
            CleanupTemp();
            _batchTask = null;
            _cancel?.Dispose();
            _cancel = null;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        if (!IsRunning) return;
        _cancel?.Cancel();
        StatusText = "正在停止，请稍候...";
        CanStop = false;
    }

    #endregion

    #region IBatchEvents —— 全部先投递回 UI 线程

    /// <summary>
    /// 日志入口（可在任意线程调用）。
    ///
    /// 重负载下不能「一条一行、每条都重建整段文本、每次都滚动到底」：
    /// 旧写法是 <c>LogText += ...</c>（每次都复制整段字符串，O(n²) 分配），
    /// 且 400 个任务并发时每秒会触发上百次完整的 UI 布局 + 滚动到末尾，
    /// 内存与 UI 线程压力都随任务数线性膨胀，最终可能 OOM 或卡死无响应。
    /// 现在改为：后台线程只入队，由 UI 定时器每 120 ms 合并一次，
    /// 日志保留最近 <see cref="MaxLogLines"/> 行，滚动也最多每 120 ms 一次。
    /// </summary>
    public void Log(string text)
    {
        string stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        // 诊断镜像（SUBMERGE_LOG=1）：把同一份日志写进 %TEMP%\subtitle_merger_gui.log，
        // 便于在无法目测界面时拿到 GUI 里真实发生的 ffmpeg 命令与错误。
        if (_mirrorLog)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "subtitle_merger_gui.log");
                lock (_logMirrorGate) File.AppendAllText(path, $"[{stamp}] {text}\n", new UTF8Encoding(false));
            }
            catch { /* 镜像失败不影响界面 */ }
        }
        bool dispatchNow = false;
        lock (_pendingLogGate)
        {
            _pendingLog.Enqueue($"[{stamp}] {text}");
            // 积压太多时丢掉最早的（例如某个任务一次性输出几百行错误）
            if (_pendingLog.Count > MaxLogLines) _pendingLog.Dequeue();
            if (!_logFlushScheduled)
            {
                _logFlushScheduled = true;
                dispatchNow = true;
            }
        }

        if (dispatchNow)
            _ui.RunOnUiThread(DrainLogQueue);
    }

    /// <summary>把排队的日志一次性并进 <see cref="LogText"/>，顺便裁掉超出上限的旧行。</summary>
    private void DrainLogQueue()
    {
        var batch = new StringBuilder();
        lock (_pendingLogGate)
        {
            while (_pendingLog.Count > 0) batch.Append(_pendingLog.Dequeue()).Append('\n');
            _logFlushScheduled = false;
        }
        if (batch.Length == 0) return;

        _logLineCount += batch.ToString().Count(c => c == '\n');
        var merged = new StringBuilder(LogText, LogText.Length + batch.Length).Append(batch);
        if (_logLineCount > MaxLogLines)
        {
            // 只保留最后 MaxLogLines 行
            string text = merged.ToString();
            int keepFrom = text.Length;
            int lines = 0;
            for (int i = text.Length - 1; i >= 0; i--)
            {
                if (text[i] == '\n' && ++lines > MaxLogLines) { keepFrom = i + 1; break; }
            }
            merged.Clear().Append(keepFrom < text.Length
                ? "……（更早的日志已省略，仅保留最近 " + MaxLogLines + " 行）\n" + text[keepFrom..]
                : text);
            _logLineCount = Math.Min(_logLineCount, MaxLogLines) + 1;
        }
        LogText = merged.ToString();
        _ui.ScrollLogToEnd();
    }

    public void JobUpdate(Job job)
        => _ui.RunOnUiThread(() => { if (_rows.TryGetValue(job.Uid, out var r)) r.Refresh(); });

    /// <summary>
    /// 进度回调（每个任务每 250 ms 一次）。多任务并发时直接逐条 Refresh 会把 UI 队列刷爆，
    /// 这里改为「标记脏 + 由同一个 120 ms 定时器统一刷新」，任务再多也只有固定频率的布局。
    /// </summary>
    public void Progress(Job job)
    {
        bool dispatchNow;
        lock (_dirtyRows)
        {
            _dirtyRows.Add(job.Uid);
            dispatchNow = !_progressFlushScheduled;
            if (dispatchNow) _progressFlushScheduled = true;
        }
        if (dispatchNow) _ui.RunOnUiThread(FlushProgress);
    }

    private void FlushProgress()
    {
        lock (_dirtyRows) _progressFlushScheduled = false;
        List<string> uids;
        lock (_dirtyRows)
        {
            uids = _dirtyRows.ToList();
            _dirtyRows.Clear();
        }
        foreach (string uid in uids)
            if (_rows.TryGetValue(uid, out JobViewModel? row)) row.Refresh();
    }

    public void JobDone(Job job)
        => _ui.RunOnUiThread(() =>
        {
            _doneCount++;
            OverallProgress = _doneCount;
            if (_rows.TryGetValue(job.Uid, out var row)) row.Refresh();

            string elapsed = "";
            if (job.StartedAt.HasValue && job.FinishedAt.HasValue)
                elapsed = $"，用时 {Formatting.FormatDuration(job.FinishedAt.Value - job.StartedAt.Value)}";

            if (job.State == Job.StateDone)
            {
                Log($"[完成] {job.Name} -> {Path.GetFileName(job.Output)}{elapsed}");
                if (job.VerifyOk == false)
                    Log($"[校验异常] {job.Name}：{string.Join("；", job.VerifyIssues)}");
            }
            else if (job.State == Job.StateFailed)
                Log($"[失败] {job.Name}：{job.Message}");
            else if (job.State == Job.StateSkipped)
                Log($"[跳过] {job.Name}：{job.Message}");
            else if (job.State == Job.StateCancelled)
                Log($"[取消] {job.Name}");
            StatusText = $"已完成 {_doneCount} / {_jobs.Count}";
        });

    public void AllDone(BatchSummary summary)
        => _ui.RunOnUiThread(() =>
        {
            IsRunning = false;
            CanStop = false;
            string total = Formatting.FormatDuration(summary.TotalSeconds);
            string cancelled = summary.Cancelled > 0 ? $" / 取消 {summary.Cancelled}" : "";
            StatusText = $"全部结束：完成 {summary.Done} / 失败 {summary.Failed} / 跳过 {summary.Skipped}{cancelled}，总用时 {total}";
            Log(Constants.LogSeparator);
            Log($"全部结束：完成 {summary.Done}，失败 {summary.Failed}，跳过 {summary.Skipped}，取消 {summary.Cancelled}，总用时 {total}");
            if (summary.Warned > 0)
                Log($"注意：{summary.Warned} 个输出文件校验异常，详见上方 [校验异常] 行");
            _lastOutputDir = summary.OutputDir;
        });

    public void Fatal(string text) => Log(text);

    private void CleanupTemp()
    {
        if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* 忽略 */ }
        }
        _tempDir = null;
    }

    #endregion

    #region 输出目录 / 报告 / 杂项

    /// <summary>当前任务列表里第一个视频所在目录；空列表返回空串。</summary>
    private string CurrentSourceDir()
        => _videos.Count > 0 ? (Path.GetDirectoryName(_videos[0]) ?? "") : "";

    /// <summary>
    /// 「输出目录同源同步」开关的实际动作：把输出目录对齐到指定源目录。
    /// 仅当源目录相对上一次同步确实变化时才改写，避免同一目录反复添加时
    /// 冲掉用户手动指定的输出目录；force 用于刚打开开关时立即对齐一次。
    /// </summary>
    private void SyncOutputDirTo(string sourceDir, bool force = false)
    {
        if (!SyncOutputDir || string.IsNullOrEmpty(sourceDir)) return;
        if (!force && string.Equals(sourceDir, _syncSourceDir, StringComparison.OrdinalIgnoreCase))
            return;
        _syncSourceDir = sourceDir;
        if (!string.Equals(OutputDir?.Trim(), sourceDir, StringComparison.OrdinalIgnoreCase))
            OutputDir = sourceDir;
    }

    [RelayCommand]
    private async Task ChooseOutdir()
    {
        string? folder = await _ui.PickFolderAsync("选择输出目录");
        if (string.IsNullOrEmpty(folder)) return;
        OutputDir = folder;
        _lastDir = folder;
        // 手动选择后以当前源目录为基准：此后只有「源目录更换」才会再次自动同步，
        // 同一目录继续添加文件不会覆盖用户刚选的输出目录
        _syncSourceDir = CurrentSourceDir();
    }

    [RelayCommand]
    private void UseSourceDir()
    {
        string dir = CurrentSourceDir();
        if (dir.Length == 0) return;
        OutputDir = dir;
        // 「同源文件」按钮本身就是显式对齐：同步基准一并更新，避免下次添加同目录文件时
        // 又因基准滞后而把输出目录改回别处
        _syncSourceDir = dir;
    }

    [RelayCommand]
    private async Task OpenOutputDir()
    {
        string folder = OutputDir?.Trim() ?? "";
        if (folder.Length == 0) folder = _lastOutputDir;
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) _ui.OpenFolder(folder);
        else await _ui.ShowInfoAsync("输出目录还不存在。");
    }

    [RelayCommand]
    private async Task OpenVideoFolder()
    {
        if (_selected.Count == 0) return;
        string dir = Path.GetDirectoryName(_selected.First().Model.Video) ?? "";
        if (dir.Length > 0 && Directory.Exists(dir)) _ui.OpenFolder(dir);
    }

    [RelayCommand]
    private async Task OpenOutputFile()
    {
        if (_selected.Count == 0) return;
        Job job = _selected.First().Model;
        if (!string.IsNullOrEmpty(job.Output) && File.Exists(job.Output))
            _ui.OpenFolder(Path.GetDirectoryName(job.Output) ?? "");
        else await _ui.ShowInfoAsync("输出文件还不存在。");
    }

    [RelayCommand]
    private void ClearLog()
    {
        lock (_pendingLogGate)
        {
            _pendingLog.Clear();
            _logFlushScheduled = false;
        }
        _logLineCount = 0;
        LogText = "";
    }

    [RelayCommand]
    private async Task ExportReport()
    {
        if (_jobs.Count == 0)
        {
            await _ui.ShowInfoAsync("没有可导出的任务。");
            return;
        }
        string defaultName = "合并报告_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv";
        string initialDir = OutputDir?.Trim() ?? "";
        if (initialDir.Length == 0) initialDir = _lastDir;
        string? path = await _ui.PickSaveCsvAsync(defaultName, initialDir);
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var sb = new StringBuilder();
            AppendCsvRow(sb, new[]
            {
                "视频文件", "分辨率", "时长(秒)", "字幕轨", "状态",
                "输出校验", "输出文件", "大小(字节)", "说明",
            });
            foreach (Job job in _jobs)
            {
                string verifyText = job.VerifyOk == true ? "通过"
                    : job.VerifyOk == false ? string.Join("；", job.VerifyIssues) : "未校验";
                string subs = string.Join(" | ", job.Subs.Where(t => t.Enabled)
                    .Select(t => $"{Path.GetFileName(t.Path)}[{t.Lang}]"));
                AppendCsvRow(sb, new[]
                {
                    job.Video,
                    ResText(job),
                    job.Duration.HasValue ? Formatting.F3(job.Duration.Value) : "",
                    subs,
                    job.State,
                    verifyText,
                    job.Output,
                    job.Size?.ToString(CultureInfo.InvariantCulture) ?? "",
                    job.Message,
                });
            }
            // Python: open(..., "w", newline="", encoding="utf-8-sig") + csv.writer → BOM + CRLF
            byte[] body = new UTF8Encoding(false).GetBytes(sb.ToString());
            byte[] withBom = new byte[body.Length + 3];
            withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
            Array.Copy(body, 0, withBom, 3, body.Length);
            await File.WriteAllBytesAsync(path, withBom);
            Log($"已导出报告：{path}");
        }
        catch (Exception ex)
        {
            await _ui.ShowErrorAsync($"导出失败：{ex.Message}");
        }
    }

    private static string ResText(Job job)
        => job.Info.Width > 0 && job.Info.Height > 0
            ? $"{job.Info.Width}x{job.Info.Height}" : "-";

    private static void AppendCsvRow(StringBuilder sb, IReadOnlyList<string> fields)
    {
        // Python csv.writer 默认 QUOTE_MINIMAL + lineterminator="\r\n"
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(',');
            string field = fields[i] ?? "";
            bool quote = field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            if (quote) sb.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
            else sb.Append(field);
        }
        sb.Append("\r\n");
    }

    #endregion

    #region 关闭

    /// <summary>返回 false 表示用户取消退出（AppWindow.Closing 需据此保留窗口）。</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (IsRunning)
        {
            bool yes = await _ui.AskYesNoAsync("正在处理中，确定退出吗？");
            if (!yes) return false;
            _cancel?.Cancel();
            // 关闭窗口前等批量任务真正收尾：否则 AllDone 还没销毁临时目录/CTS，
            // 进程就已经退出，后台 ffmpeg 子进程会残留。
            if (_batchTask is not null)
            {
                try { await _batchTask; } catch { /* RunAsync 内部已通过 Fatal 记录 */ }
            }
        }
        _closing = true;
        CancellationTokenSource[] oldSources;
        lock (_probeSources) oldSources = _probeSources.ToArray();
        foreach (CancellationTokenSource cts in oldSources) SafeCancel(cts);
        CleanupTemp();
        EncodingService.ClearUtf8Cache();
        SaveConfig();
        return true;
    }

    public bool IsClosing => _closing;

    #endregion
}
