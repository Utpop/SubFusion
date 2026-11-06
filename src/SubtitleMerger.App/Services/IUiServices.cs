using SubtitleMerger.Core.Models;

namespace SubtitleMerger.App.Services;

/// <summary>
/// View → 平台能力的窄接口：所有 WinUI 原生控件交互（文件选择器、ContentDialog、
/// ContentDialog 字幕编辑器、资源管理器、UI 线程调度）都由 MainWindow 实现。
/// ViewModel 因此不引用任何 WinUI/WinRT 类型，便于独立测试。
/// </summary>
public interface IUiServices
{
    /// <summary>多选文件；extensions 为 ".mp4" 形式，allowAll 时追加 "*"。</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(string title, IReadOnlyList<string> extensions,
                                               bool allowAll = true);

    Task<string?> PickFolderAsync(string title);
    Task<string?> PickExecutableAsync(string title, string? initialDir);
    Task<string?> PickSaveCsvAsync(string suggestedName, string initialDir);

    Task ShowInfoAsync(string message);
    Task ShowErrorAsync(string message);
    Task<bool> AskYesNoAsync(string message);

    /// <summary>字幕轨编辑对话框；返回 null 表示取消。</summary>
    Task<List<SubTrack>?> EditSubtitlesAsync(string video,
                                             IReadOnlyList<string> candidates,
                                             IReadOnlyList<string> allSubs,
                                             IReadOnlyList<SubTrack> current);

    void OpenFolder(string path);
    void ScrollLogToEnd();
    /// <summary>已在 UI 线程则同步执行，否则投递到 DispatcherQueue（保持回调顺序）。</summary>
    void RunOnUiThread(Action action);
}
