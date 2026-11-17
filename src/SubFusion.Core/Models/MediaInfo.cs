namespace SubFusion.Core.Models;

/// <summary>ffprobe 探测结果（原 core.probe_streams 返回的 dict）。</summary>
public sealed class MediaInfo
{
    public double? Duration { get; set; }
    public int VStreams { get; set; }
    public int AStreams { get; set; }
    public int SStreams { get; set; }
    public string Vcodec { get; set; } = "";
    public string Acodec { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public long Size { get; set; }

    /// <summary>
    /// 是否已被 ffprobe 成功探测过。Python 版用 `if not j.info`（空 dict）判断，
    /// C# 中模型对象始终非空，故用显式标记等价替代。
    /// </summary>
    public bool Probed { get; set; }
}
