using System.Globalization;

namespace SubFusion.Core.Chapters;

/// <summary>一个章节：Start/End 单位为秒，标题为用户可见文本。</summary>
public sealed class ChapterEntry
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Title { get; set; } = "";

    public double Duration => End - Start;

    public string StartText => FormatTime(Start);
    public string EndText => FormatTime(End);

    public ChapterEntry() { }

    public ChapterEntry(double start, double end, string title)
    {
        Start = start; End = end; Title = title;
    }

    /// <summary>HH:MM:SS.mmm（毫秒精度，与 ffmetadata 的 1/1000 时间基准一致）。</summary>
    public static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        long totalMs = (long)Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero);
        long h = totalMs / 3_600_000;
        long m = totalMs % 3_600_000 / 60_000;
        long s = totalMs % 60_000 / 1000;
        long ms = totalMs % 1000;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}.{3:000}", h, m, s, ms);
    }

    public static long ToMilliseconds(double seconds)
        => (long)Math.Round(Math.Max(0, seconds) * 1000.0, MidpointRounding.AwayFromZero);

    public ChapterEntry Clone() => new(Start, End, Title);
}
