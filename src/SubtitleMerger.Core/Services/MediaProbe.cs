using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using SubtitleMerger.Core.Models;

namespace SubtitleMerger.Core.Services;

/// <summary>
/// ffprobe 探测（原 core.probe_streams / probe_subtitle / probe_many）。
/// 所有异常都按原实现吞掉并返回默认值 / 「按可用处理」，避免一个坏文件拖垮整批任务。
/// </summary>
public static class MediaProbe
{
    public static async Task<MediaInfo> ProbeStreamsAsync(string? ffprobe, string path,
                                                           int timeout = 30,
                                                           CancellationToken cancellationToken = default)
    {
        var info = new MediaInfo();
        if (string.IsNullOrEmpty(ffprobe)) return info;

        string[] args =
        {
            "-v", "error",
            "-show_entries", "stream=index,codec_type,codec_name,width,height",
            "-show_entries", "format=duration,size",
            "-of", "json", path,
        };
        try
        {
            ProcessCapture proc = await ProcessUtil
                .RunAsync(ffprobe, args, timeout, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            string text = string.IsNullOrEmpty(proc.Stdout) ? "{}" : proc.Stdout;
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("streams", out JsonElement streams) &&
                streams.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement stream in streams.EnumerateArray())
                {
                    string kind = GetString(stream, "codec_type");
                    if (kind == "video")
                    {
                        info.VStreams++;
                        if (info.VStreams == 1)
                        {
                            info.Vcodec = GetString(stream, "codec_name");
                            info.Width = GetInt(stream, "width");
                            info.Height = GetInt(stream, "height");
                        }
                    }
                    else if (kind == "audio")
                    {
                        info.AStreams++;
                        if (info.AStreams == 1) info.Acodec = GetString(stream, "codec_name");
                    }
                    else if (kind == "subtitle")
                    {
                        info.SStreams++;
                    }
                }
            }

            try
            {
                if (root.TryGetProperty("format", out JsonElement fmt) &&
                    fmt.ValueKind == JsonValueKind.Object)
                {
                    string durationRaw = GetString(fmt, "duration");
                    if (!string.IsNullOrEmpty(durationRaw) && durationRaw != "N/A")
                        info.Duration = double.Parse(durationRaw, NumberStyles.Float, CultureInfo.InvariantCulture);
                    string sizeRaw = GetString(fmt, "size");
                    if (!string.IsNullOrEmpty(sizeRaw) && sizeRaw != "0")
                        info.Size = long.Parse(sizeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture);
                }
            }
            catch (FormatException) { /* Python: except (TypeError, ValueError): pass */ }
            catch (OverflowException) { /* 同上 */ }
            info.Probed = true;
        }
        catch
        {
            // Python: except Exception: return info
        }
        return info;
    }

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => value.GetRawText(),
        };
    }

    private static int GetInt(JsonElement element, string name)
    {
        string raw = GetString(element, name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
    }

    /// <summary>
    /// 检查字幕文件能否被 ffmpeg 识别（原 probe_subtitle）。
    /// 返回 (是否可用, 说明)；探测本身失败时按「可用」处理，避免误杀。
    /// </summary>
    public static async Task<(bool Ok, string Detail)> ProbeSubtitleAsync(string? ffprobe, string path,
                                                                           int timeout = 20)
    {
        if (string.IsNullOrEmpty(ffprobe)) return (true, "");
        if (!File.Exists(path)) return (false, "文件不存在");
        try
        {
            if (new FileInfo(path).Length == 0) return (false, "文件为空");
        }
        catch { return (false, "无法读取"); }

        string[] args =
        {
            "-v", "error", "-select_streams", "s",
            "-show_entries", "stream=index,codec_name", "-of", "csv=p=0", path,
        };
        ProcessCapture proc;
        try
        {
            proc = await ProcessUtil.RunAsync(ffprobe, args, timeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (true, $"校验失败（按可用处理）：{ex.Message}");
        }
        if (proc.TimedOut)
            return (true, "校验失败（按可用处理）：探测超时");

        string output = proc.Stdout.Trim();
        if (proc.ExitCode != 0 || string.IsNullOrEmpty(output))
        {
            string[] errLines = proc.Stderr.Trim()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r')).ToArray();
            string reason = errLines.Length > 0
                ? (errLines[^1].Length > 80 ? errLines[^1][..80] : errLines[^1])
                : "没有可识别的字幕流";
            return (false, reason);
        }
        return (true, output);
    }

    /// <summary>
    /// 并发探测多个文件（原 probe_many）；workers 夹在 1..8，取消时丢弃未开始的任务。
    /// onResult 在原版里由调用线程按完成顺序回调；这里在各工作线程里回调，
    /// UI 侧统一经 Dispatcher 投递，效果等价。
    /// </summary>
    public static async Task<Dictionary<string, MediaInfo>> ProbeManyAsync(
        string? ffprobe, IEnumerable<string> paths, int workers = Constants.ProbeWorkers,
        Action<string, MediaInfo>? onResult = null, CancellationToken cancellationToken = default)
    {
        var results = new ConcurrentDictionary<string, MediaInfo>(StringComparer.Ordinal);
        var pathList = paths.ToList();
        if (string.IsNullOrEmpty(ffprobe) || pathList.Count == 0)
            return new Dictionary<string, MediaInfo>(StringComparer.Ordinal);

        workers = Math.Clamp(workers, 1, 8);
        // 有界并发：旧写法一次性为每个文件创建 Task（每个都挂在 SemaphoreSlim 上等待，
        // 取消时还会全部抛 OperationCanceledException），几百上千个文件时任务/定时器数量
        // 与文件数成正比。Parallel.ForEachAsync 只保留 workers 个在途任务。
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = workers,
            CancellationToken = cancellationToken,
        };
        try
        {
            await Parallel.ForEachAsync(pathList, options, async (path, ct) =>
            {
                MediaInfo info;
                try
                {
                    info = await ProbeStreamsAsync(ffprobe, path, 30, ct).ConfigureAwait(false);
                }
                catch
                {
                    return;   // 单个文件失败只跳过
                }
                results[path] = info;
                try { onResult?.Invoke(path, info); } catch { /* 回调异常不影响探测 */ }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消（用户停止 / 关闭窗口）：已探测的结果照常返回
        }
        return results.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }
}
