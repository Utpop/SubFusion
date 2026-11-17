using System.Globalization;
using SubFusion.Core.Models;

namespace SubFusion.Core.Services;

/// <summary>原 core.check_integrity / verify_output：输出文件流结构与完整性校验。</summary>
public static class OutputVerifier
{
    private static readonly string[] IntegrityBadKeywords =
        { "prematurely", "invalid data", "truncat", "corrupt", "error while decoding" };
    private static readonly string[] IntegrityReasonKeywords =
        { "prematurely", "invalid", "truncat", "corrupt", "error" };

    /// <summary>快速完整性检查：解码开头 1 秒与结尾 2 秒，能发现「文件被截断」。</summary>
    public static async Task<List<string>> CheckIntegrityAsync(string? ffmpeg, string path, int timeout = 60)
    {
        var issues = new List<string>();
        if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(path)) return issues;

        var probes = new (string[] Extra, string Label)[]
        {
            (new[] { "-t", "1" }, "开头"),
            (new[] { "-sseof", "-2", "-t", "2" }, "结尾"),
        };

        foreach (var (extra, label) in probes)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
            args.AddRange(extra);
            args.AddRange(new[] { "-i", path, "-f", "null", "-" });

            ProcessCapture proc;
            try
            {
                proc = await ProcessUtil.RunAsync(ffmpeg, args, timeout).ConfigureAwait(false);
            }
            catch { continue; }        // 超时/无法启动时不误报
            if (proc.TimedOut) continue;

            string errText = proc.Stderr.Trim();
            bool bad = proc.ExitCode != 0;
            if (!bad && !string.IsNullOrEmpty(errText))
            {
                string lowered = errText.ToLowerInvariant();
                bad = IntegrityBadKeywords.Any(k => lowered.Contains(k, StringComparison.Ordinal));
            }
            if (!bad) continue;

            string[] errLines = errText.Split('\n')
                .Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
            string reason = "";
            foreach (string line in errLines)
            {
                string low = line.ToLowerInvariant();
                if (IntegrityReasonKeywords.Any(k => low.Contains(k, StringComparison.Ordinal)))
                {
                    reason = line.Length > 70 ? line[..70] : line;
                    break;
                }
            }
            if (string.IsNullOrEmpty(reason))
                reason = errLines.Length > 0
                    ? (errLines[^1].Length > 70 ? errLines[^1][..70] : errLines[^1])
                    : "读取失败";
            issues.Add($"输出文件{label}数据不完整或无法解码（{reason}）");
        }
        return issues;
    }

    /// <summary>
    /// 校验输出是否符合 plan 预期。ffprobe 不可用时返回通过，避免误报。
    /// deep=true 时额外做开头/结尾的快速解码检查。
    /// </summary>
    public static async Task<VerifyResult> VerifyOutputAsync(string? ffprobe, string output,
                                                              OutputPlan plan,
                                                              MediaInfo? sourceInfo = null,
                                                              string? ffmpeg = null,
                                                              bool deep = true, int timeout = 30)
    {
        var result = new VerifyResult();
        var issues = result.Issues;
        if (string.IsNullOrEmpty(ffprobe)) return new VerifyResult { Ok = true };

        if (!File.Exists(output))
        {
            issues.Add("输出文件不存在");
            result.Ok = false;
            return result;
        }

        long size;
        try { size = new FileInfo(output).Length; }
        catch { size = 0; }
        result.Detail.Size = size;
        if (size <= 0)
        {
            issues.Add("输出文件为空（0 字节）");
            result.Ok = false;
            return result;
        }

        MediaInfo info = await MediaProbe.ProbeStreamsAsync(ffprobe, output, timeout).ConfigureAwait(false);
        result.Detail = info;
        result.Detail.Size = size;

        if (info.VStreams == 0 && plan.ExpectedVideo != 0)
            issues.Add("输出中没有视频流");

        if (info.SStreams != plan.ExpectedSubs)
            issues.Add(string.Format(CultureInfo.InvariantCulture,
                "字幕轨数量不符（期望 {0} 条，实际 {1} 条）", plan.ExpectedSubs, info.SStreams));

        if (plan.ExpectedAudio != 0 && info.AStreams == 0)
            issues.Add(string.Format(CultureInfo.InvariantCulture,
                "源文件有 {0} 条音轨，输出中没有音轨", plan.ExpectedAudio));

        string? wanted = plan.Vcodec switch
        {
            "libx264" => "h264",
            "libx265" => "hevc",
            "h264_nvenc" => "h264",
            "hevc_nvenc" => "hevc",
            _ => null,
        };
        if (!string.IsNullOrEmpty(plan.Vcodec) && plan.Vcodec != "copy" &&
            !string.IsNullOrEmpty(info.Vcodec) && wanted is not null && info.Vcodec != wanted)
            issues.Add($"视频编码为 {info.Vcodec}，预期 {wanted}");

        if (sourceInfo is not null)
        {
            double? srcDur = sourceInfo.Duration;
            double? outDur = info.Duration;
            if (srcDur.HasValue && outDur.HasValue)
            {
                double tolerance = Math.Max(2.0, srcDur.Value * 0.05);
                if (Math.Abs(outDur.Value - srcDur.Value) > tolerance)
                    issues.Add(string.Format(CultureInfo.InvariantCulture,
                        "时长偏差较大（源 {0:F1} 秒，输出 {1:F1} 秒）", srcDur.Value, outDur.Value));
            }

            // 纯封装（不重编码）时体积不该明显缩水；硬字幕会强制重编码视频，体积缩水属正常
            if (plan.Mode != "hard" && plan.Vcodec == "copy" && plan.Acodec == "copy")
            {
                long srcSize = sourceInfo.Size;
                if (srcSize > 0 && size < srcSize * 0.5)
                    issues.Add($"输出体积（{Formatting.FormatSize(size)}）明显小于源文件" +
                               $"（{Formatting.FormatSize(srcSize)}），可能数据不完整");
            }
        }

        if (deep && !string.IsNullOrEmpty(ffmpeg))
            issues.AddRange(await CheckIntegrityAsync(ffmpeg, output).ConfigureAwait(false));

        result.Ok = issues.Count == 0;
        return result;
    }
}
