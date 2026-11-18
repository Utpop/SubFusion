using System.Globalization;

namespace SubFusion.Core.Media;

/// <summary>
/// 按「人类习惯」比较文件名：连续数字按数值大小比较，而不是按字符逐位比较。
/// 例如 "第2集" 排在 "第10集" 之前；字符串序会把 10 排在 2 前面。
/// 用例：一批剧集文件名是 1、2、...、10，用户期望列表顺序与观看顺序一致。
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    /// <summary>文件名字面相同但大小写不同时的兜底比较，保证排序稳定可复现。</summary>
    private static readonly StringComparer TieBreaker =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            char cx = x[i];
            char cy = y[j];

            bool dx = char.IsDigit(cx);
            bool dy = char.IsDigit(cy);
            if (dx && dy)
            {
                // 取两侧完整的数字段，去掉前导零后按数值长度 + 字典序比较，
                // 这样超长数字（超过 long 范围）也不会溢出。
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                string nx = x[si..i].TrimStart('0');
                string ny = y[sj..j].TrimStart('0');
                if (nx.Length != ny.Length) return nx.Length < ny.Length ? -1 : 1;
                int cmp = string.CompareOrdinal(nx, ny);
                if (cmp != 0) return cmp < 0 ? -1 : 1;
                continue;
            }

            if (cx != cy)
            {
                // 非数字位置按不区分大小写的序数比较，避免大小写影响主机间的排序一致性。
                int cmp = char.ToUpperInvariant(cx).CompareTo(char.ToUpperInvariant(cy));
                if (cmp != 0) return cmp < 0 ? -1 : 1;
            }
            i++;
            j++;
        }

        if (i < x.Length) return 1;
        if (j < y.Length) return -1;
        return TieBreaker.Compare(x, y);
    }
}
