using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubFusion.App.Services;
using SubFusion.Core;
using SubFusion.Core.Models;
using SubFusion.Core.Services;

namespace SubFusion.App.ViewModels;

/// <summary>
/// 字幕合并工作区的 ViewModel。
///
/// 职责边界：
/// - 收集参数、维护输入清单与匹配结果、把状态投影成可绑定属性；
/// - 通过 Core 的 <see cref="BatchProcessor"/> + <see cref="IBatchEvents"/> 执行任务；
/// - 不构造 ffmpeg 命令、不解析输出、不引用任何 WinUI 类型。
///
/// 线程：Core 的回调可以在任意线程触发，所有状态改动都先经 <see cref="IUiServices.RunOnUiThread"/>。
/// </summary>
public sealed partial class SubtitleViewModel : ObservableObject, IBatchEvents
{
    private readonly IUiServices _ui;
    private readonly AppSettings _settings;
    private readonly TaskCenter _tasks;

    private readonly List<string> _videos = new();
    private readonly List<string> _subs = new();
    private readonly List<Job> _jobs = new();
    private readonly Dictionary<string, SubtitleRowViewModel> _rows = new(StringComparer.Ordinal);
    private List<SubtitleRowViewModel> _selected = new();

    private const int MaxLogLines = 4000;

    private readonly object _pendingLogGate = new();
    private readonly Queue<string> _pendingLog = new();
    private bool _logFlushScheduled;
    private int _logLineCount;

    private readonly HashSet<string> _dirtyRows = new(StringComparer.Ordinal);
    private bool _progressFlushScheduled;

    private int _uidSeq;
    private FfmpegCapabilities _caps = new();
    private bool _capsChecked;
    private string _syncSourceDir = "";
    private long _probeGeneration;
    private readonly HashSet<CancellationTokenSource> _probeSources = new();
    private CancellationTokenSource? _cancel;
    private CancellationTokenSource? _runLinked; // links _cancel + record.Cancellation
    private Task? _batchTask;
    private string? _tempDir;
    private TaskRecord? _record;
    private int _doneCount;
    private int _total;
    private bool _loading;

    public SubtitleViewModel(IUiServices ui, AppSettings settings, TaskCenter tasks)
    {
        _ui = ui;
        _settings = settings;
        _tasks = tasks;
    }

    #region 集合与选项

    public ObservableCollection<SubtitleRowViewModel> Rows { get; } = new();

    /// <summary>运行日志（服务端已限流合并，UI 侧只在需要时追加）。</summary>
    public ObservableCollection<string> LogLines { get; } = new();

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
    [ObservableProperty] private bool syncOutputDir = true;
    [ObservableProperty] private bool keepExistingSubs = true;
    [ObservableProperty] private bool keepMetadata = true;
    [ObservableProperty] private bool overwrite = true;
    [ObservableProperty] private bool verify = true;
    [ObservableProperty] private bool replaceSource;
    [ObservableProperty] private bool convertEncoding = true;
    [ObservableProperty] private bool recursive = true;
    [ObservableProperty] private double threshold = Constants.DefaultThreshold;
    [ObservableProperty] private bool hardMulti = true;

    [ObservableProperty] private string statusText = "就绪";
    [ObservableProperty] private string countText = "尚未添加文件";
    [ObservableProperty] private string hintText = "";
    [ObservableProperty] private bool hintVisible;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool canStop;
    [ObservableProperty] private double overallProgress;
    [ObservableProperty] private double overallMaximum = 100;
    [ObservableProperty] private string summaryText = "";

    public bool CanStart => !IsRunning;
    public bool HasRows => Rows.Count > 0;
    public bool ShowEmptyState => Rows.Count == 0;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnContainerChanged(string value) { if (!_loading) UpdateHint(); }
    partial void OnModeChanged(string value)
    {
        if (_loading) return;
        if (value == "hard" && Vcodec == "copy") Vcodec = "libx264";
        UpdateHint();
    }
    partial void OnVcodecChanged(string value) { if (!_loading) UpdateHint(); }

    partial void OnSyncOutputDirChanged(bool value)
    {
        if (_loading || !value) return;
        SyncOutputDirTo(CurrentSourceDir(), force: true);
    }

    #endregion

    #region 初始化与设置同步

    public void Initialize()
    {
        _loading = true;
        Container = _settings.Container;
        Mode = _settings.Mode;
        Vcodec = _settings.Vcodec;
        Acodec = _settings.Acodec;
        Crf = _settings.Crf;
        Preset = _settings.Preset;
        Workers = _settings.Workers;
        Suffix = _settings.Suffix;
        OutputDir = _settings.OutputDir;
        SyncOutputDir = _settings.SyncOutputDir;
        KeepExistingSubs = _settings.KeepExistingSubs;
        KeepMetadata = _settings.KeepMetadata;
        Overwrite = _settings.Overwrite;
        Verify = _settings.Verify;
        ReplaceSource = _settings.ReplaceSource;
        ConvertEncoding = _settings.ConvertEncoding;
        Recursive = _settings.Recursive;
        Threshold = _settings.Threshold;
        HardMulti = _settings.HardMulti;
        _loading = false;
        // 重新加载设置后，输出目录同步的基准失效，重置以便下次添加文件时重新同步。
        _syncSourceDir = "";
        UpdateHint();
        UpdateCountText();
    }

    /// <summary>把工作区当前值写回设置对象（由外壳在退出或保存设置时统一落盘）。</summary>
    public void ApplyToSettings()
    {
        _settings.Container = Container;
        _settings.Mode = Mode;
        _settings.Vcodec = Vcodec;
        _settings.Acodec = Acodec;
        _settings.Crf = Crf;
        _settings.Preset = Preset;
        _settings.Workers = Workers;
        _settings.Suffix = Suffix;
        _settings.OutputDir = OutputDir;
        _settings.SyncOutputDir = SyncOutputDir;
        _settings.KeepExistingSubs = KeepExistingSubs;
        _settings.KeepMetadata = KeepMetadata;
        _settings.Overwrite = Overwrite;
        _settings.Verify = Verify;
        _settings.ReplaceSource = ReplaceSource;
        _settings.ConvertEncoding = ConvertEncoding;
        _settings.Recursive = Recursive;
        _settings.Threshold = Threshold;
        _settings.HardMulti = HardMulti;
    }

    /// <summary>
    /// 设置页改动后把默认值同步回工作区。运行中的任务不受影响；
    /// 已经手动选择的输出目录会被设置里的默认值覆盖——这是"默认值"的语义。
    /// </summary>
    public void ReloadFromSettings()
    {
        if (IsRunning) return;
        _syncSourceDir = ""; // ReloadFromSettings 也显式重置同步基准
        Initialize();
    }

    private void UpdateHint()
    {
        var hints = new List<string>();
        if (Container == "mp4")
            hints.Add("MP4 不支持图形字幕（SUP），这类字幕会被跳过；文本字幕将转为 mov_text（ASS 样式可能丢失）。");
        if (Mode == "hard")
        {
            hints.Add("硬字幕会重新编码视频，速度较慢；中文字幕依赖系统字体，若出现方块字请改用软字幕。");
            if (_capsChecked && !_caps.Libass) hints.Add("当前 ffmpeg 缺少 libass，硬字幕可能失败。");
        }
        if (Vcodec == "copy" && Container == "mp4")
            hints.Add("源视频为非 H.264/HEVC 编码时，MP4 可能需要重新编码。");
        HintText = string.Join(" ", hints);
        HintVisible = hints.Count > 0;
    }

    #endregion

    #region 外部工具状态（由外壳注入）

    [ObservableProperty] private string toolDetail = "正在检测…";
    [ObservableProperty] private bool toolReady;

    public void ApplyToolStatus(string detail, bool ready)
    {
        ToolDetail = detail;
        ToolReady = ready;
        UpdateHint();
    }

    /// <summary>由外壳在定位到 ffmpeg 后写入能力信息，用于提示 libass 缺失等情况。</summary>
    public void ApplyCapabilities(FfmpegCapabilities capabilities)
    {
        _caps = capabilities;
        _capsChecked = true;
        UpdateHint();
    }

    #endregion

    #region 添加 / 移除

    [RelayCommand]
    private async Task AddVideosAsync()
    {
        List<string> extensions = Constants.VideoExts.OrderBy(x => x, StringComparer.Ordinal).ToList();
        IReadOnlyList<string> paths = await _ui.PickFilesAsync("选择视频文件", extensions);
        await AddPathsAsync(paths);
    }

    [RelayCommand]
    private async Task AddSubtitlesAsync()
    {
        List<string> extensions = Constants.SubExts.OrderBy(x => x, StringComparer.Ordinal).ToList();
        IReadOnlyList<string> paths = await _ui.PickFilesAsync("选择字幕文件", extensions);
        await AddPathsAsync(paths);
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        string? folder = await _ui.PickFolderAsync("选择文件夹");
        if (!string.IsNullOrEmpty(folder)) await AddPathsAsync(new[] { folder });
    }

    /// <summary>文件选择器与拖放的共用入口。</summary>
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
            await _ui.ShowErrorAsync("没有找到视频或字幕文件。请确认拖入的是受支持的格式。");
            return;
        }

        string addedDir;
        if (Directory.Exists(paths[0])) addedDir = paths[0];
        else if (File.Exists(paths[0])) addedDir = Path.GetDirectoryName(paths[0]) ?? _settings.LastDir;
        else addedDir = _settings.LastDir;
        _settings.LastDir = addedDir;
        SyncOutputDirTo(addedDir);

        var knownVideos = new HashSet<string>(_videos, StringComparer.Ordinal);
        var knownSubs = new HashSet<string>(_subs, StringComparer.Ordinal);
        List<string> newVideos = collected.Videos.Where(knownVideos.Add).ToList();
        List<string> newSubs = collected.Subtitles.Where(knownSubs.Add).ToList();
        _videos.AddRange(newVideos);
        _subs.AddRange(newSubs);

        if (newVideos.Count == 0 && newSubs.Count == 0)
        {
            await _ui.ShowInfoAsync("这些文件已经在列表里了。");
            return;
        }

        Log($"添加：视频 {newVideos.Count} 个，字幕 {newSubs.Count} 个");
        Rematch();
    }

    public void SetSelection(IEnumerable<SubtitleRowViewModel> selected)
    {
        _selected = selected.Distinct().ToList();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        EditSubtitlesCommand.NotifyCanExecuteChanged();
    }

    private bool HasSelection() => _selected.Count > 0 && !IsRunning;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveSelected()
    {
        if (IsRunning) return;
        foreach (SubtitleRowViewModel row in _selected.ToList())
        {
            Job job = row.Model;
            _videos.Remove(job.Video);
            _jobs.Remove(job);
            _rows.Remove(job.Uid);
            Rows.Remove(row);
        }
        _selected.Clear();
        UpdateCountText();
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
        Rows.Clear();
        _syncSourceDir = "";
        OverallProgress = 0;
        StatusText = "已清空";
        UpdateCountText();
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
        foreach (Job job in _jobs) oldByVideo[job.Video] = job;

        _jobs.Clear();
        _rows.Clear();
        Rows.Clear();
        _selected.Clear();

        foreach (string video in _videos)
        {
            oldByVideo.TryGetValue(video, out Job? old);
            string uid = old?.Uid ?? "j" + (++_uidSeq).ToString(CultureInfo.InvariantCulture);
            var job = new Job(video, new List<SubTrack>(), "",
                              old?.Duration, old?.Info ?? new MediaInfo())
            {
                Uid = uid,
                UserEdited = old?.UserEdited ?? false,
            };

            List<string> autoPaths = matched.Mapping.TryGetValue(video, out var list) ? list : new List<string>();
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
            var row = new SubtitleRowViewModel(job);
            _rows[uid] = row;
            Rows.Add(row);
        }

        int matchedCount = matched.Mapping.Values.Sum(l => l.Count);
        CountText = $"视频 {_videos.Count} 个 / 字幕 {_subs.Count} 个 / 已匹配 {matchedCount} 份 / 未匹配 {matched.Unmatched.Count} 份";
        StatusText = $"匹配完成，共 {_videos.Count} 个视频";
        SummaryText = BuildSummary(matchedCount);
        if (matchedCount > 0) Log($"自动匹配：{matchedCount} 份字幕已挂载");
        if (matched.FuzzyMatches.Count > 0)
            Log($"有 {matched.FuzzyMatches.Count} 份字幕只能模糊对上（可能是同集的不同语言或版本），未自动挂载：" +
                string.Join("、", matched.FuzzyMatches.Take(5).Select(f => Path.GetFileName(f.Subtitle))) +
                (matched.FuzzyMatches.Count > 5 ? " 等" : ""));
        if (matched.Unmatched.Count > 0 && _videos.Count > 0)
            Log($"未匹配的字幕 {matched.Unmatched.Count} 个（可在列表里右键手动指定）");
        NotifyRowsChanged();
        _ = ProbeJobsAsync();
    }

    private string BuildSummary(int matchedCount)
    {
        if (_jobs.Count == 0) return "";
        int withSubs = _jobs.Count(j => j.Subs.Count > 0);
        int without = _jobs.Count - withSubs;
        var sb = new StringBuilder();
        sb.Append($"将处理 {_jobs.Count} 个视频（{withSubs} 个已挂字幕");
        if (without > 0) sb.Append($"，{without} 个无字幕");
        sb.Append("）");
        if (matchedCount == 0 && _subs.Count > 0) sb.Append("；当前没有可用的字幕匹配，请检查文件名或降低阈值");
        return sb.ToString();
    }

    private void UpdateCountText()
    {
        if (_videos.Count == 0 && _subs.Count == 0)
        {
            CountText = "尚未添加文件";
            SummaryText = "";
        }
        else
        {
            int matched = _jobs.Sum(j => j.Subs.Count);
            CountText = $"视频 {_videos.Count} 个 / 字幕 {_subs.Count} 个 / 已匹配 {matched} 份";
        }
        NotifyRowsChanged();
    }

    private void NotifyRowsChanged()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private static SubTrack MakeTrack(string path)
    {
        string kind = Constants.BitmapSubExts.Contains(Path.GetExtension(path).ToLowerInvariant())
            ? "bitmap" : "text";
        return new SubTrack(path, lang: NameNormalizer.DetectTypedLang(path), kind: kind);
    }

    #endregion

    #region 探测

    private async Task ProbeJobsAsync(bool onlyMissing = true)
    {
        string? ffprobe = _settings.FfprobePath;
        if (string.IsNullOrEmpty(ffprobe) || _jobs.Count == 0) return;
        List<Job> targets = _jobs.Where(j => !onlyMissing || j.Duration is null || !j.Info.Probed).ToList();
        if (targets.Count == 0) return;

        CancellationTokenSource[] oldSources;
        lock (_probeSources) oldSources = _probeSources.ToArray();
        foreach (CancellationTokenSource old in oldSources) SafeCancel(old);
        var cts = new CancellationTokenSource();
        lock (_probeSources) _probeSources.Add(cts);
        long generation = ++_probeGeneration;

        var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Job target in targets) byPath[target.Video] = target.Uid;
        try
        {
            await MediaProbe.ProbeManyAsync(ffprobe, byPath.Keys, Constants.ProbeWorkers,
                (path, info) =>
                {
                    if (generation != _probeGeneration) return;
                    if (!byPath.TryGetValue(path, out string? uid)) return;
                    _ui.RunOnUiThread(() => OnProbe(uid, info));
                }, cts.Token);
        }
        catch (OperationCanceledException) { /* 页面切换或重新匹配时正常取消 */ }
        finally
        {
            lock (_probeSources) _probeSources.Remove(cts);
            cts.Dispose();
        }
    }

    private void OnProbe(string uid, MediaInfo info)
    {
        if (!_rows.TryGetValue(uid, out SubtitleRowViewModel? row)) return;
        Job job = row.Model;
        job.Info = info;
        job.Duration = info.Duration ?? job.Duration;
        row.Refresh();
    }

    private async Task EnsureProbedAsync(IReadOnlyList<Job> jobs)
    {
        List<string> missing = jobs.Where(j => !j.Info.Probed).Select(j => j.Video).ToList();
        string? ffprobe = _settings.FfprobePath;
        if (missing.Count == 0 || string.IsNullOrEmpty(ffprobe)) return;
        StatusText = $"正在读取媒体信息（{missing.Count} 个）…";
        Dictionary<string, MediaInfo> results = await MediaProbe.ProbeManyAsync(
            ffprobe, missing, Constants.ProbeWorkers);
        foreach (Job job in jobs)
        {
            if (!results.TryGetValue(job.Video, out MediaInfo? info)) continue;
            job.Info = info;
            job.Duration = info.Duration ?? job.Duration;
            if (_rows.TryGetValue(job.Uid, out SubtitleRowViewModel? row)) row.Refresh();
        }
    }

    /// <summary>剔除 ffmpeg 无法识别的字幕（空文件、乱码、损坏的图形字幕），其余照常处理。</summary>
    private async Task DropInvalidSubsAsync()
    {
        string? ffprobe = _settings.FfprobePath;
        if (string.IsNullOrEmpty(ffprobe)) return;
        string probe = ffprobe;
        var paths = _jobs.SelectMany(j => j.Subs).Where(t => t.Enabled)
            .Select(t => t.Path).Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count == 0) return;

        StatusText = $"正在校验字幕文件（{paths.Count} 个）…";
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
                    result = await MediaProbe.ProbeSubtitleAsync(probe, path)
                        .WaitAsync(TimeSpan.FromSeconds(25), ct);
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
            return;
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
            if (_rows.TryGetValue(job.Uid, out SubtitleRowViewModel? row)) row.Refresh();
        }
        Log($"已剔除 {bad.Count} 个无效字幕文件，其余照常处理");
        UpdateCountText();
    }

    #endregion

    #region 字幕轨编辑

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditSubtitlesAsync()
    {
        if (IsRunning || _selected.Count == 0) return;
        SubtitleRowViewModel row = _selected[0];
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

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        string? ffmpeg = _settings.FfmpegPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            await _ui.ShowErrorAsync("未找到 ffmpeg，请先在「设置 → 媒体工具」中指定路径。");
            return;
        }
        if (_jobs.Count == 0)
        {
            await _ui.ShowInfoAsync("请先添加视频文件。");
            return;
        }

        Options? options = CollectOptions();
        if (options is null)
        {
            await _ui.ShowInfoAsync("请先选择输出目录。");
            return;
        }

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
            _cancel.Dispose();
            _cancel = null;
            return;
        }
        if (batchToken.IsCancellationRequested)
        {
            _cancel.Dispose();
            _cancel = null;
            return;
        }

        try { Directory.CreateDirectory(options.OutputDir); }
        catch (Exception ex)
        {
            await _ui.ShowErrorAsync($"无法创建输出目录：{ex.Message}");
            _cancel.Dispose();
            _cancel = null;
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), "subfusion_" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(_tempDir); }
        catch (Exception ex)
        {
            await _ui.ShowErrorAsync($"无法创建临时目录：{ex.Message}");
            _cancel.Dispose();
            _cancel = null;
            return;
        }

        IsRunning = true;
        CanStop = true;
        _doneCount = 0;
        _total = _jobs.Count;
        OverallMaximum = _total;
        OverallProgress = 0;
        StatusText = $"开始处理 {_total} 个视频…";

        _record = _tasks.Add(TaskKind.SubtitleMerge,
            $"字幕合并 · {_total} 个视频",
            Path.GetFileName(Path.GetDirectoryName(_videos[0]) ?? "") is { Length: > 0 } folder
                ? folder + $"（{_total} 个视频）"
                : $"{_total} 个视频",
            options.OutputDir);

        // 排队阶段已被任务中心取消：不启动 Core，直接标记已取消。
        if (_record.Cancellation.IsCancellationRequested)
        {
            _tasks.Finish(_record, TaskState.Cancelled, "已取消");
            CleanupTemp();
            IsRunning = false;
            CanStop = false;
            _cancel?.Dispose();
            _cancel = null;
            _record = null;
            return;
        }

        // 关键：把任务记录的取消令牌与本次执行令牌联动，任务中心 Cancel 可真正终止批处理。
        _runLinked = CancellationTokenSource.CreateLinkedTokenSource(
            _cancel!.Token, _record.Cancellation.Token);
        CancellationToken effectiveToken = _runLinked.Token;

        _tasks.Update(_record, r =>
        {
            r.State = TaskState.Running;
            r.Stage = "处理中";
            r.Message = $"{options.Mode switch { "hard" => "硬字幕", _ => "软字幕" }} · 容器 {options.Container}";
        });

        var processor = new BatchProcessor(this);
        _batchTask = processor.RunAsync(new BatchRequest
        {
            Options = options,
            Ffmpeg = ffmpeg,
            Ffprobe = _settings.FfprobePath,
            Jobs = _jobs.ToList(),
            TempDir = _tempDir,
        }, effectiveToken);

        try
        {
            await _batchTask;
        }
        catch (Exception ex)
        {
            Log("批量任务异常：" + ex.Message);
        }
        finally
        {
            CleanupTemp();
            _batchTask = null;
            _runLinked?.Dispose();
            _runLinked = null;
            _cancel?.Dispose();
            _cancel = null;
            IsRunning = false;
            CanStop = false;
            _record = null;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        if (!IsRunning) return;
        // 先取消任务记录（经 linked token 通知 Core），再取消本地令牌兜底；
        // 立即把任务中心状态切到已取消，不等每个 job 慢慢返回。
        if (_record is not null)
        {
            try { _record.Cancellation.Cancel(); } catch (ObjectDisposedException) { /* 收尾中 */ }
            _tasks.Finish(_record, TaskState.Cancelled, "已取消");
        }
        _cancel?.Cancel();
        StatusText = "正在停止，请稍候…";
        CanStop = false;
    }

    private void CleanupTemp()
    {
        if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* 临时目录清理失败不阻塞 */ }
        }
        _tempDir = null;
    }

    #endregion

    #region IBatchEvents —— 一律先投递回 UI 线程

    public void Log(string text)
    {
        string stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        bool dispatchNow = false;
        lock (_pendingLogGate)
        {
            _pendingLog.Enqueue($"[{stamp}] {text}");
            if (_pendingLog.Count > MaxLogLines) _pendingLog.Dequeue();
            if (!_logFlushScheduled)
            {
                _logFlushScheduled = true;
                dispatchNow = true;
            }
        }
        if (dispatchNow) _ui.RunOnUiThread(DrainLogQueue);
    }

    private void DrainLogQueue()
    {
        var batch = new StringBuilder();
        int added = 0;
        lock (_pendingLogGate)
        {
            while (_pendingLog.Count > 0)
            {
                batch.Append(_pendingLog.Dequeue()).Append('\n');
                added++;
            }
            _logFlushScheduled = false;
        }
        if (added == 0) return;

        string[] lines = batch.ToString().TrimEnd('\n').Split('\n');
        TaskRecord? record = _record;
        foreach (string line in lines)
        {
            LogLines.Add(line);
            // 同一份日志也进入任务中心，任务中心因此能对字幕任务展示真实诊断
            if (record is not null) _tasks.AppendLog(record, line);
        }
        _logLineCount += added;
        TrimLog();
        if (_logLineCount > 1) _ui.ScrollLogToEnd();
    }

    private void TrimLog()
    {
        while (LogLines.Count > MaxLogLines)
        {
            LogLines.RemoveAt(0);
            _logLineCount--;
        }
    }

    public void JobUpdate(Job job)
        => _ui.RunOnUiThread(() =>
        {
            if (_rows.TryGetValue(job.Uid, out SubtitleRowViewModel? row)) row.Refresh();
        });

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
        List<string> uids;
        lock (_dirtyRows)
        {
            _progressFlushScheduled = false;
            uids = _dirtyRows.ToList();
            _dirtyRows.Clear();
        }
        foreach (string uid in uids)
            if (_rows.TryGetValue(uid, out SubtitleRowViewModel? row)) row.Refresh();

        // 总体进度 = 已完成数 + 当前运行行的完成比例（避免"卡在整数"的假象）
        if (_total > 0)
        {
            double partial = 0;
            foreach (SubtitleRowViewModel row in Rows)
                if (row.Model.State == Job.StateRunning) partial += Math.Clamp(row.Model.Progress / 100.0, 0, 1);
            OverallProgress = Math.Min(_total, _doneCount + partial);
        }
    }

    public void JobDone(Job job)
        => _ui.RunOnUiThread(() =>
        {
            _doneCount++;
            OverallProgress = Math.Min(_total, _doneCount);
            if (_rows.TryGetValue(job.Uid, out SubtitleRowViewModel? row)) row.Refresh();

            string elapsed = "";
            if (job.StartedAt.HasValue && job.FinishedAt.HasValue)
                elapsed = $"，用时 {Formatting.FormatDuration(job.FinishedAt.Value - job.StartedAt.Value)}";

            if (job.State == Job.StateDone)
            {
                Log($"[完成] {job.Name} -> {Path.GetFileName(job.Output)}{elapsed}");
                if (job.VerifyOk == false)
                    Log($"[校验异常] {job.Name}：{string.Join("；", job.VerifyIssues)}");
            }
            else if (job.State == Job.StateFailed) Log($"[失败] {job.Name}：{job.Message}");
            else if (job.State == Job.StateSkipped) Log($"[跳过] {job.Name}：{job.Message}");
            else if (job.State == Job.StateCancelled) Log($"[取消] {job.Name}");

            StatusText = $"已完成 {_doneCount} / {_total}";
            if (_record is not null)
            {
                int done = _doneCount;
                _tasks.Update(_record, r =>
                {
                    r.Progress = _total > 0 ? 100.0 * done / _total : 0;
                    r.Stage = $"已完成 {done}/{_total}";
                });
            }
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

            if (_record is not null)
            {
                TaskRecord record = _record;
                TaskState state = summary.Cancelled > 0 && summary.Done == 0
                    ? TaskState.Cancelled
                    : summary.Failed > 0 && summary.Done == 0 ? TaskState.Failed : TaskState.Completed;
                _tasks.Update(record, r =>
                {
                    r.State = state;
                    r.Stage = "已结束";
                    r.VerifyFailed = summary.Warned > 0;
                    r.Message = $"完成 {summary.Done}，失败 {summary.Failed}，跳过 {summary.Skipped}，用时 {total}";
                    r.Result = summary;
                });
                _tasks.Finish(record, state, record.Message);
            }
        });

    public void Fatal(string text)
    {
        Log(text);
        _ui.RunOnUiThread(() =>
        {
            if (_record is not null)
            {
                _tasks.Finish(_record, TaskState.Failed, Formatting.Truncate(text, 160));
                _record = null;
            }
        });
    }

    #endregion

    #region 输出目录 / 日志 / 报告

    private string CurrentSourceDir()
        => _videos.Count > 0 ? (Path.GetDirectoryName(_videos[0]) ?? "") : "";

    private void SyncOutputDirTo(string sourceDir, bool force = false)
    {
        if (!SyncOutputDir || string.IsNullOrEmpty(sourceDir)) return;
        if (!force && string.Equals(sourceDir, _syncSourceDir, StringComparison.OrdinalIgnoreCase)) return;
        _syncSourceDir = sourceDir;
        if (!string.Equals(OutputDir?.Trim(), sourceDir, StringComparison.OrdinalIgnoreCase))
            OutputDir = sourceDir;
    }

    [RelayCommand]
    private async Task ChooseOutputDirAsync()
    {
        string? folder = await _ui.PickFolderAsync("选择输出目录");
        if (string.IsNullOrEmpty(folder)) return;
        OutputDir = folder;
        _settings.LastDir = folder;
        _syncSourceDir = CurrentSourceDir();
    }

    [RelayCommand]
    private void UseSourceDir()
    {
        string dir = CurrentSourceDir();
        if (dir.Length == 0) return;
        OutputDir = dir;
        _syncSourceDir = dir;
    }

    [RelayCommand]
    private void OpenOutputDir()
    {
        string folder = OutputDir?.Trim() ?? "";
        if (folder.Length > 0 && Directory.Exists(folder)) _ui.OpenFolder(folder);
        else _ui.ShowInfoAsync("输出目录还不存在。");
    }

    [RelayCommand]
    private void OpenSelectedVideoFolder()
    {
        if (_selected.Count == 0) return;
        string dir = Path.GetDirectoryName(_selected[0].Model.Video) ?? "";
        if (dir.Length > 0 && Directory.Exists(dir)) _ui.OpenFolder(dir);
    }

    [RelayCommand]
    private async Task OpenSelectedOutputAsync()
    {
        if (_selected.Count == 0) return;
        Job job = _selected[0].Model;
        if (!string.IsNullOrEmpty(job.Output) && File.Exists(job.Output))
            _ui.OpenFolder(Path.GetDirectoryName(job.Output) ?? "");
        else
            await _ui.ShowInfoAsync("输出文件还不存在。");
    }

    [RelayCommand]
    private void ClearLog()
    {
        lock (_pendingLogGate)
        {
            _pendingLog.Clear();
            _logFlushScheduled = false;
        }
        LogLines.Clear();
        _logLineCount = 0;
    }

    [RelayCommand]
    private async Task ExportReportAsync()
    {
        if (_jobs.Count == 0)
        {
            await _ui.ShowInfoAsync("没有可导出的任务。");
            return;
        }
        string defaultName = "合并报告_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv";
        string initialDir = OutputDir?.Trim() ?? "";
        if (initialDir.Length == 0) initialDir = _settings.LastDir;
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
                    job.Info.Width > 0 && job.Info.Height > 0 ? $"{job.Info.Width}x{job.Info.Height}" : "-",
                    job.Duration.HasValue ? Formatting.F3(job.Duration.Value) : "",
                    subs,
                    job.State,
                    verifyText,
                    job.Output,
                    job.Size?.ToString(CultureInfo.InvariantCulture) ?? "",
                    job.Message,
                });
            }
            byte[] body = new UTF8Encoding(false).GetBytes(sb.ToString());
            byte[] withBom = new byte[body.Length + 3];
            withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
            Array.Copy(body, 0, withBom, 3, body.Length);
            await File.WriteAllBytesAsync(path, withBom);
            Log($"已导出报告：{path}");
            StatusText = "报告已导出";
        }
        catch (Exception ex)
        {
            await _ui.ShowErrorAsync($"导出失败：{ex.Message}");
        }
    }

    private static void AppendCsvRow(StringBuilder sb, IReadOnlyList<string> fields)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(',');
            string field = fields[i] ?? "";
            if (field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                sb.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
            else sb.Append(field);
        }
        sb.Append("\r\n");
    }

    #endregion

    #region 关闭

    /// <summary>退出前调用：取消探测、回收临时目录。返回 false 表示用户取消了退出。</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsRunning) return true;
        bool yes = await _ui.AskYesNoAsync("字幕合并任务正在运行中，确定退出吗？");
        if (!yes) return false;
        _cancel?.Cancel();
        if (_batchTask is not null)
        {
            try { await _batchTask; } catch { /* RunAsync 内部已通过 Fatal 记录 */ }
        }
        return true;
    }

    public void Shutdown()
    {
        CancellationTokenSource[] oldSources;
        lock (_probeSources) oldSources = _probeSources.ToArray();
        foreach (CancellationTokenSource cts in oldSources) SafeCancel(cts);
        CleanupTemp();
        EncodingService.ClearUtf8Cache();
    }

    private static void SafeCancel(CancellationTokenSource? cts)
    {
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* 任务刚收尾并已释放 */ }
    }

    #endregion
}
