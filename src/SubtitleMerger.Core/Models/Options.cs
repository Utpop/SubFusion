namespace SubtitleMerger.Core.Models;

/// <summary>
/// 一次批量处理的全部选项（原 core.Options）。
/// 默认值逐项对应 Python 版 Options.__init__。
/// </summary>
public sealed class Options
{
    public string Container { get; set; } = "mkv";        // mkv / mp4 / same
    public string Mode { get; set; } = "soft";            // soft / hard
    public string Vcodec { get; set; } = "copy";          // copy / libx264 / libx265 / h264_nvenc
    public string Acodec { get; set; } = "copy";          // copy / aac
    public int Crf { get; set; } = 20;
    public string Preset { get; set; } = "medium";
    public bool KeepExistingSubs { get; set; } = true;
    public bool KeepMetadata { get; set; } = true;
    public bool ConvertEncoding { get; set; } = true;
    public string OutputDir { get; set; } = "";
    public string Suffix { get; set; } = "";
    public bool Overwrite { get; set; } = true;
    public int Workers { get; set; } = 1;
    public bool HardMulti { get; set; } = true;           // 硬字幕时是否叠加多个字幕轨
    public bool Verify { get; set; } = true;              // 处理完成后校验输出

    /// <summary>
    /// 合并并通过校验后，删除原始视频与已合并的字幕文件，只保留合并结果，
    /// 且输出使用原视频文件名（不加 <c>_sub</c>）。
    ///
    /// 安全约束（任一条不满足都只保留原件、不删除）：
    /// <list type="bullet">
    /// <item>输出目录必须与源视频目录相同（否则谈不上「替换」）；</item>
    /// <item>ffmpeg 返回 0 且输出文件存在；</item>
    /// <item>校验已开启且通过，或校验未开启但文件非空——两者都不满足时不删除；</item>
    /// <item>删除前先把合并结果改名到最终路径，改名校验失败则回滚。</item>
    /// </list>
    /// 默认关闭，避免误删用户素材。
    /// </summary>
    public bool ReplaceSource { get; set; }

    public Options Clone() => (Options)MemberwiseClone();
}
