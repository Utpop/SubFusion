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

/// <summary>
/// 任务是否成功 → InfoBarSeverity：true = Success（绿色），false = Error（红色）。
///
/// 必须与 <see cref="BoolToInfoBarSeverityConverter"/> 分开：
/// 那个转换器的 true 表示「存在会重编码的差异」，映射到 Error；
/// 而这里是「任务成功了」，映射到 Success。两者语义相反，
/// 直接复用会让成功的任务显示成红色报错。
/// </summary>
public sealed class ResultOkToSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool ok && ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is InfoBarSeverity s && s == InfoBarSeverity.Success;
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
/// 注意不能复用 <see cref="BoolToVisibilityConverter"/>，它返回的是 Visibility）。
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

/// <summary>
/// 字符串非空 → Visibility（空串折叠、非空显示）。
/// 与 <see cref="NotEmptyToBoolConverter"/> 的区别只在返回类型：Visibility 属性必须用它，
/// 直接把返回 bool 的转换器绑到 Visibility 上会在运行期抛类型转换异常。
/// </summary>
public sealed class NotEmptyToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool visible = value switch
        {
            null => false,
            string s => s.Length > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// 「这组差异会导致重新编码」→ 语义画刷。
/// 差异分组里，会重编码的那一组用警示色、无损失的那一组保持中性色——
/// 让颜色本身回答"画质会不会变差"，而不是要求用户读完全部差异。
/// </summary>
public sealed class LossyToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool lossy = value is bool b && b;
        string key = lossy ? "StatusWarningBrush" : "StatusInfoBrush";
        return Application.Current.Resources.TryGetValue(key, out object? brush) && brush is not null
            ? brush
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// JPEG 字节 → ImageSource（视频裁剪页的预览帧）。
///
/// ViewModel 里放的是 byte[] 而不是 BitmapImage，是为了让它不引用 WinUI；
/// 转换放在这一层，重活就只有这一个地方会碰 WinRT 类型。
/// </summary>
public sealed class BytesToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not byte[] bytes || bytes.Length == 0) return null;
        try
        {
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            }
            stream.Seek(0);
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            image.SetSource(stream);
            return image;
        }
        catch
        {
            // 预览是辅助功能，解码失败不该打断裁剪流程。
            return null;
        }
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

/// <summary>
/// 技术值 → 大白话标签。只用于下拉框的显示（ItemTemplate），
/// SelectedItem 绑定的仍是原始技术值，所以设置存储、命令生成完全不受影响。
/// 未登记的值原样返回，避免漏配时出现空白。
/// </summary>
public sealed class TechLabelConverter : IValueConverter
{
    private static readonly IReadOnlyDictionary<string, string> Labels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mkv"] = "MKV（推荐·可装多条字幕和章节）",
            ["mp4"] = "MP4（通用·手机和网页都能放）",
            ["same"] = "跟原文件一样",
            [""] = "自动（跟着输入走·推荐）",
            ["soft"] = "软字幕（能切换/关闭·推荐）",
            ["hard"] = "硬字幕（直接烧进画面·不能关）",
            ["copy"] = "不重新编码（直接复制·最快）",
            ["libx264"] = "H.264（通用·推荐）",
            ["libx265"] = "H.265（文件更小·编码更慢）",
            ["h264_nvenc"] = "H.264 显卡加速（需英伟达显卡）",
            ["aac"] = "AAC（通用·推荐）",
            ["ultrafast"] = "极快（文件最大）",
            ["veryfast"] = "很快",
            ["fast"] = "快",
            ["medium"] = "均衡（推荐）",
            ["slow"] = "慢（文件更小）",
            ["veryslow"] = "极慢（文件最小）",
            ["Contain"] = "完整显示（不够的地方补黑边）",
            ["Cover"] = "铺满画面（多出来的裁掉）",
            ["Stretch"] = "直接拉满（画面可能变形）",
            ["Auto"] = "自动（能不重编码就不重编码·推荐）",
            ["Reencode"] = "统一重新编码",
            ["speed"] = "偏速度（快·文件大一些）",
            ["balanced"] = "均衡（推荐）",
            ["quality"] = "偏画质（慢·文件小一些）",
        };

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string s && Labels.TryGetValue(s, out string? label) ? label : (value ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

