using System.Globalization;
using System.Text;
using SubFusion.Core.Services;

namespace SubFusion.Core.Chapters;

/// <summary>
/// ffmetadata 生成与「写入章节但不重编码视频」的实现。
///
/// 采用 ffmetadata + «-map_chapters 1» 方案：只重写容器层，码流全程 copy，
/// 不会因为加章节再压一遍画面。写入前先剥离已有章节目录（concat 拼接会把
/// 输入的章节带进输出，直接叠加会出现重复章节）。
/// </summary>
public static class ChapterWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>生成 ffmetadata 文本（FFMETADATA1 + 每章 TIMEBASE=1/1000，毫秒精度）。</summary>
    public static string BuildMetadata(IReadOnlyList<ChapterEntry> chapters)
    {
        var sb = new StringBuilder();
        sb.Append(";FFMETADATA1\n");
        foreach (ChapterEntry chapter in chapters)
        {
            sb.Append("[CHAPTER]\n");
            sb.Append("TIMEBASE=1/1000\n");
            sb.Append("START=").Append(ChapterEntry.ToMilliseconds(chapter.Start)
                .ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("END=").Append(ChapterEntry.ToMilliseconds(chapter.End)
                .ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("title=").Append(Escape(chapter.Title)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// ffmetadata 转义：反斜杠、等号、分号、井号与换行必须转义，
    /// 否则中文标题里的标点会被截断或串到下一行。
    /// </summary>
    public static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // 先把 CRLF 统一成 LF：原实现直接丢弃 \r，含换行的标题会被改写，
        // 回读后与期望不符，导致每次都误判为时间轴漂移而重写整文件。
        value = value.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder(value.Length + 8);
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;   // \ -> \\
                case '=': sb.Append("\\="); break;          // = -> \=
                case ';': sb.Append("\\;"); break;          // ; -> \;
                case '#': sb.Append("\\#"); break;          // # -> \#
                case '\n': sb.Append("\\n"); break;        // 换行 -> \n
                case '\r': break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 把章节写入输出文件（就地替换）。先剥离已有章节，再挂新章节目录，
    /// 两步都是 «-c copy»，不触发视频重新编码。
    /// </summary>
    public static async Task<(bool Ok, string Error)> ApplyAsync(
        string ffmpeg, string target, IReadOnlyList<ChapterEntry> chapters,
        string workDirectory, CancellationToken cancellationToken = default)
    {
        if (chapters.Count == 0) return (true, "");
        if (string.IsNullOrEmpty(ffmpeg)) return (false, "未找到 ffmpeg，无法写入章节");

        string ext = Path.GetExtension(target).TrimStart('.').ToLowerInvariant();
        string format = ext is "mp4" or "m4v" ? "mp4" : "matroska";

        string metaPath = Path.Combine(workDirectory, "chapters.ffmeta");
        string stagedPath = Path.Combine(workDirectory, "chaptered." + (ext.Length == 0 ? "mkv" : ext));
        try
        {
            Directory.CreateDirectory(workDirectory);
            await File.WriteAllTextAsync(metaPath, BuildMetadata(chapters), Utf8NoBom, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (false, "写入章节元数据文件失败：" + ex.Message);
        }

        // 1) 先剥离已有章节目录：拼接阶段可能已把输入的章节带进输出。
        string cleanedPath = Path.Combine(workDirectory, "nochapters." + (ext.Length == 0 ? "mkv" : ext));
        var strip = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", target,
            "-map", "0", "-c", "copy", "-map_chapters", "-1", "-f", format, cleanedPath,
        };
        ProcessCapture stripResult = await ProcessUtil.RunAsync(ffmpeg, strip, 1800,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (stripResult.TimedOut) return (false, "剥离旧章节超时");
        if (stripResult.ExitCode != 0 || !File.Exists(cleanedPath))
            return (false, "剥离旧章节失败：" + Tail(stripResult.Stderr, 240));

        // 2) 挂上新的章节目录（仍为 copy）。
        var add = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", cleanedPath,
            "-f", "ffmetadata", "-i", metaPath,
            "-map", "0", "-map_metadata", "0", "-map_chapters", "1",
            "-c", "copy", "-f", format, stagedPath,
        };
        ProcessCapture addResult = await ProcessUtil.RunAsync(ffmpeg, add, 1800,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (addResult.TimedOut) return (false, "写入章节超时");
        if (addResult.ExitCode != 0 || !File.Exists(stagedPath))
            return (false, "写入章节失败：" + Tail(addResult.Stderr, 240));

        try
        {
            File.Move(stagedPath, target, overwrite: true);
            try { File.Delete(cleanedPath); } catch { /* 临时文件清理失败不影响结果 */ }
        }
        catch (Exception ex)
        {
            return (false, "替换章节输出失败：" + ex.Message);
        }
        return (true, "");
    }

    /// <summary>回读输出文件的章节（ffprobe -show_chapters），用于验证数量、标题与时间轴。</summary>
    public static async Task<List<ChapterEntry>> ReadAsync(string ffprobe, string path,
                                                            CancellationToken cancellationToken = default)
    {
        var chapters = new List<ChapterEntry>();
        if (string.IsNullOrEmpty(ffprobe) || !File.Exists(path)) return chapters;

        string[] args = { "-v", "error", "-print_format", "json", "-show_chapters", path };
        ProcessCapture proc;
        try
        {
            proc = await ProcessUtil.RunAsync(ffprobe, args, 60,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return chapters;
        }
        if (proc.TimedOut) return chapters;
        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(proc.Stdout)) return chapters;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(proc.Stdout);
            if (!doc.RootElement.TryGetProperty("chapters", out System.Text.Json.JsonElement arr) ||
                arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                return chapters;
            foreach (System.Text.Json.JsonElement ch in arr.EnumerateArray())
            {
                double start = GetSeconds(ch, "start_time");
                double end = GetSeconds(ch, "end_time");
                string title = "";
                if (ch.TryGetProperty("tags", out System.Text.Json.JsonElement tags) &&
                    tags.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (System.Text.Json.JsonProperty p in tags.EnumerateObject())
                    {
                        if (string.Equals(p.Name, "title", StringComparison.OrdinalIgnoreCase))
                        {
                            title = p.Value.ToString();
                            break;
                        }
                    }
                }
                chapters.Add(new ChapterEntry(start, end, title));
            }
        }
        catch
        {
            // 解析失败按「没有章节」处理，由调用方报验证失败
        }
        return chapters;
    }

    /// <summary>把回读到的章节与期望章节比对，返回问题列表。</summary>
    public static List<string> Compare(IReadOnlyList<ChapterEntry> expected, IReadOnlyList<ChapterEntry> actual,
                                       double toleranceSeconds = 0.05)
    {
        var issues = new List<string>();
        if (actual.Count != expected.Count)
        {
            issues.Add(string.Format(CultureInfo.InvariantCulture,
                "章节数量不符（期望 {0}，实际 {1}）", expected.Count, actual.Count));
            return issues;
        }
        for (int i = 0; i < expected.Count; i++)
        {
            ChapterEntry e = expected[i];
            ChapterEntry a = actual[i];
            if (!string.Equals(e.Title, a.Title, StringComparison.Ordinal))
                issues.Add(string.Format(CultureInfo.InvariantCulture,
                    "第 {0} 章标题不符（期望「{1}」，实际「{2}」）", i + 1, e.Title, a.Title));
            if (Math.Abs(e.Start - a.Start) > toleranceSeconds)
                issues.Add(string.Format(CultureInfo.InvariantCulture,
                    "第 {0} 章开始时间偏差过大（期望 {1:F3}s，实际 {2:F3}s）", i + 1, e.Start, a.Start));
            if (Math.Abs(e.End - a.End) > toleranceSeconds)
                issues.Add(string.Format(CultureInfo.InvariantCulture,
                    "第 {0} 章结束时间偏差过大（期望 {1:F3}s，实际 {2:F3}s）", i + 1, e.End, a.End));
        }
        return issues;
    }

    private static double GetSeconds(System.Text.Json.JsonElement element, string name)
        => element.TryGetProperty(name, out System.Text.Json.JsonElement v) &&
           double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d : 0.0;

    private static string Tail(string text, int max)
    {
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length <= max) return trimmed.Length == 0 ? "（无输出）" : trimmed;
        return trimmed[^max..];
    }
}
