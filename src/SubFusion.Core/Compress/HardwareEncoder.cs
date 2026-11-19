using SubFusion.Core.Services;

namespace SubFusion.Core.Compress;

/// <summary>编码器后端。Software 之外的都是显卡硬件编码。</summary>
public enum EncoderKind
{
    Software = 0,
    Amf,
    Nvenc,
    Qsv,
}

/// <summary>目标编码格式。</summary>
public enum VideoCodecKind
{
    H264 = 0,
    Hevc,
}

/// <summary>
/// 一个编码器的真实可用性。
///
/// 为什么不能只看 <c>ffmpeg -encoders</c> 的列表：那份列表只说明「这个构建带了这个编码器」，
/// 与机器上有没有对应显卡无关。本机实测 <c>h264_nvenc</c>（N 卡）、<c>h264_qsv</c>（Intel 核显）、
/// <c>av1_amf</c>（RX 580 不支持 AV1 编码）全都出现在列表里，但真正初始化时一律失败。
/// 只有真的初始化一次编码（哪怕只有 3 帧）才能给出可靠结论。
/// </summary>
public sealed class EncoderAvailability
{
    public string Name { get; init; } = "";
    public EncoderKind Kind { get; init; }
    public VideoCodecKind Codec { get; init; }
    public bool Usable { get; init; }

    /// <summary>不可用原因（面向用户）；可用时为空串。</summary>
    public string Error { get; init; } = "";

    /// <summary>界面标签，例如「AMD 显卡加速 · H.264」。</summary>
    public string Label => Kind switch
    {
        EncoderKind.Amf => Codec == VideoCodecKind.Hevc ? "AMD 显卡加速 · H.265（HEVC）" : "AMD 显卡加速 · H.264",
        EncoderKind.Nvenc => Codec == VideoCodecKind.Hevc ? "NVIDIA 显卡加速 · H.265（HEVC）" : "NVIDIA 显卡加速 · H.264",
        EncoderKind.Qsv => Codec == VideoCodecKind.Hevc ? "Intel 核显加速 · H.265（HEVC）" : "Intel 核显加速 · H.264",
        _ => Codec == VideoCodecKind.Hevc ? "CPU 软件编码 · H.265（HEVC）" : "CPU 软件编码 · H.264",
    };

    public string Detail => Usable ? Label : Label + "（不可用：" + Error + "）";
}

/// <summary>
/// 编码器探测。
///
/// 探测方式：先用 <c>-encoders</c> 过滤出候选名，再对每个候选用 3 帧 320x240 的纯色画面
/// 真正编码一次，退出码为 0 才算可用。
/// 尺寸必须选 320x240 而不是更小的：AMF 的 HEVC 编码器对小画面有下限，
/// 实测 64x64 / 128x128 / 192x108 全部失败、256x144 起才成功——用 64x64 探测会把
/// 本机真实可用的 hevc_amf 误判成「不支持」。
/// </summary>
public static class HardwareEncoder
{
    /// <summary>候选编码器：名称 → (后端, 编码格式)。顺序即「自动」模式下的优先级。</summary>
    private static readonly (string Name, EncoderKind Kind, VideoCodecKind Codec)[] Candidates =
    {
        ("h264_amf", EncoderKind.Amf, VideoCodecKind.H264),
        ("hevc_amf", EncoderKind.Amf, VideoCodecKind.Hevc),
        ("h264_nvenc", EncoderKind.Nvenc, VideoCodecKind.H264),
        ("hevc_nvenc", EncoderKind.Nvenc, VideoCodecKind.Hevc),
        ("h264_qsv", EncoderKind.Qsv, VideoCodecKind.H264),
        ("hevc_qsv", EncoderKind.Qsv, VideoCodecKind.Hevc),
        ("libx264", EncoderKind.Software, VideoCodecKind.H264),
        ("libx265", EncoderKind.Software, VideoCodecKind.Hevc),
    };

    /// <summary>探测画面尺寸。见类型注释：小于 256x144 会让 AMF 的 HEVC 误判。</summary>
    private const string ProbeSize = "320x240";

    /// <summary>探测全部候选编码器。任一步失败都按「不可用」返回，不抛异常。</summary>
    public static async Task<IReadOnlyList<EncoderAvailability>> ProbeAsync(
        string ffmpeg, CancellationToken cancellationToken = default)
    {
        var results = new List<EncoderAvailability>();
        if (string.IsNullOrEmpty(ffmpeg))
        {
            foreach ((string name, EncoderKind kind, VideoCodecKind codec) in Candidates)
                results.Add(new EncoderAvailability { Name = name, Kind = kind, Codec = codec, Error = "未找到 ffmpeg" });
            return results;
        }

        // 第一步：这个构建里到底有没有这些编码器（快速、无副作用）。
        var listed = new HashSet<string>(StringComparer.Ordinal);
        ProcessCapture encoders = await ProcessUtil.RunAsync(ffmpeg,
            new[] { "-hide_banner", "-encoders" }, 20, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!encoders.TimedOut)
        {
            string text = encoders.Stdout + encoders.Stderr;
            foreach ((string name, _, _) in Candidates)
                if (text.Contains(name, StringComparison.Ordinal)) listed.Add(name);
        }

        // 第二步：真的初始化一次编码。按后端分组并发，每组最多 2 个，避免同时敲显卡。
        foreach ((string name, EncoderKind kind, VideoCodecKind codec) in Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!listed.Contains(name))
            {
                results.Add(new EncoderAvailability
                {
                    Name = name, Kind = kind, Codec = codec,
                    Error = "当前 ffmpeg 构建不含该编码器",
                });
                continue;
            }

            (bool ok, string error) = await ProbeOneAsync(ffmpeg, name, cancellationToken).ConfigureAwait(false);
            results.Add(new EncoderAvailability { Name = name, Kind = kind, Codec = codec, Usable = ok, Error = error });
        }
        return results;
    }

    /// <summary>真正编码 3 帧，验证编码器能否在本机初始化。退出码 0 才算可用。</summary>
    private static async Task<(bool Ok, string Error)> ProbeOneAsync(
        string ffmpeg, string name, CancellationToken cancellationToken)
    {
        string[] args =
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "lavfi", "-i", "color=c=black:s=" + ProbeSize + ":r=10:d=0.4",
            "-frames:v", "3", "-c:v", name, "-f", "null", "-",
        };
        ProcessCapture capture = await ProcessUtil.RunAsync(ffmpeg, args, 20,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (capture.TimedOut) return (false, "初始化超时");
        if (capture.ExitCode == 0) return (true, "");
        return (false, ShortReason(capture.Stderr));
    }

    private static string ShortReason(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "初始化失败";
        string[] lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string line = lines.FirstOrDefault(l => l.Contains("rror", StringComparison.OrdinalIgnoreCase)) ?? lines[^1];
        line = line.Trim();
        return line.Length > 120 ? line[..120] : line;
    }

    /// <summary>
    /// 选一个可用编码器。
    /// </summary>
    /// <param name="available">探测结果。</param>
    /// <param name="codec">目标编码格式。</param>
    /// <param name="preference">"auto" / "hardware" / "software"。</param>
    /// <param name="backends">限定后端（空表示不限）；界面用它让用户锁定 AMD/NVIDIA/Intel。</param>
    public static EncoderAvailability? Select(IReadOnlyList<EncoderAvailability> available,
                                              VideoCodecKind codec, string preference,
                                              IReadOnlyCollection<EncoderKind>? backends = null)
    {
        IEnumerable<EncoderAvailability> pool = available.Where(e => e.Codec == codec && e.Usable);
        if (backends is { Count: > 0 }) pool = pool.Where(e => backends.Contains(e.Kind));

        bool softwareOnly = string.Equals(preference, "software", StringComparison.OrdinalIgnoreCase);
        bool hardwareOnly = string.Equals(preference, "hardware", StringComparison.OrdinalIgnoreCase);

        if (softwareOnly) return pool.FirstOrDefault(e => e.Kind == EncoderKind.Software);

        if (hardwareOnly)
            return pool.Where(e => e.Kind != EncoderKind.Software)
                       .OrderBy(e => e.Kind)
                       .FirstOrDefault();

        // auto：硬件优先，硬件不可用时退回软件。
        return pool.Where(e => e.Kind != EncoderKind.Software).OrderBy(e => e.Kind).FirstOrDefault()
               ?? pool.FirstOrDefault(e => e.Kind == EncoderKind.Software);
    }

    /// <summary>
    /// 「自动」模式下的失败兜底：当前编码器不可用时换成同格式的软件编码器。
    /// </summary>
    public static EncoderAvailability? SoftwareFallback(IReadOnlyList<EncoderAvailability> available,
                                                        VideoCodecKind codec)
        => available.FirstOrDefault(e => e.Usable && e.Kind == EncoderKind.Software && e.Codec == codec)
           ?? available.FirstOrDefault(e => e.Usable && e.Kind == EncoderKind.Software);

    /// <summary>硬件解码是否值得启用：只有存在可用的硬编后端时才谈得上（避免无显卡机器上加参数）。</summary>
    public static bool SupportsD3D11(IReadOnlyList<EncoderAvailability> available)
        => available.Any(e => e.Usable && e.Kind != EncoderKind.Software);
}
