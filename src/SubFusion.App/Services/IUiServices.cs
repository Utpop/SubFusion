using SubFusion.Core.Models;

namespace SubFusion.App.Services;

/// <summary>
/// View → 平台能力的窄接口。所有 WinUI 原生交互（文件选择器、对话框、剪贴板、
/// 资源管理器、UI 线程调度）都由外壳窗口实现，ViewModel 因此不引用任何 WinUI/WinRT 类型，
/// 可以脱离 UI 单独构造并测试。
///
/// 约定：所有方法都在 UI 线程调用（命令本身在 UI 线程触发）；
/// <see cref="RunOnUiThread"/> 用于把任意线程的回调投递回来。
/// </summary>
public interface IUiServices
{
    /// <summary>多选文件；extensions 为 ".mp4" 形式，allowAll 时追加 "*"。</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(string title, IReadOnlyList<string> extensions,
                                               bool allowAll = true);

    Task<string?> PickFolderAsync(string title);
    Task<string?> PickExecutableAsync(string title, string? initialDir);
    Task<string?> PickSaveCsvAsync(string suggestedName, string initialDir);

    /// <summary>保存视频文件对话框（视频合并的输出路径）。</summary>
    Task<string?> PickSaveVideoAsync(string title, string suggestedName, string extension,
                                     string? initialDir);

    Task ShowInfoAsync(string message);
    Task ShowErrorAsync(string message);
    Task<bool> AskYesNoAsync(string message);

    /// <summary>字幕轨编辑对话框；返回 null 表示取消。</summary>
    Task<List<SubTrack>?> EditSubtitlesAsync(string video,
                                             IReadOnlyList<string> candidates,
                                             IReadOnlyList<string> allSubs,
                                             IReadOnlyList<SubTrack> current);

    /// <summary>把文本写入系统剪贴板（任务中心的「复制诊断日志」）。</summary>
    Task SetClipboardTextAsync(string text);

    void OpenFolder(string path);

    /// <summary>滚动日志视图到底部（仅字幕合并页使用）。</summary>
    void ScrollLogToEnd();

    /// <summary>已在 UI 线程则同步执行，否则投递到 DispatcherQueue（保持回调顺序）。</summary>
    void RunOnUiThread(Action action);
}
