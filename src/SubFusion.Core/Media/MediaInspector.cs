using System.Globalization;
using System.Text.Json;
using SubFusion.Core.Services;

namespace SubFusion.Core.Media;

/// <summary>
/// ffprobe 视频媒体信息探测：把原始 JSON 转换为 <see cref="MediaBundle"/>。
/// 与字幕侧使用的 <see cref="MediaProbe"/> 共用 <see cref="ProcessUtil"/> 子进程基础设施，
/// 但输出完整的流级参数（编码档位、帧率、SAR/DAR、色彩、HDR 等），供视频合并引擎判断兼容性。
/// </summary>
public static class MediaInspector
{
    public static async Task<MediaBundle> InspectAsync(string ffprobe, string path,
                                                       CancellationToken cancellationToken = default,
                                                       int timeoutSeconds = 60)
    {
        var bundle = new MediaBundle { Path = path };
        if (string.IsNullOrEmpty(ffprobe))
        {
            bundle.Error = "未找到 ffprobe";
            return bundle;
        }
        if (!File.Exists(path))
        {
            bundle.Error = "文件不存在";
            return bundle;
        }

        string[] args =
        {
            "-v", "error",
            "-print_format", "json",
            "-show_streams", "-show_format",
            path,
        };

        ProcessCapture proc;
        try
        {
            proc = await ProcessUtil.RunAsync(ffprobe, args, timeoutSeconds,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            bundle.Error = ex.Message;
            return bundle;
        }

        if (proc.TimedOut)
        {
            bundle.Error = "探测超时";
            return bundle;
        }
        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(proc.Stdout))
        {
            bundle.Error = LastError(proc.Stderr);
            return bundle;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(proc.Stdout);
            JsonElement root = doc.RootElement;
            ParseStreams(root, bundle);
            ParseFormat(root, bundle);
            bundle.Success = bundle.Videos.Count > 0 || bundle.Audios.Count > 0;
            if (!bundle.Success && bundle.Error.Length == 0) bundle.Error = "没有可识别的音视频流";
        }
        catch (Exception ex)
        {
            bundle.Success = false;
            bundle.Error = "解析 ffprobe 输出失败：" + ex.Message;
        }
        return bundle;
    }

    /// <summary>批量探测（有界并发）；返回与输入顺序一致的列表。</summary>
    public static async Task<List<MediaBundle>> InspectManyAsync(string ffprobe, IReadOnlyList<string> paths,
                                                                 CancellationToken cancellationToken = default,
                                                                 int workers = 4, int timeoutSeconds = 60)
    {
        var results = new MediaBundle?[paths.Count];
        int limit = Math.Clamp(workers, 1, 8);
        using var gate = new SemaphoreSlim(limit, limit);
        var tasks = new List<Task>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            int index = i;
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    results[index] = await InspectAsync(ffprobe, paths[index], cancellationToken, timeoutSeconds)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    results[index] = new MediaBundle { Path = paths[index], Error = "已取消" };
                }
                finally
                {
                    gate.Release();
                }
            }, CancellationToken.None));
        }
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* 已取消：保留已完成的结果 */ }

        var list = new List<MediaBundle>(paths.Count);
        for (int i = 0; i < results.Length; i++)
            list.Add(results[i] ?? new MediaBundle { Path = paths[i], Error = "已取消" });
        return list;
    }

    private static void ParseStreams(JsonElement root, MediaBundle bundle)
    {
        if (!root.TryGetProperty("streams", out JsonElement streams) ||
            streams.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement stream in streams.EnumerateArray())
        {
            string type = Str(stream, "codec_type");
            if (type == "video" && !IsCoverArt(stream))
            {
                bundle.Videos.Add(ParseVideo(stream));
            }
            else if (type == "audio")
            {
                bundle.Audios.Add(ParseAudio(stream));
            }
            else if (type == "subtitle")
            {
                bundle.SubtitleStreamCount++;
            }
        }
    }

    /// <summary>封面图（attached_pic）不是视频流，不能参与合并与拼接判断。</summary>
    private static bool IsCoverArt(JsonElement stream)
    {
        if (!stream.TryGetProperty("disposition", out JsonElement disp) ||
            disp.ValueKind != JsonValueKind.Object)
            return false;
        return disp.TryGetProperty("attached_pic", out JsonElement pic) &&
               pic.ValueKind == JsonValueKind.Number && pic.GetInt32() != 0;
    }

    private static VideoStreamInfo ParseVideo(JsonElement stream)
    {
        var info = new VideoStreamInfo
        {
            Index = Int(stream, "index"),
            Codec = Str(stream, "codec_name"),
            Profile = Str(stream, "profile"),
            Level = Int(stream, "level"),
            Width = Int(stream, "width"),
            Height = Int(stream, "height"),
            Sar = Str(stream, "sample_aspect_ratio"),
            Dar = Str(stream, "display_aspect_ratio"),
            PixFmt = Str(stream, "pix_fmt"),
            AvgFrameRate = Rational(Str(stream, "avg_frame_rate")),
            RealFrameRate = Rational(Str(stream, "r_frame_rate")),
            TimeBase = Str(stream, "time_base"),
            ColorSpace = Str(stream, "color_space"),
            ColorRange = Str(stream, "color_range"),
            ColorTransfer = Str(stream, "color_transfer"),
            ColorPrimaries = Str(stream, "color_primaries"),
            Duration = Num(stream, "duration"),
            StartTime = Num(stream, "start_time"),
            BitRate = Num(stream, "bit_rate"),
        };

        // 帧数：优先 nb_frames，缺失时用 duration × 帧率估算
        string frames = Str(stream, "nb_frames");
        if (long.TryParse(frames, NumberStyles.Integer, CultureInfo.InvariantCulture, out long nf) && nf > 0)
            info.FrameCount = nf;

        info.HdrFormat = DetectHdr(stream, info);

        if (info.Duration <= 0 && info.FrameCount is > 0 && info.EffectiveFrameRate > 0)
            info.Duration = info.FrameCount.Value / info.EffectiveFrameRate;
        return info;
    }

    /// <summary>
    /// HDR 判定：传输特性（PQ / HLG）、杜比视界侧数据、HDR10+ 动态元数据，
    /// 以及主控显示元数据。仅凭色彩标记不改变画面，不能用它「伪装」色调映射。
    /// </summary>
    private static string DetectHdr(JsonElement stream, VideoStreamInfo info)
    {
        string transfer = info.ColorTransfer.ToLowerInvariant();
        if (transfer == "smpte2084") return "PQ(HDR10)";
        if (transfer == "arib-std-b67") return "HLG";

        if (stream.TryGetProperty("side_data_list", out JsonElement sides) &&
            sides.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement side in sides.EnumerateArray())
            {
                string type = Str(side, "side_data_type").ToLowerInvariant();
                if (type.Contains("dovi", StringComparison.Ordinal) ||
                    type.Contains("dolby vision", StringComparison.Ordinal))
                    return "DOVI";
                if (type.Contains("hdr10+", StringComparison.Ordinal) ||
                    type.Contains("dynamic hdr", StringComparison.Ordinal))
                    return "HDR10+";
                if (type.Contains("mastering display", StringComparison.Ordinal) ||
                    type.Contains("content light level", StringComparison.Ordinal))
                    return "HDR(元数据)";
            }
        }
        return "";
    }

    private static AudioStreamInfo ParseAudio(JsonElement stream) => new()
    {
        Index = Int(stream, "index"),
        Codec = Str(stream, "codec_name"),
        Profile = Str(stream, "profile"),
        SampleRate = Int(stream, "sample_rate"),
        Channels = Int(stream, "channels"),
        ChannelLayout = Str(stream, "channel_layout"),
        Duration = Num(stream, "duration"),
        StartTime = Num(stream, "start_time"),
        BitRate = Num(stream, "bit_rate"),
    };

    private static void ParseFormat(JsonElement root, MediaBundle bundle)
    {
        if (!root.TryGetProperty("format", out JsonElement fmt) || fmt.ValueKind != JsonValueKind.Object)
            return;
        bundle.Container.FormatName = Str(fmt, "format_name");
        bundle.Container.FormatLongName = Str(fmt, "format_long_name");
        bundle.Container.Duration = Num(fmt, "duration");
        bundle.Container.StartTime = Num(fmt, "start_time");
        bundle.Container.BitRate = Num(fmt, "bit_rate");
        bundle.Container.ProbeScore = Int(fmt, "probe_score");
        string size = Str(fmt, "size");
        if (long.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes))
            bundle.Container.Size = bytes;
        if (bundle.Container.Size <= 0)
        {
            try { bundle.Container.Size = new FileInfo(bundle.Path).Length; }
            catch { bundle.Container.Size = 0; }
        }
    }

    private static string LastError(string stderr)
    {
        string[] lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length > 0) return line.Length > 200 ? line[..200] : line;
        }
        return "ffprobe 无法读取该文件";
    }

    private static string Str(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) ? value.ToString() : "";

    private static int Int(JsonElement element, string name)
        => int.TryParse(Str(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    private static double Num(JsonElement element, string name)
        => double.TryParse(Str(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0;

    /// <summary>解析 ffprobe 的 "30000/1001" 形式有理数；非法值返回 0。</summary>
    public static double Rational(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0.0;
        int slash = text.IndexOf('/');
        if (slash < 0)
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0.0;
        string num = text[..slash];
        string den = text[(slash + 1)..];
        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return 0.0;
        if (!double.TryParse(den, NumberStyles.Float, CultureInfo.InvariantCulture, out double m)) return 0.0;
        return m == 0 ? 0.0 : n / m;
    }
}
