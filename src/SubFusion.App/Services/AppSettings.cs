using System.Globalization;
using SubFusion.Core;
using SubFusion.Core.Services;

namespace SubFusion.App.Services;

/// <summary>
/// 用户偏好的强类型门面：包住 Core 的 <see cref="AppConfig"/>（JSON 键值存储），
/// 提供带默认值的属性与统一的 <see cref="Save"/>。
///
/// 所有属性赋值都经 <see cref="SetValue{T}"/> 自动落盘；Save 在一把锁内完成，
/// 并用 _dirty 保证同一批修改只写一次文件，避免并发写互相覆盖或重复写盘。
/// Read 用 _suppressSave 抑制自动保存，避免在加载阶段把默认值写回配置。
/// </summary>
public sealed class AppSettings
{
    private readonly AppConfig _config;
    private readonly object _saveGate = new();
    private bool _suppressSave;
    private bool _dirty;

    private AppSettings(AppConfig config) => _config = config;

    public static AppSettings Load() => new(AppConfig.Load());

    public string ConfigPath => AppConfig.ConfigPath;

    private void SetValue<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        _dirty = true;
        if (!_suppressSave) Save();
    }

    #region 外观

    private string _theme = "system";
    public string Theme { get => _theme; set => SetValue(ref _theme, value); }

    #endregion

    #region 外部工具

    private string _ffmpegPath = "";
    public string FfmpegPath { get => _ffmpegPath; set => SetValue(ref _ffmpegPath, value); }
    private string _ffprobePath = "";
    public string FfprobePath { get => _ffprobePath; set => SetValue(ref _ffprobePath, value); }

    #endregion

    #region 字幕合并默认值

    private string _container = "mkv";
    public string Container { get => _container; set => SetValue(ref _container, value); }
    private string _mode = "soft";
    public string Mode { get => _mode; set => SetValue(ref _mode, value); }
    private string _vcodec = "copy";
    public string Vcodec { get => _vcodec; set => SetValue(ref _vcodec, value); }
    private string _acodec = "copy";
    public string Acodec { get => _acodec; set => SetValue(ref _acodec, value); }
    private double _crf = 20;
    public double Crf { get => _crf; set => SetValue(ref _crf, value); }
    private string _preset = "medium";
    public string Preset { get => _preset; set => SetValue(ref _preset, value); }
    private double _workers = 1;
    public double Workers { get => _workers; set => SetValue(ref _workers, value); }
    private string _suffix = "";
    public string Suffix { get => _suffix; set => SetValue(ref _suffix, value); }
    private string _outputDir = "";
    public string OutputDir { get => _outputDir; set => SetValue(ref _outputDir, value); }
    private bool _syncOutputDir = true;
    public bool SyncOutputDir { get => _syncOutputDir; set => SetValue(ref _syncOutputDir, value); }
    private bool _keepExistingSubs = true;
    public bool KeepExistingSubs { get => _keepExistingSubs; set => SetValue(ref _keepExistingSubs, value); }
    private bool _keepMetadata = true;
    public bool KeepMetadata { get => _keepMetadata; set => SetValue(ref _keepMetadata, value); }
    private bool _convertEncoding = true;
    public bool ConvertEncoding { get => _convertEncoding; set => SetValue(ref _convertEncoding, value); }
    private bool _hardMulti = true;
    public bool HardMulti { get => _hardMulti; set => SetValue(ref _hardMulti, value); }
    private bool _verify = true;
    public bool Verify { get => _verify; set => SetValue(ref _verify, value); }
    private bool _replaceSource;
    public bool ReplaceSource { get => _replaceSource; set => SetValue(ref _replaceSource, value); }
    private bool _overwrite = true;
    public bool Overwrite { get => _overwrite; set => SetValue(ref _overwrite, value); }
    private double _threshold = Constants.DefaultThreshold;
    public double Threshold { get => _threshold; set => SetValue(ref _threshold, value); }
    private bool _recursive = true;
    public bool Recursive { get => _recursive; set => SetValue(ref _recursive, value); }

    #endregion

    #region 视频合并默认值

    private string _mergeContainer = "mkv";
    public string MergeContainer { get => _mergeContainer; set => SetValue(ref _mergeContainer, value); }
    private bool _mergeAllowReencode = true;
    public bool MergeAllowReencode { get => _mergeAllowReencode; set => SetValue(ref _mergeAllowReencode, value); }
    // 默认关闭：删除源文件不可逆，必须是用户显式选择加入的行为。
    private bool _mergeDeleteSource;
    public bool MergeDeleteSource { get => _mergeDeleteSource; set => SetValue(ref _mergeDeleteSource, value); }
    private string _mergeCodec = "";
    public string MergeCodec { get => _mergeCodec; set => SetValue(ref _mergeCodec, value); }
    private double _mergeCrf = 20;
    public double MergeCrf { get => _mergeCrf; set => SetValue(ref _mergeCrf, value); }
    private string _mergePreset = "medium";
    public string MergePreset { get => _mergePreset; set => SetValue(ref _mergePreset, value); }
    /// <summary>Contain / Cover / Stretch（与 Core 的 FitMode 名称一致）。</summary>
    private string _mergeFit = "Contain";
    public string MergeFit { get => _mergeFit; set => SetValue(ref _mergeFit, value); }
    /// <summary>Auto / Reencode / Copy（与 Core 的 AudioStrategy 名称一致）。</summary>
    private string _mergeAudio = "Auto";
    public string MergeAudio { get => _mergeAudio; set => SetValue(ref _mergeAudio, value); }
    private bool _mergeChapters = true;
    public bool MergeChapters { get => _mergeChapters; set => SetValue(ref _mergeChapters, value); }
    private bool _mergeVerify = true;
    public bool MergeVerify { get => _mergeVerify; set => SetValue(ref _mergeVerify, value); }
    private string _mergeOutputDir = "";
    public string MergeOutputDir { get => _mergeOutputDir; set => SetValue(ref _mergeOutputDir, value); }

    #endregion

    #region 会话态

    /// <summary>上一次使用的目录，仅用于文件对话框的默认位置提示；赋值即持久化。</summary>
    private string _lastDir = "";
    public string LastDir { get => _lastDir; set => SetValue(ref _lastDir, value); }

    #endregion

    /// <summary>从配置文件读取到属性（加载期间不回写）。</summary>
    public void Read()
    {
        _suppressSave = true;
        try
        {
            Theme = OneOf(Read("theme"), "system", "system", "light", "dark");

            FfmpegPath = Read("ffmpeg") ?? "";
            FfprobePath = Read("ffprobe") ?? "";

            Container = OneOf(Read("container"), "mkv", "mkv", "mp4", "same");
            Mode = OneOf(Read("mode"), "soft", "soft", "hard");
            Vcodec = OneOf(Read("vcodec"), "copy", "copy", "libx264", "libx265", "h264_nvenc");
            Acodec = OneOf(Read("acodec"), "copy", "copy", "aac");
            Preset = OneOf(Read("preset"), "medium",
                "ultrafast", "veryfast", "fast", "medium", "slow", "veryslow");
            Crf = Number(Read("crf"), 20);
            Workers = Number(Read("workers"), 1);
            Suffix = Read("suffix") ?? "";
            OutputDir = Read("outdir") ?? "";
            SyncOutputDir = Bool("sync_outdir", true);
            KeepExistingSubs = Bool("keep_existing_subs", true);
            KeepMetadata = Bool("keep_metadata", true);
            ConvertEncoding = Bool("convert_encoding", true);
            HardMulti = Bool("hard_multi", true);
            Verify = Bool("verify", true);
            ReplaceSource = Bool("replace_source", false);
            Overwrite = Bool("overwrite", true);
            Threshold = Number(Read("threshold"), Constants.DefaultThreshold);
            Recursive = Bool("recursive", true);

            MergeContainer = OneOf(Read("merge_container"), "mkv", "mkv", "mp4");
            MergeAllowReencode = Bool("merge_allow_reencode", true);
            MergeCodec = OneOf(Read("merge_codec"), "", "", "libx264", "libx265");
            MergeCrf = Number(Read("merge_crf"), 20);
            MergePreset = OneOf(Read("merge_preset"), "medium",
                "ultrafast", "veryfast", "fast", "medium", "slow", "veryslow");
            MergeFit = OneOf(Read("merge_fit"), "Contain", "Contain", "Cover", "Stretch");
            MergeAudio = OneOf(Read("merge_audio"), "Auto", "Auto", "Reencode", "Copy");
            MergeChapters = Bool("merge_chapters", true);
            MergeVerify = Bool("merge_verify", true);
            MergeDeleteSource = Bool("merge_delete_source", false);
            MergeOutputDir = Read("merge_outdir") ?? "";

            LastDir = Read("last_dir") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (LastDir.Length == 0)
                LastDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        finally
        {
            _suppressSave = false;
            _dirty = false; // 刚从磁盘加载，没有未保存的修改
        }
    }

    /// <summary>把当前全部属性写入配置；同一锁内串行化，避免并发写丢更新。</summary>
    public void Save()
    {
        if (_suppressSave) return;
        lock (_saveGate)
        {
            if (!_dirty) return;
            try
            {
                _config.Set("theme", Theme);

                _config.Set("ffmpeg", FfmpegPath);
                _config.Set("ffprobe", FfprobePath);
                _config.Set("container", Container);
                _config.Set("mode", Mode);
                _config.Set("vcodec", Vcodec);
                _config.Set("acodec", Acodec);
                // 与原 Python 版一致：这几个数值以字符串写入
                _config.Set("crf", ((int)Crf).ToString(CultureInfo.InvariantCulture));
                _config.Set("preset", Preset);
                _config.Set("workers", ((int)Workers).ToString(CultureInfo.InvariantCulture));
                _config.Set("suffix", Suffix);
                _config.Set("outdir", OutputDir);
                _config.Set("sync_outdir", SyncOutputDir);
                _config.Set("keep_existing_subs", KeepExistingSubs);
                _config.Set("keep_metadata", KeepMetadata);
                _config.Set("convert_encoding", ConvertEncoding);
                _config.Set("hard_multi", HardMulti);
                _config.Set("verify", Verify);
                _config.Set("replace_source", ReplaceSource);
                _config.Set("overwrite", Overwrite);
                _config.Set("threshold", Threshold.ToString("0.##", CultureInfo.InvariantCulture));
                _config.Set("recursive", Recursive);

                _config.Set("merge_container", MergeContainer);
                _config.Set("merge_allow_reencode", MergeAllowReencode);
                _config.Set("merge_codec", MergeCodec);
                _config.Set("merge_crf", ((int)MergeCrf).ToString(CultureInfo.InvariantCulture));
                _config.Set("merge_preset", MergePreset);
                _config.Set("merge_fit", MergeFit);
                _config.Set("merge_audio", MergeAudio);
                _config.Set("merge_chapters", MergeChapters);
                _config.Set("merge_verify", MergeVerify);
                _config.Set("merge_delete_source", MergeDeleteSource);
                _config.Set("merge_outdir", MergeOutputDir);

                _config.Set("last_dir", LastDir);

                _config.Save();
                _dirty = false;
            }
            catch
            {
                _dirty = true; // 写盘失败，保留脏标记以便下次重试
                throw;
            }
        }
    }

    private string? Read(string key) => _config.Get(key);

    private bool Bool(string key, bool fallback)
        => _config.Has(key) && _config.GetBool(key) is bool value ? value : fallback;

    private static double Number(string? raw, double fallback)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value : fallback;

    private static string OneOf(string? raw, string fallback, params string[] allowed)
        => raw is not null && allowed.Contains(raw, StringComparer.Ordinal) ? raw : fallback;
}
