using System.Globalization;

namespace SubFusion.Core.Chapters;

/// <summary>
/// 章节时间轴计算。
///
/// 不能简单累加「未经验证的容器时长」：输入可能有不同的流起始时间、时间戳偏移、
/// 帧率与时间基准；重新编码还会引入时间轴变化。因此：
/// <list type="number">
/// <item>有探测到的片段时长时以它为基础累加；</item>
/// <item>合并后用最终输出的实际时长做归一化，消除累加漂移；</item>
/// <item>对齐到毫秒，并强制严格递增、不重叠、不越界。</item>
/// </list>
/// </summary>
public static class ChapterTimeline
{
    /// <summary>允许的时长漂移（秒）。超过该值才做整体归一化，避免无谓改动。</summary>
    public const double DriftTolerance = 0.005;

    /// <summary>
    /// 生成章节时间轴。
    /// </summary>
    /// <param name="segmentDurations">按合并顺序的片段时长（秒）。</param>
    /// <param name="titles">章节标题；不足时用「第 N 段」补齐。</param>
    /// <param name="actualDuration">最终输出的实际时长；&lt;= 0 时按累加结果收尾。</param>
    public static List<ChapterEntry> Build(IReadOnlyList<double> segmentDurations,
                                           IReadOnlyList<string>? titles,
                                           double actualDuration)
    {
        var chapters = new List<ChapterEntry>();
        if (segmentDurations.Count == 0) return chapters;

        double sum = 0;
        foreach (double d in segmentDurations)
            sum += Math.Max(0, d);

        // 归一化系数：输出实际时长与累加值不一致时按比例校正，消除时间戳漂移。
        double scale = 1.0;
        if (actualDuration > 0 && sum > 0 && Math.Abs(actualDuration - sum) > DriftTolerance)
            scale = actualDuration / sum;

        long cursorMs = 0;
        for (int i = 0; i < segmentDurations.Count; i++)
        {
            long startMs = cursorMs;
            long lengthMs = (long)Math.Round(Math.Max(0, segmentDurations[i]) * scale * 1000.0,
                                             MidpointRounding.AwayFromZero);
            // 时长探测为 0 的输入（TS / 裸流 / 缺时长元数据的 MKV）会算出 0 长度。
            // 不能在这里无脑抬成 1 毫秒：那会写出一批「1 毫秒章节」，
            // 用户看到的是 N 个挤在时间轴起点、毫无意义的条目。
            // 真正的处理是「这段时长未知」，由引擎在调用前拒绝这种输入（见 VideoMergeEngine）。
            if (lengthMs <= 0 && segmentDurations[i] > 0) lengthMs = 1;
            long endMs = startMs + lengthMs;
            string title = titles is not null && i < titles.Count && !string.IsNullOrWhiteSpace(titles[i])
                ? titles[i].Trim()
                : "第 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 段";

            chapters.Add(new ChapterEntry(startMs / 1000.0, endMs / 1000.0, title));
            cursorMs = endMs;
        }

        // 最后一章收到最终实际时长；若实际时长明显短于累加值，按实际时长截断。
        if (actualDuration > 0)
        {
            long totalMs = (long)Math.Round(actualDuration * 1000.0, MidpointRounding.AwayFromZero);
            if (totalMs > 0)
            {
                ChapterEntry last = chapters[^1];
                if (ChapterEntry.ToMilliseconds(last.End) > totalMs)
                    last.End = Math.Max(ChapterEntry.ToMilliseconds(last.Start) + 1, totalMs) / 1000.0;
            }
        }

        // 收尾后重新校验：残留的零长/越界章节必须修正，否则写入后一定验证失败。
        if (Validate(chapters, actualDuration).Count > 0 && chapters.Count > 0)
        {
            ChapterEntry last = chapters[^1];
            long startMs = ChapterEntry.ToMilliseconds(last.Start);
            long limitMs = actualDuration > 0
                ? (long)Math.Round(actualDuration * 1000.0, MidpointRounding.AwayFromZero)
                : startMs + 1;
            last.End = Math.Max(startMs + 1, limitMs) / 1000.0;
        }
        return chapters;
    }

    /// <summary>章节时间轴合法性校验；返回空列表表示通过。</summary>
    public static List<string> Validate(IReadOnlyList<ChapterEntry> chapters, double actualDuration)
    {
        var issues = new List<string>();
        if (chapters.Count == 0)
        {
            issues.Add("章节为空");
            return issues;
        }
        for (int i = 0; i < chapters.Count; i++)
        {
            ChapterEntry c = chapters[i];
            string name = string.Format(CultureInfo.InvariantCulture, "章节 {0}", i + 1);
            if (c.Start < 0 || c.End < 0) issues.Add(name + "：存在负数时间点");
            if (c.End <= c.Start) issues.Add(name + "：结束时间不晚于开始时间");
            if (ChapterEntry.ToMilliseconds(c.Start) == ChapterEntry.ToMilliseconds(c.End))
                issues.Add(name + "：长度不足 1 毫秒");
            if (string.IsNullOrWhiteSpace(c.Title)) issues.Add(name + "：标题为空");
            if (i > 0)
            {
                ChapterEntry prev = chapters[i - 1];
                if (ChapterEntry.ToMilliseconds(c.Start) < ChapterEntry.ToMilliseconds(prev.End))
                    issues.Add(name + "：与上一章重叠");
            }
            if (actualDuration > 0 &&
                ChapterEntry.ToMilliseconds(c.End) > ChapterEntry.ToMilliseconds(actualDuration) + 1)
                issues.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0}：结束时间 {1} 超出视频时长 {2}",
                    name, c.EndText, ChapterEntry.FormatTime(actualDuration)));
        }
        return issues;
    }
}
