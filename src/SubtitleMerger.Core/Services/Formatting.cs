using System.Globalization;
using SubtitleMerger.Core.Models;

namespace SubtitleMerger.Core.Services;

/// <summary>
/// 原 core.format_duration / format_size 以及全程序共用的数值格式化。
/// Python 的 "%d" / "%.2f" 使用句点作小数点且按 banker's rounding，
/// 这里统一用 InvariantCulture，避免在中文/欧洲区域设置下输出逗号造成不等价。
/// </summary>
public static class Formatting
{
    public static string F0(double v) => v.ToString("F0", CultureInfo.InvariantCulture);
    public static string F1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
    public static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    public static string F3(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

    public static string FormatDuration(double? seconds)
    {
        if (seconds is null or <= 0)
            return "--:--";
        double s = seconds.Value;
        int hours = (int)Math.Floor(s / 3600.0);
        int minutes = (int)Math.Floor((s % 3600.0) / 60.0);
        int secs = (int)(s % 60.0);
        if (hours != 0)
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", hours, minutes, secs);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", minutes, secs);
    }

    public static string FormatSize(long? num)
    {
        if (num is null || num.Value == 0)
            return "-";
        double value = num.Value;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        foreach (string unit in units)
        {
            if (value < 1024 || unit == "TB")
            {
                if (unit == "B")
                    return string.Format(CultureInfo.InvariantCulture, "{0} B", (long)value);
                return string.Format(CultureInfo.InvariantCulture, "{0} {1}", F2(value), unit);
            }
            value /= 1024.0;
        }
        return "-";
    }

    /// <summary>原 App._plan_text。</summary>
    public static string PlanText(OutputPlan? plan)
    {
        if (plan is null) return "";
        if (plan.Mode == "hard")
            return string.Format(CultureInfo.InvariantCulture, "烧录 {0} 条字幕，无字幕轨", plan.BurnedSubs);
        return string.Format(CultureInfo.InvariantCulture, "{0} 条字幕轨", plan.ExpectedSubs);
    }

    /// <summary>原 _run_job 里的命令回显：含空格的参数加双引号，超长截断到 460 字符。</summary>
    public static string CommandText(IReadOnlyList<string> cmd)
    {
        string text = string.Join(" ", cmd.Select(p => p.Contains(' ') ? $"\"{p}\"" : p));
        if (text.Length > 460)
            text = text[..460] + " …（已截断）";
        return text;
    }

    public static string Truncate(string? text, int length)
        => string.IsNullOrEmpty(text) ? "" : (text.Length <= length ? text : text[..length]);

    public static double UnixNow()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return now.ToUnixTimeMilliseconds() / 1000.0;
    }
}
