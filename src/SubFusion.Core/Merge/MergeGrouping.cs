using SubFusion.Core.Media;

namespace SubFusion.Core.Merge;

/// <summary>一个可无损拼接的视频组合。</summary>
public sealed class MergeGroup
{
    /// <summary>组内输入，顺序即建议的合并顺序（按名称自然序）。</summary>
    public List<MediaBundle> Items { get; init; } = new();

    /// <summary>组内共同的「兼容性指纹」，用于展示为什么它们能拼在一起。</summary>
    public string Signature { get; init; } = "";

    /// <summary>
    /// true = 全部参数一致，concat + stream copy 即可；
    /// false = 只有时间轴类差异，重封装对齐后仍是 stream copy，画质同样无损。
    /// </summary>
    public bool DirectCopy { get; init; }

    public string KindText => DirectCopy ? "直接拼接" : "重封装后拼接";

    /// <summary>
    /// 为什么这一组不能直接拼（只有 <see cref="DirectCopy"/> 为 false 时才有内容）。
    /// 直接复用 <see cref="CompatibilityAnalyzer"/> 的结论，不在这里另写一套解释，
    /// 否则界面上的说法会和引擎的实际行为对不上。
    /// </summary>
    public string ReasonText { get; init; } = "";

    /// <summary>组内合计时长（秒）。</summary>
    public double TotalDuration => Items.Sum(i => Math.Max(0, i.Duration));

    public string TotalDurationText => TimeSpan.FromSeconds(TotalDuration).ToString(@"hh\:mm\:ss");
}

/// <summary>分组结果：每组都是无需重编码的组合，组与组之间才需要重编码。</summary>
public sealed class MergeGroupingResult
{
    public List<MergeGroup> Groups { get; init; } = new();

    /// <summary>无法参与任何组合的输入，附带原因。</summary>
    public List<(MediaBundle Bundle, string Reason)> Rejected { get; init; } = new();

    public int DirectGroups => Groups.Count(g => g.DirectCopy);
    public int RemuxGroups => Groups.Count(g => !g.DirectCopy);

    /// <summary>已进入某个组合的文件数。</summary>
    public int GroupedCount => Groups.Sum(g => g.Items.Count);

    public bool HasAnyGroup => Groups.Any(g => g.Items.Count > 1);

    /// <summary>
    /// 这份分组结果是否仍然对应当前输入列表（决定「按组合分配文件夹」能不能执行）。
    ///
    /// 只检查一个方向：<b>分组里出现的每个文件必须仍在输入列表中</b>。
    /// 不能要求「分组覆盖全部输入」——分组结果天然只包含能拼在一起的文件，
    /// 拼不上的落单文件在 <see cref="Rejected"/> 里。用双向相等去判，
    /// 只要有一个文件拼不上，结论就永远是「输入列表已经变化」，
    /// 用户每次点导出都被劝退，等于这个功能整体不可用。
    ///
    /// 反过来，输入列表里多出文件不需要失效：导出只搬分组内的文件，
    /// 多出来的那几个本来就不参与，搬走它们反而是错的。
    /// 而分组内的文件被移除则必须失效——那说明界面上的列表已经不能代表
    /// 这份结论了，继续导出会把一批用户以为已经排除的文件搬走。
    /// </summary>
    public bool MatchesInputs(IReadOnlyList<string> paths)
    {
        if (Groups.Count == 0) return false;
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
            if (!string.IsNullOrEmpty(path)) current.Add(path);

        foreach (MergeGroup group in Groups)
            foreach (MediaBundle bundle in group.Items)
                if (!current.Contains(bundle.Path)) return false;
        return true;
    }
}

/// <summary>
/// 把一批视频按「能否无损拼接」自动分成多个组合。
///
/// 判定不重新发明规则：每一次成组都用 <see cref="CompatibilityAnalyzer.Analyze"/> 复核，
/// 保证「这里说能无损拼」与「引擎实际走 stream copy」是同一个判断，不会出现两套标准。
///
/// 采用贪心聚类而非按参数指纹分桶：指纹只要严格相等，就会把
/// "只差一个可重封装修正的参数" 的文件拆散，导致明明能拼却识别不出组合。
/// 逐个尝试「加进来还兼容吗」可以得到多个互不重叠的组合，且不会漏掉上述情况。
/// </summary>
public static class MergeGrouping
{
    public static MergeGroupingResult Analyze(IReadOnlyList<MediaBundle> bundles)
    {
        var rejected = new List<(MediaBundle, string)>();
        var usable = new List<MediaBundle>();
        foreach (MediaBundle b in bundles)
        {
            if (!b.Success) { rejected.Add((b, string.IsNullOrEmpty(b.Error) ? "无法读取媒体信息" : b.Error)); continue; }
            if (b.PrimaryVideo is null) { rejected.Add((b, "没有视频流")); continue; }
            usable.Add(b);
        }

        // 先按名称自然序排列，使结果稳定可复现，也让组内顺序就是建议的观看/合并顺序。
        usable.Sort((x, y) => NaturalComparer.Instance.Compare(
            Path.GetFileName(x.Path), Path.GetFileName(y.Path)));

        var groups = new List<MergeGroup>();
        List<MediaBundle> remaining = usable.ToList();

        // 第一轮：只要全部参数一致就直接成组（最理想，零工作量）。
        remaining = ExtractGroups(remaining, requireDirectCopy: true, groups, rejected);

        // 第二轮：剩下的只存在时间轴类差异时，重封装对齐即可，仍然无损。
        remaining = ExtractGroups(remaining, requireDirectCopy: false, groups, rejected);

        // 落单的文件给出明确原因，而不是静默消失。
        foreach (MediaBundle lone in remaining)
            rejected.Add((lone, "与其它输入均不兼容，单独合并无可避免会重编码"));

        groups = groups
            .OrderByDescending(g => g.DirectCopy)
            .ThenByDescending(g => g.Items.Count)
            .ThenByDescending(g => g.TotalDuration)
            .ToList();

        return new MergeGroupingResult { Groups = groups, Rejected = rejected };
    }

    /// <summary>
    /// 反复取剩余列表首个文件作为种子，把能兼容的都并进来，形成一个组合后移除。
    /// 一轮只接受一种修复级别（先严格 copy，再放宽到允许重封装）。
    /// </summary>
    private static List<MediaBundle> ExtractGroups(List<MediaBundle> source, bool requireDirectCopy,
                                                   List<MergeGroup> groups,
                                                   List<(MediaBundle, string)> rejected)
    {
        var pending = source.ToList();
        while (pending.Count > 1)
        {
            MediaBundle seed = pending[0];
            var group = new List<MediaBundle> { seed };
            for (int i = 1; i < pending.Count; i++)
            {
                var trial = new List<MediaBundle>(group) { pending[i] };
                MergeCompatibility check = CompatibilityAnalyzer.Analyze(trial);
                bool ok = requireDirectCopy ? check.CanStreamCopy : check.CanRemuxOnly;
                if (ok) group.Add(pending[i]);
            }

            if (group.Count > 1)
            {
                groups.Add(new MergeGroup
                {
                    Items = group,
                    Signature = Describe(group[0]),
                    DirectCopy = requireDirectCopy,
                    // 非直接拼接时把"为什么还要多一步重封装"写清楚。
                    // 这里复用刚做过的兼容性判定结果，不额外再算一遍、也不另写一套说辞。
                    ReasonText = requireDirectCopy ? "" : FirstReason(group),
                });
                pending = pending.Where(b => !group.Contains(b)).ToList();
            }
            else
            {
                // 当前种子在这一轮凑不成组；若这一轮允许重封装也失败，
                // 说明它无论如何都要重编码，交给调用方归入 rejected。
                if (!requireDirectCopy) rejected.Add((seed, "与其它输入均无法无损拼接"));
                pending.RemoveAt(0);
            }
        }

        // 只剩一个文件时，若这一轮不要求严格 copy 也要交代清楚。
        if (pending.Count == 1 && !requireDirectCopy) rejected.Add((pending[0], "与其它输入均无法无损拼接"));
        return requireDirectCopy ? pending : new List<MediaBundle>();
    }

    /// <summary>取该组第一条差异说明，用于告诉用户为什么需要重封装。</summary>
    private static string FirstReason(List<MediaBundle> group)
    {
        MergeCompatibility compatibility = CompatibilityAnalyzer.Analyze(group);
        return compatibility.PrimaryReason;
    }

    /// <summary>给用户看的简短说明，如 "h264 MAIN 1920×1080 30fps yuv420p"。</summary>
    private static string Describe(MediaBundle b)
    {
        VideoStreamInfo v = b.PrimaryVideo!;
        string profile = v.Profile.Length > 0 ? " " + v.Profile.ToUpperInvariant() : "";
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}{1} {2} {3:0.###}fps {4}",
            v.Codec.ToLowerInvariant(), profile, v.SizeText, v.EffectiveFrameRate, v.PixFmt);
    }
}
