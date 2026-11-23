using System.Globalization;
using SubFusion.Core.Media;
using SubFusion.Core.Models;

namespace SubFusion.Core.Services;

public sealed record BuildCommandResult(List<string> Command, List<string> Notes, OutputPlan Plan);

/// <summary>
/// ffmpeg 命令行构建（原 core.build_output_path / build_command / _build_hard /
/// _build_soft / _append_av_codec）。参数顺序与取值逐项一致。
/// </summary>
public static class CommandBuilder
{
    /// <summary>输出路径；绝不允许覆盖源文件（同名时自动加后缀，缺省用 "_sub"）。</summary>
    public static string BuildOutputPath(string video, Options options, string outputDir)
    {
        string fileName = Path.GetFileName(video);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        string newExt = options.Container == "same" ? ext.ToLowerInvariant() : "." + options.Container;
        string name = stem + (options.Suffix ?? "") + newExt;
        string outDir = string.IsNullOrEmpty(outputDir)
            ? (Path.GetDirectoryName(video) ?? "")
            : outputDir;
        string path = Path.Combine(outDir, name);
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(video),
                          StringComparison.OrdinalIgnoreCase))
        {
            // 替换源文件模式：仍先写到带 _sub 的临时名，等校验通过后再删源、改名，
            // 这样任何中途失败都不会破坏原视频。
            path = Path.Combine(outDir, stem + (string.IsNullOrEmpty(options.Suffix) ? "_sub" : options.Suffix) + newExt);
        }
        return path;
    }

    /// <summary>
    /// 替换源文件模式下，合并结果最终要落到的路径（与源视频同名、同目录）。
    /// 非替换模式返回 null。
    /// </summary>
    public static string? BuildFinalReplacementPath(string video, Options options, string outputDir)
    {
        if (!options.ReplaceSource) return null;
        string outDir = string.IsNullOrEmpty(outputDir)
            ? (Path.GetDirectoryName(video) ?? "")
            : outputDir;
        // 只有输出目录等于源目录时才可能是「原地替换」
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(outDir)),
                           Path.TrimEndingDirectorySeparator(
                               Path.GetFullPath(Path.GetDirectoryName(video) ?? "")),
                           StringComparison.OrdinalIgnoreCase))
            return null;

        string fileName = Path.GetFileName(video);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = options.Container == "same"
            ? Path.GetExtension(fileName).ToLowerInvariant()
            : "." + options.Container;
        return Path.Combine(outDir, stem + ext);
    }

    public static BuildCommandResult BuildCommand(Job job, Options options, string ffmpeg)
    {
        var notes = new List<string>();
        var cmd = new List<string> { ffmpeg };
        if (options.Overwrite) cmd.Add("-y");
        cmd.AddRange(new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1" });
        cmd.AddRange(new[] { "-i", job.Video });

        var usableSubs = new List<SubTrack>();
        foreach (SubTrack track in job.Subs)
        {
            if (!track.Enabled) continue;
            if (options.Container == "mp4" && track.Kind == "bitmap")
            {
                notes.Add($"MP4 不支持图形字幕（{track.Name}），已跳过");
                continue;
            }
            if (options.Mode == "hard" && track.Kind == "bitmap")
            {
                notes.Add($"硬字幕暂不支持图形字幕（{track.Name}），已跳过");
                continue;
            }
            usableSubs.Add(track);
        }
        if (usableSubs.Count == 0) notes.Add("没有可用的字幕轨");

        foreach (SubTrack track in usableSubs)
            cmd.AddRange(new[] { "-i", track.Path });

        List<SubTrack> tracksToBurn = options.Mode == "hard"
            ? (options.HardMulti ? usableSubs : usableSubs.Take(1).ToList())
            : usableSubs;

        // ExpectedSubs 的语义与 Python v1.1 严格一致（冻结基准逐项比对），因此这里
        // 无条件采用 SStreams。探测失败它会是 0，那只影响校验口径（见 SubsSourceKnown），
        // 不能反过来改变 plan 里的期望值。
        int existingSubs = 0;
        if (options.Mode == "soft" && options.KeepExistingSubs)
            existingSubs = job.Info.SStreams;

        var plan = new OutputPlan
        {
            Mode = options.Mode,
            Container = options.Container,
            ExpectedVideo = 1,
            ExpectedAudio = job.Info.AStreams,
            // 这一项保持与 Python v1.1 完全一致（冻结基准逐项比对 ExpectedSubs）：
            // 「硬字幕 + 无可用轨」时它仍是 0，因为语义是「期望有 0 条新字幕轨」。
            // 但那时命令会 -map 0:s? 把源字幕原样复制进输出，实际数量等于源字幕数，
            // 直接用 0 去校验必然误报。这类「期望值本身不完整」的情形改由
            // OutputPlan.SubsFromSource 标记，交给 OutputVerifier 换一种算法核对。
            ExpectedSubs = options.Mode == "hard" ? 0 : usableSubs.Count + existingSubs,
            // 硬字幕但没有可烧录的轨：命令退化成「复制源字幕」，校验口径随之改变。
            SubsFromSource = options.Mode == "hard" && usableSubs.Count == 0,
            // 探测失败时 SStreams 不可信，无法给出「源字幕有几条」这个前提。
            SubsSourceKnown = job.Info.Probed,
            BurnedSubs = options.Mode == "hard" ? tracksToBurn.Count : 0,
            Vcodec = options.Vcodec,
            Acodec = options.Acodec,
        };

        if (options.Mode == "hard") BuildHard(cmd, options, usableSubs, plan);
        else BuildSoft(cmd, job, options, usableSubs, plan);

        if (!options.KeepMetadata) cmd.AddRange(new[] { "-map_metadata", "-1" });
        cmd.Add(job.Output);
        return new BuildCommandResult(cmd, notes, plan);
    }

    /// <summary>烧录字幕：视频必须重编码；字幕文件已复制到工作目录并使用 ASCII 文件名。</summary>
    private static void BuildHard(List<string> cmd, Options options, List<SubTrack> usableSubs, OutputPlan plan)
    {
        if (usableSubs.Count == 0)
        {
            cmd.AddRange(new[] { "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?" });
            AppendAvCodec(cmd, options, forceVideoEncode: false, plan);
            return;
        }

        List<SubTrack> tracks = options.HardMulti ? usableSubs : usableSubs.Take(1).ToList();
        var chains = new List<string>();
        string prev = "[0:v:0]";
        for (int index = 0; index < tracks.Count; index++)
        {
            string outTag = index == tracks.Count - 1 ? "[vout]" : $"[vs{index}]";
            chains.Add($"{prev}subtitles=filename='{Path.GetFileName(tracks[index].Path)}'{outTag}");
            prev = outTag;
        }
        cmd.AddRange(new[] { "-filter_complex", string.Join(";", chains) });
        cmd.AddRange(new[] { "-map", "[vout]", "-map", "0:a?" });
        AppendAvCodec(cmd, options, forceVideoEncode: true, plan);
    }

    private static void BuildSoft(List<string> cmd, Job job, Options options, List<SubTrack> usableSubs, OutputPlan plan)
    {
        cmd.AddRange(new[] { "-map", "0:v:0", "-map", "0:a?" });
        int existing = 0;
        if (options.KeepExistingSubs)
        {
            existing = job.Info.SStreams;
            cmd.AddRange(new[] { "-map", "0:s?" });
        }
        for (int i = 0; i < usableSubs.Count; i++)
            cmd.AddRange(new[] { "-map", $"{i + 1}:0" });

        AppendAvCodec(cmd, options, forceVideoEncode: false, plan);
        cmd.AddRange(new[] { "-c:s", options.Container == "mp4" ? "mov_text" : "copy" });

        for (int i = 0; i < usableSubs.Count; i++)
        {
            SubTrack track = usableSubs[i];
            int index = existing + i;
            if (!string.IsNullOrEmpty(track.Lang))
                cmd.AddRange(new[] { $"-metadata:s:s:{index}", $"language={track.Lang}" });
            string title = !string.IsNullOrEmpty(track.Title)
                ? track.Title
                : (Constants.LangNames.TryGetValue(track.Lang, out var langName) ? langName : "");
            if (!string.IsNullOrEmpty(title))
                cmd.AddRange(new[] { $"-metadata:s:s:{index}", $"title={title}" });
            string disposition = track.Default ? "default" : "0";
            if (track.Forced) disposition = "forced";
            cmd.AddRange(new[] { $"-disposition:s:{index}", disposition });
        }
    }

    private static void AppendAvCodec(List<string> cmd, Options options, bool forceVideoEncode, OutputPlan plan)
    {
        string vcodec = options.Vcodec;
        if (forceVideoEncode && vcodec == "copy") vcodec = "libx264";
        // plan.Vcodec/Acodec 保持「请求值」，与 Python v1.1 冻结基准一致；
        // 硬字幕强制重编码导致的体积缩水由 OutputVerifier 依据 plan.Mode 豁免。

        if (vcodec == "copy")
        {
            cmd.AddRange(new[] { "-c:v", "copy" });
        }
        else
        {
            cmd.AddRange(new[] { "-c:v", vcodec });

            // 软件与硬件的参数集完全不同，混用会让 ffmpeg 直接报 Invalid argument。
            // 这里统一走共享的 VideoEncoder（三个硬件后端各自的选项名互不相同）：
            //   · 软件 → -preset medium / -crf 20
            //   · AMF  → -quality balanced / -rc cqp -qp_i/p/b（CRF 按 +6 换算成 QP）
            //   · NVENC→ -preset p4 / -rc constqp -qp（写 -rc cqp 会 EINVAL）
            //   · QSV  → -preset medium / -global_quality（没有 -rc/-qp_* 私有选项）
            (EncoderKind kind, _) = VideoEncoder.Classify(vcodec);
            // 只有"认识的 264/265 家族"才补编码参数：用户手工填了别的编码器名时
            // 原样透传，不猜测它的参数语法。
            bool knownFamily = vcodec.Contains("264", StringComparison.Ordinal)
                            || vcodec.Contains("265", StringComparison.Ordinal)
                            || vcodec.Contains("hevc", StringComparison.OrdinalIgnoreCase);
            if (knownFamily)
            {
                VideoEncoder.AppendPreset(cmd, kind, options.Preset);
                VideoEncoder.AppendQuality(cmd, kind, options.Crf);
            }
            cmd.AddRange(new[] { "-pix_fmt", "yuv420p" });
        }

        if (options.Acodec == "copy") cmd.AddRange(new[] { "-c:a", "copy" });
        else cmd.AddRange(new[] { "-c:a", options.Acodec });
    }
}
