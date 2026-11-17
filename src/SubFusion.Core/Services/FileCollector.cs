namespace SubFusion.Core.Services;

public sealed record CollectResult(List<string> Videos, List<string> Subtitles, List<string> Skipped);

/// <summary>原 core.collect_files：文件/文件夹收集、去重、过滤隐藏子目录、排序。</summary>
public static class FileCollector
{
    // Windows 文件系统不区分大小写：同一路径的不同大小写写法必须视为同一个文件，
    // 否则会出现重复条目。Linux/macOS 保持大小写敏感。
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static CollectResult Collect(IEnumerable<string> paths, bool recursive = true)
    {
        var videos = new List<string>();
        var subs = new List<string>();
        var skipped = new List<string>();
        var seenV = new HashSet<string>(PathComparer);
        var seenS = new HashSet<string>(PathComparer);
        // 递归遍历时记录已访问过的真实路径，防止 junction/符号链接成环导致死循环或重复。
        var visited = new HashSet<string>(PathComparer);

        void Add(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string real = Path.GetFullPath(path);
            if (Constants.VideoExts.Contains(ext))
            {
                if (seenV.Add(real)) videos.Add(real);
            }
            else if (Constants.SubExts.Contains(ext))
            {
                if (seenS.Add(real)) subs.Add(real);
            }
            else
            {
                skipped.Add(real);
            }
        }

        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                Add(path);
            }
            else if (Directory.Exists(path))
            {
                // 根目录本身若是 junction/重解析点，直接跳过，避免从它开始绕环。
                if (IsReparsePoint(path)) continue;
                if (recursive)
                {
                    Walk(path, Add, visited);
                }
                else
                {
                    foreach (string name in SortedNames(Directory.GetFiles(path).Select(Path.GetFileName)!))
                    {
                        string full = Path.Combine(path, name);
                        if (File.Exists(full)) Add(full);
                    }
                }
            }
        }

        var lowerComparer = new LowerOrdinalComparer();
        List<string> sortedVideos = videos.OrderBy(p => p, lowerComparer).ToList();
        List<string> sortedSubs = subs.OrderBy(p => p, lowerComparer).ToList();
        return new CollectResult(sortedVideos, sortedSubs, skipped);
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    /// <summary>等价于 os.walk：跳过以 "." 开头的子目录；文件名按 sorted() 顺序处理。
    /// 额外跳过 junction/符号链接等重解析点，并用 visited 去重，防止成环死循环。</summary>
    private static void Walk(string root, Action<string> add, HashSet<string> visited)
    {
        try
        {
            string realRoot;
            try { realRoot = Path.GetFullPath(root); }
            catch { realRoot = root; }
            if (!visited.Add(realRoot)) return;   // 已经走过这个真实路径，避免重复/成环

            foreach (string name in SortedNames(Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName)!))
            {
                string full = Path.Combine(root, name);
                if (IsReparsePoint(full)) continue;   // 跳过符号链接文件 / junction
                if (File.Exists(full)) add(full);
            }
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                string leaf = Path.GetFileName(dir);
                if (leaf.StartsWith('.')) continue;   // dirs[:] = [d for d in dirs if not d.startswith(".")]
                if (IsReparsePoint(dir)) continue;    // 跳过 junction / 目录符号链接
                Walk(dir, add, visited);
            }
        }
        catch
        {
            // os.walk 默认 onerror=None，遇到无权限目录静默跳过
        }
    }

    private static IEnumerable<string> SortedNames(IEnumerable<string?> names)
        => names.Where(n => n is not null).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal);

    /// <summary>等价于 Python 的 sort(key=lambda p: p.lower())。</summary>
    private sealed class LowerOrdinalComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
            => StringComparer.Ordinal.Compare((x ?? "").ToLowerInvariant(), (y ?? "").ToLowerInvariant());
    }
}
