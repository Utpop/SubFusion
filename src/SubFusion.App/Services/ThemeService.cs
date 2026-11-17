using Microsoft.UI.Xaml;

namespace SubFusion.App.Services;

/// <summary>
/// 主题应用。WinUI 3 不允许在运行期改 <c>Application.RequestedTheme</c>，
/// 正确的运行期切换方式是设置窗口根元素的 <see cref="FrameworkElement.RequestedTheme"/>：
/// 它会作用于整棵可视树（含 ContentDialog、Flyout、TeachingTip 这些由 XamlRoot 承载的浮层）。
/// </summary>
public static class ThemeService
{
    public static ElementTheme ToElementTheme(string? theme) => theme switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,     // 跟随系统
    };

    public static string ToLabel(string? theme) => theme switch
    {
        "light" => "浅色",
        "dark" => "深色",
        _ => "跟随系统",
    };

    /// <summary>把主题应用到指定根元素（主窗口的 Root）。</summary>
    public static void Apply(FrameworkElement? root, string? theme)
    {
        if (root is null) return;
        root.RequestedTheme = ToElementTheme(theme);
    }
}
