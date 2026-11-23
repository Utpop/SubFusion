using System.Globalization;
using SubFusion.Core.Services;

namespace SubFusion.Core.Media;

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
public static class VideoEncoder
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

    /// <summary>
    /// 同一 ffmpeg 路径的探测结果缓存。
    ///
    /// 存在的理由是可测量的：8 个候选各真编码 3 帧，本机实测合计约 1558 ms
    /// （-encoders 列表 130 ms + h264_amf 278 / hevc_amf 280 / h264_nvenc 122 /
    /// hevc_nvenc 120 / h264_qsv 238 / hevc_qsv 238 / libx264 129 / libx265 153）。
    /// 而压制引擎在 AnalyzeAsync 与 RunAsync 里各探测一次，
    /// 等于每次开始压制都白等一次全额探测；裁剪页切换输入也会反复触发。
    ///
    /// 缓存键是 ffmpeg 可执行文件的完整路径：显卡驱动/工具路径不变时结果不会变，
    /// 换了 ffmpeg 二进制自然会重新探测。用户换工具走的是另一个路径，不会被旧结果污染。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<EncoderAvailability>>
        ProbeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>清空探测缓存（工具路径变化或自检需要重新探测时调用）。</summary>
    public static void ClearProbeCache() => ProbeCache.Clear();

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

        if (ProbeCache.TryGetValue(ffmpeg, out IReadOnlyList<EncoderAvailability>? cached))
            return cached;

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

        // 只在完整探测成功后写缓存：被取消的半截结果不能算数，
        // 否则一次中途取消会把「不可用」永久钉在缓存里。
        cancellationToken.ThrowIfCancellationRequested();
        ProbeCache[ffmpeg] = results;
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

    #region 共享参数映射（压制 / 裁剪 / 硬字幕三个模块共用）

    /// <summary>
    /// 编码器名 → 后端与编码格式。未知名称按「软件 + H.264」处理。
    ///
    /// 三个业务模块都必须走这里，而不是各自 switch 编码器名：
    /// 一旦某处漏掉一个名字，就会出现「同一条 ffmpeg 参数规则在两个页面结果不同」。
    /// </summary>
    public static (EncoderKind Kind, VideoCodecKind Codec) Classify(string? encoderName)
    {
        string name = (encoderName ?? "").Trim().ToLowerInvariant();
        foreach ((string candidate, EncoderKind kind, VideoCodecKind codec) in Candidates)
        {
            if (string.Equals(candidate, name, StringComparison.Ordinal)) return (kind, codec);
        }
        // 未登记的硬件编码器名（例如未来的 av1_nvenc）：仍按硬件处理，参数集不会退化成 x264 的。
        if (name.Contains("nvenc", StringComparison.Ordinal)) return (EncoderKind.Nvenc, VideoCodecKind.H264);
        if (name.Contains("qsv", StringComparison.Ordinal)) return (EncoderKind.Qsv, VideoCodecKind.H264);
        if (name.Contains("amf", StringComparison.Ordinal)) return (EncoderKind.Amf, VideoCodecKind.H264);
        return (EncoderKind.Software, name.Contains("265") || name.Contains("hevc", StringComparison.Ordinal)
            ? VideoCodecKind.Hevc : VideoCodecKind.H264);
    }

    /// <summary>该编码器名是否走显卡硬件编码。</summary>
    public static bool IsHardware(string? encoderName) => Classify(encoderName).Kind != EncoderKind.Software;

    /// <summary>
    /// 候选编码器名清单（顺序即自动模式的优先级：AMD → NVIDIA → Intel → 软件）。
    /// 供界面构造下拉框使用，避免三处各写一份硬编码列表。
    /// </summary>
    public static IReadOnlyList<string> CandidateNames
        => Candidates.Select(c => c.Name).ToArray();

    /// <summary>
    /// 界面画质档（CRF 语义）→ 硬件编码的 QP。
    ///
    /// Qp 与 x264 的 CRF 不同量纲：同一个数字下 AMF 明显更糊
    /// （实测 720p 6s 素材：qp 24 出 2.6MB、qp 32 出 0.9MB，而 x264 crf 23 约 1.1MB）。
    /// 因此统一按 +6 偏移映射，让「画质」滑杆在软硬件之间大体等价——
    /// 用户切到显卡加速后画面不应明显变差，更不能因为数值方向反了而变好。
    /// </summary>
    public static int HardwareQp(int crfQuality) => Math.Clamp(crfQuality + 6, 0, 51);

    /// <summary>硬件质量档：AMF 用 balanced/speed/quality，NVENC/QSV 也接受同一组名称。</summary>
    public static string NormalizeHardwareQuality(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "speed" => "speed",
        "quality" => "quality",
        "high_quality" => "quality",     // NVENC 没有 high_quality 档，降级到 quality
        _ => "balanced",
    };

    /// <summary>软件编码器 preset 白名单；非法值退回 medium（ffmpeg 对非法 preset 直接报错）。</summary>
    public static string NormalizePreset(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "ultrafast" or "veryfast" or "faster" or "fast" or "medium" or "slow" or "slower" or "veryslow"
            => value!.Trim().ToLowerInvariant(),
        _ => "medium",
    };

    /// <summary>
    /// 产出「画质」参数。软件用 <c>-crf</c>；硬件三个后端各有各的写法，不能共用一套：
    ///
    /// <list type="bullet">
    /// <item><b>AMF</b>：<c>-rc cqp</c> + <c>-qp_i/-qp_p/-qp_b</c>（本机实测可正常编码）。</item>
    /// <item><b>NVENC</b>：<c>-rc</c> 只认 constqp / vbr / cbr 三个名字。写 <c>-rc cqp</c> 会得到
    ///   <c>Unable to parse "rc" option value "cqp"</c> 并以 -22（EINVAL）失败——
    ///   本机对 h264_nvenc 实测退出码正是 -22。恒定量化参数用单值 <c>-qp</c>，
    ///   <c>-qp_i/-qp_p/-qp_b</c> 在 NVENC 上根本不存在，传了只会被静默忽略。</item>
    /// <item><b>QSV</b>：没有 <c>-rc</c> / <c>-qp_*</c> 私有选项，传了只会打印
    ///   <c>Codec AVOption ... has not been used for any stream</c> 然后忽略，
    ///   结果画质完全不受控。质量档必须走通用的 <c>-global_quality</c>。</item>
    /// </list>
    ///
    /// 这是四个调用点（压制 / 裁剪 / 硬字幕 / 合并）唯一允许构造画质参数的地方：
    /// 各写一份的结果就是「同一个滑杆数值在不同后端下画质不同，或直接报错」。
    /// </summary>
    public static void AppendQuality(List<string> args, EncoderKind kind, int crfQuality)
    {
        if (kind == EncoderKind.Software)
        {
            args.AddRange(new[] { "-crf", crfQuality.ToString(CultureInfo.InvariantCulture) });
            return;
        }

        string qp = HardwareQp(crfQuality).ToString(CultureInfo.InvariantCulture);
        switch (kind)
        {
            case EncoderKind.Nvenc:
                args.AddRange(new[] { "-rc", "constqp", "-qp", qp });
                break;
            case EncoderKind.Qsv:
                args.AddRange(new[] { "-global_quality", qp });
                break;
            default:   // AMF：独有 I/P/B 三档 QP
                args.AddRange(new[] { "-rc", "cqp", "-qp_i", qp, "-qp_p", qp, "-qp_b", qp });
                break;
        }
    }

    /// <summary>
    /// 产出「编码速度」参数。三个硬件后端的 preset 名称互不相同，统一在这里换算：
    /// AMF 只有 balanced/speed/quality；NVENC 是 p1–p7（旧的 slow/medium/fast 仍可用）；
    /// QSV 是 veryfast…veryslow。<c>-quality</c> 只有 AMF 认，其余两个传了会被忽略。
    /// </summary>
    public static void AppendPreset(List<string> args, EncoderKind kind, string? preset)
    {
        if (kind == EncoderKind.Software)
        {
            args.AddRange(new[] { "-preset", NormalizePreset(preset) });
            return;
        }
        if (kind == EncoderKind.Amf)
        {
            args.AddRange(new[] { "-quality", NormalizeHardwareQuality(preset) });
            return;
        }
        args.AddRange(new[] { "-preset", HardwarePreset(kind, preset) });
    }

    /// <summary>
    /// 把抽象质量档（speed / balanced / quality）换算成指定后端的 preset 名称。
    /// 界面只提供三档，各后端自行映射到自己的刻度，用户不必了解 p1–p7 这类细节。
    /// </summary>
    public static string HardwarePreset(EncoderKind kind, string? value)
    {
        string quality = NormalizeHardwareQuality(value);
        return kind switch
        {
            EncoderKind.Nvenc => quality switch
            {
                "speed" => "p2",
                "quality" => "p6",
                _ => "p4",
            },
            EncoderKind.Qsv => quality switch
            {
                "speed" => "fast",
                "quality" => "slow",
                _ => "medium",
            },
            _ => quality,   // AMF 原生取值就是这三档
        };
    }

    /// <summary>
    /// 产出「目标码率」参数。码率模式下 <c>-rc</c> 的取值同样按后端区分：
    /// NVENC 只认 vbr（写 vbr_peak 会 Unable to parse 并以 -22 失败）；
    /// QSV 没有 -rc 私有选项，给 <c>-b:v</c> 即可；AMF 用 vbr_peak + maxrate 约束峰值。
    /// </summary>
    public static void AppendBitrate(List<string> args, EncoderKind kind, int kbps)
    {
        string bitrate = kbps.ToString(CultureInfo.InvariantCulture) + "k";
        if (kind == EncoderKind.Software)
        {
            args.AddRange(new[] { "-b:v", bitrate, "-maxrate", bitrate,
                                  "-bufsize", (kbps * 2).ToString(CultureInfo.InvariantCulture) + "k" });
            return;
        }
        switch (kind)
        {
            case EncoderKind.Nvenc:
                args.AddRange(new[] { "-rc", "vbr", "-b:v", bitrate, "-maxrate", bitrate });
                break;
            case EncoderKind.Qsv:
                args.AddRange(new[] { "-b:v", bitrate, "-maxrate", bitrate });
                break;
            default:   // AMF
                args.AddRange(new[] { "-rc", "vbr_peak", "-b:v", bitrate, "-maxrate", bitrate });
                break;
        }
    }

    /// <summary>硬件编码器通用的 profile 约束；软件侧不在此处处理。</summary>
    public static void AppendHardwareProfile(List<string> args, VideoCodecKind codec)
        => args.AddRange(new[] { "-profile:v", codec == VideoCodecKind.Hevc ? "main" : "high" });

    #endregion
}
