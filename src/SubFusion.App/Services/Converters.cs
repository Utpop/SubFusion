using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace SubFusion.App.Services;

/// <summary>
/// bool → InfoBarSeverity：true 表示必须重编码（错误级红色），false 表示只是重封装（提示级）。
/// 差异提示条的严重级别必须跟着实际策略走，否则无损重封装也会被渲染成报错。
/// </summary>
public sealed class BoolToInfoBarSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? InfoBarSeverity.Error : InfoBarSeverity.Warning;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is InfoBarSeverity s && s == InfoBarSeverity.Error;
}

/// <summary>bool → Visibility（true 显示）；ConverterParameter=invert 时反转。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool flag = value is bool b && b;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>集合/对象为 null 或空 → Collapsed；ConverterParameter=invert 时反转。</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool empty = value switch
        {
            null => true,
            string s => s.Length == 0,
            // bool/false 视为“空”：绑定到可空布尔时 false 应显示（与 null 一致）。
            bool b => !b,
            int i => i == 0,
            System.Collections.ICollection c => c.Count == 0,
            _ => false,
        };
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) empty = !empty;
        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// 任务状态 → 语义画刷。只映射到 Theme.xaml 的语义资源，
/// 页面里不出现任何硬编码颜色，浅色/深色/高对比度都能正确切换。
/// </summary>
public sealed class TaskStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        TaskState state = value is TaskState s ? s : TaskState.Queued;
        bool tint = string.Equals(parameter as string, "Tint", StringComparison.OrdinalIgnoreCase);
        string key = state switch
        {
            TaskState.Completed => tint ? "StatusSuccessTintBrush" : "StatusSuccessBrush",
            TaskState.Failed => tint ? "StatusDangerTintBrush" : "StatusDangerBrush",
            TaskState.Running => tint ? "StatusInfoTintBrush" : "StatusInfoBrush",
            TaskState.Cancelled => tint ? "StatusWarningTintBrush" : "StatusWarningBrush",
            _ => tint ? "StatusInfoTintBrush" : "StatusNeutralBrush",
        };
        return Application.Current.Resources.TryGetValue(key, out object? brush) && brush is not null
            ? brush
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>字幕行的状态标签（done/warn/running/failed/skipped/pending）→ 语义画刷。</summary>
public sealed class StateTagToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string tag = value as string ?? "pending";
        bool tint = string.Equals(parameter as string, "Tint", StringComparison.OrdinalIgnoreCase);
        string key = tag switch
        {
            "done" => tint ? "StatusSuccessTintBrush" : "StatusSuccessBrush",
            "warn" => tint ? "StatusWarningTintBrush" : "StatusWarningBrush",
            "running" => tint ? "StatusInfoTintBrush" : "StatusInfoBrush",
            "failed" => tint ? "StatusDangerTintBrush" : "StatusDangerBrush",
            "skipped" => tint ? "StatusWarningTintBrush" : "StatusWarningBrush",
            _ => tint ? "StatusInfoTintBrush" : "StatusNeutralBrush",
        };
        return Application.Current.Resources.TryGetValue(key, out object? brush) && brush is not null
            ? brush
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// 字符串非空 → bool（用于 InfoBar.IsOpen 这类只接受布尔的属性；
/// 注意不能复用 <see cref="EmptyToVisibilityConverter"/>，它返回的是 Visibility）。
/// </summary>
public sealed class NotEmptyToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool notEmpty = value switch
        {
            null => false,
            string s => s.Length > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
            notEmpty = !notEmpty;
        return notEmpty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>布尔值 → 图标字形（用于工具就绪/缺失、任务成功/失败等）。</summary>
public sealed class BoolToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? "\uE73E" : "\uEA39";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
