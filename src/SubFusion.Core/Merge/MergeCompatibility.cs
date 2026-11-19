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

    /// <summary>
    /// 按「消除这条差异需要付什么代价」分组后的差异。
    /// UI 直接平铺 Reasons 时，一长串并列的差异会让人看不出重点——
    /// 用户真正想知道的是「有没有画质损失」，而这取决于其中有没有 Reencode 档。
    /// 实测一份四段素材能列出十几条差异，分组后通常只剩两组。
    /// </summary>
    public IReadOnlyList<MergeReasonGroup> GroupedReasons =>
        Reasons
            .GroupBy(r => r.Fix)
            .OrderBy(g => g.Key)          // Reencode 在前：更严重的问题优先呈现
            .Select(g => new MergeReasonGroup(
                g.Key,
                g.Key == MergeReasonFix.Reencode ? "必须重新编码" : "可用无损重封装解决",
                g.Key == MergeReasonFix.Reencode
                    ? "下列差异无法只靠重封装消除，需要重新编码视频："
                    : "下列差异只需重封装对齐时间轴，视频不会被重新编码：",
                g.Select(r => r.Detail).ToList()))
            .ToList();
}

/// <summary>一组修复代价相同的差异，用于分组展示。</summary>
public sealed record MergeReasonGroup(
    MergeReasonFix Fix,
    string Title,
    string Intro,
    IReadOnlyList<string> Details)
{
    /// <summary>true 表示这一组会让视频重新编码（会影响画质，界面应使用警示色）。</summary>
    public bool IsLossy => Fix == MergeReasonFix.Reencode;
}

/// <summary>
/// 输入兼容性分析。判断依据不是「编码名相同」，而是影响解码与时间轴的完整参数组合：
/// 编码/档位/等级、分辨率、像素格式、帧率、时间基准、SAR/DAR、色彩参数、
/// 视频流数量与顺序、音频编码/采样率/声道布局、容器族。
/// </summary>
public static class CompatibilityAnalyzer
{
    private const double FrameRateTolerance = 0.01;

    /// <summary>
    /// 把帧率 / 场率写在码流头里的编码器。
    /// 这类码流的拼接不能只对齐容器时间戳：解码器按段内头部解释每一帧，
    /// 帧率不同却直接 concat 时后段会按前段的帧间隔被误读，产生大面积
    /// "mcbpc damaged" / "marker does not match f_code" 并导致该段无法播放。
    /// 实测：mpeg4 12.5fps + mpeg4 15fps 直接 copy 拼接 = 116 处解码错误、总时长 12.0s 掉到 12.09s 且末段不可播；
    /// 只重编码帧率不同的那一段后 = 0 错误。
    /// H.264 / HEVC 的帧率由 SPS/VPS 承载且段间可共存，不在此列（29.97 与 30 混拼实测 0 错误）。
    /// </summary>
    private static readonly HashSet<string> BitstreamFrameRateCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "mpeg4", "mpeg2video", "mpeg1video", "msmpeg4v2", "msmpeg4v3",
        "vc1", "wmv3", "flv1", "h263", "rv30", "rv40", "theora",
    };

    /// <summary>该编码的帧率是否固化在码流头里（因此帧率差异不能用重封装消除）。</summary>
    public static bool FrameRateStoredInBitstream(string codec)
        => BitstreamFrameRateCodecs.Contains((codec ?? "").Trim());

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
                // 帧率差异的处理取决于编码器是否把帧率写进码流头：
                // - H.264/HEVC：时间戳由容器承载，重封装即可对齐（29.97 与 30 混拼实测 0 错误）；
                // - MPEG-4 Part 2 / MPEG-1/2 / VC-1 等：帧率在码流头里，只能真正重编码才能改写，
                //   否则后段会按前段帧间隔被误读，出现解码错误且该段无法播放（实测 116 处错误）。
                if (Math.Abs(v.EffectiveFrameRate - v0.EffectiveFrameRate) > FrameRateTolerance)
                {
                    bool legacy = FrameRateStoredInBitstream(v.Codec) || FrameRateStoredInBitstream(v0.Codec);
                    string detail = string.Format(CultureInfo.InvariantCulture,
                        "帧率不一致（{0} 为 {1:0.###}，第一个为 {2:0.###}）",
                        name, v.EffectiveFrameRate, v0.EffectiveFrameRate);
                    if (legacy)
                        detail += "；该编码把帧率写在码流头里，必须重编码才能统一";
                    reasons.Add(new MergeReason("framerate", detail,
                        legacy ? MergeReasonFix.Reencode : MergeReasonFix.Remux));
                }
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

    // MediaModels.HasSquarePixels 把 0:1 与 0:0 都视为方形；这里保持一致，
    // 否则语义相同的两个输入会被误判 SAR 不兼容而触发无谓重编码。
    private static string NormalizeSar(string sar)
        => sar.Length == 0 || sar is "0:1" or "0:0" ? "1:1" : sar;

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
