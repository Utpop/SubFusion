using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubFusion.App.Services;
using SubFusion.Core;
using SubFusion.Core.Services;

namespace SubFusion.App.ViewModels;

/// <summary>
/// 设置页。按功能分区组织，全部字段都直接映射到 <see cref="AppSettings"/>（即 Core 的 AppConfig），
/// 修改后立即落盘；不支持的项不做假 UI。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IUiServices _ui;
    private bool _loading;

    public SettingsViewModel(AppSettings settings, IUiServices ui)
    {
        _settings = settings;
        _ui = ui;
    }

    /// <summary>任意设置变更并落盘后触发；外壳据此应用主题并同步工作区默认值。</summary>
    public event EventHandler? Changed;

    /// <summary>仅媒体工具路径变化时触发；外壳据此重新定位 ffmpeg/ffprobe（避免每次改参数都起子进程探测）。</summary>
    public event EventHandler? ToolsChanged;

    public string ConfigPath => _settings.ConfigPath;

    public IReadOnlyList<string> Containers { get; } = new[] { "mkv", "mp4", "same" };
    public IReadOnlyList<string> Modes { get; } = new[] { "soft", "hard" };
    public IReadOnlyList<string> Vcodecs { get; } = new[] { "copy", "libx264", "libx265", "h264_nvenc" };
    public IReadOnlyList<string> Acodecs { get; } = new[] { "copy", "aac" };
    public IReadOnlyList<string> Presets { get; } =
        new[] { "ultrafast", "veryfast", "fast", "medium", "slow", "veryslow" };
    public IReadOnlyList<string> MergeContainers { get; } = new[] { "mkv", "mp4" };
    public IReadOnlyList<string> MergeCodecs { get; } = new[] { "", "libx264", "libx265" };
    public IReadOnlyList<string> MergeFits { get; } = new[] { "Contain", "Cover", "Stretch" };
    public IReadOnlyList<string> MergeAudios { get; } = new[] { "Auto", "Reencode", "Copy" };

    #region 绑定属性（全部直接读写 AppSettings）

    public string Theme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value) return;
            _settings.Theme = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSystemTheme));
            OnPropertyChanged(nameof(IsLightTheme));
            OnPropertyChanged(nameof(IsDarkTheme));
            SaveAndNotify();
        }
    }

    /// <summary>三选一主题的单选按钮绑定（互斥逻辑集中在这里，XAML 里不做判断）。</summary>
    public bool IsSystemTheme
    {
        get => _settings.Theme == "system";
        set { if (value) Theme = "system"; }
    }

    public bool IsLightTheme
    {
        get => _settings.Theme == "light";
        set { if (value) Theme = "light"; }
    }

    public bool IsDarkTheme
    {
        get => _settings.Theme == "dark";
        set { if (value) Theme = "dark"; }
    }

    public string FfmpegPath
    {
        get => _settings.FfmpegPath;
        set
        {
            if (_settings.FfmpegPath == value) return;
            _settings.FfmpegPath = value;
            OnPropertyChanged();
            SaveAndNotify();
            ToolsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string FfprobePath
    {
        get => _settings.FfprobePath;
        set
        {
            if (_settings.FfprobePath == value) return;
            _settings.FfprobePath = value;
            OnPropertyChanged();
            SaveAndNotify();
            ToolsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Container
    {
        get => _settings.Container;
        set { if (_settings.Container == value) return; _settings.Container = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public string Mode
    {
        get => _settings.Mode;
        set { if (_settings.Mode == value) return; _settings.Mode = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public string Vcodec
    {
        get => _settings.Vcodec;
        set { if (_settings.Vcodec == value) return; _settings.Vcodec = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public string Acodec
    {
        get => _settings.Acodec;
        set { if (_settings.Acodec == value) return; _settings.Acodec = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public double Crf
    {
        get => _settings.Crf;
        set { if (Math.Abs(_settings.Crf - value) < 0.001) return; _settings.Crf = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public string Preset
    {
        get => _settings.Preset;
        set { if (_settings.Preset == value) return; _settings.Preset = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public double Workers
    {
        get => _settings.Workers;
        set { if (Math.Abs(_settings.Workers - value) < 0.001) return; _settings.Workers = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public string Suffix
    {
        get => _settings.Suffix;
        set { if (_settings.Suffix == value) return; _settings.Suffix = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public string OutputDir
    {
        get => _settings.OutputDir;
        set { if (_settings.OutputDir == value) return; _settings.OutputDir = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public bool SyncOutputDir
    {
        get => _settings.SyncOutputDir;
        set { if (_settings.SyncOutputDir == value) return; _settings.SyncOutputDir = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public bool KeepExistingSubs
    {
        get => _settings.KeepExistingSubs;
        set { if (_settings.KeepExistingSubs == value) return; _settings.KeepExistingSubs = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public bool KeepMetadata
    {
        get => _settings.KeepMetadata;
        set { if (_settings.KeepMetadata == value) return; _settings.KeepMetadata = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public bool ConvertEncoding
    {
        get => _settings.ConvertEncoding;
        set { if (_settings.ConvertEncoding == value) return; _settings.ConvertEncoding = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public bool HardMulti
    {
        get => _settings.HardMulti;
        set { if (_settings.HardMulti == value) return; _settings.HardMulti = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public bool Verify
    {
        get => _settings.Verify;
        set { if (_settings.Verify == value) return; _settings.Verify = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public bool ReplaceSource
    {
        get => _settings.ReplaceSource;
        set { if (_settings.ReplaceSource == value) return; _settings.ReplaceSource = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public bool Overwrite
    {
        get => _settings.Overwrite;
        set { if (_settings.Overwrite == value) return; _settings.Overwrite = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public double Threshold
    {
        get => _settings.Threshold;
        set { if (Math.Abs(_settings.Threshold - value) < 0.001) return; _settings.Threshold = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public bool Recursive
    {
        get => _settings.Recursive;
        set { if (_settings.Recursive == value) return; _settings.Recursive = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public string MergeContainer
    {
        get => _settings.MergeContainer;
        set { if (_settings.MergeContainer == value) return; _settings.MergeContainer = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public string MergeCodec
    {
        get => _settings.MergeCodec;
        set { if (_settings.MergeCodec == value) return; _settings.MergeCodec = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public double MergeCrf
    {
        get => _settings.MergeCrf;
        set { if (Math.Abs(_settings.MergeCrf - value) < 0.001) return; _settings.MergeCrf = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public string MergePreset
    {
        get => _settings.MergePreset;
        set { if (_settings.MergePreset == value) return; _settings.MergePreset = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public string MergeFit
    {
        get => _settings.MergeFit;
        set { if (_settings.MergeFit == value) return; _settings.MergeFit = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public string MergeAudio
    {
        get => _settings.MergeAudio;
        set { if (_settings.MergeAudio == value) return; _settings.MergeAudio = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public bool MergeAllowReencode
    {
        get => _settings.MergeAllowReencode;
        set { if (_settings.MergeAllowReencode == value) return; _settings.MergeAllowReencode = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public bool MergeChapters
    {
        get => _settings.MergeChapters;
        set { if (_settings.MergeChapters == value) return; _settings.MergeChapters = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public bool MergeVerify
    {
        get => _settings.MergeVerify;
        set { if (_settings.MergeVerify == value) return; _settings.MergeVerify = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    public string MergeOutputDir
    {
        get => _settings.MergeOutputDir;
        set { if (_settings.MergeOutputDir == value) return; _settings.MergeOutputDir = value; OnPropertyChanged(); SaveAndNotify(); }
    }

    #endregion

    #region 工具状态展示

    [ObservableProperty] private string toolStatusText = "尚未检测";
    [ObservableProperty] private bool toolReady;
    [ObservableProperty] private string capabilityText = "";
    [ObservableProperty] private bool detecting;

    public void ApplyToolStatus(string text, bool ready, string capabilities)
    {
        ToolStatusText = text;
        ToolReady = ready;
        CapabilityText = capabilities;
    }

    #endregion

    private void SaveAndNotify()
    {
        if (_loading) return;
        // AppSettings setters persist automatically; Save() here is a no-op safety net.
        _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reload()
    {
        _loading = true;
        OnPropertyChanged(string.Empty);
        _loading = false;
    }

    [RelayCommand]
    private async Task PickFfmpegAsync()
    {
        string? path = await _ui.PickExecutableAsync("选择 ffmpeg.exe",
            FfmpegPath.Length > 0 ? (Path.GetDirectoryName(FfmpegPath) ?? "C:/") : "C:/");
        if (string.IsNullOrEmpty(path)) return;
        FfmpegPath = path;
        string dir = Path.GetDirectoryName(path) ?? "";
        string guess = Path.Combine(dir, "ffprobe.exe");
        if (File.Exists(guess)) FfprobePath = guess;
    }

    [RelayCommand]
    private async Task PickFfprobeAsync()
    {
        string? path = await _ui.PickExecutableAsync("选择 ffprobe.exe",
            FfprobePath.Length > 0 ? (Path.GetDirectoryName(FfprobePath) ?? "C:/") : "C:/");
        if (!string.IsNullOrEmpty(path)) FfprobePath = path;
    }

    [RelayCommand]
    private async Task ChooseSubtitleOutputDirAsync()
    {
        string? folder = await _ui.PickFolderAsync("选择字幕合并默认输出目录");
        if (!string.IsNullOrEmpty(folder)) OutputDir = folder;
    }

    [RelayCommand]
    private async Task ChooseMergeOutputDirAsync()
    {
        string? folder = await _ui.PickFolderAsync("选择视频合并默认输出目录");
        if (!string.IsNullOrEmpty(folder)) MergeOutputDir = folder;
    }

    [RelayCommand]
    private void Recheck()
    {
        // 实际检测由外壳执行；这里只标记"检测中"，外壳完成后回写状态。
        Detecting = true;
        ToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenConfigFolder()
    {
        string? folder = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) _ui.OpenFolder(folder);
        else _ui.ShowInfoAsync("配置目录还不存在。");
    }

    [RelayCommand]
    private async Task ResetDefaultsAsync()
    {
        bool yes = await _ui.AskYesNoAsync(
            "把字幕合并与视频合并的参数恢复为出厂默认值？\n\n" +
            "媒体工具路径与主题设置会保留。配置文件中的其它字段不会被删除。");
        if (!yes) return;

        _settings.Container = "mkv";
        _settings.Mode = "soft";
        _settings.Vcodec = "copy";
        _settings.Acodec = "copy";
        _settings.Crf = 20;
        _settings.Preset = "medium";
        _settings.Workers = 1;
        _settings.Suffix = "";
        _settings.KeepExistingSubs = true;
        _settings.KeepMetadata = true;
        _settings.ConvertEncoding = true;
        _settings.HardMulti = true;
        _settings.Verify = true;
        _settings.ReplaceSource = false;
        _settings.Overwrite = true;
        _settings.Threshold = Constants.DefaultThreshold;
        _settings.Recursive = true;
        _settings.MergeContainer = "mkv";
        _settings.MergeAllowReencode = true;
        _settings.MergeCodec = "";
        _settings.MergeCrf = 20;
        _settings.MergePreset = "medium";
        _settings.MergeFit = "Contain";
        _settings.MergeAudio = "Auto";
        _settings.MergeChapters = true;
        _settings.MergeVerify = true;

        _settings.Save(); // flush all batched resets in one write
        Reload();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
