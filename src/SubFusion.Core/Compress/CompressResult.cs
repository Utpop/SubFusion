using SubFusion.Core.Media;

namespace SubFusion.Core.Compress;

/// <summary>一次进度回调的快照（不可变，可安全跨线程传递）。</summary>
public sealed record CompressProgress(
    CompressStage Stage,
    double Percent,
    string Message,
    double Speed = 0,
    double? Eta = null,
    string? Encoder = null);

/// <summary>压制任务终态。</summary>
public enum CompressStatus
{
    Completed = 0,
    Failed,
    Cancelled,
}

/// <summary>
/// 视频压制任务的完整结果：状态、阶段、输入输出体积、实际使用的编码器与诊断日志。
/// GUI 只渲染这个对象，不关心内部实现。
/// </summary>
public sealed class CompressResult
{
    public string TaskId { get; init; } = "";
    public CompressStatus Status { get; init; } = CompressStatus.Failed;
    public CompressStage Stage { get; init; } = CompressStage.Failed;
    public bool Ok => Status == CompressStatus.Completed;

    /// <summary>最终输出文件路径（成功时存在）。</summary>
    public string OutputPath { get; init; } = "";

    /// <summary>输出文件的最终探测结果。</summary>
    public MediaBundle? Output { get; init; }

    /// <summary>源文件体积（字节）。</summary>
    public long SourceBytes { get; init; }

    /// <summary>输出体积（字节）。</summary>
    public long OutputBytes { get; init; }

    /// <summary>源时长（秒）。</summary>
    public double SourceDuration { get; init; }

    /// <summary>输出时长（秒）。</summary>
    public double OutputDuration { get; init; }

    /// <summary>实际使用的编码器名（如 h264_amf / libx264）。</summary>
    public string Encoder { get; init; } = "";

    /// <summary>实际使用的编码器标签（面向用户）。</summary>
    public string EncoderLabel { get; init; } = "";

    /// <summary>是否走了显卡硬件编码。</summary>
    public bool HardwareEncoded { get; init; }

    /// <summary>是否成功启用了显卡解码。</summary>
    public bool HardwareDecoded { get; init; }

    /// <summary>是否复制了音轨（未重编码）。</summary>
    public bool AudioCopied { get; init; }

    /// <summary>输出的实际码率（kbps）；无法计算时为 0。</summary>
    public double OutputBitrateKbps =>
        OutputDuration > 0 && OutputBytes > 0 ? OutputBytes * 8.0 / OutputDuration / 1000.0 : 0;

    /// <summary>体积变化比例；正数表示变小了多少（例如 0.62 表示小了 62%）。</summary>
    public double SavingsRatio =>
        SourceBytes > 0 && OutputBytes > 0 ? 1.0 - (double)OutputBytes / SourceBytes : 0;

    /// <summary>成功时的提示信息。</summary>
    public IReadOnlyList<string> Notices { get; init; } = Array.Empty<string>();

    /// <summary>失败原因（面向用户）。</summary>
    public string Error { get; init; } = "";

    /// <summary>诊断日志（命令、编码器选择、ffmpeg 输出、校验结论）。</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public double ElapsedSeconds { get; init; }

    public string SummaryText
    {
        get
        {
            if (!Ok)
                return Status == CompressStatus.Cancelled ? "已取消" : "失败：" + Error;
            string savings = SavingsRatio > 0
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "，体积减少 {0:0.#}%", SavingsRatio * 100)
                : SavingsRatio < 0
                    ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "，体积增加 {0:0.#}%", -SavingsRatio * 100)
                    : "";
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "压制完成：{0}（{1} → {2}{3}）",
                System.IO.Path.GetFileName(OutputPath),
                Formatting.SizeText(SourceBytes), Formatting.SizeText(OutputBytes), savings);
        }
    }

    private static class Formatting
    {
        public static string SizeText(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.##} {1}", value, units[unit]);
        }
    }
}
