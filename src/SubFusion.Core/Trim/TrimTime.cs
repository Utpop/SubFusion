using System.Globalization;

namespace SubFusion.Core.Trim;

/// <summary>
/// 时间点的解析与格式化。
///
/// 放在 Core 而不是 ViewModel 里，是因为这是纯粹的字符串↔秒换算，
/// 出错的表现（用户填 1:02:03 被当成 1.02 秒）会直接切错片子，
/// 必须有自检覆盖；而 ViewModel 依赖 WinUI，自检跑不起来。
/// </summary>
public static class TrimTime
{
    /// <summary>
    /// 解析用户输入的时间：纯秒数（12.5）、mm:ss(.fff) 或 hh:mm:ss(.fff)。
    /// 任何一段不是数字、或为负数、或段数超过 3，都判为非法。
    /// </summary>
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        string value = (text ?? "").Trim();
        if (value.Length == 0) return false;

        string[] parts = value.Split(':');
        if (parts.Length is < 1 or > 3) return false;

        double total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            // .NET 的 TryParse(NumberStyles.Float) 会接受 NaN/Infinity；NaN < 0 为 false，
            // 若放行会把 "NaN" 写进 -ss/-t 让 ffmpeg 失败，而不是给出"时间格式非法"。
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double piece) ||
                !double.IsFinite(piece) || piece < 0)
                return false;
            // 非末段是分/秒的进位单位，必须 <60：否则 "1:75" 会被静默解释成 135 秒——
            // 那不是用户输入的意思，而是一次实实在在切错。
            if (i < parts.Length - 1 && piece >= 60) return false;
            total = total * 60 + piece;
        }
        if (!double.IsFinite(total)) return false;
        seconds = total;
        return true;
    }

    /// <summary>秒 → hh:mm:ss.fff；负数按 0 处理。</summary>
    public static string Format(double seconds)
    {
        // NaN/∞ 会让 TimeSpan.FromSeconds 直接抛 ArgumentException，
        // 而上游的 bundle.Duration 在探测失败时可能是 NaN——格式化不该成为崩溃点。
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}.{3:000}",
            (int)span.TotalHours, span.Minutes, span.Seconds, span.Milliseconds);
    }
}
