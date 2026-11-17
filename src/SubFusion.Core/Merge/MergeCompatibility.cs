using System.Globalization;
using SubFusion.Core.Media;

namespace SubFusion.Core.Merge;

/// <summary>
/// 消除一条差异所需要的最小代价。
/// 只有「重新编码」和「重新封装」两档：后者全程 -c copy，画质零损失，耗时是秒级。
/// </summary>
public enum MergeReasonFix
{
    /// <summary>只能靠重新编码解决（编码参数、几何、色彩等）。</summary>
    Reencode = 0,
    /// <summary>重新封装即可解决（时间基准、帧率这类时间轴参数）。</summary>
    Remux,
}

/// <summary>一条「不能直接拼接」的原因；用于日志与结果诊断。</summary>
public sealed record MergeReason(string Code, string Detail, MergeReasonFix Fix = MergeReasonFix.Reencode);

/// <summary>
/// 无损拼接（concat demuxer + stream copy）的可行性结论。
/// 直接拼接不可行时还要再分一层：差异能否只靠重新封装消除。
/// 时间基准/帧率不一致会让 concat 产生非单调 DTS 和错位时长，但重新封装后即可对齐，
/// 这种情况下重压整段视频是纯粹的浪费。
/// </summary>
public sealed class MergeCompatibility
{
    /// <summary>所有输入参数完全一致，可以直接 concat。</summary>
    public bool CanStreamCopy { get; init; }

    /// <summary>
    /// 存在差异，但每一条都能用 stream copy 重封装消除，因此仍然不需要重新编码。
    /// 注意：此属性为 true 时 <see cref="CanStreamCopy"/> 必定为 false。
    /// </summary>
    public bool CanRemuxOnly => Reasons.Count > 0 && Reasons.All(r => r.Fix == MergeReasonFix.Remux);

    /// <summary>无法只靠重封装解决，必须统一重新编码。</summary>
    public bool NeedsReencode => Reasons.Count > 0 && !CanRemuxOnly;

    public List<MergeReason> Reasons { get; } = new();
    public string PrimaryReason => Reasons.Count == 0 ? "" : Reasons[0].Detail;
}

/// <summary>
/// 输入兼容性分析。判断依据不是「编码名相同」，而是影响解码与时间轴的完整参数组合：
/// 编码/档位/等级、分辨率、像素格式、帧率、时间基准、SAR/DAR、色彩参数、
/// 视频流数量与顺序、音频编码/采样率/声道布局、容器族。
/// </summary>
public static class CompatibilityAnalyzer
{
    private const double FrameRateTolerance = 0.01;

    public static MergeCompatibility Analyze(IReadOnlyList<MediaBundle> bundles)
    {
        var reasons = new List<MergeReason>();

        if (bundles.Count == 0)
            reasons.Add(new MergeReason("empty", "没有输入文件"));
        foreach (MediaBundle b in bundles.Where(x => !x.Success))
            reasons.Add(new MergeReason("probe", $"无法读取媒体信息：{Path.GetFileName(b.Path)}（{b.Error}）"));
        if (reasons.Count > 0)
            return new MergeCompatibility { CanStreamCopy = false }.Also(reasons);

        MediaBundle first = bundles[0];
        VideoStreamInfo? v0 = first.PrimaryVideo;
        if (v0 is null)
            reasons.Add(new MergeReason("no-video", "第一个输入没有视频流"));

        for (int i = 1; i < bundles.Count; i++)
        {
            MediaBundle b = bundles[i];
            string name = Path.GetFileName(b.Path);
            VideoStreamInfo? v = b.PrimaryVideo;
            if (v is null)
                reasons.Add(new MergeReason("no-video", name + " 没有视频流"));

            // 视频参数需要双方都有视频流才能比较；音频与容器诊断对纯音频输入同样有效。
            if (v0 is not null && v is not null)
            {
                if (b.Videos.Count != first.Videos.Count)
                    reasons.Add(new MergeReason("video-count",
                        $"视频流数量不一致（{name} 有 {b.Videos.Count} 条，第一个输入有 {first.Videos.Count} 条）"));
                if (!Eq(v.Codec, v0.Codec))
                    reasons.Add(new MergeReason("vcodec", $"视频编码不一致（{name} 为 {v.Codec}，第一个为 {v0.Codec}）"));
                if (!Eq(v.Profile, v0.Profile))
                    reasons.Add(new MergeReason("vprofile", $"视频档位不一致（{name} 为 {v.Profile}，第一个为 {v0.Profile}）"));
                if (v.Width != v0.Width || v.Height != v0.Height)
                    reasons.Add(new MergeReason("resolution",
                        $"分辨率不一致（{name} 为 {v.SizeText}，第一个为 {v0.SizeText}）"));
                if (!Eq(v.PixFmt, v0.PixFmt))
                    reasons.Add(new MergeReason("pixfmt", $"像素格式不一致（{name} 为 {v.PixFmt}，第一个为 {v0.PixFmt}）"));
                // 帧率差异不会破坏 concat：每段各自携带时间戳，重封装后仍能正确累加。
                // 实测 29.97 与 30 同时间基准拼接，解码零错误、总时长正确，因此不触发重编码。
                if (Math.Abs(v.EffectiveFrameRate - v0.EffectiveFrameRate) > FrameRateTolerance)
                    reasons.Add(new MergeReason("framerate", string.Format(CultureInfo.InvariantCulture,
                        "帧率不一致（{0} 为 {1:0.###}，第一个为 {2:0.###}）",
                        name, v.EffectiveFrameRate, v0.EffectiveFrameRate), MergeReasonFix.Remux));
                // 时间基准必须对齐，否则 concat 会产生非单调 DTS 与错位时长（实测 44 处错误、11s 变 8.59s）。
                // 但重封装到同一时基即可解决，不需要重新编码。
                if (!Eq(v.TimeBase, v0.TimeBase))
                    reasons.Add(new MergeReason("timebase",
                        $"时间基准不一致（{name} 为 {v.TimeBase}，第一个为 {v0.TimeBase}）", MergeReasonFix.Remux));
                if (NormalizeSar(v.Sar) != NormalizeSar(v0.Sar))
                    reasons.Add(new MergeReason("sar", $"SAR 不一致（{name} 为 {v.Sar}，第一个为 {v0.Sar}）"));
                if (!Eq(v.ColorSpace, v0.ColorSpace) || !Eq(v.ColorTransfer, v0.ColorTransfer) ||
                    !Eq(v.ColorPrimaries, v0.ColorPrimaries) || !Eq(v.ColorRange, v0.ColorRange))
                    reasons.Add(new MergeReason("color",
                        $"色彩参数不一致（{name} 为 {ColorText(v)}，第一个为 {ColorText(v0)}）"));
                if (!Eq(v.HdrFormat, v0.HdrFormat))
                    reasons.Add(new MergeReason("hdr", $"动态范围不一致（{name} 为 {(v.IsHdr ? v.HdrFormat : "SDR")}，" +
                                                       $"第一个为 {(v0.IsHdr ? v0.HdrFormat : "SDR")}）"));
            }

            // 音频与容器诊断不依赖第一个输入是否有视频流。
            if (b.Audios.Count != first.Audios.Count)
                reasons.Add(new MergeReason("audio-count",
                    $"音频流数量不一致（{name} 有 {b.Audios.Count} 条，第一个输入有 {first.Audios.Count} 条）"));
            else
            {
                for (int a = 0; a < b.Audios.Count; a++)
                {
                    AudioStreamInfo x = b.Audios[a];
                    AudioStreamInfo y = first.Audios[a];
                    if (!Eq(x.Codec, y.Codec))
                        reasons.Add(new MergeReason("acodec",
                            $"音频编码不一致（{name} 第 {a + 1} 轨为 {x.Codec}，第一个为 {y.Codec}）"));
                    if (x.SampleRate != y.SampleRate)
                        reasons.Add(new MergeReason("asamplerate", string.Format(CultureInfo.InvariantCulture,
                            "音频采样率不一致（{0} 为 {1} Hz，第一个为 {2} Hz）", name, x.SampleRate, y.SampleRate)));
                    if (x.Channels != y.Channels)
                        reasons.Add(new MergeReason("achannels", string.Format(CultureInfo.InvariantCulture,
                            "音频声道数不一致（{0} 为 {1}，第一个为 {2}）", name, x.Channels, y.Channels)));
                    if (!Eq(x.ChannelLayout, y.ChannelLayout))
                        reasons.Add(new MergeReason("alayout",
                            $"声道布局不一致（{name} 为 {x.ChannelLayout}，第一个为 {y.ChannelLayout}）"));
                }
            }

            if (!Eq(b.Container.Family, first.Container.Family))
                reasons.Add(new MergeReason("container",
                    $"容器格式不一致（{name} 为 {b.Container.Family}，第一个为 {first.Container.Family}）"));
        }

        return new MergeCompatibility { CanStreamCopy = reasons.Count == 0 }.Also(reasons);
    }

    private static string ColorText(VideoStreamInfo v) =>
        string.Join("/", new[] { v.ColorSpace, v.ColorTransfer, v.ColorPrimaries, v.ColorRange }
            .Select(s => s.Length == 0 ? "-" : s));

    private static string NormalizeSar(string sar)
        => sar.Length == 0 || sar == "0:1" ? "1:1" : sar;

    private static bool Eq(string a, string b)
    {
        static string N(string s)
        {
            string t = (s ?? "").Trim().ToLowerInvariant();
            return t is "unknown" or "reserved" or "n/a" ? "" : t;
        }
        return N(a) == N(b);
    }
}

internal static class MergeCompatibilityExtensions
{
    /// <summary>把原因列表填入结论对象，保持构造语句可读。</summary>
    public static MergeCompatibility Also(this MergeCompatibility compatibility, List<MergeReason> reasons)
    {
        compatibility.Reasons.AddRange(reasons);
        return compatibility;
    }
}
