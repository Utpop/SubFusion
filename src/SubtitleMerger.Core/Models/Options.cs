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

    public Options Clone() => (Options)MemberwiseClone();
}
