using System.Globalization;

namespace SubFusion.Core.Services;

/// <summary>
/// Python difflib.SequenceMatcher(None, a, b).ratio() 的逐行等价移植。
/// 原 core.similarity 在「非相等、非包含」分支直接调用它，因此必须保持算法一致
/// （Ratcliff/Obershelp 最长匹配块递归 + autojunk），否则匹配阈值 0.72 的行为会漂移。
/// </summary>
internal static class SequenceMatcher
{
    private readonly record struct Block(int I, int J, int K);

    public static double Ratio(string a, string b)
    {
        int la = a.Length, lb = b.Length;
        if (la + lb == 0) return 1.0;     // difflib._calculate_ratio 的边界行为

        // __chain_b：b2j、autojunk（isjunk=None，故 bjunk 为空）
        var b2j = new Dictionary<char, List<int>>();
        for (int i = 0; i < lb; i++)
        {
            char c = b[i];
            if (!b2j.TryGetValue(c, out var list))
            {
                list = new List<int>();
                b2j[c] = list;
            }
            list.Add(i);
        }
        if (lb >= 200)
        {
            int ntest = lb / 100 + 1;
            var popular = new List<char>();
            foreach (var kv in b2j)
                if (kv.Value.Count > ntest) popular.Add(kv.Key);
            foreach (char c in popular) b2j.Remove(c);
        }

        // get_matching_blocks：用显式栈代替递归
        var queue = new Stack<(int Alo, int Ahi, int Blo, int Bhi)>();
        queue.Push((0, la, 0, lb));
        var blocks = new List<Block>();
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var (i, j, k) = FindLongestMatch(a, b, b2j, alo, ahi, blo, bhi);
            if (k > 0)
            {
                blocks.Add(new Block(i, j, k));
                if (alo < i && blo < j) queue.Push((alo, i, blo, j));
                if (i + k < ahi && j + k < bhi) queue.Push((i + k, ahi, j + k, bhi));
            }
        }

        blocks.Sort(static (x, y) =>
        {
            int c = x.I.CompareTo(y.I);
            if (c != 0) return c;
            c = x.J.CompareTo(y.J);
            return c != 0 ? c : x.K.CompareTo(y.K);
        });

        // 合并相邻块（Python 2.5+ 行为）
        int i1 = 0, j1 = 0, k1 = 0;
        long matches = 0;
        foreach (var b2 in blocks)
        {
            if (i1 + k1 == b2.I && j1 + k1 == b2.J)
            {
                k1 += b2.K;
            }
            else
            {
                if (k1 != 0) matches += k1;
                i1 = b2.I; j1 = b2.J; k1 = b2.K;
            }
        }
        if (k1 != 0) matches += k1;

        return 2.0 * matches / (la + lb);
    }

    /// <summary>find_longest_match；isjunk=None 时 bjunk 为空，两端只做普通扩展。</summary>
    private static (int I, int J, int K) FindLongestMatch(
        string a, string b, Dictionary<char, List<int>> b2j, int alo, int ahi, int blo, int bhi)
    {
        int bestI = alo, bestJ = blo, bestSize = 0;
        var j2len = new Dictionary<int, int>();
        for (int i = alo; i < ahi; i++)
        {
            var newj2len = new Dictionary<int, int>();
            if (b2j.TryGetValue(a[i], out var indices))
            {
                foreach (int j in indices)
                {
                    if (j < blo) continue;
                    if (j >= bhi) break;              // indices 升序，可提前结束
                    int prev = j2len.TryGetValue(j - 1, out int p) ? p : 0;
                    int k = prev + 1;
                    newj2len[j] = k;
                    if (k > bestSize)
                    {
                        bestI = i - k + 1;
                        bestJ = j - k + 1;
                        bestSize = k;
                    }
                }
            }
            j2len = newj2len;
        }

        while (bestI > alo && bestJ > blo && a[bestI - 1] == b[bestJ - 1])
        {
            bestI--; bestJ--; bestSize++;
        }
        while (bestI + bestSize < ahi && bestJ + bestSize < bhi &&
               a[bestI + bestSize] == b[bestJ + bestSize])
        {
            bestSize++;
        }
        return (bestI, bestJ, bestSize);
    }
}
