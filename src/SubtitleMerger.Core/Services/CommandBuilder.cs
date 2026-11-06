using System.Globalization;
using SubtitleMerger.Core.Models;

namespace SubtitleMerger.Core.Services;

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
            path = Path.Combine(outDir, stem + (string.IsNullOrEmpty(options.Suffix) ? "_sub" : options.Suffix) + newExt);
        }
        return path;
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

        int existingSubs = 0;
        if (options.Mode == "soft" && options.KeepExistingSubs)
            existingSubs = job.Info.SStreams;

        var plan = new OutputPlan
        {
            Mode = options.Mode,
            Container = options.Container,
            ExpectedVideo = 1,
            ExpectedAudio = job.Info.AStreams,
            ExpectedSubs = options.Mode == "hard" ? 0 : usableSubs.Count + existingSubs,
            BurnedSubs = options.Mode == "hard" ? tracksToBurn.Count : 0,
            Vcodec = options.Vcodec,
            Acodec = options.Acodec,
        };

        if (options.Mode == "hard") BuildHard(cmd, options, usableSubs);
        else BuildSoft(cmd, job, options, usableSubs);

        if (!options.KeepMetadata) cmd.AddRange(new[] { "-map_metadata", "-1" });
        cmd.Add(job.Output);
        return new BuildCommandResult(cmd, notes, plan);
    }

    /// <summary>烧录字幕：视频必须重编码；字幕文件已复制到工作目录并使用 ASCII 文件名。</summary>
    private static void BuildHard(List<string> cmd, Options options, List<SubTrack> usableSubs)
    {
        if (usableSubs.Count == 0)
        {
            cmd.AddRange(new[] { "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?" });
            AppendAvCodec(cmd, options, forceVideoEncode: false);
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
        AppendAvCodec(cmd, options, forceVideoEncode: true);
    }

    private static void BuildSoft(List<string> cmd, Job job, Options options, List<SubTrack> usableSubs)
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

        AppendAvCodec(cmd, options, forceVideoEncode: false);
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

    private static void AppendAvCodec(List<string> cmd, Options options, bool forceVideoEncode)
    {
        string vcodec = options.Vcodec;
        if (forceVideoEncode && vcodec == "copy") vcodec = "libx264";

        if (vcodec == "copy")
        {
            cmd.AddRange(new[] { "-c:v", "copy" });
        }
        else
        {
            cmd.AddRange(new[] { "-c:v", vcodec });
            if (vcodec is "libx264" or "libx265" or "h264_nvenc" or "hevc_nvenc")
            {
                cmd.AddRange(new[] { "-preset", options.Preset });
                if (vcodec is "libx264" or "libx265")
                    cmd.AddRange(new[] { "-crf", options.Crf.ToString(CultureInfo.InvariantCulture) });
                else
                    cmd.AddRange(new[] { "-rc", "vbr", "-cq", options.Crf.ToString(CultureInfo.InvariantCulture) });
            }
            cmd.AddRange(new[] { "-pix_fmt", "yuv420p" });
        }

        if (options.Acodec == "copy") cmd.AddRange(new[] { "-c:a", "copy" });
        else cmd.AddRange(new[] { "-c:a", options.Acodec });
    }
}
