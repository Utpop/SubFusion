using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace SubtitleMerger.App.Services;

/// <summary>
/// 把任务状态映射为行背景/前景色（原 Tk Treeview 的 tag_configure 配色）。
/// ConverterParameter = "Back" 取背景，否则取前景。
/// </summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string tag = value as string ?? "pending";
        bool background = string.Equals(parameter as string, "Back", StringComparison.OrdinalIgnoreCase);
        (string back, string fore) = tag switch
        {
            "done" => ("#EAF7EA", "#1B1B1F"),
            "warn" => ("#FFF8E1", "#9A6700"),
            "running" => ("#FFF7E0", "#1B1B1F"),
            "failed" => ("#FDF3F2", "#C42B1C"),
            "skipped" => ("#FAFAFA", "#616161"),
            _ => ("#FFFFFF", "#1B1B1F"),
        };
        return HexToBrushConverter.FromHex(background ? back : fore);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>把 #RRGGBB 字符串转成 SolidColorBrush（用于 ffmpeg 状态胶囊圆点）。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => FromHex(value as string ?? "#8A8A8A");

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    public static SolidColorBrush FromHex(string hex)
    {
        string s = hex.TrimStart('#');
        if (s.Length != 6) return new SolidColorBrush(Color.FromArgb(255, 138, 138, 138));
        byte r = System.Convert.ToByte(s[..2], 16);
        byte g = System.Convert.ToByte(s[2..4], 16);
        byte b = System.Convert.ToByte(s[4..6], 16);
        return new SolidColorBrush(Color.FromArgb(255, r, g, b));
    }
}

/// <summary>
/// 完整路径 → 文件名。字幕候选列表里直接绑路径会把列表撑得很宽，
/// 只显示文件名、完整路径放到工具提示里更好读。
/// </summary>
public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string path = value as string ?? "";
        if (path.Length == 0) return "";
        try { return System.IO.Path.GetFileName(path); }
        catch { return path; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
