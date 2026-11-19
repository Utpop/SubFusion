using System.Globalization;

namespace SubFusion.Core.Trim;

/// <summary>时间轴上的命中部位。</summary>
public enum TimelineHit
{
    /// <summary>没命中任何可拖拽的部分。</summary>
    None = 0,

    /// <summary>起点手柄。</summary>
    Start,

    /// <summary>终点手柄。</summary>
    End,

    /// <summary>选中区间内部：整体平移。</summary>
    Range,
}

/// <summary>
/// 时间轴拖动选择条的几何换算。
///
/// 放在 Core 而不是控件 code-behind，是因为这里全是可以算错的纯数学：
/// 像素↔秒、区间夹紧、最小长度、手柄命中、关键帧吸附。
/// 放到 UI 里就只能靠肉眼在窗口上试，而这类错误通常表现为
/// 「拖到最右端却停在 99%」或「两个手柄能交叉」——都是用户一眼就看见的问题。
/// </summary>
public static class TimelineGeometry
{
    /// <summary>允许的最短裁剪区间（秒）。再短没有意义，也会让 ffmpeg 直接失败。</summary>
    public const double MinDurationSeconds = 0.1;

    /// <summary>手柄的命中半径（像素）。比手柄本身大一圈，手指/鼠标更容易抓住。</summary>
    public const double DefaultGrabRadius = 14.0;

    /// <summary>把秒数夹到 [0, duration]；时长非法时返回 0。</summary>
    public static double ClampSeconds(double seconds, double duration)
    {
        if (duration <= 0 || double.IsNaN(seconds)) return 0;
        if (double.IsNaN(duration)) return 0;
        if (seconds < 0) return 0;
        return seconds > duration ? duration : seconds;
    }

    /// <summary>像素 → 秒。</summary>
    public static double SecondsFromX(double x, double width, double duration)
    {
        if (width <= 0 || duration <= 0) return 0;
        return ClampSeconds(x / width * duration, duration);
    }

    /// <summary>秒 → 像素。</summary>
    public static double XFromSeconds(double seconds, double width, double duration)
    {
        if (width <= 0 || duration <= 0) return 0;
        return ClampSeconds(seconds, duration) / duration * width;
    }

    /// <summary>
    /// 判断按下点落在哪个部位。
    /// 优先命中手柄（它更小、更常用），其次才是区间内部平移；
    /// 都不命中时，落在区间左侧就抓起点、右侧就抓终点——这样「点一下轨道」也能开始拖。
    /// </summary>
    public static TimelineHit PickHandle(double x, double startX, double endX, double grabRadius = DefaultGrabRadius)
    {
        if (endX < startX) (startX, endX) = (endX, startX);
        double radius = Math.Max(1, grabRadius);

        bool nearStart = Math.Abs(x - startX) <= radius;
        bool nearEnd = Math.Abs(x - endX) <= radius;

        // 两个手柄贴在一起时（区间很短），按下点可能同时命中两个。
        // 此时选更近的那个，否则会永远只能拖起点、拖不动终点。
        if (nearStart && nearEnd) return Math.Abs(x - startX) <= Math.Abs(x - endX) ? TimelineHit.Start : TimelineHit.End;
        if (nearStart) return TimelineHit.Start;
        if (nearEnd) return TimelineHit.End;

        if (x > startX && x < endX) return TimelineHit.Range;
        return x <= startX ? TimelineHit.Start : TimelineHit.End;
    }

    /// <summary>
    /// 移动起点。会夹在 [0, end - 最短长度] 内——两个手柄永远不能交叉，
    /// 也不需要调用方再补一次判断。
    /// </summary>
    public static (double Start, double End) MoveStart(double requestedStart, double end, double duration)
    {
        double limit = Math.Max(0, end - MinDurationSeconds);
        double start = Math.Min(ClampSeconds(requestedStart, duration), limit);
        return (start, end);
    }

    /// <summary>移动终点，夹在 [start + 最短长度, duration] 内。</summary>
    public static (double Start, double End) MoveEnd(double requestedEnd, double start, double duration)
    {
        double floor = Math.Min(duration, start + MinDurationSeconds);
        double end = Math.Max(ClampSeconds(requestedEnd, duration), floor);
        return (start, end);
    }

    /// <summary>
    /// 整体平移区间。长度保持不变；撞到两端时停下，而不是把区间压缩。
    /// 压缩会悄悄改掉用户选好的时长，那是最难察觉的一种错。
    /// </summary>
    public static (double Start, double End) MoveRange(double deltaSeconds, double start, double end, double duration)
    {
        double length = end - start;
        if (duration <= 0 || length <= 0) return (start, end);

        double nextStart = start + deltaSeconds;
        if (nextStart < 0) nextStart = 0;
        if (nextStart + length > duration) nextStart = Math.Max(0, duration - length);
        return (nextStart, nextStart + length);
    }

    /// <summary>
    /// 把时间点吸附到最近的关键帧（仅在容差内）。
    ///
    /// 为什么拖起点时要吸附：无损切割的切点最终一定落在关键帧上，
    /// 提前吸附能让「拖动看到的位置」与「实际切出来的位置」一致，
    /// 避免用户拖到 5.3s、输出却从 4.0s 开始这种惊吓。
    /// </summary>
    /// <summary>无损模式专用：向前吸附到不晚于 seconds 的最后一个关键帧，与 KeyframeIndex.SnapToKeyframe 一致。</summary>
    public static double SnapToPreviousKeyframe(IReadOnlyList<double> times, double seconds, double toleranceSeconds)
    {
        if (times is null || times.Count == 0 || toleranceSeconds <= 0) return seconds;
        double best = seconds;
        foreach (double t in times)
        {
            if (t > seconds + 1e-6) break;
            if (seconds - t <= toleranceSeconds) best = t;
        }
        return best;
    }

    public static double SnapToNearestKeyframe(IReadOnlyList<double> times, double seconds, double toleranceSeconds)
    {
        if (times is null || times.Count == 0 || toleranceSeconds <= 0) return seconds;

        double best = seconds;
        double bestDistance = toleranceSeconds;
        foreach (double t in times)
        {
            double distance = Math.Abs(t - seconds);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = t;
            }
        }
        return best;
    }

    /// <summary>把秒数写成人看的刻度文字（mm:ss.t，超过一小时用 h:mm:ss）。</summary>
    public static string ShortLabel(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)span.TotalHours, span.Minutes, span.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}.{2}", span.Minutes, span.Seconds, span.Milliseconds / 100);
    }
}
