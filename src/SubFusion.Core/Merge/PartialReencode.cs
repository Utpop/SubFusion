using SubFusion.Core.Media;

namespace SubFusion.Core.Merge;

/// <summary>
/// 「只重编码少数派」的结论：哪些输入可以原样保留，哪些必须重新编码。
/// </summary>
public sealed class PartialReencodePlan
{
    /// <summary>可以原样保留的输入下标（它们彼此之间已能无损拼接）。</summary>
    public IReadOnlyList<int> ReferenceIndices { get; init; } = Array.Empty<int>();

    /// <summary>需要重新编码到参考组参数的输入下标。</summary>
    public IReadOnlyList<int> ReencodeIndices { get; init; } = Array.Empty<int>();

    /// <summary>true 表示确实存在「保留一批、只编码少数」的可行方案。</summary>
    public bool IsPartial => ReferenceIndices.Count > 0 && ReencodeIndices.Count > 0;

    public string Describe() => IsPartial
        ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "保留 {0} 段原样，仅重编码 {1} 段（下标 {2}）",
            ReferenceIndices.Count, ReencodeIndices.Count,
            string.Join(",", ReencodeIndices))
        : "无部分重编码方案";
}

/// <summary>
/// 找出一批输入里「最大的、彼此能无损拼接的子集」，其余视为需要重编码的少数派。
///
/// 存在的理由：原先只要有一个输入不兼容，就要把**全部**输入重新编码。
/// 但真实素材里更常见的是「一批同源文件 + 一个参数不同的文件」，
/// 为那一个文件把其余几十 GB 全部重压一遍，既费时又有画质损失。
/// 实测场景：三段 mpeg4（12.5 / 12.5 / 15 fps），原先三段全重编码；
/// 现在只重编码第三段，前两段保持逐比特不变。
///
/// 判定一律复用 <see cref="CompatibilityAnalyzer"/>，不在此处另立标准；
/// 调用方在真正重编码**之后**还会再核实一次，核实不过就退回全量重编码。
/// </summary>
public static class PartialReencodeAnalyzer
{
    /// <summary>少于这个段数时，「部分」没有意义（只剩 1 段可保留）。</summary>
    private const int MinimumReferenceSegments = 2;

    public static PartialReencodePlan Analyze(IReadOnlyList<MediaBundle> bundles)
    {
        if (bundles.Count < 3) return new PartialReencodePlan();   // 2 段里留 1 段 = 没有收益

        // 以每个输入为种子贪心扩张，取最大的兼容子集。
        // 输入数量通常是个位数到几十，O(n^3) 完全可接受，换来的是「不遗漏更大的子集」。
        List<int>? best = null;
        for (int seed = 0; seed < bundles.Count; seed++)
        {
            var group = new List<int> { seed };
            for (int j = 0; j < bundles.Count; j++)
            {
                if (j == seed) continue;
                var trial = new List<MediaBundle>(group.Count + 1);
                foreach (int idx in group) trial.Add(bundles[idx]);
                trial.Add(bundles[j]);
                if (CanStillMergeWithoutReencode(trial)) group.Add(j);
            }
            if (best is null || group.Count > best.Count) best = group;
        }

        if (best is null || best.Count < MinimumReferenceSegments || best.Count >= bundles.Count)
            return new PartialReencodePlan();

        var reencode = Enumerable.Range(0, bundles.Count).Where(i => !best.Contains(i)).ToList();
        // 少数派占了一半以上时，保留的那几段省不下多少，还不如全量重编码来得一致。
        if (reencode.Count >= best.Count) return new PartialReencodePlan();

        // 保持原始顺序，拼接顺序才与用户拖拽的顺序一致。
        best.Sort();
        return new PartialReencodePlan { ReferenceIndices = best, ReencodeIndices = reencode };
    }

    /// <summary>这一组输入是否仍然不需要重编码（直接拼接，或重封装对齐后拼接）。</summary>
    private static bool CanStillMergeWithoutReencode(IReadOnlyList<MediaBundle> bundles)
    {
        MergeCompatibility compatibility = CompatibilityAnalyzer.Analyze(bundles);
        return compatibility.CanStreamCopy || compatibility.CanRemuxOnly;
    }
}
