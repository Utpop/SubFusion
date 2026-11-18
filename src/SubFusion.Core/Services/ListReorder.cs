namespace SubFusion.Core.Services;

/// <summary>一次集合搬移：把 <see cref="From"/> 位置的元素移到 <see cref="To"/>。</summary>
public readonly record struct MoveStep(int From, int To);

/// <summary>
/// 把「当前顺序」变成「目标顺序」所需的搬移序列。
///
/// 存在的理由：界面上的拖拽排序只有视觉结果，真正决定合并顺序的是 ViewModel 里的集合。
/// 两者一旦不同步，行会在下一次布局时弹回原位——用户看到的就是「拖了没反应」。
/// 这段逻辑放在 Core 而不是 ViewModel 里，是为了能脱离 WinUI 直接被自检覆盖；
/// 排序是合并顺序的唯一来源，靠肉眼核对不可靠。
/// </summary>
public static class ListReorder
{
    /// <summary>
    /// 计算搬移序列。目标顺序必须是当前顺序的一个排列（元素集合完全相同）；
    /// 长度或成员不一致时返回空序列——来源不可信时宁可不动，也不要打乱用户列表。
    /// </summary>
    public static List<MoveStep> ComputeMoves<T>(IReadOnlyList<T> current, IReadOnlyList<T> target,
                                                 IEqualityComparer<T>? comparer = null)
    {
        var steps = new List<MoveStep>();
        if (current.Count != target.Count) return steps;
        comparer ??= EqualityComparer<T>.Default;

        // 成员必须一致（多重集相等）。少了这个检查，调用方传进来一个「部分项」的列表
        // 会把剩下的项挤到末尾，用户数据看着像丢了一样。
        var remaining = new List<T>(current);
        foreach (T item in target)
        {
            int idx = remaining.FindIndex(x => comparer.Equals(x, item));
            if (idx < 0) return steps;
            remaining.RemoveAt(idx);
        }
        if (remaining.Count > 0) return steps;

        // 模拟搬运：每一步都作用在「已部分调整」的工作副本上，
        // 这样产出的下标记号可以直接按顺序喂给 ObservableCollection.Move。
        var working = new List<T>(current);
        for (int target_index = 0; target_index < target.Count; target_index++)
        {
            int from = working.FindIndex(x => comparer.Equals(x, target[target_index]));
            if (from < 0) return new List<MoveStep>();
            if (from == target_index) continue;          // 已在正确位置，不必搬
            working.RemoveAt(from);
            working.Insert(target_index, target[target_index]);
            steps.Add(new MoveStep(from, target_index));
        }
        return steps;
    }

    /// <summary>目标顺序是否与当前顺序一致（用于跳过无意义的重分析）。</summary>
    public static bool IsSameOrder<T>(IReadOnlyList<T> current, IReadOnlyList<T> target,
                                      IEqualityComparer<T>? comparer = null)
    {
        if (current.Count != target.Count) return false;
        comparer ??= EqualityComparer<T>.Default;
        for (int i = 0; i < current.Count; i++)
            if (!comparer.Equals(current[i], target[i])) return false;
        return true;
    }
}
