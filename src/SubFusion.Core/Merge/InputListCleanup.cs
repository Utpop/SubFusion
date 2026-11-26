namespace SubFusion.Core.Merge;

/// <summary>
/// 合并成功后，哪些输入还应该留在界面的视频列表里。
///
/// 只有<b>确实被删掉</b>的源文件才该从列表里消失。删源是有条件的行为：
/// 校验没过、文件被占用、用户干脆没开「删掉原视频」，都会让源文件留在磁盘上。
/// 那时把列表清空就是错的——用户看着空列表，文件却还在，
/// 想再试一次还得重新把同一批文件拖回来。
///
/// 因此判定完全由 Core 给出的 <see cref="MergeResult.DeletedSources"/> 驱动，
/// 界面不自己推测「删源开着就等于都删了」。
/// </summary>
public static class InputListCleanup
{
    /// <summary>
    /// 返回仍然保留的输入（顺序不变，元素是调用方传进来的原始字符串）。
    /// <paramref name="deleted"/> 为空时原样返回：没删任何东西就没有任何理由动列表。
    /// </summary>
    public static List<string> Remaining(IReadOnlyList<string> inputs, IReadOnlyCollection<string> deleted)
    {
        var kept = new List<string>(inputs.Count);
        if (deleted.Count == 0)
        {
            foreach (string input in inputs) kept.Add(input);
            return kept;
        }

        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in deleted)
        {
            string normalized = Normalize(path);
            if (normalized.Length > 0) gone.Add(normalized);
        }

        foreach (string input in inputs)
        {
            string normalized = Normalize(input);
            if (normalized.Length > 0 && gone.Contains(normalized)) continue;
            kept.Add(input);
        }
        return kept;
    }

    /// <summary>
    /// 把路径拉平成可直接比较的形式。
    /// 删源用的路径来自 Core 的 <c>Path.GetFullPath</c>，列表里的则是用户拖进来的原始写法，
    /// 两者可能只差相对/绝对或大小写；不先归一化就会出现「删了却没从列表里移除」。
    /// 归一化失败（非法字符、超长路径）时退回原字符串，至少还能按字面比一次。
    /// </summary>
    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string trimmed = path.Trim();
        try { return Path.GetFullPath(trimmed); }
        catch { return trimmed; }
    }
}
